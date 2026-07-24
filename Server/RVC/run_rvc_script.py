"""Run an official RVC script with the vendor root on sys.path.

GPT-SoVITS's portable Python uses an isolated `._pth` configuration and ignores
PYTHONPATH.  This tiny launcher makes official CLI scripts work in both that
runtime and a normal virtual environment without editing vendored files.
"""

from __future__ import annotations

import runpy
import os
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "rvc"
LOCAL_PACKAGES = ROOT / "python_packages"


def main() -> None:
    if len(sys.argv) < 2:
        raise SystemExit("usage: run_rvc_script.py <vendor-script> [arguments ...]")
    script_text, *arguments = sys.argv[1:]
    script = (VENDOR / script_text).resolve()
    if VENDOR.resolve() not in script.parents or not script.is_file():
        raise FileNotFoundError(script)
    if LOCAL_PACKAGES.is_dir():
        sys.path.insert(0, str(LOCAL_PACKAGES))
    sys.path.insert(0, str(VENDOR))
    os.chdir(VENDOR)
    sys.argv = [str(script), *arguments]
    runpy.run_path(str(script), run_name="__main__")


if __name__ == "__main__":
    main()
