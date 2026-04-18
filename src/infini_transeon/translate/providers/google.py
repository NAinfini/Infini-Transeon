"""Google Gemini provider via the ``google-genai`` SDK."""

from __future__ import annotations

import asyncio
import time
from collections.abc import AsyncIterator

from google import genai
from google.genai import types

from infini_transeon.config.schema import ProviderConfig, StyleMode
from infini_transeon.config.secrets import get_secret
from infini_transeon.translate.base import (
    ProviderAuthError,
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
from infini_transeon.translate.prompts import (
    build_system_prompt,
    build_user_prompt,
    parse_numbered_output,
)


class GoogleGenAIProvider(TranslateProvider):
    name = "google"
    kind = ProviderKind.online

    def __init__(self, config: ProviderConfig) -> None:
        if config.protocol != "google":
            raise ValueError(f"wrong protocol for adapter: {config.protocol}")
        if not config.model:
            raise ProviderUnavailable("Google provider requires model")
        api_key = get_secret(config.api_key_ref)
        if not api_key:
            raise ProviderUnavailable("Google provider requires api_key_ref")
        self._config = config
        self._client = genai.Client(api_key=api_key)

    def is_available(self) -> bool:
        return bool(self._config.model)

    def translate(self, req: TranslationRequest) -> TranslationResult:
        started = time.monotonic()
        prompt, system = self._build(req)
        try:
            response = self._client.models.generate_content(
                model=self._config.model,  # type: ignore[arg-type]
                contents=prompt,
                config=types.GenerateContentConfig(
                    system_instruction=system,
                    temperature=self._config.temperature,
                    max_output_tokens=self._config.max_tokens,
                ),
            )
        except Exception as exc:  # google-genai raises many concrete classes
            raise _map_error(exc)

        content = (getattr(response, "text", "") or "").strip()
        translations = parse_numbered_output(content, expected=len(req.texts))
        usage = _extract_usage(response)
        return TranslationResult(
            translations=tuple(translations),
            provider=f"google::{self._config.model}",
            kind=self.kind,
            usage=usage,
            latency_ms=(time.monotonic() - started) * 1000.0,
        )

    async def atranslate(self, req: TranslationRequest) -> TranslationResult:
        # google-genai is sync-only at the moment; offload to a thread.
        return await asyncio.to_thread(self.translate, req)

    async def astream(self, req: TranslationRequest) -> AsyncIterator[str]:
        prompt, system = self._build(req)
        try:
            iterator = await asyncio.to_thread(
                self._client.models.generate_content_stream,
                model=self._config.model,
                contents=prompt,
                config=types.GenerateContentConfig(
                    system_instruction=system,
                    temperature=self._config.temperature,
                    max_output_tokens=self._config.max_tokens,
                ),
            )
        except Exception as exc:
            raise _map_error(exc)

        for chunk in iterator:
            piece = getattr(chunk, "text", None)
            if piece:
                yield piece

    def close(self) -> None:  # SDK has no explicit close
        return None

    def _build(self, req: TranslationRequest) -> tuple[str, str]:
        style = _coerce_style(req.style)
        system = build_system_prompt(
            source_lang=req.source_lang,
            target_lang=req.target_lang,
            style=style,
            glossary=req.glossary,
            history=req.history,
        )
        return build_user_prompt(req.texts), system


def _map_error(exc: Exception) -> TranslateError:
    text = str(exc)
    lowered = text.lower()
    if "api key" in lowered or "unauthorized" in lowered or "permission" in lowered:
        return ProviderAuthError(text)
    if "rate" in lowered or "quota" in lowered or "429" in lowered:
        return RateLimitError(text)
    if "timeout" in lowered or "unavailable" in lowered or "connection" in lowered:
        return ProviderNetworkError(text)
    return TranslateError(text)


def _extract_usage(response: object) -> Usage:
    meta = getattr(response, "usage_metadata", None)
    if meta is None:
        return Usage()
    return Usage(
        input_tokens=int(getattr(meta, "prompt_token_count", 0) or 0),
        output_tokens=int(getattr(meta, "candidates_token_count", 0) or 0),
    )


def _coerce_style(style: str) -> StyleMode:
    try:
        return StyleMode(style)
    except ValueError:
        return StyleMode.general


__all__ = ["GoogleGenAIProvider"]
