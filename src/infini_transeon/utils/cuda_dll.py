"""Register NVIDIA CUDA runtime DLL directories from pip wheels.

Users install the app as a normal Python package; we ship ``nvidia-cublas-cu12``
and ``nvidia-cudnn-cu12`` as optional deps so they don't have to install the
CUDA toolkit separately. Those wheels drop their DLLs into
``site-packages/nvidia/<component>/bin`` — Windows ignores that directory by
default, so CTranslate2 fails to load cuBLAS at runtime.

This helper walks ``site-packages/nvidia`` at startup and registers each
``bin`` it finds via :func:`os.add_dll_directory` (Windows) or prepends
``LD_LIBRARY_PATH`` (Linux). Safe to call before any GPU library is
imported; no-op when the wheels aren't installed.
"""

from __future__ import annotations

import os
import sys
import sysconfig
from pathlib import Path


def register_cuda_dll_dirs() -> list[Path]:
    """Register every ``site-packages/nvidia/*/bin`` directory found.

    Returns the list of directories successfully registered so callers can
    log what was wired up. Any failure is silent — missing wheels should
    NOT prevent the app from starting; the MADLAD provider's own CUDA
    fault handler will fall back to CPU instead.
    """
    roots: list[Path] = []
    site_packages = Path(sysconfig.get_paths()["purelib"])
    nvidia_root = site_packages / "nvidia"
    if not nvidia_root.is_dir():
        return roots
    for sub in nvidia_root.iterdir():
        bin_dir = sub / "bin"
        if not bin_dir.is_dir():
            continue
        if sys.platform == "win32":
            # Belt and braces: PATH prefix + add_dll_directory. CTranslate2's
            # LoadLibraryExW call on Windows honours add_dll_directory, but
            # some transitively-loaded DLLs (cudnn -> cublas) resolve the
            # old-school PATH way, so we set both.
            os.environ["PATH"] = str(bin_dir) + os.pathsep + os.environ.get("PATH", "")
            try:
                os.add_dll_directory(str(bin_dir))
            except (OSError, FileNotFoundError):
                pass
            roots.append(bin_dir)
        else:
            existing = os.environ.get("LD_LIBRARY_PATH", "")
            parts = [str(bin_dir)] + ([existing] if existing else [])
            os.environ["LD_LIBRARY_PATH"] = os.pathsep.join(parts)
            roots.append(bin_dir)
    return roots


__all__ = ["register_cuda_dll_dirs"]
