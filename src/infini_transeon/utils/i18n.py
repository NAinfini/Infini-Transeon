"""Lightweight i18n for UI strings.

Loads a flat key→string map from ``locale/<code>.yaml`` at startup. The
English file is the source of truth; translations fall back to English when a
key is missing. Pick the locale once via :func:`set_locale` (by language code)
and call :func:`tr` everywhere a user-visible string is produced.
"""

from __future__ import annotations

from importlib import resources
from threading import Lock

import yaml

_DEFAULT = "en"
_FALLBACK: dict[str, str] = {}
_ACTIVE: dict[str, str] = {}
_CURRENT = _DEFAULT
_LOCK = Lock()


def _load_file(code: str) -> dict[str, str]:
    try:
        with resources.files("infini_transeon.locale").joinpath(f"{code}.yaml").open(
            "r", encoding="utf-8"
        ) as fh:
            data = yaml.safe_load(fh) or {}
    except FileNotFoundError:
        return {}
    if not isinstance(data, dict):
        return {}
    return {str(k): str(v) for k, v in data.items()}


def _ensure_fallback() -> None:
    global _FALLBACK
    if not _FALLBACK:
        _FALLBACK = _load_file(_DEFAULT)


def set_locale(code: str | None) -> str:
    """Activate a locale by language code. Returns the code that was loaded."""
    global _ACTIVE, _CURRENT
    _ensure_fallback()
    requested = (code or _DEFAULT).strip() or _DEFAULT
    with _LOCK:
        strings = _load_file(requested)
        if not strings and "-" in requested:
            # e.g. zh-TW -> try zh
            strings = _load_file(requested.split("-", 1)[0])
            if strings:
                requested = requested.split("-", 1)[0]
        if not strings and requested != _DEFAULT:
            requested = _DEFAULT
            strings = _FALLBACK
        _ACTIVE = strings
        _CURRENT = requested
    return _CURRENT


def current_locale() -> str:
    return _CURRENT


def tr(key: str, **fmt: object) -> str:
    """Look up ``key`` in the active locale, falling back to English, then the key itself."""
    _ensure_fallback()
    value = _ACTIVE.get(key) or _FALLBACK.get(key) or key
    if fmt:
        try:
            return value.format(**fmt)
        except (KeyError, IndexError):
            return value
    return value


__all__ = ["current_locale", "set_locale", "tr"]
