"""Auto-update client built on tufup.

Release flow (implemented in ``.github/workflows/release.yml``):

1. Tag ``vX.Y.Z`` pushed → CI builds platform bundles with PyInstaller.
2. ``tufup targets add-bundle`` packages the new bundle as a target.
3. Signed metadata + targets are uploaded as GitHub Release assets.
4. This client points ``tufup.Client.metadata_base_url`` at the release asset
   URL pattern and checks for newer versions on startup + on demand.

The first release does not ship signed metadata yet; the client degrades
gracefully (logs a warning, reports "no updates found") rather than crashing.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from infini_transeon import __version__
from infini_transeon.config import paths
from infini_transeon.utils.logging import logger

try:
    from tufup.client import Client  # type: ignore[import-not-found]
except ImportError:  # pragma: no cover - tufup is a required dep
    Client = None  # type: ignore[assignment]


APP_NAME = "Infini-Transeon"
UPDATE_BASE_URL = (
    "https://github.com/NAinfini/Infini-Transeon/releases/download/updates"
)
METADATA_DIR = "metadata"
TARGETS_DIR = "targets"


@dataclass(frozen=True, slots=True)
class UpdateCheckResult:
    available: bool
    current_version: str
    latest_version: str | None
    error: str | None = None


class UpdateClient:
    """Thin tufup wrapper with user-friendly failure modes."""

    def __init__(self) -> None:
        paths.ensure_all()
        self._metadata_dir = paths.data_dir() / METADATA_DIR
        self._targets_dir = paths.data_dir() / TARGETS_DIR
        self._metadata_dir.mkdir(parents=True, exist_ok=True)
        self._targets_dir.mkdir(parents=True, exist_ok=True)

    def check(self) -> UpdateCheckResult:
        if Client is None:
            return UpdateCheckResult(
                available=False,
                current_version=__version__,
                latest_version=None,
                error="tufup is not installed",
            )
        try:
            client = self._build_client()
            new_target = client.check_for_updates()
        except FileNotFoundError as exc:
            # No signed root.json yet — this is expected before the first signed
            # release ships; surface as info, not a warning.
            logger.info("update check skipped (no trust root): {}", exc)
            return UpdateCheckResult(
                available=False,
                current_version=__version__,
                latest_version=None,
                error="No signed update metadata published yet.",
            )
        except Exception as exc:  # noqa: BLE001 - tufup surfaces many types
            logger.warning("update check failed: {}", exc)
            return UpdateCheckResult(
                available=False,
                current_version=__version__,
                latest_version=None,
                error=str(exc),
            )
        if new_target is None:
            return UpdateCheckResult(
                available=False,
                current_version=__version__,
                latest_version=None,
            )
        return UpdateCheckResult(
            available=True,
            current_version=__version__,
            latest_version=str(new_target.version),
        )

    def apply(self) -> UpdateCheckResult:
        if Client is None:
            return UpdateCheckResult(
                available=False,
                current_version=__version__,
                latest_version=None,
                error="tufup is not installed",
            )
        try:
            client = self._build_client()
            new_target = client.check_for_updates()
            if new_target is None:
                return UpdateCheckResult(
                    available=False,
                    current_version=__version__,
                    latest_version=None,
                )
            client.download_and_apply_update(new_target)
            return UpdateCheckResult(
                available=True,
                current_version=__version__,
                latest_version=str(new_target.version),
            )
        except Exception as exc:  # noqa: BLE001
            logger.warning("apply update failed: {}", exc)
            return UpdateCheckResult(
                available=False,
                current_version=__version__,
                latest_version=None,
                error=str(exc),
            )

    def _build_client(self) -> Any:
        assert Client is not None
        return Client(
            app_name=APP_NAME,
            app_install_dir=Path(__file__).resolve().parents[3],
            current_version=__version__,
            metadata_dir=self._metadata_dir,
            metadata_base_url=f"{UPDATE_BASE_URL}/{METADATA_DIR}/",
            target_dir=self._targets_dir,
            target_base_url=f"{UPDATE_BASE_URL}/{TARGETS_DIR}/",
            refresh_required=False,
        )


__all__ = ["UpdateClient", "UpdateCheckResult"]
