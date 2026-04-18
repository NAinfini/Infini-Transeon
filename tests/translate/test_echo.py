"""Table-driven classifier tests. Add a row for every bug we see in prod."""

from __future__ import annotations

import pytest

from infini_transeon.translate.echo import (
    EchoVerdict,
    classify,
    is_translatable,
    is_untranslatable_content,
    normalize,
)


@pytest.mark.parametrize(
    "source, target, target_lang, expected",
    [
        # Baseline: real en->zh translation.
        ("Hello", "你好", "zh", EchoVerdict.translated),
        # Real zh->en translation.
        ("你好", "Hello", "en", EchoVerdict.translated),

        # Hard echo: target_lang=zh but output is pure Latin source.
        ("Hugging Face", "Hugging Face", "zh", EchoVerdict.passthrough),
        # ^ brand passed through verbatim — passthrough, not hard_echo.
        ("This is a sentence", "This is a sentence", "zh", EchoVerdict.hard_echo),
        # ^ translatable prose echoed verbatim — hard echo.

        # MyMemory failure mode: zh input returned as zh "translation".
        ("你好世界", "你好世界", "en", EchoVerdict.hard_echo),

        # URL passthrough — correct behaviour.
        ("https://huggingface.co/models", "https://huggingface.co/models", "zh",
         EchoVerdict.passthrough),

        # Identifier passthrough — correct behaviour.
        ("Helsinki-NLP/opus-mt-en-ko", "Helsinki-NLP/opus-mt-en-ko", "zh",
         EchoVerdict.passthrough),

        # Mixed output: translated prose + preserved brand.
        ("Hugging Face platform", "Hugging Face 平台", "zh", EchoVerdict.translated),

        # A mostly-English output with one CJK char still counts as
        # translated — any target-script content signals the model did
        # *something*. We don't try to second-guess mixed outputs.
        ("This is a long English sentence that should be translated",
         "This is a long English sentence 的",
         "zh",
         EchoVerdict.translated),

        # Same-script pair: es echo of en is accepted, we can't tell.
        ("Hello world", "Hello world", "es", EchoVerdict.translated),

        # Empty output — always hard echo.
        ("Hello", "", "zh", EchoVerdict.hard_echo),
        ("Hello", "   ", "zh", EchoVerdict.hard_echo),

        # Digits / punctuation only — passthrough regardless of scripts.
        ("12345", "12345", "zh", EchoVerdict.passthrough),
        ("-->", "-->", "zh", EchoVerdict.passthrough),

        # Unknown target language: degrade gracefully.
        ("Hello", "Ciao", "xx", EchoVerdict.translated),
        ("https://example.com", "https://example.com", "xx", EchoVerdict.passthrough),

        # CJK<->CJK (zh->ja) same-script pair, must not reject.
        ("你好", "こんにちは", "ja", EchoVerdict.translated),

        # Cyrillic target with real translation.
        ("Hello", "Привет", "ru", EchoVerdict.translated),
        # Cyrillic target echoed as Latin source — hard echo.
        ("This is English", "This is English", "ru", EchoVerdict.hard_echo),
    ],
)
def test_classify(source: str, target: str, target_lang: str, expected: EchoVerdict) -> None:
    assert classify(source, target, target_lang) is expected


@pytest.mark.parametrize(
    "text, expected",
    [
        ("Hello", True),
        ("你好", True),
        ("a", False),             # too short
        ("", False),
        ("   ", False),
        ("-->", False),           # no letters
        ("123", False),
        ("$0.01", False),
        ("Hi!", True),
    ],
)
def test_is_translatable(text: str, expected: bool) -> None:
    assert is_translatable(text) is expected


@pytest.mark.parametrize(
    "text, expected",
    [
        ("https://example.com", True),
        ("www.huggingface.co", True),
        ("Helsinki-NLP/opus-mt", True),
        ("12345", True),
        ("--->", True),
        ("Hello world", False),      # real prose — not untranslatable
        ("你好世界", False),
        ("", True),
    ],
)
def test_is_untranslatable_content(text: str, expected: bool) -> None:
    assert is_untranslatable_content(text) is expected


def test_normalize_collapses_whitespace() -> None:
    assert normalize("  Hello   world  ") == "hello   world".replace("   ", " ")
    assert normalize("") == ""
    assert normalize("\u3000\u3000你好\u3000") == "你好"
