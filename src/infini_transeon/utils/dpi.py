"""DPI / scaling helpers shared across capture and overlay modules."""

from __future__ import annotations

from infini_transeon.capture.base import Rect


def logical_to_physical(rect: Rect, dpr: float) -> Rect:
    return Rect(
        x=int(rect.x * dpr),
        y=int(rect.y * dpr),
        width=int(rect.width * dpr),
        height=int(rect.height * dpr),
    )


def physical_to_logical(rect: Rect, dpr: float) -> Rect:
    dpr = dpr or 1.0
    return Rect(
        x=int(rect.x / dpr),
        y=int(rect.y / dpr),
        width=int(rect.width / dpr),
        height=int(rect.height / dpr),
    )


__all__ = ["logical_to_physical", "physical_to_logical"]
