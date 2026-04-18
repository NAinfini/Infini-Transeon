# Infini-Transeon — Architecture

**Cross-platform screen-translation overlay (Windows / macOS / Linux).**
OCR a user-selected window, translate with an LLM or a local NMT model, render the translation on top of the original text.

---

## 1. Product scope

- User points at a window → app OCRs its contents → translates → overlays the translation in place of (or beside) the original text.
- Two strict translation modes, **user-selected**:
  - **Online** — LLM APIs (OpenAI-compatible / Anthropic / Google / free MyMemory fallback).
  - **Local** — Opus-MT per-pair NMT (default) or ALMA-7B (optional upgrade).
- **One-way degradation only**: `Online` may fall back to `Local`. `Local` **never** falls back to `Online` (privacy guarantee).
- Change detection avoids re-translating unchanged text. Translation memory (TM) caches repeated strings.

## 2. Tech stack

| Layer | Tech |
|---|---|
| Language | Python 3.12 |
| Package manager | uv |
| UI | PySide6 (Qt 6) |
| Capture | mss + `pywin32` / `pyobjc-framework-Quartz` / `python-xlib` |
| OCR | PaddleOCR (PP-OCRv5) + SymSpell post-correction |
| Change detection | imagehash (pHash) |
| Online translation | `openai` (OpenAI-compat), `anthropic`, `google-genai`, `deep-translator` (MyMemory) |
| Local translation — default | Opus-MT (Helsinki-NLP) via CTranslate2 int8, per-pair on-demand |
| Local translation — optional upgrade | ALMA-7B-R (Q4_K_M GGUF) via `llama-cpp-python` |
| Translation memory | diskcache (SQLite) |
| Secrets | keyring (Windows Credential Manager / macOS Keychain / Linux Secret Service) |
| Config | pydantic + pydantic-settings + platformdirs |
| Auto-update | tufup (TUF-based) + GitHub Releases |
| Packaging | PyInstaller → NSIS (Win) / DMG+codesign (Mac) / AppImage (Linux) |
| CI/CD | GitHub Actions matrix |

## 3. Translation routing (strict rules)

```
Mode = Online:
    request → OnlineProvider
              ├─ ok → done
              └─ fail → LocalProvider (if configured)
                        ├─ ok → done + UI badge turns yellow
                        └─ fail → MyMemory (if enabled)
                                  ├─ ok → done
                                  └─ fail → error to user

Mode = Local:
    request → LocalProvider
              ├─ ok → done
              └─ fail → error to user   # NEVER contacts the network
```

In Local mode, the router **physically does not construct** any online adapter. No env-based escape.

## 4. Provider model (protocol-driven, zero presets)

Only three protocols are maintained as code; users fill `base_url`, `model`, `api_key` for anything:

```
providers/
  openai_compat.py   # OpenAI / OpenRouter / DeepSeek / Groq / Ollama / LM Studio / xAI / Together / llama.cpp server / any OAI-compat endpoint
  anthropic.py       # Claude Messages API
  google.py          # Gemini (google-genai SDK)
  mymemory.py        # Free fallback, no key
  opus_mt.py         # Local NMT (per-pair)
  alma.py            # Local ALMA-7B via llama.cpp server (OAI-compat reuses openai_compat adapter)
```

User config sketch:

```yaml
translation:
  mode: online                 # online | local

  online:
    primary:
      protocol: openai_compat
      base_url: https://openrouter.ai/api/v1
      model: deepseek/deepseek-chat:free
      api_key_ref: keyring://infini-transeon/online.primary
      temperature: 0.3
      max_tokens: 2048
      timeout_seconds: 30
      extra_headers: {}
    fallback_to_local: true
    last_resort_mymemory: true

  local:
    engine: opus_mt            # opus_mt | alma
    opus_mt:
      downloaded_pairs: [en-zh, zh-en, ja-en]
    alma:
      enabled: false
      model_path: ~/.local/share/infini-transeon/models/alma-7b-r.Q4_K_M.gguf
      gpu_layers: auto
```

