"""Shared ``mss``-based screen capture utilities used by all platforms."""

from __future__ import annotations

import threading

import numpy as np

from infini_transeon.capture.base import Frame, Rect

try:
    import mss  # type: ignore[import-not-found]
except ImportError:  # pragma: no cover - mss is a required dep
    mss = None  # type: ignore[assignment]


class ScreenGrabber:
    """Thin wrapper around ``mss.mss`` that yields BGRA frames.

    ``mss`` stores GDI handles in ``threading.local``; calling ``grab`` from a
    thread that did not construct the instance raises ``AttributeError``.
    Keep one ``mss.mss()`` per thread instead of sharing a single instance.
    """

    def __init__(self) -> None:
        if mss is None:
            raise RuntimeError("mss is not installed")
        self._tls = threading.local()
        self._instances: list[object] = []
        self._lock = threading.Lock()

    def _local_sct(self):  # type: ignore[no-untyped-def]
        sct = getattr(self._tls, "sct", None)
        if sct is None:
            sct = mss.mss()
            self._tls.sct = sct
            with self._lock:
                self._instances.append(sct)
        return sct

    def grab(self, rect: Rect, *, dpr: float = 1.0) -> Frame:
        """Grab ``rect`` (Qt-logical pixels) as a BGRA frame.

        ``mss`` works in physical device pixels, so we scale up by ``dpr``
        before asking it. The returned ``Frame.rect`` keeps the original
        logical rect, but ``Frame.image`` is physical-sized — downstream
        callers divide OCR bbox coords by ``dpr`` to map back to logical.
        """
        if dpr and dpr > 0 and dpr != 1.0:
            monitor = {
                "top": int(round(rect.y * dpr)),
                "left": int(round(rect.x * dpr)),
                "width": int(round(rect.width * dpr)),
                "height": int(round(rect.height * dpr)),
            }
        else:
            monitor = {"top": rect.y, "left": rect.x, "width": rect.width, "height": rect.height}
        shot = self._local_sct().grab(monitor)
        img = np.frombuffer(shot.rgb, dtype=np.uint8).reshape(shot.height, shot.width, 3)
        # ``mss`` returns RGB; convert to BGRA by appending an alpha channel + channel swap
        bgra = np.empty((shot.height, shot.width, 4), dtype=np.uint8)
        bgra[..., 0] = img[..., 2]
        bgra[..., 1] = img[..., 1]
        bgra[..., 2] = img[..., 0]
        bgra[..., 3] = 255
        return Frame(image=bgra, rect=rect, dpr=dpr)

    def close(self) -> None:
        with self._lock:
            instances = list(self._instances)
            self._instances.clear()
        for sct in instances:
            try:
                sct.close()  # type: ignore[attr-defined]
            except Exception:  # noqa: BLE001
                pass


__all__ = ["ScreenGrabber"]
