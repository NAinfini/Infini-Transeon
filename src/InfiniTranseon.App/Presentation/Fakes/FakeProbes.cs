using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.App.Presentation.Fakes;

// Deterministic probe doubles used while building setup/workbench UI against the Task 1
// contracts. Real capture/OCR/translation/overlay integrations arrive at backend gates
// (frontend plan cross-plan dependency table).

public sealed class FakeCaptureProbe : ICaptureProbe
{
    private static readonly IReadOnlyList<CaptureProbeTarget> Targets =
    [
        new(new CaptureTargetId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "ELDEN RING™", "Window", 3840, 2160, 144, Capturable: true, ErrorCode: null),
        new(new CaptureTargetId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            "P5R.exe", "Window", 2560, 1440, 96, Capturable: true, ErrorCode: null),
        new(new CaptureTargetId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            "Display 2", "Display", 1920, 1080, 96, Capturable: true, ErrorCode: null),
    ];

    public ValueTask<CaptureProbeResult> ProbeAsync(
        CaptureProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CaptureProbeTarget> matches = string.IsNullOrWhiteSpace(request.NameFilter)
            ? Targets
            : Targets.Where(target => target.DisplayName.Contains(
                request.NameFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        return ValueTask.FromResult(new CaptureProbeResult(matches));
    }
}

/// <summary>
/// Design-time still frame: a flat opaque fill at the requested size. It never claims to be a
/// picture of anything — the real probe is the only thing that produces game pixels.
/// </summary>
public sealed class FakeStillFrameProbe : IStillFrameProbe
{
    public ValueTask<StillFrameProbeResult> CaptureAsync(
        StillFrameProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        int width = Math.Clamp(request.MaximumLongEdge, 16, 1920);
        int height = Math.Max(1, width * 9 / 16);
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x2E;
            pixels[index + 1] = 0x25;
            pixels[index + 2] = 0x23;
            pixels[index + 3] = 0xFF;
        }
        return ValueTask.FromResult(new StillFrameProbeResult(width, height, pixels));
    }
}

public sealed class FakeOcrProbe : IOcrProbe
{
    public ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        const string text = "力なき者よ、なぜ来た。";
        IReadOnlyList<TextLine> lines =
        [
            new(text, new NormalizedRect(0.05, 0.80, 0.90, 0.12), 0.98),
        ];
        return ValueTask.FromResult(new OcrProbeResult(text, lines, TimeSpan.FromMilliseconds(48)));
    }
}

public sealed class FakeTranslationProbe : ITranslationProbe
{
    public ValueTask<TranslationProbeResult> TranslateAsync(
        TranslationProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TranslationProbeResult(
            ProviderId: "fake-nmt",
            Text: "无力之人啊，你为何而来。",
            Latency: TimeSpan.FromMilliseconds(180),
            ErrorCode: null));
    }
}

public sealed class FakeOverlayPreviewRenderer : IOverlayPreviewRenderer
{
    public ValueTask<OverlayPreviewResult> RenderAsync(
        OverlayPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        int width = Math.Clamp(request.PixelWidth, 1, 640);
        int height = Math.Clamp(request.PixelHeight, 1, 360);
        // Opaque neutral fill; the preview never persists frames and never reveals real pixels.
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x23;
            pixels[index + 1] = 0x25;
            pixels[index + 2] = 0x2E;
            pixels[index + 3] = 0xFF;
        }

        return ValueTask.FromResult(new OverlayPreviewResult(width, height, pixels));
    }
}
