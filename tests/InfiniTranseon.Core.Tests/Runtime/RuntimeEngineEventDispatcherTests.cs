using System.Runtime.CompilerServices;
using System.Threading.Channels;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeEngineEventDispatcherTests
{
    [Fact]
    public void FullRuntimeEventQueueFailsExplicitlyAndClearsRejectedPayload()
    {
        var channel = Channel.CreateBounded<RuntimeEngineEvent>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        var accepted = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddSeconds(10),
            [1]);
        var rejected = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddSeconds(10),
            [2]);
        Assert.True(channel.Writer.TryWrite(accepted));

        RuntimeProtocolException failure = Assert.Throws<RuntimeProtocolException>(() =>
            RuntimeEngineHostSession.EnqueueEventOrThrow(channel.Writer, rejected));

        Assert.Equal(RuntimeProtocolError.EventQueueCapacityExceeded, failure.Error);
        Assert.True(rejected.IsDisposed);
        Assert.False(accepted.IsDisposed);
        accepted.Dispose();
    }

    [Fact]
    public async Task DispatchesNativeOcrResultToTypedHandlerWithoutCloudDispatch()
    {
        Guid epoch = Guid.NewGuid();
        OcrResultSnapshot expected = Result(epoch);
        OcrResultSnapshot? received = null;
        int cloudCalls = 0;
        var dispatcher = new RuntimeEngineEventDispatcher(
            (_, _) =>
            {
                cloudCalls++;
                return ValueTask.CompletedTask;
            },
            (result, _) =>
            {
                received = result;
                return ValueTask.CompletedTask;
            },
            (_, _) => ValueTask.CompletedTask);
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.OcrResult,
            Guid.NewGuid(),
            epoch,
            DateTimeOffset.UtcNow.AddSeconds(10),
            RuntimeOcrResultPayloadCodec.Encode(expected));

        await dispatcher.DispatchAsync(
            runtimeEvent,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, cloudCalls);
        Assert.NotNull(received);
        Assert.Equal(expected.ExecutionToken, received.ExecutionToken);
        Assert.Equal(expected.ModelId, received.ModelId);
        Assert.Equal(expected.ModelVersion, received.ModelVersion);
        Assert.Equal(expected.IsStable, received.IsStable);
        Assert.Equal(expected.TerminalErrorCode, received.TerminalErrorCode);
        Assert.Equal(expected.Lines, received.Lines);
    }

    [Fact]
    public async Task PumpDisposesSensitivePayloadWhenHandlerFails()
    {
        Guid epoch = Guid.NewGuid();
        var dispatcher = new RuntimeEngineEventDispatcher(
            (_, _) => ValueTask.CompletedTask,
            (_, _) => throw new InvalidOperationException("ocr.failed"),
            (_, _) => ValueTask.CompletedTask);
        var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.OcrResult,
            Guid.NewGuid(),
            epoch,
            DateTimeOffset.UtcNow.AddSeconds(10),
            RuntimeOcrResultPayloadCodec.Encode(Result(epoch)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.PumpAsync(
            One(runtimeEvent),
            TestContext.Current.CancellationToken));

        Assert.True(runtimeEvent.IsDisposed);
    }

    [Fact]
    public async Task SlowCloudOcrDoesNotBlockNativeOcrAndLifecycleEvents()
    {
        Guid epoch = Guid.NewGuid();
        var cloudStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCloud = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeOcrReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new RuntimeEngineEventDispatcher(
            async (_, cancellationToken) =>
            {
                cloudStarted.TrySetResult();
                await releaseCloud.Task.WaitAsync(cancellationToken);
            },
            (_, _) =>
            {
                nativeOcrReceived.TrySetResult();
                return ValueTask.CompletedTask;
            },
            (_, _) => ValueTask.CompletedTask);
        var cloudEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            epoch,
            DateTimeOffset.UtcNow.AddSeconds(10),
            [1]);
        var nativeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.OcrResult,
            Guid.NewGuid(),
            epoch,
            DateTimeOffset.UtcNow.AddSeconds(10),
            RuntimeOcrResultPayloadCodec.Encode(Result(epoch)));

        Task pump = dispatcher.PumpAsync(
            Two(cloudEvent, nativeEvent),
            TestContext.Current.CancellationToken);
        await cloudStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task first = await Task.WhenAny(
            nativeOcrReceived.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
        releaseCloud.TrySetResult();
        await pump;

        Assert.Same(nativeOcrReceived.Task, first);
        Assert.True(cloudEvent.IsDisposed);
        Assert.True(nativeEvent.IsDisposed);
    }

    [Fact]
    public async Task CloudOcrConcurrencyNeverExceedsTheConfiguredRuntimeCapacity()
    {
        Guid epoch = Guid.NewGuid();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;
        int calls = 0;
        var dispatcher = new RuntimeEngineEventDispatcher(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                int current = Interlocked.Increment(ref active);
                int observed;
                do
                {
                    observed = Volatile.Read(ref maximum);
                    if (current <= observed) break;
                }
                while (Interlocked.CompareExchange(ref maximum, current, observed) != observed);
                try { await release.Task.WaitAsync(cancellationToken); }
                finally { Interlocked.Decrement(ref active); }
            },
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            maximumConcurrentCloudOcr: 2);
        RuntimeEngineEvent[] runtimeEvents = Enumerable.Range(0, 3)
            .Select(_ => new RuntimeEngineEvent(
                RuntimeMessageKind.CloudOcrCropRequest,
                Guid.NewGuid(),
                epoch,
                DateTimeOffset.UtcNow.AddSeconds(10),
                [1]))
            .ToArray();

        Task pump = dispatcher.PumpAsync(
            Many(runtimeEvents),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => Volatile.Read(ref calls) == 2,
            TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref calls));
        release.TrySetResult();
        await pump;

        Assert.Equal(3, calls);
        Assert.Equal(2, maximum);
        Assert.All(runtimeEvents, runtimeEvent => Assert.True(runtimeEvent.IsDisposed));
    }

    [Fact]
    public async Task CloudOcrInfrastructureFailureStopsAnOtherwiseIdleEventPump()
    {
        Guid epoch = Guid.NewGuid();
        var dispatcher = new RuntimeEngineEventDispatcher(
            (_, _) => ValueTask.FromException(
                new InvalidOperationException("ocr.result.sinkFailed")),
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask);
        var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            epoch,
            DateTimeOffset.UtcNow.AddSeconds(10),
            [1]);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.PumpAsync(
                OneThenWait(runtimeEvent, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        Assert.Equal("ocr.result.sinkFailed", failure.Message);
        Assert.True(runtimeEvent.IsDisposed);
    }

    [Fact]
    public async Task RejectsExpiredRuntimeEventBeforeCallingHandler()
    {
        int calls = 0;
        var dispatcher = new RuntimeEngineEventDispatcher(
            (_, _) =>
            {
                calls++;
                return ValueTask.CompletedTask;
            },
            (_, _) =>
            {
                calls++;
                return ValueTask.CompletedTask;
            },
            (_, _) =>
            {
                calls++;
                return ValueTask.CompletedTask;
            });
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.OcrResult,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            RuntimeOcrResultPayloadCodec.Encode(Result(Guid.NewGuid())));

        RuntimeProtocolException error = await Assert.ThrowsAsync<RuntimeProtocolException>(
            () => dispatcher.DispatchAsync(
                runtimeEvent,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(RuntimeProtocolError.DeadlineExpired, error.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DispatchesTypedRuntimeBudgetSnapshotAndValidatesItsEpoch()
    {
        Guid epoch = Guid.NewGuid();
        RuntimeBudgetSnapshot? received = null;
        var dispatcher = new RuntimeEngineEventDispatcher(
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.CompletedTask,
            (snapshot, _) =>
            {
                received = snapshot;
                return ValueTask.CompletedTask;
            });
        var expected = new RuntimeBudgetSnapshot(
            RuntimeProtocol.CurrentVersion,
            epoch,
            2,
            DateTimeOffset.UtcNow,
            [new RuntimeBudgetPool(
                "engine.targets.slots", 8, 2, 0, RuntimeBudgetUnit.Slots)]);
        using var runtimeEvent = new RuntimeEngineEvent(
            RuntimeMessageKind.RuntimeBudgetSnapshot,
            Guid.NewGuid(),
            epoch,
            DateTimeOffset.UtcNow.AddSeconds(10),
            RuntimeBudgetSnapshotPayloadCodec.Encode(expected));

        await dispatcher.DispatchAsync(
            runtimeEvent,
            TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal(expected.SnapshotRevision, received.SnapshotRevision);
        Assert.Equal(expected.Pools, received.Pools);
    }

    private static async IAsyncEnumerable<RuntimeEngineEvent> One(RuntimeEngineEvent value)
    {
        await Task.Yield();
        yield return value;
    }

    private static async IAsyncEnumerable<RuntimeEngineEvent> Two(
        RuntimeEngineEvent first,
        RuntimeEngineEvent second)
    {
        await Task.Yield();
        yield return first;
        yield return second;
    }

    private static async IAsyncEnumerable<RuntimeEngineEvent> Many(
        IEnumerable<RuntimeEngineEvent> values)
    {
        await Task.Yield();
        foreach (RuntimeEngineEvent value in values) yield return value;
    }

    private static async IAsyncEnumerable<RuntimeEngineEvent> OneThenWait(
        RuntimeEngineEvent value,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return value;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static OcrResultSnapshot Result(Guid epoch)
    {
        var source = new SourceGenerationToken(
            epoch,
            new TargetInstanceId(Guid.NewGuid()),
            CaptureAreaKey.UserRegion(new RegionId(Guid.NewGuid())),
            new TextTrackId(Guid.NewGuid()),
            1,
            1);
        return new OcrResultSnapshot(
            new OcrExecutionToken(source, Guid.NewGuid(), 1, 1),
            [new TextLine("攻撃", new NormalizedRect(0.1, 0.2, 0.4, 0.2), 0.5)],
            "windows.media.ocr",
            "windows-11",
            IsStable: false,
            TerminalErrorCode: null);
    }
}
