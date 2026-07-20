using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Contracts.Probes;

public sealed record CaptureProbeRequest(string? NameFilter);

public sealed record CaptureProbeTarget(
    CaptureTargetId TargetId,
    string DisplayName,
    string Kind,
    int PixelWidth,
    int PixelHeight,
    int Dpi,
    bool Capturable,
    string? ErrorCode);

public sealed record CaptureProbeResult(IReadOnlyList<CaptureProbeTarget> Targets);

public interface ICaptureProbe
{
    ValueTask<CaptureProbeResult> ProbeAsync(
        CaptureProbeRequest request,
        CancellationToken cancellationToken);
}

public sealed record OcrProbeRequest(
    RegionId RegionId,
    int PixelWidth,
    int PixelHeight,
    ReadOnlyMemory<byte> EncodedCrop);

public sealed record OcrProbeResult(string Text, IReadOnlyList<TextLine> Lines, TimeSpan Latency);

public interface IOcrProbe
{
    ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken);
}

public sealed record TranslationProbeRequest(
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    string? Context);

public sealed record TranslationProbeResult(
    string ProviderId,
    string Text,
    TimeSpan Latency,
    string? ErrorCode);

public interface ITranslationProbe
{
    ValueTask<TranslationProbeResult> TranslateAsync(
        TranslationProbeRequest request,
        CancellationToken cancellationToken);
}

public sealed record OverlayPreviewRequest(
    string SourceText,
    IReadOnlyList<string> Translations,
    int PixelWidth,
    int PixelHeight);

public sealed record OverlayPreviewResult(int PixelWidth, int PixelHeight, byte[] RgbaPixels);

public interface IOverlayPreviewRenderer
{
    ValueTask<OverlayPreviewResult> RenderAsync(
        OverlayPreviewRequest request,
        CancellationToken cancellationToken);
}
