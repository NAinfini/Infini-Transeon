"""Provider configuration form used inside the settings dialog."""

from __future__ import annotations

from PySide6.QtCore import Signal
from PySide6.QtWidgets import (
    QComboBox,
    QDoubleSpinBox,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QSpinBox,
    QVBoxLayout,
    QWidget,
)

from infini_transeon.config.schema import ProviderConfig
from infini_transeon.utils.i18n import tr

_PROTOCOL_CHOICES = [
    ("OpenAI-compatible", "openai_compat"),
    ("Anthropic", "anthropic"),
    ("Google Gemini", "google"),
    ("MyMemory (no key)", "mymemory"),
]

_PLACEHOLDERS = {
    "openai_compat": ("https://openrouter.ai/api/v1", "deepseek/deepseek-chat:free"),
    "anthropic": ("https://api.anthropic.com", "claude-sonnet-4-6"),
    "google": ("", "gemini-2.5-flash"),
    "mymemory": ("", ""),
}


class ProviderForm(QWidget):
    """A reusable, generic provider config widget."""

    test_requested = Signal(ProviderConfig, str)          # config, api_key_plaintext
    save_requested = Signal(ProviderConfig, str, bool)     # config, key, clear_flag

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._build_ui()

    def load(self, cfg: ProviderConfig) -> None:
        for i, (_, value) in enumerate(_PROTOCOL_CHOICES):
            if value == cfg.protocol:
                self._protocol.setCurrentIndex(i)
                break
        self._base_url.setText(cfg.base_url or "")
        self._model.setText(cfg.model or "")
        self._temperature.setValue(cfg.temperature)
        self._max_tokens.setValue(cfg.max_tokens)
        self._timeout.setValue(cfg.timeout_seconds)
        self._apikey.clear()
        self._apikey.setPlaceholderText(
            tr("provider.api_key.placeholder") if cfg.api_key_ref else ""
        )

    def snapshot(self) -> tuple[ProviderConfig, str]:
        protocol = _PROTOCOL_CHOICES[self._protocol.currentIndex()][1]
        cfg = ProviderConfig(
            protocol=protocol,  # type: ignore[arg-type]
            base_url=self._base_url.text().strip() or None,
            model=self._model.text().strip() or None,
            api_key_ref=None,  # caller fills after keyring persistence
            temperature=float(self._temperature.value()),
            max_tokens=int(self._max_tokens.value()),
            timeout_seconds=float(self._timeout.value()),
            extra_headers={},
        )
        return cfg, self._apikey.text()

    def _build_ui(self) -> None:
        outer = QVBoxLayout(self)
        outer.setSpacing(14)

        config_box = QGroupBox(tr("provider.group.main"))
        form = QFormLayout(config_box)
        form.setHorizontalSpacing(16)
        form.setVerticalSpacing(10)

        self._protocol = QComboBox()
        for label, _ in _PROTOCOL_CHOICES:
            self._protocol.addItem(label)
        self._protocol.currentIndexChanged.connect(self._on_protocol_changed)
        form.addRow(tr("provider.protocol"), self._protocol)

        self._base_url = QLineEdit()
        self._base_url.setPlaceholderText("e.g. https://openrouter.ai/api/v1")
        form.addRow(tr("provider.base_url"), self._base_url)

        self._model = QLineEdit()
        self._model.setPlaceholderText("e.g. deepseek/deepseek-chat:free")
        form.addRow(tr("provider.model"), self._model)

        self._apikey = QLineEdit()
        self._apikey.setEchoMode(QLineEdit.EchoMode.Password)
        self._apikey.setPlaceholderText(tr("provider.api_key.placeholder"))
        form.addRow(tr("provider.api_key"), self._apikey)

        outer.addWidget(config_box)

        advanced = QGroupBox(tr("provider.group.advanced"))
        af = QFormLayout(advanced)
        af.setHorizontalSpacing(16)
        af.setVerticalSpacing(10)
        self._temperature = QDoubleSpinBox()
        self._temperature.setRange(0.0, 2.0)
        self._temperature.setSingleStep(0.1)
        af.addRow(tr("provider.temperature"), self._temperature)
        self._max_tokens = QSpinBox()
        self._max_tokens.setRange(1, 32768)
        self._max_tokens.setValue(2048)
        af.addRow(tr("provider.max_tokens"), self._max_tokens)
        self._timeout = QDoubleSpinBox()
        self._timeout.setRange(1, 600)
        self._timeout.setValue(30)
        self._timeout.setSuffix(" s")
        af.addRow(tr("provider.timeout"), self._timeout)
        outer.addWidget(advanced)

        button_row = QHBoxLayout()
        button_row.setSpacing(10)
        self._test = QPushButton(tr("provider.test"))
        self._test.setObjectName("primary")
        self._test.setToolTip(tr("provider.test.tip"))
        self._test.clicked.connect(self._emit_test)
        button_row.addWidget(self._test)
        self._save = QPushButton(tr("provider.save"))
        self._save.setToolTip(tr("provider.save.tip"))
        self._save.clicked.connect(self._emit_save)
        button_row.addWidget(self._save)
        self._clear = QPushButton(tr("provider.clear"))
        self._clear.setObjectName("danger")
        self._clear.setToolTip(tr("provider.clear.tip"))
        self._clear.clicked.connect(self._emit_clear)
        button_row.addWidget(self._clear)
        button_row.addStretch(1)
        outer.addLayout(button_row)

        self._status = QLabel("")
        self._status.setObjectName("muted")
        self._status.setWordWrap(True)
        outer.addWidget(self._status)

        outer.addStretch(1)
        self._on_protocol_changed(self._protocol.currentIndex())

    def set_status(self, text: str) -> None:
        self._status.setText(text)

    def clear_plain_key(self) -> None:
        """Wipe the entered password field after it has been persisted."""
        self._apikey.clear()

    def set_testing(self, testing: bool) -> None:
        self._test.setDisabled(testing)
        self._save.setDisabled(testing)
        self._clear.setDisabled(testing)
        self._status.setText(tr("provider.testing") if testing else "")

    def _on_protocol_changed(self, idx: int) -> None:
        protocol = _PROTOCOL_CHOICES[idx][1]
        base_ph, model_ph = _PLACEHOLDERS[protocol]
        self._base_url.setPlaceholderText(base_ph or "(not applicable)")
        self._model.setPlaceholderText(model_ph or "(not applicable)")
        is_mymemory = protocol == "mymemory"
        self._base_url.setDisabled(is_mymemory or protocol == "google")
        self._model.setDisabled(is_mymemory)
        self._apikey.setDisabled(is_mymemory)

    def _emit_test(self) -> None:
        cfg, key = self.snapshot()
        self.test_requested.emit(cfg, key)

    def _emit_save(self) -> None:
        cfg, key = self.snapshot()
        self.save_requested.emit(cfg, key, False)

    def _emit_clear(self) -> None:
        cfg, _ = self.snapshot()
        self.save_requested.emit(cfg, "", True)


__all__ = ["ProviderForm"]
