# Infini-Transeon

[English](#english) · [中文](#中文)

Cross-platform screen-translation overlay for **Windows, macOS, and Linux**.
Point at a window (or drag a region), the app OCRs its contents, translates
with an LLM (online) or a local NMT model (offline), and renders the
translation on top of the original text.

> Status: **pre-alpha** (`v0.1.0`). Skeleton is landing milestone by
> milestone. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## English

### Highlights

- **Two modes, user-picked.** Online uses any OpenAI-compatible / Anthropic /
  Google Gemini endpoint. Local uses MADLAD-400 (3B default, 7B optional)
  via CTranslate2.
- **Strict privacy guarantee.** Local mode never contacts the network.
  Degradation is one-way (Online → Local, never the reverse).
- **Zero vendor presets.** You fill `base_url`, `model`, `api_key` yourself.
  Works with OpenRouter, OpenAI, Claude, Gemini, DeepSeek, Groq, Ollama,
  LM Studio, llama.cpp server, xAI, Together, or any OAI-compatible endpoint.
- **On-demand local models.** 3B is the default (~3 GB, fits 4 GB VRAM);
  opt into 7B in Settings → Local models if you have ~8 GB VRAM.
- **Quality engineering.** Paragraph segmentation, context injection,
  glossary, style prompts, OCR post-correction, translation memory.
- **Soft resource governor.** CPU / RAM / VRAM pressure triggers a
  progressive, reversible slowdown (longer OCR interval, smaller MADLAD
  beam/batch) instead of an OOM. Returns to your settings when pressure
  clears.
- **Auto-updates** via `tufup` + GitHub Releases (requires a signed TUF
  root; disabled out-of-the-box).

### Requirements

- Python 3.12+
- OS: Windows 10+ / macOS 12+ / Linux (X11 for MVP; Wayland planned)
- Optional GPU: CUDA for RapidOCR / CTranslate2 / llama.cpp (install the
  `gpu-cuda` extra to get the bundled NVIDIA runtime wheels)

### Dev setup

```bash
# Install uv (https://docs.astral.sh/uv/)
# then:
uv sync --all-extras
uv run infini-transeon
```

### Install from the release

```bash
# From the downloaded wheel
pip install infini_transeon-0.1.0-py3-none-any.whl
infini-transeon
```

`gpu-cuda` extra is recommended on machines with an NVIDIA GPU:

```bash
pip install "infini_transeon-0.1.0-py3-none-any.whl[gpu-cuda]"
```

### Provider examples (online)

Enter these yourself in **Settings → Provider**. No presets are stored
in the code.

| Provider | Protocol | Base URL | Example model |
|---|---|---|---|
| OpenRouter | OpenAI-compatible | `https://openrouter.ai/api/v1` | `deepseek/deepseek-chat:free` |
| OpenAI | OpenAI-compatible | `https://api.openai.com/v1` | `gpt-4o-mini` |
| Anthropic | Anthropic | `https://api.anthropic.com` | `claude-sonnet-4-6` |
| Google Gemini | Google Gemini | (auto) | `gemini-2.5-flash` |
| DeepSeek | OpenAI-compatible | `https://api.deepseek.com` | `deepseek-chat` |
| Ollama | OpenAI-compatible | `http://localhost:11434/v1` | `gemma3:4b` |
| LM Studio | OpenAI-compatible | `http://localhost:1234/v1` | (leave key blank) |

MyMemory (free, no key) is available as a last-resort fallback —
toggle it in **Settings → General**. Quality is inconsistent; keep a
real provider configured as primary.

### Local translation (offline)

- **Default: MADLAD-400 3B** (~3 GB, CTranslate2 int8/int8_float16).
  Downloaded on first use from HuggingFace; one model covers 450+
  languages. Fits 4 GB VRAM; auto-falls back to CPU if CUDA fails.
- **Optional upgrade: MADLAD-400 7B** (~8 GB). Higher quality,
  especially on technical text. Needs ~8 GB VRAM. Opt in from
  **Settings → Local models**.

### Building from source

```bash
uv sync --all-extras
uv run pytest           # 63 tests
uv build                # dist/*.whl + dist/*.tar.gz
```

### License

MIT. See [`LICENSE`](LICENSE).

---

## 中文

### 功能亮点

- **双模式可切换。** 在线模式支持任何 OpenAI 兼容 / Anthropic / Google Gemini
  端点；本地模式通过 CTranslate2 运行 MADLAD-400（默认 3B，可选 7B）。
- **严格隐私。** 本地模式绝不联网，降级路径单向（在线 → 本地，反之不会）。
- **零厂商预设。** `base_url`、`model`、`api_key` 全部由你自己填。OpenRouter、
  OpenAI、Claude、Gemini、DeepSeek、Groq、Ollama、LM Studio、llama.cpp server、
  xAI、Together 等 OAI 兼容端点均可。
- **本地模型按需下载。** 默认 3B（约 3 GB，4 GB 显存够用）；若显存 ≥ 8 GB
  可在「设置 → 本地模型」里升级到 7B。
- **质量工程。** 段落切分、上下文注入、术语表、风格提示、OCR 后处理、翻译记忆。
- **软资源限额。** CPU / 内存 / 显存吃紧时自动渐进降级（延长 OCR 间隔、缩小
  MADLAD beam/batch 等），资源恢复后自动还原你的设置，不会 OOM 杀进程。
- **自动更新** 基于 `tufup` + GitHub Releases（需要已签名的 TUF root；默认关闭）。

### 运行要求

- Python 3.12+
- 操作系统：Windows 10+ / macOS 12+ / Linux（当前 MVP 支持 X11，计划支持 Wayland）
- 可选 GPU：NVIDIA CUDA（RapidOCR / CTranslate2 / llama.cpp 可用）；安装
  `gpu-cuda` 附加依赖即可一并带上 NVIDIA 运行时 DLL

### 开发环境

```bash
# 先安装 uv：https://docs.astral.sh/uv/
uv sync --all-extras
uv run infini-transeon
```

### 使用 Release 安装

```bash
pip install infini_transeon-0.1.0-py3-none-any.whl
infini-transeon
```

有 NVIDIA 显卡时推荐带上 `gpu-cuda`：

```bash
pip install "infini_transeon-0.1.0-py3-none-any.whl[gpu-cuda]"
```

### 在线服务示例

在「设置 → 在线服务」里自行填写；代码里不内置任何预设。

| 服务 | 协议 | Base URL | 示例模型 |
|---|---|---|---|
| OpenRouter | OpenAI 兼容 | `https://openrouter.ai/api/v1` | `deepseek/deepseek-chat:free` |
| OpenAI | OpenAI 兼容 | `https://api.openai.com/v1` | `gpt-4o-mini` |
| Anthropic | Anthropic | `https://api.anthropic.com` | `claude-sonnet-4-6` |
| Google Gemini | Google Gemini | 自动 | `gemini-2.5-flash` |
| DeepSeek | OpenAI 兼容 | `https://api.deepseek.com` | `deepseek-chat` |
| Ollama | OpenAI 兼容 | `http://localhost:11434/v1` | `gemma3:4b` |
| LM Studio | OpenAI 兼容 | `http://localhost:1234/v1` | （API Key 留空） |

MyMemory（免费、无密钥）可作为兜底——在「设置 → 通用」里开启。质量不稳定，
请保留一个真正的主服务。

### 本地翻译（离线）

- **默认：MADLAD-400 3B**（约 3 GB，CTranslate2 int8/int8_float16）。首次
  使用时从 HuggingFace 拉取；一个模型覆盖 450+ 种语言。4 GB 显存足够；
  CUDA 出问题时自动退回 CPU。
- **可选升级：MADLAD-400 7B**（约 8 GB）。技术文本质量更高；需要约 8 GB
  显存。在「设置 → 本地模型」里启用。

### 源码构建

```bash
uv sync --all-extras
uv run pytest           # 63 项测试
uv build                # 产物在 dist/
```

### 开源许可

MIT，详见 [`LICENSE`](LICENSE)。
