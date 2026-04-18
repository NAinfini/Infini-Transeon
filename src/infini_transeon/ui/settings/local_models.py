"""Local models settings pane — MADLAD-400 variant picker + download.

Single multilingual model, no per-pair downloads. User picks **3B** (default,
fits 4 GB GPUs) or **7B** (opt-in, needs 8 GB VRAM). Each variant has its own
download/delete flow; switching the variant reloads the provider.
"""

from __future__ import annotations

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QButtonGroup,
    QFormLayout,
    QFrame,
    QHBoxLayout,
    QLabel,
    QMessageBox,
    QProgressBar,
    QPushButton,
    QRadioButton,
    QSpinBox,
    QVBoxLayout,
    QWidget,
)

from infini_transeon.config.languages import LanguageRegistry
from infini_transeon.config.schema import AppConfig, MadladVariant
from infini_transeon.models.downloader import (
    ModelDownloadError,
    ModelNotAvailable,
    delete_madlad,
    ensure_madlad,
    is_madlad_downloaded,
    madlad_size_bytes,
)
from infini_transeon.utils.i18n import tr

from PySide6.QtCore import QThread


def _fmt_size(size_bytes: int) -> str:
    if size_bytes <= 0:
        return "—"
    value = float(size_bytes)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024.0:
            return f"{value:.1f} {unit}"
        value /= 1024.0
    return f"{value:.1f} TB"


class _MadladDownloadThread(QThread):
    progress = Signal(str, float)
    progress_bytes = Signal(int, int, float)  # done, total, ratio
    finished_ok = Signal(str)
    finished_err = Signal(str)

    def __init__(self, variant: MadladVariant) -> None:
        super().__init__()
        self._variant = variant

    def run(self) -> None:
        try:
            path = ensure_madlad(
                self._variant,
                progress=lambda msg, ratio: self.progress.emit(msg, ratio),
                bytes_progress=lambda done, total, ratio: self.progress_bytes.emit(
                    done, total, ratio
                ),
            )
            self.finished_ok.emit(str(path))
        except (ModelDownloadError, ModelNotAvailable) as exc:
            self.finished_err.emit(str(exc))


