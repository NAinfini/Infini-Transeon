"""OpenAI-compatible Chat Completions provider.

Covers OpenAI, OpenRouter, DeepSeek, Groq, xAI, Together, Fireworks, Ollama,
LM Studio, llama.cpp server, vLLM, and any other service that implements
``POST /v1/chat/completions``.
"""

from __future__ import annotations

import time
from collections.abc import AsyncIterator

import httpx
from openai import (
    APIConnectionError,
    APIStatusError,
    APITimeoutError,
    AsyncOpenAI,
    AuthenticationError,
    OpenAI,
    RateLimitError as OpenAIRateLimit,
)
from tenacity import (
    retry,
    retry_if_exception_type,
    stop_after_attempt,
    wait_exponential,
)

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
from infini_transeon.utils.logging import logger


class OpenAICompatProvider(TranslateProvider):
    """Synchronous + async OpenAI-compatible client."""

    name = "openai_compat"
    kind = ProviderKind.online

    def __init__(self, config: ProviderConfig) -> None:
        if config.protocol != "openai_compat":
            raise ValueError(f"wrong protocol for adapter: {config.protocol}")
        if not config.base_url:
            raise ProviderUnavailable("OpenAI-compatible provider requires base_url")
        if not config.model:
            raise ProviderUnavailable("OpenAI-compatible provider requires model")
        self._config = config
        api_key = get_secret(config.api_key_ref) or "no-key-needed"
        self._client = OpenAI(
            api_key=api_key,
            base_url=config.base_url,
            timeout=config.timeout_seconds,
            default_headers=dict(config.extra_headers) or None,
        )
        self._aclient = AsyncOpenAI(
            api_key=api_key,
            base_url=config.base_url,
            timeout=config.timeout_seconds,
            default_headers=dict(config.extra_headers) or None,
        )

    def is_available(self) -> bool:
        return bool(self._config.base_url and self._config.model)

    # --- sync -----------------------------------------------------------

    @retry(
        retry=retry_if_exception_type((ProviderNetworkError,)),
        stop=stop_after_attempt(3),
        wait=wait_exponential(multiplier=0.5, min=0.5, max=4),
        reraise=True,
    )
    def translate(self, req: TranslationRequest) -> TranslationResult:
        started = time.monotonic()
        messages = self._build_messages(req)
        try:
            response = self._client.chat.completions.create(
                model=self._require_model(),
                messages=messages,
                temperature=self._config.temperature,
                max_tokens=self._config.max_tokens,
                stream=False,
            )
        except AuthenticationError as exc:
            raise ProviderAuthError(str(exc)) from exc
        except OpenAIRateLimit as exc:
            raise RateLimitError(str(exc)) from exc
        except (APITimeoutError, APIConnectionError, httpx.HTTPError) as exc:
            raise ProviderNetworkError(str(exc)) from exc
        except APIStatusError as exc:
            if 500 <= exc.status_code < 600:
                raise ProviderNetworkError(str(exc)) from exc
            raise TranslateError(str(exc)) from exc

        choice = response.choices[0]
        content = (choice.message.content or "").strip()
        translations = parse_numbered_output(content, expected=len(req.texts))
        usage = Usage(
            input_tokens=int(getattr(response, "usage", None) and response.usage.prompt_tokens or 0),
            output_tokens=int(getattr(response, "usage", None) and response.usage.completion_tokens or 0),
        )
        return TranslationResult(
            translations=tuple(translations),
            provider=self._tag(),
            kind=self.kind,
            usage=usage,
            latency_ms=(time.monotonic() - started) * 1000.0,
        )

    async def atranslate(self, req: TranslationRequest) -> TranslationResult:
        started = time.monotonic()
        messages = self._build_messages(req)
        try:
            response = await self._aclient.chat.completions.create(
                model=self._require_model(),
                messages=messages,
                temperature=self._config.temperature,
                max_tokens=self._config.max_tokens,
                stream=False,
            )
        except AuthenticationError as exc:
            raise ProviderAuthError(str(exc)) from exc
        except OpenAIRateLimit as exc:
            raise RateLimitError(str(exc)) from exc
        except (APITimeoutError, APIConnectionError, httpx.HTTPError) as exc:
            raise ProviderNetworkError(str(exc)) from exc
        except APIStatusError as exc:
            if 500 <= exc.status_code < 600:
                raise ProviderNetworkError(str(exc)) from exc
            raise TranslateError(str(exc)) from exc

        content = (response.choices[0].message.content or "").strip()
        translations = parse_numbered_output(content, expected=len(req.texts))
        usage = Usage(
            input_tokens=int(getattr(response, "usage", None) and response.usage.prompt_tokens or 0),
            output_tokens=int(getattr(response, "usage", None) and response.usage.completion_tokens or 0),
        )
        return TranslationResult(
            translations=tuple(translations),
            provider=self._tag(),
            kind=self.kind,
            usage=usage,
            latency_ms=(time.monotonic() - started) * 1000.0,
        )

    async def astream(self, req: TranslationRequest) -> AsyncIterator[str]:
        messages = self._build_messages(req)
        try:
            stream = await self._aclient.chat.completions.create(
                model=self._require_model(),
                messages=messages,
                temperature=self._config.temperature,
                max_tokens=self._config.max_tokens,
                stream=True,
            )
            async for event in stream:
                delta = event.choices[0].delta.content or ""
                if delta:
                    yield delta
        except AuthenticationError as exc:
            raise ProviderAuthError(str(exc)) from exc
        except OpenAIRateLimit as exc:
            raise RateLimitError(str(exc)) from exc
        except (APITimeoutError, APIConnectionError, httpx.HTTPError) as exc:
            raise ProviderNetworkError(str(exc)) from exc

    def close(self) -> None:
        try:
            self._client.close()
        except Exception:  # noqa: BLE001 - closing is best-effort
            logger.debug("openai sync client close failed", exc_info=True)

    # --- helpers --------------------------------------------------------

    def _require_model(self) -> str:
        if not self._config.model:
            raise ProviderUnavailable("model not configured")
        return self._config.model

    def _tag(self) -> str:
        host = self._config.base_url or "unknown"
        return f"openai_compat::{host}::{self._config.model}"

    def _build_messages(self, req: TranslationRequest) -> list[dict[str, str]]:
        style = _coerce_style(req.style)
        system = build_system_prompt(
            source_lang=req.source_lang,
            target_lang=req.target_lang,
            style=style,
            glossary=req.glossary,
            history=req.history,
        )
        user = build_user_prompt(req.texts)
        return [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
        ]


def _coerce_style(style: str) -> StyleMode:
    try:
        return StyleMode(style)
    except ValueError:
        return StyleMode.general


__all__ = ["OpenAICompatProvider"]
