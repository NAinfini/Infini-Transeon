"""Pipeline orchestrator: capture -> OCR -> translate -> render.

Driven by a QTimer on the main thread; each tick spawns a short-lived worker
thread that runs capture + OCR + translate, then emits the resulting overlay
items back to the main thread via Qt signals. No persistent worker, no change
detection, no cross-tick state — each tick is independent. The router's own
TranslationMemory still de-duplicates identical source strings, so a static
screen doesn't re-hit the provider every tick.
"""

from __future__ import annotations

import time
from threading import Thread

import numpy as np
from PySide6.QtCore import QObject, QTimer

from infini_transeon.capture.base import CaptureBackend, Frame, RegionInfo, WindowInfo
from infini_transeon.config.schema import AppConfig
from infini_transeon.ocr import postprocess, preprocess, segmentation
from infini_transeon.ocr.base import OcrEngine
from infini_transeon.overlay.renderer import to_items
from infini_transeon.pipeline.events import PipelineSignals
from infini_transeon.translate.base import TranslationRequest
from infini_transeon.translate.echo import EchoVerdict, classify, is_translatable
from infini_transeon.translate.glossary import entries_for_target
from infini_transeon.translate.router import Router, RouterError
from infini_transeon.utils.logging import logger


CaptureTarget = WindowInfo | RegionInfo


