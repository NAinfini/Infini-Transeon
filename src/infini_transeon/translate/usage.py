"""Token/cost accounting for translation requests.

Persists aggregates to SQLite via diskcache. For every request we bump both a
per-provider bucket and a rollup ``__all__`` bucket so the dashboard can
report totals in O(1) without scanning keys.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, date, datetime
from threading import Lock

import diskcache

from infini_transeon.config import paths
from infini_transeon.translate.base import Usage

_ROLLUP = "__all__"


@dataclass(frozen=True, slots=True)
class Totals:
    requests: int
    input_tokens: int
    output_tokens: int
    cost_usd: float


class UsageTracker:
    def __init__(self) -> None:
        paths.ensure_all()
        self._db = diskcache.Cache(
            directory=str(paths.cache_dir() / "usage"),
            eviction_policy="none",
        )
        self._lock = Lock()

    def record(self, provider: str, usage: Usage, *, now: datetime | None = None) -> None:
        when = now or datetime.now(UTC)
        with self._lock:
            # Per-provider buckets for future drill-down UI.
            self._bump(self._day_key(provider, when.date()), usage)
            self._bump(self._month_key(provider, when), usage)
            # Rollup buckets the dashboard reads directly.
            self._bump(self._day_key(_ROLLUP, when.date()), usage)
            self._bump(self._month_key(_ROLLUP, when), usage)

    def today(self, provider: str | None = None) -> Totals:
        key = self._day_key(provider or _ROLLUP, datetime.now(UTC).date())
        return self._read(key)

    def this_month(self, provider: str | None = None) -> Totals:
        key = self._month_key(provider or _ROLLUP, datetime.now(UTC))
        return self._read(key)

    def close(self) -> None:
        self._db.close()

    # --- internal -----------------------------------------------------

    @staticmethod
    def _day_key(provider: str, d: date) -> str:
        return f"day|{d.isoformat()}|{provider}"

    @staticmethod
    def _month_key(provider: str, dt: datetime) -> str:
        return f"month|{dt.year:04d}-{dt.month:02d}|{provider}"

    def _bump(self, key: str, usage: Usage) -> None:
        current = self._db.get(key) or {
            "requests": 0,
            "input_tokens": 0,
            "output_tokens": 0,
            "cost_usd": 0.0,
        }
        current["requests"] += 1
        current["input_tokens"] += int(usage.input_tokens)
        current["output_tokens"] += int(usage.output_tokens)
        current["cost_usd"] += float(usage.cost_usd)
        self._db.set(key, current)

    def _read(self, key: str) -> Totals:
        data = self._db.get(key) or {}
        return Totals(
            requests=int(data.get("requests", 0)),
            input_tokens=int(data.get("input_tokens", 0)),
            output_tokens=int(data.get("output_tokens", 0)),
            cost_usd=float(data.get("cost_usd", 0.0)),
        )


__all__ = ["Totals", "UsageTracker"]
