"""Platform-specific helpers for enabling click-through on overlay windows."""

from __future__ import annotations

import sys

from PySide6.QtCore import Qt
from PySide6.QtGui import QWindow


def apply(window: QWindow) -> None:
    """Make the window transparent to mouse events across platforms.

    ``Qt.WA_TransparentForMouseEvents`` handles Windows and macOS correctly.
    For X11 we additionally use the shape extension via Qt's built-in support
    for ``Qt.WindowTransparentForInput`` (set at window-flag level by callers).
    """
    window.setFlag(Qt.WindowType.WindowTransparentForInput, True)
    if sys.platform == "linux":
        # ``setWindowMask`` with an empty region ensures X11 clicks pass through
        # on compositors that don't honour WindowTransparentForInput.
        try:
            from PySide6.QtGui import QRegion

            window.setMask(QRegion())
        except Exception:  # noqa: BLE001
            pass


__all__ = ["apply"]
