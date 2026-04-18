"""Fullscreen drag-to-select picker for an arbitrary screen region.

Spawns one frameless, always-on-top, translucent window covering the entire
virtual desktop (all monitors). The user drags to draw a rectangle; on release,
``selected(Rect, dpr)`` is emitted with Qt **logical** (device-independent)
coordinates and the source screen's device-pixel ratio. Downstream capture/
overlay layers live in logical space and scale to physical pixels only at
the ``mss.grab`` boundary.
"""

from __future__ import annotations

from PySide6.QtCore import QPoint, QRect, Qt, Signal
from PySide6.QtGui import (
    QColor,
    QGuiApplication,
    QKeyEvent,
    QMouseEvent,
    QPainter,
    QPaintEvent,
    QPen,
)
from PySide6.QtWidgets import QApplication, QLabel, QWidget

from infini_transeon.capture.base import Rect
from infini_transeon.utils.i18n import tr
from infini_transeon.utils.logging import logger


class RegionPicker(QWidget):
    """Fullscreen translucent widget that returns a user-drawn rectangle."""

    # rect is in Qt logical coords; dpr is the screen that owns the rect.
    selected = Signal(Rect, float)
    cancelled = Signal()

    def __init__(self) -> None:
        super().__init__()
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setCursor(Qt.CursorShape.CrossCursor)
        self._origin: QPoint | None = None
        self._rubber: QRect = QRect()

        # Cover the entire virtual desktop so regions can span monitors.
        self._virtual_rect = self._compute_virtual_rect()
        self.setGeometry(self._virtual_rect)

        self._hint = QLabel(tr("region.picker.hint"), self)
        self._hint.setStyleSheet(
            "QLabel { background: rgba(0,0,0,180); color: white; "
            "padding: 8px 14px; border-radius: 6px; font-size: 13px; }"
        )
        self._hint.adjustSize()
        self._hint.move(
            (self.width() - self._hint.width()) // 2,
            24,
        )

    # --- public API -----------------------------------------------------

    @staticmethod
    def _compute_virtual_rect() -> QRect:
        app = QApplication.instance()
        if app is None:
            return QRect(0, 0, 1920, 1080)
        screens = QGuiApplication.screens()
        if not screens:
            return QRect(0, 0, 1920, 1080)
        geom = screens[0].geometry()
        for screen in screens[1:]:
            geom = geom.united(screen.geometry())
        return geom

    @staticmethod
    def _dpr_at(point: QPoint) -> float:
        """Device-pixel ratio of the screen containing ``point`` (global coords)."""
        screen = QGuiApplication.screenAt(point)
        if screen is not None:
            return float(screen.devicePixelRatio())
        primary = QGuiApplication.primaryScreen()
        return float(primary.devicePixelRatio()) if primary is not None else 1.0

    # --- events ---------------------------------------------------------

    def mousePressEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        if event.button() == Qt.MouseButton.LeftButton:
            self._origin = event.position().toPoint()
            self._rubber = QRect(self._origin, self._origin)
            self.update()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        if self._origin is not None:
            self._rubber = QRect(self._origin, event.position().toPoint()).normalized()
            self.update()

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        if event.button() != Qt.MouseButton.LeftButton or self._origin is None:
            return
        rect = QRect(self._origin, event.position().toPoint()).normalized()
        self._origin = None
        if rect.width() < 4 or rect.height() < 4:
            self.cancelled.emit()
            self.close()
            return
        # Convert BOTH corners to global coords and measure the span there.
        # Taking width/height from widget-local coords is unsafe when the
        # picker spans multiple monitors with different DPRs: Qt scales the
        # widget-local origin to the primary screen, but mouse positions on
        # a secondary monitor come back in that monitor's scale, so
        # rect.width() ends up 1.25-1.75x too large. Global coords are
        # uniform across the virtual desktop, so the span is correct.
        tl_global = self.mapToGlobal(rect.topLeft())
        br_global = self.mapToGlobal(rect.bottomRight())
        dpr = self._dpr_at(tl_global)
        picked = Rect(
            x=int(tl_global.x()),
            y=int(tl_global.y()),
            width=max(1, int(br_global.x() - tl_global.x())),
            height=max(1, int(br_global.y() - tl_global.y())),
        )
        logger.info(
            "region picked: logical=({}, {}, {}x{}) dpr={}",
            picked.x, picked.y, picked.width, picked.height, dpr,
        )
        self.selected.emit(picked, dpr)
        self.close()

    def keyPressEvent(self, event: QKeyEvent) -> None:  # noqa: D401 - Qt override
        if event.key() == Qt.Key.Key_Escape:
            self.cancelled.emit()
            self.close()
            return
        super().keyPressEvent(event)

    def paintEvent(self, event: QPaintEvent) -> None:  # noqa: D401 - Qt override
        painter = QPainter(self)
        try:
            painter.setRenderHint(QPainter.RenderHint.Antialiasing, True)
            # Dim the whole surface so the user sees what they're picking against.
            painter.fillRect(self.rect(), QColor(0, 0, 0, 90))

            if not self._rubber.isNull():
                # Clear the selection rectangle so it shows the screen behind.
                painter.setCompositionMode(QPainter.CompositionMode.CompositionMode_Clear)
                painter.fillRect(self._rubber, Qt.GlobalColor.transparent)
                painter.setCompositionMode(QPainter.CompositionMode.CompositionMode_SourceOver)

                pen = QPen(QColor("#4aa3ff"))
                pen.setWidth(2)
                painter.setPen(pen)
                painter.drawRect(self._rubber)

                # Size readout anchored to the rubber band.
                painter.setPen(QColor("white"))
                painter.fillRect(
                    QRect(self._rubber.left(), self._rubber.bottom() + 4, 120, 22),
                    QColor(0, 0, 0, 200),
                )
                painter.drawText(
                    QRect(self._rubber.left() + 6, self._rubber.bottom() + 4, 114, 22),
                    Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft,
                    f"{self._rubber.width()} × {self._rubber.height()}",
                )
        finally:
            painter.end()


__all__ = ["RegionPicker"]
