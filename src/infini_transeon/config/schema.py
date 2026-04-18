"""Pydantic models for user-facing configuration.

The schema is the source of truth: defaults, validation rules and documentation
all live here. YAML on disk is a serialization of this model.
"""

from __future__ import annotations

from enum import StrEnum
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator


class TranslationMode(StrEnum):
    online = "online"
    local = "local"


class LocalEngine(StrEnum):
    madlad = "madlad"


class MadladVariant(StrEnum):
    """MADLAD-400 size variant. 3B is the default; 7B requires a decent GPU."""

    v3b = "3b"
    v7b = "7b"


class StyleMode(StrEnum):
    general = "general"
    gaming = "gaming"
    technical = "technical"
    casual = "casual"
    literary = "literary"


# --- providers -------------------------------------------------------------


ProtocolLiteral = Literal["openai_compat", "anthropic", "google", "mymemory"]


class ProviderConfig(BaseModel):
    """Configuration for an online translation provider.

    Users fill ``base_url`` / ``model`` / ``api_key_ref`` themselves; we do not
    ship presets.
    """

    model_config = ConfigDict(str_strip_whitespace=True, extra="forbid")

    protocol: ProtocolLiteral = "openai_compat"
    base_url: str | None = Field(
        default=None,
        description=(
            "Endpoint URL. Required for openai_compat and anthropic protocols. "
            "Google Gemini and MyMemory ignore this field."
        ),
    )
    model: str | None = Field(
        default=None,
        description="Model identifier at the provider (e.g. 'gpt-4o-mini').",
    )
    api_key_ref: str | None = Field(
        default=None,
        description="keyring:// URI to resolve the API key. None means no key.",
    )
    temperature: Annotated[float, Field(ge=0.0, le=2.0)] = 0.3
    max_tokens: Annotated[int, Field(ge=1, le=32_768)] = 2048
    timeout_seconds: Annotated[float, Field(gt=0, le=600)] = 30.0
    extra_headers: dict[str, str] = Field(default_factory=dict)
    stream: bool = True

    @field_validator("base_url")
    @classmethod
    def _strip_trailing_slash(cls, v: str | None) -> str | None:
        if v is None:
            return None
        v = v.strip()
        return v.rstrip("/") or None


class OnlineConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    primary: ProviderConfig = Field(default_factory=ProviderConfig)
    fallback_to_local: bool = True
    last_resort_mymemory: bool = True


class MadladConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    variant: MadladVariant = MadladVariant.v3b
    # "auto" picks int8_float16 on CUDA (fits 4 GB cards for 3B, 8 GB for 7B)
    # and int8 on CPU. The pre-converted CT2 weights are int8-quantized, so
    # requesting pure float16 here just doubles VRAM without improving
    # quality — beam_size / max_decoding_length are the real quality knobs.
    compute_type: Literal["auto", "int8", "int8_float16", "float16", "float32"] = "auto"
    # "auto" prefers CUDA when present, falls back to CPU. Runtime guards
    # refuse 7B on <7 GB VRAM and auto-fallback to CPU on OOM.
    device: Literal["auto", "cpu", "cuda"] = "auto"
    # Beam=5 is the MADLAD paper's recommended setting for quality. Lower
    # (1-2) speeds decode but visibly degrades coherence on short/technical
    # input; higher (8+) has diminishing returns. Combined with the ngram
    # and repetition guards in madlad.py, this stays clear of the
    # single-word-loop failure mode without capping output quality.
    beam_size: Annotated[int, Field(ge=1, le=8)] = 5
    # 192 tokens covers realistic game-dialog / UI sentences in every target
    # language. Shorter caps truncated long lines; longer caps waste decode
    # time on short inputs (the model already emits EOS earlier anyway).
    max_decoding_length: Annotated[int, Field(ge=32, le=1024)] = 192
    # Cap per-call batch size to keep VRAM bounded on low-end GPUs; paragraphs
    # beyond this cap are processed in subsequent batches. 32 is a sweet spot
    # on mid-range consumer GPUs (12 GB VRAM class) — smaller caps leave the
    # GPU underutilized on dense UIs where a single tick can carry 50-100
    # short paragraphs, and 7B int8_float16 KV-cache at batch 32 for ≤192
    # decoding tokens still fits comfortably in ~1 GB of activation VRAM.
    max_batch_size: Annotated[int, Field(ge=1, le=64)] = 32


class LocalConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    engine: LocalEngine = LocalEngine.madlad
    madlad: MadladConfig = Field(default_factory=MadladConfig)


# --- translation features --------------------------------------------------


class GlossaryEntry(BaseModel):
    model_config = ConfigDict(extra="forbid")

    source: str
    target: str
    case_sensitive: bool = False
    languages: list[str] = Field(
        default_factory=list,
        description="Restrict entry to these target languages. Empty = any.",
    )


class TranslationConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    mode: TranslationMode = TranslationMode.online
    source_lang: str = Field(default="auto", description="ISO 639-1 or 'auto'.")
    target_lang: str = Field(default="en", description="ISO 639-1 code.")
    style: StyleMode = StyleMode.general
    online: OnlineConfig = Field(default_factory=OnlineConfig)
    local: LocalConfig = Field(default_factory=LocalConfig)
    glossary: list[GlossaryEntry] = Field(default_factory=list)
    cache_size: Annotated[int, Field(ge=0, le=1_000_000)] = 5000
    budget_monthly_usd: Annotated[float, Field(ge=0)] = 5.0


# --- OCR / detect / overlay -----------------------------------------------


class OcrConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    # 0.35 matches what PP-OCR returns for genuinely-correct output on small
    # UI text. The earlier 0.6 default dropped legitimate detections that
    # happened to score in the 0.45-0.6 band.
    min_confidence: Annotated[float, Field(ge=0.0, le=1.0)] = 0.35
    # Typo correction is a net-loss on low-confidence output (it invents
    # plausible-but-wrong words from OCR noise). Orchestrator only applies
    # it when a block's confidence clears ``postcorrect_min_confidence``.
    enable_postcorrect: bool = True
    postcorrect_min_confidence: Annotated[float, Field(ge=0.0, le=1.0)] = 0.75
    paddle_lang: str = Field(
        default="ch",
        description=(
            "PaddleOCR language code ('ch' unified model covers Chinese + "
            "English + Japanese + pinyin; 'en', 'korean' etc. for others)."
        ),
    )
    use_gpu: bool = False
    # CLAHE + denoise + smart-upscale before handing the frame to OCR. Big
    # quality lift on small UI text; trivial runtime cost vs. OCR itself.
    enable_preprocess: bool = True
    # Upper bound on the detector's input long side (pixels). RapidOCR's
    # default of 1280 triples-downscales a 4K-at-175% window capture and
    # destroys small glyphs. 3840 preserves native 4K; drop to 2560 or 1280
    # on CPU-only setups where detection time dominates.
    det_max_side: Annotated[int, Field(ge=640, le=7680)] = 3840
    # Detector model tier. "server" is the high-accuracy PP-OCRv5 det
    # (~100-300MB more VRAM, +100-200ms per frame on GPU, noticeably better
    # small-text and cluttered-UI recall). "mobile" is the lightweight tier
    # — ~5x faster on CPU, weaker on dense UIs and small fonts.
    det_tier: Literal["mobile", "server"] = "server"


class CaptureConfig(BaseModel):
    """User-controlled capture loop behaviour."""

    model_config = ConfigDict(extra="forbid")

    mode: Literal["window", "region"] = Field(
        default="window",
        description="Capture source: pick a whole window, or a fixed screen region.",
    )
    ocr_interval_seconds: Annotated[float, Field(ge=0.2, le=30.0)] = Field(
        default=1.0,
        description="Seconds between OCR passes.",
    )


class OverlayConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    font_family: str = "system"
    font_scale: Annotated[float, Field(gt=0, le=4)] = 1.0
    bg_opacity: Annotated[float, Field(ge=0.0, le=1.0)] = 0.75
    bg_color: str = "#000000"
    text_color: str = "#FFFFFF"
    show_original_on_hover: bool = True
    click_through: bool = Field(
        default=True,
        description="When True, overlay ignores mouse events so clicks hit the window beneath.",
    )
    high_contrast: bool = Field(
        default=False,
        description="Override bg/text colors with a black-on-yellow high-contrast preset.",
    )


class HotkeysConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    toggle: str = "ctrl+alt+t"
    pick_window: str = "ctrl+alt+w"
    retry_online: str = "ctrl+alt+r"
    translate_now: str = "ctrl+alt+space"


class UpdateConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    auto_check: bool = True
    auto_apply: bool = False
    check_interval_hours: Annotated[int, Field(ge=1, le=168)] = 24
    channel: Literal["stable", "beta"] = "stable"


# --- root -----------------------------------------------------------------


class AppConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    version: Literal[1] = 1
    translation: TranslationConfig = Field(default_factory=TranslationConfig)
    ocr: OcrConfig = Field(default_factory=OcrConfig)
    capture: CaptureConfig = Field(default_factory=CaptureConfig)
    overlay: OverlayConfig = Field(default_factory=OverlayConfig)
    hotkeys: HotkeysConfig = Field(default_factory=HotkeysConfig)
    updates: UpdateConfig = Field(default_factory=UpdateConfig)
    ui_language: str = Field(
        default="auto",
        description="UI language code (e.g. 'en', 'zh', 'ja') or 'auto' for system locale.",
    )
    theme: Literal["auto", "light", "dark"] = Field(
        default="auto",
        description="App UI theme. 'auto' follows the operating system.",
    )
    first_run_completed: bool = False


__all__ = [
    "AppConfig",
    "TranslationConfig",
    "TranslationMode",
    "OnlineConfig",
    "LocalConfig",
    "LocalEngine",
    "MadladConfig",
    "MadladVariant",
    "ProviderConfig",
    "ProtocolLiteral",
    "StyleMode",
    "GlossaryEntry",
    "OcrConfig",
    "CaptureConfig",
    "OverlayConfig",
    "HotkeysConfig",
    "UpdateConfig",
]
