"""Loguru-based logging with key redaction."""

from __future__ import annotations

import re
import sys
from collections import deque
from threading import Lock
from typing import Any

from loguru import logger

from infini_transeon.config import paths

_REDACT_PATTERNS = [
    # OpenAI / OpenRouter / generic sk- prefix
    re.compile(r"sk-[A-Za-z0-9_\-]{16,}"),
    # Anthropic
    re.compile(r"sk-ant-[A-Za-z0-9_\-]{16,}"),
    # Google AIza prefix
    re.compile(r"AIza[0-9A-Za-z_\-]{20,}"),
    # HuggingFace
    re.compile(r"hf_[A-Za-z0-9]{16,}"),
    # Bearer tokens in headers we may dump
    re.compile(r"Bearer\s+[A-Za-z0-9_\-.]{16,}"),
]


def _redact(message: str) -> str:
    for pat in _REDACT_PATTERNS:
        message = pat.sub("[REDACTED]", message)
    return message


def _patcher(record: dict[str, Any]) -> None:
    record["message"] = _redact(record["message"])


class LogBuffer:
    """Thread-safe ring buffer of recent log lines, for the UI log panel."""

    def __init__(self, capacity: int = 500) -> None:
        self._capacity = capacity
        self._entries: deque[str] = deque(maxlen=capacity)
        self._lock = Lock()
        self._listeners: list = []

    def append(self, message: str) -> None:
        with self._lock:
            self._entries.append(message)
            listeners = list(self._listeners)
        for cb in listeners:
            try:
                cb(message)
            except Exception:  # noqa: BLE001 - never break logging
                pass

    def snapshot(self) -> list[str]:
        with self._lock:
            return list(self._entries)

    def clear(self) -> None:
        with self._lock:
            self._entries.clear()

    def subscribe(self, callback) -> None:
        with self._lock:
            self._listeners.append(callback)

    def unsubscribe(self, callback) -> None:
        with self._lock:
            if callback in self._listeners:
                self._listeners.remove(callback)


log_buffer = LogBuffer()


def _sink(message) -> None:
    # Loguru passes a Message object whose str(...) is the formatted line.
    log_buffer.append(str(message).rstrip())


def setup_logging(level: str = "INFO") -> None:
    """Configure loguru sinks. Safe to call multiple times."""
    paths.ensure_all()
    logger.remove()
    logger.configure(patcher=_patcher)
    logger.add(
        sys.stderr,
        level=level,
        backtrace=False,
        diagnose=False,
        enqueue=True,
        format=(
            "<green>{time:YYYY-MM-DD HH:mm:ss.SSS}</green> | "
            "<level>{level: <8}</level> | "
            "<cyan>{name}</cyan>:<cyan>{function}</cyan>:<cyan>{line}</cyan> - "
            "<level>{message}</level>"
        ),
    )
    logger.add(
        paths.log_dir() / "infini-transeon.log",
        level=level,
        rotation="10 MB",
        retention=5,
        compression="zip",
        backtrace=False,
        diagnose=False,
        enqueue=True,
    )
    # In-memory sink that powers the log panel. No color, plain text.
    logger.add(
        _sink,
        level="DEBUG",
        backtrace=False,
        diagnose=False,
        enqueue=False,
        colorize=False,
        format="{time:HH:mm:ss} | {level: <8} | {message}",
    )


__all__ = ["LogBuffer", "log_buffer", "setup_logging", "logger"]
