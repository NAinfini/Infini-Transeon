"""Anthropic Claude (Messages API) provider."""

from __future__ import annotations

import time
from collections.abc import AsyncIterator

import httpx
from anthropic import (
    APIConnectionError,
    APIStatusError,
    APITimeoutError,
    AsyncAnthropic,
    Anthropic,
    AuthenticationError,
    RateLimitError as AnthropicRateLimit,
)
from tenacity import retry, retry_if_exception_type, stop_after_attempt, wait_exponential

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

_DEFAULT_BASE_URL = "https://api.anthropic.com"


class AnthropicProvider(TranslateProvider):
    name = "anthropic"
    kind = ProviderKind.online

    def __init__(self, config: ProviderConfig) -> None:
        if config.protocol != "anthropic":
            raise ValueError(f"wrong protocol for adapter: {config.protocol}")
        if not config.model:
            raise ProviderUnavailable("Anthropic provider requires model")
        api_key = get_secret(config.api_key_ref)
        if not api_key:
            raise ProviderUnavailable("Anthropic provider requires api_key_ref")
        base_url = config.base_url or _DEFAULT_BASE_URL
        self._config = config
        self._client = Anthropic(
            api_key=api_key,
            base_url=base_url,
            timeout=config.timeout_seconds,
            default_headers=dict(config.extra_headers) or None,
        )
        self._aclient = AsyncAnthropic(
            api_key=api_key,
            base_url=base_url,
            timeout=config.timeout_seconds,
            default_headers=dict(config.extra_headers) or None,
        )

    def is_available(self) -> bool:
        return bool(self._config.model)

    @retry(
        retry=retry_if_exception_type((ProviderNetworkError,)),
        stop=stop_after_attempt(3),
        wait=wait_exponential(multiplier=0.5, min=0.5, max=4),
        reraise=True,
    )
    def translate(self, req: TranslationRequest) -> TranslationResult:
        started = time.monotonic()
        system, user = self._build(req)
        try:
            response = self._client.messages.create(
                model=self._config.model,  # type: ignore[arg-type]
                system=system,
                messages=[{"role": "user", "content": user}],
                max_tokens=self._config.max_tokens,
                temperature=self._config.temperature,
            )
        except AuthenticationError as exc:
            raise ProviderAuthError(str(exc)) from exc
        except AnthropicRateLimit as exc:
            raise RateLimitError(str(exc)) from exc
        except (APITimeoutError, APIConnectionError, httpx.HTTPError) as exc:
            raise ProviderNetworkError(str(exc)) from exc
        except APIStatusError as exc:
            if 500 <= exc.status_code < 600:
                raise ProviderNetworkError(str(exc)) from exc
            raise TranslateError(str(exc)) from exc

        content = "".join(
            block.text for block in response.content if getattr(block, "type", "") == "text"
        ).strip()
        translations = parse_numbered_output(content, expected=len(req.texts))
        usage = Usage(
            input_tokens=int(getattr(response.usage, "input_tokens", 0) or 0),
            output_tokens=int(getattr(response.usage, "output_tokens", 0) or 0),
        )
        return TranslationResult(
            translations=tuple(translations),
            provider=f"anthropic::{self._config.model}",
            kind=self.kind,
            usage=usage,
            latency_ms=(time.monotonic() - started) * 1000.0,
        )

    async def atranslate(self, req: TranslationRequest) -> TranslationResult:
        started = time.monotonic()
        system, user = self._build(req)
        try:
            response = await self._aclient.messages.create(
                model=self._config.model,  # type: ignore[arg-type]
                system=system,
                messages=[{"role": "user", "content": user}],
                max_tokens=self._config.max_tokens,
                temperature=self._config.temperature,
            )
        except AuthenticationError as exc:
            raise ProviderAuthError(str(exc)) from exc
        except AnthropicRateLimit as exc:
            raise RateLimitError(str(exc)) from exc
        except (APITimeoutError, APIConnectionError, httpx.HTTPError) as exc:
            raise ProviderNetworkError(str(exc)) from exc
        except APIStatusError as exc:
            if 500 <= exc.status_code < 600:
                raise ProviderNetworkError(str(exc)) from exc
            raise TranslateError(str(exc)) from exc

        content = "".join(
            block.text for block in response.content if getattr(block, "type", "") == "text"
        ).strip()
        translations = parse_numbered_output(content, expected=len(req.texts))
        usage = Usage(
            input_tokens=int(getattr(response.usage, "input_tokens", 0) or 0),
            output_tokens=int(getattr(response.usage, "output_tokens", 0) or 0),
        )
        return TranslationResult(
            translations=tuple(translations),
            provider=f"anthropic::{self._config.model}",
            kind=self.kind,
            usage=usage,
            latency_ms=(time.monotonic() - started) * 1000.0,
        )

    async def astream(self, req: TranslationRequest) -> AsyncIterator[str]:
        system, user = self._build(req)
        try:
            async with self._aclient.messages.stream(
                model=self._config.model,  # type: ignore[arg-type]
                system=system,
                messages=[{"role": "user", "content": user}],
                max_tokens=self._config.max_tokens,
                temperature=self._config.temperature,
            ) as stream:
                async for chunk in stream.text_stream:
                    if chunk:
                        yield chunk
        except AuthenticationError as exc:
            raise ProviderAuthError(str(exc)) from exc
        except AnthropicRateLimit as exc:
            raise RateLimitError(str(exc)) from exc
        except (APITimeoutError, APIConnectionError, httpx.HTTPError) as exc:
            raise ProviderNetworkError(str(exc)) from exc

    def close(self) -> None:
        try:
            self._client.close()
        except Exception:  # noqa: BLE001
            pass

    def _build(self, req: TranslationRequest) -> tuple[str, str]:
        style = _coerce_style(req.style)
        system = build_system_prompt(
            source_lang=req.source_lang,
            target_lang=req.target_lang,
            style=style,
            glossary=req.glossary,
            history=req.history,
        )
        return system, build_user_prompt(req.texts)


def _coerce_style(style: str) -> StyleMode:
    try:
        return StyleMode(style)
    except ValueError:
        return StyleMode.general


__all__ = ["AnthropicProvider"]
