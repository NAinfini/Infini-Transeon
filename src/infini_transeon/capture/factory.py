"""Pick the correct capture backend for the current platform."""

from __future__ import annotations

import os
import sys

from infini_transeon.capture.base import CaptureBackend, CaptureError


def create() -> CaptureBackend:
    """Return a ready-to-use capture backend, raising :class:`CaptureError` on failure."""
    if sys.platform == "win32":
        from infini_transeon.capture.windows import WindowsCapture

        return WindowsCapture()
    if sys.platform == "darwin":
        from infini_transeon.capture.macos import MacOSCapture

        return MacOSCapture()
    if sys.platform == "linux":
        if os.environ.get("WAYLAND_DISPLAY") and not os.environ.get("DISPLAY"):
            from infini_transeon.capture.linux_wayland import WaylandPortalCapture

            return WaylandPortalCapture()
        from infini_transeon.capture.linux_x11 import LinuxX11Capture

        return LinuxX11Capture()
    raise CaptureError(f"unsupported platform: {sys.platform}")


__all__ = ["create"]
