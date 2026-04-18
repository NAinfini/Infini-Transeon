"""Windows capture backend (Win32 window enumeration + mss frame grab)."""

from __future__ import annotations

import sys

from PySide6.QtGui import QGuiApplication

from infini_transeon.capture._mss_grab import ScreenGrabber
from infini_transeon.capture.base import (
    CaptureBackend,
    CaptureError,
    Frame,
    Rect,
    WindowInfo,
)
from infini_transeon.utils.logging import logger

if sys.platform == "win32":
    import win32con  # type: ignore[import-not-found]
    import win32gui  # type: ignore[import-not-found]
    import win32process  # type: ignore[import-not-found]
else:  # pragma: no cover - file only imported on Windows
    win32gui = win32con = win32process = None  # type: ignore[assignment]


class WindowsCapture(CaptureBackend):
    name = "windows"

    def __init__(self) -> None:
        if sys.platform != "win32":
            raise CaptureError("WindowsCapture only runs on Windows")
        self._grabber = ScreenGrabber()

    def list_windows(self, *, visible_only: bool = True) -> list[WindowInfo]:
        results: list[WindowInfo] = []

        def _enum(hwnd: int, _: object) -> bool:
            if visible_only and not win32gui.IsWindowVisible(hwnd):
                return True
            title = win32gui.GetWindowText(hwnd)
            if not title:
                return True
            try:
                phys = win32gui.GetWindowRect(hwnd)
            except Exception:  # noqa: BLE001
                return True
            px, py, pright, pbottom = phys
            if pright - px <= 1 or pbottom - py <= 1:
                return True
            rect, dpr = _physical_to_logical(px, py, pright, pbottom)
            try:
                _, pid = win32process.GetWindowThreadProcessId(hwnd)
            except Exception:  # noqa: BLE001
                pid = 0
            results.append(
                WindowInfo(
                    handle=int(hwnd),
                    title=title,
                    app=_process_name(pid),
                    rect=rect,
                    pid=pid,
                    is_minimized=bool(win32gui.IsIconic(hwnd)),
                    is_visible=True,
                    dpr=dpr,
                )
            )
            return True

        win32gui.EnumWindows(_enum, None)
        return results

    def get_window(self, handle: int) -> WindowInfo | None:
        if not win32gui.IsWindow(handle):
            return None
        try:
            title = win32gui.GetWindowText(handle)
            phys = win32gui.GetWindowRect(handle)
            _, pid = win32process.GetWindowThreadProcessId(handle)
        except Exception as exc:  # noqa: BLE001
            logger.debug("get_window failed for {}: {}", handle, exc)
            return None
        px, py, pright, pbottom = phys
        rect, dpr = _physical_to_logical(px, py, pright, pbottom)
        return WindowInfo(
            handle=handle,
            title=title,
            app=_process_name(pid),
            rect=rect,
            pid=pid,
            is_minimized=bool(win32gui.IsIconic(handle)),
            is_visible=bool(win32gui.IsWindowVisible(handle)),
            dpr=dpr,
        )

    def capture(self, window: WindowInfo) -> Frame | None:
        if window.is_minimized or not window.is_visible:
            return None
        return self._grabber.grab(window.rect, dpr=window.dpr)

    def capture_rect(self, rect: Rect, *, dpr: float = 1.0) -> Frame | None:
        if rect.width <= 0 or rect.height <= 0:
            return None
        return self._grabber.grab(rect, dpr=dpr)

    def close(self) -> None:
        self._grabber.close()


def _physical_to_logical(
    px: int, py: int, pright: int, pbottom: int
) -> tuple[Rect, float]:
    """Convert Win32's physical-pixel window rect into Qt-logical coords.

    Under PerMonitorV2 DPI awareness (set in app._ensure_dpi_awareness),
    GetWindowRect returns physical pixels. Qt / overlay code throughout
    this app work in *logical* pixels, so we divide by the DPR of whichever
    screen the window's centre point lands on. Returns (logical_rect, dpr).
    """
    app = QGuiApplication.instance()
    dpr = 1.0
    if app is not None:
        cx = (px + pright) // 2
        cy = (py + pbottom) // 2
        # screenAt uses logical coords on some builds and physical on others;
        # try the physical midpoint first and fall back to scanning virtual
        # geometry in logical space if that misses.
        screen = QGuiApplication.screenAt(_QPoint(cx, cy)) if QGuiApplication.screens() else None
        if screen is None:
            # Fallback: match by physical geometry overlap.
            for s in QGuiApplication.screens():
                g = s.geometry()
                sdpr = float(s.devicePixelRatio())
                pg_left = int(round(g.x() * sdpr))
                pg_top = int(round(g.y() * sdpr))
                pg_right = pg_left + int(round(g.width() * sdpr))
                pg_bottom = pg_top + int(round(g.height() * sdpr))
                if pg_left <= cx < pg_right and pg_top <= cy < pg_bottom:
                    screen = s
                    break
        if screen is not None:
            dpr = float(screen.devicePixelRatio()) or 1.0
    if dpr == 1.0:
        return (
            Rect(x=px, y=py, width=pright - px, height=pbottom - py),
            1.0,
        )
    lx = int(round(px / dpr))
    ly = int(round(py / dpr))
    lright = int(round(pright / dpr))
    lbottom = int(round(pbottom / dpr))
    return (
        Rect(x=lx, y=ly, width=lright - lx, height=lbottom - ly),
        dpr,
    )


def _QPoint(x: int, y: int):  # noqa: N802
    # Local import keeps Qt out of module import time on non-Qt test runs.
    from PySide6.QtCore import QPoint

    return QPoint(x, y)


def _process_name(pid: int) -> str:
    if not pid:
        return ""
    try:
        import psutil  # type: ignore[import-not-found]

        return psutil.Process(pid).name()
    except Exception:  # noqa: BLE001
        return ""


__all__ = ["WindowsCapture"]
