"""macOS capture backend (Quartz window enumeration + mss frame grab)."""

from __future__ import annotations

import sys

from infini_transeon.capture._mss_grab import ScreenGrabber
from infini_transeon.capture.base import (
    CaptureBackend,
    CaptureError,
    Frame,
    Rect,
    WindowInfo,
)

if sys.platform == "darwin":
    from Quartz import (  # type: ignore[import-not-found]
        CGWindowListCopyWindowInfo,
        kCGNullWindowID,
        kCGWindowListOptionOnScreenOnly,
    )
else:  # pragma: no cover - file only imported on macOS
    CGWindowListCopyWindowInfo = None  # type: ignore[assignment]
    kCGNullWindowID = 0  # type: ignore[assignment]
    kCGWindowListOptionOnScreenOnly = 0  # type: ignore[assignment]


class MacOSCapture(CaptureBackend):
    name = "macos"

    def __init__(self) -> None:
        if sys.platform != "darwin":
            raise CaptureError("MacOSCapture only runs on macOS")
        self._grabber = ScreenGrabber()

    def list_windows(self, *, visible_only: bool = True) -> list[WindowInfo]:
        windows = CGWindowListCopyWindowInfo(
            kCGWindowListOptionOnScreenOnly if visible_only else 0,
            kCGNullWindowID,
        )
        results: list[WindowInfo] = []
        for entry in windows or []:
            title = entry.get("kCGWindowName") or ""
            owner = entry.get("kCGWindowOwnerName") or ""
            bounds = entry.get("kCGWindowBounds") or {}
            wid = int(entry.get("kCGWindowNumber") or 0)
            pid = int(entry.get("kCGWindowOwnerPID") or 0)
            x = int(bounds.get("X", 0))
            y = int(bounds.get("Y", 0))
            w = int(bounds.get("Width", 0))
            h = int(bounds.get("Height", 0))
            if w < 2 or h < 2:
                continue
            if not title and not owner:
                continue
            results.append(
                WindowInfo(
                    handle=wid,
                    title=str(title or owner),
                    app=str(owner),
                    rect=Rect(x=x, y=y, width=w, height=h),
                    pid=pid,
                )
            )
        return results

    def get_window(self, handle: int) -> WindowInfo | None:
        for info in self.list_windows(visible_only=False):
            if info.handle == handle:
                return info
        return None

    def capture(self, window: WindowInfo) -> Frame | None:
        return self._grabber.grab(window.rect, dpr=window.dpr)

    def capture_rect(self, rect: Rect, *, dpr: float = 1.0) -> Frame | None:
        if rect.width <= 0 or rect.height <= 0:
            return None
        return self._grabber.grab(rect, dpr=dpr)

    def close(self) -> None:
        self._grabber.close()


__all__ = ["MacOSCapture"]
