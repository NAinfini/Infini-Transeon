"""Frameless, always-on-top, translucent overlay window.

Renders a list of translated :class:`Paragraph` overlays positioned over the
source bboxes. The window follows a target window's rect when the pipeline
reports a move/resize event.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from PySide6.QtCore import QRect, Qt, Signal
from PySide6.QtGui import (
    QColor,
    QFont,
    QFontMetrics,
    QImage,
    QPainter,
    QPainterPath,
    QPaintEvent,
    QPen,
    QPixmap,
)
from PySide6.QtWidgets import QWidget

from infini_transeon.capture.base import Rect
from infini_transeon.config.schema import OverlayConfig
from infini_transeon.overlay import click_through, exclude_capture


@dataclass(frozen=True, slots=True)
class OverlayItem:
    bbox: Rect
    text: str
    # Optional pre-sampled background colour for the region the bubble
    # covers (``#RRGGBB``). Fallback when ``bg_crop`` is unavailable.
    bg_color: str | None = None
    # Height of a single source text line in logical pixels, used to pick
    # a matching font size. For multi-line paragraphs this is the per-line
    # glyph height, not the paragraph's total height — using the total
    # height would render translations 3-5x too large.
    line_height: int = 0
    # Cropped pixels from the captured frame that sit directly under this
    # paragraph's source text, stored as a BGR/BGRA numpy array. When
    # present, the overlay blurs this crop and uses it as a "frosted glass"
    # backdrop — the blur inherits the local scene colour / luminance, so
    # multi-coloured or gradient backgrounds read as intentional UI instead
    # of a flat rectangular patch. None = fall back to ``bg_color``.
    bg_crop: Any = None


class OverlayWindow(QWidget):
    """Always-on-top, click-through, translucent paint surface."""

    closed = Signal()

    def __init__(self, config: OverlayConfig) -> None:
        super().__init__()
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
            | Qt.WindowType.NoDropShadowWindowHint
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating, True)
        self._config = config
        self._items: list[OverlayItem] = []
        self._font = QFont(config.font_family if config.font_family != "system" else "")
        self.setFont(self._font)
        self._apply_click_through(config.click_through)

    # --- public API ------------------------------------------------------

    def apply_config(self, config: OverlayConfig) -> None:
        """Update runtime-tunable overlay settings."""
        prev_click_through = self._config.click_through
        self._config = config
        self._font = QFont(config.font_family if config.font_family != "system" else "")
        self.setFont(self._font)
        if prev_click_through != config.click_through:
            self._apply_click_through(config.click_through)
        self.update()

    def set_items(self, items: list[OverlayItem]) -> None:
        self._items = items
        self.update()

    def clear(self) -> None:
        self._items = []
        self.update()

    def move_to(self, rect: Rect) -> None:
        self.setGeometry(QRect(rect.x, rect.y, rect.width, rect.height))

    def showEvent(self, event):  # noqa: D401 - Qt override
        super().showEvent(event)
        handle = self.windowHandle()
        if handle is not None:
            # Keep this overlay out of our own screen captures so translated
            # bubbles don't get re-OCR'd and fed back through the pipeline.
            exclude_capture.apply(handle)
            if self._config.click_through:
                click_through.apply(handle)

    def _apply_click_through(self, enabled: bool) -> None:
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, enabled)
        handle = self.windowHandle()
        if handle is not None:
            handle.setFlag(Qt.WindowType.WindowTransparentForInput, enabled)
            if enabled:
                click_through.apply(handle)

    def paintEvent(self, event: QPaintEvent) -> None:  # noqa: D401 - Qt override
        painter = QPainter(self)
        try:
            painter.setRenderHint(QPainter.RenderHint.Antialiasing, True)
            painter.setRenderHint(QPainter.RenderHint.TextAntialiasing, True)
            painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform, True)
            if self._config.high_contrast:
                # Black text on saturated yellow — WCAG AAA at typical sizes.
                bg_default = QColor("#FFFF00")
                bg_default.setAlphaF(max(self._config.bg_opacity, 0.85))
                text_default = QColor("#000000")
            else:
                bg_default = QColor(self._config.bg_color)
                bg_default.setAlphaF(self._config.bg_opacity)
                text_default = QColor(self._config.text_color)
            origin = self.geometry().topLeft()
            widget_w = self.width()
            widget_h = self.height()
            for item in self._items:
                base_rect = QRect(
                    item.bbox.x - origin.x(),
                    item.bbox.y - origin.y(),
                    max(item.bbox.width, 1),
                    max(item.bbox.height, 1),
                )
                # Per-bubble size tracks the source text height so each
                # translation renders close to the glyph size of the text it
                # replaces. Fall back to the full paragraph bbox height only
                # when the line-height metadata is missing.
                line_h = item.line_height if item.line_height > 0 else item.bbox.height
                size_px = max(12, int(line_h * 0.62 * self._config.font_scale))
                rect, font = _fit_text_rect(
                    base_rect,
                    item.text,
                    self._font,
                    widget_w,
                    widget_h,
                    size_px,
                )

                # Backdrop: prefer a blurred "frosted glass" crop of the
                # scene under the paragraph. Falls back to the sampled flat
                # colour (or user-configured default) when no crop is
                # available — or when the user has forced high-contrast.
                local_lum: float | None = None
                if (
                    item.bg_crop is not None
                    and not self._config.high_contrast
                ):
                    pix = _frosted_pixmap(item.bg_crop, rect.size())
                    if pix is not None:
                        painter.drawPixmap(rect, pix)
                        local_lum = _crop_luminance(item.bg_crop)
                    else:
                        painter.fillRect(rect, bg_default)
                elif item.bg_color is not None and not self._config.high_contrast:
                    bg = QColor(item.bg_color)
                    # Opaque fill — semi-transparent leaks the source text.
                    bg.setAlphaF(max(self._config.bg_opacity, 0.98))
                    painter.fillRect(rect, bg)
                    local_lum = _hex_luminance(item.bg_color)
                else:
                    painter.fillRect(rect, bg_default)

                # Text colour + outline: dark text with a light halo on
                # bright backdrops, light text with a dark halo on dark
                # ones. The halo lets the translation stay legible even
                # when the blurred crop has high-frequency detail.
                if self._config.high_contrast:
                    text_fill = text_default
                    text_outline = QColor("#FFFF00")
                elif local_lum is not None:
                    if local_lum > 0.5:
                        text_fill = QColor("#000000")
                        text_outline = QColor(255, 255, 255, 220)
                    else:
                        text_fill = QColor("#FFFFFF")
                        text_outline = QColor(0, 0, 0, 220)
                else:
                    text_fill = text_default
                    text_outline = QColor(0, 0, 0, 180)

                _draw_stroked_text(
                    painter,
                    rect.adjusted(4, 2, -4, -2),
                    font,
                    item.text,
                    fill=text_fill,
                    outline=text_outline,
                )
        finally:
            painter.end()


__all__ = ["OverlayItem", "OverlayWindow"]


def _frosted_pixmap(crop: Any, target_size) -> QPixmap | None:
    """Return a per-cell bg-colour pixmap sized to ``target_size``.

    The crop is downsampled to a coarse grid whose cells are roughly the
    size of a single source glyph, then upscaled with bilinear smoothing
    back to ``target_size``. At cell-level averaging, the source text
    strokes are dominated by the surrounding background pixels, so each
    cell becomes the local background colour. Bilinear upscale then
    produces smooth colour merges between neighbouring cells (and between
    lines), which gives the backdrop its per-character "inherit the
    original background" feel without needing per-glyph OCR bboxes.

    Design notes:

    * ``cell_px`` is heuristically tied to the crop's shorter side so one
      line of text maps to ~1 cell tall. On dense captures this smears
      text completely; on single-line tall text we still get a few cells
      across the width, enough to catch horizontal gradients.
    * Each cell is the **median** of its pixels — a mean would be biased
      by the dark text strokes, producing a muddy-grey wash on light
      backgrounds. Median is O(n log n) but cells are tiny, so cost per
      bubble stays well under a millisecond.
    """
    try:
        import numpy as np  # local import keeps overlay startup cheap
    except ImportError:
        return None
    if crop is None:
        return None
    arr = np.ascontiguousarray(crop)
    if arr.ndim != 3 or arr.shape[0] < 2 or arr.shape[1] < 2:
        return None
    h, w = arr.shape[:2]

    # Target one cell per ~line-height square so strokes average into bg.
    # Clamp grid to reasonable bounds: too small (<3 cells) defeats the
    # "per-character" feel; too large (>64 cells) burns median compute for
    # no visible gain at typical bubble sizes.
    short_side = max(6, min(h, w))
    cell_px = max(6, short_side // 2)
    gw = max(2, min(64, w // cell_px))
    gh = max(1, min(32, h // cell_px))

    # Trim crop to a multiple of (gh, gw) so reshape-median works cleanly.
    th = (h // gh) * gh
    tw = (w // gw) * gw
    if th < gh or tw < gw:
        return None
    trimmed = arr[:th, :tw, :3]  # drop alpha if present

    cell_h = th // gh
    cell_w = tw // gw
    # Reshape to (gh, cell_h, gw, cell_w, 3) so we can median over the two
    # within-cell axes at once.
    cells = trimmed.reshape(gh, cell_h, gw, cell_w, 3)
    cells = cells.transpose(0, 2, 1, 3, 4).reshape(gh, gw, cell_h * cell_w, 3)
    # Subsample inside each cell when it's large — 128 samples is plenty
    # for a stable median and cuts sort cost on big bubbles.
    if cells.shape[2] > 128:
        step = cells.shape[2] // 128
        cells = cells[:, :, ::step, :][:, :, :128, :]
    medians = np.median(cells, axis=2).astype(np.uint8)  # (gh, gw, 3) BGR

    # Build a tiny QImage from the medians. Capture is BGR; swap to RGB.
    rgb = np.ascontiguousarray(medians[:, :, ::-1])
    small = QImage(rgb.data, gw, gh, 3 * gw, QImage.Format.Format_RGB888).copy()

    tw_out = max(1, target_size.width())
    th_out = max(1, target_size.height())
    scaled = QPixmap.fromImage(small).scaled(
        tw_out,
        th_out,
        Qt.AspectRatioMode.IgnoreAspectRatio,
        Qt.TransformationMode.SmoothTransformation,
    )
    return scaled


def _crop_luminance(crop: Any) -> float:
    """Mean WCAG-style luminance of ``crop`` in [0, 1]."""
    try:
        import numpy as np
    except ImportError:
        return 0.5
    arr = crop[:, :, :3] if crop.ndim == 3 and crop.shape[2] >= 3 else crop
    # Capture is BGR; pull channels accordingly.
    b = arr[:, :, 0].astype("float32") / 255.0
    g = arr[:, :, 1].astype("float32") / 255.0
    r = arr[:, :, 2].astype("float32") / 255.0

    def _lin(x):
        return np.where(x <= 0.03928, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)

    lum = 0.2126 * _lin(r) + 0.7152 * _lin(g) + 0.0722 * _lin(b)
    return float(lum.mean())


def _hex_luminance(bg_hex: str) -> float:
    c = QColor(bg_hex)

    def _chan(v: int) -> float:
        s = v / 255.0
        return s / 12.92 if s <= 0.03928 else ((s + 0.055) / 1.055) ** 2.4

    return 0.2126 * _chan(c.red()) + 0.7152 * _chan(c.green()) + 0.0722 * _chan(c.blue())


def _draw_stroked_text(
    painter: QPainter,
    rect: QRect,
    font: QFont,
    text: str,
    *,
    fill: QColor,
    outline: QColor,
) -> None:
    """Draw ``text`` into ``rect`` with a contrast halo.

    Uses :class:`QPainterPath` so the outline sits *behind* the fill glyph
    edges instead of overwriting them — a simple "draw outline, then fill"
    would visibly thicken the strokes. Path rendering costs a few hundred
    microseconds per bubble, which is well below the per-frame budget.
    """
    painter.save()
    painter.setFont(font)
    flags = int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignTop | Qt.TextFlag.TextWordWrap)

    # QPainterPath.addText doesn't honour word-wrap; wrap manually using
    # QFontMetrics, then stamp each wrapped line.
    metrics = QFontMetrics(font)
    lines = _wrap_lines(metrics, text, rect.width())
    line_height = metrics.lineSpacing()
    baseline = rect.top() + metrics.ascent()

    path = QPainterPath()
    for line in lines:
        if baseline > rect.bottom() + metrics.descent():
            break
        path.addText(rect.left(), baseline, font, line)
        baseline += line_height

    # Halo: stroke the whole path in one pass.
    stroke_width = max(2.0, font.pixelSize() * 0.14)
    pen = QPen(outline)
    pen.setWidthF(stroke_width)
    pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
    pen.setCapStyle(Qt.PenCapStyle.RoundCap)
    painter.setPen(pen)
    painter.setBrush(Qt.BrushStyle.NoBrush)
    painter.drawPath(path)

    # Fill on top.
    painter.setPen(Qt.PenStyle.NoPen)
    painter.setBrush(fill)
    painter.drawPath(path)
    painter.restore()
    del flags  # suppress unused-var warning; kept for clarity of intent


def _wrap_lines(metrics: QFontMetrics, text: str, max_width: int) -> list[str]:
    """Greedy word-wrap; falls back to char-wrap for unbroken CJK runs."""
    if not text:
        return []
    words = text.split(" ")
    lines: list[str] = []
    current = ""
    for word in words:
        candidate = word if not current else current + " " + word
        if metrics.horizontalAdvance(candidate) <= max_width:
            current = candidate
            continue
        if current:
            lines.append(current)
        # Word alone exceeds max_width → char-wrap it.
        if metrics.horizontalAdvance(word) <= max_width:
            current = word
        else:
            buf = ""
            for ch in word:
                if metrics.horizontalAdvance(buf + ch) <= max_width:
                    buf += ch
                else:
                    if buf:
                        lines.append(buf)
                    buf = ch
            current = buf
    if current:
        lines.append(current)
    return lines


def _fit_text_rect(
    base: QRect,
    text: str,
    base_font: QFont,
    widget_w: int,
    widget_h: int,
    size_px: int,
) -> tuple[QRect, QFont]:
    """Return a (rect, font) that fits ``text`` within the source bbox width.

    Width is hard-clamped to the source text bbox — translations never
    grow wider than the text they replace, which prevents bubbles from
    blowing past window/screen edges on long target-language output.
    We only grow **downwards** (more wrapped lines) and, when even the
    wrapped version overflows, shrink the pixel size down to 12 px.

    Uses :meth:`QFont.setPixelSize` so the rendered size matches the bbox
    regardless of the widget's DPI, avoiding the blurry "point-size scaled
    on a high-DPI monitor" look. ``PreferAntialias`` / ``PreferQuality``
    force the native rasteriser to emit full anti-aliased glyphs instead
    of the default pixel-aligned hinting, which reads as low-res on 1.5x+
    screens.
    """
    pad_x = 8
    pad_y = 4

    size_px = max(12, size_px)
    font = QFont(base_font)
    font.setPixelSize(size_px)
    font.setHintingPreference(QFont.HintingPreference.PreferNoHinting)
    font.setStyleStrategy(
        QFont.StyleStrategy.PreferAntialias | QFont.StyleStrategy.PreferQuality
    )

    rect = QRect(base)
    # Keep width locked to the source bbox. Height can still grow
    # downwards to fit extra wrapped lines, bounded by the widget edge so
    # bubbles never paint outside the overlay surface.
    max_bottom = max(rect.bottom(), widget_h - 1)

    flags = int(Qt.TextFlag.TextWordWrap)
    metrics = QFontMetrics(font)
    bound = metrics.boundingRect(
        QRect(0, 0, rect.width() - pad_x, 100_000), flags, text
    )
    needed_h = bound.height() + pad_y
    if needed_h > rect.height():
        grow_h = min(needed_h - rect.height(), max_bottom - rect.bottom())
        if grow_h > 0:
            rect.setHeight(rect.height() + grow_h)

    # Last-resort pixel-size shrink, only if the wrapped text still can't
    # fit in the (possibly expanded vertically) rect. Floor at 12 px to
    # stay legible at any zoom.
    while size_px > 12:
        metrics = QFontMetrics(font)
        bound = metrics.boundingRect(
            QRect(0, 0, rect.width() - pad_x, rect.height() - pad_y), flags, text
        )
        if bound.width() <= rect.width() - pad_x and bound.height() <= rect.height() - pad_y:
            break
        size_px -= 1
        font.setPixelSize(size_px)

    del widget_w  # no longer used — kept in signature for call-site stability
    return rect, font
