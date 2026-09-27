#!/usr/bin/env python3
"""Derive the committed `gloops-airshow-hard` fixture from `gloops-airshow` BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. KSP-SETTINGS-AUDIT-2026-09-26 S7 (owner ruling 2026-09-26):
Rewind-to-Separation and Re-Fly ignore the stock Hard flags, and on
`Flight.CanRestart = false` stock does not build the Esc-menu "Revert Flight" button,
so the re-fly Retry choice is not offered. Merge / Discard stay reachable by LEAVING
THE FLIGHT (the scene-exit interceptor surfaces the Re-Fly merge dialog), and
`ReFlyRevertButtonGate.Apply` logs one Info line per evaluation while a re-fly is live
on such a game. The lanes that prove it (RF-16 merge, RF-17 discard) need the S4.1
host with the Hard flight flags set, and nothing else different.

THE DERIVATION. `gloops-airshow`'s persistent.sfs byte for byte, except four values
inside the top-level `PARAMETERS` node:

  PARAMETERS { preset = Normal -> Hard }
  PARAMETERS { FLIGHT { CanQuickLoad = True -> False,
                        CanRestart = True -> False,
                        CanLeaveToEditor = True -> False } }

These are exactly the three values KSP's own Hard preset turns off in the FLIGHT
section (GameParameters.FlightParams); the other Hard-preset differences (DIFFICULTY
section) are deliberately NOT applied, so a delta between S4.1 and RF-16 is
attributable to the flight flags alone. `preset = Hard` is a label: decompiled
`GameParameters.Load` parses it into the enum and then loads every section's values
from the save as written, so it re-applies nothing. The rewind-b9 injector builds its
RewindPoint quicksave sidecar from the staged save (ScenarioWriter.InjectIntoSaveFile
passes the un-injected input save as the donor root), so the RP quicksave the re-fly
loads carries the same PARAMETERS and the re-fly runs on a CanRestart=False game.

WHAT IS NOT COPIED. `Ships/VAB/` and `AddOns/` are left out: the lanes this fixture
hosts are seam-only and launch nothing through kRPC. `persistent.loadmeta` is copied
byte for byte (it carries no preset).

Run `python harness/tools/build_gloops_airshow_hard.py` to (re)write the fixture and
`--check` to re-derive and compare against the committed bytes;
`harness/lib/test_gloops_airshow_hard.py` runs the check in-process so a hand edit to
either fixture reds in the unit suite rather than in a flight.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gloops-airshow")
TARGET_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gloops-airshow-hard")
COPIED_FILES = ("persistent.sfs", "persistent.loadmeta")

# (path inside PARAMETERS, key, before, after). An empty section means the value
# sits directly on PARAMETERS.
EDITS = (
    ("", "preset", "Normal", "Hard"),
    ("FLIGHT", "CanQuickLoad", "True", "False"),
    ("FLIGHT", "CanRestart", "True", "False"),
    ("FLIGHT", "CanLeaveToEditor", "True", "False"),
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
        # Spans are recomputed per edit: True -> False shifts every later offset.
        p_start, p_end = _node_span(text, "PARAMETERS", "\t")
        if section:
            s_start, s_end = _node_span(text, section, "\t\t", p_start, p_end)
            text = _set_value(text, s_start, s_end, "\t\t\t", key, before, after)
        else:
            # Values directly on PARAMETERS sit at depth 2 before its first child node.
            first_child = text.find("\t\t{\n", p_start, p_end)
            text = _set_value(text, p_start, first_child if first_child >= 0 else p_end,
                              "\t\t", key, before, after)
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
        print("gloops-airshow-hard: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    os.makedirs(TARGET_DIR, exist_ok=True)
    for name, data in derive_all().items():
        with open(os.path.join(TARGET_DIR, name), "wb") as fh:
            fh.write(data)
        print("wrote %s (%d bytes)" % (os.path.join(TARGET_DIR, name), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
