using InfiniTranseon.Contracts.Translation;

namespace InfiniTranseon.Core.Translation.Local;

public sealed record LocalWorkerSessionManagerOptions(TimeSpan IdleTimeout, bool PinWarm)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(IdleTimeout, TimeSpan.FromSeconds(5));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(IdleTimeout, TimeSpan.FromHours(1));
    }
}

public sealed class LocalWorkerSessionManager : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<ILocalWorkerSession>> _launch;
    private readonly LocalWorkerSessionManagerOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ILocalWorkerSession? _session;
    private CancellationTokenSource? _idleStopCancellation;
    private Task? _idleStopTask;
    private TaskCompletionSource? _requestsDrained;
    private DateTimeOffset _lastUsedUtc;
    private int _activeRequests;
    private bool _disposed;

    public LocalWorkerSessionManager(
        Func<CancellationToken, ValueTask<ILocalWorkerSession>> launch,
        LocalWorkerSessionManagerOptions options,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _launch = launch;
        _options = options;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _lastUsedUtc = _clock();
    }

    public async ValueTask<LocalTranslationResponse> TranslateAsync(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int maximumOutputCharacters,
        CancellationToken cancellationToken)
    {
        ILocalWorkerSession session;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelIdleStopUnsafe();
            _session ??= await _launch(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("Local worker launcher returned no session.");
            session = _session;
            if (_activeRequests == 0)
                _requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRequests++;
        }
        finally
        {
            _gate.Release();
        }
        try
        {
            return await session.Client.TranslateAsync(
                modelId,
                sourceLanguage,
                targetLanguage,
                text,
                maximumOutputCharacters,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFatalSessionFailure(exception))
        {
            Exception? cleanupFailure = null;
            try
            {
                await RetireFailedSessionAsync(session).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                cleanupFailure = cleanupException;
            }
            if (cleanupFailure is not null)
                throw new AggregateException(exception, cleanupFailure);
            throw;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _activeRequests--;
                _lastUsedUtc = _clock();
                if (_activeRequests == 0)
                {
                    _requestsDrained?.TrySetResult();
                    _requestsDrained = null;
                    if (!_disposed && _session is not null && !_options.PinWarm)
                        ScheduleIdleStopUnsafe();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask<bool> StopIfIdleAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ILocalWorkerSession? stopped = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_options.PinWarm || _activeRequests != 0 || _session is null ||
                now - _lastUsedUtc < _options.IdleTimeout) return false;
            stopped = _session;
            _session = null;
            CancelIdleStopUnsafe();
        }
        finally
        {
            _gate.Release();
        }
        await stopped.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        ILocalWorkerSession? stopped;
        Task? idleStopTask;
        Task activeRequestsDrained;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            stopped = _session;
            _session = null;
            _idleStopCancellation?.Cancel();
            _idleStopCancellation = null;
            idleStopTask = _idleStopTask;
            _idleStopTask = null;
            activeRequestsDrained = _activeRequests == 0
                ? Task.CompletedTask
                : _requestsDrained?.Task ??
                    throw new InvalidOperationException("Active local requests have no drain signal.");
        }
        finally
        {
            _gate.Release();
        }
        if (idleStopTask is not null) await idleStopTask.ConfigureAwait(false);
        await activeRequestsDrained.ConfigureAwait(false);
        if (stopped is not null) await stopped.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async ValueTask RetireFailedSessionAsync(ILocalWorkerSession failedSession)
    {
        ILocalWorkerSession? stopped = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_disposed && ReferenceEquals(_session, failedSession))
            {
                _session = null;
                CancelIdleStopUnsafe();
                stopped = failedSession;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (stopped is not null) await stopped.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsFatalSessionFailure(Exception exception) =>
        exception is IOException or ObjectDisposedException or LocalWorkerSessionCanceledException;

    private void ScheduleIdleStopUnsafe()
    {
        CancelIdleStopUnsafe();
        var cancellation = new CancellationTokenSource();
        _idleStopCancellation = cancellation;
        _idleStopTask = StopAfterIdleAsync(cancellation);
    }

    private void CancelIdleStopUnsafe()
    {
        _idleStopCancellation?.Cancel();
        _idleStopCancellation = null;
        _idleStopTask = null;
    }

    private async Task StopAfterIdleAsync(CancellationTokenSource cancellation)
    {
        try
        {
            TimeSpan remaining = _options.IdleTimeout;
            while (true)
            {
                await _delay(remaining, cancellation.Token).ConfigureAwait(false);
                ILocalWorkerSession? stopped = null;
                await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (_disposed || !ReferenceEquals(_idleStopCancellation, cancellation) ||
                        _session is null || _activeRequests != 0) return;
                    remaining = _options.IdleTimeout - (_clock() - _lastUsedUtc);
                    if (remaining > TimeSpan.Zero) continue;
                    stopped = _session;
                    _session = null;
                    _idleStopCancellation = null;
                    _idleStopTask = null;
                }
                finally
                {
                    _gate.Release();
                }
                await stopped.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
