#!/usr/bin/env python3
"""Build the committed `srv-rotor-rover` fixture BY CONSTRUCTION (the SRV-2 host).

WHY THIS FIXTURE EXISTS. SRV-2 records a Breaking Ground rotor spinning up, holding its RPM
and spinning down, and replays it as a ghost away from the launch site: the recorder must read
the rotor's live rate of motion (todo RECORDER-ROTOR-RPM-PAW-ONLY: `currentRPM` is refreshed
only while the part menu is open) and keep a steady spin "moving", and the ghost must turn the
rotor and the part riding it. It is a SIBLING of SRV-1's `srv-hinge-rover`, built the same way
from the same base, because adding a rotor to that craft would change SRV-1's pinned follower
plan (servos=1 followers=1).

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/miner-rover-watched`
  (MINE-6's pair: V's real `Mining Rover RC` at index 0, active, LANDED 820 m from the runway
  threshold, its every clock at UT 30; the crewless `Miner Watcher` 120 m north at index 1;
  the inert ParsekScenario node; `AddOns/`; `persistent.loadmeta` vesselCount 2, UT 30). After
  Rewind-to-Launch strips the recorded rover, a reload of the rewound save lands FLIGHT on the
  watcher (vessel 0), inside the physics bubble of the replayed ghost (MINE-6, live-proven).
  RENAMED: the rover's vessel `name` becomes `SRV2 Rotor Rover`.
  ADDED to the rover, the `build_srv_hinge_rover.py` precedent: two parts stacked on the free
  top node of its `adapterSmallMiniShort` (part 26, the rover's front end along its driving
  axis, vessel-local +y):
    27. `rotor.01` (EM-16 Light Duty Rotor), `parent = 26`, by its BOTTOM node (rotor_01.cfg
        `node_stack_bottom` y 0): `position` y = the adapter's + its top node (0.1875). The
        adapter's `attN = top, -1` becomes `attN = top, 27`.
    28. `batteryBankMini` (Z-200, 200 EC), `parent = 27`, on the rotor's TOP node, which is on
        its MOVING side (rotor_01.cfg `servoAttachNodes = top,N2_1,...`): `position` y = the
        rotor's + 0.415 (`node_stack_top`) + 0.1 (z-200Battery.cfg `node_stack_bottom`).
  Both keep the adapter's own `rotation`: the rotor's spin axis (`mainAxis = Y`) is the stack
  axis, so the battery turns about its own axis, level with the rover, never into the ground.
  So the replay's follower plan must read servos=1 followers=1 invertedServos=0.
  THE ROTOR'S MODULE: one `ModuleRoboticServoRotor` node with the keys stock writes for a rotor
  (`Butterfly Rover.craft`'s, the stock Breaking Ground example) set to a free, motorized rotor
  at rest with its motor OFF: unlocked, `servoIsMotorized = True`, `servoMotorIsEngaged = False`
  (so it does not spin before the recording starts; SRV-2 engages it with PartAction rotoron,
  BaseServo's MotorOnAction), `rpmLimit` / `launchPosition` 30 (stock's ResetLaunchPosition
  restores `rpmLimit` from `launchPosition`), `servoMotorSize` 10 / `servoMotorLimit` 100 (the
  stock rover's), clockwise, not inverted, no ratchet, brakes 100 percent but not applied.
  Decompiled `ModuleRoboticServoRotor` (KSP 1.12.5): with the motor engaged, OnFixedUpdate ramps
  the joint's target angular velocity to `rpmLimit` at `rotorSpoolTime` (150 on the EM-16, about
  two physics frames to 30 RPM); disengaged, it drives the joint to zero (at `brakeTorque` while
  `lockPartOnPowerLoss`), and `IsMoving()` is `transformRateOfMotion > 0.5`.
  `servoTransformPosition`, `servoTransformRotation` and `jointParentRotation` are DELIBERATELY
  absent (the `srv-hinge-rover` rule: `BaseServo.OnLoad` applies each only when present; stock
  writes all three live in the recording's snapshot). The rotor's other modules
  (ModuleDynamicNodes, ModulePartVariants, ModuleResourceAutoShiftState, ModuleCargoPart) are
  not written: a ProtoPartSnapshot with fewer MODULE nodes keeps the prefab's own. The battery
  carries its RESOURCE node (200 of 200 ElectricCharge).
  HEADERS: each new PART's scalar header and EVENTS / ACTIONS / PARTDATA tail are the adapter's
  own lines with name, cid, uid, persistentId, parent, position, attN, mass and rTrf rewritten
  and the adapter's variant / cargo-stack keys dropped (`build_srv_hinge_rover` helpers). cid,
  uid and persistentId are derived (sha256 of `SEED` and a tag) and asserted unique in the save.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_srv_rotor_rover.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
OUT_DIR = os.path.join(SAVES, "srv-rotor-rover")

sys.path.insert(0, _HERE)
import build_srv_hinge_rover as hinge  # noqa: E402

landed = hinge.landed
variants = hinge.variants
watched = hinge.watched

BASE_DIR = hinge.BASE_DIR
LF = "\n"
EDITED = ("persistent.sfs",)
SEED = "srv-rotor-rover/v1"
TITLE = "srv-rotor-rover (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: a real rover with an EM-16 rotor "
               "carrying a Z-200 on its front, and a neighbouring rover).")

ROVER_BASE_NAME = hinge.ROVER_BASE_NAME
ROVER_NAME = "SRV2 Rotor Rover"
WATCHER_NAME = hinge.WATCHER_NAME
BASE_PART_COUNT = hinge.BASE_PART_COUNT
ADAPTER_INDEX = hinge.ADAPTER_INDEX
ADAPTER_NAME = hinge.ADAPTER_NAME
ADAPTER_TOP_NODE = hinge.ADAPTER_TOP_NODE
ROTOR_INDEX = 27
BATTERY_INDEX = 28
ROTOR = "rotor.01"
BATTERY = hinge.BATTERY
# rotor_01.cfg: node_stack_bottom y 0, node_stack_top y 0.415.
ROTOR_BOTTOM = 0.0
ROTOR_TOP = 0.415
BATTERY_HALF = hinge.BATTERY_HALF
ROTOR_MASS = "0.0199999996"
BATTERY_MASS = hinge.BATTERY_MASS
ROTOR_RPM = "30"

P4 = hinge.P4
P5 = hinge.P5

ROTOR_MODULE_VALUES = (
    ("name", "ModuleRoboticServoRotor"),
    ("isEnabled", "True"),
    ("rpmLimit", ROTOR_RPM),
    ("rotateCounterClockwise", "False"),
    ("inverted", "False"),
    ("ratcheted", "0"),
    ("brakePercentage", "100"),
    ("servoIsLocked", "False"),
    ("servoIsMotorized", "True"),
    ("servoMotorIsEngaged", "False"),
    ("launchPosition", ROTOR_RPM),
    ("servoMotorSize", "10"),
    ("servoMotorLimit", "100"),
    ("lockPartOnPowerLoss", "True"),
    ("useMultipleDragCubes", "False"),
    ("stagingEnabled", "True"),
)


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _rotor_module() -> List[str]:
    body = [P5 + k + " = " + v for k, v in ROTOR_MODULE_VALUES]
    for child in ("EVENTS", "ACTIONS", "UPGRADESAPPLIED"):
        body += hinge._block(P5, child, [])
    return hinge._block(P4, "MODULE", body)


def build_part(adapter: List[str], tag: str, name: str, parent: int, y: float,
               att_n: List[str], mass: str, extra: List[str],
               used_pids: set, used_uids: set) -> List[str]:
    part = hinge._adapter_header_and_tail(adapter)
    pid = _derive_uint(tag + "|persistentId")
    uid = _derive_uint(tag + "|uid")
    assert str(pid) not in used_pids and str(uid) not in used_uids, "%s id collision" % tag
    used_pids.add(str(pid))
    used_uids.add(str(uid))
    pos = hinge._get_part(adapter, "position").split(",")
    for key, value in (
            ("name", name),
            ("cid", str(_derive_uint(tag + "|cid"))),
            ("uid", str(uid)),
            ("persistentId", str(pid)),
            ("parent", str(parent)),
            ("position", ",".join((pos[0], repr(y), pos[2]))),
            ("mass", mass),
            ("rTrf", name)):
        hinge._set_part(part, key, value)
    attn = [i for i, l in enumerate(part) if hinge._ATTN_RE.match(l)]
    assert len(attn) == 2, "adapter header carries %d attN lines" % len(attn)
    part[attn[0]:attn[1] + 1] = [P4 + "attN = " + a for a in att_n]
    # MODULE / RESOURCE nodes go after PARTDATA, before the PART's closing brace.
    part[-1:-1] = extra
    return part


def add_rotor_and_battery(rover: List[str], used_pids: set, used_uids: set) -> List[str]:
    blocks = variants.part_blocks(rover)
    assert len(blocks) == BASE_PART_COUNT, "base rover has %d parts" % len(blocks)
    a_start, a_end = blocks[ADAPTER_INDEX]
    adapter = rover[a_start:a_end]
    assert adapter[2] == P4 + "name = " + ADAPTER_NAME, adapter[2]
    top = [i for i, l in enumerate(adapter) if l == P4 + "attN = top, -1"]
    assert len(top) == 1, "the adapter's top node must be free"
    adapter_y = float(hinge._get_part(adapter, "position").split(",")[1])
    rotor_y = adapter_y + ADAPTER_TOP_NODE - ROTOR_BOTTOM
    battery_y = rotor_y + ROTOR_TOP + BATTERY_HALF
    rotor = build_part(adapter, "rotor", ROTOR, ADAPTER_INDEX, rotor_y,
                       ["top, %d" % BATTERY_INDEX, "bottom, %d" % ADAPTER_INDEX],
                       ROTOR_MASS, _rotor_module(), used_pids, used_uids)
    battery = build_part(adapter, "battery", BATTERY, ROTOR_INDEX, battery_y,
                         ["top, -1", "bottom, %d" % ROTOR_INDEX],
                         BATTERY_MASS, hinge._battery_resource(), used_pids, used_uids)
    out = list(rover)
    out[a_start + top[0]] = P4 + "attN = top, %d" % ROTOR_INDEX
    end = blocks[-1][1]
    out[end:end] = rotor + battery
    assert len(variants.part_blocks(out)) == BASE_PART_COUNT + 2
    return out


def build_sfs() -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-watched is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [ROVER_BASE_NAME, WATCHER_NAME], [b[2] for b in blocks]
    r_start, r_end, _ = blocks[0]
    used_pids = hinge._ids(lines, "persistentId")
    used_uids = hinge._ids(lines, "uid")
    rover = add_rotor_and_battery(lines[r_start:r_end], used_pids, used_uids)
    landed._set_top(rover, "name", ROVER_NAME)
    lines[r_start:r_end] = rover
    assert "\t\tactiveVessel = 0" in lines, "the rover stays the active vessel"
    title = lines.index("\tTitle = " + watched.TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + watched.DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    return LF.join(lines)


def rover_part_ids() -> Dict[str, int]:
    """The ids a lane names: the rover's and the watcher's vessel pids and the rotor's."""
    lines = build_sfs().split(LF)
    blocks = landed.vessel_blocks(lines)
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = variants.part_blocks(rover)
    rotor = rover[parts[ROTOR_INDEX][0]:parts[ROTOR_INDEX][1]]

    def vessel_pid(block: List[str]) -> int:
        return int([l for l in block if l.startswith("\t\t\tpersistentId = ")][0].split(" = ")[1])
    return {"roverPersistentId": vessel_pid(rover),
            "watcherPersistentId": vessel_pid(lines[blocks[1][0]:blocks[1][1]]),
            "rotorPersistentId": int(hinge._get_part(rotor, "persistentId"))}