class Pipeline(QObject):
    """Periodic capture + OCR + translate loop.

    A QTimer fires every ``ocr_interval_seconds``. Each tick runs off-thread
    so the UI stays responsive; if a tick is still running when the timer
    fires again, the new tick is skipped (we don't queue).
    """

    def __init__(
        self,
        *,
        capture: CaptureBackend,
        ocr: OcrEngine,
        router: Router,
        config: AppConfig,
        signals: PipelineSignals,
    ) -> None:
        super().__init__()
        self._capture = capture
        self._ocr = ocr
        self._router = router
        self._signals = signals
        self._config = config
        self._target: CaptureTarget | None = None
        self._tick_in_flight = False
        self._timer = QTimer(self)
        self._timer.setTimerType(self._timer.timerType())  # default: coarse; fine at 1s range
        self._timer.timeout.connect(self._on_timer)
        self._apply_interval()

    # --- lifecycle -----------------------------------------------------

    def start(self) -> None:
        if self._timer.isActive():
            return
        self._apply_interval()
        self._timer.start()

    def stop(self) -> None:
        if self._timer.isActive():
            self._timer.stop()
        self._signals.stopped.emit()

    def trigger_once(self) -> None:
        """Fire a single tick immediately, independent of the timer."""
        self._on_timer()

    def set_target(self, target: CaptureTarget | None) -> None:
        self._target = target
        if isinstance(target, WindowInfo):
            self._signals.target_changed.emit(target.handle, target.title)
        elif isinstance(target, RegionInfo):
            # Negative handle marks a region target so the UI can distinguish.
            self._signals.target_changed.emit(-1, target.label or "Region")
        else:
            self._signals.target_changed.emit(0, "")

    def update_config(self, config: AppConfig) -> None:
        self._config = config
        self._apply_interval()

    def replace_ocr(self, ocr: OcrEngine) -> None:
        """Swap the OCR engine instance. Used when OCR config forces a rebuild."""
        self._ocr = ocr

    # --- timer / dispatch ---------------------------------------------

    def _apply_interval(self) -> None:
        ms = max(1, int(self._config.capture.ocr_interval_seconds * 1000))
        self._timer.setInterval(ms)

    def _on_timer(self) -> None:
        if self._target is None:
            return
        if self._tick_in_flight:
            # Previous tick still running — skip rather than pile up work.
            # On local MADLAD a tick can take 4-5s, much longer than the
            # default 1s interval; queueing would grow unbounded.
            return
        self._tick_in_flight = True
        Thread(
            target=self._run_tick,
            name="InfiniTranseonTick",
            daemon=True,
        ).start()

    def _run_tick(self) -> None:
        try:
            self._tick()
        except Exception as exc:  # noqa: BLE001 - surface to UI
            logger.exception("pipeline tick failed")
            self._signals.error.emit(str(exc))
        finally:
            self._tick_in_flight = False

    # --- tick ----------------------------------------------------------

    def _tick(self) -> None:
        target = self._target
        config = self._config
        if target is None:
            return

        t0 = time.perf_counter()
        frame = self._grab(target)
        if frame is None:
            return
        t_grab = time.perf_counter()

        frame_dpr = frame.dpr if (frame.dpr and frame.dpr > 0) else 1.0
        ocr_image, up_scale = preprocess.enhance(
            frame.image, enabled=config.ocr.enable_preprocess
        )
        effective_scale = up_scale
        t_prep = time.perf_counter()

        blocks = self._ocr.recognize(ocr_image, lang_hint=config.translation.source_lang)
        t_ocr = time.perf_counter()
        origin_x, origin_y = frame.rect.x, frame.rect.y
        if not blocks:
            del ocr_image
            del frame
            self._signals.items_ready.emit([])
            return

        raw_block_colors = [
            _sample_bbox_color(frame.image, _scaled_bbox(b.bbox, 1.0 / up_scale))
            for b in blocks
        ]
        # Per-block bbox in *frame-image pixel space* (pre-scale, pre-origin).
        # Needed later to crop the "frosted glass" backdrop for each
        # paragraph. Kept parallel to ``raw_block_colors`` so the same
        # propagation logic (post-correct → absolute → paragraph) applies.
        raw_block_frame_bboxes = [
            _scaled_bbox(b.bbox, 1.0 / up_scale) for b in blocks
        ]
        # Keep the BGRA frame pixels alive across segmentation so we can
        # sample per-paragraph crops for the "frosted glass" overlay
        # backdrop. The OCR intermediate (``ocr_image``) is upscaled and
        # can be dropped — crops come from the native frame.
        frame_image = frame.image
        frame_image_h, frame_image_w = frame_image.shape[:2]
        del ocr_image
        del frame

        cleaned_blocks: list = []
        cleaned_colors: list[str] = []
        cleaned_frame_bboxes: list = []
        postcorrect_floor = config.ocr.postcorrect_min_confidence
        for block, color, frame_bbox in zip(
            blocks, raw_block_colors, raw_block_frame_bboxes, strict=True
        ):
            enable_typo = (
                config.ocr.enable_postcorrect
                and block.confidence >= postcorrect_floor
            )
            processed = postprocess.apply(
                [block],
                lang=_ocr_lang_for(config.translation.source_lang),
                enable_typo=enable_typo,
            )
            if processed:
                cleaned_blocks.extend(processed)
                cleaned_colors.extend([color] * len(processed))
                cleaned_frame_bboxes.extend([frame_bbox] * len(processed))
        blocks = cleaned_blocks

        total_ocr_to_physical = effective_scale if effective_scale > 0 else 1.0
        scale = (1.0 / total_ocr_to_physical) / frame_dpr if frame_dpr > 0 else 1.0
        absolute = [
            type(block)(
                bbox=type(block.bbox)(
                    x=int(block.bbox.x * scale) + origin_x,
                    y=int(block.bbox.y * scale) + origin_y,
                    width=max(1, int(block.bbox.width * scale)),
                    height=max(1, int(block.bbox.height * scale)),
                ),
                text=block.text,
                confidence=block.confidence,
                language=block.language,
            )
            for block in blocks
        ]
        color_by_abs_id: dict[int, str] = {
            id(b): c for b, c in zip(absolute, cleaned_colors, strict=True)
        }
        frame_bbox_by_abs_id: dict[int, object] = {
            id(b): fb for b, fb in zip(absolute, cleaned_frame_bboxes, strict=True)
        }

        paragraphs = segmentation.segment(absolute)
        paragraphs = [p for p in paragraphs if is_translatable(p.text)]
        to_translate = [p for p in paragraphs if p.text]

        translations_by_source: dict[str, str] = {}
        xlate_ms = 0.0
        if to_translate:
            glossary = tuple(
                entries_for_target(config.translation.glossary, config.translation.target_lang)
            )
            req = TranslationRequest(
                texts=tuple(p.text for p in to_translate),
                source_lang=config.translation.source_lang,
                target_lang=config.translation.target_lang,
                style=getattr(config.translation.style, "value", config.translation.style),
                glossary=glossary,
            )
            t_xlate_start = time.perf_counter()
            try:
                result = self._router.translate(req)
            except RouterError as exc:
                self._signals.error.emit(str(exc))
                return
            xlate_ms = (time.perf_counter() - t_xlate_start) * 1000.0

            self._signals.degraded.emit(self._router.state.degraded)
            for src, tgt in zip(req.texts, result.translations, strict=False):
                if not src or not tgt:
                    continue
                verdict = classify(src, tgt, config.translation.target_lang)
                if verdict is EchoVerdict.hard_echo:
                    # Provider returned unchanged text — drop it; next tick retries.
                    continue
                translations_by_source[src] = tgt

        merged = [translations_by_source.get(p.text, "") for p in paragraphs]
        bg_colors: list[str | None] = [
            _paragraph_bg_color(p, color_by_abs_id) for p in paragraphs
        ]
        bg_crops: list[object] = [
            _paragraph_bg_crop(
                p,
                frame_bbox_by_abs_id,
                frame_image,
                frame_image_w,
                frame_image_h,
            )
            for p in paragraphs
        ]
        # frame_image is kept alive until here so the crops above own their
        # own copies; drop the reference now so the capture buffer can be
        # reclaimed before the next tick.
        del frame_image
        items = to_items(paragraphs, merged, bg_colors, bg_crops)
        self._signals.items_ready.emit(items)
        t_end = time.perf_counter()
        logger.info(
            "tick: grab={:.0f} prep={:.0f} ocr={:.0f} xlate={:.0f} total={:.0f}ms "
            "blocks={} to_translate={}",
            (t_grab - t0) * 1000.0,
            (t_prep - t_grab) * 1000.0,
            (t_ocr - t_prep) * 1000.0,
            xlate_ms,
            (t_end - t0) * 1000.0,
            len(blocks),
            len(to_translate),
        )

    def _grab(self, target: CaptureTarget) -> Frame | None:
        """Dispatch capture based on whether the target is a window or a fixed region."""
        if isinstance(target, WindowInfo):
            refreshed = self._capture.get_window(target.handle)
            if refreshed is None:
                self._signals.status.emit("Target window closed")
                self.set_target(None)
                return None
            if refreshed.rect != target.rect:
                self._signals.window_moved.emit(refreshed.rect)
                self._target = refreshed
            if refreshed.is_minimized or not refreshed.is_visible:
                return None
            return self._capture.capture(refreshed)
        return self._capture.capture_rect(target.rect, dpr=target.dpr)


