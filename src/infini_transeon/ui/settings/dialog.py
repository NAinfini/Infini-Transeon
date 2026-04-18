"""Top-level Settings dialog wiring together the individual panels."""

from __future__ import annotations

from PySide6.QtCore import Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QDoubleSpinBox,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QMessageBox,
    QPushButton,
    QSpinBox,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from infini_transeon.config import secrets
from infini_transeon.config.languages import LanguageRegistry
from infini_transeon.config.schema import (
    AppConfig,
    StyleMode,
    TranslationMode,
)
from infini_transeon.translate.usage import UsageTracker
from infini_transeon.ui.settings.glossary_editor import GlossaryEditor
from infini_transeon.ui.settings.hotkeys_panel import HotkeysPanel
from infini_transeon.ui.settings.local_models import LocalModelsPanel
from infini_transeon.ui.settings.overlay_panel import OverlayPanel
from infini_transeon.ui.settings.provider_form import ProviderForm
from infini_transeon.ui.settings.provider_test import ProviderTestJob
from infini_transeon.utils.i18n import tr


class SettingsDialog(QDialog):
    saved = Signal(AppConfig)
    check_updates_requested = Signal()

    def __init__(
        self,
        config: AppConfig,
        *,
        registry: LanguageRegistry,
        usage: UsageTracker,
        parent=None,
    ) -> None:
        super().__init__(parent)
        self._config = config.model_copy(deep=True)
        self._registry = registry
        # Kept in the constructor signature for API stability with callers;
        # the Usage dashboard was removed from the dialog. Unused here.
        del usage
        self._test_job: ProviderTestJob | None = None
        self.setWindowTitle(tr("settings.title"))
        self.resize(680, 600)
        self._build_ui()
        self._load()

    # --- UI layout ----------------------------------------------------

    def _build_ui(self) -> None:
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(12)
        self._tabs = QTabWidget()
        layout.addWidget(self._tabs)

        # Tabs split from the old single Translation tab to keep each page
        # short enough to fit on small displays (no scroll, no clipping).
        self._tabs.addTab(self._build_general_tab(), tr("settings.tab.general"))
        self._tabs.addTab(self._build_capture_tab(), tr("settings.tab.capture"))
        self._tabs.addTab(self._build_ocr_tab(), tr("settings.tab.ocr"))

        # Provider tab (online)
        self._provider_form = ProviderForm()
        self._provider_form.test_requested.connect(self._on_provider_test)
        self._provider_form.save_requested.connect(self._on_provider_save)
        self._tabs.addTab(self._provider_form, tr("settings.tab.provider"))

        # Local models tab
        self._local_panel = LocalModelsPanel(self._registry)
        self._local_panel.request_variant_change.connect(self._on_variant_change)
        self._tabs.addTab(self._local_panel, tr("settings.tab.local"))

        # Glossary
        self._glossary = GlossaryEditor()
        self._tabs.addTab(self._glossary, tr("settings.tab.glossary"))

        # Overlay appearance + behaviour
        self._overlay = OverlayPanel()
        self._tabs.addTab(self._overlay, tr("settings.tab.overlay"))

        # Hotkeys
        self._hotkeys = HotkeysPanel()
        self._tabs.addTab(self._hotkeys, tr("settings.tab.hotkeys"))

        # Updates
        self._tabs.addTab(self._build_updates_tab(), tr("settings.tab.updates"))

        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        ok_btn = buttons.button(QDialogButtonBox.StandardButton.Ok)
        if ok_btn is not None:
            ok_btn.setObjectName("primary")
            ok_btn.setText(tr("settings.btn.ok"))
        cancel_btn = buttons.button(QDialogButtonBox.StandardButton.Cancel)
        if cancel_btn is not None:
            cancel_btn.setText(tr("settings.btn.cancel"))
        buttons.accepted.connect(self._save_and_close)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def _build_general_tab(self) -> QWidget:
        page = QGroupBox()
        form = QFormLayout(page)
        form.setHorizontalSpacing(16)
        form.setVerticalSpacing(10)

        self._mode = QComboBox()
        self._mode.addItem(tr("settings.translation.mode.online"), TranslationMode.online)
        self._mode.addItem(tr("settings.translation.mode.local"), TranslationMode.local)
        form.addRow(tr("settings.translation.mode"), self._mode)

        self._style = QComboBox()
        for style in StyleMode:
            self._style.addItem(style.value.capitalize(), style)
        form.addRow(tr("settings.translation.style"), self._style)

        self._ui_language = QComboBox()
        self._ui_language.addItem("Auto (system)", "auto")
        for lang in self._registry:
            self._ui_language.addItem(lang.display, lang.code)
        form.addRow(tr("settings.translation.ui_language"), self._ui_language)
        ui_hint = QLabel(tr("settings.translation.ui_language.hint"))
        ui_hint.setObjectName("muted")
        ui_hint.setWordWrap(True)
        form.addRow("", ui_hint)

        self._theme = QComboBox()
        self._theme.addItem(tr("settings.theme.auto"), "auto")
        self._theme.addItem(tr("settings.theme.light"), "light")
        self._theme.addItem(tr("settings.theme.dark"), "dark")
        form.addRow(tr("settings.theme.label"), self._theme)
        theme_hint = QLabel(tr("settings.theme.hint"))
        theme_hint.setObjectName("muted")
        theme_hint.setWordWrap(True)
        form.addRow("", theme_hint)

        # Online fallback behaviour + translation-memory cache size. These
        # used to live only in config.yaml; surfacing them here so users
        # can see and change them without editing files.
        self._fallback_to_local = QCheckBox(
            tr("settings.translation.fallback_to_local.label")
        )
        form.addRow("", self._fallback_to_local)
        fb_hint = QLabel(tr("settings.translation.fallback_to_local.hint"))
        fb_hint.setObjectName("muted")
        fb_hint.setWordWrap(True)
        form.addRow("", fb_hint)

        self._last_resort_mymemory = QCheckBox(
            tr("settings.translation.last_resort_mymemory.label")
        )
        form.addRow("", self._last_resort_mymemory)
        mm_hint = QLabel(tr("settings.translation.last_resort_mymemory.hint"))
        mm_hint.setObjectName("muted")
        mm_hint.setWordWrap(True)
        form.addRow("", mm_hint)

        self._cache_size = QSpinBox()
        self._cache_size.setRange(0, 1_000_000)
        self._cache_size.setSingleStep(500)
        form.addRow(tr("settings.translation.cache_size.label"), self._cache_size)
        cs_hint = QLabel(tr("settings.translation.cache_size.hint"))
        cs_hint.setObjectName("muted")
        cs_hint.setWordWrap(True)
        form.addRow("", cs_hint)
        return page

    def _build_capture_tab(self) -> QWidget:
        page = QGroupBox()
        form = QFormLayout(page)
        form.setHorizontalSpacing(16)
        form.setVerticalSpacing(10)

        self._ocr_interval = QDoubleSpinBox()
        self._ocr_interval.setRange(0.2, 30.0)
        self._ocr_interval.setDecimals(1)
        self._ocr_interval.setSingleStep(0.1)
        self._ocr_interval.setSuffix(" s")
        form.addRow(tr("settings.capture.interval.label"), self._ocr_interval)
        interval_hint = QLabel(tr("settings.capture.interval.hint"))
        interval_hint.setObjectName("muted")
        interval_hint.setWordWrap(True)
        form.addRow("", interval_hint)
        return page

    def _build_ocr_tab(self) -> QWidget:
        page = QWidget()
        outer = QVBoxLayout(page)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(12)

        primary = QGroupBox()
        form = QFormLayout(primary)
        form.setHorizontalSpacing(16)
        form.setVerticalSpacing(10)

        self._det_max_side = QSpinBox()
        self._det_max_side.setRange(640, 7680)
        self._det_max_side.setSingleStep(128)
        self._det_max_side.setSuffix(" px")
        form.addRow(tr("settings.ocr.det_max_side.label"), self._det_max_side)
        det_hint = QLabel(tr("settings.ocr.det_max_side.hint"))
        det_hint.setObjectName("muted")
        det_hint.setWordWrap(True)
        form.addRow("", det_hint)

        self._det_tier = QComboBox()
        self._det_tier.addItem(tr("settings.ocr.det_tier.mobile"), "mobile")
        self._det_tier.addItem(tr("settings.ocr.det_tier.server"), "server")
        form.addRow(tr("settings.ocr.det_tier.label"), self._det_tier)
        det_tier_hint = QLabel(tr("settings.ocr.det_tier.hint"))
        det_tier_hint.setObjectName("muted")
        det_tier_hint.setWordWrap(True)
        form.addRow("", det_tier_hint)

        self._min_conf = QDoubleSpinBox()
        self._min_conf.setRange(0.0, 1.0)
        self._min_conf.setDecimals(2)
        self._min_conf.setSingleStep(0.05)
        form.addRow(tr("settings.ocr.min_confidence.label"), self._min_conf)
        min_conf_hint = QLabel(tr("settings.ocr.min_confidence.hint"))
        min_conf_hint.setObjectName("muted")
        min_conf_hint.setWordWrap(True)
        form.addRow("", min_conf_hint)

        outer.addWidget(primary)

        # Advanced group: post-correction tuning + preprocessing toggle.
        # Checkable QGroupBox with ``setChecked(False)`` collapses the
        # body so the tab stays short by default; users can expand it
        # when they need to tune noisy captures.
        advanced = QGroupBox(tr("settings.ocr.advanced.title"))
        advanced.setCheckable(True)
        advanced.setChecked(False)
        adv_form = QFormLayout(advanced)
        adv_form.setHorizontalSpacing(16)
        adv_form.setVerticalSpacing(10)

        self._postcorrect_min_conf = QDoubleSpinBox()
        self._postcorrect_min_conf.setRange(0.0, 1.0)
        self._postcorrect_min_conf.setDecimals(2)
        self._postcorrect_min_conf.setSingleStep(0.05)
        adv_form.addRow(
            tr("settings.ocr.postcorrect_min_confidence.label"),
            self._postcorrect_min_conf,
        )
        pc_hint = QLabel(tr("settings.ocr.postcorrect_min_confidence.hint"))
        pc_hint.setObjectName("muted")
        pc_hint.setWordWrap(True)
        adv_form.addRow("", pc_hint)

        self._enable_postcorrect = QCheckBox(
            tr("settings.ocr.enable_postcorrect.label")
        )
        adv_form.addRow("", self._enable_postcorrect)
        epc_hint = QLabel(tr("settings.ocr.enable_postcorrect.hint"))
        epc_hint.setObjectName("muted")
        epc_hint.setWordWrap(True)
        adv_form.addRow("", epc_hint)

        self._enable_preprocess = QCheckBox(
            tr("settings.ocr.enable_preprocess.label")
        )
        adv_form.addRow("", self._enable_preprocess)
        ep_hint = QLabel(tr("settings.ocr.enable_preprocess.hint"))
        ep_hint.setObjectName("muted")
        ep_hint.setWordWrap(True)
        adv_form.addRow("", ep_hint)

        # Toggle the child widgets' visibility with the group's checked
        # state so the collapsed group actually shrinks instead of just
        # disabling its contents.
        def _sync_advanced_visibility(checked: bool) -> None:
            for i in range(adv_form.rowCount()):
                label_item = adv_form.itemAt(i, QFormLayout.ItemRole.LabelRole)
                field_item = adv_form.itemAt(i, QFormLayout.ItemRole.FieldRole)
                for item in (label_item, field_item):
                    if item is None:
                        continue
                    w = item.widget()
                    if w is not None:
                        w.setVisible(checked)

        advanced.toggled.connect(_sync_advanced_visibility)
        _sync_advanced_visibility(False)

        outer.addWidget(advanced)
        outer.addStretch(1)
        return page

    def _load(self) -> None:
        cfg = self._config
        self._mode.setCurrentIndex(
            0 if cfg.translation.mode == TranslationMode.online else 1
        )
        for i in range(self._style.count()):
            if self._style.itemData(i) == cfg.translation.style:
                self._style.setCurrentIndex(i)
                break
        self._select_code(self._ui_language, cfg.ui_language)
        self._select_code(self._theme, cfg.theme)
        self._fallback_to_local.setChecked(bool(cfg.translation.online.fallback_to_local))
        self._last_resort_mymemory.setChecked(
            bool(cfg.translation.online.last_resort_mymemory)
        )
        self._cache_size.setValue(int(cfg.translation.cache_size))
        self._ocr_interval.setValue(cfg.capture.ocr_interval_seconds)
        self._det_max_side.setValue(int(cfg.ocr.det_max_side))
        self._select_code(self._det_tier, cfg.ocr.det_tier)
        self._min_conf.setValue(float(cfg.ocr.min_confidence))
        self._postcorrect_min_conf.setValue(float(cfg.ocr.postcorrect_min_confidence))
        self._enable_postcorrect.setChecked(bool(cfg.ocr.enable_postcorrect))
        self._enable_preprocess.setChecked(bool(cfg.ocr.enable_preprocess))
        self._updates_auto_check.setChecked(bool(cfg.updates.auto_check))
        self._updates_auto_apply.setChecked(bool(cfg.updates.auto_apply))
        self._updates_interval.setValue(int(cfg.updates.check_interval_hours))
        self._select_code(self._updates_channel, cfg.updates.channel)
        self._overlay.load(cfg.overlay)
        self._provider_form.load(cfg.translation.online.primary)
        self._local_panel.load(cfg)
        self._glossary.load(cfg.translation.glossary)
        self._hotkeys.load(cfg.hotkeys)

    def _select_code(self, combo: QComboBox, code: str) -> None:
        for i in range(combo.count()):
            if combo.itemData(i) == code:
                combo.setCurrentIndex(i)
                return

    def _build_updates_tab(self) -> QWidget:
        tab = QWidget()
        layout = QVBoxLayout(tab)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(12)

        title = QLabel(tr("settings.updates.title"))
        title.setObjectName("h2")
        layout.addWidget(title)

        hint = QLabel(tr("settings.updates.hint"))
        hint.setObjectName("muted")
        hint.setWordWrap(True)
        layout.addWidget(hint)

        form = QFormLayout()
        form.setHorizontalSpacing(16)
        form.setVerticalSpacing(10)

        self._updates_auto_check = QCheckBox(
            tr("settings.updates.auto_check.label")
        )
        form.addRow("", self._updates_auto_check)

        self._updates_auto_apply = QCheckBox(
            tr("settings.updates.auto_apply.label")
        )
        form.addRow("", self._updates_auto_apply)

        self._updates_interval = QSpinBox()
        self._updates_interval.setRange(1, 168)
        self._updates_interval.setSuffix(" h")
        form.addRow(
            tr("settings.updates.check_interval.label"), self._updates_interval
        )

        self._updates_channel = QComboBox()
        self._updates_channel.addItem(tr("settings.updates.channel.stable"), "stable")
        self._updates_channel.addItem(tr("settings.updates.channel.beta"), "beta")
        form.addRow(tr("settings.updates.channel.label"), self._updates_channel)

        layout.addLayout(form)

        row = QHBoxLayout()
        row.setSpacing(10)
        self._check_updates_btn = QPushButton(tr("main.btn.updates"))
        self._check_updates_btn.setObjectName("primary")
        self._check_updates_btn.clicked.connect(self.check_updates_requested)
        row.addWidget(self._check_updates_btn)
        row.addStretch(1)
        layout.addLayout(row)

        layout.addStretch(1)
        return tab

    # --- handlers -----------------------------------------------------

    def _on_provider_test(self, cfg, plain_key: str) -> None:
        if self._test_job is not None:
            return  # already running
        if plain_key:
            ref = secrets.set_secret("online.test", plain_key)
            cfg = cfg.model_copy(update={"api_key_ref": ref})
        else:
            cfg = cfg.model_copy(
                update={"api_key_ref": self._config.translation.online.primary.api_key_ref}
            )
        self._provider_form.set_testing(True)
        target_lang = self._config.translation.target_lang or "zh"
        self._test_job = ProviderTestJob(cfg, target_lang=target_lang)
        self._test_job.finished.connect(self._on_test_finished)
        self._test_job.start()

    def _on_test_finished(self, result) -> None:
        self._test_job = None
        self._provider_form.set_testing(False)
        if result.ok:
            body = tr(
                "provider.test_success.body",
                latency=f"{result.latency_ms:.0f}",
                provider=result.provider_tag,
                sample=result.sample_output,
            )
            if result.usage:
                body += tr("provider.test_success.usage_suffix", usage=result.usage)
            QMessageBox.information(self, tr("provider.test_success_title"), body)
        else:
            QMessageBox.critical(
                self,
                tr("provider.test_failed_title"),
                result.error or "unknown error",
            )

    def _on_provider_save(self, cfg, plain_key: str, clear: bool) -> None:
        # "Save" on the provider tab now routes through the same persistence
        # path as OK: it commits all tabs so the on-disk config is consistent.
        self._commit_provider(cfg, plain_key, clear)
        self._provider_form.set_status(tr("provider.saved"))
        self._emit_full_save()

    def _commit_provider(self, cfg, plain_key: str, clear: bool) -> None:
        primary = self._config.translation.online.primary
        ref = primary.api_key_ref
        if clear:
            if ref:
                try:
                    secrets.delete_secret(ref)
                except secrets.SecretError:
                    pass
            ref = None
        elif plain_key:
            ref = secrets.set_secret("online.primary", plain_key)
        cfg = cfg.model_copy(update={"api_key_ref": ref})
        online = self._config.translation.online.model_copy(update={"primary": cfg})
        translation = self._config.translation.model_copy(update={"online": online})
        self._config = self._config.model_copy(update={"translation": translation})

    def _on_variant_change(self, variant_value: str) -> None:
        # Coerce to the enum so downstream consumers (provider, downloader)
        # don't trip on a raw string from ``model_copy``.
        from infini_transeon.config.schema import MadladVariant

        madlad = self._config.translation.local.madlad.model_copy(
            update={"variant": MadladVariant(variant_value)}
        )
        local = self._config.translation.local.model_copy(update={"madlad": madlad})
        translation = self._config.translation.model_copy(update={"local": local})
        self._config = self._config.model_copy(update={"translation": translation})

    def _collect_full_config(self) -> AppConfig:
        """Merge every tab's current widget state into ``self._config``."""
        form_cfg, plain_key = self._provider_form.snapshot()
        # If the user typed a new key but didn't click "Save", persist it on OK.
        ref = self._config.translation.online.primary.api_key_ref
        if plain_key:
            ref = secrets.set_secret("online.primary", plain_key)
            self._provider_form.clear_plain_key()
        form_cfg = form_cfg.model_copy(update={"api_key_ref": ref})
        online = self._config.translation.online.model_copy(
            update={
                "primary": form_cfg,
                "fallback_to_local": bool(self._fallback_to_local.isChecked()),
                "last_resort_mymemory": bool(self._last_resort_mymemory.isChecked()),
            }
        )

        # MADLAD decode-quality knobs from the local-models panel.
        # Merge on top of the current madlad config (variant is owned by
        # the panel's variant radios and is already applied to
        # ``self._config`` via ``_on_variant_change``).
        madlad = self._config.translation.local.madlad.model_copy(
            update=self._local_panel.snapshot_quality()
        )
        local = self._config.translation.local.model_copy(update={"madlad": madlad})

        translation = self._config.translation.model_copy(
            update={
                "mode": self._mode.currentData(),
                "style": self._style.currentData(),
                "glossary": self._glossary.snapshot(),
                "online": online,
                "local": local,
                "cache_size": int(self._cache_size.value()),
            }
        )
        capture = self._config.capture.model_copy(
            update={
                "ocr_interval_seconds": float(self._ocr_interval.value()),
            }
        )
        ocr = self._config.ocr.model_copy(
            update={
                "det_max_side": int(self._det_max_side.value()),
                "det_tier": self._det_tier.currentData() or "server",
                "min_confidence": float(self._min_conf.value()),
                "postcorrect_min_confidence": float(self._postcorrect_min_conf.value()),
                "enable_postcorrect": bool(self._enable_postcorrect.isChecked()),
                "enable_preprocess": bool(self._enable_preprocess.isChecked()),
            }
        )
        updates = self._config.updates.model_copy(
            update={
                "auto_check": bool(self._updates_auto_check.isChecked()),
                "auto_apply": bool(self._updates_auto_apply.isChecked()),
                "check_interval_hours": int(self._updates_interval.value()),
                "channel": self._updates_channel.currentData() or "stable",
            }
        )
        return self._config.model_copy(
            update={
                "translation": translation,
                "capture": capture,
                "ocr": ocr,
                "updates": updates,
                "hotkeys": self._hotkeys.snapshot(),
                "ui_language": self._ui_language.currentData() or "auto",
                "theme": self._theme.currentData() or "auto",
                "overlay": self._overlay.snapshot(self._config.overlay),
            }
        )

    def _emit_full_save(self) -> None:
        self._config = self._collect_full_config()
        self.saved.emit(self._config)

    def _save_and_close(self) -> None:
        self._emit_full_save()
        self.accept()


__all__ = ["SettingsDialog"]
