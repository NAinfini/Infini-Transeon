"""MADLAD-400 model download + inventory helpers.

Ships two variants:

* **3B** (default) — Apache-2, ~3 GB int8. Fits on low-end GPUs with 4 GB+
  VRAM. Covers 450+ languages at solid quality for high-resource pairs.
* **7B** (opt-in) — Apache-2, ~8 GB int8_float16. Noticeably better on
  technical / mixed text, but requires ~8 GB VRAM or will OOM. The UI
  refuses the toggle when the user's GPU can't hold it.

Both are pulled pre-converted from HuggingFace; no local PyTorch
conversion step, no runtime PyTorch dependency.
"""

from __future__ import annotations

import shutil
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from huggingface_hub import snapshot_download
from huggingface_hub.errors import (
    EntryNotFoundError,
    RepositoryNotFoundError,
)

from infini_transeon.config import paths
from infini_transeon.config.schema import MadladVariant
from infini_transeon.utils.logging import logger


class ModelNotAvailable(RuntimeError):
    """Raised when a model repo does not exist."""


class ModelDownloadError(RuntimeError):
    """Raised when a model download fails for a reason other than 'not found'."""


@dataclass(frozen=True, slots=True)
class DownloadedModel:
    repo_id: str
    local_path: Path


ProgressCb = Callable[[str, float], None]  # (message, ratio 0..1)
BytesProgressCb = Callable[[int, int, float], None]  # (done, total, ratio 0..1)


# Pre-converted CT2 repos. The 3B one is int8_float16 on GPU, int8 on CPU
# (CTranslate2 picks the right precision per device at load time).
_MADLAD_REPOS: dict[MadladVariant, str] = {
    MadladVariant.v3b: "SoybeanMilk/madlad400-3b-mt-ct2-int8_float16",
    MadladVariant.v7b: "avans06/madlad400-7b-mt-bt-ct2-int8_float16",
}

_MADLAD_DIR_NAMES: dict[MadladVariant, str] = {
    MadladVariant.v3b: "madlad-400-3b-mt-ct2-int8",
    MadladVariant.v7b: "madlad-400-7b-mt-ct2-int8",
}

# Approximate on-disk size for progress-UI expectations.
_MADLAD_EXPECTED_GB: dict[MadladVariant, float] = {
    MadladVariant.v3b: 3.0,
    MadladVariant.v7b: 8.0,
}


def madlad_repo(variant: MadladVariant) -> str:
    return _MADLAD_REPOS[variant]


def madlad_local_dir(variant: MadladVariant = MadladVariant.v3b) -> Path:
    return paths.models_dir() / _MADLAD_DIR_NAMES[variant]


def is_madlad_downloaded(variant: MadladVariant = MadladVariant.v3b) -> bool:
    directory = madlad_local_dir(variant)
    return (directory / "model.bin").exists() and (directory / "shared_vocabulary.json").exists()


def madlad_size_bytes(variant: MadladVariant = MadladVariant.v3b) -> int:
    directory = madlad_local_dir(variant)
    return _dir_size_bytes(directory) if directory.is_dir() else 0


def delete_madlad(variant: MadladVariant = MadladVariant.v3b) -> bool:
    directory = madlad_local_dir(variant)
    if not directory.is_dir():
        return False
    shutil.rmtree(directory, ignore_errors=True)
    logger.info("deleted MADLAD-400 {} model", variant.value)
    return True


