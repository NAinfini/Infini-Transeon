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

/// <summary>What one caller asked for, independent of how many wire requests served it.</summary>
public sealed record LocalTranslationOutcome(bool Success, string? Text, string? ErrorCode);

/// <summary>
/// Owns the single worker process a local model runs in, and therefore owns the order in which
/// translations reach it. Requests do not go to the worker one at a time: every line the worker has
/// not already translated, from every request waiting at that moment, goes in one call. A decoding
/// step reads the whole model out of memory whether it produces one line or ten, so the lines a
/// caption, a name plate and a choice list need at the same instant cost far less decoded together
/// than decoded in turn.
/// </summary>
public sealed class LocalWorkerSessionManager : IAsyncDisposable
{
    private sealed class PendingTranslation
    {
        public required string ModelId { get; init; }
        public required string SourceLanguage { get; init; }
        public required string TargetLanguage { get; init; }
        public required int MaximumOutputCharacters { get; init; }
        public required string[] Lines { get; init; }

        /// <summary>Per line: the translation already known, or null while the worker still owes it.</summary>
        public required string?[] Resolved { get; init; }
        public required CancellationToken Cancellation { get; init; }
        public TaskCompletionSource<LocalTranslationOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Func<CancellationToken, ValueTask<ILocalWorkerSession>> _launch;
    private readonly LocalWorkerSessionManagerOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalTranslationLineLedger _lines = new();
    private readonly Queue<PendingTranslation> _queue = new();
    private bool _pumping;
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

    /// <summary>
    /// Brings the worker up before a translation asks for it. The worker reads a multi-gigabyte
    /// model before it answers its first request, so a session that starts it on demand pays that
    /// read inside its first translation and shows its opening line seconds after every later one.
    /// </summary>
    public async ValueTask WarmAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireSessionUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LocalTranslationOutcome> TranslateAsync(
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        string text,
        int maximumOutputCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputCharacters, 1);

