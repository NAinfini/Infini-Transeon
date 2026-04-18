"""Protocols and data classes for OCR engines."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol, runtime_checkable

import numpy as np

from infini_transeon.capture.base import Rect


@dataclass(frozen=True, slots=True)
class TextBlock:
    """A single OCR detection with its bounding box in image coordinates."""

    bbox: Rect
    text: str
    confidence: float            # 0..1
    language: str | None = None  # detected script/language, optional


@dataclass(frozen=True, slots=True)
class Paragraph:
    """A group of text blocks aggregated into a logical unit."""

    bbox: Rect
    blocks: tuple[TextBlock, ...]
    text: str                    # joined text of the paragraph

    @property
    def confidence(self) -> float:
        if not self.blocks:
            return 0.0
        return sum(b.confidence for b in self.blocks) / len(self.blocks)


@runtime_checkable
class OcrEngine(Protocol):
    """OCR interface. Implementations must be thread-safe per call."""

    name: str

    def recognize(self, image: np.ndarray, *, lang_hint: str | None = None) -> list[TextBlock]:
        """Run OCR on a BGRA/BGR image, return detected text blocks."""

    def warmup(self) -> None:
        """Optional: preload models/weights to avoid first-call latency."""

    def close(self) -> None:
        """Release resources."""


__all__ = ["TextBlock", "Paragraph", "OcrEngine"]
