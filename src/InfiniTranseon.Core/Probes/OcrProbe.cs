using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Probes;

/// <summary>
/// One-shot OCR probe. The EngineHost protocol (v1) exposes no app-to-engine "recognize
/// this image" message: the engine performs OCR only on frames it captures for registered
/// targets and emits <see cref="Contracts.Runtime.RuntimeMessageKind.OcrResult"/> events
/// on its own cadence. Recognizing a caller-supplied crop is therefore not something the
/// engine can do, and there is no managed OCR path in this layer to fall back to.
/// <para>
/// Rather than fabricate a result, this probe fails loudly. The presentation-neutral fakes
/// remain the source of preview data until a real recognition message is added to the
/// protocol; at that point this probe should drive it through the EngineHost session.
/// </para>
/// </summary>
public sealed class OcrProbe : IOcrProbe
{
    public const string UnsupportedOperationKey = "cropOcr";

    public ValueTask<OcrProbeResult> RecognizeAsync(
        OcrProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new EngineRuntimeUnsupportedOperationException(UnsupportedOperationKey);
    }
}
