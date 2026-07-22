using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

/// <summary>
/// Receives runtime events from a backend so the facade can fan them out to UI
/// subscribers. The facade implements this and hands it to the backend factory; the
/// backend (translation pipeline, event dispatcher) calls into it as events arrive.
/// </summary>
public interface IEngineRuntimeEventPublisher
{
    void PublishOcrResult(OcrResultSnapshot result);
    void PublishTranslationOutput(Guid profileId, TranslationOutput output);
    void PublishTargetLifecycle(TargetLifecycleEvent lifecycle);
    void PublishBudget(RuntimeBudgetSnapshot snapshot);
    void PublishDiagnostic(EngineDiagnostic diagnostic);
}

/// <summary>
/// Coarse control surface a backend exposes to the facade. Each operation maps to the
/// closest real mechanism the EngineHost protocol supports (see the concrete backend
/// wiring); unsupported operations fail loudly rather than degrade silently.
/// </summary>
public interface IEngineRuntimeControl
{
    ValueTask PauseAllAsync(CancellationToken cancellationToken);
    ValueTask ResumeAllAsync(CancellationToken cancellationToken);
    ValueTask SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken);
    ValueTask RequestManualOcrAsync(CancellationToken cancellationToken);
}

/// <summary>A live backend paired with its control surface.</summary>
public sealed record EngineRuntimeBackendSession(
    IRecoverableRuntimeBackend Backend,
    IEngineRuntimeControl Control);

/// <summary>Acquires an authenticated EngineHost session (initial launch or restart).</summary>
public delegate ValueTask<IRuntimeEngineHostSession> EngineRuntimeSessionFactory(
    CancellationToken cancellationToken);

/// <summary>
/// Builds the backend (translation pipeline + event dispatcher wiring) over a freshly
/// launched session, wiring its events into <paramref name="publisher"/>.
/// </summary>
public delegate ValueTask<EngineRuntimeBackendSession> EngineRuntimeBackendFactory(
    IRuntimeEngineHostSession session,
    IEngineRuntimeEventPublisher publisher,
    CancellationToken cancellationToken);

/// <summary>Raised when a facade control operation is not supported by the active backend.</summary>
public sealed class EngineRuntimeUnsupportedOperationException : Exception
{
    public EngineRuntimeUnsupportedOperationException(string operationKey)
        : base($"The EngineHost runtime does not support the '{operationKey}' operation.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        OperationKey = operationKey;
    }

    public string OperationKey { get; }

    public string LocalizationKey => $"engine.runtime.unsupported.{OperationKey}";
}

/// <summary>
/// Facade over the EngineHost launch/handshake/restart machinery. Owns the process
/// lifecycle, surfaces an explicit status state machine (including a distinct
/// executable-not-found state that carries the searched paths), fans backend events out
/// to UI subscribers, and guarantees the native process is torn down on disposal.
/// </summary>
public sealed class EngineRuntimeService : IEngineRuntime, IEngineRuntimeEventPublisher
{
    private readonly EngineHostLocateResult _locateResult;
    private readonly EngineRuntimeSessionFactory _sessionFactory;
    private readonly EngineRuntimeBackendFactory _backendFactory;
    private readonly RuntimeRestartBudget _restartBudget;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, TargetSnapshot> _targets = [];
    private readonly CancellationTokenSource _stop = new();

    private EngineRuntimeStatus _status = EngineRuntimeStatus.Stopped;
    private EngineRuntimeBackendSession? _current;
    private Task? _superviseLoop;
    private int _started;
    private int _disposed;

