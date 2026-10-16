#!/usr/bin/env python3
"""Build the committed `eva-construct-pad` fixture BY CONSTRUCTION (the EVA-11 host).

WHY THIS FIXTURE EXISTS. EVA-CONSTRUCTION-ON-FOREIGN-VESSEL-UNCLAIMED (and roadmap lane
RC-6) needs a FOREIGN vessel - one no committed or live tree carries - parked within
EVA construction reach of a kerbal an Engineer can step out with, on a save with no
recordings, so the only tree in play is the one the lane records. No committed fixture
had one: every rover fixture's rover is itself a recorded tree member.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: `fixtures/saves/eva3-pad-3crew` (SANDBOX,
  no Parsek footprint, the Kerbal X PRELAUNCH on the pad with Jeb, Bill (an Engineer)
  and Bob aboard, plus the stock starting asteroid).
  CLONED: the `rover fuel 0` VESSEL node out of `fixtures/saves/rover-route-recorded`, a
  REAL harvested crewless probe rover (16 parts: probe core, Mk2 tank, SAS, four
  wheels, two conformal storage units, six OX-STAT solar panels, a Clamp-O-Tron whose
  top node is free) - the same donor `pad-runway-pair` and `k2-held-kerbal-pad` clone.
  MOVED to lat/lon `ROVER_LAT` / `ROVER_LON`, about 153 m from the pad on bearing ~116,
  on the flat `KSC_Pad_Grounds` apron at terrain ~67.80 m (measured: a Kerbal X debris
  part that came to rest there in `eve-orbit-recorded`, alt 68.398 - hgt 0.603; two more
  debris rests 149-155 m out on bearings 116-118 read 67.77-67.78). That is outside the
  KSC pad exclusion (50 m) and inside the 2.25 km landed load range. `alt` is set
  `ROVER_ALT_ABOVE_TERRAIN` above that terrain (the donor's own `hgt` plus a margin);
  `skipGroundPositioning = False` stays, so KSP's own ground check settles it on load.
  `sit` becomes LANDED and `landedAt` / `displaylandedAt` empty (it is not on the runway
  any more). `rot` (srfRelRotation) stays verbatim: the local up differs by ~0.2 deg.
  DERIVED, deterministically (sha256 of a fixed seed and the old value), so the clone
  shares no identity with its donor: the vessel `pid` (launch Guid), the vessel
  `persistentId`, and EVERY `persistentId` under the node. `lct` / `lastUT` are
  re-stamped to the base save's UT. Part `uid`s stay verbatim; the builder asserts they
  do not collide with the base save's.
  REORDERED: the base lists [asteroid, Kerbal X] with `activeVessel = 1`; the fixture
  lists [Kerbal X, asteroid, rover] with `activeVessel = 0`, so a Space Center save of
  this world (whose focus is vessel 0) reloads FLIGHT on the pad, not at the asteroid.
  EDITED: the save `Title` (so the Load menu names the fixture) and
  `persistent.loadmeta`'s `vesselCount` 1 -> 2.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_eva_construct_pad.py` runs it in-process.

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
BASE_DIR = os.path.join(SAVES, "eva3-pad-3crew")
DONOR_SFS = os.path.join(SAVES, "rover-route-recorded", "persistent.sfs")
OUT_DIR = os.path.join(SAVES, "eva-construct-pad")

DONOR_VESSEL_NAME = "rover fuel 0"
BASE_PAD_VESSEL = "Kerbal X"
BASE_TITLE = "eva3-pad-3crew (SANDBOX)"
TITLE = "eva-construct-pad (SANDBOX)"
ENGINEER = "Bill Kerman"
SEED = "eva-construct-pad/v1"
LF = "\n"
EDITED = ("persistent.sfs", "persistent.loadmeta")

ROVER_LAT = "-0.103559624817681"
ROVER_LON = "-74.544550495871917"
TERRAIN_ALT = 67.80
ROVER_ALT_ABOVE_TERRAIN = 1.40


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


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
                if lines[k].startswith("\t\t\tname = "):
                    name = lines[k][len("\t\t\tname = "):]
                    break
            out.append((i, j + 1, name))
            i = j
        i += 1
    return out


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _derive_guid(tag: str) -> str:
    return hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:32]


def rover_persistent_id() -> int:
    """The clone's vessel persistentId (the pid EVA-11 names as the step anchor)."""
    donor = _read(DONOR_SFS).split(LF)
    block = [b for b in vessel_blocks(donor) if b[2] == DONOR_VESSEL_NAME]
    assert len(block) == 1, "donor vessel not found exactly once"
    start, end, _ = block[0]
    for line in donor[start:end]:
        if line.startswith("\t\t\tpersistentId = "):
            return _derive_uint("persistentId|" + line[len("\t\t\tpersistentId = "):])
    raise AssertionError("donor vessel has no persistentId")


