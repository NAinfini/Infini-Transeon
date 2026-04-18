"""Platform permission checks and user guidance.

On macOS we must ensure Screen Recording permission is granted or screen
capture returns a black image silently. On Windows and Linux no extra
permissions are typically required for user-session windows.
"""

from __future__ import annotations

import sys
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class PermissionStatus:
    ok: bool
    message: str
    action_url: str | None = None


def check_capture_permission() -> PermissionStatus:
    if sys.platform == "darwin":
        return _check_macos_screen_recording()
    return PermissionStatus(ok=True, message="No additional permissions required.")


def _check_macos_screen_recording() -> PermissionStatus:
    try:
        from Quartz import (  # type: ignore[import-not-found]
            CGPreflightScreenCaptureAccess,
            CGRequestScreenCaptureAccess,
        )
    except ImportError:  # pragma: no cover - pyobjc missing
        return PermissionStatus(
            ok=False,
            message=(
                "pyobjc is not installed; cannot verify Screen Recording permission."
            ),
        )

    if CGPreflightScreenCaptureAccess():
        return PermissionStatus(ok=True, message="Screen recording permission granted.")
    # Trigger the system prompt but don't block on the result.
    CGRequestScreenCaptureAccess()
    return PermissionStatus(
        ok=False,
        message=(
            "Screen Recording permission is required. Enable Infini-Transeon in "
            "System Settings → Privacy & Security → Screen Recording, then restart."
        ),
        action_url="x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture",
    )


__all__ = ["PermissionStatus", "check_capture_permission"]
