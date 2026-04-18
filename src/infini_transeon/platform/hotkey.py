"""Cross-platform global hotkeys via pynput."""

from __future__ import annotations

from collections.abc import Callable
from threading import Lock

from pynput import keyboard

from infini_transeon.utils.logging import logger


class HotkeyManager:
    """Register/unregister global hotkeys. Thread-safe; starts one listener."""

    def __init__(self) -> None:
        self._hotkeys: dict[str, Callable[[], None]] = {}
        self._listener: keyboard.GlobalHotKeys | None = None
        self._lock = Lock()

    def set_binding(self, combo: str, callback: Callable[[], None]) -> None:
        normalized = _normalize(combo)
        with self._lock:
            self._hotkeys[normalized] = callback
            self._rebuild()

    def clear(self) -> None:
        with self._lock:
            self._hotkeys.clear()
            self._rebuild()

    def stop(self) -> None:
        with self._lock:
            if self._listener is not None:
                self._listener.stop()
                self._listener = None

    def _rebuild(self) -> None:
        if self._listener is not None:
            self._listener.stop()
            self._listener = None
        if not self._hotkeys:
            return
        try:
            self._listener = keyboard.GlobalHotKeys(dict(self._hotkeys))
            self._listener.start()
        except Exception as exc:  # noqa: BLE001 - hotkey registration may fail on some WMs
            logger.warning("failed to install global hotkeys: {}", exc)
            self._listener = None


def _normalize(combo: str) -> str:
    """pynput expects combos like '<ctrl>+<alt>+t'."""
    parts = [p.strip().lower() for p in combo.split("+") if p.strip()]
    mapped: list[str] = []
    for p in parts:
        if p in {"ctrl", "alt", "shift", "cmd", "win", "super"}:
            mapped.append(f"<{p}>")
        elif len(p) == 1:
            mapped.append(p)
        else:
            mapped.append(f"<{p}>")
    return "+".join(mapped)


__all__ = ["HotkeyManager"]
