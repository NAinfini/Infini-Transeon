"""First-run onboarding wizard."""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFrame,
    QLabel,
    QRadioButton,
    QVBoxLayout,
)

from infini_transeon.config.languages import LanguageRegistry
from infini_transeon.config.schema import AppConfig, TranslationMode
from infini_transeon.utils.i18n import tr


class FirstRunWizard(QDialog):
    """Welcoming first-run wizard — pick mode + default target language."""

    completed = Signal(AppConfig)

    def __init__(
        self,
        config: AppConfig,
        *,
        registry: LanguageRegistry,
        parent=None,
    ) -> None:
        super().__init__(parent)
        self.setWindowTitle(tr("wiz.title"))
        self.resize(600, 520)
        self._config = config.model_copy(deep=True)
        self._registry = registry
        self._build_ui()

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(28, 24, 28, 20)
        root.setSpacing(18)

        title = QLabel(tr("wiz.title"))
        title.setObjectName("h1")
        subtitle = QLabel(tr("wiz.subtitle"))
        subtitle.setObjectName("muted")
        subtitle.setWordWrap(True)
        root.addWidget(title)
        root.addWidget(subtitle)

        # Mode card
        mode_card = QFrame()
        mode_card.setObjectName("card")
        ml = QVBoxLayout(mode_card)
        ml.setContentsMargins(18, 14, 18, 14)
        ml.setSpacing(10)
        mode_title = QLabel(tr("wiz.mode.title"))
        mode_title.setObjectName("h2")
        ml.addWidget(mode_title)

        self._online = QRadioButton(tr("wiz.mode.online"))
        self._online.setChecked(True)
        online_hint = QLabel(tr("wiz.mode.online.hint"))
        online_hint.setObjectName("muted")
        online_hint.setWordWrap(True)
        online_hint.setIndent(24)

        self._local = QRadioButton(tr("wiz.mode.local"))
        local_hint = QLabel(tr("wiz.mode.local.hint"))
        local_hint.setObjectName("muted")
        local_hint.setWordWrap(True)
        local_hint.setIndent(24)

        ml.addWidget(self._online)
        ml.addWidget(online_hint)
        ml.addSpacing(6)
        ml.addWidget(self._local)
        ml.addWidget(local_hint)
        root.addWidget(mode_card)

        # Language card
        lang_card = QFrame()
        lang_card.setObjectName("card")
        ll = QVBoxLayout(lang_card)
        ll.setContentsMargins(18, 14, 18, 14)
        ll.setSpacing(10)
        lang_title = QLabel(tr("wiz.lang.title"))
        lang_title.setObjectName("h2")
        ll.addWidget(lang_title)

        self._target = QComboBox()
        for lang in self._registry:
            self._target.addItem(lang.display, lang.code)
        ll.addWidget(self._target)

        lang_hint = QLabel(tr("wiz.lang.hint"))
        lang_hint.setObjectName("muted")
        lang_hint.setWordWrap(True)
        ll.addWidget(lang_hint)
        root.addWidget(lang_card)

        root.addStretch(1)

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        ok_btn = buttons.button(QDialogButtonBox.StandardButton.Ok)
        if ok_btn is not None:
            ok_btn.setObjectName("primary")
            ok_btn.setText(tr("wiz.btn.start"))
        cancel_btn = buttons.button(QDialogButtonBox.StandardButton.Cancel)
        if cancel_btn is not None:
            cancel_btn.setText(tr("wiz.btn.skip"))
        buttons.accepted.connect(self._finish)
        buttons.rejected.connect(self._skip)
        root.addWidget(buttons, alignment=Qt.AlignmentFlag.AlignRight)

    def _finish(self) -> None:
        mode = TranslationMode.online if self._online.isChecked() else TranslationMode.local
        translation = self._config.translation.model_copy(
            update={
                "mode": mode,
                "target_lang": self._target.currentData(),
            }
        )
        self._config = self._config.model_copy(
            update={"translation": translation, "first_run_completed": True}
        )
        self.completed.emit(self._config)
        self.accept()

    def _skip(self) -> None:
        self._config = self._config.model_copy(update={"first_run_completed": True})
        self.completed.emit(self._config)
        self.reject()


__all__ = ["FirstRunWizard"]
