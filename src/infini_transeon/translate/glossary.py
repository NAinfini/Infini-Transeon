"""Glossary matching for forced terminology."""

from __future__ import annotations

from collections.abc import Iterable

from infini_transeon.config.schema import GlossaryEntry


def entries_for_target(
    entries: Iterable[GlossaryEntry], target_lang: str
) -> list[tuple[str, str]]:
    """Return ``(source, target)`` tuples applicable to ``target_lang``."""
    result: list[tuple[str, str]] = []
    for entry in entries:
        if entry.languages and target_lang not in entry.languages:
            continue
        result.append((entry.source, entry.target))
    return result


def apply_forced(
    text: str, pairs: Iterable[tuple[str, str]], *, case_sensitive: bool = False
) -> str:
    """Rewrite forced terms on already-translated text. Useful as a last-mile guarantee."""
    out = text
    for src, tgt in pairs:
        if not src:
            continue
        if case_sensitive:
            out = out.replace(src, tgt)
        else:
            # Case-insensitive replace without regex to avoid pattern injection.
            lower = out.lower()
            target = src.lower()
            if target not in lower:
                continue
            rebuilt: list[str] = []
            i = 0
            while i < len(out):
                if lower[i : i + len(target)] == target:
                    rebuilt.append(tgt)
                    i += len(target)
                else:
                    rebuilt.append(out[i])
                    i += 1
            out = "".join(rebuilt)
    return out


__all__ = ["entries_for_target", "apply_forced"]
