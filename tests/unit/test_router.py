"""Router behavior tests using stub providers.

The core privacy guarantee — that LOCAL mode never falls back to online — is
verified by inspecting the providers the router constructs under each mode.
"""

from __future__ import annotations

from collections.abc import AsyncIterator

import pytest

from infini_transeon.config.schema import (
    AppConfig,
    LocalConfig,
    LocalEngine,
    OnlineConfig,
    ProviderConfig,
    TranslationConfig,
    TranslationMode,
)
from infini_transeon.translate.base import (
    ProviderKind,
    ProviderNetworkError,
    TranslateProvider,
    TranslationRequest,
    TranslationResult,
    Usage,
)
from infini_transeon.translate.router import Router


class _StubProvider(TranslateProvider):
    def __init__(
        self,
        name: str,
        kind: ProviderKind,
        *,
        responses: list[tuple[str, ...]] | None = None,
        raise_on_call: Exception | None = None,
    ) -> None:
        self.name = name
        self.kind = kind
        self._responses = list(responses or [])
        self._raise = raise_on_call
        self.calls = 0

    def is_available(self) -> bool:
        return True

    def translate(self, req: TranslationRequest) -> TranslationResult:
        self.calls += 1
        if self._raise is not None:
            raise self._raise
        translations = self._responses.pop(0) if self._responses else tuple(
            f"[{self.name}] {text}" for text in req.texts
        )
        return TranslationResult(
            translations=translations,
            provider=self.name,
            kind=self.kind,
            usage=Usage(),
            latency_ms=1.0,
        )

    async def atranslate(self, req: TranslationRequest) -> TranslationResult:
        return self.translate(req)

    async def astream(self, req: TranslationRequest) -> AsyncIterator[str]:
        yield "\n".join(self.translate(req).translations)

    def close(self) -> None:
        pass


@pytest.fixture
def sample_req() -> TranslationRequest:
    return TranslationRequest(
        texts=("hello",),
        source_lang="en",
        target_lang="zh",
    )


def _build_router(
    *,
    mode: TranslationMode,
    online: TranslateProvider | None,
    local: TranslateProvider | None,
    mymemory: TranslateProvider | None = None,
) -> Router:
    cfg = AppConfig(
        translation=TranslationConfig(
            mode=mode,
            source_lang="en",
            target_lang="zh",
            online=OnlineConfig(
                primary=ProviderConfig(
                    protocol="openai_compat",
                    base_url="https://example.test/v1",
                    model="stub-model",
                ),
                fallback_to_local=True,
                last_resort_mymemory=True,
            ),
            local=LocalConfig(engine=LocalEngine.madlad),
        )
    )
    router = Router(cfg)
    router._online = online  # type: ignore[attr-defined]
    router._local = local  # type: ignore[attr-defined]
    router._mymemory = mymemory  # type: ignore[attr-defined]
    return router


def test_online_success_does_not_touch_local(sample_req: TranslationRequest) -> None:
    online = _StubProvider("online", ProviderKind.online)
    local = _StubProvider("local", ProviderKind.local)
    router = _build_router(mode=TranslationMode.online, online=online, local=local)
    result = router.translate(sample_req)
    assert result.provider == "online"
    assert local.calls == 0


def test_online_failure_falls_back_to_local(sample_req: TranslationRequest) -> None:
    online = _StubProvider("online", ProviderKind.online, raise_on_call=ProviderNetworkError("boom"))
    local = _StubProvider("local", ProviderKind.local)
    router = _build_router(mode=TranslationMode.online, online=online, local=local)
    result = router.translate(sample_req)
    assert result.provider == "local"
    assert router.state.degraded is True


def test_once_degraded_stays_local_until_retry(sample_req: TranslationRequest) -> None:
    online = _StubProvider("online", ProviderKind.online, raise_on_call=ProviderNetworkError("boom"))
    local = _StubProvider("local", ProviderKind.local)
    router = _build_router(mode=TranslationMode.online, online=online, local=local)
    router.translate(sample_req)
    # Next request: online should not be tried again until retry_online() is called.
    online.calls = 0
    second = TranslationRequest(texts=("world",), source_lang="en", target_lang="zh")
    router.translate(second)
    assert online.calls == 0
    router.retry_online()
    assert router.state.degraded is False


def test_local_mode_ignores_online_even_if_configured(sample_req: TranslationRequest) -> None:
    online = _StubProvider("online", ProviderKind.online)
    local = _StubProvider("local", ProviderKind.local)
    router = _build_router(mode=TranslationMode.local, online=online, local=local)
    result = router.translate(sample_req)
    assert result.provider == "local"
    assert online.calls == 0


def test_local_mode_router_does_not_construct_online() -> None:
    cfg = AppConfig(
        translation=TranslationConfig(
            mode=TranslationMode.local,
            online=OnlineConfig(
                primary=ProviderConfig(
                    protocol="openai_compat",
                    base_url="https://example.test/v1",
                    model="stub-model",
                ),
            ),
            local=LocalConfig(engine=LocalEngine.madlad),
        )
    )
    router = Router(cfg)
    try:
        assert router._online is None  # type: ignore[attr-defined]
        assert router._mymemory is None  # type: ignore[attr-defined]
    finally:
        router.close()
