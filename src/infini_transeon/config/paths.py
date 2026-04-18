"""Cross-platform user data / config / cache / logs paths.

All paths are created lazily; call :func:`ensure_all` at startup to materialize
them. The directory names intentionally match the app display name so they are
easy to find in platform file managers.
"""

from __future__ import annotations

from pathlib import Path

from platformdirs import PlatformDirs

APP_NAME = "Infini-Transeon"
APP_AUTHOR = "NAinfini"

_DIRS = PlatformDirs(appname=APP_NAME, appauthor=APP_AUTHOR, roaming=True)


def config_dir() -> Path:
    return Path(_DIRS.user_config_dir)


def data_dir() -> Path:
    return Path(_DIRS.user_data_dir)


def cache_dir() -> Path:
    return Path(_DIRS.user_cache_dir)


def log_dir() -> Path:
    return Path(_DIRS.user_log_dir)


def models_dir() -> Path:
    return data_dir() / "models"


def tm_dir() -> Path:
    return data_dir() / "tm"


def config_file() -> Path:
    return config_dir() / "config.yaml"


def ensure_all() -> None:
    for p in (config_dir(), data_dir(), cache_dir(), log_dir(), models_dir(), tm_dir()):
        p.mkdir(parents=True, exist_ok=True)


__all__ = [
    "APP_NAME",
    "APP_AUTHOR",
    "config_dir",
    "data_dir",
    "cache_dir",
    "log_dir",
    "models_dir",
    "tm_dir",
    "config_file",
    "ensure_all",
]
