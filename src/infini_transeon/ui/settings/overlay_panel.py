"""Overlay appearance + behaviour controls."""

from __future__ import annotations

from PySide6.QtCore import Qt
from PySide6.QtGui import QColor
from PySide6.QtWidgets import (
    QCheckBox,
    QColorDialog,
    QComboBox,
    QDoubleSpinBox,
    QFormLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSlider,
    QSpinBox,
    QVBoxLayout,
    QWidget,
)

from infini_transeon.config.schema import OverlayConfig
from infini_transeon.utils.i18n import tr

_FONT_CHOICES = [
    ("System default", "system"),
    ("Segoe UI", "Segoe UI"),
    ("Arial", "Arial"),
    ("Noto Sans", "Noto Sans"),
    ("Noto Sans CJK", "Noto Sans CJK"),
    ("Source Han Sans", "Source Han Sans"),
    ("Microsoft YaHei", "Microsoft YaHei"),
    ("Yu Gothic", "Yu Gothic"),
    ("SF Pro Text", "SF Pro Text"),
]


class OverlayPanel(QWidget):
    """Controls for overlay typography, opacity, color and click-through."""

    def __init__(self) -> None:
        super().__init__()
        self._bg_color = "#000000"
        self._text_color = "#FFFFFF"
        self._build_ui()

    def _build_ui(self) -> None:
        outer = QVBoxLayout(self)
        outer.setContentsMargins(16, 16, 16, 16)
        outer.setSpacing(10)

        hint = QLabel(tr("overlay.hint"))
        hint.setObjectName("muted")
        hint.setWordWrap(True)
        outer.addWidget(hint)

        form = QFormLayout()
        form.setHorizontalSpacing(16)
        form.setVerticalSpacing(10)

        self._font_family = QComboBox()
        for label, value in _FONT_CHOICES:
            self._font_family.addItem(label, value)
        form.addRow(tr("overlay.font_family"), self._font_family)

        self._font_scale = QDoubleSpinBox()
        self._font_scale.setRange(0.5, 3.0)
        self._font_scale.setSingleStep(0.1)
        self._font_scale.setDecimals(2)
        self._font_scale.setSuffix("×")
        form.addRow(tr("overlay.font_scale"), self._font_scale)

        opacity_row = QHBoxLayout()
        opacity_row.setSpacing(8)
        self._opacity = QSlider(Qt.Orientation.Horizontal)
        self._opacity.setRange(20, 100)
        self._opacity.setTickInterval(10)
        self._opacity.valueChanged.connect(self._on_opacity_changed)
        self._opacity_value = QSpinBox()
        self._opacity_value.setRange(20, 100)
        self._opacity_value.setSuffix("%")
        self._opacity_value.valueChanged.connect(self._opacity.setValue)
        self._opacity.valueChanged.connect(self._opacity_value.setValue)
        opacity_row.addWidget(self._opacity, 1)
        opacity_row.addWidget(self._opacity_value)
        form.addRow(tr("overlay.opacity"), opacity_row)

        self._high_contrast = QCheckBox(tr("overlay.high_contrast"))
        self._high_contrast.setToolTip(tr("overlay.high_contrast.tip"))
        self._high_contrast.toggled.connect(self._on_high_contrast_toggled)
        form.addRow(tr("overlay.contrast.label"), self._high_contrast)

        bg_row = QHBoxLayout()
        bg_row.setSpacing(8)
        self._bg_swatch = QPushButton()
        self._bg_swatch.setFixedSize(28, 24)
        self._bg_swatch.clicked.connect(self._pick_bg_color)
        bg_row.addWidget(self._bg_swatch)
        self._bg_label = QLabel(self._bg_color)
        bg_row.addWidget(self._bg_label)
        bg_row.addStretch(1)
        form.addRow(tr("overlay.bg_color"), bg_row)

        text_row = QHBoxLayout()
        text_row.setSpacing(8)
        self._text_swatch = QPushButton()
        self._text_swatch.setFixedSize(28, 24)
        self._text_swatch.clicked.connect(self._pick_text_color)
        text_row.addWidget(self._text_swatch)
        self._text_label = QLabel(self._text_color)
        text_row.addWidget(self._text_label)
        text_row.addStretch(1)
        form.addRow(tr("overlay.text_color"), text_row)

        self._click_through = QCheckBox(tr("settings.overlay.click_through"))
        self._click_through.setToolTip(tr("settings.overlay.click_through.tip"))
        form.addRow(tr("overlay.click_through.label"), self._click_through)

        outer.addLayout(form)
        outer.addStretch(1)

    # --- state --------------------------------------------------------

    def load(self, cfg: OverlayConfig) -> None:
        for i in range(self._font_family.count()):
            if self._font_family.itemData(i) == cfg.font_family:
                self._font_family.setCurrentIndex(i)
                break
        self._font_scale.setValue(cfg.font_scale)
        self._opacity.setValue(int(round(cfg.bg_opacity * 100)))
        self._high_contrast.setChecked(cfg.high_contrast)
        self._click_through.setChecked(cfg.click_through)
        self._bg_color = cfg.bg_color
        self._text_color = cfg.text_color
        self._refresh_swatches()
        self._apply_contrast_lock(cfg.high_contrast)

    def snapshot(self, base: OverlayConfig) -> OverlayConfig:
        return base.model_copy(
            update={
                "font_family": self._font_family.currentData() or "system",
                "font_scale": float(self._font_scale.value()),
                "bg_opacity": self._opacity.value() / 100.0,
                "bg_color": self._bg_color,
                "text_color": self._text_color,
                "high_contrast": self._high_contrast.isChecked(),
                "click_through": self._click_through.isChecked(),
            }
        )

    # --- handlers -----------------------------------------------------

    def _on_opacity_changed(self, _value: int) -> None:
        pass

    def _on_high_contrast_toggled(self, enabled: bool) -> None:
        self._apply_contrast_lock(enabled)

    def _apply_contrast_lock(self, enabled: bool) -> None:
        # In high-contrast mode the colours are fixed; dim the pickers so users
        # understand why clicking them has no visual effect.
        self._bg_swatch.setEnabled(not enabled)
        self._text_swatch.setEnabled(not enabled)

    def _pick_bg_color(self) -> None:
        picked = QColorDialog.getColor(QColor(self._bg_color), self, tr("overlay.bg_color"))
        if picked.isValid():
            self._bg_color = picked.name()
            self._refresh_swatches()

    def _pick_text_color(self) -> None:
        picked = QColorDialog.getColor(QColor(self._text_color), self, tr("overlay.text_color"))
        if picked.isValid():
            self._text_color = picked.name()
            self._refresh_swatches()

    def _refresh_swatches(self) -> None:
        self._bg_swatch.setStyleSheet(
            f"background-color: {self._bg_color}; border: 1px solid #d0d5dd; border-radius: 4px;"
        )
        self._text_swatch.setStyleSheet(
            f"background-color: {self._text_color}; border: 1px solid #d0d5dd; border-radius: 4px;"
        )
        self._bg_label.setText(self._bg_color)
        self._text_label.setText(self._text_color)


__all__ = ["OverlayPanel"]
