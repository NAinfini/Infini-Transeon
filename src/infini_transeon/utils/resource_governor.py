"""Soft resource limiter for the running pipeline.

Samples the current process's CPU% / RSS and, if CUDA is in use, VRAM;
classifies the reading as ``normal`` / ``pressure`` / ``critical`` and
notifies a callback when the level changes. The app reacts by applying
progressively lighter pipeline settings (longer OCR interval, mobile
detector, smaller beam, smaller batch). Every reduction is recorded on
the ``ResourceDecision`` so the app can restore the original settings
when pressure clears.

The governor itself never kills the process — that's intentional. A
hard cap would take down the translator mid-tick with no path back, and
we prefer visible degradation to an invisible OOM. Thresholds are
conservative defaults tuned for a mid-range consumer PC with a 4-8 GB
GPU and 16-32 GB RAM; override via ``ResourceThresholds`` if needed.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from enum import StrEnum

import psutil

from infini_transeon.utils.logging import logger


class ResourceLevel(StrEnum):
    normal = "normal"
    pressure = "pressure"
    critical = "critical"


@dataclass(frozen=True, slots=True)
class ResourceThresholds:
    # Process CPU% can exceed 100 on multi-core boxes (psutil sums cores).
    # 150 = 1.5 cores sustained; 250 = 2.5 cores. Below pressure is normal.
    cpu_pct_pressure: float = 150.0
    cpu_pct_critical: float = 250.0
    # Process RSS in bytes. 4 GB covers everything short of MADLAD 7B;
    # 10 GB still fits a 7B user but catches runaway leaks.
    rss_bytes_pressure: int = 4 * 1024 * 1024 * 1024
    rss_bytes_critical: int = 10 * 1024 * 1024 * 1024
    # Fraction of total VRAM used, 0..1. Only evaluated when CUDA is
    # reachable via pynvml; otherwise VRAM is ignored entirely.
    vram_frac_pressure: float = 0.80
    vram_frac_critical: float = 0.92
    # Require this many consecutive samples at the new level before
    # switching — stops a single spike from thrashing settings.
    debounce_samples: int = 2


@dataclass(slots=True)
class ResourceSample:
    cpu_pct: float
    rss_bytes: int
    vram_frac: float | None


@dataclass(slots=True)
class ResourceDecision:
    """What the governor wants the app to do for ``level``.

    Values are *targets*, not deltas. ``None`` means "don't override the
    user's configured value for this field". The app restores the
    originals when level goes back to ``normal``. Detector tier is
    intentionally omitted: flipping server→mobile rebuilds the OCR ONNX
    session for 60-90s, which makes things worse under load.
    """

    level: ResourceLevel
    ocr_interval_multiplier: float = 1.0
    beam_size_cap: int | None = None
    max_batch_size_cap: int | None = None
    reason: str = ""


def decide(sample: ResourceSample, t: ResourceThresholds) -> tuple[ResourceLevel, str]:
    """Map a sample to a raw (un-debounced) level + short human reason."""
    crit_reasons: list[str] = []
    if sample.cpu_pct >= t.cpu_pct_critical:
        crit_reasons.append(f"CPU {sample.cpu_pct:.0f}%")
    if sample.rss_bytes >= t.rss_bytes_critical:
        crit_reasons.append(f"RAM {sample.rss_bytes / 1e9:.1f}GB")
    if sample.vram_frac is not None and sample.vram_frac >= t.vram_frac_critical:
        crit_reasons.append(f"VRAM {sample.vram_frac * 100:.0f}%")
    if crit_reasons:
        return ResourceLevel.critical, " + ".join(crit_reasons)

    press_reasons: list[str] = []
    if sample.cpu_pct >= t.cpu_pct_pressure:
        press_reasons.append(f"CPU {sample.cpu_pct:.0f}%")
    if sample.rss_bytes >= t.rss_bytes_pressure:
        press_reasons.append(f"RAM {sample.rss_bytes / 1e9:.1f}GB")
    if sample.vram_frac is not None and sample.vram_frac >= t.vram_frac_pressure:
        press_reasons.append(f"VRAM {sample.vram_frac * 100:.0f}%")
    if press_reasons:
        return ResourceLevel.pressure, " + ".join(press_reasons)

    return ResourceLevel.normal, ""


def decision_for(level: ResourceLevel, reason: str) -> ResourceDecision:
    """Targets to apply for a given level.

    ``pressure``: 1.5× OCR interval, beam -> 2, batch halved (cap 16).
    ``critical``: 2.5× OCR interval, beam 1, batch 1.
    ``normal``: identity — app restores user's configured values.
    """
    if level is ResourceLevel.pressure:
        return ResourceDecision(
            level=level,
            ocr_interval_multiplier=1.5,
            beam_size_cap=2,
            max_batch_size_cap=16,
            reason=reason,
        )
    if level is ResourceLevel.critical:
        return ResourceDecision(
            level=level,
            ocr_interval_multiplier=2.5,
            beam_size_cap=1,
            max_batch_size_cap=1,
            reason=reason,
        )
    return ResourceDecision(level=ResourceLevel.normal)


@dataclass(slots=True)
class _DebounceState:
    level: ResourceLevel = ResourceLevel.normal
    pending: ResourceLevel = ResourceLevel.normal
    pending_count: int = 0
    pending_reason: str = ""


class ResourceGovernor:
    """Samples process metrics and emits level-change decisions.

    Not a QObject — wiring to Qt (QTimer + signal) is the caller's job
    so this module stays unit-testable without a Qt event loop.
    """

    def __init__(
        self,
        on_decision: Callable[[ResourceDecision], None],
        *,
        thresholds: ResourceThresholds | None = None,
    ) -> None:
        self._on_decision = on_decision
        self._t = thresholds or ResourceThresholds()
        self._state = _DebounceState()
        self._proc = psutil.Process()
        # Prime psutil's CPU% so the first sample isn't 0.0 (first call
        # always returns 0 because it needs two points to compute delta).
        try:
            self._proc.cpu_percent(interval=None)
        except psutil.Error:
            pass
        self._pynvml_handle = _init_pynvml()

    def tick(self) -> ResourceDecision | None:
        """Take one sample; if the debounced level changed, fire the callback.

        Returns the fired decision, or None if the level didn't change
        this tick. Exceptions from ``psutil`` / ``pynvml`` are swallowed
        (logged at debug) so a transient metric failure doesn't stop
        the governor — we just skip this sample.
        """
        try:
            cpu = float(self._proc.cpu_percent(interval=None))
            rss = int(self._proc.memory_info().rss)
        except psutil.Error as exc:
            logger.debug("resource sample (psutil) failed: {}", exc)
            return None
        vram_frac = _read_vram_frac(self._pynvml_handle)

        sample = ResourceSample(cpu_pct=cpu, rss_bytes=rss, vram_frac=vram_frac)
        raw_level, reason = decide(sample, self._t)

        s = self._state
        if raw_level == s.pending:
            s.pending_count += 1
            s.pending_reason = reason or s.pending_reason
        else:
            s.pending = raw_level
            s.pending_count = 1
            s.pending_reason = reason

        if s.pending == s.level:
            return None
        if s.pending_count < self._t.debounce_samples:
            return None

        s.level = s.pending
        decision = decision_for(s.level, s.pending_reason)
        logger.info(
            "resource governor level -> {} ({})",
            decision.level.value,
            decision.reason or "ok",
        )
        self._on_decision(decision)
        return decision


def _init_pynvml() -> object | None:
    """Return an opaque handle usable by ``_read_vram_frac``, or None."""
    try:
        import pynvml  # type: ignore[import-not-found]
    except ImportError:
        return None
    try:
        pynvml.nvmlInit()
        handle = pynvml.nvmlDeviceGetHandleByIndex(0)
    except Exception as exc:  # noqa: BLE001
        logger.debug("pynvml init failed; VRAM tracking disabled: {}", exc)
        return None
    return (pynvml, handle)


def _read_vram_frac(state: object | None) -> float | None:
    if state is None:
        return None
    try:
        pynvml, handle = state  # type: ignore[misc]
        info = pynvml.nvmlDeviceGetMemoryInfo(handle)
        total = int(info.total)
        used = int(info.used)
        if total <= 0:
            return None
        return used / total
    except Exception as exc:  # noqa: BLE001
        logger.debug("VRAM probe failed: {}", exc)
        return None


__all__ = [
    "ResourceGovernor",
    "ResourceThresholds",
    "ResourceSample",
    "ResourceDecision",
    "ResourceLevel",
    "decide",
    "decision_for",
]
