"""Group ``TextBlock`` detections into ``Paragraph`` units.

Bias: **over-merge rather than over-split**. A translator given too much
context still produces coherent output; given too little (a half-sentence
cut off mid-clause) it produces nonsense. We'd rather a paragraph bbox
cover a little extra whitespace than split what was originally one
thought across multiple provider calls.

Heuristics:

* Blocks are first assigned to horizontal line bands (quantised by the
  median line height), so near-equal y-coordinates never interleave.
* Within a line band, blocks are ordered left-to-right and split on
  **very** large horizontal gaps (> ~3× line height). This is the one
  split we keep — a visual tab bar like "Character    Map    Discover"
  has 4-8× gaps between labels, and fusing those into a single string
  gives the translator nonsense that hurts translation quality too.
* Two line groups merge into the same paragraph when the vertical gap
  is small AND the left edges roughly align. We no longer require the
  previous line to "look wrapped" (fill ratio + terminator check) —
  sentences routinely end with a period at the visual line break, but
  the next line is still the same paragraph of narrative prose. The
  previous heuristic fragmented long paragraphs at every sentence end.

The goal is to give the translator enough context for multi-sentence
paragraphs without ever letting the resulting paragraph bbox cover
unrelated UI elements stacked vertically on their own rows.
"""

from __future__ import annotations

from statistics import median

from infini_transeon.capture.base import Rect
from infini_transeon.ocr.base import Paragraph, TextBlock


# Horizontal gap ceiling (as a multiple of line height) for merging
# adjacent blocks on the same row into a single line group. A 3× ceiling
# keeps normal word-spacing and wide inter-word gaps (e.g. justified text
# with extra spaces) inside a single line, while cleanly splitting UI tab
# rows where gaps between labels are typically 4-8× line height.
_INTRA_LINE_GAP_FACTOR = 3.0


def segment(blocks: list[TextBlock], *, line_gap_factor: float = 1.2) -> list[Paragraph]:
    """Group blocks into paragraphs, biased toward merging.

    ``line_gap_factor`` is the permitted vertical gap between adjacent
    lines expressed as a multiple of line height. 1.2 comfortably covers
    typical prose line-spacing (1.2-1.5×) so multi-line paragraphs stay
    together; larger gaps are treated as a new visual block.
    """
    if not blocks:
        return []
    ordered = sorted(blocks, key=lambda b: (b.bbox.y, b.bbox.x))
    lines = _group_into_lines(ordered)

    groups: list[list[TextBlock]] = []
    current = list(lines[0])
    prev_line_bbox = _union(b.bbox for b in current)
    prev_line_height = max(prev_line_bbox.height, 1)
    for line in lines[1:]:
        line_bbox = _union(b.bbox for b in line)
        line_height = max(line_bbox.height, prev_line_height, 1)
        vertical_gap = line_bbox.y - prev_line_bbox.bottom
        # Left-alignment tolerance is generous (full line-height) so
        # hanging indents, first-line indents, and justified paragraphs
        # all still merge as one unit. The only thing we're defending
        # against here is a totally-different column to the right.
        left_aligned = abs(line_bbox.x - prev_line_bbox.x) <= line_height
        # Merge aggressively: close vertical + roughly-aligned left edge
        # is enough. No longer gated on sentence terminators or "line
        # looks full" — those checks fragmented long narrative paragraphs
        # at every period, which ruined translation quality.
        if vertical_gap <= line_height * line_gap_factor and left_aligned:
            current.extend(line)
        else:
            groups.append(current)
            current = list(line)
        prev_line_bbox = line_bbox
        prev_line_height = line_height
    groups.append(current)

    paragraphs: list[Paragraph] = []
    for grp in groups:
        bbox = _union(b.bbox for b in grp)
        text = " ".join(b.text for b in grp).strip()
        paragraphs.append(Paragraph(bbox=bbox, blocks=tuple(grp), text=text))
    return paragraphs


def _group_into_lines(ordered: list[TextBlock]) -> list[list[TextBlock]]:
    """Bucket blocks into rows, then split each row on large horizontal gaps.

    Row bucketing: y-centres within half a line height go together, which
    prevents a block that starts 2 px higher than its line neighbour from
    being sorted before the whole previous line (used to produce zig-zag
    reading order on multi-column captures).

    Row splitting: within a row, adjacent blocks whose horizontal gap exceeds
    ``_INTRA_LINE_GAP_FACTOR * line_height`` become separate line groups.
    Without this split, UI tab bars with 4-8x-height gaps between labels got
    fused into single pseudo-sentences that wrecked translation.
    """
    heights = [max(b.bbox.height, 1) for b in ordered]
    typical = max(int(median(heights)), 1)
    band = max(typical // 2, 4)
    rows: list[list[TextBlock]] = []
    current: list[TextBlock] = []
    current_center: float | None = None
    for block in ordered:
        center = block.bbox.y + block.bbox.height / 2.0
        if current_center is None or abs(center - current_center) <= band:
            current.append(block)
            n = len(current)
            current_center = (
                ((current_center or 0.0) * (n - 1) + center) / n
                if current_center is not None
                else center
            )
        else:
            current.sort(key=lambda b: b.bbox.x)
            rows.append(current)
            current = [block]
            current_center = center
    if current:
        current.sort(key=lambda b: b.bbox.x)
        rows.append(current)

    # Split each row on large horizontal gaps between adjacent blocks.
    lines: list[list[TextBlock]] = []
    for row in rows:
        row_heights = [max(b.bbox.height, 1) for b in row]
        row_height = max(int(median(row_heights)), 1)
        gap_ceiling = max(int(row_height * _INTRA_LINE_GAP_FACTOR), 8)
        segment_buf: list[TextBlock] = [row[0]]
        for prev, nxt in zip(row, row[1:], strict=False):
            gap = nxt.bbox.x - prev.bbox.right
            if gap > gap_ceiling:
                lines.append(segment_buf)
                segment_buf = [nxt]
            else:
                segment_buf.append(nxt)
        lines.append(segment_buf)
    return lines


def _union(rects):
    rects = list(rects)
    x = min(r.x for r in rects)
    y = min(r.y for r in rects)
    right = max(r.right for r in rects)
    bottom = max(r.bottom for r in rects)
    return Rect(x=x, y=y, width=right - x, height=bottom - y)


__all__ = ["segment"]