class LocalModelsPanel(QWidget):
    """Settings → Local Models."""

    request_variant_change = Signal(str)  # "3b" / "7b"

    def __init__(self, registry: LanguageRegistry, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._registry = registry
        self._thread: _MadladDownloadThread | None = None
        self._selected_variant = MadladVariant.v3b
        self._build_ui()
        self._refresh_status()

    # --- UI -----------------------------------------------------------

    def _build_ui(self) -> None:
        root = QVBoxLayout(self)
        root.setContentsMargins(12, 12, 12, 12)
        root.setSpacing(12)

        card = QFrame()
        card.setObjectName("card")
        layout = QVBoxLayout(card)
        layout.setContentsMargins(16, 14, 16, 14)
        layout.setSpacing(10)

        title = QLabel(tr("local.madlad.title"))
        title.setObjectName("h2")
        layout.addWidget(title)

        hint = QLabel(tr("local.madlad.hint"))
        hint.setObjectName("muted")
        hint.setWordWrap(True)
        layout.addWidget(hint)

        variant_row = QHBoxLayout()
        variant_row.setSpacing(16)
        self._variant_group = QButtonGroup(self)
        self._radio_3b = QRadioButton(tr("local.madlad.variant.3b"))
        self._radio_3b.setToolTip(tr("local.madlad.variant.3b.tip"))
        self._radio_7b = QRadioButton(tr("local.madlad.variant.7b"))
        self._radio_7b.setToolTip(tr("local.madlad.variant.7b.tip"))
        self._variant_group.addButton(self._radio_3b)
        self._variant_group.addButton(self._radio_7b)
        self._radio_3b.toggled.connect(self._on_variant_toggled)
        self._radio_7b.toggled.connect(self._on_variant_toggled)
        variant_row.addWidget(self._radio_3b)
        variant_row.addWidget(self._radio_7b)
        variant_row.addStretch(1)
        layout.addLayout(variant_row)

        self._status = QLabel("")
        self._status.setObjectName("muted")
        self._status.setWordWrap(True)
        layout.addWidget(self._status)

        self._progress = QProgressBar()
        self._progress.setRange(0, 100)
        self._progress.setVisible(False)
        layout.addWidget(self._progress)

        button_row = QHBoxLayout()
        button_row.setSpacing(10)
        self._download_btn = QPushButton(tr("local.madlad.btn.download"))
        self._download_btn.setObjectName("primary")
        self._download_btn.setToolTip(tr("local.madlad.btn.download.tip"))
        self._download_btn.clicked.connect(self._on_download_clicked)
        button_row.addWidget(self._download_btn)

        self._delete_btn = QPushButton(tr("local.btn.delete"))
        self._delete_btn.setObjectName("danger")
        self._delete_btn.setToolTip(tr("local.madlad.delete.tip"))
        self._delete_btn.clicked.connect(self._on_delete_clicked)
        button_row.addWidget(self._delete_btn)
        button_row.addStretch(1)
        layout.addLayout(button_row)

        # --- MADLAD decode-quality knobs ---------------------------------
        # Exposed because the right value depends on the user's hardware
        # (beam 5 on 7B + CUDA is real quality, beam 1 on CPU is tolerable
        # latency) and on what they're translating (short UI fragments vs.
        # long novel paragraphs). We can't pick a single default that suits
        # every combination, so: show the numbers and explain them.
        quality_title = QLabel(tr("local.madlad.quality.title"))
        quality_title.setObjectName("h3")
        layout.addWidget(quality_title)

        quality_form = QFormLayout()
        quality_form.setLabelAlignment(Qt.AlignmentFlag.AlignLeft)
        quality_form.setFormAlignment(Qt.AlignmentFlag.AlignTop)

        self._beam_size = QSpinBox()
        self._beam_size.setRange(1, 8)
        self._beam_size.setSingleStep(1)
        quality_form.addRow(tr("local.madlad.beam_size.label"), self._beam_size)
        beam_hint = QLabel(tr("local.madlad.beam_size.hint"))
        beam_hint.setObjectName("muted")
        beam_hint.setWordWrap(True)
        quality_form.addRow("", beam_hint)

        self._max_decoding_length = QSpinBox()
        self._max_decoding_length.setRange(32, 1024)
        self._max_decoding_length.setSingleStep(16)
        self._max_decoding_length.setSuffix(" tok")
        quality_form.addRow(
            tr("local.madlad.max_decoding_length.label"),
            self._max_decoding_length,
        )
        mdl_hint = QLabel(tr("local.madlad.max_decoding_length.hint"))
        mdl_hint.setObjectName("muted")
        mdl_hint.setWordWrap(True)
        quality_form.addRow("", mdl_hint)

        self._max_batch_size = QSpinBox()
        self._max_batch_size.setRange(1, 64)
        self._max_batch_size.setSingleStep(1)
        quality_form.addRow(
            tr("local.madlad.max_batch_size.label"),
            self._max_batch_size,
        )
        mbs_hint = QLabel(tr("local.madlad.max_batch_size.hint"))
        mbs_hint.setObjectName("muted")
        mbs_hint.setWordWrap(True)
        quality_form.addRow("", mbs_hint)

        layout.addLayout(quality_form)

        root.addWidget(card)
        root.addStretch(1)

    # --- data ---------------------------------------------------------

    def load(self, config: AppConfig) -> None:
        variant = config.translation.local.madlad.variant
        self._selected_variant = variant
        self._radio_3b.blockSignals(True)
        self._radio_7b.blockSignals(True)
        self._radio_3b.setChecked(variant == MadladVariant.v3b)
        self._radio_7b.setChecked(variant == MadladVariant.v7b)
        self._radio_3b.blockSignals(False)
        self._radio_7b.blockSignals(False)
        # Quality knobs
        madlad = config.translation.local.madlad
        self._beam_size.setValue(int(madlad.beam_size))
        self._max_decoding_length.setValue(int(madlad.max_decoding_length))
        self._max_batch_size.setValue(int(madlad.max_batch_size))
        self._refresh_status()

    def snapshot_quality(self) -> dict:
        """Return the user-edited MADLAD decode-quality knobs.

        The variant itself still flows through ``request_variant_change``
        because changing variant reloads the provider — callers must pass
        these three values back through the normal settings-apply flow.
        """
        return {
            "beam_size": int(self._beam_size.value()),
            "max_decoding_length": int(self._max_decoding_length.value()),
            "max_batch_size": int(self._max_batch_size.value()),
        }

    def _refresh_status(self) -> None:
        variant = self._selected_variant
        downloaded = is_madlad_downloaded(variant)
        busy = self._thread is not None and self._thread.isRunning()
        if downloaded:
            size = _fmt_size(madlad_size_bytes(variant))
            self._status.setText(tr("local.madlad.installed", size=size))
            self._download_btn.setEnabled(False)
        else:
            self._status.setText(tr("local.madlad.not_installed"))
            self._download_btn.setEnabled(not busy)
        self._delete_btn.setEnabled(downloaded and not busy)
        self._radio_3b.setEnabled(not busy)
        self._radio_7b.setEnabled(not busy)

    # --- handlers -----------------------------------------------------

    def _on_variant_toggled(self, checked: bool) -> None:
        if not checked:
            return
        variant = MadladVariant.v3b if self._radio_3b.isChecked() else MadladVariant.v7b
        if variant == self._selected_variant:
            return
        self._selected_variant = variant
        self.request_variant_change.emit(variant.value)
        self._refresh_status()

    def _on_download_clicked(self) -> None:
        if self._thread is not None and self._thread.isRunning():
            return
        variant = self._selected_variant
        if is_madlad_downloaded(variant):
            self._refresh_status()
            return
        self._progress.setValue(0)
        self._progress.setVisible(True)
        self._status.setText(tr("local.madlad.downloading"))
        self._download_btn.setEnabled(False)
        self._delete_btn.setEnabled(False)
        self._radio_3b.setEnabled(False)
        self._radio_7b.setEnabled(False)
        thread = _MadladDownloadThread(variant)
        thread.progress.connect(self._on_progress)
        thread.progress_bytes.connect(self._on_progress_bytes)
        thread.finished_ok.connect(self._on_download_ok)
        thread.finished_err.connect(self._on_download_err)
        thread.start()
        self._thread = thread

    def _on_progress(self, message: str, ratio: float) -> None:
        # Fallback when the bytes signal hasn't fired yet (no HF metadata):
        # the English message at least confirms something's happening.
        if not self._status.text() or "/" not in self._status.text():
            self._status.setText(message)
        self._progress.setValue(int(max(0.0, min(1.0, ratio)) * 100))

    def _on_progress_bytes(self, done: int, total: int, ratio: float) -> None:
        gb_done = done / (1024**3)
        gb_total = total / (1024**3) if total else 0.0
        self._status.setText(
            tr("local.madlad.progress", done=f"{gb_done:.2f}", total=f"{gb_total:.2f}")
        )
        self._progress.setValue(int(max(0.0, min(1.0, ratio)) * 100))

    def _on_download_ok(self, path: str) -> None:
        self._progress.setVisible(False)
        self._thread = None
        self._refresh_status()

    def _on_download_err(self, message: str) -> None:
        self._progress.setVisible(False)
        self._thread = None
        self._status.setText(tr("local.madlad.failed", message=message))
        QMessageBox.warning(self, tr("local.download_failed.title"), message)
        self._refresh_status()

    def _on_delete_clicked(self) -> None:
        variant = self._selected_variant
        if not is_madlad_downloaded(variant):
            return
        choice = QMessageBox.question(
            self,
            tr("local.delete.title"),
            tr("local.madlad.delete.confirm"),
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
        )
        if choice != QMessageBox.StandardButton.Yes:
            return
        delete_madlad(variant)
        self._refresh_status()


__all__ = ["LocalModelsPanel"]
