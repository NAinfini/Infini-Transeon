using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public delegate ValueTask RuntimeCloudOcrEventHandler(
    RuntimeEngineEvent runtimeEvent,
    CancellationToken cancellationToken);

public sealed class RuntimeEngineEventDispatcher
{
    private readonly RuntimeCloudOcrEventHandler _cloudOcr;
    private readonly Func<OcrResultSnapshot, CancellationToken, ValueTask> _ocrResult;
    private readonly Func<TargetLifecycleEvent, CancellationToken, ValueTask> _targetLifecycle;
    private readonly Func<RuntimeBudgetSnapshot, CancellationToken, ValueTask>? _budgetSnapshot;
    private readonly int _maximumConcurrentCloudOcr;

    public RuntimeEngineEventDispatcher(
        RuntimeCloudOcrEventHandler cloudOcr,
        Func<OcrResultSnapshot, CancellationToken, ValueTask> ocrResult,
        Func<TargetLifecycleEvent, CancellationToken, ValueTask> targetLifecycle,
        Func<RuntimeBudgetSnapshot, CancellationToken, ValueTask>? budgetSnapshot = null,
        int maximumConcurrentCloudOcr = 4)
    {
        ArgumentNullException.ThrowIfNull(cloudOcr);
        ArgumentNullException.ThrowIfNull(ocrResult);
        ArgumentNullException.ThrowIfNull(targetLifecycle);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentCloudOcr, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumConcurrentCloudOcr,
            RuntimeCapabilities.VersionOne.MaxOcrSessions);
        _cloudOcr = cloudOcr;
        _ocrResult = ocrResult;
        _targetLifecycle = targetLifecycle;
        _budgetSnapshot = budgetSnapshot;
        _maximumConcurrentCloudOcr = maximumConcurrentCloudOcr;
    }

    public async ValueTask DispatchAsync(
        RuntimeEngineEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        ObjectDisposedException.ThrowIf(runtimeEvent.IsDisposed, runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();
        if (runtimeEvent.RuntimeEpoch == Guid.Empty)
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
        if (runtimeEvent.DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new RuntimeProtocolException(RuntimeProtocolError.DeadlineExpired);

        try
        {
            switch (runtimeEvent.MessageKind)
            {
                case RuntimeMessageKind.CloudOcrCropRequest:
                    await _cloudOcr(runtimeEvent, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                case RuntimeMessageKind.OcrResult:
                {
                    OcrResultSnapshot result = RuntimeOcrResultPayloadCodec.Decode(
                        runtimeEvent.Payload.Span);
                    if (result.ExecutionToken.Source.RuntimeEpoch != runtimeEvent.RuntimeEpoch)
                        throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
                    await _ocrResult(result, cancellationToken).ConfigureAwait(false);
                    return;
                }
                case RuntimeMessageKind.TargetLifecycle:
                {
                    TargetLifecycleEvent lifecycle = RuntimeTargetLifecyclePayloadCodec.Decode(
                        runtimeEvent.Payload.Span);
                    await _targetLifecycle(lifecycle, cancellationToken).ConfigureAwait(false);
                    return;
                }
                case RuntimeMessageKind.RuntimeBudgetSnapshot:
                {
                    if (_budgetSnapshot is null)
                        throw new RuntimeProtocolException(
                            RuntimeProtocolError.UnexpectedMessageKind);
                    RuntimeBudgetSnapshot snapshot =
                        RuntimeBudgetSnapshotPayloadCodec.Decode(
                            runtimeEvent.Payload.Span);
                    if (snapshot.RuntimeEpoch != runtimeEvent.RuntimeEpoch)
                        throw new RuntimeProtocolException(
                            RuntimeProtocolError.InvalidRuntimeEpoch);
                    await _budgetSnapshot(snapshot, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                default:
                    throw new RuntimeProtocolException(RuntimeProtocolError.UnexpectedMessageKind);
            }
        }
        catch (InvalidDataException exception)
        {
            throw new RuntimeProtocolException(
                RuntimeProtocolError.InvalidPayloadLength, exception);
        }
    }

    public async Task PumpAsync(
        IAsyncEnumerable<RuntimeEngineEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cloudOcr = new HashSet<Task>();
        IAsyncEnumerator<RuntimeEngineEvent> enumerator =
            events.GetAsyncEnumerator(stop.Token);
        Task<bool>? moveNext = null;
        RuntimeEngineEvent? current = null;
        try
        {
            moveNext = enumerator.MoveNextAsync().AsTask();
            while (true)
            {
                await ObserveCompletedCloudOcrAsync(cloudOcr).ConfigureAwait(false);
                if (cloudOcr.Count > 0)
                {
                    Task completed = await Task.WhenAny(
                        cloudOcr.Append(moveNext)).ConfigureAwait(false);
                    if (!ReferenceEquals(completed, moveNext))
                    {
                        cloudOcr.Remove(completed);
                        await completed.ConfigureAwait(false);
                        continue;
                    }
                }
                if (!await moveNext.ConfigureAwait(false))
                {
                    moveNext = null;
                    break;
                }

                current = enumerator.Current;
                if (current.MessageKind == RuntimeMessageKind.CloudOcrCropRequest)
                {
                    while (cloudOcr.Count >= _maximumConcurrentCloudOcr)
                        await ObserveOneCloudOcrAsync(cloudOcr).ConfigureAwait(false);
                    cloudOcr.Add(DispatchOwnedCloudOcrAsync(current, stop.Token));
                    current = null;
                }
                else
                {
                    using (current)
                    {
                        await DispatchAsync(current, stop.Token).ConfigureAwait(false);
                    }
                    current = null;
                }
                moveNext = enumerator.MoveNextAsync().AsTask();
            }
            await Task.WhenAll(cloudOcr).ConfigureAwait(false);
        }
        catch
        {
            current?.Dispose();
            stop.Cancel();
            if (moveNext is not null)
            {
                try { await moveNext.ConfigureAwait(false); }
                catch { }
            }
            try { await Task.WhenAll(cloudOcr).ConfigureAwait(false); }
            catch { }
            throw;
        }
        finally
        {
            stop.Cancel();
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchOwnedCloudOcrAsync(
        RuntimeEngineEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        using (runtimeEvent)
        {
            await DispatchAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ObserveCompletedCloudOcrAsync(HashSet<Task> tasks)
    {
        foreach (Task completed in tasks.Where(task => task.IsCompleted).ToArray())
        {
            tasks.Remove(completed);
            await completed.ConfigureAwait(false);
        }
    }

    private static async Task ObserveOneCloudOcrAsync(HashSet<Task> tasks)
    {
        Task completed = await Task.WhenAny(tasks).ConfigureAwait(false);
        tasks.Remove(completed);
        await completed.ConfigureAwait(false);
    }
}
