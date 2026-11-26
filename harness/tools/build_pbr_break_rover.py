#!/usr/bin/env python3
"""Build the committed `pbr-break-rover` fixture BY CONSTRUCTION (the PBR-1 host).

WHY THIS FIXTURE EXISTS. Test-gap batch 4 (todo TEST-GAPS-B4-PART-BREAKS): a solar panel
breaking and a wheel breaking had only synthetic coverage (H37's in-game cells build their
ghosts from PartLoader prefabs; SyntheticRecordingTests writes the events by hand), so the
recorder's own break edges (`CheckDeployableBrokenTransition`, `CheckWheelDamageTransition`)
had never been read off a real part, and the ghost appliers never off a recorded event. PBR-1
breaks one panel and one wheel on a parked rover while recording, then replays it. The rover
is SRV-1's: a vessel that never leaves the launch site has its ghost hidden by design
(`LaunchSiteOnlyGhost`), and this one is parked 820 m from the runway threshold with a crewless
watcher 120 m north to land FLIGHT on after the rewind (MINE-6, live-proven).

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/miner-rover-watched`
  (V's real `Mining Rover RC` at index 0, active, LANDED, its every clock at UT 30; the
  crewless `Miner Watcher` 120 m north at index 1; the inert ParsekScenario node; `AddOns/`;
  `persistent.loadmeta`).
  RENAMED: the rover's vessel `name` becomes `PBR1 Break Rover`, so PBR-1's log lines never read
  as a mining or servo lane's.
  REPLACED: the rover carries no solar panel, so one of its four RTGs (part 12, `rtg`, surface
  attached to the `fuelTankSmall` at part 7, on top of the rover) becomes a `solarPanels2`
  (a deployable, breakable panel; the one the committed `Duna Rocket.craft` and the outer planet
  fixtures carry). The PART keeps the RTG's place (parent, position, rotation, `srfN`), its
  cid / uid / persistentId and its header; the RTG's mirror link is cut on both sides (its own
  `sym = 13` line and part 13's `sym = 12`), its two stack `attN` lines are dropped (the panel
  has only a surface node), `name` / `rTrf` / `mass` / `moduleCargoStackableQuantity` become the
  panel's, and its three MODULE nodes (ModuleGenerator, ModuleCoreHeat, ModuleCargoPart) become
  the panel's two (ModuleDeployableSolarPanel, ModuleCargoPart), the `b18-dres-pad` panel's keys
  with `deployState = RETRACTED`: a retracted panel cannot meet the ground or a wheel on load,
  and stock's `breakPanels()` breaks it all the same (it reads no deploy state).
  UNTOUCHED: the four `roverWheel1` wheels (each a ModuleWheelDamage); PBR-1 breaks part 22 by
  its persistentId.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_pbr_break_rover.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "miner-rover-watched")
OUT_DIR = os.path.join(SAVES, "pbr-break-rover")

sys.path.insert(0, _HERE)
import build_miner_rover_landed as landed  # noqa: E402
import build_miner_rover_variants as variants  # noqa: E402
import build_miner_rover_watched as watched  # noqa: E402

LF = "\n"
EDITED = ("persistent.sfs",)
TITLE = "pbr-break-rover (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: a real rover with one RTG swapped "
               "for a deployable solar panel, and a neighbouring rover).")

ROVER_BASE_NAME = landed.ROVER_NAME
ROVER_NAME = "PBR1 Break Rover"
WATCHER_NAME = watched.WATCHER_NAME
PART_COUNT = 27
PANEL_INDEX = 12
PANEL_MIRROR_INDEX = 13
RTG = "rtg"
PANEL = "solarPanels2"
PANEL_MASS = "0.0250000004"
PANEL_STACK_QUANTITY = "1"
# The wheel PBR-1 breaks (part 22, on the probe core's side) and its persistentId.
WHEEL = "roverWheel1"
WHEEL_INDEX = 22

P4 = "\t\t\t\t"
P5 = "\t\t\t\t\t"

PANEL_MODULE_VALUES = (
    ("name", "ModuleDeployableSolarPanel"),
    ("isEnabled", "True"),
    ("efficiencyMult", "1"),
    ("launchUT", "30"),
    ("currentRotation", "(-0.707106829, 0, 0, 0.707106829)"),
    ("storedAnimationTime", "0"),
    ("storedAnimationSpeed", "0"),
    ("deployState", "RETRACTED"),
    ("stagingEnabled", "True"),
)
PANEL_ACTIONS = ("ExtendPanelsAction", "ExtendAction", "RetractAction")
CARGO_MODULE_VALUES = (
    ("name", "ModuleCargoPart"),
    ("isEnabled", "True"),
    ("packedVolume", "70"),
    ("beingAttached", "False"),
    ("beingSettled", "False"),
    ("reinitResourcesOnStoreInVessel", "False"),
    ("stagingEnabled", "True"),
)


def _part_key(part: List[str], key: str) -> int:
    prefix = P4 + key + " = "
    hits = [i for i, l in enumerate(part) if l.startswith(prefix)]
    assert len(hits) == 1, "part key %r found %d times" % (key, len(hits))
    return hits[0]


def _get_part(part: List[str], key: str) -> str:
    return part[_part_key(part, key)].split(" = ", 1)[1]


def _set_part(part: List[str], key: str, value: str) -> None:
    part[_part_key(part, key)] = P4 + key + " = " + value


def _block(indent: str, name: str, body: List[str]) -> List[str]:
    return [indent + name, indent + "{"] + body + [indent + "}"]


def _action(name: str) -> List[str]:
    p6 = P5 + "\t"
    return _block(p6, name, [p6 + "\tactionGroup = None", p6 + "\tactive = False",
                             p6 + "\twasActiveBeforePartWasAdjusted = False"])


def _module(values, actions=()) -> List[str]:
    body = [P5 + k + " = " + v for k, v in values]
    body += _block(P5, "EVENTS", [])
    acts: List[str] = []
    for a in actions:
        acts += _action(a)
    body += _block(P5, "ACTIONS", acts)
    body += _block(P5, "UPGRADESAPPLIED", [])
    return _block(P4, "MODULE", body)


def rtg_to_panel(rtg: List[str]) -> List[str]:
    """The RTG's PART block rewritten as the panel (see the module docstring)."""
    assert rtg[2] == P4 + "name = " + RTG, rtg[2]
    first_module = rtg.index(P4 + "MODULE")
    head = [l for l in rtg[:first_module]
            if not l.startswith(P4 + "attN = ") and not l.startswith(P4 + "sym = ")]
    part = head + _module(PANEL_MODULE_VALUES, PANEL_ACTIONS) + _module(CARGO_MODULE_VALUES) + ["\t\t\t}"]
    for key, value in (("name", PANEL), ("rTrf", PANEL), ("mass", PANEL_MASS),
                       ("moduleCargoStackableQuantity", PANEL_STACK_QUANTITY)):
        _set_part(part, key, value)
    return part