    public EngineRuntimeService(
        EngineHostLocateResult locateResult,
        EngineRuntimeSessionFactory sessionFactory,
        EngineRuntimeBackendFactory backendFactory,
        RuntimeEngineHostRestartPolicy restartPolicy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(locateResult);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(restartPolicy);
        _locateResult = locateResult;
        _sessionFactory = sessionFactory;
        _backendFactory = backendFactory;
        _restartBudget = new RuntimeRestartBudget(restartPolicy);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds a service that launches the real EngineHost located at
    /// <paramref name="locateResult"/>. When the executable was not found the returned
    /// service reports <see cref="EngineRuntimeStatus.ExecutableNotFound"/> on start.
    /// </summary>
    public static EngineRuntimeService CreateForLaunch(
        EngineHostLocateResult locateResult,
        TimeSpan handshakeTimeout,
        EngineRuntimeBackendFactory backendFactory,
        RuntimeEngineHostRestartPolicy restartPolicy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(locateResult);
        if (handshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }
        return new EngineRuntimeService(
            locateResult,
            async cancellationToken => await RuntimeEngineHostLauncher.LaunchAsync(
                locateResult.ExecutablePath
                    ?? throw new EngineHostNotFoundException(locateResult.SearchedPaths),
                handshakeTimeout,
                cancellationToken).ConfigureAwait(false),
            backendFactory,
            restartPolicy,
            timeProvider);
    }

    public event EventHandler<EngineRuntimeStatusChange>? StatusChanged;
    public event EventHandler<EngineTargetSnapshotEvent>? TargetsChanged;
    public event EventHandler<EngineOcrResultEvent>? OcrResultReceived;
    public event EventHandler<EngineTranslationOutputEvent>? TranslationOutputReceived;
    public event EventHandler<EngineBudgetEvent>? BudgetUpdated;
    public event EventHandler<EngineDiagnostic>? DiagnosticRaised;

    public EngineRuntimeStatus Status
    {
        get { lock (_stateLock) return _status; }
    }

    public IReadOnlyList<TargetSnapshot> TargetSnapshots
    {
        get { lock (_stateLock) return _targets.Values.ToArray(); }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                throw new InvalidOperationException("engine.runtime.alreadyStarted");
            }
            if (!_locateResult.Found)
            {
                SetStatus(
                    EngineRuntimeStatus.ExecutableNotFound,
                    "engine.runtime.executableNotFound",
                    _locateResult.SearchedPaths);
                throw new EngineHostNotFoundException(_locateResult.SearchedPaths);
            }

            SetStatus(EngineRuntimeStatus.Locating);
            SetStatus(EngineRuntimeStatus.Starting);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _stop.Token);
            EngineRuntimeBackendSession session = await LaunchAndStartAsync(linked.Token)
                .ConfigureAwait(false);
            SetCurrent(session);
            SetStatus(EngineRuntimeStatus.Running);
            _restartBudget.Reset();
            _superviseLoop = SuperviseAsync(session);
        }
        catch
        {
            if (Status != EngineRuntimeStatus.ExecutableNotFound)
            {
                SetStatusIfNot(EngineRuntimeStatus.Faulted, "engine.runtime.startFailed");
            }
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Task? loop;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _started) == 0)
            {
                SetStatus(EngineRuntimeStatus.Stopped);
                return;
            }
            SetStatus(EngineRuntimeStatus.Stopping);
            _stop.Cancel();
            loop = _superviseLoop;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await DisposeCurrentAsync().ConfigureAwait(false);
        SetStatus(EngineRuntimeStatus.Stopped);
    }

    public ValueTask PauseAllAsync(CancellationToken cancellationToken) =>
        WithControlAsync(control => control.PauseAllAsync(cancellationToken));

    public ValueTask ResumeAllAsync(CancellationToken cancellationToken) =>
        WithControlAsync(control => control.ResumeAllAsync(cancellationToken));

    public ValueTask SetOverlayVisibleAsync(bool visible, CancellationToken cancellationToken) =>
        WithControlAsync(control => control.SetOverlayVisibleAsync(visible, cancellationToken));

    public ValueTask RequestManualOcrAsync(CancellationToken cancellationToken) =>
        WithControlAsync(control => control.RequestManualOcrAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stop.Cancel();
        Task? loop = _superviseLoop;
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* teardown must not throw from the supervise loop */ }
        }
        await DisposeCurrentAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
        _stop.Dispose();
    }

    void IEngineRuntimeEventPublisher.PublishOcrResult(OcrResultSnapshot result)
    {
        ArgumentNullException.ThrowIfNull(result);
        OcrResultReceived?.Invoke(
            this, new EngineOcrResultEvent(result, _timeProvider.GetUtcNow()));
    }

    void IEngineRuntimeEventPublisher.PublishTranslationOutput(
        Guid profileId, TranslationOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        TranslationOutputReceived?.Invoke(
            this, new EngineTranslationOutputEvent(profileId, output, _timeProvider.GetUtcNow()));
    }

    void IEngineRuntimeEventPublisher.PublishTargetLifecycle(TargetLifecycleEvent lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lock (_stateLock)
        {
            if (lifecycle.Target.State == TargetLifecycleState.Closed)
            {
                _targets.Remove(lifecycle.Target.TargetInstanceId.Value);
            }
            else
            {
                _targets[lifecycle.Target.TargetInstanceId.Value] = lifecycle.Target;
            }
        }
        TargetsChanged?.Invoke(this, new EngineTargetSnapshotEvent(lifecycle));
    }

    void IEngineRuntimeEventPublisher.PublishBudget(RuntimeBudgetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BudgetUpdated?.Invoke(this, new EngineBudgetEvent(snapshot));
    }

    void IEngineRuntimeEventPublisher.PublishDiagnostic(EngineDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        DiagnosticRaised?.Invoke(this, diagnostic);
    }

    private async ValueTask<EngineRuntimeBackendSession> LaunchAndStartAsync(
        CancellationToken cancellationToken)
    {
        IRuntimeEngineHostSession session = await _sessionFactory(cancellationToken)
            .ConfigureAwait(false);
        EngineRuntimeBackendSession backend;
        try
        {
            backend = await _backendFactory(session, this, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        try
        {
            await backend.Backend.StartAsync(cancellationToken).ConfigureAwait(false);
            return backend;
        }
        catch
        {
            await backend.Backend.DisposeAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task SuperviseAsync(EngineRuntimeBackendSession initial)
    {
        EngineRuntimeBackendSession backend = initial;
        while (true)
        {
            Exception? failure = null;
            try
            {
                await backend.Backend.Completion.WaitAsync(_stop.Token).ConfigureAwait(false);
                failure = new RuntimeBackendUnexpectedCompletionException();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            await DisposeCurrentAsync().ConfigureAwait(false);
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            PublishRuntimeDiagnostic(
                "engine.runtime.restarting",
                RuntimeDiagnosticSeverity.Warning,
                failure);
            SetStatus(EngineRuntimeStatus.Restarting, "engine.runtime.restarting");
            if (!_restartBudget.TryReserve(_timeProvider.GetUtcNow(), out TimeSpan delay))
            {
                SetStatus(
                    EngineRuntimeStatus.Faulted, "engine.runtime.restartBudgetExhausted");
                return;
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, _stop.Token).ConfigureAwait(false);
                }
                backend = await LaunchAndStartAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                PublishRuntimeDiagnostic(
                    "engine.runtime.restartFailed",
                    RuntimeDiagnosticSeverity.Error,
                    exception);
                SetStatus(EngineRuntimeStatus.Faulted, "engine.runtime.restartFailed");
                return;
            }

            SetCurrent(backend);
            SetStatus(EngineRuntimeStatus.Running);
        }
    }

    private ValueTask WithControlAsync(Func<IEngineRuntimeControl, ValueTask> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IEngineRuntimeControl? control;
        lock (_stateLock)
        {
            control = _current?.Control;
        }
        if (control is null)
        {
            throw new InvalidOperationException("engine.runtime.notRunning");
        }
        return operation(control);
    }

    private void SetCurrent(EngineRuntimeBackendSession session)
    {
        lock (_stateLock)
        {
            _current = session;
        }
    }

    private async ValueTask DisposeCurrentAsync()
    {
        EngineRuntimeBackendSession? current;
        lock (_stateLock)
        {
            current = _current;
            _current = null;
        }
        if (current is not null)
        {
            await current.Backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void PublishRuntimeDiagnostic(
        string errorCode, RuntimeDiagnosticSeverity severity, Exception? failure)
    {
        DiagnosticRaised?.Invoke(this, new EngineDiagnostic(
            errorCode,
            failure is null ? errorCode : $"{errorCode}: {failure.Message}",
            severity,
            _timeProvider.GetUtcNow()));
    }

    private void SetStatus(
        EngineRuntimeStatus status,
        string? errorCode = null,
        IReadOnlyList<string>? searchedPaths = null)
    {
        lock (_stateLock)
        {
            _status = status;
        }
        StatusChanged?.Invoke(this, new EngineRuntimeStatusChange(
            status, _timeProvider.GetUtcNow(), errorCode, searchedPaths));
    }

    private void SetStatusIfNot(EngineRuntimeStatus status, string? errorCode)
    {
        lock (_stateLock)
        {
            if (_status == status)
            {
                return;
            }
            _status = status;
        }
        StatusChanged?.Invoke(this, new EngineRuntimeStatusChange(
            status, _timeProvider.GetUtcNow(), errorCode));
    }
}
