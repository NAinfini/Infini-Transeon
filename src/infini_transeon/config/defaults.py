"""Default AppConfig factory — thin wrapper for discoverability."""

from __future__ import annotations

from infini_transeon.config.schema import AppConfig


def default_config() -> AppConfig:
    return AppConfig()


__all__ = ["default_config"]
