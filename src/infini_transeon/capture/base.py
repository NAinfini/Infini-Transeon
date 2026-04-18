"""Protocols and data classes for platform-specific screen capture."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol, runtime_checkable

import numpy as np


@dataclass(frozen=True, slots=True)
class Rect:
    x: int
    y: int
    width: int
    height: int

    @property
    def right(self) -> int:
        return self.x + self.width

    @property
    def bottom(self) -> int:
        return self.y + self.height


@dataclass(frozen=True, slots=True)
class WindowInfo:
    """A target window as described by the platform."""

    handle: int                 # HWND / CGWindowID / XID, 0 means unknown
    title: str
    app: str                    # executable / bundle id / WM_CLASS
    rect: Rect
    pid: int = 0
    is_minimized: bool = False
    is_visible: bool = True
    dpr: float = 1.0            # device pixel ratio at time of enumeration


@dataclass(frozen=True, slots=True)
class RegionInfo:
    """A fixed rectangle on the virtual desktop, picked via drag-to-select."""

    rect: Rect
    label: str = ""             # user-facing name, e.g. "Region 1280x720"
    dpr: float = 1.0


@dataclass(frozen=True, slots=True)
class Frame:
    """A captured BGRA numpy image + its origin on the virtual desktop."""

    image: np.ndarray            # shape: (H, W, 4), dtype uint8
    rect: Rect                   # position in logical screen coords
    dpr: float = 1.0             # scaling for hi-DPI correction

    @property
    def height(self) -> int:
        return int(self.image.shape[0])

    @property
    def width(self) -> int:
        return int(self.image.shape[1])


@runtime_checkable
class CaptureBackend(Protocol):
    """Platform backend for window enumeration + capture."""

    name: str

    def list_windows(self, *, visible_only: bool = True) -> list[WindowInfo]:
        """Return windows the user could plausibly select."""

    def get_window(self, handle: int) -> WindowInfo | None:
        """Re-fetch metadata (rect / state) for a window by its handle."""

    def capture(self, window: WindowInfo) -> Frame | None:
        """Capture the window's current contents. Returns None when hidden/minimized."""

    def capture_rect(self, rect: Rect, *, dpr: float = 1.0) -> Frame | None:
        """Capture an arbitrary rectangle on the virtual desktop."""

    def close(self) -> None:
        """Release any native resources."""


class CaptureError(RuntimeError):
    """Raised when capture fails for reasons other than 'window gone'."""


__all__ = [
    "Rect",
    "WindowInfo",
    "RegionInfo",
    "Frame",
    "CaptureBackend",
    "CaptureError",
]
