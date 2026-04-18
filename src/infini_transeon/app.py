"""Top-level Qt application wiring."""

from __future__ import annotations

from PySide6.QtCore import Q_ARG, Qt, QMetaObject, QTimer
from PySide6.QtWidgets import QApplication, QMessageBox

from infini_transeon.capture import factory as capture_factory
from infini_transeon.capture.base import CaptureBackend, Rect, RegionInfo, WindowInfo
from infini_transeon.config import loader as config_loader
from infini_transeon.config import paths
from infini_transeon.config.languages import LanguageRegistry
from infini_transeon.config.schema import AppConfig
from infini_transeon.ocr.base import OcrEngine
from infini_transeon.ocr.rapid import RapidOcrEngine
from infini_transeon.overlay.window import OverlayWindow
from infini_transeon.pipeline.events import PipelineSignals
from infini_transeon.pipeline.orchestrator import Pipeline
from infini_transeon.platform.hotkey import HotkeyManager
from infini_transeon.platform.permissions import check_capture_permission
from infini_transeon.translate.cache import TranslationMemory
from infini_transeon.translate.router import Router
from infini_transeon.translate.usage import UsageTracker
from infini_transeon.ui.first_run import FirstRunWizard
from infini_transeon.ui.log_panel import LogPanel
from infini_transeon.ui.main_window import MainWindow
from infini_transeon.ui.region_picker import RegionPicker
from infini_transeon.ui.region_editor import RegionEditor
from infini_transeon.ui.settings.dialog import SettingsDialog
from infini_transeon.ui.style import build_stylesheet, resolve_theme
from infini_transeon.ui.window_picker import WindowPicker
from infini_transeon.updater.tufup_client import UpdateClient
from infini_transeon.utils.i18n import set_locale, tr
from infini_transeon.utils.logging import logger, setup_logging
from infini_transeon.utils.resource_governor import (
    ResourceDecision,
    ResourceGovernor,
    ResourceLevel,
)


def _resolve_ui_locale(code: str) -> str:
    if code and code != "auto":
        return code
    import locale as _locale

    try:
        sys_locale, _ = _locale.getlocale()
    except Exception:  # noqa: BLE001
        sys_locale = None
    if not sys_locale:
        return "en"
    tag = sys_locale.replace("_", "-").lower()
    # Map common Windows locales onto our supported codes.
    if tag.startswith("zh-tw") or tag.startswith("zh-hant") or tag.startswith("zh-hk"):
        return "zh-TW"
    if tag.startswith("zh"):
        return "zh"
    primary = tag.split("-", 1)[0]
    return primary if primary in {
        "en", "ja", "ko", "es", "fr", "de", "ru", "pt"
    } else "en"


def _build_ocr_engine(ocr_config) -> OcrEngine:  # type: ignore[no-untyped-def]
    """Construct the RapidOCR engine (PP-OCRv5 server).

    RapidOCR 3.x ships PP-OCRv5 weights as ONNX — much lower RSS, no MKLDNN
    crash path, and pure wheels for easier PyInstaller bundling. No Paddle
    fallback: if RapidOCR init fails the error surfaces so the user can
    reinstall / check the model cache.
    """
    return RapidOcrEngine(ocr_config)


