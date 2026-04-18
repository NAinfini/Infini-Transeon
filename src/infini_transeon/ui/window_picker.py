"""Window picker dialog — lists available windows for the user to target."""

from __future__ import annotations

from PySide6.QtCore import Qt, QTimer, Signal
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QListWidget,
    QListWidgetItem,
    QPushButton,
    QVBoxLayout,
)

from infini_transeon.capture.base import CaptureBackend, WindowInfo
from infini_transeon.utils.i18n import tr


class WindowPicker(QDialog):
    """Modal dialog to choose a window to translate. Auto-refreshes."""

    selected = Signal(object)  # emits WindowInfo

    REFRESH_INTERVAL_MS = 1500

    def __init__(self, backend: CaptureBackend, parent=None) -> None:
        super().__init__(parent)
        self.setWindowTitle(tr("picker.title"))
        self.resize(560, 460)
        self._backend = backend
        self._all: list[WindowInfo] = []

        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(10)
        filter_row = QHBoxLayout()
        filter_row.setSpacing(8)
        filter_row.addWidget(QLabel(tr("picker.filter.label")))
        self._filter = QLineEdit()
        self._filter.setPlaceholderText(tr("picker.filter.placeholder"))
        self._filter.textChanged.connect(lambda _t: self._render())
        filter_row.addWidget(self._filter)
        self._refresh_btn = QPushButton(tr("picker.btn.refresh"))
        self._refresh_btn.clicked.connect(self._refresh)
        filter_row.addWidget(self._refresh_btn)
        layout.addLayout(filter_row)

        self._list = QListWidget()
        self._list.itemDoubleClicked.connect(self._confirm)
        layout.addWidget(self._list)

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        ok_btn = buttons.button(QDialogButtonBox.StandardButton.Ok)
        if ok_btn is not None:
            ok_btn.setObjectName("primary")
            ok_btn.setText(tr("picker.btn.ok"))
        cancel_btn = buttons.button(QDialogButtonBox.StandardButton.Cancel)
        if cancel_btn is not None:
            cancel_btn.setText(tr("picker.btn.cancel"))
        buttons.accepted.connect(self._confirm)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

        self._timer = QTimer(self)
        self._timer.setInterval(self.REFRESH_INTERVAL_MS)
        self._timer.timeout.connect(self._refresh)

        self._refresh()
        self._timer.start()

    # --- refresh / render --------------------------------------------

    def _refresh(self) -> None:
        try:
            self._all = self._backend.list_windows(visible_only=True)
        except Exception:  # noqa: BLE001 - best-effort
            return
        self._render()

    def _render(self) -> None:
        # Preserve current selection (by handle) and scroll position.
        current = self._list.currentItem()
        selected_handle: int | None = None
        if current is not None:
            info = current.data(Qt.ItemDataRole.UserRole)
            if isinstance(info, WindowInfo):
                selected_handle = info.handle
        scroll_pos = self._list.verticalScrollBar().value()

        needle = self._filter.text().lower().strip()
        self._list.blockSignals(True)
        self._list.clear()
        reselect_row = -1
        for info in self._all:
            label = f"{info.app or '?'} — {info.title}"
            if needle and needle not in label.lower():
                continue
            item = QListWidgetItem(label)
            item.setData(Qt.ItemDataRole.UserRole, info)
            self._list.addItem(item)
            if selected_handle is not None and info.handle == selected_handle:
                reselect_row = self._list.count() - 1
        if reselect_row >= 0:
            self._list.setCurrentRow(reselect_row)
        self._list.verticalScrollBar().setValue(scroll_pos)
        self._list.blockSignals(False)

    # --- confirm / lifecycle -----------------------------------------

    def _confirm(self, *_args: object) -> None:
        current = self._list.currentItem()
        if current is None:
            return
        self._timer.stop()
        self.selected.emit(current.data(Qt.ItemDataRole.UserRole))
        self.accept()

    def reject(self) -> None:  # noqa: D401 - Qt override
        self._timer.stop()
        super().reject()


__all__ = ["WindowPicker"]
