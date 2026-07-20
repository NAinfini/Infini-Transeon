using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeIpcAdmissionTests
{
    [Theory]
    [InlineData(RuntimeMessageKind.CloudOcrCropRequest, RuntimeMessageLane.Data)]
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
}
