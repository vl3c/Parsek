#!/usr/bin/env python3
"""Build the `career-earned-ksc` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. `ResourceTopBar` is career-only AND Space-Center-scoped (the funds /
science / reputation bar reflects the ledger after a recalc, and the currency
tooltip resolves its widget rects); H71 drives it here. The two `StockUiOverlay`
Mission Control cells were the second intended customer (H45 measured them skipping
for want of an OFFERED contract), but the 2026-09-07 census read them skipping on
this host too, "rows=9, contractRows=0": the offered rows populate only with the
Mission Control building UI open, which no seam verb drives. The first
multi-category census (LT-2, 2026-09-07) read both
`ResourceTopBar` cells skipping on `fresh-sandbox` - "career-only", "no funds/science
currency widget present" - and no committed career save boots to the Space Center
WITH contracts on offer: `fresh-career` and `strategy-career` are vessel-less but
carry no CONTRACT nodes, and every earned career (`career-earned-pad`,
`career-contract-pad`, `career-science-pad`) carries a pad craft that routes
`LoadGame` to FLIGHT.

The host already exists one directory over. `career-earned-pad` is SPLICED from the
xUnit fixture `Source/Parsek.Tests/Fixtures/C2CareerPostFix/` - the one committed
copy of the save harness run `2026-08-19_2130_L3-career-science-recover` produced -
and that base carries ZERO `VESSEL` nodes (its craft was recovered), a populated
ledger, nine `Offered` CONTRACT nodes and a real career clock. Vessel-less, it takes
`TestCommandLoadGame.DecideLoadRoute`'s NoVesselSpaceCenter route, which is exactly
the scene these cells want. This tool copies that base into a harness fixture with
the same two hygiene edits `build_career_earned_pad.py` applies and NOTHING else:

  1. The `rewindSave = parsek_rw_*` hint is STRIPPED from every RECORDING node
     (`CommittedFixtureRewindSaveTests`: no fixture may carry a hint at a
     Rewind-to-Launch quicksave that is not committed).
  2. `Parsek/Saves/` is not copied (the same test forbids committing the
     `parsek_rw_*.sfs` harvest exhaust). `Parsek/GameState/` and
     `Parsek/Recordings/` are copied verbatim, so the ledger and the two recorded
     flights the base carries are the fixture's payload exactly as they are the
     pad fixture's.

No contract is re-stated Active (that is the pad fixture's D8 splice and needs a
craft to be honest); the nine stay Offered, which is the Mission Control cells'
precondition. `persistent.loadmeta` is copied verbatim: its vessel count is already 0.

Run `python harness/tools/build_career_earned_ksc.py` to (re)write the fixture and
`--check` to re-derive and compare byte for byte against the committed one;
`harness/lib/test_career_earned_ksc.py` runs the check in-process so a hand edit to
either side reds in the unit suite rather than in a live boot.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import sys

_HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
BASE_DIR = os.path.join(_REPO_ROOT, "Source", "Parsek.Tests", "Fixtures", "C2CareerPostFix")
TARGET_DIR = os.path.join(_HARNESS_ROOT, "fixtures", "saves", "career-earned-ksc")
PRUNED_PARSEK_SUBDIRS = ("Saves",)
_REWIND_HINT_RE = re.compile(r"^\s*rewindSave = parsek_rw_\w+\s*$")


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    """The base save with every `rewindSave = parsek_rw_*` line removed. Line
    endings and every other byte are preserved."""
    nl = b"\r\n" if b"\r\n" in source_bytes else b"\n"
    lines = source_bytes.split(nl)
    kept = [l for l in lines if not _REWIND_HINT_RE.match(l.decode("utf-8", "replace"))]
    assert len(kept) < len(lines), "the base carries no rewindSave hint; the derivation would be a plain copy"
    return nl.join(kept)


def expected_files() -> dict:
    """{relative path: bytes} of every file the fixture must contain."""
    files = {
        "persistent.sfs": derive_persistent_sfs(_read(os.path.join(BASE_DIR, "persistent.sfs"))),
        "persistent.loadmeta": _read(os.path.join(BASE_DIR, "persistent.loadmeta")),
    }
    parsek_src = os.path.join(BASE_DIR, "Parsek")
    for dirpath, dirnames, filenames in os.walk(parsek_src):
        rel_dir = os.path.relpath(dirpath, BASE_DIR).replace(os.sep, "/")
        top = rel_dir.split("/")[1] if "/" in rel_dir else None
        if top in PRUNED_PARSEK_SUBDIRS:
            dirnames[:] = []
            continue
        for fn in filenames:
            files[rel_dir + "/" + fn] = _read(os.path.join(dirpath, fn))
    return files


def check() -> list:
    """Differences between a fresh derivation and the committed fixture (empty = OK)."""
    problems = []
    expected = expected_files()
    for rel, data in expected.items():
        path = os.path.join(TARGET_DIR, rel)
        if not os.path.isfile(path):
            problems.append("missing %s" % rel)
        elif _read(path) != data:
            problems.append("%s differs from a fresh derivation" % rel)
    if os.path.isdir(TARGET_DIR):
        for dirpath, _, filenames in os.walk(TARGET_DIR):
            for fn in filenames:
                rel = os.path.relpath(os.path.join(dirpath, fn), TARGET_DIR).replace(os.sep, "/")
                if rel not in expected:
                    problems.append("unexpected file in fixture: %s" % rel)
    return problems


def verify_shape(sfs_text: str) -> list:
    """Post-conditions the fixture exists for, stated on the derived bytes."""
    problems = []
    if re.search(r"^\t*Mode = CAREER$", sfs_text, re.M) is None:
        problems.append("not a CAREER save")
    if re.search(r"^\t+VESSEL$", sfs_text, re.M) is not None:
        problems.append("carries a VESSEL node; the Space Center route needs none")
    if sfs_text.count("state = Offered") < 1:
        problems.append("no Offered CONTRACT node; the Mission Control cells need one")
    if "state = Active" in sfs_text:
        problems.append("carries an Active contract; that splice belongs to career-earned-pad")
    if _REWIND_HINT_RE.search(sfs_text) is not None or "rewindSave = parsek_rw_" in sfs_text:
        problems.append("a rewindSave = parsek_rw_* hint survived")
    if "name = ParsekScenario" not in sfs_text:
        problems.append("no ParsekScenario node; the ledger payload would not load")
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture; exit 1 on drift")
    args = parser.parse_args(argv)
    if not os.path.isdir(BASE_DIR):
        print("FAIL: base fixture missing: %s" % BASE_DIR)
        return 1
    # CRLF-authored saves: normalise so the $ anchors in verify_shape see line ends.
    text = expected_files()["persistent.sfs"].decode("utf-8", "replace")
    shape = verify_shape(text.replace(chr(13) + chr(10), chr(10)))
    for p in shape:
        print("FAIL: " + p)
    if shape:
        return 1
    if args.check:
        problems = check()
        for p in problems:
            print("DRIFT: " + p)
        print("career-earned-ksc: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    shutil.rmtree(TARGET_DIR, ignore_errors=True)
    for rel, data in expected_files().items():
        path = os.path.join(TARGET_DIR, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote %s (%d files)" % (TARGET_DIR, len(expected_files())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
