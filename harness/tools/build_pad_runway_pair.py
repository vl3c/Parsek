#!/usr/bin/env python3
"""Build the committed `pad-runway-pair` fixture BY CONSTRUCTION (the EX-1 host).

WHY THIS FIXTURE EXISTS. `EX-1-ghost-extension-past-endut` needs the non-chain
held-ghost path: a recording whose end-of-recording spawn is BLOCKED in the FLIGHT
scene while its ghost is still playing, so `ParsekPlaybackPolicy.HandlePlaybackCompleted`
holds the ghost past EndUT ("Ghost held pending spawn retry:"). The only blocker that
holds on the non-chain path is the KSC exclusion zone (`VesselSpawner.CheckSpawnCollisions`,
#170: a landed home-world spawn within 50 m of the pad or runway). A vessel parked on the
endpoint does NOT hold: #112 recovers a same-name blocker, walkback (#264) relocates the
spawn, and walkback exhaustion abandons with `VesselSpawned = true`.

So the lane records a pad subject in-run and rewinds it to launch, which strips it. To
see the re-cross in FLIGHT the post-rewind save needs a SECOND focusable vessel AT VESSEL
INDEX 0: a save written at the Space Center carries `activeVessel = 0` (MEASURED: EX-1's
first reading and LF-1's reload both wrote it), so `LoadGame` of the post-rewind save
focuses index 0. With the host asteroid there, EX-1's first reading focused an asteroid in
solar orbit and the pad ghost sat in the `Beyond` zone (dist 6.15e9 m), never live. The
second vessel must also sit inside ghost range of the pad and must not overlap the subject.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the vessel order and focus: `fixtures/saves/logi-cargo-pad`
  (the forged `Logi Cargo Rig` on the pad, no recorded state).
  CLONED: the `rover fuel 0` VESSEL node out of `fixtures/saves/rover-route-recorded`, a
  REAL harvested vessel PRELAUNCH on the runway start, ~1.8 km from the pad. It is
  inserted FIRST, so the list reads [rover, asteroid, Rig], and `activeVessel` moves
  1 -> 2 so the fixture still boots focused on the Rig.
  DERIVED, deterministically (sha256 of a fixed seed and the old value), so the clone
  shares no identity with its donor: the vessel `pid` (launch Guid), the vessel
  `persistentId`, and EVERY `persistentId` under the node (parts and stored parts).
  `lct` / `lastUT` are re-stamped to the base save's UT, so the clone is not a vessel
  launched in the future. Part `uid`s (flight ids, which `ref` and docking data name)
  stay verbatim; the builder asserts they do not collide with the base save's.
  `persistent.loadmeta`'s `vesselCount` is bumped 1 -> 2; everything else is verbatim.

`--check` re-derives and compares against the committed bytes;
`harness/lib/test_pad_runway_pair.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from typing import List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "logi-cargo-pad")
DONOR_SFS = os.path.join(SAVES, "rover-route-recorded", "persistent.sfs")
OUT_DIR = os.path.join(SAVES, "pad-runway-pair")

DONOR_VESSEL_NAME = "rover fuel 0"
BASE_SUBJECT_NAME = "Logi Cargo Rig"
SEED = "pad-runway-pair/v1"


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _vessel_blocks(lines: List[str]) -> List[Tuple[int, int, str]]:
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


def build_texts() -> Tuple[str, str]:
    """Returns (persistent.sfs, persistent.loadmeta) for the fixture."""
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    donor = _read(DONOR_SFS)
    assert "\r" not in base and "\r" not in donor, "fixtures are LF-only"
    base_lines = base.split("\n")
    donor_lines = donor.split("\n")

    base_blocks = _vessel_blocks(base_lines)
    names = [b[2] for b in base_blocks]
    assert len(base_blocks) == 2 and names[1] == BASE_SUBJECT_NAME, names
    assert "\t\tactiveVessel = 1" in base_lines, "base must focus index 1"
    ut_line = next(l for l in base_lines if l.startswith("\t\tUT = "))
    base_ut = ut_line[len("\t\tUT = "):]

    donor_block = [b for b in _vessel_blocks(donor_lines) if b[2] == DONOR_VESSEL_NAME]
    assert len(donor_block) == 1, "donor vessel not found exactly once"
    start, end, _ = donor_block[0]
    clone = list(donor_lines[start:end])

    base_uids = {l.strip()[len("uid = "):] for l in base_lines
                 if l.strip().startswith("uid = ")}
    clone_uids = {l.strip()[len("uid = "):] for l in clone
                  if l.strip().startswith("uid = ")}
    # `uid = 0` is a stored (inventory) part's placeholder, not a flight id.
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

    base_pids = {int(m.group(2)) for m in (pid_re.match(l) for l in base_lines) if m}
    assert not (base_pids & seen_pids), "derived persistentId collides with the base save"

    insert_at = base_blocks[0][0]
    out_lines = base_lines[:insert_at] + rewritten + base_lines[insert_at:]
    focus = out_lines.index("\t\tactiveVessel = 1")
    out_lines[focus] = "\t\tactiveVessel = 2"
    sfs = "\n".join(out_lines)

    meta = _read(os.path.join(BASE_DIR, "persistent.loadmeta"))
    # KSP writes the loadmeta CRLF (committed byte-preserved), the .sfs LF.
    meta, n = re.subn(r"^vesselCount = 1(\r?\n)", r"vesselCount = 2\1", meta, count=1,
                      flags=re.M)
    assert n == 1, "loadmeta vesselCount = 1 not found"
    return sfs, meta


def _committed_matches(sfs: str, meta: str) -> List[str]:
    problems = []
    for name, want in (("persistent.sfs", sfs), ("persistent.loadmeta", meta)):
        path = os.path.join(OUT_DIR, name)
        if not os.path.isfile(path):
            problems.append("missing " + name)
        elif _read(path) != want:
            problems.append("drift in " + name)
    base_addons = os.path.join(BASE_DIR, "AddOns", "DistantObject", "Settings.cfg")
    out_addons = os.path.join(OUT_DIR, "AddOns", "DistantObject", "Settings.cfg")
    if not os.path.isfile(out_addons) or _read(out_addons) != _read(base_addons):
        problems.append("drift in AddOns/DistantObject/Settings.cfg")
    return problems


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="re-derive and compare against the committed fixture")
    args = ap.parse_args(argv)
    sfs, meta = build_texts()
    if args.check:
        problems = _committed_matches(sfs, meta)
        for p in problems:
            print("DRIFT: " + p)
        print("OK" if not problems else "FAIL")
        return 0 if not problems else 1
    os.makedirs(OUT_DIR, exist_ok=True)
    for name, text in (("persistent.sfs", sfs), ("persistent.loadmeta", meta)):
        with open(os.path.join(OUT_DIR, name), "w", encoding="utf-8", newline="") as fh:
            fh.write(text)
    addons = os.path.join(OUT_DIR, "AddOns", "DistantObject")
    os.makedirs(addons, exist_ok=True)
    shutil.copyfile(os.path.join(BASE_DIR, "AddOns", "DistantObject", "Settings.cfg"),
                    os.path.join(addons, "Settings.cfg"))
    print("wrote " + OUT_DIR)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
