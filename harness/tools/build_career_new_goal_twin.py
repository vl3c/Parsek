#!/usr/bin/env python3
"""Derive the committed `career-new-goal-twin` fixture from `cs-career-new` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. Todo CONTRACT-TWIN-GUID-SEEDED-TYPES (owner ruling 2026-10-27,
"check both IDs and the other identifiers"): after a rewind to before a committed accept of an
exploration contract, stock can generate the same goal again under a new guid, and that copy's
stock `ContractID` differs from the committed one's (the exploration contract's
`ProgressTrackingParameter` hashes `SystemUtilities.SuperSeed(Root)`, which reads the guid).
Parsek withdraws such a copy by its goal key. Stock generates a copy only by chance, so a lane
that waited for one would be a lane about luck. This fixture puts the copy in the pool from the
start: the stock New Game's Offered "reach space" exploration contract (guid
31f5240c-4c87-4a98-803b-1e84e1f19ce6, goal `ExplorationContract:REACHSPACE@1`) and a second
Offered copy of it under another guid and seed, exactly the pair a regeneration produces. Its
lane is CTW-1-contract-goal-twin-withdrawn.

THE DERIVATION. `cs-career-new`'s `persistent.sfs` byte for byte, plus ONE CONTRACT node
inserted right after the original's closing brace: the original node with its `guid` and
`seed` lines replaced (TWIN_GUID, TWIN_SEED). Nothing else changes; `persistent.loadmeta` is
copied byte for byte (it counts no Offered contract).

Run `python harness/tools/build_career_new_goal_twin.py` to (re)write the fixture and `--check`
to re-derive and compare against the committed bytes; `harness/lib/test_career_new_goal_twin.py`
runs the check in-process.
"""
from __future__ import annotations

import argparse
import os
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-new")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-new-goal-twin")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

ORIGINAL_GUID = "31f5240c-4c87-4a98-803b-1e84e1f19ce6"
ORIGINAL_SEED = "-1389357956"
TWIN_GUID = "d7c2a0b4-5e1f-4c3a-9b8e-2f6a1c4d7e90"
TWIN_SEED = "-1389351234"
GOAL_KEY = "ExplorationContract:REACHSPACE@1"

CONTRACT_HEADER = "\t\t\tCONTRACT"
CONTRACT_CLOSE = "\t\t\t}"


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def find_contract_block(lines: list, guid: str) -> tuple:
    """(start, end) line indexes of the CONTRACT node carrying `guid`, end inclusive."""
    guid_line = "\t\t\t\tguid = " + guid
    for i, line in enumerate(lines):
        if line != guid_line:
            continue
        start = i
        while start >= 0 and lines[start] != CONTRACT_HEADER:
            start -= 1
        end = i
        while end < len(lines) and lines[end] != CONTRACT_CLOSE:
            end += 1
        if start < 0 or end >= len(lines):
            break
        return start, end
    raise SystemExit("contract %s not found in %s" % (guid, SOURCE_DIR))


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    if b"\r\n" in source_bytes:
        raise SystemExit("%s/persistent.sfs is expected to use LF line ends" % SOURCE_DIR)
    lines = source_bytes.decode("ascii").split("\n")
    start, end = find_contract_block(lines, ORIGINAL_GUID)
    block = lines[start:end + 1]
    twin = []
    swapped = 0
    for line in block:
        if line == "\t\t\t\tguid = " + ORIGINAL_GUID:
            line = "\t\t\t\tguid = " + TWIN_GUID
            swapped += 1
        elif line == "\t\t\t\tseed = " + ORIGINAL_SEED:
            line = "\t\t\t\tseed = " + TWIN_SEED
            swapped += 1
        twin.append(line)
    if swapped != 2:
        raise SystemExit("expected to swap guid and seed, swapped %d line(s)" % swapped)
    out = lines[:end + 1] + twin + lines[end + 1:]
    return "\n".join(out).encode("ascii")


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
        print("career-new-goal-twin: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
