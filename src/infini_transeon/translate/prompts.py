"""Prompt templates for LLM-based translation.

Prompts are parameterized by style and include a strict protocol for batched
numbered-line output so we can reliably map outputs back onto input bboxes.
"""

from __future__ import annotations

from collections.abc import Sequence

from infini_transeon.config.schema import StyleMode

_STYLE_HINTS: dict[StyleMode, str] = {
    StyleMode.general: (
        "general prose; preserve tone, register, and factual precision"
    ),
    StyleMode.gaming: (
        "video-game UI and dialogue; short, punchy, idiomatic; never literal; "
        "keep skill/item/character names as-is unless a standard localization exists"
    ),
    StyleMode.technical: (
        "technical documentation; preserve terminology, code, identifiers, "
        "CLI flags, URLs, and file paths verbatim"
    ),
    StyleMode.casual: (
        "casual conversational tone; contractions allowed; natural phrasing"
    ),
    StyleMode.literary: (
        "literary prose; maintain rhythm, metaphor, and stylistic voice"
    ),
}


def build_system_prompt(
    *,
    source_lang: str,
    target_lang: str,
    style: StyleMode,
    glossary: Sequence[tuple[str, str]],
    history: Sequence[str],
) -> str:
    glossary_lines = "\n".join(f"- {src} => {tgt}" for src, tgt in glossary) or "(none)"
    history_lines = "\n".join(f"- {h}" for h in history) or "(none)"
    src_label = "auto-detect" if source_lang == "auto" else source_lang
    return (
        "You are a professional translator specialized in on-screen UI and text.\n\n"
        f"TASK: Translate from {src_label} to {target_lang}.\n"
        f"STYLE CONTEXT: {_STYLE_HINTS[style]}.\n\n"
        "STRICT RULES:\n"
        "1. Preserve meaning, tone and register faithfully.\n"
        "2. Preserve ALL formatting exactly: numbers, URLs, emails, code, placeholders, punctuation.\n"
        "3. Preserve proper nouns (names, brands, products) unless a common localized form exists.\n"
        "4. Apply the glossary below verbatim whenever a term matches.\n"
        "5. If an input line is already in the target language, return it unchanged.\n"
        "6. For short UI strings, use natural equivalents, not literal translations.\n"
        "7. Do NOT add commentary, quotes, prefixes, or explanations.\n"
        "8. Output exactly one line per input line, in the same order, with no extra blank lines.\n\n"
        f"GLOSSARY:\n{glossary_lines}\n\n"
        f"RECENT CONTEXT (do NOT translate, for style continuity only):\n{history_lines}\n"
    )


def build_user_prompt(texts: Sequence[str]) -> str:
    numbered = "\n".join(f"{i + 1}. {t}" for i, t in enumerate(texts))
    return (
        f"Translate each of the following {len(texts)} line(s). "
        "Output exactly the same count of lines, numbered identically, "
        "with only the translation after the number.\n\n"
        f"{numbered}"
    )


def parse_numbered_output(raw: str, expected: int) -> list[str]:
    """Extract translations from a model's numbered response.

    We tolerate missing/extra whitespace and minor numbering drift. If the
    model returns fewer lines than expected, remaining slots are left empty.
    """
    lines = [ln.rstrip() for ln in raw.strip().splitlines() if ln.strip()]
    out: list[str] = [""] * expected
    for ln in lines:
        text = ln.lstrip()
        # Strip leading "123.", "123)", "[123]", "(123)" style prefixes.
        for sep in (". ", ") ", "] ", "- ", ": "):
            if sep in text[:8]:
                idx_str, rest = text.split(sep, 1)
                idx_str = idx_str.strip("[]() ")
                if idx_str.isdigit():
                    idx = int(idx_str) - 1
                    if 0 <= idx < expected:
                        out[idx] = rest.strip()
                        break
        else:
            # Unlabeled line — fill the first empty slot.
            for i, slot in enumerate(out):
                if not slot:
                    out[i] = text.strip()
                    break
    return out


__all__ = ["build_system_prompt", "build_user_prompt", "parse_numbered_output"]
