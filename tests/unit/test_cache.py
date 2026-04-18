from __future__ import annotations

from infini_transeon.translate.cache import CacheEntry, TranslationMemory, make_key


def test_cache_hit_after_set() -> None:
    tm = TranslationMemory()
    key = make_key(
        "hello",
        source_lang="en",
        target_lang="zh",
        style="general",
        glossary=(),
    )
    assert tm.get(key) is None
    tm.set(key, CacheEntry(text="你好", provider="test"))
    entry = tm.get(key)
    assert entry is not None
    assert entry.text == "你好"
    assert entry.provider == "test"
    tm.close()


def test_cache_key_is_stable() -> None:
    a = make_key("x", source_lang="en", target_lang="zh", style="general", glossary=(("a", "b"),))
    b = make_key("x", source_lang="en", target_lang="zh", style="general", glossary=(("a", "b"),))
    assert a == b


def test_cache_key_glossary_order_insensitive() -> None:
    a = make_key(
        "x",
        source_lang="en",
        target_lang="zh",
        style="general",
        glossary=(("a", "b"), ("c", "d")),
    )
    b = make_key(
        "x",
        source_lang="en",
        target_lang="zh",
        style="general",
        glossary=(("c", "d"), ("a", "b")),
    )
    assert a == b