## 5. Local translation model strategy (Opus-MT + ALMA)

### Default: Opus-MT, per-pair, on-demand
- Each language pair is a separate ~75 MB CTranslate2-quantized model.
- **No English pivot, no pre-download.** The app downloads only the exact pair the user selects.
- If a pair's direct model does not exist on HuggingFace, the UI shows:
  ```
  "Direct <src> → <tgt> model not available in Opus-MT.
   Upgrade to ALMA-7B (~4 GB) for this pair?"
   [Upgrade to ALMA]  [Cancel]
  ```
- English pivot (two-hop translation) is **not** offered. Quality-first.

### Optional upgrade: ALMA-7B-R (Q4_K_M GGUF, ~4 GB)
- One model covers all 10 major languages with near-GPT-4 quality.
- Runs through `llama-cpp-python` exposing an OpenAI-compatible endpoint; reuses `openai_compat` provider code.
- Enabled only when user opts in (settings toggle → downloads model).

### Supported languages (initial top 10)
English · Chinese (Simplified) · Chinese (Traditional) · Japanese · Korean · Spanish · French · German · Russian · Portuguese. More languages can be added via `languages.yaml` without code changes.

## 6. OCR pipeline

1. **Capture** target window (platform backend).
2. **Change detect** (pHash + stability frames).
3. **PaddleOCR PP-OCRv5** → list of `TextBlock{bbox, text, confidence}`.
4. **Post-process**:
   - Confidence filter.
   - Line-join for wrapped lines (based on bbox proximity + font height).
   - SymSpell typo correction (per language).
5. **Segmentation** → paragraph groups (by bbox spacing / alignment).
6. **Text diff** vs previous OCR result → only changed paragraphs are sent to translate.

## 7. Translation quality engineering

- **Batch translate** one frame's paragraphs in a single LLM call (context consistency, lower per-token overhead).
- **Structured prompt** with role + strict rules (preserve numbers/URLs/entities, output-only-translation).
- **Glossary** (user-editable): forced terms for names/brands/game terms.
- **Style modes**: general / gaming / technical / casual / literary (swappable prompt template).
- **Previous-frame context** as non-translated history to keep style coherent.
- **Streaming responses** from LLM providers → overlay renders progressively.
- **Translation memory** (SQLite via diskcache): text hash → translation, avoids re-paying for identical text.

## 8. Overlay window

- Frameless, always-on-top, translucent Qt window covering the target window's rect.
- Each translated TextBlock rendered at the source bbox with a semi-transparent background.
- **Click-through**: `Qt.WA_TransparentForMouseEvents` (Win/Mac) + XShape (Linux/X11).
- Tracks target window movement, resize, occlusion — pauses when target is minimized/hidden.
- Per-platform DPR (device pixel ratio) normalization for bbox coordinates.

## 9. Change detection

1. Grab target region every `interval_ms` (default 500 ms).
2. Compute pHash of the region.
3. If Hamming distance from previous pHash ≤ `phash_threshold`, treat as unchanged.
4. Require `stability_frames` consecutive identical-signature frames before triggering OCR (anti-flicker).
5. Optional grid-based diff: only OCR the changed cells.

## 10. Config and secrets

- `pydantic-settings` models under `config/schema.py`.
- Persistence: YAML in `platformdirs.user_config_dir("Infini-Transeon")`.
- Secrets: API keys via `keyring`. Config stores only references like `keyring://infini-transeon/online.primary`.
- Never logs/prints API keys; `loguru` is configured to redact known key patterns.

## 11. Auto-update

- `tufup` client runs on startup (offline-tolerant) and on demand from the tray menu.
- Update repository hosted at GitHub Releases (signed targets + metadata).
- Release workflow:
  1. Tag `vX.Y.Z` pushed → GitHub Actions builds per-platform bundle with PyInstaller.
  2. tufup signs & uploads targets/metadata to the release assets.
  3. Client sees newer version, downloads diff patch, verifies signature, restarts.

## 12. Milestones