class Application:
    """Main application controller."""

    def __init__(self, qt_app: QApplication) -> None:
        self._qt = qt_app
        qt_app.setApplicationName(paths.APP_NAME)
        qt_app.setQuitOnLastWindowClosed(False)
        qt_app.setAttribute(Qt.ApplicationAttribute.AA_DontShowIconsInMenus, False)
        # Fusion draws controls (radios, checkboxes, dropdown arrows) consistently
        # across themes. Without it Windows falls back to the native style, which
        # renders as unstyled black/gray boxes on our dark palette.
        try:
            from PySide6.QtWidgets import QStyleFactory

            fusion = QStyleFactory.create("Fusion")
            if fusion is not None:
                qt_app.setStyle(fusion)
        except Exception:  # noqa: BLE001
            pass

        self._config = config_loader.load()
        self._registry = LanguageRegistry.load()
        set_locale(_resolve_ui_locale(self._config.ui_language))
        self._apply_theme()

        # Follow OS theme changes in 'auto' mode.
        try:
            qt_app.styleHints().colorSchemeChanged.connect(self._on_system_theme_changed)
        except Exception:  # noqa: BLE001
            pass

        permission = check_capture_permission()
        if not permission.ok:
            QMessageBox.warning(
                None,
                tr("common.permission_title"),
                permission.message,
            )

        self._capture: CaptureBackend = capture_factory.create()
        self._ocr: OcrEngine = _build_ocr_engine(self._config.ocr)
        self._tm = TranslationMemory()
        self._usage = UsageTracker()
        self._router = Router(self._config, tm=self._tm, usage=self._usage)
        self._signals = PipelineSignals()
        self._pipeline = Pipeline(
            capture=self._capture,
            ocr=self._ocr,
            router=self._router,
            config=self._config,
            signals=self._signals,
        )

        self._overlay = OverlayWindow(self._config.overlay)
        self._window = MainWindow(self._registry)
        self._window.set_languages(
            self._config.translation.source_lang, self._config.translation.target_lang
        )
        self._window.set_capture_mode(self._config.capture.mode)
        self._window.set_device_info(self._describe_translator_device())
        self._hotkeys = HotkeyManager()
        self._updater = UpdateClient()
        self._wizard: FirstRunWizard | None = None
        self._log_panel: LogPanel | None = None
        self._region_picker: RegionPicker | None = None
        self._region_editor: RegionEditor | None = None
        self._main_window_placed = False

        self._running = False
        # Governor's restore target for degraded runtime. Must be
        # initialized before ``_wire``/wizard because ``_commit_config``
        # touches it.
        self._baseline_config: AppConfig | None = None
        self._wire()

        if not self._config.first_run_completed:
            self._run_first_run_wizard()
        else:
            self._show_main_window()

        if self._config.updates.auto_check:
            QTimer.singleShot(3000, self._background_update_check)

        # Kick off engine warmup (OCR + local translator) on a worker so
        # the UI doesn't freeze on the first tick. Surfaces a "Loading
        # translator model…" pill until *both* engines are ready — OCR
        # (~60-90s ONNX graph init) and MADLAD (load + first-call CUDA
        # kernel JIT).
        self._warmup_engines()

        # Soft resource governor: samples CPU% / RSS / VRAM every 5s and
        # applies progressively lighter pipeline settings when the
        # process is pressuring the host, then restores user settings
        # when pressure clears. Never kills the process — visible
        # degradation beats silent OOM.
        self._governor = ResourceGovernor(on_decision=self._apply_governor_decision)
        self._governor_timer = QTimer(self._qt)
        self._governor_timer.setInterval(5000)
        self._governor_timer.timeout.connect(self._governor.tick)
        self._governor_timer.start()

    # --- wiring -------------------------------------------------------

    def _wire(self) -> None:
        self._signals.items_ready.connect(self._on_items_ready)
        self._signals.window_moved.connect(self._overlay.move_to)
        self._signals.target_changed.connect(self._on_target_changed)
        self._signals.degraded.connect(self._on_degraded)
        self._signals.error.connect(self._on_pipeline_error)
        self._signals.status.connect(lambda s: self._window.show_status(s))

        self._window.pick_window.connect(self._pick_window)
        self._window.pick_region.connect(self._pick_region)
        self._window.toggle_running.connect(self._toggle_running)
        self._window.translate_now.connect(self._translate_now)
        self._window.capture_mode_changed.connect(self._on_capture_mode_changed)
        self._window.open_settings.connect(self._open_settings)
        self._window.retry_online.connect(self._retry_online)
        self._window.check_updates.connect(self._check_updates_now)
        self._window.open_logs.connect(self._open_logs)
        self._window.source_lang_changed.connect(self._on_source_lang_changed)
        self._window.target_lang_changed.connect(self._on_target_lang_changed)
        self._window.quit_requested.connect(self._quit)

        self._apply_hotkeys()

    def _apply_hotkeys(self) -> None:
        hk = self._config.hotkeys
        # Run the pynput callbacks on the UI thread.
        self._hotkeys.set_binding(hk.toggle, lambda: QTimer.singleShot(0, self._toggle_running))
        self._hotkeys.set_binding(hk.pick_window, lambda: QTimer.singleShot(0, self._pick_window))
        self._hotkeys.set_binding(hk.retry_online, lambda: QTimer.singleShot(0, self._retry_online))
        self._hotkeys.set_binding(hk.translate_now, lambda: QTimer.singleShot(0, self._translate_now))

    # --- actions ------------------------------------------------------

    def _describe_translator_device(self) -> str:
        """Human-readable summary for the main-window 'Translator' row.

        Shows the active provider. For local mode: which device MADLAD is
        on and its compute type. For online mode: the configured endpoint
        host (or a "not configured" hint when base_url is missing) — the
        GPU/MADLAD details are irrelevant unless/until the router falls
        back to local, so we don't pretend otherwise.
        """
        mode = str(self._config.translation.mode).rsplit(".", 1)[-1]
        if mode == "online":
            primary = self._config.translation.online.primary
            base_url = (primary.base_url or "").strip()
            if not base_url:
                return "Online · not configured"
            # Strip scheme + trailing path noise for a compact label.
            host = base_url.split("://", 1)[-1].split("/", 1)[0]
            return f"Online · {host}" if host else "Online"
        # Local mode: report MADLAD's actual device + compute type.
        local = getattr(self._router, "_local", None)
        active_device = getattr(local, "active_device", None) if local else None
        active_compute = getattr(local, "active_compute_type", None) if local else None
        try:
            import ctranslate2

            gpu_detected = ctranslate2.get_cuda_device_count() > 0
        except Exception:  # noqa: BLE001
            gpu_detected = False
        if active_device == "cuda":
            return f"MADLAD · GPU ({active_compute})"
        if active_device == "cpu":
            if gpu_detected:
                return "MADLAD · CPU (GPU detected but unused)"
            return "MADLAD · CPU (no GPU)"
        return f"MADLAD · {'GPU detected' if gpu_detected else 'CPU only'}"

    def _show_main_window(self) -> None:
        first_show = not self._main_window_placed
        self._window.show()
        if first_show:
            # Center once on the primary screen so the window can't spawn with
            # its titlebar clipped off the top of a multi-monitor setup. After
            # that, respect wherever the user has moved it.
            try:
                screen = self._qt.primaryScreen()
                if screen is not None:
                    geo = screen.availableGeometry()
                    frame = self._window.frameGeometry()
                    frame.moveCenter(geo.center())
                    self._window.move(frame.topLeft())
            except Exception:  # noqa: BLE001
                pass
            self._main_window_placed = True
        self._window.raise_()
        self._window.activateWindow()

    def _run_first_run_wizard(self) -> None:
        self._wizard = FirstRunWizard(self._config, registry=self._registry)
        self._wizard.completed.connect(self._commit_config)
        self._wizard.finished.connect(lambda _: self._show_main_window())
        self._wizard.show()
        self._wizard.raise_()
        self._wizard.activateWindow()

    def _pick_window(self) -> None:
        logger.info("pick_window: opening picker")
        picker = WindowPicker(self._capture)
        picker.selected.connect(self._on_window_selected)
        picker.exec()
        logger.info("pick_window: picker closed")

    def _pick_region(self) -> None:
        # Hide the main window + any overlay so the picker can paint over raw desktop.
        was_visible = self._window.isVisible()
        if was_visible:
            self._window.hide()
        overlay_visible = self._overlay.isVisible()
        if overlay_visible:
            self._overlay.hide()
        editor_visible = (
            self._region_editor is not None and self._region_editor.isVisible()
        )
        if editor_visible:
            self._region_editor.hide()

        picker = RegionPicker()

        def _restore() -> None:
            if was_visible:
                self._show_main_window()

        picker.selected.connect(self._on_region_selected)
        picker.selected.connect(lambda *_a: _restore())
        picker.cancelled.connect(_restore)
        if overlay_visible:
            # Let the overlay reappear only after region-selection completes;
            # on cancel we keep the previous target.
            picker.cancelled.connect(lambda: self._overlay.show())
        picker.show()
        picker.raise_()
        picker.activateWindow()
        self._region_picker = picker

    def _on_window_selected(self, info: WindowInfo) -> None:
        logger.info(
            "window selected: handle={} title='{}' app='{}' rect={}x{}+{}+{}",
            info.handle, info.title, info.app,
            info.rect.width, info.rect.height, info.rect.x, info.rect.y,
        )
        self._pipeline.set_target(info)
        self._overlay.move_to(info.rect)
        self._overlay.show()
        self._start()

    def _on_region_selected(self, rect: Rect, dpr: float = 1.0) -> None:
        label = f"{rect.width}×{rect.height} @ ({rect.x},{rect.y})"
        region = RegionInfo(rect=rect, label=label, dpr=float(dpr))
        self._pipeline.set_target(region)
        self._overlay.move_to(rect)
        self._overlay.show()
        self._show_region_editor(rect, float(dpr))
        self._start()

    def _show_region_editor(self, rect: Rect, dpr: float) -> None:
        if self._region_editor is None:
            self._region_editor = RegionEditor()
            self._region_editor.region_edited.connect(self._on_region_edited)
            self._region_editor.repick_requested.connect(self._pick_region)
        self._region_editor.set_region(rect, dpr)
        self._region_editor.show()
        self._region_editor.raise_()

    def _hide_region_editor(self) -> None:
        if self._region_editor is not None:
            self._region_editor.hide()

    def _on_region_edited(self, rect: Rect, dpr: float) -> None:
        # User dragged / resized the region frame — update the pipeline target
        # and move the overlay to match. Don't emit a target-changed notification
        # pop-up for each nudge; it'd be spammy.
        label = f"{rect.width}×{rect.height} @ ({rect.x},{rect.y})"
        region = RegionInfo(rect=rect, label=label, dpr=float(dpr))
        self._pipeline.set_target(region)
        self._overlay.move_to(rect)

    def _on_capture_mode_changed(self, mode: str) -> None:
        # Clear any prior target — window-mode target and region-mode target
        # are not interchangeable. User has to re-pick in the new mode.
        self._pipeline.set_target(None)
        self._hide_region_editor()
        capture = self._config.capture.model_copy(update={"mode": mode})
        self._commit_config(self._config.model_copy(update={"capture": capture}))

    def _translate_now(self) -> None:
        self._pipeline.trigger_once()

    def _on_target_changed(self, handle: int, title: str) -> None:
        if handle != 0:
            self._window.set_target(title)
            self._window.notify(tr("main.notify.target_selected"), title)
        else:
            self._window.set_target("")
            self._overlay.clear()
            self._overlay.hide()
            self._hide_region_editor()

    def _on_degraded(self, degraded: bool) -> None:
        self._window.set_running_state(running=self._running, degraded=degraded)
        if degraded:
            self._window.notify(
                tr("main.notify.degraded_title"),
                tr("main.notify.degraded_body"),
            )

    def _on_pipeline_error(self, message: str) -> None:
        self._window.notify(tr("main.notify.error_title"), message)

    def _on_items_ready(self, items) -> None:
        self._overlay.set_items(items)

    def _toggle_running(self) -> None:
        if self._running:
            self._pipeline.stop()
            self._running = False
            # Stop means "put the overlay away entirely": hide the bubbles,
            # hide the region-editor frame, drop the selected target. The
            # user has to re-pick a window/region before they can start
            # translating again.
            self._overlay.clear()
            self._overlay.hide()
            self._hide_region_editor()
            self._pipeline.set_target(None)
            self._window.set_target("")
        else:
            self._pipeline.start()
            self._running = True
        self._window.set_running_state(
            running=self._running, degraded=self._router.state.degraded
        )

    def _start(self) -> None:
        if not self._running:
            self._pipeline.start()
            self._running = True
            self._window.set_running_state(running=True, degraded=False)

    def _retry_online(self) -> None:
        self._router.retry_online()
        self._window.set_running_state(running=self._running, degraded=False)

    def _open_logs(self) -> None:
        if self._log_panel is None or not self._log_panel.isVisible():
            self._log_panel = LogPanel(parent=self._window)
            self._log_panel.show()
        else:
            self._log_panel.raise_()
            self._log_panel.activateWindow()

    def _on_source_lang_changed(self, code: str) -> None:
        translation = self._config.translation.model_copy(update={"source_lang": code})
        self._commit_config(self._config.model_copy(update={"translation": translation}))

    def _on_target_lang_changed(self, code: str) -> None:
        translation = self._config.translation.model_copy(update={"target_lang": code})
        self._commit_config(self._config.model_copy(update={"translation": translation}))

    def _open_settings(self) -> None:
        dialog = SettingsDialog(
            self._config, registry=self._registry, usage=self._usage, parent=self._window
        )
        dialog.saved.connect(self._commit_config)
        dialog.check_updates_requested.connect(self._check_updates_now)
        dialog.exec()

    def _commit_config(self, config: AppConfig) -> None:
        prev_det_max_side = self._config.ocr.det_max_side
        prev_det_tier = self._config.ocr.det_tier
        self._config = config
        # Keep the governor's "original" in sync with what the user
        # actually saved. Otherwise a later restore after pressure
        # would revive the pre-save settings and silently undo the
        # user's edit.
        if self._baseline_config is not None:
            self._baseline_config = config
        config_loader.save(config)
        set_locale(_resolve_ui_locale(config.ui_language))
        self._apply_theme()
        self._router.reload(config)
        self._pipeline.update_config(config)
        self._overlay.apply_config(config.overlay)
        self._apply_hotkeys()
        if (
            config.ocr.det_max_side != prev_det_max_side
            or config.ocr.det_tier != prev_det_tier
        ):
            # Detector input size / tier are baked into RapidOCR's ONNX
            # session at init. Rebuilding is the only way the new settings
            # take effect.
            self._rebuild_ocr_engine()
        self._window.set_languages(
            config.translation.source_lang, config.translation.target_lang
        )
        self._window.set_capture_mode(config.capture.mode)
        # Refresh the status-card device row so "MADLAD/Online" + GPU/CPU
        # reflects the config the user just saved. Otherwise the label stays
        # frozen at whatever was set during __init__ and silently lies about
        # which provider is actually handling requests.
        self._window.set_device_info(self._describe_translator_device())

    def _apply_governor_decision(self, decision: ResourceDecision) -> None:
        """Apply a transient pipeline slowdown from the resource governor.

        Does NOT persist to disk and does NOT rebuild the OCR engine:
        only touches fields that Router/Pipeline read per-call
        (``capture.ocr_interval_seconds`` and MADLAD
        ``beam_size`` / ``max_batch_size``). The *user's* saved config
        is cached in ``self._baseline_config`` on the first degradation
        and restored when the level returns to ``normal``.
        """
        if decision.level is ResourceLevel.normal:
            if self._baseline_config is None:
                return
            restored = self._baseline_config
            self._baseline_config = None
            logger.info("resource governor: restoring user settings")
            self._apply_runtime_config(restored)
            self._window.show_status(tr("governor.restored"))
            return

        if self._baseline_config is None:
            self._baseline_config = self._config

        base = self._baseline_config
        # Cap (not override): pick min(user_value, governor_cap) so
        # users who already run lean don't get accidentally raised.
        new_madlad = base.translation.local.madlad.model_copy(
            update={
                "beam_size": (
                    decision.beam_size_cap
                    if decision.beam_size_cap is not None
                    and decision.beam_size_cap < base.translation.local.madlad.beam_size
                    else base.translation.local.madlad.beam_size
                ),
                "max_batch_size": (
                    decision.max_batch_size_cap
                    if decision.max_batch_size_cap is not None
                    and decision.max_batch_size_cap
                    < base.translation.local.madlad.max_batch_size
                    else base.translation.local.madlad.max_batch_size
                ),
            }
        )
        new_local = base.translation.local.model_copy(update={"madlad": new_madlad})
        new_translation = base.translation.model_copy(update={"local": new_local})
        scaled_interval = min(
            30.0, base.capture.ocr_interval_seconds * decision.ocr_interval_multiplier
        )
        new_capture = base.capture.model_copy(
            update={"ocr_interval_seconds": scaled_interval}
        )
        degraded = base.model_copy(
            update={"translation": new_translation, "capture": new_capture}
        )
        logger.warning(
            "resource governor: {} — reducing pipeline load ({})",
            decision.level.value,
            decision.reason,
        )
        self._apply_runtime_config(degraded)
        self._window.show_status(
            tr("governor.degraded", level=decision.level.value, reason=decision.reason)
        )

    def _apply_runtime_config(self, config: AppConfig) -> None:
        """Push ``config`` into Router + Pipeline without saving to disk.

        Used for transient governor-driven changes. Intentionally skips
        ``config_loader.save`` (would pollute user config), detector
        rebuild (too slow under load), and UI refreshes unrelated to
        the changed fields. Router's ``reload`` preserves the warmed-up
        MADLAD translator as long as variant/device/compute_type are
        unchanged, and pushes the new decode knobs into it.
        """
        self._config = config
        self._router.reload(config)
        self._pipeline.update_config(config)

    def _apply_theme(self) -> None:
        theme = resolve_theme(self._config.theme)
        self._qt.setStyleSheet(build_stylesheet(theme))

    def _rebuild_ocr_engine(self) -> None:
        """Tear down the RapidOCR engine and create a fresh one.

        Needed when a parameter baked into the ONNX session at init time
        (``det_max_side``, ``det_tier``) changes. The next recognize() call
        will lazy-load the new engine, which re-runs the ~30-90s ONNX graph
        init.
        """
        try:
            self._ocr.close()
        except Exception:  # noqa: BLE001 - best-effort
            logger.debug("old ocr engine close failed", exc_info=True)
        self._ocr = _build_ocr_engine(self._config.ocr)
        self._pipeline.replace_ocr(self._ocr)
        import threading
        threading.Thread(
            target=self._ocr.warmup, name="OcrWarmup", daemon=True
        ).start()

    def _warmup_engines(self) -> None:
        """Load OCR + local translator on worker threads at startup.

        Both engines have a large first-call cost (RapidOCR ~60-90s ONNX
        graph init; MADLAD 7B ~10-60s load plus CUDA kernel JIT on the
        first translate). Running them off-thread and pinning a
        ``main.status.loading_model`` label in the status pill lets the
        user pick a window / tweak settings while the cold path pays its
        cost. The label is only cleared after *both* warmups finish, so
        the pill never shows "运行中" while the first tick is still
        blocked on engine init.

        MADLAD warmup is skipped when there is no local provider or the
        model isn't on disk yet; OCR warmup always runs.
        """
        self._window.set_loading(tr("main.status.loading_model"))

        local = getattr(self._router, "_local", None)
        ensure_translator = getattr(local, "_ensure_loaded", None) if local else None

        def _clear_label() -> None:
            QMetaObject.invokeMethod(
                self._window,
                "set_loading",
                Qt.ConnectionType.QueuedConnection,
                Q_ARG(str, ""),
            )

        def _coordinator() -> None:
            import threading

            def _ocr_warmup() -> None:
                try:
                    self._ocr.warmup()
                except Exception as exc:  # noqa: BLE001 - surface, don't crash UI
                    logger.warning("OCR warmup failed: {}", exc)

            def _translator_warmup() -> None:
                if ensure_translator is None:
                    return
                try:
                    ensure_translator()
                    # Pay the CUDA kernel JIT cost now with a dummy
                    # translate — otherwise the first real tick still
                    # blocks several seconds after the model is "loaded".
                    self._prime_local_translator()
                except Exception as exc:  # noqa: BLE001
                    logger.warning("translator warmup failed: {}", exc)

            threads = [
                threading.Thread(target=_ocr_warmup, name="OcrWarmup", daemon=True),
                threading.Thread(
                    target=_translator_warmup, name="TranslatorWarmup", daemon=True
                ),
            ]
            for t in threads:
                t.start()
            for t in threads:
                t.join()
            _clear_label()

        import threading
        threading.Thread(target=_coordinator, name="EnginesWarmup", daemon=True).start()

    def _prime_local_translator(self) -> None:
        """Run a tiny translate() so CUDA kernels are JIT-compiled now.

        Without this, the MADLAD model is "loaded" but the first real
        translate() still pays 3-10s of kernel compilation on CUDA, which
        stalls the first pipeline tick after the loading label is
        cleared. Uses a throwaway request with the user's configured
        languages so the primed kernels match real traffic.
        """
        from infini_transeon.translate.base import TranslationRequest

        local = getattr(self._router, "_local", None)
        if local is None:
            return
        translate = getattr(local, "translate", None)
        if translate is None:
            return
        req = TranslationRequest(
            texts=("warmup",),
            source_lang=self._config.translation.source_lang,
            target_lang=self._config.translation.target_lang,
            style=getattr(
                self._config.translation.style,
                "value",
                self._config.translation.style,
            ),
            glossary=(),
        )
        translate(req)

    def _on_system_theme_changed(self, *_args) -> None:
        if self._config.theme == "auto":
            self._apply_theme()

    def _check_updates_now(self) -> None:
        result = self._updater.check()
        if result.available:
            self._window.notify(
                tr("main.notify.update_available_title"),
                tr("main.notify.update_available_body", version=result.latest_version),
            )
        elif result.error:
            self._window.notify(tr("main.notify.update_failed_title"), result.error)
        else:
            self._window.notify(
                tr("main.notify.up_to_date_title"),
                tr("main.notify.up_to_date_body", version=result.current_version),
            )

    def _background_update_check(self) -> None:
        logger.info("running background update check")
        self._check_updates_now()

    def _quit(self) -> None:
        logger.info("shutting down")
        self._pipeline.stop()
        self._router.close()
        self._hotkeys.stop()
        self._ocr.close()
        self._capture.close()
        self._overlay.close()
        if self._region_editor is not None:
            self._region_editor.close()
        self._qt.quit()


