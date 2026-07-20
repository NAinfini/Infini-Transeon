namespace InfiniTranseon.Core.Runtime;

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
