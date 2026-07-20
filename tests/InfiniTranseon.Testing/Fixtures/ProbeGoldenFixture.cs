using System.Text.Json;
using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Testing.Fixtures;

public sealed record OcrProbeFixture(RegionId RegionId, OcrProbeResult Result);

public sealed record ProbeGoldenFixture(
    CaptureProbeResult Capture,
    OcrProbeFixture Ocr,
    TranslationProbeResult Translation,
    OverlayPreviewResult Overlay)
{
    public static ProbeGoldenFixture Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        JsonElement capture = root.GetProperty("capture");
        var target = new CaptureProbeTarget(
            new CaptureTargetId(capture.GetProperty("targetId").GetGuid()),
            capture.GetProperty("displayName").GetString()!,
            capture.GetProperty("kind").GetString()!,
            capture.GetProperty("pixelWidth").GetInt32(),
            capture.GetProperty("pixelHeight").GetInt32(),
            capture.GetProperty("dpi").GetInt32(),
            capture.GetProperty("capturable").GetBoolean(),
            null);
        JsonElement ocr = root.GetProperty("ocr");
        RegionId regionId = new(ocr.GetProperty("regionId").GetGuid());
        var bounds = new NormalizedRect(0.1, 0.1, 0.8, 0.2);
        var ocrResult = new OcrProbeResult(
            ocr.GetProperty("text").GetString()!,
            [new TextLine(ocr.GetProperty("text").GetString()!, bounds, 0.99)],
            TimeSpan.FromMilliseconds(ocr.GetProperty("latencyMs").GetInt32()));
        JsonElement translation = root.GetProperty("translation");
        var translationResult = new TranslationProbeResult(
            translation.GetProperty("providerId").GetString()!,
            translation.GetProperty("text").GetString()!,
            TimeSpan.FromMilliseconds(translation.GetProperty("latencyMs").GetInt32()),
            null);
        JsonElement overlay = root.GetProperty("overlay");
        var overlayResult = new OverlayPreviewResult(
            overlay.GetProperty("pixelWidth").GetInt32(),
            overlay.GetProperty("pixelHeight").GetInt32(),
            Convert.FromBase64String(overlay.GetProperty("rgbaBase64").GetString()!));
        return new ProbeGoldenFixture(
            new CaptureProbeResult([target]),
            new OcrProbeFixture(regionId, ocrResult),
            translationResult,
            overlayResult);
    }
}
