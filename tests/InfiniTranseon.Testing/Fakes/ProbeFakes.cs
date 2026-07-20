using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Testing.Fixtures;

namespace InfiniTranseon.Testing.Fakes;

public sealed class FakeCaptureProbe(CaptureProbeResult result) : ICaptureProbe
{
    public ValueTask<CaptureProbeResult> ProbeAsync(
        CaptureProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }
}

public sealed class FakeOcrProbe(OcrProbeFixture fixture) : IOcrProbe
{
    public ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(fixture.Result);
    }
}

public sealed class FakeTranslationProbe(TranslationProbeResult result) : ITranslationProbe
{
    public ValueTask<TranslationProbeResult> TranslateAsync(
        TranslationProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }
}

public sealed class FakeOverlayPreviewRenderer(OverlayPreviewResult result) : IOverlayPreviewRenderer
{
    public ValueTask<OverlayPreviewResult> RenderAsync(
        OverlayPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result with { RgbaPixels = result.RgbaPixels.ToArray() });
    }
}
