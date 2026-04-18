"""Hotkeys panel."""

from __future__ import annotations

from PySide6.QtWidgets import (
    QFormLayout,
    QKeySequenceEdit,
    QLabel,
    QVBoxLayout,
    QWidget,
)

from infini_transeon.config.schema import HotkeysConfig
from infini_transeon.utils.i18n import tr


class HotkeysPanel(QWidget):
    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        root = QVBoxLayout(self)
        root.setContentsMargins(12, 12, 12, 12)
        root.setSpacing(10)

        hint = QLabel(tr("hotkeys.hint"))
        hint.setObjectName("muted")
        hint.setWordWrap(True)
        root.addWidget(hint)

        form = QFormLayout()
        form.setHorizontalSpacing(16)
        form.setVerticalSpacing(10)
        self._toggle = QKeySequenceEdit()
        self._pick = QKeySequenceEdit()
        self._retry = QKeySequenceEdit()
        self._translate_now = QKeySequenceEdit()
        form.addRow(tr("hotkeys.toggle"), self._toggle)
        form.addRow(tr("hotkeys.pick"), self._pick)
        form.addRow(tr("hotkeys.retry"), self._retry)
        form.addRow(tr("hotkeys.translate_now"), self._translate_now)
        root.addLayout(form)
        root.addStretch(1)

    def load(self, hk: HotkeysConfig) -> None:
        self._toggle.setKeySequence(hk.toggle)
        self._pick.setKeySequence(hk.pick_window)
        self._retry.setKeySequence(hk.retry_online)
        self._translate_now.setKeySequence(hk.translate_now)

    def snapshot(self) -> HotkeysConfig:
        return HotkeysConfig(
            toggle=self._toggle.keySequence().toString() or "ctrl+alt+t",
            pick_window=self._pick.keySequence().toString() or "ctrl+alt+w",
            retry_online=self._retry.keySequence().toString() or "ctrl+alt+r",
            translate_now=self._translate_now.keySequence().toString() or "ctrl+alt+space",
        )


__all__ = ["HotkeysPanel"]
