#!/usr/bin/env python3
"""Derive the committed `career-science-pad-hard` fixture from `career-science-pad` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. The game-settings axis's first Hard lane (HC-1, host
`career-pad-craft-hard`) proved Parsek boots, recalculates, commits and rewinds a Hard career,
but nothing was earned or spent there, so no multiplier-scaled amount was ever checked
(operator request 2026-10-03). `career-science-pad` is the host of `L3-career-science-recover`,
the one committed flight that makes a career EARN through every channel a reward multiplier
touches: stock world-first milestones (funds, science and reputation scaled by the
Funds/Science/RepGainMultiplier through `GameVariables.GetContract*CompletionFactor`,
decompiled `ProgressUtilities.WorldFirstStandardReward`), transmitted and recovered science
(`ScienceGainMultiplier`, the S1 stamp) and a vessel recovery (no multiplier: the control).
L3's x1 amounts are measured twice, so the same flight on this host isolates the multipliers.

THE DERIVATION. `career-science-pad`'s `persistent.sfs` byte for byte except the eleven
PARAMETERS values `build_career_pad_craft_hard.py` sets (KSP 1.12.5's Hard preset, decompiled
`GameParameters.SetDifficultyPresets`); the EDITS table and the already-present Hard values are
IMPORTED from that builder, not copied, so the two Hard hosts cannot disagree on what Hard
means. `career-science-pad`'s PARAMETERS node is byte-identical to `career-pad-craft`'s (it is
that save with three parts spliced onto the craft), which the drift test asserts.
`persistent.loadmeta` is copied byte for byte.

WHAT IS NOT COPIED. `AddOns/` (a DistantObject settings file the stock-minimal profile does
not read).

Run `python harness/tools/build_career_science_pad_hard.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_career_science_pad_hard.py` runs the check in-process.
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-science-pad")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-science-pad-hard")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")
HARD_BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_pad_craft_hard.py")


def load_hard_builder():
    spec = importlib.util.spec_from_file_location("build_career_pad_craft_hard", HARD_BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    """The Hard derivation over a CRLF save. `career-science-pad` is committed with CRLF line
    ends (`-text`, as KSP wrote it) while the Hard builder parses LF, so the bytes are taken
    to LF, derived, and put back to CRLF; a source with any bare LF is refused, because the
    round trip would then not be exact."""
    if source_bytes.count(b"\n") != source_bytes.count(b"\r\n"):
        raise SystemExit("%s/persistent.sfs mixes line endings" % SOURCE_DIR)
    lf = source_bytes.replace(b"\r\n", b"\n")
    return load_hard_builder().derive_persistent_sfs(lf).replace(b"\n", b"\r\n")


def derive_all() -> dict:
    files = {}
    for name in COPIED_FILES:
        src = _read(os.path.join(SOURCE_DIR, name))
        files[name] = derive_persistent_sfs(src) if name == "persistent.sfs" else src
    return files


def check() -> list:
    """Differences between a fresh derivation and the committed bytes (empty = OK)."""
    problems = []
    for name, data in derive_all().items():
        path = os.path.join(TARGET_DIR, name)
        if not os.path.isfile(path):
            problems.append("missing %s" % path)
        elif _read(path) != data:
            problems.append("%s differs from a fresh derivation" % path)
    for name in sorted(os.listdir(TARGET_DIR)) if os.path.isdir(TARGET_DIR) else []:
        if name not in COPIED_FILES:
            problems.append("unexpected file in fixture: %s" % name)
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture; exit 1 on drift")
    args = parser.parse_args(argv)
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("career-science-pad-hard: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
