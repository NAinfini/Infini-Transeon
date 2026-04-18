from __future__ import annotations

from infini_transeon.config.schema import StyleMode
from infini_transeon.translate.prompts import (
    build_system_prompt,
    build_user_prompt,
    parse_numbered_output,
)


def test_user_prompt_numbering() -> None:
    text = build_user_prompt(["hello", "world"])
    assert "1. hello" in text
    assert "2. world" in text


def test_parse_numbered_output_basic() -> None:
    out = parse_numbered_output("1. 你好\n2. 世界", expected=2)
    assert out == ["你好", "世界"]


def test_parse_numbered_output_missing_lines() -> None:
    out = parse_numbered_output("1. 你好", expected=3)
    assert out == ["你好", "", ""]


def test_parse_numbered_output_tolerates_prefixes() -> None:
    out = parse_numbered_output("[1] 你好\n[2] 世界", expected=2)
    assert out == ["你好", "世界"]


def test_system_prompt_includes_style_and_glossary() -> None:
    text = build_system_prompt(
        source_lang="en",
        target_lang="zh",
        style=StyleMode.gaming,
        glossary=[("Potion", "药水")],
        history=["Prior line"],
    )
    assert "game" in text.lower()
    assert "Potion => 药水" in text
    assert "Prior line" in text
