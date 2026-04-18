from __future__ import annotations

from infini_transeon.capture.base import Rect
from infini_transeon.ocr.base import TextBlock
from infini_transeon.ocr.segmentation import segment


def test_segment_groups_nearby_blocks() -> None:
    blocks = [
        TextBlock(bbox=Rect(x=10, y=10, width=200, height=20), text="Hello", confidence=0.95),
        TextBlock(bbox=Rect(x=10, y=35, width=200, height=20), text="world", confidence=0.9),
    ]
    paragraphs = segment(blocks)
    assert len(paragraphs) == 1
    assert paragraphs[0].text == "Hello world"


def test_segment_splits_far_blocks() -> None:
    blocks = [
        TextBlock(bbox=Rect(x=10, y=10, width=200, height=20), text="A", confidence=0.95),
        TextBlock(bbox=Rect(x=10, y=200, width=200, height=20), text="B", confidence=0.9),
    ]
    paragraphs = segment(blocks)
    assert len(paragraphs) == 2
