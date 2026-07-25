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
    string? ErrorCode)
{
    /// <summary>
    /// Native OS handle for the enumerated target (HWND for windows, HMONITOR for
    /// monitors), packed as an unsigned value. Zero when unknown (for example the
    /// presentation-neutral fakes). Additive: existing constructors and fakes are
    /// unaffected.
    /// </summary>
    public ulong NativeHandle { get; init; }

    /// <summary>Owning process image name for window targets, when resolvable.</summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// Top-left pixel position in virtual-desktop coordinates for monitor targets.
    /// Values may be negative when a monitor is left of or above the primary display.
    /// </summary>
    public int DesktopX { get; init; }
    public int DesktopY { get; init; }
}

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
