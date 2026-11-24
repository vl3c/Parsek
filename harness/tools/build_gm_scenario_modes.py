#!/usr/bin/env python3
"""Build the `gm-scenario-modes` save fixture BY CONSTRUCTION (no KSP launch): GM-1's host.

WHY THIS EXISTS. Parsek is inert in every game mode other than SANDBOX, CAREER and
SCIENCE_SANDBOX (`ParsekGameModeGate`, owner ruling S9): a stock scenario save
(`Mode = SCENARIO`) and a stock training save (`Mode = SCENARIO_NON_RESUMABLE`) must
record nothing, run no ParsekScenario load or save logic, and keep whatever Parsek node a
save carries byte-for-byte. xUnit pins the gate's predicate and the inert pass-through
bodies headless; no lane ever loaded such a save in KSP, and no committed fixture has
either mode (every fixture is SANDBOX, CAREER or SCIENCE_SANDBOX).

WHAT IT BUILDS. Two saves in one folder, both derived from `b1-pad-craft` (a SANDBOX save
whose active vessel 1 is a PRELAUNCH pad craft, so the seam's LoadGame takes the FLIGHT
route):

  persistent.sfs   `Mode = SCENARIO`. The donor's `SCENARIO{name=ParsekScenario}` node is
                   kept VERBATIM (name, scene and two Parsek values, 4 values in all), the
                   shape a save Parsek already wrote has. Stock never adds the node to a
                   scenario game, but a file that carries it still instantiates the module,
                   so this is the save that drives ParsekScenario's inert OnLoad stash and
                   OnSave write-back: the lane pins `(4 value(s), 0 node(s))` on each load
                   and `(2 entries)` on each save, a mechanical round trip.
  training.sfs     `Mode = SCENARIO_NON_RESUMABLE`, the ParsekScenario node and the
                   `PARAMETERS > ParsekSettings` node removed: the shape a stock training
                   save has (no Parsek module is ever instantiated for it).

Both get their `Title` restamped. `persistent.loadmeta` and `AddOns/` are copied verbatim;
`Parsek/` is not (the donor has none, and an inert game reads none).

Stdlib only; ASCII only; no em dashes. `--check` verifies the committed copy and writes
nothing.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import Dict, List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import build_career_pad_craft as base_builder  # noqa: E402
import build_preparsek_fixtures as preparsek  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
set_top_value = base_builder.set_top_value

BASE = "b1-pad-craft"
TARGET = "gm-scenario-modes"
SCENARIO_FILE = "persistent.sfs"
TRAINING_FILE = "training.sfs"
SCENARIO_MODE = "SCENARIO"
TRAINING_MODE = "SCENARIO_NON_RESUMABLE"
SCENARIO_TITLE = "gm-scenario-modes (SCENARIO)"
TRAINING_TITLE = "gm-scenario-modes training (SCENARIO_NON_RESUMABLE)"
# The donor's ParsekScenario node, value for value: the lane's stash line counts these.
PARSEK_NODE_VALUES = ("name", "scene", "missionHideArchived", "gameStateEventCount")
VERBATIM_FILES: Tuple[str, ...] = ("persistent.loadmeta",)
VERBATIM_DIRS: Tuple[str, ...] = ("AddOns",)


def parsek_node_keys(lines: List[str]) -> Optional[List[str]]:
    """The direct value keys of the ParsekScenario node, in order, or None without one."""
    node = preparsek.find_named_scenario(lines, preparsek.PARSEK_SCENARIO_NAME)
    if node is None:
        return None
    start, end = node
    keys: List[str] = []
    depth = 0
    for i in range(start + 2, end - 1):
        s = lines[i].strip()
        if s == "{":
            depth += 1
        elif s == "}":
            depth -= 1
        elif depth == 0 and " = " in s:
            keys.append(s.split(" = ", 1)[0])
        elif depth == 0 and s and lines[i + 1].strip() == "{":
            keys.append(s + "{}")
    return keys


def build_scenario(base_lines: List[str]) -> List[str]:
    lines = list(base_lines)
    if not set_top_value(lines, "Mode", SCENARIO_MODE):
        raise SystemExit("base save has no GAME-level Mode line")
    if not set_top_value(lines, "Title", SCENARIO_TITLE):
        raise SystemExit("base save has no GAME-level Title line")
    return lines


def build_training(base_lines: List[str]) -> List[str]:
    lines = list(base_lines)
    node = preparsek.find_named_scenario(lines, preparsek.PARSEK_SCENARIO_NAME)
    if node is None:
        raise SystemExit("base save has no ParsekScenario node to remove")
    lines = lines[:node[0]] + lines[node[1]:]
    lines, had_settings = preparsek.strip_parsek_settings(lines)
    if not had_settings:
        raise SystemExit("base save has no ParsekSettings node to remove")
    if not set_top_value(lines, "Mode", TRAINING_MODE):
        raise SystemExit("base save has no GAME-level Mode line")
    if not set_top_value(lines, "Title", TRAINING_TITLE):
        raise SystemExit("base save has no GAME-level Title line")
    return lines


def build_all(base_lines: List[str]) -> Dict[str, List[str]]:
    return {SCENARIO_FILE: build_scenario(base_lines), TRAINING_FILE: build_training(base_lines)}


def _focusable(lines: List[str]) -> bool:
    active = [l.strip() for l in lines if l.strip().startswith("activeVessel = ")]
    vessels = sum(1 for l in lines if l.strip() == "VESSEL")
    return len(active) == 1 and active[0] != "activeVessel = -1" and vessels > 0


def verify(built: Dict[str, List[str]]) -> List[str]:
    """Structural problems in the built pair (empty when the shape GM-1 relies on holds)."""
    problems: List[str] = []
    scen, train = built[SCENARIO_FILE], built[TRAINING_FILE]
    if "\tMode = %s" % SCENARIO_MODE not in scen:
        problems.append("%s must be Mode = %s" % (SCENARIO_FILE, SCENARIO_MODE))
    if "\tMode = %s" % TRAINING_MODE not in train:
        problems.append("%s must be Mode = %s" % (TRAINING_FILE, TRAINING_MODE))
    keys = parsek_node_keys(scen)
    if keys != list(PARSEK_NODE_VALUES):
        problems.append("%s must carry the ParsekScenario node %r verbatim, got %r"
                        % (SCENARIO_FILE, PARSEK_NODE_VALUES, keys))
    if parsek_node_keys(train) is not None:
        problems.append("%s must carry no ParsekScenario node" % TRAINING_FILE)
    if find_node(train, preparsek.PARSEK_SETTINGS_NODE) is not None:
        problems.append("%s must carry no ParsekSettings node" % TRAINING_FILE)
    for name, lines in built.items():
        if not _focusable(lines):
            problems.append("%s must name a focusable active vessel (the FLIGHT route)" % name)
        if not all(l.isascii() for l in lines):
            problems.append("%s must be ASCII" % name)
    return problems


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def committed_problems() -> List[str]:
    base_lines = read_lines(os.path.join(SAVES, BASE, "persistent.sfs"))
    built = build_all(base_lines)
    target_dir = os.path.join(SAVES, TARGET)
    problems = list(verify(built))
    for name, lines in built.items():
        path = os.path.join(target_dir, name)
        if not os.path.isfile(path):
            problems.append("%s is not committed" % path)
        elif read_lines(path) != lines:
            problems.append("%s differs from a fresh build" % name)
    for leaf in VERBATIM_FILES:
        src, dst = os.path.join(SAVES, BASE, leaf), os.path.join(target_dir, leaf)
        if not os.path.isfile(dst) or _read_bytes(src) != _read_bytes(dst):
            problems.append("%s differs from the base's copy" % leaf)
    if os.path.isdir(os.path.join(target_dir, "Parsek")):
        problems.append("a Parsek/ folder must not be committed")
    expected = sorted(list(built) + list(VERBATIM_FILES) + list(VERBATIM_DIRS))
    found = sorted(os.listdir(target_dir)) if os.path.isdir(target_dir) else []
    if found != expected:
        problems.append("committed files %r, expected %r" % (found, expected))
    return problems


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="verify the committed fixture against a fresh build; write nothing")
    args = ap.parse_args(argv)
    if args.check:
        problems = committed_problems()
        for p in problems:
            print("  - %s" % p)
        print("OK" if not problems else "PROBLEMS")
        return 1 if problems else 0
    base_dir = os.path.join(SAVES, BASE)
    built = build_all(read_lines(os.path.join(base_dir, "persistent.sfs")))
    problems = verify(built)
    if problems:
        for p in problems:
            print("  - %s" % p)
        return 1
    target_dir = os.path.join(SAVES, TARGET)
    if os.path.isdir(target_dir):
        shutil.rmtree(target_dir)
    os.makedirs(target_dir)
    for name, lines in built.items():
        write_lines(os.path.join(target_dir, name), lines)
    for leaf in VERBATIM_FILES:
        shutil.copy2(os.path.join(base_dir, leaf), os.path.join(target_dir, leaf))
    for leaf in VERBATIM_DIRS:
        shutil.copytree(os.path.join(base_dir, leaf), os.path.join(target_dir, leaf))
    print("wrote %s" % target_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
