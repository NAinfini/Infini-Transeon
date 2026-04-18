"""MADLAD-400 local NMT provider via CTranslate2.

Ships two variants selected in :class:`MadladConfig`:

* **3B** — default. ~3 GB int8, fits on 4 GB+ GPUs.
* **7B** — opt-in. ~8 GB int8_float16, needs 8 GB+ VRAM.

Runtime behaviour:

* ``device="auto"`` resolves to CUDA when CTranslate2 reports a usable GPU
  and the selected variant fits in VRAM; otherwise CPU.
* ``compute_type="auto"`` picks ``int8_float16`` on GPU (lower VRAM) and
  ``int8`` on CPU (the only precision the non-CUDA wheel supports well).
* On a CUDA OOM at load or translate time we drop the GPU translator and
  re-load on CPU, then retry the call once. The router surfaces a toast
  so the user knows quality/latency changed.

Target language is selected by prepending the ``<2{code}>`` control token
to each source line — this is the MADLAD convention baked into its
SentencePiece vocabulary.
"""

from __future__ import annotations

import asyncio
import time
from collections.abc import AsyncIterator
from threading import Lock

import ctranslate2
import sentencepiece as spm

from infini_transeon.config.schema import MadladConfig, MadladVariant
from infini_transeon.models.downloader import (
    is_madlad_downloaded,
    madlad_local_dir,
)
from infini_transeon.translate.base import (
    ProviderKind,
    ProviderUnavailable,
    TranslateProvider,
    TranslationRequest,
    TranslationResult,
    UnsupportedPairError,
    Usage,
)
from infini_transeon.utils.logging import logger


# Conservative VRAM floors (MB). CTranslate2 reports free VRAM via
# get_cuda_device_count + device properties; we consult pynvml-style info
# through ctranslate2.get_supported_compute_types where possible, and fall
# back to a hard-coded floor when we can't introspect.
_VARIANT_MIN_VRAM_MB: dict[MadladVariant, int] = {
    MadladVariant.v3b: 3_800,   # fits 4 GB cards
    MadladVariant.v7b: 7_800,   # needs 8 GB cards
}


