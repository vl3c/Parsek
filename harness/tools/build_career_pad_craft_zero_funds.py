#!/usr/bin/env python3
"""Derive the committed `career-pad-craft-zero-funds` fixture from `career-pad-craft` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. KSP-SETTINGS-AUDIT S4 (todo KSP-SETTINGS-AUDIT-2026-09-26): the
stock new-game slider allows Starting Funds = 0, and Parsek used to seed the career's
`FundsInitial` row from non-zero pools only, so a zero career stayed unseeded, `PatchFunds`
skipped every recalc as "no FundsInitial seed", and the first non-zero pool (a recovery, a
contract advance) was then taken AS the starting value, double-counting the earning. The fix
(commit d5e3ec399) seeds a CONFIRMED zero once Funding's OnLoad has provably run and the
ledger carries no funds history. Every committed career fixture starts above zero, so no lane
had ever loaded a zero-funds career. This is the L2 career pad host with the funds pool and
the Starting Funds parameter at zero, so a lane on it measures exactly that and nothing else.

THE DERIVATION. `career-pad-craft`'s files byte for byte, except:

  persistent.sfs    SCENARIO Funding { funds = 500000 -> 0 }
                    PARAMETERS { CAREER { StartingFunds = 10000 -> 0 } }
  persistent.loadmeta  funds = 500000 -> 0   (the load menu's cached copy of the pool)

The source carries no `Parsek/` directory (no ledger, no FundsInitial row), so the first load
is a first-ever Parsek load of a zero-funds career: the shape S4 is about. `preset` stays
`Custom` (the source's label; any slider edit flips a stock game to Custom).

WHAT IS NOT COPIED. `AddOns/` (a DistantObject settings file): the lane is seam-only.

Run `python harness/tools/build_career_pad_craft_zero_funds.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_career_pad_craft_zero_funds.py` runs the check in-process.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft-zero-funds")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

# (file, anchored regex matching exactly one line, replacement line).
EDITS = (
    ("persistent.sfs", r"(?m)^\t\tfunds = 500000$", "\t\tfunds = 0"),
    ("persistent.sfs", r"(?m)^\t\t\tStartingFunds = 10000$", "\t\t\tStartingFunds = 0"),
    ("persistent.loadmeta", r"(?m)^funds = 500000$", "funds = 0"),
)


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _funding_span(text: str):
    """(start, end) of the SCENARIO body whose `name = Funding`."""
    at = text.find("\n\t\tname = Funding\n")
    if at < 0:
        raise SystemExit("SCENARIO Funding not found in %s" % SOURCE_DIR)
    end = text.index("\n\t}\n", at)
    return at, end


def derive_file(name: str, source_bytes: bytes) -> bytes:
    text = source_bytes.decode("utf-8")
    for fname, pattern, replacement in EDITS:
        if fname != name:
            continue
        matches = list(re.finditer(pattern, text))
        if len(matches) != 1:
            raise SystemExit("expected exactly one match of %r in %s, found %d"
                             % (pattern, name, len(matches)))
        m = matches[0]
        if name == "persistent.sfs" and "funds = " in replacement and "Starting" not in replacement:
            start, end = _funding_span(text)
            if not (start <= m.start() <= end):
                raise SystemExit("the funds line is not inside SCENARIO Funding")
        text = text[:m.start()] + replacement + text[m.end():]
    return text.encode("utf-8")


def derive_all() -> dict:
    return {name: derive_file(name, _read(os.path.join(SOURCE_DIR, name)))
            for name in COPIED_FILES}


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
        print("career-pad-craft-zero-funds: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
