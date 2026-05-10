#!/usr/bin/env python
"""Compatibility wrapper for migrated generator logic script."""

from __future__ import annotations

import os
import runpy


def main() -> int:
    current_dir = os.path.dirname(os.path.abspath(__file__))
    w_root = os.path.abspath(os.path.join(current_dir, "..", ".."))
    central_dir = None
    for root, _, files in os.walk(w_root):
        if "build-autocad-plugin.ps1" in files:
            central_dir = root
            break
    if not central_dir:
        raise FileNotFoundError("Central script directory not found.")
    target = os.path.join(central_dir, "generate_sheet_jsqcad.py")
    if not os.path.isfile(target):
        raise FileNotFoundError(f"Central script not found: {target}")
    runpy.run_path(target, run_name="__main__")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
