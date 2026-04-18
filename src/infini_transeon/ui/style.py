"""Global Qt stylesheet.

Two palettes ship: ``light`` (neutral slate/blue) and ``dark`` (near-black
surface + higher-contrast text). The previous ``STYLESHEET`` module-level
constant is kept as an alias for ``build_stylesheet('light')`` so any code
that imported it directly keeps working.
"""

from __future__ import annotations

import sys
from typing import Literal

Theme = Literal["light", "dark"]


_LIGHT = {
    "text": "#1f2329",
    "text_strong": "#0f172a",
    "text_muted": "#64748b",
    "surface": "#f6f7f9",
    "surface_card": "#ffffff",
    "border": "#e4e7ec",
    "border_strong": "#cbd2dc",
    "primary": "#2563eb",
    "primary_hover": "#1d4ed8",
    "primary_pressed": "#1e40af",
    "primary_disabled": "#93c5fd",
    "danger_text": "#b42318",
    "danger_bg": "#fef3f2",
    "danger_border": "#fecdca",
    "pill_bg": "#e4e7ec",
    "pill_text": "#475467",
    "pill_running_bg": "#dcfce7",
    "pill_running_text": "#166534",
    "pill_degraded_bg": "#fef3c7",
    "pill_degraded_text": "#92400e",
    "pill_error_bg": "#fee2e2",
    "pill_error_text": "#991b1b",
    "input_bg": "#ffffff",
    "input_border": "#cbd2dc",
    "hover_bg": "#eef1f5",
    "pressed_bg": "#dfe4eb",
    "statusbar_bg": "#f2f4f7",
    "statusbar_hover_bg": "#e4e7ec",
    "selection_bg": "#dbeafe",
    "selection_text": "#0f172a",
    "scrollbar": "#d0d5dd",
    "scrollbar_hover": "#98a2b3",
    "dropdown_hover": "#eff6ff",
    "tab_text_inactive": "#475467",
    "disabled_text": "#98a2b3",
    "disabled_bg": "#f2f4f7",
    "indicator_bg": "#ffffff",
    "indicator_border": "#98a2b3",
    "arrow": "#475467",
}

_DARK = {
    "text": "#e6e8eb",
    "text_strong": "#f8fafc",
    "text_muted": "#94a3b8",
    "surface": "#0f1115",
    "surface_card": "#1b1f27",
    "border": "#2a2f3a",
    "border_strong": "#3e4553",
    "primary": "#3b82f6",
    "primary_hover": "#2563eb",
    "primary_pressed": "#1d4ed8",
    "primary_disabled": "#1e3a8a",
    "danger_text": "#fca5a5",
    "danger_bg": "#2a1414",
    "danger_border": "#7f1d1d",
    "pill_bg": "#2a2f3a",
    "pill_text": "#cbd5e1",
    "pill_running_bg": "#064e3b",
    "pill_running_text": "#6ee7b7",
    "pill_degraded_bg": "#422006",
    "pill_degraded_text": "#fcd34d",
    "pill_error_bg": "#450a0a",
    "pill_error_text": "#fca5a5",
    "input_bg": "#111419",
    "input_border": "#3e4553",
    "hover_bg": "#262b35",
    "pressed_bg": "#323845",
    "statusbar_bg": "#14171d",
    "statusbar_hover_bg": "#1f232b",
    "selection_bg": "#1e3a8a",
    "selection_text": "#f8fafc",
    "scrollbar": "#303641",
    "scrollbar_hover": "#475569",
    "dropdown_hover": "#1e3a8a",
    "tab_text_inactive": "#94a3b8",
    "disabled_text": "#4b5563",
    "disabled_bg": "#1a1d23",
    "indicator_bg": "#111419",
    "indicator_border": "#4b5563",
    "arrow": "#94a3b8",
}


