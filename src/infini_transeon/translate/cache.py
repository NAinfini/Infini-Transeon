"""Translation memory (TM) backed by diskcache/SQLite.

Key is a BLAKE2 hash over ``(source_lang, target_lang, style, text, glossary_fingerprint)``
so identical requests hit the same entry regardless of insertion order.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Any

import diskcache

from infini_transeon.config import paths


def _fingerprint(items: tuple[tuple[str, str], ...]) -> str:
    h = hashlib.blake2b(digest_size=16)
    for a, b in sorted(items):
        h.update(a.encode("utf-8"))
        h.update(b"\x00")
        h.update(b.encode("utf-8"))
        h.update(b"\x01")
    return h.hexdigest()


def make_key(
    text: str,
    *,
    source_lang: str,
    target_lang: str,
    style: str,
    glossary: tuple[tuple[str, str], ...] = (),
) -> str:
    h = hashlib.blake2b(digest_size=20)
    h.update(source_lang.encode("utf-8"))
    h.update(b"|")
    h.update(target_lang.encode("utf-8"))
    h.update(b"|")
    h.update(style.encode("utf-8"))
    h.update(b"|")
    h.update(_fingerprint(glossary).encode("ascii"))
    h.update(b"|")
    h.update(text.encode("utf-8"))
    return h.hexdigest()


@dataclass(frozen=True, slots=True)
class CacheEntry:
    text: str
    provider: str


class TranslationMemory:
    """Persistent, process-safe translation cache."""

    def __init__(self, *, size_limit_bytes: int = 256 * 1024 * 1024) -> None:
        paths.ensure_all()
        self._cache = diskcache.Cache(
            directory=str(paths.tm_dir()),
            size_limit=size_limit_bytes,
            eviction_policy="least-recently-used",
        )

    def get(self, key: str) -> CacheEntry | None:
        value: Any = self._cache.get(key)
        if value is None:
            return None
        if isinstance(value, dict):
            return CacheEntry(text=value["text"], provider=value.get("provider", ""))
        return None

    def set(self, key: str, entry: CacheEntry) -> None:
        self._cache.set(key, {"text": entry.text, "provider": entry.provider})

    def clear(self) -> None:
        self._cache.clear()

    def close(self) -> None:
        self._cache.close()

    def __len__(self) -> int:
        return len(self._cache)


__all__ = ["TranslationMemory", "CacheEntry", "make_key"]
