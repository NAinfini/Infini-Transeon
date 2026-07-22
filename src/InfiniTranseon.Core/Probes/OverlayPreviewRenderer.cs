using InfiniTranseon.Contracts.Probes;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Probes;

/// <summary>
/// Overlay preview renderer. The EngineHost renders the translation overlay onto a real
/// layered, capture-excluded window and answers overlay commands with only an
/// acknowledgement (accepted / rejected) — it never returns a pixel buffer. The
/// <see cref="OverlayPreviewResult"/> contract asks for rendered RGBA bytes, which the
/// protocol (v1) cannot produce.
/// <para>
/// Rather than fabricate pixels, this probe fails loudly. Real overlay display goes through
/// the live path — <see cref="RuntimeEngineHostOverlaySink"/> /
/// <see cref="VisibilityGatingOverlaySink"/> applying an
/// <see cref="Contracts.Runtime.OverlayDesiredState"/> — not through a returned buffer.
/// </para>
/// </summary>
public sealed class OverlayPreviewRenderer : IOverlayPreviewRenderer
{
    public const string UnsupportedOperationKey = "overlayPixelPreview";

    public ValueTask<OverlayPreviewResult> RenderAsync(
        OverlayPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new EngineRuntimeUnsupportedOperationException(UnsupportedOperationKey);
    }
}
