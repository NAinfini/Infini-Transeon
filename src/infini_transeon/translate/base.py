"""Protocols, data classes and error types for translation providers."""

from __future__ import annotations

from collections.abc import AsyncIterator, Iterable, Sequence
from dataclasses import dataclass, field
from enum import StrEnum
from typing import Protocol, runtime_checkable


class ProviderKind(StrEnum):
    """Where a provider runs."""

    online = "online"
    local = "local"


@dataclass(frozen=True, slots=True)
class TranslationRequest:
    """A single batch of texts to translate."""

    texts: tuple[str, ...]
    source_lang: str             # ISO 639-1 or "auto"
    target_lang: str
    style: str = "general"
    glossary: tuple[tuple[str, str], ...] = ()  # (source_term, target_term)
    history: tuple[str, ...] = ()                # previous frame context
    trace_id: str | None = None


@dataclass(frozen=True, slots=True)
class Usage:
    input_tokens: int = 0
    output_tokens: int = 0
    cost_usd: float = 0.0


@dataclass(frozen=True, slots=True)
class TranslationResult:
    translations: tuple[str, ...]
    provider: str
    kind: ProviderKind
    usage: Usage = field(default_factory=Usage)
    latency_ms: float = 0.0
    cached: bool = False


class TranslateError(RuntimeError):
    """Base class for translation failures."""


class ProviderUnavailable(TranslateError):
    """Raised when a provider is not configured or cannot run."""


class ProviderNetworkError(TranslateError):
    """Raised on recoverable network failures (timeout/5xx)."""


class ProviderAuthError(TranslateError):
    """Raised on 401/403."""


class RateLimitError(TranslateError):
    """Raised on 429-equivalent responses."""


class UnsupportedPairError(TranslateError):
    """Raised when a local provider cannot handle a language pair."""


@runtime_checkable
class TranslateProvider(Protocol):
    """Unified provider interface. Implementations must be stateless per call."""

    name: str
    kind: ProviderKind

    def is_available(self) -> bool:
        """Fast, side-effect-free check that the provider can be invoked."""

    def translate(self, req: TranslationRequest) -> TranslationResult:
        """Synchronous translation."""

    async def atranslate(self, req: TranslationRequest) -> TranslationResult:
        """Async translation; may be a thin wrapper around ``translate``."""

    async def astream(self, req: TranslationRequest) -> AsyncIterator[str]:
        """Stream partial output. Implementations without streaming may yield the full result once."""

    def close(self) -> None:
        """Release any held resources."""


def merge_glossary(
    base: Iterable[tuple[str, str]],
    extra: Sequence[tuple[str, str]],
) -> tuple[tuple[str, str], ...]:
    """Merge two glossaries, with ``extra`` winning on conflicts."""
    out: dict[str, str] = {src: tgt for src, tgt in base}
    for src, tgt in extra:
        out[src] = tgt
    return tuple(out.items())


__all__ = [
    "ProviderKind",
    "TranslationRequest",
    "TranslationResult",
    "Usage",
    "TranslateProvider",
    "TranslateError",
    "ProviderUnavailable",
    "ProviderNetworkError",
    "ProviderAuthError",
    "RateLimitError",
    "UnsupportedPairError",
    "merge_glossary",
]
