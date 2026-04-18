"""Exclude a top-level window from screen-capture on platforms that support it.

Without this, a translucent overlay drawn over the target region ends up in
the next capture frame, which re-feeds its own translation back into OCR and
produces corrupted mixed-language bubbles. On Windows, the SetWindowDisplay-
Affinity API with WDA_EXCLUDEFROMCAPTURE flag tells the compositor to skip
the window in any BitBlt / DWM thumbnail / Graphics Capture path — including
the mss backend we use. Requires Windows 10 2004 (build 19041) or newer.

macOS and Linux have no equivalent system call that mss respects, so callers
must fall back to hide-capture-show cycling on those platforms.
"""

from __future__ import annotations

import sys

from PySide6.QtGui import QWindow

from infini_transeon.utils.logging import logger


_WDA_EXCLUDEFROMCAPTURE = 0x00000011


def apply(window: QWindow) -> bool:
    """Try to hide ``window`` from screen capture. Returns True on success."""
    if sys.platform != "win32":
        return False
    try:
        import ctypes

        hwnd = int(window.winId())
        user32 = ctypes.windll.user32
        # BOOL SetWindowDisplayAffinity(HWND hWnd, DWORD dwAffinity);
        ok = bool(user32.SetWindowDisplayAffinity(hwnd, _WDA_EXCLUDEFROMCAPTURE))
        if not ok:
            err = ctypes.get_last_error()
            logger.debug("SetWindowDisplayAffinity failed: err={}", err)
        return ok
    except Exception as exc:  # noqa: BLE001 - best-effort, fall back to hide-cycle
        logger.debug("exclude_from_capture skipped: {}", exc)
        return False


__all__ = ["apply"]
