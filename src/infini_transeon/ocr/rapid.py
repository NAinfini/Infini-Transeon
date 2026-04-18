"""RapidOCR engine (PP-OCRv5 via ONNX Runtime).

Detector tier is configurable (``OcrConfig.det_tier``, default "server"):
"server" uses PP-OCRv5 server det for better small-text / cluttered-UI
recall; "mobile" is the lightweight tier for CPU-only setups. Detector
input size is clamped by ``OcrConfig.det_max_side`` (default 3840) so
4K captures keep their native resolution. Recognizer always runs
PP-OCRv5 *server* weights on native-resolution crops from the detector.

Contract matches :class:`infini_transeon.ocr.base.OcrEngine`.
"""

from __future__ import annotations

from threading import Lock
from typing import Any

import numpy as np

from infini_transeon.capture.base import Rect
from infini_transeon.config.schema import OcrConfig
from infini_transeon.ocr.base import OcrEngine, TextBlock
from infini_transeon.utils.logging import logger

try:
    from rapidocr import (  # type: ignore[import-not-found]
        EngineType,
        LangDet,
        LangRec,
        ModelType,
        OCRVersion,
        RapidOCR,
    )
except ImportError:  # pragma: no cover - declared as a hard dep
    RapidOCR = None  # type: ignore[assignment]
    EngineType = None  # type: ignore[assignment]
    LangDet = None  # type: ignore[assignment]
    LangRec = None  # type: ignore[assignment]
    ModelType = None  # type: ignore[assignment]
    OCRVersion = None  # type: ignore[assignment]


class OcrUnavailableError(RuntimeError):
    """Raised once, then cached, when RapidOCR fails to initialise."""


class RapidOcrEngine(OcrEngine):
    name = "rapidocr"

    def __init__(self, config: OcrConfig) -> None:
        if RapidOCR is None:
            raise RuntimeError(
                "'rapidocr' (>=3.0) is not installed; required for PP-OCRv5 server weights"
            )
        self._config = config
        self._ocr: Any | None = None
        self._init_error: str | None = None
        self._lock = Lock()

    def _ensure(self) -> Any:
        if self._ocr is not None:
            return self._ocr
        if self._init_error is not None:
            raise OcrUnavailableError(self._init_error)
        with self._lock:
            if self._ocr is not None:
                return self._ocr
            if self._init_error is not None:
                raise OcrUnavailableError(self._init_error)
            logger.info(
                "loading rapidocr 3.x (PP-OCRv5, {} det + server rec)",
                self._config.det_tier,
            )
            try:
                # Split detector/recognizer tiers:
                #   Det: PP-OCRv5, tier controlled by OcrConfig.det_tier
                #     ("server" default for better small-text / cluttered-UI
                #     recall; "mobile" is the lightweight tier for CPU-only
                #     setups). limit_type="max", limit_side_len from config
                #     (default 3840) preserves 4K native resolution —
                #     RapidOCR's default limit_type="min" would force the
                #     short side up to 736 and leave the detector chewing
                #     millions of pixels on a 4K frame for no recall gain.
                #   Rec: PP-OCRv5 SERVER. Receives native-resolution crops from
                #     the detector, so glyphs keep their pixels. Server weights
                #     are noticeably more accurate than mobile on small UI text
                #     at ~2x the cost per crop — but crops are tiny, so the total
                #     recognize time stays well under the detection time.
                #   Cls: default (angle classifier, small model, rarely hot).
                # Det + Rec both prefer CUDA ONNX Runtime when available — on
                # CPU, PP-OCRv5 server rec is ~3-4s per frame which dominates
                # every tick. The ``use_cuda`` flag falls back silently to CPU
                # if the runtime doesn't have a CUDA EP built in.
                # First init downloads ~200 MB into the rapidocr cache
                # (~/.cache/rapidocr). Subsequent runs are instant.
                engine_cfg_cuda = {"use_cuda": _cuda_ep_available()}
                det_model_type = (
                    ModelType.SERVER
                    if self._config.det_tier == "server"
                    else ModelType.MOBILE
                )
                self._ocr = RapidOCR(
                    params={
                        "Det.engine_type": EngineType.ONNXRUNTIME,
                        "Det.lang_type": LangDet.CH,
                        "Det.ocr_version": OCRVersion.PPOCRV5,
                        "Det.model_type": det_model_type,
                        "Det.limit_type": "max",
                        "Det.limit_side_len": int(self._config.det_max_side),
                        "Det.engine_cfg": engine_cfg_cuda,
                        "Rec.engine_type": EngineType.ONNXRUNTIME,
                        "Rec.lang_type": LangRec.CH,
                        "Rec.ocr_version": OCRVersion.PPOCRV5,
                        "Rec.model_type": ModelType.SERVER,
                        "Rec.engine_cfg": engine_cfg_cuda,
                        "Cls.engine_type": EngineType.ONNXRUNTIME,
                    },
                )
            except Exception as exc:  # noqa: BLE001 - surface but don't loop
                self._init_error = f"rapidocr init failed: {exc}"
                logger.error("{}", self._init_error)
                raise OcrUnavailableError(self._init_error) from exc
        return self._ocr

    def recognize(
        self, image: np.ndarray, *, lang_hint: str | None = None
    ) -> list[TextBlock]:
        del lang_hint  # PP-OCRv5 'ch' recognizer handles CJK + Latin unified.
        try:
            ocr = self._ensure()
        except OcrUnavailableError:
            return []
        # Capture layer emits BGRA; RapidOCR's preprocessor wants 3 channels.
        if image.ndim == 3 and image.shape[2] == 4:
            image = image[:, :, :3]
        image = np.ascontiguousarray(image)
        with self._lock:
            raw = ocr(image)
        if raw is None:
            return []
        blocks: list[TextBlock] = []
        min_conf = self._config.min_confidence
        boxes = getattr(raw, "boxes", None)
        txts = getattr(raw, "txts", None)
        scores = getattr(raw, "scores", None)
        if boxes is None or txts is None or scores is None:
            return []
        for poly, text, score in zip(boxes, txts, scores, strict=False):
            try:
                score_f = float(score)
            except (TypeError, ValueError):
                score_f = 0.0
            if score_f < min_conf or not str(text).strip():
                continue
            blocks.append(
                TextBlock(
                    bbox=_poly_to_rect(poly),
                    text=str(text),
                    confidence=score_f,
                )
            )
        return blocks

    def warmup(self) -> None:
        try:
            self._ensure()
        except OcrUnavailableError:
            pass

    def close(self) -> None:
        self._ocr = None


def _poly_to_rect(polygon: Any) -> Rect:
    xs = [int(p[0]) for p in polygon]
    ys = [int(p[1]) for p in polygon]
    x, y = min(xs), min(ys)
    return Rect(x=x, y=y, width=max(xs) - x, height=max(ys) - y)


def _cuda_ep_available() -> bool:
    """True when onnxruntime reports a CUDA execution provider is built in.

    Falls back to False on any import/introspection error so a missing or
    CPU-only onnxruntime wheel silently keeps OCR on CPU instead of crashing.
    The RapidOCR-bundled CUDA EP config is conservative: if the DLL load
    fails at session create, ORT returns a CPU session with a warning.
    """
    try:
        import onnxruntime  # type: ignore[import-not-found]
    except ImportError:
        return False
    try:
        return "CUDAExecutionProvider" in onnxruntime.get_available_providers()
    except Exception:  # noqa: BLE001
        return False


__all__ = ["RapidOcrEngine", "OcrUnavailableError"]
