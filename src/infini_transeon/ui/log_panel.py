"""Log panel — shows recent log lines, click from the status bar to open."""

from __future__ import annotations

from PySide6.QtCore import QObject, Qt, Signal
from PySide6.QtGui import QFont
from PySide6.QtWidgets import (
    QDialog,
    QHBoxLayout,
    QLabel,
    QPlainTextEdit,
    QPushButton,
    QVBoxLayout,
)

from infini_transeon.utils.i18n import tr
from infini_transeon.utils.logging import log_buffer


class _AppendBridge(QObject):
    """Marshal log lines from any thread onto the Qt event loop."""

    line = Signal(str)


class LogPanel(QDialog):
    """Dialog that mirrors the in-memory log buffer, live-updating."""

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setWindowTitle(tr("log.title"))
        self.resize(720, 440)
        self._build_ui()

        # Live subscription via a signal so threads marshal onto the UI thread.
        self._bridge = _AppendBridge()
        self._bridge.line.connect(self._append_line)
        log_buffer.subscribe(self._bridge.line.emit)

        self._reload()

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(16, 16, 16, 16)
        root.setSpacing(10)

        header = QLabel(tr("log.subtitle"))
        header.setObjectName("muted")
        header.setWordWrap(True)
        root.addWidget(header)

        self._view = QPlainTextEdit()
        self._view.setReadOnly(True)
        self._view.setLineWrapMode(QPlainTextEdit.LineWrapMode.NoWrap)
        mono = QFont("Consolas")
        mono.setStyleHint(QFont.StyleHint.Monospace)
        mono.setPointSize(10)
        self._view.setFont(mono)
        root.addWidget(self._view)

        buttons = QHBoxLayout()
        buttons.addStretch(1)
        clear_btn = QPushButton(tr("log.btn.clear"))
        clear_btn.clicked.connect(self._clear)
        buttons.addWidget(clear_btn)
        close_btn = QPushButton(tr("common.close"))
        close_btn.setObjectName("primary")
        close_btn.clicked.connect(self.accept)
        buttons.addWidget(close_btn)
        root.addLayout(buttons)

    def _reload(self) -> None:
        lines = log_buffer.snapshot()
        self._view.setPlainText("\n".join(lines))
        self._scroll_to_end()

    def _append_line(self, line: str) -> None:
        self._view.appendPlainText(line)
        self._scroll_to_end()

    def _scroll_to_end(self) -> None:
        bar = self._view.verticalScrollBar()
        bar.setValue(bar.maximum())

    def _clear(self) -> None:
        log_buffer.clear()
        self._view.clear()

    def closeEvent(self, event) -> None:  # noqa: D401 - Qt override
        try:
            log_buffer.unsubscribe(self._bridge.line.emit)
        except Exception:  # noqa: BLE001
            pass
        super().closeEvent(event)

    def keyPressEvent(self, event) -> None:  # noqa: D401 - Qt override
        if event.key() == Qt.Key.Key_Escape:
            self.accept()
            return
        super().keyPressEvent(event)


__all__ = ["LogPanel"]
