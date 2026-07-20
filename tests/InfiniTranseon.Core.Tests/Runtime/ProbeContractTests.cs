using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Testing.Fakes;
using InfiniTranseon.Testing.Fixtures;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class ProbeContractTests
{
    [Fact]
    public async Task GoldenFixtureDrivesAllPresentationNeutralFakes()
    {
        ProbeGoldenFixture fixture = ProbeGoldenFixture.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "probe-golden-fixtures.json"));
        var capture = new FakeCaptureProbe(fixture.Capture);
        var ocr = new FakeOcrProbe(fixture.Ocr);
        var translation = new FakeTranslationProbe(fixture.Translation);
        var overlay = new FakeOverlayPreviewRenderer(fixture.Overlay);

        CaptureProbeResult captureResult = await capture.ProbeAsync(
            new CaptureProbeRequest(null),
            TestContext.Current.CancellationToken);
        OcrProbeResult ocrResult = await ocr.RecognizeAsync(
            new OcrProbeRequest(fixture.Ocr.RegionId, 320, 80, new byte[16]),
            TestContext.Current.CancellationToken);
        TranslationProbeResult translationResult = await translation.TranslateAsync(
            new TranslationProbeRequest("fixture", "en", "zh-Hans", "context"),
            TestContext.Current.CancellationToken);
        OverlayPreviewResult overlayResult = await overlay.RenderAsync(
            new OverlayPreviewRequest("原文", ["Translation"], 2, 2),
            TestContext.Current.CancellationToken);

        Assert.Single(captureResult.Targets);
        Assert.Equal("Hello", ocrResult.Text);
        Assert.Equal("你好", translationResult.Text);
        Assert.Equal(16, overlayResult.RgbaPixels.Length);
    }

    [Fact]
    public async Task FakeCancellationIsObservedBeforeReturningFixtureData()
    {
        ProbeGoldenFixture fixture = ProbeGoldenFixture.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "probe-golden-fixtures.json"));
        var probe = new FakeTranslationProbe(fixture.Translation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await probe.TranslateAsync(
                new TranslationProbeRequest("fixture", "en", "zh-Hans", null),
                cancellation.Token));
    }
}