def ensure_madlad(
    variant: MadladVariant = MadladVariant.v3b,
    progress: ProgressCb | None = None,
    *,
    bytes_progress: BytesProgressCb | None = None,
) -> Path:
    """Ensure the requested MADLAD variant's CT2 weights are on disk.

    Two progress channels. ``progress`` gets English status strings (used
    by CLI / logs). ``bytes_progress`` gets raw (done, total, ratio) tuples
    so the UI can format a localized label. Live progress is driven by a
    background disk-poll thread that computes (bytes on disk in target dir)
    / (expected total from HF metadata). This works across any
    huggingface_hub version and doesn't depend on their tqdm internals,
    which change shape between releases.
    """
    target = madlad_local_dir(variant)
    target.mkdir(parents=True, exist_ok=True)
    if is_madlad_downloaded(variant):
        return target

    repo_id = madlad_repo(variant)
    total_bytes = _madlad_expected_bytes(repo_id)
    expected_gb = _MADLAD_EXPECTED_GB[variant]
    stop_poll = threading.Event()
    poll_thread: threading.Thread | None = None

    if (progress or bytes_progress) and total_bytes > 0:
        def _poll() -> None:
            while not stop_poll.is_set():
                downloaded = _dir_size_bytes(target)
                ratio = min(0.99, downloaded / total_bytes) if total_bytes else 0.0
                if bytes_progress:
                    bytes_progress(downloaded, total_bytes, ratio)
                if progress:
                    gb_done = downloaded / (1024**3)
                    gb_total = total_bytes / (1024**3)
                    progress(
                        f"Downloading MADLAD-400 {variant.value.upper()} · "
                        f"{gb_done:.2f} / {gb_total:.2f} GB",
                        ratio,
                    )
                if stop_poll.wait(0.5):
                    break

        poll_thread = threading.Thread(target=_poll, name="MadladDownloadPoll", daemon=True)
        poll_thread.start()

    try:
        if progress:
            progress(f"Downloading MADLAD-400 {variant.value.upper()} (~{expected_gb:.0f} GB)", 0.0)
        snapshot_download(
            repo_id=repo_id,
            local_dir=str(target),
            local_dir_use_symlinks=False,
            allow_patterns=_MADLAD_DOWNLOAD_PATTERNS,
        )
        if bytes_progress and total_bytes:
            bytes_progress(total_bytes, total_bytes, 1.0)
        if progress:
            progress(f"MADLAD-400 {variant.value.upper()} ready", 1.0)
    except (RepositoryNotFoundError, EntryNotFoundError) as exc:
        raise ModelNotAvailable(str(exc)) from exc
    except Exception as exc:  # noqa: BLE001
        raise ModelDownloadError(str(exc)) from exc
    finally:
        stop_poll.set()
        if poll_thread is not None:
            poll_thread.join(timeout=1.0)
    if not is_madlad_downloaded(variant):
        raise ModelDownloadError(
            f"MADLAD-400 {variant.value} files missing after download"
        )
    return target


def _madlad_expected_bytes(repo_id: str) -> int:
    """Look up the total byte size of MADLAD's download whitelist on HF.

    Falls back to 0 on network failure; caller treats that as "progress
    unknown" and reports the static "Downloading..." message instead.
    """
    try:
        from huggingface_hub import HfApi
        api = HfApi()
        info = api.model_info(repo_id, files_metadata=True)
    except Exception:  # noqa: BLE001 — best-effort, progress just degrades
        return 0
    import fnmatch
    total = 0
    for sibling in info.siblings:
        if not sibling.size:
            continue
        name = sibling.rfilename
        if any(fnmatch.fnmatch(name, pat) for pat in _MADLAD_DOWNLOAD_PATTERNS):
            total += sibling.size
    return total


_MADLAD_DOWNLOAD_PATTERNS = [
    "model.bin",
    "config.json",
    "shared_vocabulary.*",
    "spiece.model",
    "tokenizer.json",
    "tokenizer_config.json",
    "special_tokens_map.json",
    "generation_config.json",
    "README.md",
]


def _dir_size_bytes(path: Path) -> int:
    total = 0
    for p in path.rglob("*"):
        try:
            if p.is_file():
                total += p.stat().st_size
        except OSError:
            continue
    return total


__all__ = [
    "DownloadedModel",
    "ModelDownloadError",
    "ModelNotAvailable",
    "ProgressCb",
    "BytesProgressCb",
    "delete_madlad",
    "ensure_madlad",
    "is_madlad_downloaded",
    "madlad_local_dir",
    "madlad_repo",
    "madlad_size_bytes",
]
