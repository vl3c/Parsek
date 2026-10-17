#!/usr/bin/env python3
"""Build the committed `miner-rover-watched` fixture BY CONSTRUCTION (a miner with a neighbour).

WHY THIS FIXTURE EXISTS. `miner-rover-landed` (MINE-2's host) has no second vessel near the
rover, so after Rewind-to-Launch the rover's ghost plays at the Space Center or far below an
orbiting probe: nothing in FLIGHT is near enough to draw the ghost's drill pose and
converter loop, to press Real Spawn Control (its row needs the active vessel within 250 m of
the ghost) or to switch to the spawned rover while it is loaded. This fixture parks a
crewless rover 120 m north of the miner as that neighbour (the mining program, lanes
MINE-6, MINE-7 and MINE-9; `docs/dev/research/mining-isru-test-program-2026-10-17.md`).

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed
  `fixtures/saves/miner-rover-landed` (the `Mining Rover RC` at index 0, active, radiators
  on, tanks emptied, every converter clock at UT 30; the inert ParsekScenario node). Its
  index-1 `Observer Probe` is DROPPED: the neighbour is the FLIGHT host after a rewind.
  CLONED as index 1: the `rover fuel 0` VESSEL node out of
  `fixtures/saves/rover-route-recorded` (a real harvested crewless 16-part probe rover:
  probe core, Mk2 tank, SAS, four wheels, two conformal storage units, six OX-STAT solar
  panels, a Clamp-O-Tron), the donor `eva-construct-pad` also clones. RENAMED `Miner
  Watcher` (vessel `name` only).
  MOVED to `WATCHER_LAT` / `WATCHER_LON`: 120 m due north of the miner (2.0e-4 rad of
  latitude on Kerbin's 600 km radius). That is inside 250 m (Real Spawn Control's nearby
  radius) and well clear of the miner's spawn bounds. `alt` is the miner's recorded
  terrain height (64.78 m, `terrainHeightAtEnd` of V's recording) plus `ALT_ABOVE_TERRAIN`;
  `skipGroundPositioning = False` stays as the donor has it, so KSP's own
  `Vessel.CheckGroundCollision` settles it on the terrain at load whatever the local
  height is. `sit` becomes LANDED and `landedAt` / `displaylandedAt` empty; ORBIT `EPH`
  is re-stamped to 30 (the donor's is 979.48, later than the fixture UT).
  DERIVED, deterministically (sha256 of `SEED` and the old value): the vessel `pid`, the
  vessel `persistentId` and EVERY `persistentId` under the node; asserted disjoint from
  the miner's. Part `uid`s stay verbatim (asserted disjoint). `lct` / `lastUT` = 30.
  EDITED IN THE SAVE: the `Title` and `Description`. `persistent.loadmeta` stays as the
  base has it (vesselCount 2, UT 30).

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_miner_rover_watched.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "miner-rover-landed")
DONOR_SFS = os.path.join(SAVES, "rover-route-recorded", "persistent.sfs")
OUT_DIR = os.path.join(SAVES, "miner-rover-watched")

sys.path.insert(0, _HERE)
import build_miner_rover_landed as landed  # noqa: E402

LF = "\n"
EDITED = ("persistent.sfs",)
SEED = "miner-rover-watched/watcher/v1"
DONOR_VESSEL_NAME = "rover fuel 0"
WATCHER_NAME = "Miner Watcher"
WATCHER_PART_COUNT = 16
WATCHER_LAT = "0.041075400"
WATCHER_LON = "-74.724749708449622"
TERRAIN_ALT = 64.78
ALT_ABOVE_TERRAIN = 1.40
BASE_TITLE = landed.TITLE
BASE_DESCRIPTION = landed.DESCRIPTION
TITLE = "miner-rover-watched (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: a real mining rover and a "
               "neighbouring rover).")


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _derive_guid(tag: str) -> str:
    return hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:32]


def build_watcher(miner: List[str]) -> List[str]:
    lines = landed._read(DONOR_SFS).split(LF)
    hits = [b for b in landed.vessel_blocks(lines) if b[2] == DONOR_VESSEL_NAME]
    assert len(hits) == 1, "donor vessel not found exactly once"
    start, end, _ = hits[0]
    clone = list(lines[start:end])
    assert not any(l.strip().startswith("crew = ") for l in clone), "the donor must be crewless"
    assert sum(1 for l in clone if l == "\t\t\tPART") == WATCHER_PART_COUNT
    assert "\t\t\tskipGroundPositioning = False" in clone

    pid_re = re.compile(r"^(\t+)persistentId = (\d+)$")
    miner_pids = {int(m.group(2)) for m in (pid_re.match(l) for l in miner) if m}
    miner_guids = {l.strip() for l in miner if l.startswith("\t\t\tpid = ")}
    miner_uids = {l.strip() for l in miner if l.strip().startswith("uid = ")}
    clone_uids = {l.strip() for l in clone if l.strip().startswith("uid = ")}
    assert clone_uids and not (miner_uids & clone_uids), "part uid collision with the miner"

    out = []
    seen = set()
    for line in clone:
        m = pid_re.match(line)
        if line.startswith("\t\t\tpid = "):
            line = "\t\t\tpid = " + _derive_guid("vessel-pid")
            assert line.strip() not in miner_guids
        elif m:
            new_pid = _derive_uint("persistentId|" + m.group(2))
            assert new_pid not in seen, "derived persistentId collision"
            assert new_pid not in miner_pids, "derived persistentId collides with the miner"
            seen.add(new_pid)
            line = "%spersistentId = %d" % (m.group(1), new_pid)
        out.append(line)
    assert len(seen) >= WATCHER_PART_COUNT + 1, "vessel + every part persistentId re-derived"
    landed._set_top(out, "name", WATCHER_NAME)
    landed._set_top(out, "lct", landed.FIXTURE_UT)
    landed._set_top(out, "lastUT", landed.FIXTURE_UT)
    landed._set_top(out, "sit", "LANDED")
    landed._set_top(out, "landedAt", "")
    landed._set_top(out, "displaylandedAt", "")
    landed._set_top(out, "lat", WATCHER_LAT)
    landed._set_top(out, "lon", WATCHER_LON)
    landed._set_top(out, "alt", repr(round(TERRAIN_ALT + ALT_ABOVE_TERRAIN, 4)))
    eph = [i for i, l in enumerate(out) if l.startswith("\t\t\t\tEPH = ")]
    assert len(eph) == 1, "watcher ORBIT EPH found %d times" % len(eph)
    out[eph[0]] = "\t\t\t\tEPH = " + landed.FIXTURE_UT
    return out


def watcher_persistent_id() -> int:
    lines = landed._read(DONOR_SFS).split(LF)
    start, end, _ = [b for b in landed.vessel_blocks(lines) if b[2] == DONOR_VESSEL_NAME][0]
    for line in lines[start:end]:
        if line.startswith("\t\t\tpersistentId = "):
            return _derive_uint("persistentId|" + line[len("\t\t\tpersistentId = "):])
    raise AssertionError("donor vessel has no persistentId")


def build_sfs() -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-landed is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [landed.ROVER_NAME, landed.PROBE_NAME], \
        [b[2] for b in blocks]
    (m_start, m_end, _), (p_start, p_end, _) = blocks
    assert m_end == p_start, "the probe follows the miner directly"
    miner = lines[m_start:m_end]
    lines[p_start:p_end] = build_watcher(miner)
    assert "\t\tactiveVessel = 0" in lines, "the miner stays the active vessel"
    title = lines.index("\tTitle = " + BASE_TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + BASE_DESCRIPTION)
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
    print("wrote " + OUT_DIR + " minerPersistentId=%d watcherPersistentId=%d"
          % (landed.rover_persistent_id(), watcher_persistent_id()))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
