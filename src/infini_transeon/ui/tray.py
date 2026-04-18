"""System tray icon and menu."""

from __future__ import annotations

from collections.abc import Callable

from PySide6.QtCore import QObject, Signal
from PySide6.QtGui import QAction, QIcon, QPixmap
from PySide6.QtWidgets import QMenu, QSystemTrayIcon


class TrayIcon(QObject):
    """Minimal tray icon exposing commands as Qt signals."""

    pick_window = Signal()
    toggle_running = Signal()
    open_settings = Signal()
    retry_online = Signal()
    check_updates = Signal()
    quit_requested = Signal()

    def __init__(self, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self._tray = QSystemTrayIcon(_default_icon(), parent=parent)
        menu = QMenu()
        self._actions: dict[str, QAction] = {}
        self._add(menu, "Pick window…", self.pick_window)
        self._add(menu, "Pause / Resume", self.toggle_running)
        menu.addSeparator()
        self._add(menu, "Retry online", self.retry_online)
        self._add(menu, "Settings…", self.open_settings)
        self._add(menu, "Check for updates", self.check_updates)
        menu.addSeparator()
        self._add(menu, "Quit", self.quit_requested)
        self._tray.setContextMenu(menu)
        self._tray.setToolTip("Infini-Transeon")
        self._tray.activated.connect(self._on_activated)

    def show(self) -> None:
        self._tray.show()

    def hide(self) -> None:
        self._tray.hide()

    def set_state(self, *, running: bool, degraded: bool) -> None:
        tooltip = [
            "Running" if running else "Paused",
            "(degraded to local)" if degraded else "",
        ]
        self._tray.setToolTip(" ".join(t for t in tooltip if t) or "Infini-Transeon")
        icon = _badge_icon(running=running, degraded=degraded)
        if icon is not None:
            self._tray.setIcon(icon)

    def notify(self, title: str, message: str) -> None:
        self._tray.showMessage(title, message, QSystemTrayIcon.MessageIcon.Information, 4000)

    def _add(self, menu: QMenu, label: str, signal: Signal) -> None:
        action = QAction(label, self._tray)
        action.triggered.connect(lambda: signal.emit())  # noqa: PLC0501
        menu.addAction(action)
        self._actions[label] = action

    def _on_activated(self, reason: QSystemTrayIcon.ActivationReason) -> None:
        if reason == QSystemTrayIcon.ActivationReason.Trigger:
            self.pick_window.emit()


def _default_icon() -> QIcon:
    pix = QPixmap(16, 16)
    pix.fill()
    return QIcon(pix)


def _badge_icon(*, running: bool, degraded: bool) -> QIcon | None:
    # Placeholder: replace with real packaged icons in a later pass.
    pix = QPixmap(16, 16)
    pix.fill()
    return QIcon(pix)


__all__ = ["TrayIcon"]
