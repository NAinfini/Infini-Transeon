"""Pre-OCR image enhancement pipeline.

OCR engines are trained on document-like input. UI screenshots and game
overlays have anti-aliased glyphs on low-contrast backgrounds that frustrate
them: PP-OCR regularly returns garbled output on text a human reads at a
glance.

The chain below is the same sequence the literature consistently reports as
highest-yield for screen text:

1. BGRA → grayscale to strip chroma noise.
2. CLAHE (contrast-limited adaptive histogram equalization) to boost the
   local foreground/background gap without amplifying global noise.
3. Bilateral denoise to smooth flat regions while keeping glyph edges sharp.
4. Smart upscale: frames whose long axis is below a threshold get 2x Lanczos
   scaling so small UI text clears PP-OCR's minimum glyph-pixel floor.

The function is a no-op when ``enabled`` is False, so it is safe to chain in
the pipeline unconditionally.
"""

from __future__ import annotations

import numpy as np

try:
    import cv2  # type: ignore[import-not-found]
except ImportError:  # pragma: no cover - cv2 is a hard dep
    cv2 = None  # type: ignore[assignment]


# Long-axis threshold below which we upscale 2x. Bigger frames already have
# enough pixels per glyph; upscaling them would just waste OCR compute.
_UPSCALE_LONG_AXIS_MIN = 1200


def enhance(image: np.ndarray, *, enabled: bool = True) -> tuple[np.ndarray, float]:
    """Return ``(enhanced_image, scale)`` where ``scale >= 1.0``.

    The returned image is always 3-channel BGR (RapidOCR's expected layout).
    ``scale`` is the upscale factor applied; callers must divide OCR bboxes by
    this value to map results back to input coordinates.
    """
    if not enabled or cv2 is None or image.size == 0:
        return _ensure_bgr(image), 1.0

    bgr = _ensure_bgr(image)
    h, w = bgr.shape[:2]

    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)

    # CLAHE clip=2.0, tile=8x8 is the default most OCR papers report.
    # Higher clip amplifies noise; larger tiles wash out local contrast.
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    contrasted = clahe.apply(gray)

    # Bilateral: d=5 is enough to smooth chroma-noise from anti-aliasing
    # without bloating runtime. sigmaColor/sigmaSpace 40/40 keeps edges.
    denoised = cv2.bilateralFilter(contrasted, d=5, sigmaColor=40, sigmaSpace=40)

    long_axis = max(h, w)
    if long_axis < _UPSCALE_LONG_AXIS_MIN:
        scale = 2.0
        new_size = (int(w * scale), int(h * scale))
        upscaled = cv2.resize(denoised, new_size, interpolation=cv2.INTER_LANCZOS4)
        output = cv2.cvtColor(upscaled, cv2.COLOR_GRAY2BGR)
        return output, scale

    output = cv2.cvtColor(denoised, cv2.COLOR_GRAY2BGR)
    return output, 1.0


def _ensure_bgr(image: np.ndarray) -> np.ndarray:
    """Collapse BGRA / grayscale inputs to 3-channel BGR."""
    if image.ndim == 2:
        if cv2 is None:
            return np.stack([image] * 3, axis=-1)
        return cv2.cvtColor(image, cv2.COLOR_GRAY2BGR)
    if image.shape[2] == 4:
        return image[:, :, :3]
    return image


__all__ = ["enhance"]
