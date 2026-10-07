#!/usr/bin/env python3
"""Build the `career-gs1-two-stage-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. QL-2b flies QL-4's in-flight F9 shape (GS-1's two-stage hop: the
crewed pod lands, the probe-cored booster is still under its parachutes) in a CAREER,
so a reward earned in the abandoned future (the first EVA on Kerbin's surface pays
funds) is a real career earning whose fate after the F9 the ledger and KSP's funds
pool both show. `gs1-two-stage-pad` is SANDBOX and no committed career save carries the
GS-1 craft (QL-2's header names the gap).

THE RECIPE IS `career-pad-craft`'s, WITH A DIFFERENT DONOR. `build_career_pad_craft.py`
already splices a sandbox pad craft into `fresh-career`; this tool runs its `build`
with `gs1-two-stage-pad` as the donor and Valentina Kerman (the pod's crew) as the
kerbal. Exactly three edits against the `fresh-career` save, plus a loadmeta restamp:

  1. `fresh-career`'s empty `FLIGHTSTATE` is replaced by `gs1-two-stage-pad`'s, with
     every non-`Ship` `VESSEL` dropped (the donor's `Ast. VPA-167` asteroid, which
     `fresh-career`'s `DiscoverableObjects` never registered) and `activeVessel`
     re-indexed onto the surviving `GS1 Auto-Chute Booster` (1 -> 0).
  2. The `ROSTER` row for `Valentina Kerman` is replaced by the donor's, the
     `state = Assigned` row KSP itself wrote alongside this exact vessel.
  3. The donor's inert `SCENARIO{name=ParsekScenario}` node (scene,
     missionHideArchived, gameStateEventCount) is copied in: without it the seam's
     FLIGHT focus route never creates the ScenarioModule (CL-1 flight 1).

THE CRAFT IS COPIED VERBATIM: same `pid`, same `persistentId` (2200110033), same parts
(mk1pod.v2 + parachuteSingle + Decoupler.1 over probeCoreOcto2.v2 + fuelTankSmall +
liquidEngine2 + 6x parachuteRadial + 3x basicFin), same staging and chute settings, so
GS-1's measured flight profile and `gs1_auto_chute_booster`'s parameters transfer. A
persisted VESSEL node loads regardless of the career's tech unlocks, and the lane never
launches from the editor, so the parts outside the `start` node are not a gate.

EVERY CAREER SURFACE COMES FROM `fresh-career` UNCHANGED: Mode CAREER, Funding 500000,
ResearchAndDevelopment 100, Reputation 0, all facilities at level 0 (EVA is still
possible: stock allows it on Kerbin's surface below Astronaut Complex level 1, and the
seam's EvaExit calls FlightEVA.spawnEVA directly), the 4-kerbal roster, the DIFFICULTY
block, and an EMPTY ProgressTracking tree. The empty tree is the lane's premise: the
Kerbin SurfaceEVA milestone has not been earned, so the first EVA pays it.

NOT COPIED: the donor's `Ships/` (the lane flies the craft already on the pad).
`AddOns/` comes from the base, as for `career-pad-craft`.

Usage:
    python harness/tools/build_career_gs1_two_stage_pad.py            # write the fixture
    python harness/tools/build_career_gs1_two_stage_pad.py --check    # re-derive, compare

`--check` rebuilds from the committed inputs and compares the bytes with the committed
fixture; `harness/lib/test_career_gs1_two_stage_pad.py` runs it in-process, so a change
to `fresh-career`, `gs1-two-stage-pad` or the shared recipe reds in the suite instead
of drifting into a flight.

Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import filecmp
import os
import shutil
import sys
from typing import Dict, List, Optional

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import build_career_pad_craft as _recipe  # noqa: E402

_SAVES = os.path.join(os.path.dirname(_HERE), "fixtures", "saves")

BASE_NAME = "fresh-career"
DONOR_NAME = "gs1-two-stage-pad"
TARGET_NAME = "career-gs1-two-stage-pad"
CREW_NAME = "Valentina Kerman"
VESSEL_NAME = "GS1 Auto-Chute Booster"
VESSEL_PERSISTENT_ID = "2200110033"
TITLE = "%s (CAREER)" % TARGET_NAME
WRITTEN_FILES = ("persistent.sfs", "persistent.loadmeta")


def _path(*parts: str) -> str:
    return os.path.join(_SAVES, *parts)


def _vessel_named(lines: List[str], name: str):
    fs = _recipe.find_node(lines, "FLIGHTSTATE")
    if fs is None:
        return None
    for vessel in _recipe.child_nodes(lines, fs, "VESSEL"):
        if _recipe.get_value(lines, vessel, "name") == name:
            return vessel
    return None


def build_all() -> Dict[str, List[str]]:
    """The fixture's two text files, as line lists. Pure over the committed inputs."""
    base_lines = _recipe.read_lines(_path(BASE_NAME, "persistent.sfs"))
    donor_lines = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
    built = _recipe.build(base_lines, donor_lines, CREW_NAME, TITLE)
    meta = _recipe.build_loadmeta(
        _recipe.read_lines(_path(BASE_NAME, "persistent.loadmeta")), built)
    return {"persistent.sfs": built, "persistent.loadmeta": meta}


