#!/usr/bin/env python3
"""Build the committed `seat-rover-pad` fixture BY CONSTRUCTION (the SEAT-2 / SEAT-3 host).

WHY THIS FIXTURE EXISTS. Two seat fixes (GC-11, 2026-10-24) had unit tests only:
  SEAT-OCCUPANT-LINK-LOST-ON-SPAWN: a Parsek spawn regenerates every part uid, and a
    stock external command seat names its occupant by part flightID (`KerbalSeat`
    `occupantID`), so a spawned vessel with a seated kerbal loaded with the seat empty.
    The fix (`VesselSnapshotOps.RemapSeatOccupantUids`) runs only on a REGENERATED spawn
    of a vessel the replayed recordings themselves produced, and an end-of-flight spawn
    LANDED inside 50 m of the pad or the runway threshold is retired, never spawned, so
    `seat-pad`'s pod on the pad can never exercise it.
  FOREIGN-SEAT-BOARD-LEAVES-NO-GHOST-CLAIM: a recorded EVA kerbal taking the seat of a
    vessel outside his tree stamped no partner on the Dock branch point, so neither the
    chain walker nor the rewind hold claimed that vessel.
Both need an external command seat on a vessel parked OUTSIDE the KSC exclusion zone,
next to a crewed vessel a kerbal can step out of. `eva-construct-pad` has everything but
the seat: the pad Kerbal X (Jeb, Bill, Bob) and a crewless rover 153 m away.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: `fixtures/saves/eva-construct-pad` (its own
  builder `build_eva_construct_pad.py` derives it from `eva3-pad-3crew` and
  `rover-route-recorded`). Every file but persistent.sfs is copied byte for byte; the
  rover keeps its persistentId 1871615455 and launch Guid, which the SEAT lanes name.
  EDITED: the save `Title` (so the Load menu names the fixture).
  ADDED: one PART, an EAS-1 External Command Seat (`seatExternalCmd`), appended as the
  rover's 17th and last part (index 16), SURFACE-attached (`attm = 1`,
  `srfN = srfAttach, 2`) on top of the rover's Advanced Inline Stabilizer (part 2, the
  `advSasModule` at the rear). Its scalar header and its EVENTS / ACTIONS / PARTDATA tail
  are the SAS part's own lines (same vessel, same mission / launch ids, same thermal
  state), with these keys rewritten: name, cid, uid, persistentId, parent, position,
  rotation, attm, srfN (and the SAS's two attN lines dropped), mass, rTrf. NO MODULE node
  is written: a ProtoPartSnapshot with fewer MODULE nodes than its prefab keeps the
  prefab's own module defaults (the same tolerance that lets a Module Manager patch add a
  module to a part in an existing save), so KerbalSeat loads with `occupantID` 0, an
  empty seat. GUESSED, the first flight confirms: that tolerance for this part, and the
  pose below.
  POSE. The rover was built in the SPH: its parts lie along vessel-local +y (the probe
  core is the root at the front, the SAS at y = -2.174), the wheels hang on the +z side
  and the solar panels face -z, so vessel-local -z is the sky. The seat sits on the SAS's
  top surface (z = -0.3125, the 0.625 m part's radius) with rotation (-0.7071068, 0, 0,
  0.7071068), a -90 deg turn about x that maps the seat's own up (+y) to vessel -z. The
  seat's srfAttach node offset is taken as 0, as in `seat-pad` (a gap or an overlap
  between the seat base and the SAS is cosmetic: a surface joint is made at the recorded
  position and same-vessel parts do not collide). The seat is ~2.2 m from the rover's
  root, so a kerbal set down 4 m from the root (`EvaGroundScience step distance=4`, the
  EVA-11 move) is at most ~6.2 m from it, inside `EvaSeatBoard`'s 10 m bound.
  DERIVED, deterministically (sha256 of the seed `seat-rover-pad/v1` and a tag): the
  seat's cid, uid (flightID) and persistentId; the builder asserts neither collides with
  any value in the base save.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_seat_rover_pad.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "eva-construct-pad")
OUT_DIR = os.path.join(SAVES, "seat-rover-pad")

ROVER_NAME = "rover fuel 0"
ROVER_PERSISTENT_ID = 1871615455
ROVER_GUID = "9892740591b9b500ab14bbc687377a0b"
ROVER_BASE_PART_COUNT = 16
BASE_TITLE = "eva-construct-pad (SANDBOX)"
TITLE = "seat-rover-pad (SANDBOX)"
SEED = "seat-rover-pad/v1"
LF = "\n"
EDITED = ("persistent.sfs",)

SEAT_PART = "seatExternalCmd"
# The part the seat is surface-attached to, by its index in the rover's PART list.
SEAT_PARENT_INDEX = 2
SEAT_PARENT_PART = "advSasModule"
# Vessel-local pose (see the module docstring): on the SAS's top surface, upright.
SEAT_POSITION = "0,-2.1740291118621826,-0.3125"
SEAT_ROTATION = "-0.707106769,0,0,0.707106769"
# Stock EAS-1 dry mass (t), written the way KSP writes a float mass.
SEAT_MASS = "0.0500000007"

P3 = "\t\t\t"
P4 = "\t\t\t\t"


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def vessel_blocks(lines: List[str]) -> List[Tuple[int, int, str]]:
    """(start, end_exclusive, name) of every depth-2 VESSEL block under FLIGHTSTATE."""
    out = []
    in_fs = False
    i = 0
    while i < len(lines):
        line = lines[i]
        if line == "\tFLIGHTSTATE":
            in_fs = True
        elif in_fs and line == "\t}":
            break
        elif in_fs and line == "\t\tVESSEL":
            assert lines[i + 1] == "\t\t{", "VESSEL not followed by an open brace"
            j = i + 2
            while lines[j] != "\t\t}":
                j += 1
            name = ""
            for k in range(i + 2, j):
                if lines[k].startswith(P3 + "name = "):
                    name = lines[k][len(P3 + "name = "):]
                    break
            out.append((i, j + 1, name))
            i = j
        i += 1
    return out


def part_blocks(lines: List[str], start: int, end: int) -> List[Tuple[int, int]]:
    """(start, end_exclusive) of every depth-3 PART block inside lines[start:end]."""
    out = []
    i = start
    while i < end:
        if lines[i] == P3 + "PART":
            assert lines[i + 1] == P3 + "{", "PART not followed by an open brace"
            j = i + 2
            while lines[j] != P3 + "}":
                j += 1
            out.append((i, j + 1))
            i = j
        i += 1
    return out


def _part_name(block: List[str]) -> str:
    return block[2][len(P4 + "name = "):]


def build_seat_part(sas_block: List[str]) -> List[str]:
    """The seat's PART block, derived from the SAS part's header and tail (no MODULE)."""
    assert sas_block[0] == P3 + "PART" and sas_block[1] == P3 + "{"
    assert _part_name(sas_block) == SEAT_PARENT_PART, _part_name(sas_block)
    body = sas_block[2:-1]
    first_module = next((k for k, l in enumerate(body) if l == P4 + "MODULE"), len(body))
    body = body[:first_module]
    replace = {
        "name": SEAT_PART,
        "cid": str(_derive_uint("seat-cid")),
        "uid": str(_derive_uint("seat-uid")),
        "persistentId": str(_derive_uint("seat-persistentId")),
        "parent": str(SEAT_PARENT_INDEX),
        "position": SEAT_POSITION,
        "rotation": SEAT_ROTATION,
        "attm": "1",
        "srfN": "srfAttach, %d" % SEAT_PARENT_INDEX,
        "mass": SEAT_MASS,
        "rTrf": SEAT_PART,
    }
    seen = set()
    out = []
    for line in body:
        if line.startswith(P4 + "attN = "):
            continue  # a surface-attached seat has no stack node links
        if line.startswith(P4) and not line.startswith(P4 + "\t") and " = " in line:
            key = line[len(P4):].split(" = ", 1)[0]
            if key in replace:
                assert key not in seen, "SAS header key %r twice" % key
                seen.add(key)
                line = P4 + key + " = " + replace[key]
        out.append(line)
    missing = set(replace) - seen
    assert not missing, "SAS header lacks %s" % sorted(missing)
    return [P3 + "PART", P3 + "{"] + out + [P3 + "}"]


def build_sfs() -> str:
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "eva-construct-pad is committed LF"
    lines = base.split(LF)
    blocks = vessel_blocks(lines)
    rover = [b for b in blocks if b[2] == ROVER_NAME]
    assert len(rover) == 1, "rover not found exactly once"
    vstart, vend, _ = rover[0]
    assert P3 + "persistentId = %d" % ROVER_PERSISTENT_ID in lines[vstart:vend]
    assert P3 + "pid = " + ROVER_GUID in lines[vstart:vend]
    parts = part_blocks(lines, vstart, vend)
    assert len(parts) == ROVER_BASE_PART_COUNT, len(parts)
    assert not any("KerbalSeat" in l or SEAT_PART in l for l in lines[vstart:vend]), \
        "the base rover already carries a seat"
    sas_start, sas_end = parts[SEAT_PARENT_INDEX]
    seat = build_seat_part(lines[sas_start:sas_end])

    # The derived identities must be fresh in the whole save.
    values = set()
    for l in lines:
        s = l.strip()
        for key in ("uid = ", "persistentId = ", "cid = "):
            if s.startswith(key):
                values.add(s[len(key):])
    for key in ("seat-cid", "seat-uid", "seat-persistentId"):
        assert str(_derive_uint(key)) not in values, "derived %s collides with the base" % key

    last_end = parts[-1][1]
    out = lines[:last_end] + seat + lines[last_end:]
    title = out.index("\tTitle = " + BASE_TITLE)
    out[title] = "\tTitle = " + TITLE
    return LF.join(out)


def expected_files() -> Dict[str, bytes]:
    """{'/'-separated relative path: bytes} of every file the fixture holds."""
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = _read_bytes(path)
    files["persistent.sfs"] = build_sfs().encode("utf-8")
    return files


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    if os.path.isdir(OUT_DIR):
        for root, _dirs, names in os.walk(OUT_DIR):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, OUT_DIR).replace(os.sep, "/")] = _read_bytes(path)
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
    print("wrote " + OUT_DIR + " seatUid=%d seatPersistentId=%d"
          % (_derive_uint("seat-uid"), _derive_uint("seat-persistentId")))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
