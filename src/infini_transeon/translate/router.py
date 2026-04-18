"""Translation router enforcing strict mode semantics.

Rules (see docs/ARCHITECTURE.md §3 — DO NOT relax without approval):

* ``Mode = Online``: try the configured online provider. On failure, optionally
  fall back to the local provider, then to MyMemory. The UI reflects the
  degraded state.
* ``Mode = Local``: only the local provider is ever consulted. **No online
  adapter is constructed**; even if the network is reachable, we refuse to
  talk to it. This guarantees privacy/offline behaviour.
* Online -> Local is **one-way**. Once we have fallen back in a given pipeline
  run, we stay on local until the user manually retries online.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from threading import Lock

from infini_transeon.config.schema import (
    AppConfig,
    LocalEngine,
    ProviderConfig,
    TranslationMode,
)
from infini_transeon.translate.base import (
    ProviderKind,
    ProviderUnavailable,
    RateLimitError,
    TranslateError,
    TranslateProvider,
    TranslationRequest,
    TranslationResult,
    UnsupportedPairError,
)
from infini_transeon.translate.cache import CacheEntry, TranslationMemory, make_key
from infini_transeon.translate.providers.anthropic import AnthropicProvider
from infini_transeon.translate.providers.google import GoogleGenAIProvider
from infini_transeon.translate.providers.madlad import MadladProvider
from infini_transeon.translate.providers.mymemory import MyMemoryProvider
from infini_transeon.translate.providers.openai_compat import OpenAICompatProvider
from infini_transeon.translate.usage import UsageTracker
from infini_transeon.utils.logging import logger


class RouterError(TranslateError):
    """Raised when no provider in the allowed chain produced a result."""


def build_online_provider(cfg: ProviderConfig) -> TranslateProvider:
    """Instantiate an online provider adapter for the given config."""
    # Bail early when the provider is obviously unconfigured. Without this,
    # the default (empty) provider config still instantiates a network
    # client whose first call takes the full connect timeout (~30s) to fail,
    # blocking every OCR tick behind it.
    if cfg.protocol in ("openai_compat", "anthropic") and not cfg.base_url:
        raise ProviderUnavailable(
            f"{cfg.protocol} provider missing base_url — configure it in Settings"
        )
    match cfg.protocol:
        case "openai_compat":
            return OpenAICompatProvider(cfg)
        case "anthropic":
            return AnthropicProvider(cfg)
        case "google":
            return GoogleGenAIProvider(cfg)
        case "mymemory":
            return MyMemoryProvider()
        case _:  # pragma: no cover - schema prevents this
            raise ProviderUnavailable(f"unknown protocol: {cfg.protocol}")


def build_local_provider(app_config: AppConfig) -> TranslateProvider:
    """Instantiate the configured local provider (MADLAD)."""
    local = app_config.translation.local
    match local.engine:
        case LocalEngine.madlad:
            return MadladProvider(local.madlad)


@dataclass
class RouterState:
    """Runtime state the router exposes to the UI (via read-only getters)."""

    degraded: bool = False                # online fell back to local in this session
    last_error: str | None = None
    last_provider: str | None = None
    last_kind: ProviderKind | None = None
    locks: Lock = field(default_factory=Lock)


class Router:
    """Route translation requests according to the configured mode."""

    def __init__(
        self,
        app_config: AppConfig,
        *,
        tm: TranslationMemory | None = None,
        usage: UsageTracker | None = None,
    ) -> None:
        self._config = app_config
        self._tm = tm or TranslationMemory()
        self._usage = usage or UsageTracker()
        self._state = RouterState()
        self._online: TranslateProvider | None = None
        self._mymemory: TranslateProvider | None = None
        self._local: TranslateProvider | None = None
        self._build_providers()

    # --- lifecycle ------------------------------------------------------

    def reload(self, app_config: AppConfig) -> None:
        """Reinitialize providers after config changes.

        Only providers whose config actually changed are torn down. Rebuilding
        the MADLAD local provider would cost 15-25s of CUDA kernel compile
        on the next translate call, so an unrelated settings change (theme,
        hotkeys, overlay colours) must not trigger that. Online and MyMemory
        providers are cheap to rebuild so we still refresh them unconditionally.
        """
        prev = self._config
        self._config = app_config
        with self._state.locks:
            self._state.degraded = False
            self._state.last_error = None
            self._state.last_provider = None
            self._state.last_kind = None
        # Online + mymemory: always rebuild (cheap, catches credential changes).
        for provider in (self._online, self._mymemory):
            if provider is not None:
                try:
                    provider.close()
                except Exception:  # noqa: BLE001 - best-effort
                    logger.debug("provider close failed", exc_info=True)
        self._online = None
        self._mymemory = None
        # Local (MADLAD): only rebuild if the user actually changed variant/
        # device/compute_type/engine. Preserves the warmed-up CUDA translator
        # across unrelated settings saves.
        if self._local is None or _local_config_changed(prev, app_config):
            if self._local is not None:
                try:
                    self._local.close()
                except Exception:  # noqa: BLE001 - best-effort
                    logger.debug("local provider close failed", exc_info=True)
                self._local = None
        self._build_providers(preserve_local=self._local is not None)
        # If we preserved the local provider, ``_local_config_changed``
        # guaranteed variant/device/compute_type did not change. But
        # decode knobs (beam_size / max_batch_size / max_decoding_length)
        # may still have changed — push them into the running provider
        # so governor-driven reductions actually take effect without
        # paying a full rebuild.
        if self._local is not None:
            apply = getattr(self._local, "apply_runtime_config", None)
            if apply is not None:
                try:
                    apply(app_config.translation.local.madlad)
                except ValueError:
                    logger.exception(
                        "runtime config rejected by local provider — "
                        "this should have triggered a rebuild"
                    )

    def close(self) -> None:
        self._close_providers()
        self._tm.close()
        self._usage.close()

    def _close_providers(self) -> None:
        for provider in (self._online, self._mymemory, self._local):
            if provider is not None:
                try:
                    provider.close()
                except Exception:  # noqa: BLE001 - best-effort
                    logger.debug("provider close failed", exc_info=True)
        self._online = self._mymemory = self._local = None

    # --- public API -----------------------------------------------------

    @property
    def state(self) -> RouterState:
        return self._state

    @property
    def mode(self) -> TranslationMode:
        return self._config.translation.mode

    def retry_online(self) -> None:
        """Clear the degraded flag so the next request tries online first again."""
        with self._state.locks:
            self._state.degraded = False

    def translate(self, req: TranslationRequest) -> TranslationResult:
        """Route and execute a translation request.

        Per-text cache + in-batch de-duplication: any text already in the
        translation memory, or appearing multiple times in the same batch,
        is answered from cache/memoisation and never reaches the provider.
        On a dense UI this alone cuts the provider's effective batch size
        by 50-90% tick over tick, which is the single biggest determinant
        of MADLAD latency.
        """
        if not req.texts:
            return TranslationResult(
                translations=(), provider="noop", kind=ProviderKind.local, cached=True
            )

        # Step 1: per-text cache lookup. Texts that miss (or are blank) go
        # into the de-dup set for the provider.
        cached_by_text: dict[str, str] = {}
        cached_providers: set[str] = set()
        misses: dict[str, None] = {}  # insertion-ordered unique set
        for text in req.texts:
            if not text:
                continue
            if text in cached_by_text or text in misses:
                continue
            entry = self._tm.get(
                make_key(
                    text,
                    source_lang=req.source_lang,
                    target_lang=req.target_lang,
                    style=req.style,
                    glossary=req.glossary,
                )
            )
            if entry is not None:
                cached_by_text[text] = entry.text
                cached_providers.add(entry.provider)
            else:
                misses[text] = None

        # Step 2: if everything hit the cache, short-circuit without touching
        # any provider. Keeps the degraded flag / usage counters untouched.
        if not misses:
            outputs = tuple(cached_by_text.get(t, "") for t in req.texts)
            return TranslationResult(
                translations=outputs,
                provider=next(iter(cached_providers), "cache"),
                kind=self._state.last_kind or ProviderKind.online,
                cached=True,
                latency_ms=0.0,
            )

        # Step 3: route only the unique cache-miss texts.
        miss_texts = tuple(misses.keys())
        sub_req = TranslationRequest(
            texts=miss_texts,
            source_lang=req.source_lang,
            target_lang=req.target_lang,
            style=req.style,
            glossary=req.glossary,
            history=req.history,
            trace_id=req.trace_id,
        )
        mode = self._config.translation.mode
        if mode == TranslationMode.local:
            sub_result = self._run_local(sub_req)
        else:
            sub_result = self._run_online(sub_req)

        # Step 4: persist new translations into the cache keyed by source
        # text, then assemble the per-input-position output tuple.
        translated_by_text: dict[str, str] = {}
        for src, tgt in zip(miss_texts, sub_result.translations, strict=False):
            if not src or not tgt:
                continue
            translated_by_text[src] = tgt
            self._tm.set(
                make_key(
                    src,
                    source_lang=req.source_lang,
                    target_lang=req.target_lang,
                    style=req.style,
                    glossary=req.glossary,
                ),
                CacheEntry(text=tgt, provider=sub_result.provider),
            )

        outputs = tuple(
            cached_by_text.get(t, translated_by_text.get(t, "")) for t in req.texts
        )
        return TranslationResult(
            translations=outputs,
            provider=sub_result.provider,
            kind=sub_result.kind,
            usage=sub_result.usage,
            latency_ms=sub_result.latency_ms,
            cached=False,
        )

    # --- internal -------------------------------------------------------

    def _build_providers(self, *, preserve_local: bool = False) -> None:
        mode = self._config.translation.mode
        online_cfg = self._config.translation.online

        self._online = None
        self._mymemory = None
        # Keep the existing (warmed-up) local provider if the caller has
        # verified its config didn't change. Otherwise reset and rebuild.
        if not preserve_local:
            self._local = None

        if mode == TranslationMode.online:
            try:
                self._online = build_online_provider(online_cfg.primary)
            except ProviderUnavailable as exc:
                logger.warning("online provider unavailable: {}", exc)
                self._online = None
            if online_cfg.last_resort_mymemory:
                self._mymemory = MyMemoryProvider()
            if online_cfg.fallback_to_local and self._local is None:
                self._local = self._try_build_local()
        else:
            # LOCAL MODE: never construct online adapters.
            if self._local is None:
                self._local = self._try_build_local()

    def _try_build_local(self) -> TranslateProvider | None:
        try:
            return build_local_provider(self._config)
        except ProviderUnavailable as exc:
            logger.info("local provider unavailable: {}", exc)
            return None

    def _run_online(self, req: TranslationRequest) -> TranslationResult:
        assert self.mode == TranslationMode.online
        # If a previous request degraded to local, keep going local until
        # retry_online() is called by the user.
        start_with_online = not self._state.degraded and self._online is not None
        errors: list[str] = []

        # Surface *why* we're not starting with online — the most common
        # confusion is "I set mode=online but it still runs locally". Log
        # once per translate at debug so the user can cross-check.
        if not start_with_online:
            reason = (
                "no online provider built (check base_url/model in Settings)"
                if self._online is None
                else "router state is degraded; press Retry Online"
            )
            logger.debug("online skipped: {}", reason)

        if start_with_online and self._online is not None:
            try:
                result = self._online.translate(req)
                self._record(result)
                return result
            except (ProviderUnavailable, RateLimitError, TranslateError) as exc:
                errors.append(f"online:{exc!s}")
                logger.warning("online translate failed: {}", exc)
                with self._state.locks:
                    self._state.degraded = True

        if self._local is not None:
            try:
                result = self._local.translate(req)
                self._record(result)
                return result
            except (ProviderUnavailable, UnsupportedPairError, TranslateError) as exc:
                errors.append(f"local:{exc!s}")
                logger.warning("local fallback failed: {}", exc)

        if self._mymemory is not None:
            try:
                result = self._mymemory.translate(req)
                self._record(result)
                return result
            except TranslateError as exc:
                errors.append(f"mymemory:{exc!s}")
                logger.warning("mymemory fallback failed: {}", exc)

        raise RouterError("; ".join(errors) or "no provider available")

    def _run_local(self, req: TranslationRequest) -> TranslationResult:
        assert self.mode == TranslationMode.local
        if self._local is None:
            raise RouterError("local provider is not configured or failed to load")
        try:
            result = self._local.translate(req)
        except (ProviderUnavailable, UnsupportedPairError, TranslateError) as exc:
            # Hard-fail. Never, under any circumstance, silently leak to network.
            with self._state.locks:
                self._state.last_error = str(exc)
            raise RouterError(f"local:{exc!s}") from exc
        self._record(result)
        return result

    def _lookup_cache(self, req: TranslationRequest) -> TranslationResult | None:
        """Deprecated: kept for back-compat with any external callers.

        The active code path is :meth:`translate` which does per-text cache
        + de-dup. This helper still works for an all-or-nothing lookup.
        """
        outputs: list[str] = []
        providers: set[str] = set()
        for text in req.texts:
            entry = self._tm.get(
                make_key(
                    text,
                    source_lang=req.source_lang,
                    target_lang=req.target_lang,
                    style=req.style,
                    glossary=req.glossary,
                )
            )
            if entry is None:
                return None
            outputs.append(entry.text)
            providers.add(entry.provider)
        return TranslationResult(
            translations=tuple(outputs),
            provider=next(iter(providers), "cache"),
            kind=self._state.last_kind or ProviderKind.online,
            cached=True,
            latency_ms=0.0,
        )

    def _remember(self, req: TranslationRequest, result: TranslationResult) -> None:
        if result.cached:
            return
        for src, tgt in zip(req.texts, result.translations, strict=False):
            if not src or not tgt:
                continue
            self._tm.set(
                make_key(
                    src,
                    source_lang=req.source_lang,
                    target_lang=req.target_lang,
                    style=req.style,
                    glossary=req.glossary,
                ),
                CacheEntry(text=tgt, provider=result.provider),
            )

    def _record(self, result: TranslationResult) -> None:
        with self._state.locks:
            self._state.last_provider = result.provider
            self._state.last_kind = result.kind
            self._state.last_error = None
        self._usage.record(result.provider, result.usage, now=_now())


def _now():
    from datetime import UTC, datetime

    return datetime.now(UTC)


# --- connection testing (used by Settings -> Test button) ------------------


@dataclass(frozen=True, slots=True)
class TestResult:
    ok: bool
    latency_ms: float
    sample_output: str
    provider_tag: str
    usage: str | None
    error: str | None = None


def test_provider(cfg: ProviderConfig, *, target_lang: str = "zh") -> TestResult:
    """Issue a minimal translation to validate a provider configuration."""
    try:
        provider = build_online_provider(cfg)
    except ProviderUnavailable as exc:
        return TestResult(
            ok=False,
            latency_ms=0.0,
            sample_output="",
            provider_tag=cfg.protocol,
            usage=None,
            error=str(exc),
        )
    started = time.monotonic()
    try:
        result = provider.translate(
            TranslationRequest(
                texts=("hello",),
                source_lang="en",
                target_lang=target_lang,
                style="general",
            )
        )
    except TranslateError as exc:
        return TestResult(
            ok=False,
            latency_ms=(time.monotonic() - started) * 1000.0,
            sample_output="",
            provider_tag=cfg.protocol,
            usage=None,
            error=str(exc),
        )
    finally:
        provider.close()
    usage_txt = (
        f"in={result.usage.input_tokens}, out={result.usage.output_tokens}"
        if result.usage.input_tokens or result.usage.output_tokens
        else None
    )
    sample = result.translations[0] if result.translations else ""
    return TestResult(
        ok=bool(sample),
        latency_ms=result.latency_ms,
        sample_output=sample,
        provider_tag=result.provider,
        usage=usage_txt,
    )


def _local_config_changed(prev: AppConfig, curr: AppConfig) -> bool:
    """True when any field that would require rebuilding the local provider changed.

    Covers engine, MADLAD variant, device, compute_type. Source/target language
    and style do NOT require a rebuild — they're per-request parameters.
    """
    p = prev.translation.local
    c = curr.translation.local
    if p.engine != c.engine:
        return True
    pm, cm = p.madlad, c.madlad
    return (
        pm.variant != cm.variant
        or pm.device != cm.device
        or pm.compute_type != cm.compute_type
    )


__all__ = [
    "Router",
    "RouterError",
    "RouterState",
    "TestResult",
    "build_local_provider",
    "build_online_provider",
    "test_provider",
]
