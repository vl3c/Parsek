#!/usr/bin/env python3
"""Build the committed `k2-held-kerbal-pad` fixture BY CONSTRUCTION (the KB-5 host).

WHY THIS FIXTURE EXISTS. Block audit row K2
(docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md): a kerbal a committed
flight HOLDS, aboard a live vessel that continues no committed flight, may not be taken
out on EVA or transferred (`FlightEVA.spawnEVA` / `CrewTransfer.Create` backstops, the
portrait and hatch-dialog greys). No committed fixture booted that state, so the block
had never run in game.

`career-earned-pad` nearly is it: Jebediah Kerman is held open-ended by the career's
committed `Jumping Flea` flight (`KerbalsModule`: ReservedActive, endUT -> Infinity) and
sits in the pad craft, whose launch guid and pid were re-stamped away from the recorded
flight's, so it continues no committed flight. But the pad craft is the ACTIVE vessel,
and the flight-ready crew swap (`CrewReservationManager.SwapReservedCrewInFlight`) takes
a held kerbal out of the active vessel on boot, putting his stand-in in the seat. K2 is
about a held kerbal aboard a vessel the player SWITCHES to, which no flight-ready swap
touches.

So this fixture gives the save a second, focusable vessel to boot on, and leaves Jeb
aboard the non-active pad craft.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the two edits below: `fixtures/saves/career-earned-pad`
  (the whole tree: the Parsek sidecars, ledger and GameState files are its payload).
  CLONED: the `rover fuel 0` VESSEL node out of `fixtures/saves/rover-route-recorded`, a
  REAL harvested crewless probe PRELAUNCH on the runway start, ~1.8 km from the pad, the
  same donor `pad-runway-pair` clones (inside the 2.25 km landed load range, so the pad
  craft is loaded and a Switch-To reaches it without a scene reload). It is inserted
  FIRST, so the list reads [rover, pad craft] and the base's `activeVessel = 0` now boots
  focused on the rover: nothing held rides it, so the flight-ready swap has nothing to do.
  DERIVED, deterministically (sha256 of a fixed seed and the old value), so the clone
  shares no identity with its donor: the vessel `pid` (launch Guid), the vessel
  `persistentId`, and EVERY `persistentId` under the node. `lct` / `lastUT` are
  re-stamped to the base save's UT. Part `uid`s stay verbatim; the builder asserts they
  do not collide with the base save's. The donor is LF, the base CRLF; the clone is
  written CRLF.
  EDITED: the save `Title` (so the Load menu names the fixture) and
  `persistent.loadmeta`'s `vesselCount` 1 -> 2.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_k2_held_kerbal_pad.py` runs it in-process.

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
BASE_DIR = os.path.join(SAVES, "career-earned-pad")
DONOR_SFS = os.path.join(SAVES, "rover-route-recorded", "persistent.sfs")
OUT_DIR = os.path.join(SAVES, "k2-held-kerbal-pad")

DONOR_VESSEL_NAME = "rover fuel 0"
BASE_TITLE = "career-earned-pad (CAREER)"
TITLE = "k2-held-kerbal-pad (CAREER)"
HELD_KERBAL = "Jebediah Kerman"
SEED = "k2-held-kerbal-pad/v1"
CRLF = "\r\n"
EDITED = ("persistent.sfs", "persistent.loadmeta")


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


def build_texts() -> Tuple[str, str]:
    """Returns (persistent.sfs, persistent.loadmeta) for the fixture."""
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    donor = _read(DONOR_SFS)
    assert CRLF in base, "career-earned-pad is committed CRLF"
    assert "\r" not in donor, "rover-route-recorded is committed LF"
    base_lines = base.split(CRLF)
    donor_lines = donor.split("\n")

    base_blocks = vessel_blocks(base_lines)
    assert len(base_blocks) == 1, [b[2] for b in base_blocks]
    assert "\t\tactiveVessel = 0" in base_lines, "base must focus index 0"
    pad_start, pad_end, _ = base_blocks[0]
    assert any(l.strip() == "crew = " + HELD_KERBAL for l in base_lines[pad_start:pad_end]), \
        "the base pad craft must carry " + HELD_KERBAL
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

    base_pids = {int(m.group(2)) for m in (pid_re.match(l) for l in base_lines) if m}
    assert not (base_pids & seen_pids), "derived persistentId collides with the base save"

    out_lines = base_lines[:pad_start] + rewritten + base_lines[pad_start:]
    title = out_lines.index("\tTitle = " + BASE_TITLE)
    out_lines[title] = "\tTitle = " + TITLE
    sfs = CRLF.join(out_lines)

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
    print("wrote " + OUT_DIR)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
