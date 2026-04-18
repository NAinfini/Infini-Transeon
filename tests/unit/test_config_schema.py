from __future__ import annotations

from infini_transeon.config.schema import (
    AppConfig,
    ProviderConfig,
    TranslationMode,
)


def test_default_config_round_trip() -> None:
    cfg = AppConfig()
    dumped = cfg.model_dump(mode="json")
    restored = AppConfig.model_validate(dumped)
    assert restored == cfg


def test_mode_defaults_to_online() -> None:
    cfg = AppConfig()
    assert cfg.translation.mode == TranslationMode.online


def test_provider_strips_trailing_slash() -> None:
    cfg = ProviderConfig(base_url="https://api.openai.com/v1/")
    assert cfg.base_url == "https://api.openai.com/v1"


def test_provider_rejects_unknown_fields() -> None:
    import pydantic

    try:
        ProviderConfig(base_url="x", nonsense="y")  # type: ignore[call-arg]
    except pydantic.ValidationError:
        return
    raise AssertionError("ValidationError was expected")
