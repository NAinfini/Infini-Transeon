"""Helpers to convert OCR output + translations into overlay items."""

from __future__ import annotations

from statistics import median
from typing import Any

from infini_transeon.ocr.base import Paragraph
from infini_transeon.overlay.window import OverlayItem


def to_items(
    paragraphs: list[Paragraph],
    translations: list[str],
    bg_colors: list[str | None] | None = None,
    bg_crops: list[Any] | None = None,
) -> list[OverlayItem]:
    items: list[OverlayItem] = []
    n = len(paragraphs)
    colors: list[str | None] = bg_colors or [None] * n
    crops: list[Any] = bg_crops or [None] * n
    for paragraph, text, bg, crop in zip(
        paragraphs, translations, colors, crops, strict=False
    ):
        if not text:
            continue
        items.append(
            OverlayItem(
                bbox=paragraph.bbox,
                text=text,
                bg_color=bg,
                line_height=_line_height(paragraph),
                bg_crop=crop,
            )
        )
    return items


def _line_height(paragraph: Paragraph) -> int:
    """Median height of the paragraph's constituent single-line OCR blocks.

    ``paragraph.bbox`` is the union of all lines, so on a 5-line paragraph
    it is roughly 5x the glyph height. Using it directly for font sizing
    produces text that's several times larger than the source.
    """
    heights = [max(b.bbox.height, 1) for b in paragraph.blocks]
    if not heights:
        return max(paragraph.bbox.height, 1)
    return max(int(median(heights)), 1)


__all__ = ["to_items"]
