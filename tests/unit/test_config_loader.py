from __future__ import annotations

from infini_transeon.config import paths
from infini_transeon.config import loader as config_loader
from infini_transeon.config.schema import AppConfig


def test_load_returns_defaults_when_missing() -> None:
    cfg = config_loader.load()
    assert isinstance(cfg, AppConfig)


def test_round_trip(tmp_path) -> None:
    cfg = AppConfig()
    config_loader.save(cfg)
    restored = config_loader.load()
    assert restored == cfg


def test_invalid_yaml_is_backed_up(tmp_path) -> None:
    path = paths.config_file()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(": not yaml :", encoding="utf-8")
    cfg = config_loader.load()
    assert cfg == AppConfig()
    backups = list(path.parent.glob("config.yaml.broken.*"))
    assert backups
