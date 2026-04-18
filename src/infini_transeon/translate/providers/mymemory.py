"""MyMemory public translation API (no key required).

Used strictly as the last-resort fallback in ONLINE mode; never touched in
LOCAL mode. The MyMemory endpoint has daily character limits and variable
quality — fine for short UI strings, not recommended as a primary provider.
"""

from __future__ import annotations

import asyncio
import time
from collections.abc import AsyncIterator

import httpx
from deep_translator import MyMemoryTranslator
from deep_translator.exceptions import (
    BaseError,
    NotValidPayload,
    RequestError,
    TooManyRequests,
    TranslationNotFound,
)

from infini_transeon.translate.base import (
    ProviderKind,
    ProviderNetworkError,
    ProviderUnavailable,
    RateLimitError,
    TranslateError,
    TranslateProvider,
    TranslationRequest,
    TranslationResult,
    Usage,
)


class MyMemoryProvider(TranslateProvider):
    name = "mymemory"
    kind = ProviderKind.online

    def is_available(self) -> bool:
        return True

    def translate(self, req: TranslationRequest) -> TranslationResult:
        started = time.monotonic()
        source = _normalize(req.source_lang)
        target = _normalize(req.target_lang)
        try:
            translator = MyMemoryTranslator(source=source, target=target)
            translations = [self._one(translator, text) for text in req.texts]
        except NotValidPayload as exc:
            raise ProviderUnavailable(str(exc)) from exc
        except TooManyRequests as exc:
            raise RateLimitError(str(exc)) from exc
        except (RequestError, httpx.HTTPError) as exc:
            raise ProviderNetworkError(str(exc)) from exc
        except BaseError as exc:
            raise TranslateError(str(exc)) from exc

        return TranslationResult(
            translations=tuple(translations),
            provider="mymemory",
            kind=self.kind,
            usage=Usage(),
            latency_ms=(time.monotonic() - started) * 1000.0,
        )

    async def atranslate(self, req: TranslationRequest) -> TranslationResult:
        return await asyncio.to_thread(self.translate, req)

    async def astream(self, req: TranslationRequest) -> AsyncIterator[str]:
        result = await self.atranslate(req)
        for t in result.translations:
            yield t + "\n"

    def close(self) -> None:
        return None

    @staticmethod
    def _one(translator: MyMemoryTranslator, text: str) -> str:
        if not text.strip():
            return text
        try:
            return translator.translate(text) or ""
        except TranslationNotFound:
            return text


def _normalize(code: str) -> str:
    """Map our ISO codes onto the variants MyMemory expects.

    MyMemory's language dictionary (see error listing) is keyed by regional
    variants for the top locales: en-US / en-GB, pt-BR / pt-PT, zh-CN /
    zh-TW. Stripping the region for those codes silently downgraded output
    to whichever default the server chose. We pass them through verbatim
    and only strip region for other locales where region variants aren't
    meaningful.
    """
    if code == "auto":
        return "auto"
    lowered = code.lower()
    if lowered.startswith("zh"):
        # MyMemory accepts zh-CN / zh-TW; default to simplified for "zh".
        return "zh-TW" if lowered == "zh-tw" else "zh-CN"
    if lowered in {"en-us", "en-gb", "pt-br", "pt-pt"}:
        parts = code.split("-", 1)
        return f"{parts[0].lower()}-{parts[1].upper()}"
    if lowered == "en":
        # Bare 'en' — pick en-US as a sensible default rather than letting
        # the server flip between GB/US spellings frame to frame.
        return "en-US"
    if lowered == "pt":
        return "pt-BR"
    return code.split("-", 1)[0].lower()


__all__ = ["MyMemoryProvider"]
