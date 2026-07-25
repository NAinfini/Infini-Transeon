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

/// <summary>
/// A single frame grabbed from a capture target without starting the engine.
/// <paramref name="NativeHandle"/> and <paramref name="Kind"/> come straight from the
/// <see cref="CaptureProbeTarget"/> the user picked.
/// </summary>
public sealed record StillFrameProbeRequest(
    ulong NativeHandle,
    string Kind,
    int MaximumLongEdge);

/// <summary>
/// Bottom-up-free, top-down BGRA32 pixels with no row padding
/// (<paramref name="PixelWidth"/> * 4 bytes per row). Encoding to a container format needs an
/// imaging stack, which Core deliberately does not depend on — the presentation layer already has
/// one and does that step.
/// </summary>
public sealed record StillFrameProbeResult(
    int PixelWidth,
    int PixelHeight,
    byte[] BgraPixels);

/// <summary>
/// Grabs one frame from a window or monitor in-process, so region drawing, preview and the OCR
/// test work before a profile has ever been started. This is not the runtime capture path: it uses
/// GDI, it is a still image, and it fails loudly (never a blank frame) when the target refuses to
/// render — notably fullscreen-exclusive and some DirectX games, for which the caller must tell the
/// user to use monitor capture instead.
/// </summary>
public interface IStillFrameProbe
{
    ValueTask<StillFrameProbeResult> CaptureAsync(
        StillFrameProbeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A one-shot recognition of a single already-encoded crop. <paramref name="LanguageTag"/> is the
/// BCP-47 tag of the text being read, or <c>"auto"</c>/<see langword="null"/> to use the user profile
/// languages — the same convention the native engine applies. Recognising Japanese with an English
/// model does not fail, it returns confident nonsense, so the caller's configured source language
/// must reach the engine rather than being guessed here.
/// </summary>
public sealed record OcrProbeRequest(
    RegionId RegionId,
    int PixelWidth,
    int PixelHeight,
    ReadOnlyMemory<byte> EncodedCrop,
    string? LanguageTag = null);

/// <summary>
/// <paramref name="LanguageTag"/> is the language the engine actually recognised with, which is not
/// necessarily the one requested: <c>"auto"</c> resolves to the account's display languages, so a
/// Japanese game read on an English-only Windows returns fluent-looking nonsense with no error. The
/// caller must be able to show which model ran.
/// </summary>
public sealed record OcrProbeResult(
    string Text,
    IReadOnlyList<TextLine> Lines,
    TimeSpan Latency,
    string? LanguageTag = null);

public interface IOcrProbe
{
    ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A one-shot translation test. <paramref name="ProviderId"/> selects which configured provider to
/// exercise; implementations that were constructed for a single fixed provider ignore it. It is
/// optional only so existing callers keep compiling — a probe that routes by provider must reject a
/// missing value rather than silently testing some other provider than the one on screen.
/// </summary>
public sealed record TranslationProbeRequest(
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    string? Context,
    string? ProviderId = null);

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
