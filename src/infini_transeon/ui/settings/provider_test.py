"""Background worker for provider connection tests (keeps the UI responsive)."""

from __future__ import annotations

from PySide6.QtCore import QObject, QThread, Signal

from infini_transeon.config.schema import ProviderConfig
from infini_transeon.translate.router import TestResult, test_provider


class _TestRunner(QObject):
    done = Signal(object)  # TestResult

    def __init__(self, cfg: ProviderConfig, target_lang: str) -> None:
        super().__init__()
        self._cfg = cfg
        self._target = target_lang

    def run(self) -> None:
        try:
            result = test_provider(self._cfg, target_lang=self._target)
        except Exception as exc:  # noqa: BLE001 - guard against SDK surprises
            result = TestResult(
                ok=False,
                latency_ms=0.0,
                sample_output="",
                provider_tag=self._cfg.protocol,
                usage=None,
                error=str(exc),
            )
        self.done.emit(result)


class ProviderTestJob(QObject):
    """Run a provider test on a dedicated QThread and emit the result."""

    finished = Signal(object)  # TestResult

    def __init__(self, cfg: ProviderConfig, target_lang: str = "zh") -> None:
        super().__init__()
        self._thread = QThread()
        self._runner = _TestRunner(cfg, target_lang)
        self._runner.moveToThread(self._thread)
        self._thread.started.connect(self._runner.run)
        self._runner.done.connect(self._on_done)
        self._runner.done.connect(self._thread.quit)
        self._thread.finished.connect(self._runner.deleteLater)
        self._thread.finished.connect(self._thread.deleteLater)

    def start(self) -> None:
        self._thread.start()

    def _on_done(self, result: TestResult) -> None:
        self.finished.emit(result)


__all__ = ["ProviderTestJob"]