class MadladProvider(TranslateProvider):
    """Local NMT via MADLAD-400 (CT2 int8 / int8_float16)."""

    name = "madlad"
    kind = ProviderKind.local

    def __init__(self, config: MadladConfig) -> None:
        self._config = config
        # Coerce to enum in case an upstream caller stashed a raw string via
        # ``model_copy`` (which skips re-validation). Every ``variant`` use
        # below — provider tag, download dir, VRAM check — assumes the enum.
        self._variant: MadladVariant = MadladVariant(config.variant)
        self._translator: ctranslate2.Translator | None = None
        self._sp: spm.SentencePieceProcessor | None = None
        # Planned device/compute-type: what we'd use if load succeeded right
        # now. Revised on load (possibly down to CPU on a CUDA fault) and
        # surfaced to the UI for the "GPU: yes/no, in use: yes/no" status.
        planned_device = _resolve_device(config.device, self._variant)
        self._active_device: str = planned_device
        self._active_compute_type: str = _resolve_compute_type(
            config.compute_type, planned_device, self._variant
        )
        self._lock = Lock()

    def is_available(self) -> bool:
        return is_madlad_downloaded(self._variant)

    @property
    def active_device(self) -> str:
        """'cuda' or 'cpu' — reflects what we're actually running on.

        Before first translate, returns the *planned* device. After the
        first load succeeds, returns the real one (may have fallen back
        from cuda to cpu due to OOM or missing CUDA libs).
        """
        return self._active_device

    @property
    def active_compute_type(self) -> str:
        return self._active_compute_type

    def apply_runtime_config(self, config: MadladConfig) -> None:
        """Update runtime decode knobs without rebuilding the translator.

        Safe to call on the hot path: only ``beam_size``,
        ``max_decoding_length`` and ``max_batch_size`` are read per-call,
        so swapping the held config in place is enough to take effect on
        the next ``translate``. ``variant`` / ``device`` / ``compute_type``
        must NOT change here — those require reloading the CT2 model.
        Router enforces the rebuild branch; this is an assertion so a
        bug in the caller surfaces immediately.
        """
        if (
            MadladVariant(config.variant) != self._variant
            or config.device != self._config.device
            or config.compute_type != self._config.compute_type
        ):
            raise ValueError(
                "apply_runtime_config cannot change variant/device/compute_type; "
                "use Router.reload for those"
            )
        self._config = config

    def translate(self, req: TranslationRequest) -> TranslationResult:
        started = time.monotonic()
        tgt = _normalize(req.target_lang)
        if tgt == "auto":
            raise UnsupportedPairError("MADLAD requires an explicit target language")
        translator, sp = self._ensure_loaded()

        tagged: list[list[str]] = []
        for text in req.texts:
            if not text.strip():
                tagged.append([])
                continue
            pieces = sp.encode(text, out_type=str)
            # CTranslate2's MADLAD convention: source starts with </s>, then
            # the <2xx> language tag, then the SentencePiece pieces. Putting
            # </s> at the end (the transformers HF format) produces garbled
            # single-word loops on this CT2 conversion.
            tagged.append(["</s>", f"<2{tgt}>"] + pieces)

        outputs: list[str] = [""] * len(req.texts)
        batch: list[list[str]] = []
        batch_indices: list[int] = []
        cap = max(1, self._config.max_batch_size)

        def _flush() -> None:
            """Translate the accumulated batch, with one GPU→CPU retry on OOM."""
            nonlocal translator
            if not batch:
                return
            try:
                results = translator.translate_batch(
                    batch,
                    beam_size=self._config.beam_size,
                    max_decoding_length=self._config.max_decoding_length,
                    # Suppresses the "translate once, then repeat verbatim"
                    # failure mode MADLAD drifts into on short inputs that
                    # don't hit an EOS prediction quickly.
                    no_repeat_ngram_size=4,
                    repetition_penalty=1.1,
                )
            except RuntimeError as exc:
                if not _is_cuda_fault(exc) or self._active_device == "cpu":
                    raise
                logger.warning(
                    "MADLAD CUDA fault — falling back to CPU for remainder of session: {}",
                    exc,
                )
                self._reload_on_cpu()
                translator, _ = self._ensure_loaded()
                results = translator.translate_batch(
                    batch,
                    beam_size=self._config.beam_size,
                    max_decoding_length=self._config.max_decoding_length,
                    no_repeat_ngram_size=4,
                    repetition_penalty=1.1,
                )
            for idx, res in zip(batch_indices, results, strict=True):
                hyp = res.hypotheses[0]
                outputs[idx] = sp.decode(hyp)
            batch.clear()
            batch_indices.clear()

        for i, toks in enumerate(tagged):
            if not toks:
                outputs[i] = req.texts[i]
                continue
            batch.append(toks)
            batch_indices.append(i)
            if len(batch) >= cap:
                _flush()
        _flush()

        return TranslationResult(
            translations=tuple(outputs),
            provider=f"madlad::{self._variant.value}::{tgt}",
            kind=self.kind,
            usage=Usage(),
            latency_ms=(time.monotonic() - started) * 1000.0,
        )

    async def atranslate(self, req: TranslationRequest) -> TranslationResult:
        return await asyncio.to_thread(self.translate, req)

    async def astream(self, req: TranslationRequest) -> AsyncIterator[str]:
        result = await self.atranslate(req)
        for line in result.translations:
            yield line + "\n"

    def close(self) -> None:
        with self._lock:
            self._translator = None
            self._sp = None
        # Force a GC cycle so the CTranslate2 translator's VRAM is released
        # synchronously. Without this, a variant swap (7B -> 3B) can fail or
        # stall for tens of seconds because CUDA memory from the previous
        # translator is still held until Python's lazy GC catches up, and
        # the new translator's load call then competes for VRAM with the
        # soon-to-be-freed old one.
        import gc
        gc.collect()

    # --- loading -------------------------------------------------------

    def _ensure_loaded(self) -> tuple[ctranslate2.Translator, spm.SentencePieceProcessor]:
        if self._translator is not None and self._sp is not None:
            return self._translator, self._sp
        with self._lock:
            if self._translator is None or self._sp is None:
                if not is_madlad_downloaded(self._variant):
                    raise ProviderUnavailable(
                        "MADLAD model not downloaded — enable it in Settings → Local models"
                    )
                directory = madlad_local_dir(self._variant)
                device = _resolve_device(self._config.device, self._variant)
                compute_type = _resolve_compute_type(self._config.compute_type, device, self._variant)
                logger.info(
                    "loading MADLAD-400 {} from {} (device={}, compute_type={})",
                    self._variant.value, directory, device, compute_type,
                )
                try:
                    self._translator = ctranslate2.Translator(
                        str(directory),
                        device=device,
                        compute_type=compute_type,
                    )
                except (RuntimeError, ValueError) as exc:
                    if device == "cuda":
                        logger.warning(
                            "MADLAD CUDA load failed ({}) — retrying on CPU", exc,
                        )
                        device = "cpu"
                        compute_type = _resolve_compute_type(self._config.compute_type, device, self._variant)
                        self._translator = ctranslate2.Translator(
                            str(directory),
                            device=device,
                            compute_type=compute_type,
                        )
                    else:
                        raise
                self._active_device = device
                self._active_compute_type = compute_type
                sp = spm.SentencePieceProcessor()
                sp.load(str(directory / "spiece.model"))
                self._sp = sp
        return self._translator, self._sp  # type: ignore[return-value]

    def _reload_on_cpu(self) -> None:
        with self._lock:
            self._translator = None
            self._sp = None
            directory = madlad_local_dir(self._variant)
            compute_type = _resolve_compute_type(self._config.compute_type, "cpu", self._variant)
            self._translator = ctranslate2.Translator(
                str(directory),
                device="cpu",
                compute_type=compute_type,
            )
            self._active_device = "cpu"
            self._active_compute_type = compute_type
            sp = spm.SentencePieceProcessor()
            sp.load(str(directory / "spiece.model"))
            self._sp = sp


