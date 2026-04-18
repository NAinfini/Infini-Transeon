"""Load and save :class:`AppConfig` to YAML on disk.

The loader is tolerant to missing fields (fills defaults) and strict on unknown
fields (surfaces user typos). Corrupt files are backed up with a timestamp
suffix and replaced with defaults so the app always has a usable config.

Between app versions we sometimes rename or remove config keys (e.g. the
``translation.local.alma`` block disappeared when ALMA was replaced with
MADLAD). The migration step below drops *known-dead* keys before validation
so a user's hotkeys, colours, and API key refs survive the upgrade; truly
unexpected content still trips Pydantic's ``extra_forbidden`` and the whole
file is backed up.
"""

from __future__ import annotations

import shutil
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import yaml
from pydantic import ValidationError

from infini_transeon.config import paths
from infini_transeon.config.schema import AppConfig
from infini_transeon.utils.logging import logger


# (dotted-path, reason) pairs dropped before validation. Each entry must be a
# tuple of keys pointing at a nested dict key; matching keys are removed with
# a warning so legacy configs upgrade cleanly instead of being factory-reset.
_LEGACY_KEYS: tuple[tuple[tuple[str, ...], str], ...] = (
    (("translation", "local", "alma"), "ALMA was replaced with MADLAD in a prior release"),
    (("translation", "local", "opus_mt"), "Opus-MT was removed in favour of MADLAD-400"),
    (("translation", "local", "madlad", "enabled"), "MADLAD is now the sole local engine — no toggle"),
    (("ocr", "max_long_axis"), "OCR clamp is now internal to RapidOCR's Det.limit_side_len"),
    (("ocr", "max_long_axis_high_dpr"), "OCR clamp is now internal to RapidOCR's Det.limit_side_len"),
)


def _migrate(raw: dict[str, Any]) -> dict[str, Any]:
    """Drop known-dead keys in-place and log what we removed."""
    # Engine value: if the old config says opus_mt, flip to madlad before
    # pydantic rejects the now-invalid enum value.
    try:
        local = raw.get("translation", {}).get("local", {})
        if isinstance(local, dict) and local.get("engine") == "opus_mt":
            local["engine"] = "madlad"
            logger.info("config migration: engine opus_mt -> madlad")
    except AttributeError:
        pass
    # MADLAD compute_type: earlier revisions saved "int8" (slower than the
    # GPU's native int8_float16 path) and later "float16" (a bad default —
    # the pre-converted CT2 repos are int8-quantized, so requesting pure
    # fp16 just dequantizes int8 -> fp16 at load, doubling VRAM for zero
    # quality gain). Coerce both back to "auto" so runtime picks the right
    # precision per device. User-chosen "int8_float16"/"float32" are kept.
    try:
        madlad = raw.get("translation", {}).get("local", {}).get("madlad", {})
        if isinstance(madlad, dict):
            stale = madlad.get("compute_type")
            if stale in ("int8", "float16"):
                madlad["compute_type"] = "auto"
                logger.info("config migration: madlad.compute_type {} -> auto", stale)
    except AttributeError:
        pass
    # MADLAD beam_size: earlier revisions saved 1 (single-word loops) then
    # 4 (coherent but below the paper's recommended quality setting). Bump
    # any stale value below 5 up to the new default — beam=5 is the paper's
    # recommended quality target and the repetition guards handle the rest.
    try:
        madlad = raw.get("translation", {}).get("local", {}).get("madlad", {})
        if isinstance(madlad, dict):
            beam = madlad.get("beam_size")
            if isinstance(beam, int) and beam < 5:
                madlad["beam_size"] = 5
                logger.info("config migration: madlad.beam_size {} -> 5", beam)
    except AttributeError:
        pass
    # MADLAD max_decoding_length: earlier revisions saved 256 (too long, wasted
    # decode on short inputs) then 96 (truncated long game-dialog lines). 192
    # is the balance. Coerce stale values outside [160, 224].
    try:
        madlad = raw.get("translation", {}).get("local", {}).get("madlad", {})
        if isinstance(madlad, dict):
            mdl = madlad.get("max_decoding_length")
            if isinstance(mdl, int) and (mdl < 160 or mdl > 224):
                madlad["max_decoding_length"] = 192
                logger.info("config migration: madlad.max_decoding_length {} -> 192", mdl)
    except AttributeError:
        pass
    for path, reason in _LEGACY_KEYS:
        cursor: Any = raw
        for key in path[:-1]:
            if not isinstance(cursor, dict) or key not in cursor:
                cursor = None
                break
            cursor = cursor[key]
        if isinstance(cursor, dict) and path[-1] in cursor:
            cursor.pop(path[-1], None)
            logger.info(
                "config migration: dropped {} ({})", ".".join(path), reason
            )
    return raw


def load(path: Path | None = None) -> AppConfig:
    """Load the app config from YAML, returning defaults if missing/invalid."""
    path = path or paths.config_file()
    if not path.exists():
        logger.info("no config found at {}, using defaults", path)
        return AppConfig()

    try:
        raw = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    except (OSError, yaml.YAMLError) as exc:
        logger.error("failed to read config {}: {}", path, exc)
        _backup(path)
        return AppConfig()

    if not isinstance(raw, dict):
        logger.error("config file is not a mapping: {}", path)
        _backup(path)
        return AppConfig()

    raw = _migrate(raw)

    try:
        config = AppConfig.model_validate(raw)
    except ValidationError as exc:
        logger.error("config validation failed:\n{}", exc)
        _backup(path)
        return AppConfig()

    # Persist the migrated form so the user doesn't see the same warning on
    # every launch. Safe: model_dump round-trips cleanly and save() writes
    # atomically via a .tmp rename.
    try:
        save(config, path)
    except OSError as exc:
        logger.warning("could not persist migrated config: {}", exc)
    return config


def save(config: AppConfig, path: Path | None = None) -> None:
    """Persist the config to YAML atomically."""
    path = path or paths.config_file()
    paths.ensure_all()
    # Re-validate before dumping so any fields that hold raw strings where a
    # StrEnum is declared (can happen after ``model_copy`` with UI-supplied
    # values) are coerced back to proper enums. Without this, ``model_dump``
    # emits "Expected `enum` - serialized value may not be as expected"
    # warnings on every save.
    normalized = AppConfig.model_validate(config.model_dump())
    data = normalized.model_dump(mode="json")
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(
        yaml.safe_dump(data, sort_keys=False, allow_unicode=True),
        encoding="utf-8",
    )
    tmp.replace(path)
    logger.debug("config saved to {}", path)


def _backup(path: Path) -> None:
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    backup = path.with_suffix(f"{path.suffix}.broken.{stamp}")
    try:
        shutil.copy2(path, backup)
        logger.warning("backed up invalid config to {}", backup)
    except OSError as exc:
        logger.error("failed to back up config {}: {}", path, exc)


__all__ = ["load", "save"]
