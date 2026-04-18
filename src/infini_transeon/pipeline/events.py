"""Qt signals emitted by the pipeline to drive the UI."""

from __future__ import annotations

from PySide6.QtCore import QObject, Signal

from infini_transeon.capture.base import Rect


class PipelineSignals(QObject):
    """Signals exposed by the pipeline orchestrator thread."""

    target_changed = Signal(int, str)           # window handle, title
    window_moved = Signal(Rect)                  # target window rect changed
    items_ready = Signal(list)                   # list[OverlayItem]
    degraded = Signal(bool)                      # True if online fell back to local
    error = Signal(str)                          # user-visible error message
    status = Signal(str)                         # low-priority status text
    stopped = Signal()


__all__ = ["PipelineSignals"]
