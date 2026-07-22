using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Probes;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Probes;

public sealed class ExplicitFailureProbeTests
{
    [Fact]
    public async Task OcrProbeFailsLoudlyBecauseProtocolHasNoCropRecognition()
    {
        var probe = new OcrProbe();
        var request = new OcrProbeRequest(
            new RegionId(Guid.NewGuid()), 8, 8, ReadOnlyMemory<byte>.Empty);

        EngineRuntimeUnsupportedOperationException exception =
            await Assert.ThrowsAsync<EngineRuntimeUnsupportedOperationException>(
                async () => await probe.RecognizeAsync(
                    request, TestContext.Current.CancellationToken));

        Assert.Equal(OcrProbe.UnsupportedOperationKey, exception.OperationKey);
    }

    [Fact]
    public async Task OverlayPreviewRendererFailsLoudlyBecauseEngineReturnsNoPixels()
    {
        var renderer = new OverlayPreviewRenderer();
        var request = new OverlayPreviewRequest("source", ["target"], 64, 32);

        EngineRuntimeUnsupportedOperationException exception =
            await Assert.ThrowsAsync<EngineRuntimeUnsupportedOperationException>(
                async () => await renderer.RenderAsync(
                    request, TestContext.Current.CancellationToken));

        Assert.Equal(OverlayPreviewRenderer.UnsupportedOperationKey, exception.OperationKey);
    }
}
