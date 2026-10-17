#!/usr/bin/env python3
"""Derive the committed `career-gs1-leadership-pad` fixture from `career-gs1-two-stage-pad`.

WHY THIS FIXTURE EXISTS. STRATEGY-CONVERSION-ROWS-OUTLIVE-THEIR-AWARD: the share of a
flight's reward a stock strategy diverts is a separate ledger row, and before the fix it
was written untagged, so an F9 took the reward back and kept the share. QL-2c flies
QL-2b's in-flight F9 with a strategy that diverts a MILESTONE reward active, so the
lane's post-F5 SurfaceEVA milestone carries a strategy share that must go back with it.
The host is QL-2b's own (`career-gs1-two-stage-pad`) with one stock strategy already
active and nothing else different, so a delta between QL-2b and QL-2c is the strategy.

WHICH STRATEGY. `LeadershipInitiative`: its CurrencyOperation multiplies Progression
rewards (milestones), and L3 measured that uplift captured as a Strategy funds row
(`L3-strategy-currency-conversion`, 7500.0048828125 on a 100000 Progression award at
the default factor). The contract-reward strategies (Appreciation Campaign and its kin)
divert ContractReward only, which a pad hop cannot earn.

WHY A SAVE NODE AND NOT AN ACTIVATION. Activating Leadership Initiative in game costs a
reputation setup fee lerped to 14.5 at the default factor, and this career's pool is 0
(`build_strategy_career.py` reads the gate). A STRATEGY node in the save skips the gate:
decompiled KSP 1.12.5 `StrategySystem.LoadStrategies` hands every `STRATEGY` node whose
`name` matches a config to `Strategy.Load`, which reads `date` and `factor` and then
sets `isActive = true` and calls `Register()` unconditionally. `StrategySystem.OnSave`
writes exactly these nodes for active strategies (`name`, `date` as G17, `factor` as
G9), so the node below is the shape stock itself writes. Parsek's ledger holds no
activation row for it, and `StrategyStatePatcher.ComputePlan` leaves a strategy active
in stock but never managed by the ledger alone ("Unmanaged").

THE DERIVATION. `career-gs1-two-stage-pad`'s files byte for byte, except two edits to
persistent.sfs:
  1. the empty `STRATEGIES {}` of `SCENARIO { name = StrategySystem }` gains
     `STRATEGY { name = LeadershipInitiative  date = 0  factor = 0.0500000007 }`
     (0.0500000007 is stock's `factorSliderDefault = 0.05` as G9 writes it);
  2. `Title` is restamped to this fixture's name.
The source uses CRLF line endings and so does the output. `persistent.loadmeta` and
`AddOns/` are copied byte for byte.

Run `python harness/tools/build_career_gs1_leadership_pad.py` to (re)write the fixture
and `--check` to re-derive and compare against the committed bytes;
`harness/lib/test_career_gs1_leadership_pad.py` runs the check in-process.
"""
from __future__ import annotations

import argparse
import filecmp
import os
import shutil
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
SOURCE_NAME = "career-gs1-two-stage-pad"
TARGET_NAME = "career-gs1-leadership-pad"
SOURCE_DIR = os.path.join(SAVES, SOURCE_NAME)
TARGET_DIR = os.path.join(SAVES, TARGET_NAME)
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")
COPIED_DIRS = ("AddOns",)

STRATEGY_NAME = "LeadershipInitiative"
STRATEGY_DATE = "0"
STRATEGY_FACTOR = "0.0500000007"

NL = "\r\n"
EMPTY_STRATEGIES = (
    "\t\tname = StrategySystem" + NL
    + "\t\tscene = 7, 8, 5, 6" + NL
    + "\t\tSTRATEGIES" + NL
    + "\t\t{" + NL
    + "\t\t}" + NL)
FILLED_STRATEGIES = (
    "\t\tname = StrategySystem" + NL
    + "\t\tscene = 7, 8, 5, 6" + NL
    + "\t\tSTRATEGIES" + NL
    + "\t\t{" + NL
    + "\t\t\tSTRATEGY" + NL
    + "\t\t\t{" + NL
    + "\t\t\t\tname = " + STRATEGY_NAME + NL
    + "\t\t\t\tdate = " + STRATEGY_DATE + NL
    + "\t\t\t\tfactor = " + STRATEGY_FACTOR + NL
    + "\t\t\t}" + NL
    + "\t\t}" + NL)
SOURCE_TITLE = "\tTitle = " + SOURCE_NAME + " (CAREER)" + NL
TARGET_TITLE = "\tTitle = " + TARGET_NAME + " (CAREER)" + NL


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _replace_once(text: str, before: str, after: str, what: str) -> str:
    count = text.count(before)
    if count != 1:
        raise SystemExit("expected exactly one %s in %s, found %d" % (what, SOURCE_DIR, count))
    return text.replace(before, after)


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    text = source_bytes.decode("utf-8")
    text = _replace_once(text, EMPTY_STRATEGIES, FILLED_STRATEGIES, "empty StrategySystem STRATEGIES node")
    text = _replace_once(text, SOURCE_TITLE, TARGET_TITLE, "source Title line")
    return text.encode("utf-8")


def derive_all() -> dict:
    files = {}
    for name in COPIED_FILES:
        src = _read(os.path.join(SOURCE_DIR, name))
        files[name] = derive_persistent_sfs(src) if name == "persistent.sfs" else src
    return files


def _dirs_identical(a: str, b: str) -> bool:
    cmp = filecmp.dircmp(a, b)
    if cmp.left_only or cmp.right_only or cmp.funny_files:
        return False
    _, mismatch, errors = filecmp.cmpfiles(a, b, cmp.common_files, shallow=False)
    if mismatch or errors:
        return False
    return all(_dirs_identical(os.path.join(a, d), os.path.join(b, d)) for d in cmp.common_dirs)


def check() -> list:
    """Differences between a fresh derivation and the committed bytes (empty = OK)."""
    problems = []
    for name, data in derive_all().items():
        path = os.path.join(TARGET_DIR, name)
        if not os.path.isfile(path):
            problems.append("missing %s" % path)
        elif _read(path) != data:
            problems.append("%s differs from a fresh derivation" % path)
    for name in COPIED_DIRS:
        target = os.path.join(TARGET_DIR, name)
        if not os.path.isdir(target) or not _dirs_identical(os.path.join(SOURCE_DIR, name), target):
            problems.append("%s/ differs from %s's" % (name, SOURCE_NAME))
    if os.path.isdir(TARGET_DIR):
        for name in sorted(os.listdir(TARGET_DIR)):
            if name not in COPIED_FILES and name not in COPIED_DIRS:
                problems.append("unexpected entry in fixture: %s" % name)
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
        print("%s: %s" % (TARGET_NAME, "OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    for name in COPIED_DIRS:
        target = os.path.join(TARGET_DIR, name)
        if os.path.isdir(target):
            shutil.rmtree(target)
        shutil.copytree(os.path.join(SOURCE_DIR, name), target)
        print("copied %s/" % target)
    return 0


if __name__ == "__main__":
    sys.exit(main())