def verify(lines: List[str]) -> List[str]:
    """Post-conditions on the built (or committed) save; empty = every one holds."""
    problems = list(_recipe.verify(lines, CREW_NAME))

    vessel = _vessel_named(lines, VESSEL_NAME)
    if vessel is None:
        problems.append("no VESSEL named %r" % VESSEL_NAME)
    else:
        if _recipe.get_value(lines, vessel, "persistentId") != VESSEL_PERSISTENT_ID:
            problems.append("vessel persistentId is %r, expected %r"
                            % (_recipe.get_value(lines, vessel, "persistentId"),
                               VESSEL_PERSISTENT_ID))
        donor = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
        donor_vessel = _vessel_named(donor, VESSEL_NAME)
        if donor_vessel is None or (lines[vessel[0]:vessel[1]]
                                    != donor[donor_vessel[0]:donor_vessel[1]]):
            problems.append("the vessel is not the donor's VESSEL node byte for byte")

    # The lane's premise: the career has earned no Kerbin progress yet, so the first
    # EVA on Kerbin's surface completes (and pays) KSPAchievements.SurfaceEVA.
    progress = _scenario_child(lines, "ProgressTracking", "Progress")
    if progress is None:
        problems.append("no SCENARIO ProgressTracking / Progress node")
    elif progress[1] - progress[0] != 3:
        problems.append("ProgressTracking.Progress is not empty (%d lines)"
                        % (progress[1] - progress[0]))
    return problems


def _scenario_child(lines: List[str], scenario_name: str, child: str):
    i = 0
    while True:
        node = _recipe.find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if _recipe.get_value(lines, node, "name") == scenario_name:
            found = _recipe.child_nodes(lines, node, child)
            return found[0] if found else None
        i = node[1]


def _encoded(lines: List[str]) -> bytes:
    return "\r\n".join(lines).encode("utf-8")


def check() -> List[str]:
    """Differences between a fresh build and the committed fixture (empty = OK)."""
    target = _path(TARGET_NAME)
    problems: List[str] = []
    built = build_all()
    for name in WRITTEN_FILES:
        path = os.path.join(target, name)
        if not os.path.isfile(path):
            problems.append("missing %s" % path)
            continue
        with open(path, "rb") as fh:
            if fh.read() != _encoded(built[name]):
                problems.append("%s differs from a fresh build" % path)
    cmp = filecmp.dircmp(_path(BASE_NAME, "AddOns"), os.path.join(target, "AddOns"))
    if not _same_tree(cmp):
        problems.append("AddOns/ differs from %s's" % BASE_NAME)
    expected = sorted(WRITTEN_FILES + ("AddOns",))
    actual = sorted(os.listdir(target)) if os.path.isdir(target) else []
    if actual != expected:
        problems.append("fixture entries are %r, expected %r" % (actual, expected))
    problems += verify(built["persistent.sfs"])
    return problems


def _same_tree(cmp: filecmp.dircmp) -> bool:
    if cmp.left_only or cmp.right_only or cmp.funny_files:
        return False
    _, mismatch, errors = filecmp.cmpfiles(cmp.left, cmp.right, cmp.common_files, shallow=False)
    if mismatch or errors:
        return False
    return all(_same_tree(sub) for sub in cmp.subdirs.values())


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture")
    args = parser.parse_args(argv)

    if args.check:
        problems = check()
        for p in problems:
            print("FAIL: %s" % p)
        print("%s: %s" % (TARGET_NAME, "OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0

    built = build_all()
    problems = verify(built["persistent.sfs"])
    if problems:
        for p in problems:
            print("FAIL: %s" % p)
        return 1
    target = _path(TARGET_NAME)
    os.makedirs(target, exist_ok=True)
    for name in WRITTEN_FILES:
        _recipe.write_lines(os.path.join(target, name), built[name])
    shutil.rmtree(os.path.join(target, "AddOns"), ignore_errors=True)
    shutil.copytree(_path(BASE_NAME, "AddOns"), os.path.join(target, "AddOns"))
    print("OK: wrote %s (base=%s donor=%s crew=%s)" % (target, BASE_NAME, DONOR_NAME, CREW_NAME))
    return 0


if __name__ == "__main__":
    sys.exit(main())
