#!/usr/bin/env python3
"""Derive the committed `career-pad-craft-hard` fixture from `career-pad-craft` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. The game-settings test axis (operator request 2026-09-29):
every committed career fixture carried x1 multipliers and quickload on, so no lane had
ever run Parsek's ledger under a stock HARD career. This is the L2 career pad host with
KSP's own Hard preset values applied, so a lane on it measures Parsek under Hard and
nothing else different.

THE DERIVATION. `career-pad-craft`'s persistent.sfs byte for byte, except the values
inside the top-level `PARAMETERS` node that differ between that save and KSP 1.12.5's
Hard preset (decompiled `GameParameters.SetDifficultyPresets`, the `Preset.Hard` entry):

  PARAMETERS { preset = Custom -> Hard }
  PARAMETERS { FLIGHT { CanQuickLoad, CanRestart, CanLeaveToEditor = True -> False } }
  PARAMETERS { CAREER { FundsGainMultiplier, RepGainMultiplier,
                        ScienceGainMultiplier = 1 -> 0.6,
                        FundsLossMultiplier, RepLossMultiplier = 1 -> 2 } }
  PARAMETERS { DIFFICULTY { BypassEntryPurchaseAfterResearch = True -> False,
                            AllowOtherLaunchSites = False -> True } }

Every other Hard value is ALREADY what the source carries (StartingFunds 10000,
RepLossDeclined 3, MissingCrewsRespawn False, ResourceAbundance 0.5, ReentryHeatScale 1,
EnableCommNet True, AllowStockVessels False, persistKerbalInventories False), and each
of those is asserted by the drift test rather than assumed. `preset = Hard` is a label:
decompiled `GameParameters.Load` parses it into the enum and then loads every section's
values from the save as written, so the values above are what the game runs with.

WHAT IS NOT COPIED. `AddOns/` (a DistantObject settings file) is left out: the lanes
this fixture hosts are seam-only. `persistent.loadmeta` is copied byte for byte.

Run `python harness/tools/build_career_pad_craft_hard.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_career_pad_craft_hard.py` runs the check in-process so a hand edit to
either fixture reds in the unit suite rather than in a flight.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft-hard")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

# (section inside PARAMETERS, key, before, after). An empty section means the value
# sits directly on PARAMETERS.
EDITS = (
    ("", "preset", "Custom", "Hard"),
    ("FLIGHT", "CanQuickLoad", "True", "False"),
    ("FLIGHT", "CanRestart", "True", "False"),
    ("FLIGHT", "CanLeaveToEditor", "True", "False"),
    ("DIFFICULTY", "BypassEntryPurchaseAfterResearch", "True", "False"),
    ("DIFFICULTY", "AllowOtherLaunchSites", "False", "True"),
    ("CAREER", "FundsGainMultiplier", "1", "0.6"),
    ("CAREER", "RepGainMultiplier", "1", "0.6"),
    ("CAREER", "ScienceGainMultiplier", "1", "0.6"),
    ("CAREER", "FundsLossMultiplier", "1", "2"),
    ("CAREER", "RepLossMultiplier", "1", "2"),
)

# KSP 1.12.5's Hard preset values the source already carries (asserted, not edited).
HARD_VALUES_ALREADY_PRESENT = (
    ("DIFFICULTY", "MissingCrewsRespawn", "False"),
    ("DIFFICULTY", "ResourceAbundance", "0.5"),
    ("DIFFICULTY", "ReentryHeatScale", "1"),
    ("DIFFICULTY", "EnableCommNet", "True"),
    ("DIFFICULTY", "AllowStockVessels", "False"),
    ("DIFFICULTY", "persistKerbalInventories", "False"),
    ("CAREER", "StartingFunds", "10000"),
    ("CAREER", "RepLossDeclined", "3"),
)


def _read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _node_span(text: str, header: str, indent: str, start: int = 0, end: int = None):
    """(body_start, body_end) of the first `<indent><header>\\n<indent>{\\n` node in
    text[start:end]; the node closes at the first `\\n<indent>}\\n` after it."""
    end = len(text) if end is None else end
    opener = "\n" + indent + header + "\n" + indent + "{\n"
    at = text.find(opener, start, end)
    if at < 0:
        raise SystemExit("node %r not found in %s" % (header, SOURCE_DIR))
    body_start = at + len(opener)
    body_end = text.index("\n" + indent + "}\n", body_start)
    if body_end > end:
        raise SystemExit("node %r overruns its parent" % header)
    return body_start, body_end


def section_values(text: str, section: str) -> dict:
    """{key: value} of PARAMETERS/<section>, or of PARAMETERS' own values for ''."""
    p_start, p_end = _node_span(text, "PARAMETERS", "\t")
    if not section:
        first_child = text.find("\t\t{\n", p_start, p_end)
        body = text[p_start:first_child if first_child >= 0 else p_end]
        return dict(re.findall(r"(?m)^\t\t(\w+) = (.*)$", body))
    s_start, s_end = _node_span(text, section, "\t\t", p_start, p_end)
    return dict(re.findall(r"(?m)^\t\t\t(\w+) = (.*)$", text[s_start:s_end]))


def _set_value(text: str, start: int, end: int, indent: str, key: str,
               before: str, after: str) -> str:
    pat = re.compile(r"(?m)^" + re.escape(indent + key) + r" = " + re.escape(before) + r"$")
    matches = list(pat.finditer(text, start, end))
    if len(matches) != 1:
        raise SystemExit("expected exactly one `%s = %s` at depth %d, found %d"
                         % (key, before, len(indent), len(matches)))
    m = matches[0]
    return text[:m.start()] + indent + key + " = " + after + text[m.end():]


def derive_persistent_sfs(source_bytes: bytes) -> bytes:
    text = source_bytes.decode("utf-8")
    for section, key, before, after in EDITS:
        # Spans are recomputed per edit: a changed value shifts every later offset.
        p_start, p_end = _node_span(text, "PARAMETERS", "\t")
        if section:
            s_start, s_end = _node_span(text, section, "\t\t", p_start, p_end)
            text = _set_value(text, s_start, s_end, "\t\t\t", key, before, after)
        else:
            first_child = text.find("\t\t{\n", p_start, p_end)
            text = _set_value(text, p_start, first_child if first_child >= 0 else p_end,
                              "\t\t", key, before, after)
    for section, key, value in HARD_VALUES_ALREADY_PRESENT:
        got = section_values(text, section).get(key)
        if got != value:
            raise SystemExit("source %s/%s = %r, the Hard preset needs %r"
                             % (section, key, got, value))
    return text.encode("utf-8")


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
        print("career-pad-craft-hard: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
