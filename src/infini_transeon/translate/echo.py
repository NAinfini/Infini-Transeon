"""Translation echo / passthrough classifier.

A single pure function ``classify(source, target, target_lang)`` categorizes
an MT result into one of four verdicts. Used by the pipeline to decide
whether to render, cache, or reject the output.

Decision bands (in order of strictness):

* ``hard_echo``    — target is pure source-script, source *had* translatable
  content, and target_lang uses a different script. Safe to reject: the
  provider didn't understand the pair (classic MyMemory failure mode).
* ``partial_echo`` — output mixes non-target-script chunks with target-script
  chunks. Accept for display but *don't* cache, so the next tick retries
  and we don't freeze a half-broken translation onto the overlay.
* ``passthrough``  — output equals source (post-normalize) *and* the source
  is untranslatable content (URL, bare identifier, digits, punctuation).
  Accept and cache: this is correct behaviour, not an echo.
* ``translated``   — everything else. Accept and cache.

Same-script pairs (es<->en, zh<->ja) can never be classified as ``hard_echo``
because we have no way to tell translation from passthrough on output text
alone. They always fall to ``translated`` and the downstream cache keys each
entry by (source_lang, target_lang) so no ambiguity arises in practice.
"""

from __future__ import annotations

import re
from enum import StrEnum


class EchoVerdict(StrEnum):
    translated = "translated"
    passthrough = "passthrough"
    partial_echo = "partial_echo"
    hard_echo = "hard_echo"


# --- script detection -----------------------------------------------------

_CJK_RE = re.compile(
    r"[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af\uf900-\ufaff\uff66-\uff9f]"
)
_LATIN_RE = re.compile(r"[A-Za-z]")
_CYRILLIC_RE = re.compile(r"[\u0400-\u04FF]")
_ARABIC_RE = re.compile(r"[\u0600-\u06FF]")

_SCRIPT_PROBES: dict[str, re.Pattern[str]] = {
    "cjk": _CJK_RE,
    "latin": _LATIN_RE,
    "cyrillic": _CYRILLIC_RE,
    "arabic": _ARABIC_RE,
}

_LANG_TO_SCRIPT: dict[str, str] = {
    "en": "latin", "es": "latin", "fr": "latin", "de": "latin", "pt": "latin",
    "it": "latin", "nl": "latin", "tr": "latin", "vi": "latin", "id": "latin",
    "zh": "cjk", "ja": "cjk", "ko": "cjk",
    "ru": "cyrillic", "uk": "cyrillic", "bg": "cyrillic",
    "ar": "arabic", "fa": "arabic",
}


def script_of_lang(target_lang: str) -> str | None:
    if not target_lang:
        return None
    code = target_lang.split("-", 1)[0].lower()
    return _LANG_TO_SCRIPT.get(code)


# --- text shape helpers ---------------------------------------------------

_NORM_STRIP = re.compile(r"[\s\u3000]+")
_NORM_EDGE = re.compile(r"^[\W_]+|[\W_]+$", re.UNICODE)
_LETTER_RE = re.compile(r"[^\W\d_]", re.UNICODE)
_URL_RE = re.compile(r"https?://|www\.|\w+\.(com|net|org|io|co|ai|dev)\b", re.IGNORECASE)
_IDENTIFIER_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_\-./]*$")


def normalize(text: str) -> str:
    """Collapse whitespace, trim edge punctuation, casefold ASCII."""
    if not text:
        return ""
    collapsed = _NORM_STRIP.sub(" ", text).strip()
    collapsed = _NORM_EDGE.sub("", collapsed)
    return collapsed.casefold()


def is_translatable(text: str) -> bool:
    """True when the fragment has enough linguistic content to be worth MT.

    Filters out OCR jitter like "->", "--", "1x", "$0.01" that dominate the
    overlay with no real content to translate.
    """
    stripped = text.strip()
    if len(stripped) < 2:
        return False
    if not _LETTER_RE.search(stripped):
        return False
    return True


def is_untranslatable_content(text: str) -> bool:
    """True when passing the text through verbatim is the correct output.

    URLs, bare identifiers, and strings with no letters at all are expected
    to survive MT unchanged — accepting them as ``passthrough`` is correct.
    """
    stripped = text.strip()
    if not stripped:
        return True
    if not _LETTER_RE.search(stripped):
        return True
    if _URL_RE.search(stripped):
        return True
    # Pure identifier (no spaces, only alnum + common id punctuation).
    if " " not in stripped and _IDENTIFIER_RE.match(stripped):
        return True
    return False


# --- classifier -----------------------------------------------------------


def classify(source: str, target: str, target_lang: str) -> EchoVerdict:
    """Categorize an MT result. See module docstring for the four bands."""
    # Untranslatable content that came through verbatim is always a
    # passthrough, regardless of script analysis (norm of punct-only strings
    # collapses to empty and trips the wrong branches below otherwise).
    if source.strip() == target.strip() and is_untranslatable_content(source):
        return EchoVerdict.passthrough

    norm_src = normalize(source)
    norm_tgt = normalize(target)
    if not norm_tgt:
        return EchoVerdict.hard_echo

    target_script = script_of_lang(target_lang)
    # Unknown target language — no script-based reasoning possible. Accept
    # anything non-empty as translated.
    if target_script is None:
        return EchoVerdict.translated

    source_scripts = {
        name for name, probe in _SCRIPT_PROBES.items() if probe.search(source)
    }

    # Exact normalized match: legitimate short-brand passthrough or real echo.
    if norm_src == norm_tgt:
        if source_scripts and target_script not in source_scripts:
            # Short proper-noun / brand echoes are almost always correct.
            # Heuristic: <=2 whitespace-separated tokens, <=24 chars, ASCII,
            # Title-Cased, no sentence-ending punctuation. Accepts "Hugging
            # Face" / "GitHub" / "OpenAI" while rejecting "This is English"
            # (3 tokens) or "this is a test" (lowercase / sentence-shaped).
            stripped = source.strip()
            is_brandlike = (
                len(stripped) <= 24
                and len(stripped.split()) <= 2
                and stripped.isascii()
                and stripped[-1].isalnum()
                and stripped[0].isupper()
            )
            if is_brandlike:
                return EchoVerdict.passthrough
            return EchoVerdict.hard_echo
        # Same-script pair — can't distinguish echo from legitimate identity.
        return EchoVerdict.translated

    # Different strings. Does the output contain ANY target-script content?
    target_has_target_script = bool(_SCRIPT_PROBES[target_script].search(target))
    if target_has_target_script:
        # Mixed outputs (preserved brand + translated prose) are correct.
        return EchoVerdict.translated

    # Output differs from source but has zero target-script chars. Either
    # the MT transformed to yet a third script (unlikely), or it echoed
    # something source-shaped. If source_lang and target_lang differ in
    # script and source still dominates the output, that's partial echo —
    # render but don't cache so we retry.
    if source_scripts and target_script not in source_scripts:
        source_still_dominates = any(
            _SCRIPT_PROBES[s].search(target) for s in source_scripts
        )
        if source_still_dominates:
            return EchoVerdict.partial_echo
        return EchoVerdict.hard_echo

    # Same-script pair with no target-script chars somehow (rare). Accept.
    return EchoVerdict.translated


__all__ = [
    "EchoVerdict",
    "classify",
    "is_translatable",
    "is_untranslatable_content",
    "normalize",
    "script_of_lang",
]