def _set_top(clone: List[str], key: str, value: str) -> None:
    prefix = "\t\t\t" + key + " = "
    hits = [i for i, l in enumerate(clone) if l.startswith(prefix)]
    assert len(hits) == 1, "donor vessel key %r found %d times" % (key, len(hits))
    clone[hits[0]] = prefix + value


def build_texts() -> Tuple[str, str]:
    """Returns (persistent.sfs, persistent.loadmeta) for the fixture."""
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    donor = _read(DONOR_SFS)
    assert "\r" not in base, "eva3-pad-3crew is committed LF"
    assert "\r" not in donor, "rover-route-recorded is committed LF"
    base_lines = base.split(LF)
    donor_lines = donor.split(LF)

    base_blocks = vessel_blocks(base_lines)
    assert [b[2] for b in base_blocks][1] == BASE_PAD_VESSEL, [b[2] for b in base_blocks]
    assert len(base_blocks) == 2, [b[2] for b in base_blocks]
    assert "\t\tactiveVessel = 1" in base_lines, "base must focus index 1 (the Kerbal X)"
    (ast_start, ast_end, _), (pad_start, pad_end, _) = base_blocks
    assert ast_end <= pad_start
    assert any(l.strip() == "crew = " + ENGINEER for l in base_lines[pad_start:pad_end]), \
        "the base pad craft must carry " + ENGINEER
    ut_line = next(l for l in base_lines if l.startswith("\t\tUT = "))
    base_ut = ut_line[len("\t\tUT = "):]

    donor_block = [b for b in vessel_blocks(donor_lines) if b[2] == DONOR_VESSEL_NAME]
    assert len(donor_block) == 1, "donor vessel not found exactly once"
    start, end, _ = donor_block[0]
    clone = list(donor_lines[start:end])
    assert not any(l.strip().startswith("crew = ") for l in clone), "the donor must be crewless"

    base_uids = {l.strip()[len("uid = "):] for l in base_lines if l.strip().startswith("uid = ")}
    clone_uids = {l.strip()[len("uid = "):] for l in clone if l.strip().startswith("uid = ")}
    assert not ((base_uids & clone_uids) - {"0"}), "part flight-id collision with the base save"

    pid_re = re.compile(r"^(\t+)persistentId = (\d+)$")
    rewritten = []
    seen_pids = set()
    for line in clone:
        m = pid_re.match(line)
        if line.startswith("\t\t\tpid = "):
            line = "\t\t\tpid = " + _derive_guid("vessel-pid")
        elif line.startswith("\t\t\tlct = "):
            line = "\t\t\tlct = " + base_ut
        elif line.startswith("\t\t\tlastUT = "):
            line = "\t\t\tlastUT = " + base_ut
        elif m:
            new_pid = _derive_uint("persistentId|" + m.group(2))
            assert new_pid not in seen_pids, "derived persistentId collision"
            seen_pids.add(new_pid)
            line = "%spersistentId = %d" % (m.group(1), new_pid)
        rewritten.append(line)
    _set_top(rewritten, "sit", "LANDED")
    _set_top(rewritten, "landedAt", "")
    _set_top(rewritten, "displaylandedAt", "")
    _set_top(rewritten, "lat", ROVER_LAT)
    _set_top(rewritten, "lon", ROVER_LON)
    _set_top(rewritten, "alt", repr(round(TERRAIN_ALT + ROVER_ALT_ABOVE_TERRAIN, 4)))

    base_pids = {int(m.group(2)) for m in (pid_re.match(l) for l in base_lines) if m}
    assert not (base_pids & seen_pids), "derived persistentId collides with the base save"

    asteroid = base_lines[ast_start:ast_end]
    pad = base_lines[pad_start:pad_end]
    out_lines = (base_lines[:ast_start] + pad + asteroid + rewritten
                 + base_lines[ast_end:pad_start] + base_lines[pad_end:])
    active = out_lines.index("\t\tactiveVessel = 1")
    out_lines[active] = "\t\tactiveVessel = 0"
    title = out_lines.index("\tTitle = " + BASE_TITLE)
    out_lines[title] = "\tTitle = " + TITLE
    sfs = LF.join(out_lines)

    meta = _read(os.path.join(BASE_DIR, "persistent.loadmeta"))
    meta, n = re.subn(r"^vesselCount = 1(\r?\n)", r"vesselCount = 2\1", meta, count=1, flags=re.M)
    assert n == 1, "loadmeta vesselCount = 1 not found"
    return sfs, meta


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
    sfs, meta = build_texts()
    files["persistent.sfs"] = sfs.encode("utf-8")
    files["persistent.loadmeta"] = meta.encode("utf-8")
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
    print("wrote " + OUT_DIR + " roverPersistentId=%d" % rover_persistent_id())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
