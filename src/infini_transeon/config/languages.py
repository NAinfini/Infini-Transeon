"""Language registry — maps ISO 639-1 codes to display names and engine codes."""

from __future__ import annotations

from dataclasses import dataclass
from importlib import resources
from typing import Any

import yaml


@dataclass(frozen=True, slots=True)
class Language:
    code: str                # ISO 639-1 (e.g. "zh", "zh-TW", "en", "ja")
    display: str             # human-readable name
    paddle_code: str         # PaddleOCR language code
    flores_code: str         # NLLB/MADLAD FLORES-200 code (future use)
    script: str              # "Latn" | "Hans" | "Hant" | "Jpan" | ...


class LanguageRegistry:
    """Loads languages from YAML bundled with the package."""

    def __init__(self, entries: dict[str, Language]):
        self._by_code = entries

    @classmethod
    def load(cls) -> LanguageRegistry:
        data: Any
        with resources.files("infini_transeon.config").joinpath("languages.yaml").open(
            "r", encoding="utf-8"
        ) as fh:
            data = yaml.safe_load(fh)
        entries: dict[str, Language] = {}
        for row in data.get("languages", []):
            lang = Language(
                code=row["code"],
                display=row["display"],
                paddle_code=row.get("paddle_code", row["code"]),
                flores_code=row.get("flores_code", ""),
                script=row.get("script", ""),
            )
            entries[lang.code] = lang
        return cls(entries)

    def __iter__(self):
        return iter(self._by_code.values())

    def __contains__(self, code: str) -> bool:
        return code in self._by_code

    def get(self, code: str) -> Language | None:
        return self._by_code.get(code)

    def require(self, code: str) -> Language:
        lang = self.get(code)
        if lang is None:
            raise KeyError(f"unknown language code: {code}")
        return lang

    def codes(self) -> list[str]:
        return list(self._by_code.keys())


__all__ = ["Language", "LanguageRegistry"]