def verify(sfs: str) -> List[str]:
    """Structural problems in a built save (empty when the shape SRV-2 relies on holds)."""
    problems: List[str] = []
    lines = sfs.split(LF)
    blocks = landed.vessel_blocks(lines)
    names = [b[2] for b in blocks]
    if names != [ROVER_NAME, WATCHER_NAME]:
        problems.append("vessels must be [%s, %s], got %r" % (ROVER_NAME, WATCHER_NAME, names))
        return problems
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = variants.part_blocks(rover)
    if len(parts) != BASE_PART_COUNT + 2:
        problems.append("the rover must carry %d parts, got %d" % (BASE_PART_COUNT + 2, len(parts)))
        return problems
    adapter = rover[parts[ADAPTER_INDEX][0]:parts[ADAPTER_INDEX][1]]
    rotor = rover[parts[ROTOR_INDEX][0]:parts[ROTOR_INDEX][1]]
    battery = rover[parts[BATTERY_INDEX][0]:parts[BATTERY_INDEX][1]]
    if P4 + "attN = top, %d" % ROTOR_INDEX not in adapter:
        problems.append("the adapter's top node must carry the rotor")
    if P4 + "name = " + ROTOR not in rotor or P4 + "parent = %d" % ADAPTER_INDEX not in rotor:
        problems.append("part %d must be the rotor, child of the adapter" % ROTOR_INDEX)
    # The battery rides the rotor's top node, on its MOVING side; the adapter is on the base
    # (bottom) node. Swap them and the replay's follower plan inverts.
    if P4 + "attN = top, %d" % BATTERY_INDEX not in rotor:
        problems.append("the battery must sit on the rotor's top (moving) node")
    if P4 + "attN = bottom, %d" % ADAPTER_INDEX not in rotor:
        problems.append("the adapter must sit on the rotor's bottom (base) node")
    if P4 + "name = " + BATTERY not in battery or P4 + "parent = %d" % ROTOR_INDEX not in battery:
        problems.append("part %d must be the battery, child of the rotor" % BATTERY_INDEX)
    if P4 + "attN = bottom, %d" % ROTOR_INDEX not in battery:
        problems.append("the battery's bottom node must be on the rotor")
    if P5 + "name = ModuleRoboticServoRotor" not in rotor:
        problems.append("the rotor must carry its ModuleRoboticServoRotor node")
    # The motor must load OFF (or the rotor spins before the recording starts) on a free,
    # motorized rotor PartAction rotoron can engage.
    for key, want in (("servoIsLocked", "False"), ("servoMotorIsEngaged", "False"),
                      ("servoIsMotorized", "True"), ("rpmLimit", ROTOR_RPM),
                      ("launchPosition", ROTOR_RPM), ("servoMotorLimit", "100")):
        if P5 + key + " = " + want not in rotor:
            problems.append("the rotor must load with %s = %s" % (key, want))
    for key in ("servoTransformPosition", "servoTransformRotation", "jointParentRotation"):
        if any(l.startswith(P5 + key + " = ") for l in rotor):
            problems.append("the rotor must not pin %s (stock writes it live)" % key)
    if P5 + "amount = " + hinge.BATTERY_EC not in battery:
        problems.append("the battery must hold its charge")
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
