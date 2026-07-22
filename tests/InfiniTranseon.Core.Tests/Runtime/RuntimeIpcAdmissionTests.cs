using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeIpcAdmissionTests
{
    [Theory]
    [InlineData(RuntimeMessageKind.CloudOcrCropRequest, RuntimeMessageLane.Data)]
    [InlineData(RuntimeMessageKind.OcrResult, RuntimeMessageLane.Data)]
    [InlineData(RuntimeMessageKind.Thumbnail, RuntimeMessageLane.Data)]
    [InlineData(RuntimeMessageKind.ControlRequest, RuntimeMessageLane.Control)]
    [InlineData(RuntimeMessageKind.ShutdownRequest, RuntimeMessageLane.Control)]
    public void ClassifiesLargePayloadsSeparatelyFromControl(
        RuntimeMessageKind kind,
        RuntimeMessageLane expected)
    {
        Assert.Equal(expected, RuntimeMessageLaneClassifier.Classify(kind));
    }

    [Fact]
    public void EnforcesLaneItemLimitsAndReturnsCapacityOnDispose()
    {
        var admission = new RuntimeIpcAdmission(new RuntimeIpcBackpressureOptions(
            controlMaxItems: 2,
            dataMaxItems: 1,
            controlMaxBytes: 256,
            dataMaxBytes: 512,
            totalMaxBytes: 600));

        Assert.True(admission.TryAcquire(RuntimeMessageLane.Control, 100, out RuntimeIpcLease? first));
        Assert.True(admission.TryAcquire(RuntimeMessageLane.Control, 100, out RuntimeIpcLease? second));
        Assert.False(admission.TryAcquire(RuntimeMessageLane.Control, 1, out _));
        Assert.True(admission.TryAcquire(RuntimeMessageLane.Data, 300, out RuntimeIpcLease? data));
        Assert.False(admission.TryAcquire(RuntimeMessageLane.Data, 1, out _));
        Assert.Equal(500, admission.TotalReservedBytes);

        first!.Dispose();
        Assert.True(admission.TryAcquire(RuntimeMessageLane.Control, 100, out RuntimeIpcLease? replacement));

        replacement!.Dispose();
        second!.Dispose();
        data!.Dispose();
        Assert.Equal(0, admission.TotalReservedBytes);
    }

    [Fact]
    public void SharedBudgetPreventsTwoLanesFromExceedingGlobalLimit()
    {
        var admission = new RuntimeIpcAdmission(new RuntimeIpcBackpressureOptions(
            controlMaxItems: 2,
            dataMaxItems: 2,
            controlMaxBytes: 500,
            dataMaxBytes: 500,
            totalMaxBytes: 550));

        Assert.True(admission.TryAcquire(RuntimeMessageLane.Control, 300, out RuntimeIpcLease? control));
        Assert.False(admission.TryAcquire(RuntimeMessageLane.Data, 300, out _));
        Assert.True(admission.TryAcquire(RuntimeMessageLane.Data, 250, out RuntimeIpcLease? data));

        control!.Dispose();
        data!.Dispose();
    }

    [Fact]
    public void AdmissionPublishesActualInFlightBytesThroughTheRuntimeLedger()
    {
        var ledger = new RuntimeBudgetLedger(
            Guid.NewGuid(),
            [new RuntimeBudgetPoolDefinition("app.ipc.inflight.bytes", 200)]);
        var admission = new RuntimeIpcAdmission(
            new RuntimeIpcBackpressureOptions(2, 2, 200, 200, 200),
            ledger,
            "app.ipc.inflight.bytes");

        Assert.True(admission.TryAcquire(
            RuntimeMessageLane.Control, 150, out RuntimeIpcLease? lease));
        RuntimeBudgetPool active = Assert.Single(ledger.Snapshot().Pools);
        Assert.Equal(150, active.Committed);
        Assert.Equal(0, active.Reserved);
        Assert.False(admission.TryAcquire(RuntimeMessageLane.Data, 51, out _));

        lease!.Dispose();
        Assert.Equal(0, Assert.Single(ledger.Snapshot().Pools).Committed);
    }

    [Fact]
    public void IncomingRuntimeEventOwnsItsIpcByteLeaseUntilPayloadDisposal()
    {
        var admission = new RuntimeIpcAdmission(new RuntimeIpcBackpressureOptions(
            controlMaxItems: 2,
            dataMaxItems: 2,
            controlMaxBytes: 100,
            dataMaxBytes: 100,
            totalMaxBytes: 100));
        Assert.True(admission.TryAcquire(
            RuntimeMessageLane.Data, 80, out RuntimeIpcLease? lease));
        var runtimeEvent = RuntimeEngineEvent.TakeOwnership(
            RuntimeMessageKind.CloudOcrCropRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddSeconds(10),
            new byte[24],
            lease);

        Assert.False(admission.TryAcquire(RuntimeMessageLane.Data, 21, out _));
        runtimeEvent.Dispose();
        Assert.True(admission.TryAcquire(
            RuntimeMessageLane.Data, 100, out RuntimeIpcLease? replacement));

        replacement!.Dispose();
    }

    [Fact]
    public void DirectionsKeepIndependentItemLimitsButShareOneByteBudget()
    {
        var ledger = new RuntimeBudgetLedger(
            Guid.NewGuid(),
            [new RuntimeBudgetPoolDefinition("app.ipc.inflight.bytes", 100)]);
        var options = new RuntimeIpcBackpressureOptions(1, 1, 100, 100, 100);
        var outgoing = new RuntimeIpcAdmission(
            options, ledger, "app.ipc.inflight.bytes");
        var incoming = new RuntimeIpcAdmission(
            options, ledger, "app.ipc.inflight.bytes");

        Assert.True(outgoing.TryAcquire(
            RuntimeMessageLane.Data, 40, out RuntimeIpcLease? request));
        Assert.True(incoming.TryAcquire(
            RuntimeMessageLane.Data, 40, out RuntimeIpcLease? runtimeEvent));
        Assert.False(incoming.TryAcquire(RuntimeMessageLane.Data, 1, out _));
        Assert.False(new RuntimeIpcAdmission(
            options, ledger, "app.ipc.inflight.bytes").TryAcquire(
                RuntimeMessageLane.Data, 21, out _));
        Assert.Equal(80, Assert.Single(ledger.Snapshot().Pools).Committed);

        request!.Dispose();
        runtimeEvent!.Dispose();
    }
}