        string[] lines = LocalTranslationLineLedger.SplitLines(text);
        var resolved = new string?[lines.Length];
        bool needsWorker = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!LocalTranslationLineLedger.HasContent(lines[index]))
            {
                resolved[index] = lines[index];
                continue;
            }
            resolved[index] = _lines.Find(modelId, sourceLanguage, targetLanguage, lines[index]);
            needsWorker |= resolved[index] is null;
        }
        if (!needsWorker)
            return new LocalTranslationOutcome(
                true, LocalTranslationLineLedger.JoinLines(resolved!), null);

        var pending = new PendingTranslation
        {
            ModelId = modelId,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            MaximumOutputCharacters = maximumOutputCharacters,
            Lines = lines,
            Resolved = resolved,
            Cancellation = cancellationToken,
        };
        bool startPump;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelIdleStopUnsafe();
            if (_activeRequests == 0)
                _requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeRequests++;
            _queue.Enqueue(pending);
            startPump = !_pumping;
            _pumping = true;
        }
        finally
        {
            _gate.Release();
        }
        if (startPump) _ = PumpAsync();
        return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            List<PendingTranslation> batch;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                batch = TakeBatchUnsafe();
                if (batch.Count == 0)
                {
                    _pumping = false;
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }
            await RunBatchAsync(batch).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes everything at the head of the queue the worker can answer in one call. Requests that
    /// name a different model or language pair need a different call, so the first of those ends
    /// the batch and starts the next one rather than being skipped over.
    /// </summary>
    private List<PendingTranslation> TakeBatchUnsafe()
    {
        var batch = new List<PendingTranslation>();
        int characters = 0;
        while (_queue.Count > 0)
        {
            PendingTranslation next = _queue.Peek();
            if (batch.Count > 0 &&
                (!string.Equals(next.ModelId, batch[0].ModelId, StringComparison.Ordinal) ||
                    !string.Equals(next.SourceLanguage, batch[0].SourceLanguage, StringComparison.Ordinal) ||
                    !string.Equals(next.TargetLanguage, batch[0].TargetLanguage, StringComparison.Ordinal)))
                break;
            int size = next.Lines.Sum(line => line.Length + 1);
            if (batch.Count > 0 && characters + size > LocalWorkerProtocol.MaximumTextCharacters)
                break;
            characters += size;
            batch.Add(_queue.Dequeue());
        }
        return batch;
    }

    private async Task RunBatchAsync(List<PendingTranslation> batch)
    {
        // Asked again here, not only when the request arrived: a batch that ran while this one
        // waited may already have translated the very lines it is missing.
        var order = new List<string>();
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (PendingTranslation pending in batch)
            for (int index = 0; index < pending.Lines.Length; index++)
            {
                if (pending.Resolved[index] is not null) continue;
                pending.Resolved[index] = _lines.Find(
                    pending.ModelId,
                    pending.SourceLanguage,
                    pending.TargetLanguage,
                    pending.Lines[index]);
                if (pending.Resolved[index] is null && position.TryAdd(pending.Lines[index], order.Count))
                    order.Add(pending.Lines[index]);
            }
        if (order.Count == 0)
        {
            await SettleAsync(batch, pending => pending.Completion.TrySetResult(
                new LocalTranslationOutcome(
                    true, LocalTranslationLineLedger.JoinLines(pending.Resolved!), null)))
                .ConfigureAwait(false);
            return;
        }

        ILocalWorkerSession session;
        try
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                session = await AcquireSessionUnsafeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception)
        {
            await FailAsync(batch, exception).ConfigureAwait(false);
            return;
        }

        // A batch is abandoned only when every caller has abandoned it: one superseded caption must
        // not take the translations its neighbours are still waiting for down with it.
        using var abandoned = new CancellationTokenSource();
        int waiting = batch.Count;
        var registrations = new List<CancellationTokenRegistration>(batch.Count);
        LocalTranslationResponse response;
        try
        {
            foreach (PendingTranslation pending in batch)
                registrations.Add(pending.Cancellation.Register(() =>
                {
                    if (Interlocked.Decrement(ref waiting) == 0) abandoned.Cancel();
                }));

            int budget = 0;
            foreach (PendingTranslation pending in batch)
                budget = Math.Min(
                    LocalWorkerProtocol.MaximumTextCharacters,
                    budget + pending.MaximumOutputCharacters);

            response = await session.Client.TranslateAsync(
                batch[0].ModelId,
                batch[0].SourceLanguage,
                batch[0].TargetLanguage,
                LocalTranslationLineLedger.JoinLines(order),
                budget,
                abandoned.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (IsFatalSessionFailure(exception))
            {
                try
                {
                    await RetireFailedSessionAsync(session).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    exception = new AggregateException(exception, cleanupException);
                }
            }
            await FailAsync(batch, exception).ConfigureAwait(false);
            return;
        }
        finally
        {
            foreach (CancellationTokenRegistration registration in registrations)
                registration.Dispose();
        }
        await CompleteAsync(batch, order, position, response).ConfigureAwait(false);
    }

    private async Task CompleteAsync(
        List<PendingTranslation> batch,
        List<string> order,
        Dictionary<string, int> position,
        LocalTranslationResponse response)
    {
        if (!response.Success)
        {
            var failure = new LocalTranslationOutcome(false, null, response.ErrorCode);
            await SettleAsync(batch, pending => pending.Completion.TrySetResult(failure))
                .ConfigureAwait(false);
            return;
        }

        string[] translated = LocalTranslationLineLedger.SplitLines(response.Text ?? string.Empty);
        if (translated.Length != order.Count)
        {
            await FailAsync(batch, new InvalidDataException(
                "Local worker answered with a different number of lines than it was given."))
                .ConfigureAwait(false);
            return;
        }
        for (int index = 0; index < order.Count; index++)
            if (translated[index].Length > 0)
                _lines.Store(
                    batch[0].ModelId,
                    batch[0].SourceLanguage,
                    batch[0].TargetLanguage,
                    order[index],
                    translated[index]);

        await SettleAsync(batch, pending =>
        {
            var assembled = new string[pending.Lines.Length];
            for (int index = 0; index < assembled.Length; index++)
                assembled[index] = pending.Resolved[index] ??
                    translated[position[pending.Lines[index]]];
            pending.Completion.TrySetResult(new LocalTranslationOutcome(
                true, LocalTranslationLineLedger.JoinLines(assembled), null));
        }).ConfigureAwait(false);
    }

    private Task FailAsync(List<PendingTranslation> batch, Exception exception) =>
        SettleAsync(batch, pending =>
        {
            if (exception is OperationCanceledException)
                pending.Completion.TrySetCanceled(pending.Cancellation);
            else
                pending.Completion.TrySetException(exception);
        });

    /// <summary>
    /// Records that the worker is free again before handing any caller its answer, so a caller that
    /// asks whether the worker is idle the moment its translation returns is told the truth.
    /// </summary>
    private async Task SettleAsync(List<PendingTranslation> batch, Action<PendingTranslation> settle)
    {
        await ReleaseRequestsAsync(batch.Count).ConfigureAwait(false);
        foreach (PendingTranslation pending in batch) settle(pending);
    }

    private async Task ReleaseRequestsAsync(int count)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _activeRequests -= count;
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

    /// <summary>Callers must hold <see cref="_gate" />.</summary>
    private async ValueTask<ILocalWorkerSession> AcquireSessionUnsafeAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelIdleStopUnsafe();
        return _session ??= await _launch(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Local worker launcher returned no session.");
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
        exception is IOException or ObjectDisposedException;

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
