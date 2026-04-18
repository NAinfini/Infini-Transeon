"""Main control window. Replaces the tray-only model with a proper UI."""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal, Slot
from PySide6.QtGui import QCloseEvent, QMouseEvent
from PySide6.QtWidgets import (
    QButtonGroup,
    QComboBox,
    QFormLayout,
    QFrame,
    QHBoxLayout,
    QLabel,
    QMainWindow,
    QPushButton,
    QRadioButton,
    QStatusBar,
    QVBoxLayout,
    QWidget,
)

from infini_transeon.config.languages import LanguageRegistry
from infini_transeon.utils.i18n import tr


class _ClickableStatusBar(QStatusBar):
    clicked = Signal()

    def mousePressEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        if event.button() == Qt.MouseButton.LeftButton:
            self.clicked.emit()
        super().mousePressEvent(event)


class MainWindow(QMainWindow):
    """Single-window control surface for Infini-Transeon."""

    pick_window = Signal()
    pick_region = Signal()
    toggle_running = Signal()
    translate_now = Signal()
    capture_mode_changed = Signal(str)  # "window" | "region"
    open_settings = Signal()
    retry_online = Signal()
    check_updates = Signal()
    open_logs = Signal()
    source_lang_changed = Signal(str)
    target_lang_changed = Signal(str)
    quit_requested = Signal()

    def __init__(self, registry: LanguageRegistry) -> None:
        super().__init__()
        self._registry = registry
        self._running = False
        self._degraded = False
        self._has_target = False
        self._loading_label = ""
        self._capture_mode: str = "window"
        self._last_error: str = ""

        self.setWindowTitle(tr("app.title"))
        self.resize(720, 640)
        self.setMinimumSize(640, 600)

        central = QWidget()
        self.setCentralWidget(central)
        root = QVBoxLayout(central)
        root.setContentsMargins(24, 20, 24, 16)
        root.setSpacing(12)

        # Header
        header = QVBoxLayout()
        header.setSpacing(2)
        title = QLabel(tr("app.title"))
        title.setObjectName("h1")
        subtitle = QLabel(tr("app.subtitle"))
        subtitle.setObjectName("muted")
        header.addWidget(title)
        header.addWidget(subtitle)
        root.addLayout(header)

        root.addWidget(self._build_status_card())
        root.addWidget(self._build_capture_card())
        root.addWidget(self._build_language_card())

        # Actions — single row
        actions = QHBoxLayout()
        actions.setSpacing(10)

        self._toggle_btn = QPushButton(tr("main.btn.start"))
        self._toggle_btn.setObjectName("primary")
        self._toggle_btn.clicked.connect(self.toggle_running)
        self._toggle_btn.setEnabled(False)
        actions.addWidget(self._toggle_btn)

        self._translate_now_btn = QPushButton(tr("main.btn.translate_now"))
        self._translate_now_btn.setToolTip(tr("main.btn.translate_now.tip"))
        self._translate_now_btn.clicked.connect(self.translate_now)
        self._translate_now_btn.setEnabled(False)
        actions.addWidget(self._translate_now_btn)

        self._retry_btn = QPushButton(tr("main.btn.retry_online"))
        self._retry_btn.clicked.connect(self.retry_online)
        self._retry_btn.setVisible(False)
        actions.addWidget(self._retry_btn)

        self._settings_btn = QPushButton(tr("main.btn.settings"))
        self._settings_btn.clicked.connect(self.open_settings)
        actions.addWidget(self._settings_btn)

        self._logs_btn = QPushButton(tr("main.btn.logs"))
        self._logs_btn.clicked.connect(self.open_logs)
        actions.addWidget(self._logs_btn)

        actions.addStretch(1)
        root.addLayout(actions)
        root.addStretch(1)

        status_bar = _ClickableStatusBar()
        status_bar.setCursor(Qt.CursorShape.PointingHandCursor)
        status_bar.setToolTip(tr("main.statusbar.tooltip"))
        status_bar.clicked.connect(self.open_logs)
        self.setStatusBar(status_bar)
        self._status_bar = status_bar

    # --- builders -----------------------------------------------------

    def _build_status_card(self) -> QFrame:
        card = QFrame()
        card.setObjectName("card")
        layout = QVBoxLayout(card)
        layout.setContentsMargins(18, 14, 18, 14)
        layout.setSpacing(10)

        head = QHBoxLayout()
        head.setSpacing(10)
        head_title = QLabel(tr("main.status.label"))
        head_title.setObjectName("h2")
        head.addWidget(head_title)
        self._status_pill = QLabel(tr("main.status.idle"))
        self._status_pill.setObjectName("statusPill")
        self._status_pill.setProperty("state", "idle")
        head.addWidget(self._status_pill)
        head.addStretch(1)
        layout.addLayout(head)

        form = QFormLayout()
        form.setHorizontalSpacing(12)
        form.setVerticalSpacing(6)
        form.setLabelAlignment(Qt.AlignmentFlag.AlignRight)

        self._target_value = QLabel(tr("main.target.none"))
        self._target_value.setWordWrap(True)
        self._source_value = QLabel("—")
        self._dest_value = QLabel("—")
        self._device_value = QLabel("—")

        form.addRow(QLabel(tr("main.field.window")), self._target_value)
        form.addRow(QLabel(tr("main.field.source")), self._source_value)
        form.addRow(QLabel(tr("main.field.target")), self._dest_value)
        form.addRow(QLabel(tr("main.field.device")), self._device_value)
        layout.addLayout(form)
        return card

    def _build_capture_card(self) -> QFrame:
        card = QFrame()
        card.setObjectName("card")
        outer = QVBoxLayout(card)
        outer.setContentsMargins(18, 14, 18, 14)
        outer.setSpacing(8)

        head = QLabel(tr("main.capture.label"))
        head.setObjectName("h2")
        outer.addWidget(head)

        # Single row: mode radios on the left, picker button on the right.
        row = QHBoxLayout()
        row.setSpacing(12)
        self._mode_group = QButtonGroup(self)
        self._mode_group.setExclusive(True)
        self._mode_window = QRadioButton(tr("main.capture.mode.window"))
        self._mode_region = QRadioButton(tr("main.capture.mode.region"))
        self._mode_group.addButton(self._mode_window)
        self._mode_group.addButton(self._mode_region)
        self._mode_window.setChecked(True)
        self._mode_window.toggled.connect(self._on_mode_toggled)
        self._mode_region.toggled.connect(self._on_mode_toggled)
        row.addWidget(self._mode_window)
        row.addWidget(self._mode_region)
        row.addSpacing(12)

        # Exactly one picker visible at a time.
        self._pick_btn = QPushButton(tr("main.btn.pick"))
        self._pick_btn.clicked.connect(self.pick_window)
        row.addWidget(self._pick_btn)

        self._region_btn = QPushButton(tr("main.btn.pick_region"))
        self._region_btn.setToolTip(tr("main.btn.pick_region.tip"))
        self._region_btn.clicked.connect(self.pick_region)
        self._region_btn.setVisible(False)
        row.addWidget(self._region_btn)
        row.addStretch(1)
        outer.addLayout(row)
        return card

    def _build_language_card(self) -> QFrame:
        card = QFrame()
        card.setObjectName("card")
        layout = QHBoxLayout(card)
        layout.setContentsMargins(18, 14, 18, 14)
        layout.setSpacing(12)

        src_col = QVBoxLayout()
        src_col.setSpacing(4)
        src_label = QLabel(tr("main.field.source"))
        src_label.setObjectName("muted")
        src_col.addWidget(src_label)
        self._source_combo = QComboBox()
        self._source_combo.addItem(tr("settings.translation.source.auto"), "auto")
        for lang in self._registry:
            self._source_combo.addItem(lang.display, lang.code)
        self._source_combo.currentIndexChanged.connect(self._on_source_changed)
        src_col.addWidget(self._source_combo)
        layout.addLayout(src_col, 1)

        arrow = QLabel("→")
        arrow.setObjectName("muted")
        arrow.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(arrow)

        tgt_col = QVBoxLayout()
        tgt_col.setSpacing(4)
        tgt_label = QLabel(tr("main.field.target"))
        tgt_label.setObjectName("muted")
        tgt_col.addWidget(tgt_label)
        self._target_combo = QComboBox()
        for lang in self._registry:
            self._target_combo.addItem(lang.display, lang.code)
        self._target_combo.currentIndexChanged.connect(self._on_target_changed)
        tgt_col.addWidget(self._target_combo)
        layout.addLayout(tgt_col, 1)
        return card

    # --- public API ---------------------------------------------------

    def set_languages(self, source_code: str, target_code: str) -> None:
        self._set_combo(self._source_combo, source_code)
        self._set_combo(self._target_combo, target_code)
        self._source_value.setText(self._combo_label(self._source_combo))
        self._dest_value.setText(self._combo_label(self._target_combo))

    def set_device_info(self, text: str) -> None:
        """One-line compute-device summary for the status card."""
        self._device_value.setText(text)

    def set_target(self, title: str) -> None:
        if title:
            self._has_target = True
            self._target_value.setText(title)
        else:
            self._has_target = False
            self._target_value.setText(tr("main.target.none"))
        self._refresh_action_buttons()
        self._refresh_status_pill()

    def set_capture_mode(self, mode: str) -> None:
        """Sync the radio buttons with config (external source of truth)."""
        if mode not in ("window", "region"):
            mode = "window"
        if self._capture_mode == mode and self._mode_window.isChecked() == (mode == "window"):
            return
        self._capture_mode = mode
        self._mode_window.blockSignals(True)
        self._mode_region.blockSignals(True)
        if mode == "window":
            self._mode_window.setChecked(True)
        else:
            self._mode_region.setChecked(True)
        self._mode_window.blockSignals(False)
        self._mode_region.blockSignals(False)
        self._apply_mode_visibility()

    def set_running_state(self, *, running: bool, degraded: bool) -> None:
        self._running = running
        self._degraded = degraded
        self._toggle_btn.setText(
            tr("main.btn.stop") if running else tr("main.btn.start")
        )
        self._retry_btn.setVisible(degraded)
        self._refresh_action_buttons()
        self._refresh_status_pill()

    def show_status(self, message: str, timeout_ms: int = 4000) -> None:
        self._status_bar.showMessage(message, timeout_ms)

    def notify(self, title: str, message: str) -> None:
        self._last_error = f"{title}: {message}"
        self._status_bar.showMessage(self._last_error, 8000)

    # --- internal -----------------------------------------------------

    def _set_combo(self, combo: QComboBox, code: str) -> None:
        combo.blockSignals(True)
        for i in range(combo.count()):
            if combo.itemData(i) == code:
                combo.setCurrentIndex(i)
                break
        combo.blockSignals(False)

    def _combo_label(self, combo: QComboBox) -> str:
        return combo.currentText() or "—"

    def _on_source_changed(self, _i: int) -> None:
        code = self._source_combo.currentData()
        self._source_value.setText(self._combo_label(self._source_combo))
        if isinstance(code, str):
            self.source_lang_changed.emit(code)

    def _on_target_changed(self, _i: int) -> None:
        code = self._target_combo.currentData()
        self._dest_value.setText(self._combo_label(self._target_combo))
        if isinstance(code, str):
            self.target_lang_changed.emit(code)

    def _on_mode_toggled(self, checked: bool) -> None:
        # Only act on the button that got checked, not the one that got unchecked.
        if not checked:
            return
        mode = "window" if self._mode_window.isChecked() else "region"
        if mode == self._capture_mode:
            return
        self._capture_mode = mode
        self._apply_mode_visibility()
        self.capture_mode_changed.emit(mode)

    def _apply_mode_visibility(self) -> None:
        is_window = self._capture_mode == "window"
        self._pick_btn.setVisible(is_window)
        self._region_btn.setVisible(not is_window)

    def _refresh_action_buttons(self) -> None:
        self._toggle_btn.setEnabled(self._has_target)
        self._translate_now_btn.setEnabled(self._has_target)

    def _refresh_status_pill(self) -> None:
        if self._loading_label:
            state, text = "idle", self._loading_label
        elif not self._has_target:
            state, text = "idle", tr("main.status.idle")
        elif self._degraded:
            state, text = "degraded", tr("main.status.degraded")
        elif self._running:
            state, text = "running", tr("main.status.running")
        else:
            state, text = "idle", tr("main.status.paused")
        self._status_pill.setProperty("state", state)
        self._status_pill.setText(text)
        self._status_pill.style().unpolish(self._status_pill)
        self._status_pill.style().polish(self._status_pill)

    @Slot(str)
    def set_loading(self, label: str | None) -> None:
        """Show a transient "busy" label in the status pill.

        Pass a user-facing string (e.g. ``tr("main.status.loading_model")``)
        to pin that text; pass ``None`` or "" to clear. Decorated as a Qt
        Slot so ``QMetaObject.invokeMethod`` from a worker thread can call
        it via a queued connection onto the UI thread.
        """
        self._loading_label = label or ""
        self._refresh_status_pill()

    def closeEvent(self, event: QCloseEvent) -> None:  # noqa: D401 - Qt override
        event.accept()
        self.quit_requested.emit()


__all__ = ["MainWindow"]
