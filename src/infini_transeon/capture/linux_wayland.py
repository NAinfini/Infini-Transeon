"""Placeholder for Wayland capture via ``xdg-desktop-portal`` (milestone M8)."""

from __future__ import annotations

from infini_transeon.capture.base import CaptureBackend, CaptureError, Frame, Rect, WindowInfo


class WaylandPortalCapture(CaptureBackend):
    name = "wayland"

    def __init__(self) -> None:
        raise CaptureError("Wayland capture is not implemented yet (tracked in M8)")

    def list_windows(self, *, visible_only: bool = True) -> list[WindowInfo]:  # pragma: no cover
        return []

    def get_window(self, handle: int) -> WindowInfo | None:  # pragma: no cover
        return None

    def capture(self, window: WindowInfo) -> Frame | None:  # pragma: no cover
        return None

    def capture_rect(self, rect: Rect, *, dpr: float = 1.0) -> Frame | None:  # pragma: no cover
        return None

    def close(self) -> None:  # pragma: no cover
        return None


__all__ = ["WaylandPortalCapture"]