def swap_rtg_for_panel(rover: List[str]) -> List[str]:
    blocks = variants.part_blocks(rover)
    assert len(blocks) == PART_COUNT, "base rover has %d parts" % len(blocks)
    out = list(rover)
    m_start, m_end = blocks[PANEL_MIRROR_INDEX]
    mirror = out[m_start:m_end]
    assert mirror[2] == P4 + "name = " + RTG, mirror[2]
    link = mirror.index(P4 + "sym = %d" % PANEL_INDEX)
    del mirror[link]
    out[m_start:m_end] = mirror
    p_start, p_end = blocks[PANEL_INDEX]
    out[p_start:p_end] = rtg_to_panel(rover[p_start:p_end])
    assert len(variants.part_blocks(out)) == PART_COUNT
    return out


def build_sfs() -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-watched is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [ROVER_BASE_NAME, WATCHER_NAME], [b[2] for b in blocks]
    r_start, r_end, _ = blocks[0]
    rover = swap_rtg_for_panel(lines[r_start:r_end])
    landed._set_top(rover, "name", ROVER_NAME)
    lines[r_start:r_end] = rover
    assert "\t\tactiveVessel = 0" in lines, "the rover stays the active vessel"
    title = lines.index("\tTitle = " + watched.TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + watched.DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    return LF.join(lines)


def rover_part_ids() -> Dict[str, int]:
    """The ids PBR-1 names: the panel's and the broken wheel's persistentIds."""
    lines = build_sfs().split(LF)
    blocks = landed.vessel_blocks(lines)
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = variants.part_blocks(rover)
    panel = rover[parts[PANEL_INDEX][0]:parts[PANEL_INDEX][1]]
    wheel = rover[parts[WHEEL_INDEX][0]:parts[WHEEL_INDEX][1]]
    return {"panelPersistentId": int(_get_part(panel, "persistentId")),
            "wheelPersistentId": int(_get_part(wheel, "persistentId"))}


def verify(sfs: str) -> List[str]:
    """Structural problems in a built save (empty when the shape PBR-1 relies on holds)."""
    problems: List[str] = []
    lines = sfs.split(LF)
    blocks = landed.vessel_blocks(lines)
    names = [b[2] for b in blocks]
    if names != [ROVER_NAME, WATCHER_NAME]:
        problems.append("vessels must be [%s, %s], got %r" % (ROVER_NAME, WATCHER_NAME, names))
        return problems
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = variants.part_blocks(rover)
    if len(parts) != PART_COUNT:
        problems.append("the rover must carry %d parts, got %d" % (PART_COUNT, len(parts)))
        return problems
    names_by_index = [rover[s:e][2] for s, e in parts]
    panels = [i for i, n in enumerate(names_by_index) if n == P4 + "name = " + PANEL]
    if panels != [PANEL_INDEX]:
        problems.append("exactly one %s, at part %d, got %r" % (PANEL, PANEL_INDEX, panels))
        return problems
    panel = rover[parts[PANEL_INDEX][0]:parts[PANEL_INDEX][1]]
    if P5 + "name = ModuleDeployableSolarPanel" not in panel:
        problems.append("the panel must carry its ModuleDeployableSolarPanel node")
    if P5 + "deployState = RETRACTED" not in panel:
        problems.append("the panel must load retracted (deployState = RETRACTED)")
    if any(l.startswith(P5 + "name = ModuleGenerator") or l.startswith(P5 + "name = ModuleCoreHeat")
           for l in panel):
        problems.append("the panel must not keep the RTG's modules")
    if any(l.startswith(P4 + "sym = ") or l.startswith(P4 + "attN = ") for l in panel):
        problems.append("the panel must carry no mirror link and no stack node")
    if P4 + "srfN = srfAttach, 7,Cyl12" not in panel:
        problems.append("the panel must stay surface attached to the fuel tank (part 7)")
    mirror = rover[parts[PANEL_MIRROR_INDEX][0]:parts[PANEL_MIRROR_INDEX][1]]
    if any(l == P4 + "sym = %d" % PANEL_INDEX for l in mirror):
        problems.append("the RTG at part %d must not point its mirror link at the panel"
                        % PANEL_MIRROR_INDEX)
    wheel = rover[parts[WHEEL_INDEX][0]:parts[WHEEL_INDEX][1]]
    if wheel[2] != P4 + "name = " + WHEEL:
        problems.append("part %d must be the wheel PBR-1 breaks" % WHEEL_INDEX)
    if not sfs.isascii():
        problems.append("the save must be ASCII")
    return problems


def expected_files() -> Dict[str, bytes]:
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = landed._read_bytes(path)
    files["persistent.sfs"] = build_sfs().encode("utf-8")
    return files


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    if os.path.isdir(OUT_DIR):
        for root, _dirs, names in os.walk(OUT_DIR):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, OUT_DIR).replace(os.sep, "/")] = \
                    landed._read_bytes(path)
    problems = ["structure: " + p for p in verify(want["persistent.sfs"].decode("utf-8"))]
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
    files = expected_files()
    problems = verify(files["persistent.sfs"].decode("utf-8"))
    if problems:
        for p in problems:
            print("PROBLEM: " + p)
        return 1
    if os.path.isdir(OUT_DIR):
        shutil.rmtree(OUT_DIR)
    for rel, data in files.items():
        path = os.path.join(OUT_DIR, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    ids = rover_part_ids()
    print("wrote " + OUT_DIR + " " + " ".join("%s=%d" % kv for kv in sorted(ids.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
