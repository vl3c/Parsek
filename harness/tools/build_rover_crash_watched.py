#!/usr/bin/env python3
"""Build the committed `rover-crash-watched` fixture BY CONSTRUCTION (two rovers and a watcher).

WHY THIS FIXTURE EXISTS. Lane RVC-1 (todo NEXT-TESTS-TO-BUILD-2026-11-11 item 3a: two rovers
crash into each other) needs two rovers recorded in ONE tree, close together, and a third
vessel to host FLIGHT after Rewind-to-Launch so the replayed crash plays in the physics
bubble. `miner-rover-depot` (MINE-5b / MINE-4's host) already gives the first two: the
`Mining Rover RC` docks the `Ore Depot` 2.3 m in front of it by driving straight forward,
and the undock makes the depot a background-recorded member of the miner's tree.
`miner-rover-watched` (MINE-6 / MINE-14's host) already gives the third: `Miner Watcher`,
a crewless rover parked 120 m north of the miner on proven terrain. This fixture is the
union: the depot fixture with its index-1 `Observer Probe` (orbiting, useless as a FLIGHT
host) replaced by that same watcher.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/miner-rover-depot`
  (the ported `Mining Rover RC` at index 0, active, 28 parts; the `Observer Probe` at
  index 1; the `Ore Depot` at index 2, 24 parts, ports facing; the inert ParsekScenario
  node; `AddOns/`; `persistent.loadmeta` vesselCount 3, UT 30).
  REPLACED, index 1: the `Observer Probe` VESSEL node by `build_miner_rover_watched.
  build_watcher` (the `rover fuel 0` clone, renamed `Miner Watcher`, LANDED at
  `WATCHER_LAT` / `WATCHER_LON`, every clock at UT 30, ids derived from that builder's
  SEED), so the watcher is byte-identical to `miner-rover-watched`'s. Its derived ids are
  checked disjoint from the miner's AND the depot's (vessel `pid`, every `persistentId`,
  every part `uid`); that builder only checks the miner's.
  The order stays miner, watcher, depot: after the rewind strips the miner, the watcher is
  vessel 0, the vessel a Space Center save focuses (MINE-6's measured reload).
  EDITED IN THE SAVE: the `Title` and `Description`. `persistent.loadmeta` stays the base's
  (vesselCount 3, UT 30).

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_rover_crash_watched.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "miner-rover-depot")
OUT_DIR = os.path.join(SAVES, "rover-crash-watched")

sys.path.insert(0, _HERE)
import build_miner_rover_depot as depot_builder  # noqa: E402
import build_miner_rover_landed as landed  # noqa: E402
import build_miner_rover_watched as watched  # noqa: E402

LF = "\n"
EDITED = ("persistent.sfs",)
TITLE = "rover-crash-watched (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: a mining rover, an ore depot "
               "rover it can dock, and a neighbouring rover).")
EXPECTED_NAMES = [landed.ROVER_NAME, watched.WATCHER_NAME, depot_builder.DEPOT_NAME]

_PID_RE = re.compile(r"^\t+persistentId = (\d+)$")


def _ids(block: List[str]) -> Dict[str, set]:
    return {
        "pid": {l.strip() for l in block if l.startswith("\t\t\tpid = ")},
        "persistentId": {m.group(1) for m in (_PID_RE.match(l) for l in block) if m},
        "uid": {l.strip() for l in block if l.strip().startswith("uid = ")},
    }


def build_sfs() -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-depot is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [landed.ROVER_NAME, landed.PROBE_NAME,
                                      depot_builder.DEPOT_NAME], [b[2] for b in blocks]
    (m_start, m_end, _), (p_start, p_end, _), (d_start, d_end, _) = blocks
    assert m_end == p_start and p_end == d_start, "miner, probe and depot are contiguous"
    miner = lines[m_start:m_end]
    depot = lines[d_start:d_end]
    # build_watcher checks its derived ids against the lines it is handed: hand it both rovers.
    watcher = watched.build_watcher(miner + depot)
    w_ids, m_ids, d_ids = _ids(watcher), _ids(miner), _ids(depot)
    for key in ("pid", "persistentId", "uid"):
        assert w_ids[key], "watcher has no %s" % key
        assert not (w_ids[key] & m_ids[key]), "watcher %s collides with the miner" % key
        assert not (w_ids[key] & d_ids[key]), "watcher %s collides with the depot" % key
    lines[p_start:p_end] = watcher
    assert [b[2] for b in landed.vessel_blocks(lines)] == EXPECTED_NAMES
    assert "\t\tactiveVessel = 0" in lines, "the miner stays the active vessel"
    title = lines.index("\tTitle = " + depot_builder.TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + depot_builder.DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    return LF.join(lines)


def expected_files() -> Dict[str, bytes]:
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = landed._read_bytes(path)
    files["persistent.sfs"] = build_sfs().encode("utf-8")
    return files


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    if os.path.isdir(OUT_DIR):
        for root, _dirs, names in os.walk(OUT_DIR):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, OUT_DIR).replace(os.sep, "/")] = \
                    landed._read_bytes(path)
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
    ids = depot_builder.depot_ids()
    print("wrote " + OUT_DIR + " minerPersistentId=%d depotPersistentId=%d "
          "watcherPersistentId=%d" % (ids["minerPersistentId"], ids["depotPersistentId"],
                                      watched.watcher_persistent_id()))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
