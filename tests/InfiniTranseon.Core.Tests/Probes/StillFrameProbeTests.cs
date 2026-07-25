using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Core.Probes;

namespace InfiniTranseon.Core.Tests.Probes;

/// <summary>
/// Contract behaviour of the in-process still-frame probe. Capturing an actual desktop is not
/// asserted here: a CI agent has no meaningful screen content, and a test that passes only on a
/// developer's machine is worse than no test. What is asserted is that every refusal path produces
/// a typed, coded failure instead of a blank frame the UI would render as a black rectangle.
/// </summary>
public sealed class StillFrameProbeTests
{
    [Fact]
    public async Task A_missing_handle_is_reported_as_a_gone_target()
    {
        StillFrameUnavailableException failure =
            await Assert.ThrowsAsync<StillFrameUnavailableException>(async () =>
                await new StillFrameProbe().CaptureAsync(
                    new StillFrameProbeRequest(0, "Window", 640),
                    TestContext.Current.CancellationToken));

        Assert.Equal(StillFrameUnavailableException.TargetGoneCode, failure.ErrorCode);
    }

    [Fact]
    public async Task A_closed_window_is_reported_as_a_gone_target()
    {
        StillFrameUnavailableException failure =
            await Assert.ThrowsAsync<StillFrameUnavailableException>(async () =>
                await new StillFrameProbe().CaptureAsync(
                    // Window handles are small packed index/generation values; nothing near 2^32
                    // is ever handed out, so this exercises the IsWindow rejection deterministically.
                    new StillFrameProbeRequest(0xDEAD_BEEF, "Window", 640),
                    TestContext.Current.CancellationToken));

        Assert.Equal(StillFrameUnavailableException.TargetGoneCode, failure.ErrorCode);
    }

    [Fact]
    public async Task An_unknown_target_kind_is_refused_rather_than_guessed()
    {
        StillFrameUnavailableException failure =
            await Assert.ThrowsAsync<StillFrameUnavailableException>(async () =>
                await new StillFrameProbe().CaptureAsync(
                    new StillFrameProbeRequest(1, "Camera", 640),
                    TestContext.Current.CancellationToken));

        Assert.Equal(StillFrameUnavailableException.UnsupportedKindCode, failure.ErrorCode);
    }

    [Fact]
    public async Task A_long_edge_below_the_minimum_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await new StillFrameProbe().CaptureAsync(
                new StillFrameProbeRequest(1, "Monitor", 8),
                TestContext.Current.CancellationToken));

    [Fact]
    public async Task Cancellation_is_observed_before_any_native_call() =>
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new StillFrameProbe().CaptureAsync(
                new StillFrameProbeRequest(1, "Monitor", 640),
                new CancellationToken(canceled: true)));
}
