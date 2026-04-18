from __future__ import annotations

from infini_transeon.config.schema import GlossaryEntry
from infini_transeon.translate.glossary import apply_forced, entries_for_target


def test_entries_filtered_by_target_language() -> None:
    entries = [
        GlossaryEntry(source="Fire", target="火", languages=["zh"]),
        GlossaryEntry(source="Fire", target="Feu", languages=["fr"]),
    ]
    zh = entries_for_target(entries, "zh")
    fr = entries_for_target(entries, "fr")
    assert zh == [("Fire", "火")]
    assert fr == [("Fire", "Feu")]


def test_entries_without_languages_apply_to_any() -> None:
    entries = [GlossaryEntry(source="Mana", target="MP", languages=[])]
    assert entries_for_target(entries, "zh") == [("Mana", "MP")]


def test_apply_forced_case_insensitive_by_default() -> None:
    out = apply_forced("The fire is hot", [("Fire", "火")])
    assert out == "The 火 is hot"


def test_apply_forced_case_sensitive() -> None:
    out = apply_forced("fire Fire", [("Fire", "火")], case_sensitive=True)
    assert out == "fire 火"
