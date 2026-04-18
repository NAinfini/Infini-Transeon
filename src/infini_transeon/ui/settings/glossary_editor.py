"""Glossary editor (inline editable table)."""

from __future__ import annotations

from PySide6.QtWidgets import (
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from infini_transeon.config.schema import GlossaryEntry
from infini_transeon.utils.i18n import tr


class GlossaryEditor(QWidget):
    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(10)

        hint = QLabel(tr("glossary.hint"))
        hint.setObjectName("muted")
        hint.setWordWrap(True)
        layout.addWidget(hint)

        self._table = QTableWidget(0, 3)
        self._table.setHorizontalHeaderLabels([
            tr("glossary.col.source"),
            tr("glossary.col.target"),
            tr("glossary.col.languages"),
        ])
        header = self._table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        layout.addWidget(self._table, 1)

        button_row = QHBoxLayout()
        button_row.setSpacing(10)
        self._add_btn = QPushButton(tr("glossary.btn.add"))
        self._add_btn.setObjectName("primary")
        self._add_btn.setToolTip(tr("glossary.btn.add.tip"))
        self._add_btn.clicked.connect(lambda: self._add_row())
        button_row.addWidget(self._add_btn)
        self._remove_btn = QPushButton(tr("glossary.btn.remove"))
        self._remove_btn.setObjectName("danger")
        self._remove_btn.setToolTip(tr("glossary.btn.remove.tip"))
        self._remove_btn.clicked.connect(self._remove_selected)
        button_row.addWidget(self._remove_btn)
        button_row.addStretch(1)
        layout.addLayout(button_row)

    def load(self, entries: list[GlossaryEntry]) -> None:
        self._table.setRowCount(0)
        for entry in entries:
            self._add_row(entry.source, entry.target, ", ".join(entry.languages))

    def snapshot(self) -> list[GlossaryEntry]:
        entries: list[GlossaryEntry] = []
        for row in range(self._table.rowCount()):
            source = self._cell(row, 0)
            target = self._cell(row, 1)
            if not source or not target:
                continue
            languages = [
                part.strip()
                for part in self._cell(row, 2).split(",")
                if part.strip()
            ]
            entries.append(
                GlossaryEntry(source=source, target=target, languages=languages)
            )
        return entries

    def _add_row(self, source: str = "", target: str = "", languages: str = "") -> None:
        row = self._table.rowCount()
        self._table.insertRow(row)
        self._table.setItem(row, 0, QTableWidgetItem(source))
        self._table.setItem(row, 1, QTableWidgetItem(target))
        self._table.setItem(row, 2, QTableWidgetItem(languages))

    def _remove_selected(self) -> None:
        rows = sorted({idx.row() for idx in self._table.selectedIndexes()}, reverse=True)
        for row in rows:
            self._table.removeRow(row)

    def _cell(self, row: int, col: int) -> str:
        item = self._table.item(row, col)
        if item is None:
            return ""
        return item.text().strip()


__all__ = ["GlossaryEditor"]