def run(argv: list[str]) -> int:
    setup_logging()
    logger.info("Infini-Transeon starting")
    _log_gpu_status()
    _ensure_dpi_awareness()
    app = QApplication(argv)
    _log_screen_topology()
    Application(app)
    return app.exec()


def _log_gpu_status() -> None:
    """One-shot log line that tells the user exactly what translation
    device they'll get. Mirrored into the main-window status bar so the
    user doesn't have to open the log panel to find out."""
    try:
        import ctranslate2

        device_count = ctranslate2.get_cuda_device_count()
    except Exception as exc:  # noqa: BLE001
        logger.info("GPU: detection failed ({}) — using CPU for translation", exc)
        return
    if device_count <= 0:
        logger.info("GPU: no CUDA device detected — translation will run on CPU")
        return
    # Probe the DLL stack by attempting a tiny no-op allocator touch. CT2 4.x
    # doesn't expose a direct "is this usable" probe, so the cleanest signal
    # is "we saw a device and the nvidia-cublas / cudnn wheels are present".
    from pathlib import Path
    import sysconfig

    nvidia_root = Path(sysconfig.get_paths()["purelib"]) / "nvidia"
    have_cublas = (nvidia_root / "cublas" / "bin").is_dir()
    have_cudnn = (nvidia_root / "cudnn" / "bin").is_dir()
    if have_cublas and have_cudnn:
        logger.info(
            "GPU: CUDA device detected (x{}) with bundled cuBLAS/cuDNN — "
            "MADLAD will translate on GPU",
            device_count,
        )
    else:
        logger.warning(
            "GPU: CUDA device detected (x{}) but cuBLAS/cuDNN wheels missing "
            "from site-packages — MADLAD will fall back to CPU on first load. "
            "Install with: pip install nvidia-cublas-cu12 nvidia-cudnn-cu12",
            device_count,
        )


