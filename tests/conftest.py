"""Shared pytest fixtures."""

from __future__ import annotations

from pathlib import Path

import pytest


@pytest.fixture(autouse=True)
def isolate_user_dirs(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    """Redirect platformdirs to a per-test tmp_path so tests never touch real user data."""
    import platformdirs

    class _FakeDirs:
        def __init__(self, base: Path) -> None:
            self._base = base

        @property
        def user_config_dir(self) -> str:
            return str(self._base / "config")

        @property
        def user_data_dir(self) -> str:
            return str(self._base / "data")

        @property
        def user_cache_dir(self) -> str:
            return str(self._base / "cache")

        @property
        def user_log_dir(self) -> str:
            return str(self._base / "logs")

    fake = _FakeDirs(tmp_path)

    # Replace the module-level PlatformDirs instance inside our paths module.
    from infini_transeon.config import paths as paths_module

    monkeypatch.setattr(paths_module, "_DIRS", fake, raising=True)
    paths_module.ensure_all()

    # Just in case anything else imports platformdirs.PlatformDirs directly.
    monkeypatch.setattr(platformdirs, "PlatformDirs", lambda *a, **kw: fake)

    return tmp_path
