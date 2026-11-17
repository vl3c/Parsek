#!/usr/bin/env python3
"""Build the committed `mallard-splashed` fixture BY CONSTRUCTION (AIR-1's Mallard afloat, Parsek-stripped).

WHY THIS FIXTURE EXISTS. Lane BOAT-1 (todo NEXT-TESTS-TO-BUILD-2026-11-11 item 9, audit B9:
"no lane drives a boat") needs a vessel FLOATING on Kerbin's sea with its engines lit, to
record a surface run on water. `air-circuit-recorded` (AIR-1's harvest) already holds one:
the stock Mallard at rest in the sea east of the Space Center (SPLASHED, lat 0.0545 lon
-73.6970), both J-33 jets ignited at zero throttle, Bill and Bob aboard. But that save
holds AIR-1's COMMITTED TREE for the Mallard, and a seam StartRecording cannot open a
standalone tree on a committed tree's own vessel (measured, B23 flight 1; done/todo v11
SEAM-STARTRECORDING-JOINS-COMMITTED-TREE). So this is the park fixtures' strip, applied to
a sea park.

WHAT IS COPIED AND WHAT IS DERIVED.
  COPIED VERBATIM from `fixtures/saves/air-circuit-recorded`: `persistent.loadmeta` and
  `AddOns/`.
  NOT COPIED: `Parsek/` (AIR-1's sidecars, ledger and game-state store) and `Ships/` (the
  Mallard and Kerbal X Sweep craft files; BOAT-1 launches nothing, and a second copy of a
  craft file is a drift risk `fixtures/shared-ships.toml` exists to prevent).
  EDITED IN `persistent.sfs`:
    - the `SCENARIO{name=ParsekScenario}` node keeps its `name`, `scene` and
      `gameStateEventCount = 0` lines and loses everything else: the `saveStamp`,
      `parsekSaveFormat` and `parsekWrittenBy` values and EVERY child node
      (RECORDING_TREE, KERBAL_SLOTS, CREW_REPLACEMENTS, MILESTONE_STATE, GAME_STATE,
      STOCK_POOL_REFERENCE today; the strip walks the node, it does not name them), the
      shape `laythe-park-nerv` carries;
    - the `Title`.
  Every VESSEL, the roster, the clock (UT 427.46) and the stock scenarios are untouched, so
  the world is AIR-1's end state with no Parsek history: the Mallard (index 2, active), the
  GS1 booster on the pad and the asteroid.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_mallard_splashed.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "air-circuit-recorded")
OUT_DIR = os.path.join(SAVES, "mallard-splashed")

LF = "\n"
BASE_TITLE = "air-circuit-recorded (SANDBOX)"
TITLE = "mallard-splashed (SANDBOX)"
SCENARIO_KEEP = ("name", "scene", "gameStateEventCount")
COPIED_TOP = ("persistent.loadmeta", "AddOns")
MALLARD = "Mallard"


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def scenario_span(lines: List[str]) -> Tuple[int, int]:
    """(open_brace_index, close_brace_index) of the depth-1 SCENARIO whose first value
    line is `name = ParsekScenario`."""
    for i, line in enumerate(lines):
        if line == "\t\tname = ParsekScenario" and lines[i - 1] == "\t{" \
                and lines[i - 2] == "\tSCENARIO":
            for j in range(i + 1, len(lines)):
                if lines[j] == "\t}":
                    return i - 1, j
    raise SystemExit("base save has no depth-1 ParsekScenario SCENARIO node")


def strip_scenario(body: List[str]) -> Tuple[List[str], Dict[str, int]]:
    """Keep the depth-2 value lines named in SCENARIO_KEEP; drop every other value line
    and every child node (brace-balanced). Returns the kept lines and a count of the
    dropped child nodes by name."""
    kept: List[str] = []
    dropped: Dict[str, int] = {}
    i = 0
    while i < len(body):
        line = body[i]
        if line.startswith("\t\t") and not line.startswith("\t\t\t"):
            stripped = line.strip()
            nxt = body[i + 1] if i + 1 < len(body) else ""
            if nxt == "\t\t{":
                dropped[stripped] = dropped.get(stripped, 0) + 1
                depth = 0
                j = i + 1
                while True:
                    s = body[j].strip()
                    if s == "{":
                        depth += 1
                    elif s == "}":
                        depth -= 1
                        if depth == 0:
                            break
                    j += 1
                i = j + 1
                continue
            key = stripped.split(" = ", 1)[0]
            if key in SCENARIO_KEEP:
                kept.append(line)
            i += 1
            continue
        raise SystemExit("unexpected line in the ParsekScenario node: %r" % line)
    return kept, dropped


def build_sfs() -> str:
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "air-circuit-recorded is committed LF"
    lines = base.split(LF)
    open_i, close_i = scenario_span(lines)
    kept, dropped = strip_scenario(lines[open_i + 1:close_i])
    assert [l.strip().split(" = ")[0] for l in kept] == list(SCENARIO_KEEP), kept
    assert "\t\tgameStateEventCount = 0" in kept, kept
    assert dropped.get("RECORDING_TREE") == 1, dropped
    lines[open_i + 1:close_i] = kept
    title = lines.index("\tTitle = " + BASE_TITLE)
    lines[title] = "\tTitle = " + TITLE
    out = LF.join(lines)
    assert "RECORDING_TREE" not in out and "parsekSaveFormat" not in out
    return out


def expected_files() -> Dict[str, bytes]:
    files: Dict[str, bytes] = {}
    for top in COPIED_TOP:
        src = os.path.join(BASE_DIR, top)
        if os.path.isfile(src):
            files[top] = _read_bytes(src)
            continue
        for root, _dirs, names in os.walk(src):
            for name in names:
                path = os.path.join(root, name)
                files[os.path.relpath(path, BASE_DIR).replace(os.sep, "/")] = \
                    _read_bytes(path)
    files["persistent.sfs"] = build_sfs().encode("utf-8")
    return files


def committed_problems() -> List[str]:
    want = expected_files()
    have: Dict[str, bytes] = {}
    if os.path.isdir(OUT_DIR):
        for root, _dirs, names in os.walk(OUT_DIR):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, OUT_DIR).replace(os.sep, "/")] = \
                    _read_bytes(path)
    problems = []
    for rel in sorted(set(want) | set(have)):
        if rel not in have:
            problems.append("missing " + rel)
        elif rel not in want:
            problems.append("unexpected " + rel)
        elif have[rel] != want[rel]:
            problems.append("drift in " + rel)
    return problems


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="re-derive and compare against the committed fixture")
    args = ap.parse_args(argv)
    if args.check:
        problems = committed_problems()
        for p in problems:
            print("DRIFT: " + p)
        print("OK" if not problems else "FAIL")
        return 0 if not problems else 1
    if os.path.isdir(OUT_DIR):
        shutil.rmtree(OUT_DIR)
    for rel, data in expected_files().items():
        path = os.path.join(OUT_DIR, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote %s" % OUT_DIR)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