def _render(p: dict[str, str]) -> str:
    return f"""
* {{
    font-family: "Segoe UI", "SF Pro Text", "Helvetica Neue", Arial, sans-serif;
    font-size: 13px;
    color: {p['text']};
}}

QWidget {{ background-color: {p['surface']}; }}
QDialog, QMainWindow {{ background-color: {p['surface']}; }}

QLabel {{ background: transparent; }}
QLabel#h1 {{ font-size: 22px; font-weight: 600; color: {p['text_strong']}; }}
QLabel#h2 {{ font-size: 15px; font-weight: 600; color: {p['text_strong']}; }}
QLabel#muted {{ color: {p['text_muted']}; }}

QFrame#card {{
    background-color: {p['surface_card']};
    border: 1px solid {p['border']};
    border-radius: 10px;
}}
QFrame#card QLabel {{ background: transparent; }}

/* --- Buttons ---------------------------------------------------------- */
QPushButton {{
    background-color: {p['surface_card']};
    color: {p['text_strong']};
    border: 1px solid {p['border_strong']};
    border-radius: 6px;
    padding: 8px 16px;
    min-height: 22px;
    font-weight: 500;
}}
QPushButton:hover {{
    background-color: {p['hover_bg']};
    border-color: {p['text_muted']};
}}
QPushButton:pressed {{ background-color: {p['pressed_bg']}; }}
QPushButton:focus {{ border: 1px solid {p['primary']}; outline: none; }}
QPushButton:disabled {{
    color: {p['disabled_text']};
    background-color: {p['disabled_bg']};
    border-color: {p['border']};
}}

QPushButton#primary {{
    background-color: {p['primary']};
    color: #ffffff;
    border: 1px solid {p['primary_hover']};
    font-weight: 600;
}}
QPushButton#primary:hover {{ background-color: {p['primary_hover']}; border-color: {p['primary_pressed']}; }}
QPushButton#primary:pressed {{ background-color: {p['primary_pressed']}; }}
QPushButton#primary:focus {{ border: 1px solid #ffffff; }}
QPushButton#primary:disabled {{
    background-color: {p['primary_disabled']};
    border-color: {p['primary_disabled']};
    color: #ffffff;
}}

QPushButton#danger {{
    background-color: {p['surface_card']};
    color: {p['danger_text']};
    border: 1px solid {p['danger_border']};
}}
QPushButton#danger:hover {{ background-color: {p['danger_bg']}; }}

/* --- Inputs ----------------------------------------------------------- */
QComboBox, QLineEdit, QSpinBox, QDoubleSpinBox, QPlainTextEdit, QTextEdit, QKeySequenceEdit {{
    background-color: {p['input_bg']};
    border: 1px solid {p['input_border']};
    border-radius: 6px;
    padding: 6px 10px;
    min-height: 22px;
    selection-background-color: {p['selection_bg']};
    selection-color: {p['selection_text']};
}}
QComboBox:hover, QLineEdit:hover, QSpinBox:hover, QDoubleSpinBox:hover,
QPlainTextEdit:hover, QTextEdit:hover, QKeySequenceEdit:hover {{
    border-color: {p['text_muted']};
}}
QComboBox:focus, QLineEdit:focus, QSpinBox:focus, QDoubleSpinBox:focus,
QPlainTextEdit:focus, QTextEdit:focus, QKeySequenceEdit:focus {{
    border: 1px solid {p['primary']};
}}
QComboBox:disabled, QLineEdit:disabled, QSpinBox:disabled, QDoubleSpinBox:disabled {{
    color: {p['disabled_text']};
    background-color: {p['disabled_bg']};
    border-color: {p['border']};
}}

QComboBox::drop-down {{
    subcontrol-origin: padding;
    subcontrol-position: center right;
    width: 22px;
    border: none;
    background: transparent;
}}
/* Let Fusion draw the chevron glyph — don't wipe it with image:none. */

QComboBox QAbstractItemView {{
    background-color: {p['surface_card']};
    border: 1px solid {p['border_strong']};
    selection-background-color: {p['dropdown_hover']};
    selection-color: {p['text_strong']};
    padding: 4px;
    outline: 0;
}}

/* --- Radios & checkboxes --------------------------------------------- */
QRadioButton, QCheckBox {{
    spacing: 8px;
    padding: 4px 2px;
    background: transparent;
}}
QRadioButton::indicator, QCheckBox::indicator {{
    width: 16px;
    height: 16px;
    border: 1.5px solid {p['indicator_border']};
    background-color: {p['indicator_bg']};
}}
QRadioButton::indicator {{ border-radius: 9px; }}
QCheckBox::indicator {{ border-radius: 4px; }}
QRadioButton::indicator:hover, QCheckBox::indicator:hover {{
    border-color: {p['primary']};
}}
QRadioButton::indicator:checked {{
    background-color: {p['primary']};
    border: 1.5px solid {p['primary']};
}}
QCheckBox::indicator:checked {{
    background-color: {p['primary']};
    border-color: {p['primary']};
}}
QRadioButton:disabled, QCheckBox:disabled {{ color: {p['disabled_text']}; }}
QRadioButton::indicator:disabled, QCheckBox::indicator:disabled {{
    border-color: {p['border']};
    background-color: {p['disabled_bg']};
}}

/* --- Tabs ------------------------------------------------------------- */
QTabWidget::pane {{
    border: 1px solid {p['border']};
    border-radius: 8px;
    background-color: {p['surface_card']};
    top: -1px;
}}
QTabBar::tab {{
    background: transparent;
    padding: 8px 14px;
    border: 1px solid transparent;
    border-bottom: none;
    border-top-left-radius: 8px;
    border-top-right-radius: 8px;
    color: {p['tab_text_inactive']};
}}
QTabBar::tab:selected {{
    background: {p['surface_card']};
    border: 1px solid {p['border']};
    color: {p['text_strong']};
    font-weight: 600;
}}
QTabBar::tab:hover:!selected {{ color: {p['text_strong']}; }}

QGroupBox {{
    border: 1px solid {p['border']};
    border-radius: 8px;
    background-color: {p['surface_card']};
    margin-top: 14px;
    padding-top: 10px;
}}
QGroupBox::title {{
    subcontrol-origin: margin;
    left: 12px;
    padding: 0 6px;
    color: {p['text']};
    font-weight: 600;
}}

QScrollBar:vertical {{ background: transparent; width: 10px; margin: 2px; }}
QScrollBar::handle:vertical {{
    background: {p['scrollbar']};
    border-radius: 5px;
    min-height: 24px;
}}
QScrollBar::handle:vertical:hover {{ background: {p['scrollbar_hover']}; }}
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{ height: 0; }}

QStatusBar {{
    background: {p['statusbar_bg']};
    color: {p['text_muted']};
    border-top: 1px solid {p['border']};
    padding: 2px 8px;
}}
QStatusBar:hover {{
    background: {p['statusbar_hover_bg']};
    color: {p['text']};
}}
QStatusBar::item {{ border: none; }}

QLabel#statusPill {{
    padding: 3px 12px;
    border-radius: 11px;
    background-color: {p['pill_bg']};
    color: {p['pill_text']};
    font-weight: 600;
}}
QLabel#statusPill[state="running"] {{
    background-color: {p['pill_running_bg']};
    color: {p['pill_running_text']};
}}
QLabel#statusPill[state="degraded"] {{
    background-color: {p['pill_degraded_bg']};
    color: {p['pill_degraded_text']};
}}
QLabel#statusPill[state="error"] {{
    background-color: {p['pill_error_bg']};
    color: {p['pill_error_text']};
}}
QLabel#statusPill[state="manual"] {{
    background-color: {p['pill_degraded_bg']};
    color: {p['pill_degraded_text']};
}}
"""


