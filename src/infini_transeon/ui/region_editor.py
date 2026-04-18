"""Persistent, in-place drag/resize handle for a region target.

Shown on top of the translation overlay while ``capture.mode == "region"`` and
a region is set. The user can:

* Drag anywhere inside the frame to move the region.
* Drag any of the eight edge/corner grips to resize.
* Double-click to re-run the full RegionPicker.

On every change (mouse release) the widget emits ``region_edited(Rect, dpr)``
so the pipeline and overlay re-center on the new rect.

The widget is a frameless, always-on-top, translucent border. It is **not**
click-through: it has to accept mouse events. The translation bubbles rendered
by ``OverlayWindow`` stay click-through as before and simply peek through the
transparent interior of this frame.
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
from PySide6.QtWidgets import QWidget

from infini_transeon.capture.base import Rect
from infini_transeon.overlay import exclude_capture


_HANDLE_SIZE = 10          # px side of corner/edge grips
_FRAME_THICKNESS = 2       # px outline
_HIT_SLOP = 8              # px inside the frame that counts as border-drag


# Resize modes, encoded as (dx_origin, dy_origin, dx_size, dy_size) deltas
# applied to the starting rect when the mouse moves by (dx, dy).
class _HitZone:
    NONE = 0
    MOVE = 1
    N = 2
    S = 3
    W = 4
    E = 5
    NW = 6
    NE = 7
    SW = 8
    SE = 9


_CURSORS = {
    _HitZone.NONE: Qt.CursorShape.ArrowCursor,
    _HitZone.MOVE: Qt.CursorShape.SizeAllCursor,
    _HitZone.N: Qt.CursorShape.SizeVerCursor,
    _HitZone.S: Qt.CursorShape.SizeVerCursor,
    _HitZone.W: Qt.CursorShape.SizeHorCursor,
    _HitZone.E: Qt.CursorShape.SizeHorCursor,
    _HitZone.NW: Qt.CursorShape.SizeFDiagCursor,
    _HitZone.SE: Qt.CursorShape.SizeFDiagCursor,
    _HitZone.NE: Qt.CursorShape.SizeBDiagCursor,
    _HitZone.SW: Qt.CursorShape.SizeBDiagCursor,
}


class RegionEditor(QWidget):
    """Draggable / resizable frame overlay for the current region target."""

    region_edited = Signal(Rect, float)  # rect (logical coords), dpr
    repick_requested = Signal()

    def __init__(self, *, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        # BypassWindowManagerHint stops Windows from ever rendering a title
        # bar or taskbar entry for this overlay. Without it, the native WM
        # briefly decorates the Tool window with an app-name label before
        # WA_TranslucentBackground / Frameless take effect, which the user
        # sees as "Infini-Transeon" text inside the blue frame.
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
            | Qt.WindowType.NoDropShadowWindowHint
            | Qt.WindowType.BypassWindowManagerHint
        )
        self.setWindowTitle("")
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating, True)
        self.setAttribute(Qt.WidgetAttribute.WA_NoSystemBackground, True)
        self.setMouseTracking(True)

        self._zone: int = _HitZone.NONE
        self._drag_origin: QPoint | None = None  # global pos at drag start
        self._start_rect: QRect = QRect()        # widget geometry at drag start
        self._dpr: float = 1.0

    # --- public API ----------------------------------------------------

    def set_region(self, rect: Rect, dpr: float) -> None:
        self._dpr = float(dpr)
        self.setGeometry(QRect(rect.x, rect.y, rect.width, rect.height))
        self.update()

    def showEvent(self, event):  # noqa: D401 - Qt override
        super().showEvent(event)
        handle = self.windowHandle()
        if handle is not None:
            # The dashed frame + grips would otherwise land inside our own
            # region captures and get fed back through OCR as noise.
            exclude_capture.apply(handle)

    # --- event handling ------------------------------------------------

    def mousePressEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        if event.button() != Qt.MouseButton.LeftButton:
            return
        self._zone = self._hit_test(event.position().toPoint())
        if self._zone == _HitZone.NONE:
            return
        self._drag_origin = event.globalPosition().toPoint()
        self._start_rect = self.geometry()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        pos = event.position().toPoint()
        if self._drag_origin is None:
            # Not dragging — just update the cursor shape over the zone.
            zone = self._hit_test(pos)
            self.setCursor(_CURSORS.get(zone, Qt.CursorShape.ArrowCursor))
            return
        global_now = event.globalPosition().toPoint()
        dx = global_now.x() - self._drag_origin.x()
        dy = global_now.y() - self._drag_origin.y()
        new_rect = self._apply_delta(self._start_rect, self._zone, dx, dy)
        new_rect = self._clamp_to_virtual(new_rect)
        self.setGeometry(new_rect)
        self.update()

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        if event.button() != Qt.MouseButton.LeftButton or self._drag_origin is None:
            return
        self._drag_origin = None
        self._zone = _HitZone.NONE
        self.setCursor(Qt.CursorShape.ArrowCursor)
        g = self.geometry()
        rect = Rect(x=g.x(), y=g.y(), width=g.width(), height=g.height())
        # DPR may have changed if the region crossed screens.
        center = self.mapToGlobal(self.rect().center())
        screen = QGuiApplication.screenAt(center)
        if screen is not None:
            self._dpr = float(screen.devicePixelRatio())
        self.region_edited.emit(rect, self._dpr)

    def mouseDoubleClickEvent(self, event: QMouseEvent) -> None:  # noqa: D401 - Qt override
        if event.button() == Qt.MouseButton.LeftButton:
            self.repick_requested.emit()

    def keyPressEvent(self, event: QKeyEvent) -> None:  # noqa: D401 - Qt override
        # Arrow-key nudging for pixel-accurate adjustments.
        step = 10 if event.modifiers() & Qt.KeyboardModifier.ShiftModifier else 1
        g = self.geometry()
        dx = dy = 0
        if event.key() == Qt.Key.Key_Left:
            dx = -step
        elif event.key() == Qt.Key.Key_Right:
            dx = step
        elif event.key() == Qt.Key.Key_Up:
            dy = -step
        elif event.key() == Qt.Key.Key_Down:
            dy = step
        else:
            super().keyPressEvent(event)
            return
        new_rect = self._clamp_to_virtual(QRect(g.x() + dx, g.y() + dy, g.width(), g.height()))
        self.setGeometry(new_rect)
        rect = Rect(x=new_rect.x(), y=new_rect.y(), width=new_rect.width(), height=new_rect.height())
        self.region_edited.emit(rect, self._dpr)

    def paintEvent(self, event: QPaintEvent) -> None:  # noqa: D401 - Qt override
        painter = QPainter(self)
        try:
            painter.setRenderHint(QPainter.RenderHint.Antialiasing, True)

            # Outline — dashed so it reads as "edit mode" rather than a static bubble.
            pen = QPen(QColor("#4aa3ff"))
            pen.setWidth(_FRAME_THICKNESS)
            pen.setStyle(Qt.PenStyle.DashLine)
            painter.setPen(pen)
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawRect(self.rect().adjusted(1, 1, -1, -1))

            # 8 grips — corners + edge midpoints.
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(QColor("#4aa3ff"))
            for pt in self._grip_centers():
                painter.drawRect(
                    pt.x() - _HANDLE_SIZE // 2,
                    pt.y() - _HANDLE_SIZE // 2,
                    _HANDLE_SIZE,
                    _HANDLE_SIZE,
                )
        finally:
            painter.end()

    # --- helpers -------------------------------------------------------

    def _grip_centers(self) -> list[QPoint]:
        r = self.rect()
        mid_x = r.center().x()
        mid_y = r.center().y()
        return [
            QPoint(r.left(), r.top()),       # NW
            QPoint(mid_x, r.top()),          # N
            QPoint(r.right(), r.top()),      # NE
            QPoint(r.right(), mid_y),        # E
            QPoint(r.right(), r.bottom()),   # SE
            QPoint(mid_x, r.bottom()),       # S
            QPoint(r.left(), r.bottom()),    # SW
            QPoint(r.left(), mid_y),         # W
        ]

    def _hit_test(self, pos: QPoint) -> int:
        r = self.rect()
        x, y = pos.x(), pos.y()
        slop = _HIT_SLOP
        on_left = 0 <= x <= slop
        on_right = r.right() - slop <= x <= r.right()
        on_top = 0 <= y <= slop
        on_bottom = r.bottom() - slop <= y <= r.bottom()
        if on_left and on_top:
            return _HitZone.NW
        if on_right and on_top:
            return _HitZone.NE
        if on_left and on_bottom:
            return _HitZone.SW
        if on_right and on_bottom:
            return _HitZone.SE
        if on_top:
            return _HitZone.N
        if on_bottom:
            return _HitZone.S
        if on_left:
            return _HitZone.W
        if on_right:
            return _HitZone.E
        if r.contains(pos):
            return _HitZone.MOVE
        return _HitZone.NONE

    @staticmethod
    def _apply_delta(start: QRect, zone: int, dx: int, dy: int) -> QRect:
        x, y, w, h = start.x(), start.y(), start.width(), start.height()
        min_w = min_h = 20
        if zone == _HitZone.MOVE:
            return QRect(x + dx, y + dy, w, h)
        if zone in (_HitZone.W, _HitZone.NW, _HitZone.SW):
            new_w = max(min_w, w - dx)
            x = x + (w - new_w)
            w = new_w
        if zone in (_HitZone.E, _HitZone.NE, _HitZone.SE):
            w = max(min_w, w + dx)
        if zone in (_HitZone.N, _HitZone.NW, _HitZone.NE):
            new_h = max(min_h, h - dy)
            y = y + (h - new_h)
            h = new_h
        if zone in (_HitZone.S, _HitZone.SW, _HitZone.SE):
            h = max(min_h, h + dy)
        return QRect(x, y, w, h)

    @staticmethod
    def _clamp_to_virtual(rect: QRect) -> QRect:
        screens = QGuiApplication.screens()
        if not screens:
            return rect
        geom = screens[0].geometry()
        for s in screens[1:]:
            geom = geom.united(s.geometry())
        out = QRect(rect)
        if out.width() > geom.width():
            out.setWidth(geom.width())
        if out.height() > geom.height():
            out.setHeight(geom.height())
        if out.left() < geom.left():
            out.moveLeft(geom.left())
        if out.top() < geom.top():
            out.moveTop(geom.top())
        if out.right() > geom.right():
            out.moveRight(geom.right())
        if out.bottom() > geom.bottom():
            out.moveBottom(geom.bottom())
        return out


__all__ = ["RegionEditor"]
