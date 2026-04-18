"""Linux X11 capture backend (python-xlib enumeration + mss frame grab)."""

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
from infini_transeon.utils.logging import logger

if sys.platform == "linux":
    try:
        from Xlib import X, display  # type: ignore[import-not-found]
        from Xlib.error import XError  # type: ignore[import-not-found]

        _XLIB_OK = True
    except ImportError:  # pragma: no cover
        _XLIB_OK = False
else:  # pragma: no cover - file only imported on Linux
    _XLIB_OK = False


class LinuxX11Capture(CaptureBackend):
    name = "linux_x11"

    def __init__(self) -> None:
        if sys.platform != "linux" or not _XLIB_OK:
            raise CaptureError("Linux X11 capture requires python-xlib and an X11 display")
        try:
            self._display = display.Display()
        except Exception as exc:  # noqa: BLE001
            raise CaptureError(f"cannot open X display: {exc}") from exc
        self._root = self._display.screen().root
        self._grabber = ScreenGrabber()
        self._net_wm_name = self._display.intern_atom("_NET_WM_NAME")
        self._utf8_string = self._display.intern_atom("UTF8_STRING")
        self._net_client_list = self._display.intern_atom("_NET_CLIENT_LIST")

    def list_windows(self, *, visible_only: bool = True) -> list[WindowInfo]:
        try:
            prop = self._root.get_full_property(self._net_client_list, X.AnyPropertyType)
        except XError as exc:
            logger.warning("X11 client list failed: {}", exc)
            return []
        if prop is None:
            return []
        results: list[WindowInfo] = []
        for wid in prop.value:
            try:
                win = self._display.create_resource_object("window", wid)
                attrs = win.get_attributes()
                geom = win.get_geometry()
                # translate win's (0,0) into root coords -> absolute screen position
                translated = self._root.translate_coords(win, 0, 0)
                title_prop = win.get_full_property(self._net_wm_name, self._utf8_string)
                title = title_prop.value.decode("utf-8") if title_prop else ""
                cls = win.get_wm_class() or ("", "")
            except XError:
                continue
            if visible_only and attrs.map_state != X.IsViewable:
                continue
            x = int(translated.x)
            y = int(translated.y)
            w = int(geom.width)
            h = int(geom.height)
            if w < 2 or h < 2:
                continue
            results.append(
                WindowInfo(
                    handle=int(wid),
                    title=title,
                    app=cls[1] if len(cls) >= 2 else "",
                    rect=Rect(x=x, y=y, width=w, height=h),
                    is_visible=attrs.map_state == X.IsViewable,
                )
            )
        return results

    def get_window(self, handle: int) -> WindowInfo | None:
        for info in self.list_windows(visible_only=False):
            if info.handle == handle:
                return info
        return None

    def capture(self, window: WindowInfo) -> Frame | None:
        if not window.is_visible:
            return None
        return self._grabber.grab(window.rect, dpr=window.dpr)

    def capture_rect(self, rect: Rect, *, dpr: float = 1.0) -> Frame | None:
        if rect.width <= 0 or rect.height <= 0:
            return None
        return self._grabber.grab(rect, dpr=dpr)

    def close(self) -> None:
        self._grabber.close()
        try:
            self._display.close()
        except Exception:  # noqa: BLE001
            pass


__all__ = ["LinuxX11Capture"]