def build_stylesheet(theme: Theme = "light") -> str:
    palette = _DARK if theme == "dark" else _LIGHT
    return _render(palette)


def detect_system_theme() -> Theme:
    """Best-effort read of the OS-level color scheme.

    Returns 'light' when the probe is inconclusive so the app stays readable.
    """
    try:
        from PySide6.QtGui import QGuiApplication
        from PySide6.QtCore import Qt

        app = QGuiApplication.instance()
        if app is None:
            return "light"
        hints = app.styleHints()
        scheme = getattr(hints, "colorScheme", None)
        if scheme is not None:
            value = scheme()
            if value == Qt.ColorScheme.Dark:
                return "dark"
            if value == Qt.ColorScheme.Light:
                return "light"
    except Exception:  # noqa: BLE001
        pass

    # Windows-native fallback via the registry.
    if sys.platform == "win32":
        try:
            import winreg  # type: ignore[import-not-found]

            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            ) as key:
                value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
                return "light" if int(value) == 1 else "dark"
        except Exception:  # noqa: BLE001
            return "light"
    return "light"


def resolve_theme(choice: str) -> Theme:
    """Translate the user's 'auto'|'light'|'dark' preference into a concrete theme."""
    if choice == "dark":
        return "dark"
    if choice == "light":
        return "light"
    return detect_system_theme()


# Back-compat: existing imports of STYLESHEET keep working (default light).
STYLESHEET = build_stylesheet("light")


__all__ = [
    "STYLESHEET",
    "Theme",
    "build_stylesheet",
    "detect_system_theme",
    "resolve_theme",
]