def _ocr_lang_for(source_lang: str) -> str:
    """Map translation source code to the language symspell dictionaries use."""
    return source_lang.split("-", 1)[0].lower() if source_lang else "en"


def _scaled_bbox(bbox, factor: float):
    cls = type(bbox)
    return cls(
        x=int(bbox.x * factor),
        y=int(bbox.y * factor),
        width=max(1, int(bbox.width * factor)),
        height=max(1, int(bbox.height * factor)),
    )


def _sample_bbox_color(image: np.ndarray, bbox) -> str:
    """Sample the median colour of the pixels inside ``bbox`` on ``image``."""
    h, w = image.shape[:2]
    mx = max(1, int(bbox.width * 0.15))
    my = max(1, int(bbox.height * 0.15))
    x0 = max(0, bbox.x + mx)
    y0 = max(0, bbox.y + my)
    x1 = min(w, bbox.x + bbox.width - mx)
    y1 = min(h, bbox.y + bbox.height - my)
    if x1 <= x0 or y1 <= y0:
        return "#000000"
    region = image[y0:y1, x0:x1]
    flat = region.reshape(-1, region.shape[2])
    if flat.shape[0] > 256:
        step = flat.shape[0] // 256
        flat = flat[::step][:256]
    med = np.median(flat[:, :3], axis=0).astype(int)
    b, g, r = int(med[0]), int(med[1]), int(med[2])
    return f"#{r:02X}{g:02X}{b:02X}"


def _paragraph_bg_color(paragraph, color_by_abs_id: dict[int, str]) -> str | None:
    if not paragraph.blocks:
        return None
    colors = [color_by_abs_id.get(id(b)) for b in paragraph.blocks]
    colors = [c for c in colors if c]
    if not colors:
        return None
    rs, gs, bs = [], [], []
    for c in colors:
        rs.append(int(c[1:3], 16))
        gs.append(int(c[3:5], 16))
        bs.append(int(c[5:7], 16))
    r = sum(rs) // len(rs)
    g = sum(gs) // len(gs)
    b = sum(bs) // len(bs)
    return f"#{r:02X}{g:02X}{b:02X}"


def _paragraph_bg_crop(
    paragraph,
    frame_bbox_by_abs_id: dict,
    frame_image: np.ndarray,
    frame_w: int,
    frame_h: int,
) -> np.ndarray | None:
    """Return the frame-pixel crop that sits under ``paragraph``.

    Returns a contiguous *copy* of the slice so downstream consumers (the
    overlay, running on the Qt main thread) don't share memory with the
    capture buffer, which is freed as soon as this function returns.

    Union is built over the frame-space bboxes of the paragraph's
    constituent blocks rather than the paragraph's screen-space bbox, so
    we don't have to invert DPR + origin transforms at crop time.
    """
    if not paragraph.blocks:
        return None
    bboxes = [frame_bbox_by_abs_id.get(id(b)) for b in paragraph.blocks]
    bboxes = [bb for bb in bboxes if bb is not None]
    if not bboxes:
        return None
    x0 = max(0, min(bb.x for bb in bboxes))
    y0 = max(0, min(bb.y for bb in bboxes))
    x1 = min(frame_w, max(bb.x + bb.width for bb in bboxes))
    y1 = min(frame_h, max(bb.y + bb.height for bb in bboxes))
    # A tiny outward margin makes the blurred backdrop extend slightly
    # past the source glyph edges so the translation's outline doesn't
    # run straight into the un-blurred scene and shimmer.
    pad_x = max(2, (x1 - x0) // 20)
    pad_y = max(2, (y1 - y0) // 10)
    x0 = max(0, x0 - pad_x)
    y0 = max(0, y0 - pad_y)
    x1 = min(frame_w, x1 + pad_x)
    y1 = min(frame_h, y1 + pad_y)
    if x1 <= x0 or y1 <= y0:
        return None
    crop = frame_image[y0:y1, x0:x1]
    return np.ascontiguousarray(crop).copy()


__all__ = ["Pipeline", "CaptureTarget"]
