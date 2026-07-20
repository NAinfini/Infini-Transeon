using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeFrameQueueTests
{
    [Fact]
    public async Task ItemCapacityRejectsWithoutTakingOwnership()
    {
        using var queue = new RuntimeFrameQueue(maxItems: 1, maxBytes: 1024);
        RuntimeFrame first = CreateFrame(10);
        using RuntimeFrame rejected = CreateFrame(10);

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(rejected));
        Assert.Equal(RuntimeProtocol.WireHeaderBytes + 10, queue.ReservedBytes);

        await using RuntimeFrameLease lease = await queue.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, lease.Frame);
    }

    [Fact]
    public async Task ByteBudgetReturnsOnlyWhenConsumerReleasesLease()
    {
        int frameBytes = RuntimeProtocol.WireHeaderBytes + 20;
        using var queue = new RuntimeFrameQueue(maxItems: 2, maxBytes: frameBytes);
        RuntimeFrame first = CreateFrame(20);
        RuntimeFrame second = CreateFrame(20);

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        RuntimeFrameLease lease = await queue.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(frameBytes, queue.ReservedBytes);

        await lease.DisposeAsync();

        Assert.Equal(0, queue.ReservedBytes);
        Assert.True(queue.TryEnqueue(second));
    }

    private static RuntimeFrame CreateFrame(int payloadLength)
    {
        byte[] payload = new byte[payloadLength];
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            RuntimeMessageKind.ControlRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            payload.Length,
            DateTimeOffset.UtcNow.AddMinutes(1));
        return new RuntimeFrame(header, payload);
    }
}
