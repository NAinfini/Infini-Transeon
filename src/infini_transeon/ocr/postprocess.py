"""Lightweight OCR post-processing helpers.

SymSpell-based correction is best-effort: we only apply it when an English
dictionary is available. For other languages we currently skip correction and
leave the text unchanged. The module is structured so more languages can drop
in dictionaries without touching call sites.
"""

from __future__ import annotations

from importlib import resources
from threading import Lock

from symspellpy import SymSpell, Verbosity

from infini_transeon.ocr.base import TextBlock

_GARBAGE_CHARS = set("•●◆◇■□▲△▶◀→←↑↓⇒⇐⇔⌂⌘✓✗★☆☐☑")


class _SymSpellLazy:
    def __init__(self) -> None:
        self._engines: dict[str, SymSpell] = {}
        self._lock = Lock()

    def get(self, lang: str) -> SymSpell | None:
        if lang != "en":
            return None
        with self._lock:
            if lang in self._engines:
                return self._engines[lang]
            try:
                sym = SymSpell(max_dictionary_edit_distance=2, prefix_length=7)
                # symspellpy ships a frequency dictionary for English.
                with resources.files("symspellpy").joinpath(
                    "frequency_dictionary_en_82_765.txt"
                ).open("r", encoding="utf-8") as fh:
                    sym.load_dictionary(fh, term_index=0, count_index=1)
            except FileNotFoundError:
                return None
            self._engines[lang] = sym
            return sym


_SYM = _SymSpellLazy()


def correct(text: str, lang: str) -> str:
    """Best-effort typo correction; returns input unchanged if unsupported."""
    sym = _SYM.get(lang)
    if sym is None or not text.strip():
        return text
    words = text.split(" ")
    corrected: list[str] = []
    for word in words:
        stripped = word.strip()
        if not stripped or not stripped.isalpha():
            corrected.append(word)
            continue
        suggestions = sym.lookup(stripped, Verbosity.CLOSEST, max_edit_distance=1)
        if suggestions and suggestions[0].term.lower() != stripped.lower():
            head = word[: len(word) - len(stripped)]
            tail = word[len(head) + len(stripped) :]
            corrected.append(head + suggestions[0].term + tail)
        else:
            corrected.append(word)
    return " ".join(corrected)


def clean(text: str) -> str:
    """Strip UI ornament characters and collapse whitespace."""
    filtered = "".join(ch for ch in text if ch not in _GARBAGE_CHARS)
    # Collapse runs of internal whitespace to a single space.
    return " ".join(filtered.split()).strip()


def filter_low_confidence(blocks: list[TextBlock], min_conf: float) -> list[TextBlock]:
    return [b for b in blocks if b.confidence >= min_conf]


def apply(blocks: list[TextBlock], *, lang: str = "en", enable_typo: bool = True) -> list[TextBlock]:
    """Run the full cleanup pipeline on a block list."""
    out: list[TextBlock] = []
    for block in blocks:
        text = clean(block.text)
        if enable_typo:
            text = correct(text, lang)
        if not text:
            continue
        out.append(
            TextBlock(
                bbox=block.bbox,
                text=text,
                confidence=block.confidence,
                language=block.language,
            )
        )
    return out


__all__ = ["apply", "clean", "correct", "filter_low_confidence"]
