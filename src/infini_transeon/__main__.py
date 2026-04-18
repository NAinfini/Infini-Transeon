"""Entry point for `python -m infini_transeon` and the console script."""

from __future__ import annotations

import sys


def main() -> int:
    # Register nvidia-cublas / nvidia-cudnn DLL dirs BEFORE anything imports
    # ctranslate2 — on Windows those dirs aren't on the default DLL search
    # path even though they're inside our venv.
    from infini_transeon.utils.cuda_dll import register_cuda_dll_dirs

    register_cuda_dll_dirs()

    from infini_transeon.app import run

    return run(sys.argv)


if __name__ == "__main__":
    sys.exit(main())
