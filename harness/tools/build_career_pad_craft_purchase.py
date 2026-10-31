#!/usr/bin/env python3
"""Derive the committed `career-pad-craft-purchase` fixture from `career-pad-craft` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. Todo STRATEGY-DISCOUNT-ON-GROSS-ROWS-UNLEDGERED owes a lane on the
part-purchase path: Aggressive Negotiations active, a part bought at its discounted entry cost,
a Rewind-to-Launch and the crossing. No committed career could host it:

  - the hosts with entry purchases ON (`career-pad-craft-hard`, `career-science-pad-hard`,
    `stock-screen-census*`) hold reputation 0 or 2, and Aggressive Negotiations needs more:
    stock `Strategy.CanBeActivated` refuses while `ceil(rep) < floor(RequiredReputation)`
    (decompiled; `RequiredReputation` lerps `requiredReputationMin` 38 at the default factor),
    and the setup cost lerps 10..100 reputation to 14.5;
  - every reputation-rich host (`cs-career-mun*`, `cs-career-orbit`) runs
    BypassEntryPurchaseAfterResearch = True, where a part costs nothing.

So this is L2's career pad host with entry purchases on and a reputation seed, and nothing else.

THE DERIVATION. `career-pad-craft`'s files byte for byte, except three lines:

  persistent.sfs       PARAMETERS { DIFFICULTY { BypassEntryPurchaseAfterResearch = True -> False } }
                       SCENARIO Reputation { rep = 0 -> 120 }
  persistent.loadmeta  reputationPercent = 0 -> 12   (`(int)(rep / 10f)`, the load menu's copy)

THE SEED. 120, above the 115 at which `cs-career-mun4` activated Aggressive Negotiations at
the default factor (RW-9 and RW-20, flown), so the activation gate is cleared by a measured
margin rather than by a reading of a config this repository does not carry. The seed is not
an action: a career with no ledger seeds its `ReputationInitial` row from the loaded pool on
the first load (`LedgerOrchestrator`'s seed path, the one `strategy-career` relies on).

THE MULTIPLIERS STAY x1. `career-pad-craft-hard` already has entry purchases on, but its Hard
reputation multipliers would sit between the strategy's reputation leg and the rule under
test; this host differs from L2's only in what the lane needs.

WHAT IS NOT COPIED. `AddOns/` (a DistantObject settings file): the hosted lanes are seam-only.

Run `python harness/tools/build_career_pad_craft_purchase.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_career_pad_craft_purchase.py` runs the check in-process.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft-purchase")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

REPUTATION_SEED = 120

# The reputation at which a flown lane activated Aggressive Negotiations at the default factor
# (cs-career-mun4, RW-9 / RW-20). The seed must not be below it.
MEASURED_AN_ACTIVATION_REP = 115

# (file, anchored regex matching exactly one line, replacement line, enclosing SCENARIO name or
# None). A scoped edit must land inside that SCENARIO's body.
EDITS = (
    ("persistent.sfs", r"(?m)^\t\t\tBypassEntryPurchaseAfterResearch = True$",
     "\t\t\tBypassEntryPurchaseAfterResearch = False", None),
    ("persistent.sfs", r"(?m)^\t\trep = 0$", "\t\trep = %d" % REPUTATION_SEED, "Reputation"),
    ("persistent.loadmeta", r"(?m)^reputationPercent = 0$",
     "reputationPercent = %d" % int(REPUTATION_SEED / 10), None),
)


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _scenario_span(text: str, name: str):
    """(start, end) of the SCENARIO body whose `name = <name>`."""
    at = text.find("\n\t\tname = %s\n" % name)
    if at < 0:
        raise SystemExit("SCENARIO %s not found in %s" % (name, SOURCE_DIR))
    end = text.index("\n\t}\n", at)
    return at, end


def derive_file(name: str, source_bytes: bytes) -> bytes:
    text = source_bytes.decode("utf-8")
    for fname, pattern, replacement, scenario in EDITS:
        if fname != name:
            continue
        matches = list(re.finditer(pattern, text))
        if len(matches) != 1:
            raise SystemExit("expected exactly one match of %r in %s, found %d"
                             % (pattern, name, len(matches)))
        m = matches[0]
        if scenario is not None:
            start, end = _scenario_span(text, scenario)
            if not (start <= m.start() <= end):
                raise SystemExit("%r is not inside SCENARIO %s" % (pattern, scenario))
        text = text[:m.start()] + replacement + text[m.end():]
    return text.encode("utf-8")


def derive_all() -> dict:
    return {name: derive_file(name, _read(os.path.join(SOURCE_DIR, name)))
            for name in COPIED_FILES}


def check() -> list:
    """Differences between a fresh derivation and the committed bytes (empty = OK)."""
    problems = []
    if REPUTATION_SEED < MEASURED_AN_ACTIVATION_REP:
        problems.append("REPUTATION_SEED %d is below the measured Aggressive Negotiations "
                        "activation reputation %d" % (REPUTATION_SEED, MEASURED_AN_ACTIVATION_REP))
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
        print("career-pad-craft-purchase: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
