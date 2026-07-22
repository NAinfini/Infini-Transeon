namespace InfiniTranseon.Core.Runtime;

public interface IRecoverableRuntimeBackend : IAsyncDisposable
{
    Task Completion { get; }
    ValueTask StartAsync(CancellationToken cancellationToken);
}

public sealed class RuntimeBackendUnexpectedCompletionException : Exception
{
    public RuntimeBackendUnexpectedCompletionException()
        : base("runtime.backend.unexpectedCompletion")
    {
    }
}

public sealed class RuntimeBackendRecoveryCoordinator : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<IRuntimeEngineHostSession>> _startSession;
    private readonly Func<CancellationToken, ValueTask<IRuntimeEngineHostSession>> _restartSession;
    private readonly Func<IRuntimeEngineHostSession, IRecoverableRuntimeBackend> _createBackend;
    private readonly Func<Exception, CancellationToken, ValueTask> _reportFailure;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _started;
    private int _disposed;

    public RuntimeBackendRecoveryCoordinator(
        Func<CancellationToken, ValueTask<IRuntimeEngineHostSession>> startSession,
        Func<CancellationToken, ValueTask<IRuntimeEngineHostSession>> restartSession,
        Func<IRuntimeEngineHostSession, IRecoverableRuntimeBackend> createBackend,
        Func<Exception, CancellationToken, ValueTask> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(startSession);
        ArgumentNullException.ThrowIfNull(restartSession);
        ArgumentNullException.ThrowIfNull(createBackend);
        ArgumentNullException.ThrowIfNull(reportFailure);
        _startSession = startSession;
        _restartSession = restartSession;
        _createBackend = createBackend;
        _reportFailure = reportFailure;
    }

    public Task Completion => _loop ?? Task.CompletedTask;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException(
                "Runtime backend recovery has already started.");
        _loop = RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task RunAsync()
    {
        bool initial = true;
        while (true)
        {
            _stop.Token.ThrowIfCancellationRequested();
            IRuntimeEngineHostSession session;
            try
            {
                session = await (initial
                    ? _startSession(_stop.Token)
                    : _restartSession(_stop.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception launchFailure)
            {
                try
                {
                    await _reportFailure(launchFailure, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception reportFailure)
                {
                    throw new AggregateException(launchFailure, reportFailure);
                }
                throw;
            }
            initial = false;
            IRecoverableRuntimeBackend? backend = null;
            Exception? failure = null;
            try
            {
                backend = _createBackend(session) ??
                    throw new InvalidOperationException(
                        "Runtime backend factory returned no backend.");
                await backend.StartAsync(_stop.Token).ConfigureAwait(false);
                await backend.Completion.WaitAsync(_stop.Token).ConfigureAwait(false);
                failure = new RuntimeBackendUnexpectedCompletionException();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (backend is not null)
                {
                    try
                    {
                        await backend.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeFailure)
                    {
                        failure = failure is null
                            ? disposeFailure
                            : new AggregateException(failure, disposeFailure);
                    }
                }
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception sessionDisposeFailure)
                {
                    failure = failure is null
                        ? sessionDisposeFailure
                        : new AggregateException(failure, sessionDisposeFailure);
                }
            }
            if (_stop.IsCancellationRequested)
            {
                if (failure is not null) throw failure;
                return;
            }
            await _reportFailure(
                failure ?? new RuntimeBackendUnexpectedCompletionException(),
                _stop.Token).ConfigureAwait(false);
        }
    }
}

public sealed record RuntimeEngineHostRestartPolicy
{
    public RuntimeEngineHostRestartPolicy(
        int maxAttempts,
        TimeSpan window,
        TimeSpan initialDelay,
        TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
        if (initialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }
        if (maxDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay));
        }

        MaxAttempts = maxAttempts;
        Window = window;
        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
    }

    public int MaxAttempts { get; }
    public TimeSpan Window { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan MaxDelay { get; }
}

public sealed class RuntimeRestartBudget
{
    private readonly object _gate = new();
    private readonly RuntimeEngineHostRestartPolicy _policy;
    private readonly Queue<DateTimeOffset> _attempts = new();

    public RuntimeRestartBudget(RuntimeEngineHostRestartPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    public bool TryReserve(DateTimeOffset utcNow, out TimeSpan delay)
    {
        lock (_gate)
        {
            DateTimeOffset cutoff = utcNow - _policy.Window;
            while (_attempts.TryPeek(out DateTimeOffset attempt) && attempt < cutoff)
            {
                _attempts.Dequeue();
            }
            if (_attempts.Count >= _policy.MaxAttempts)
            {
                delay = default;
                return false;
            }

            if (_policy.InitialDelay == TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }
            else
            {
                double delayedMilliseconds = _policy.InitialDelay.TotalMilliseconds *
                    Math.Pow(2, _attempts.Count);
                delay = TimeSpan.FromMilliseconds(Math.Min(
                    _policy.MaxDelay.TotalMilliseconds,
                    delayedMilliseconds));
            }
            _attempts.Enqueue(utcNow);
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _attempts.Clear();
        }
    }
}

public sealed class RuntimeEngineHostSupervisor : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly TimeSpan _handshakeTimeout;
    private readonly RuntimeRestartBudget _restartBudget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RuntimeEngineHostSession? _current;
    private int _disposed;

    public RuntimeEngineHostSupervisor(
        string executablePath,
        TimeSpan handshakeTimeout,
        RuntimeEngineHostRestartPolicy restartPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (handshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }
        ArgumentNullException.ThrowIfNull(restartPolicy);

        _executablePath = Path.GetFullPath(executablePath);
        _handshakeTimeout = handshakeTimeout;
        _restartBudget = new RuntimeRestartBudget(restartPolicy);
    }

    public async ValueTask<RuntimeEngineHostSession> StartAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                throw new InvalidOperationException("EngineHost supervisor has already been started.");
            }
            _current = await RuntimeEngineHostLauncher.LaunchAsync(
                _executablePath,
                _handshakeTimeout,
                cancellationToken).ConfigureAwait(false);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RuntimeEngineHostSession> RestartAfterUnexpectedExitAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is null || !_current.HasExited)
            {
                throw new InvalidOperationException("EngineHost has not exited unexpectedly.");
            }
            if (!_restartBudget.TryReserve(DateTimeOffset.UtcNow, out TimeSpan delay))
            {
                throw new RuntimeRestartLimitExceededException();
            }

            RuntimeEngineHostSession previous = _current;
            _current = null;
            await previous.DisposeAsync().ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _current = await RuntimeEngineHostLauncher.LaunchAsync(
                _executablePath,
                _handshakeTimeout,
                cancellationToken).ConfigureAwait(false);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RuntimeEngineHostSession> RestartAfterRuntimeFailureAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is null)
                throw new InvalidOperationException(
                    "EngineHost supervisor has not been started.");
            if (!_restartBudget.TryReserve(DateTimeOffset.UtcNow, out TimeSpan delay))
                throw new RuntimeRestartLimitExceededException();

            RuntimeEngineHostSession previous = _current;
            _current = null;
            await previous.DisposeAsync().ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _current = await RuntimeEngineHostLauncher.LaunchAsync(
                _executablePath,
                _handshakeTimeout,
                cancellationToken).ConfigureAwait(false);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void MarkStable() => _restartBudget.Reset();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                await _current.DisposeAsync().ConfigureAwait(false);
                _current = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

public sealed class RuntimeRestartLimitExceededException : Exception
{
    public RuntimeRestartLimitExceededException()
        : base("EngineHost restart limit was reached within the configured window.")
    {
    }
}