| M | Goal | Verification |
|---|---|---|
| M0 | Skeleton + CI + config + keyring + Qt tray | `python -m infini_transeon` opens tray on all OS |
| M1 | Windows MVP full loop (OCR → LLM → overlay) | Select window in Win, English text overlaid with Chinese |
| M2 | Change detection + paragraph segmentation + TM + glossary + styles | Stable translations, no flicker, sub-second when cached |
| M3 | Multi-provider (OpenAI-compat / Anthropic / Google / MyMemory) + test connection + usage metering | Test button validates config; budget cap works |
| M4 | macOS adaptation (Quartz + permissions) | Mac: screen recording permission guided; full loop works |
| M5 | Linux X11 adaptation (Xlib + XShape click-through) | X11: full loop works |
| M6 | Settings UI + first-run wizard + glossary editor + style picker | All configuration driven from UI |
| M7 | PyInstaller packaging + NSIS/DMG/AppImage + GitHub Release workflow + tufup auto-update | Signed releases; auto-update end-to-end |
| M8 (P2) | Wayland via xdg-desktop-portal; ALMA-7B local upgrade path; export/import TM | — |

## 13. Repository layout

```
Infini-Transeon/
├── LICENSE
├── README.md
├── pyproject.toml
├── uv.lock                  # generated
├── .gitignore
├── .github/workflows/
│   ├── ci.yml
│   └── release.yml
├── docs/
│   └── ARCHITECTURE.md
├── src/infini_transeon/
│   ├── __init__.py
│   ├── __main__.py
│   ├── app.py
│   ├── config/
│   │   ├── __init__.py
│   │   ├── schema.py        # pydantic models
│   │   ├── defaults.py
│   │   ├── loader.py        # read/write YAML
│   │   ├── secrets.py       # keyring wrapper
│   │   ├── paths.py         # platformdirs wrapper
│   │   └── languages.yaml
│   ├── pipeline/
│   │   ├── __init__.py
│   │   ├── orchestrator.py
│   │   └── events.py
│   ├── capture/
│   │   ├── __init__.py
│   │   ├── base.py
│   │   ├── windows.py
│   │   ├── macos.py
│   │   ├── linux_x11.py
│   │   └── linux_wayland.py # P2
│   ├── detect/
│   │   ├── __init__.py
│   │   ├── change.py
│   │   └── text_diff.py
│   ├── ocr/
│   │   ├── __init__.py
│   │   ├── base.py
│   │   ├── paddle.py
│   │   ├── postprocess.py
│   │   └── segmentation.py
│   ├── translate/
│   │   ├── __init__.py
│   │   ├── base.py
│   │   ├── router.py
│   │   ├── cache.py
│   │   ├── context.py
│   │   ├── glossary.py
│   │   ├── usage.py
│   │   ├── prompts.py
│   │   └── providers/
│   │       ├── __init__.py
│   │       ├── openai_compat.py
│   │       ├── anthropic.py
│   │       ├── google.py
│   │       ├── mymemory.py
│   │       ├── opus_mt.py
│   │       └── alma.py
│   ├── overlay/
│   │   ├── __init__.py
│   │   ├── window.py
│   │   ├── renderer.py
│   │   └── click_through.py
│   ├── ui/
│   │   ├── __init__.py
│   │   ├── tray.py
│   │   ├── window_picker.py
│   │   ├── first_run.py
│   │   └── settings/
│   │       ├── __init__.py
│   │       ├── dialog.py
│   │       ├── provider_form.py
│   │       ├── local_models.py
│   │       ├── glossary_editor.py
│   │       ├── hotkeys_panel.py
│   │       └── usage_dashboard.py
│   ├── platform/
│   │   ├── __init__.py
│   │   ├── hotkey.py
│   │   └── permissions.py
│   ├── models/
│   │   └── downloader.py
│   ├── updater/
│   │   └── tufup_client.py
│   └── utils/
│       ├── __init__.py
│       ├── logging.py
│       └── dpi.py
├── tests/
│   ├── unit/
│   └── integration/
└── release/                 # packaging/update scripts (generated artifacts ignored)
```