# --- device / compute-type resolution --------------------------------------

def _resolve_device(requested: str, variant: MadladVariant) -> str:
    """Pick a device honouring user's explicit choice, with VRAM-aware auto."""
    if requested == "cpu":
        return "cpu"
    if requested == "cuda":
        return "cuda" if _cuda_available() else "cpu"
    # "auto": prefer CUDA only when the variant actually fits
    if not _cuda_available():
        return "cpu"
    free_mb = _cuda_free_vram_mb()
    needed = _VARIANT_MIN_VRAM_MB[variant]
    if free_mb is None or free_mb >= needed:
        return "cuda"
    logger.info(
        "MADLAD {}: GPU has ~{} MB free, needs ~{} MB — using CPU",
        variant.value, free_mb, needed,
    )
    return "cpu"


def _resolve_compute_type(requested: str, device: str, variant: MadladVariant | None = None) -> str:
    if requested != "auto":
        return requested
    if device != "cuda":
        return "int8"
    # The pre-converted CT2 repos we ship are int8-quantized. Requesting
    # compute_type="float16" makes CT2 dequantize int8 -> fp16 at load —
    # double the VRAM, same quality as int8_float16. Pointless. Stay on
    # int8_float16 so 3B fits 4 GB cards and 7B fits 8 GB. Real quality
    # improvements come from beam_size / max_decoding_length, not compute
    # type swaps on these particular weights.
    del variant  # threshold no longer needed
    return "int8_float16"


def _cuda_available() -> bool:
    try:
        return ctranslate2.get_cuda_device_count() > 0
    except Exception:  # noqa: BLE001 — older ct2 builds may not expose this
        return False


def _cuda_free_vram_mb() -> int | None:
    """Best-effort free-VRAM probe. None means 'unknown, assume fits'.

    Tries pynvml (often bundled with modern CUDA wheels); if not available
    we defer to ctranslate2's own check at load time, which will raise
    RuntimeError on actual OOM and the caller will fall back to CPU.
    """
    try:
        import pynvml  # type: ignore[import-not-found]
    except ImportError:
        return None
    try:
        pynvml.nvmlInit()
        handle = pynvml.nvmlDeviceGetHandleByIndex(0)
        info = pynvml.nvmlDeviceGetMemoryInfo(handle)
        return int(info.free // (1024 * 1024))
    except Exception:  # noqa: BLE001
        return None
    finally:
        try:
            pynvml.nvmlShutdown()
        except Exception:  # noqa: BLE001
            pass


def _is_cuda_fault(exc: BaseException) -> bool:
    """True for any CUDA-runtime issue we can recover from by using CPU.

    Covers: OOM, missing cublas/cudnn DLLs, driver version mismatches, and
    'no CUDA-capable device' errors that slip past the pre-load checks.
    """
    msg = str(exc).lower()
    cuda_signals = (
        "out of memory",
        "cublas",
        "cudnn",
        "cuda driver",
        "cuda error",
        "no cuda-capable device",
        "library ",           # "Library xxx.dll is not found"
    )
    return any(s in msg for s in cuda_signals)


def _normalize(code: str) -> str:
    if code == "auto":
        return "auto"
    lowered = code.lower()
    if lowered.startswith("zh-tw"):
        return "zh_Hant"
    if lowered.startswith("zh"):
        return "zh"
    return lowered.split("-", 1)[0]


__all__ = ["MadladProvider"]