def _log_screen_topology() -> None:
    from PySide6.QtGui import QGuiApplication

    for i, s in enumerate(QGuiApplication.screens()):
        g = s.geometry()
        logger.info(
            "screen[{}] name={!r} logical={}x{}+{}+{} dpr={} phys_dpi={}",
            i, s.name(), g.width(), g.height(), g.x(), g.y(),
            s.devicePixelRatio(), s.physicalDotsPerInch(),
        )


def _ensure_dpi_awareness() -> None:
    """Force Per-Monitor-V2 DPI awareness on Windows before Qt starts.

    Without this, when launched from the Python interpreter (no manifest),
    the process is System-DPI-aware: Windows virtualizes coordinates and
    bitmap-stretches the UI on secondary monitors with a different scale.
    Qt then reports logical coords that don't match what ``mss`` sees at
    the GDI layer, so a region picked at 1.75x ends up 1.75x too big when
    captured. PerMonitorV2 makes every layer agree on physical pixels.

    No-op on non-Windows; PyInstaller-bundled builds already declare this
    in their manifest.
    """
    import sys

    if sys.platform != "win32":
        return
    try:
        import ctypes

        user32 = ctypes.windll.user32
        # -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        set_ctx = getattr(user32, "SetProcessDpiAwarenessContext", None)
        if set_ctx is not None:
            set_ctx(ctypes.c_void_p(-4))
            return
        # Win 8.1 fallback: 2 = PROCESS_PER_MONITOR_DPI_AWARE
        shcore = ctypes.windll.shcore
        shcore.SetProcessDpiAwareness(2)
    except Exception as exc:  # noqa: BLE001 - best-effort, already-set is fine
        logger.debug("dpi awareness setup skipped: {}", exc)


__all__ = ["run", "Application"]
