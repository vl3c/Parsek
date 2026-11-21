#!/usr/bin/env python3
"""Build the committed `srv-mirror-hinge-rover` fixture BY CONSTRUCTION (the SRV-3 host).

WHY THIS FIXTURE EXISTS. Todo GHOST-HINGE-POSE-MODEL-INITIAL-ANGLE's two ghost fixes need a
live recording: (1) the mirrored half of a symmetry pair (`mirrorRotation = True`) spawned its
ghost turned the wrong way until its first recorded event, because the snapshot baseline read
the raw `targetAngle` while stock poses `-targetAngle` (`GhostVisualBuilder.
ResolveSnapshotDisplayedServoAngle`, Verbose `Servo snapshot pose mirrored for ...`); (2) an
offset hinge (`modelInitialAngle = 90`) was posed 90 deg off (`Servo pose offset for ...`).
SRV-3 records a mirrored G-11 pair swinging and replays it, with an offset Alligator hinge
riding along. It is a SIBLING of SRV-1's `srv-hinge-rover` and SRV-2's `srv-rotor-rover`,
built the same way from the same base, so neither of their pinned follower plans moves.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/miner-rover-watched`
  (MINE-6's pair: V's real `Mining Rover RC` at index 0, active, LANDED 820 m from the runway
  threshold, its every clock at UT 30; the crewless `Miner Watcher` 120 m north at index 1;
  the inert ParsekScenario node; `AddOns/`; `persistent.loadmeta` vesselCount 2, UT 30).
  RENAMED: the rover's vessel `name` becomes `SRV3 Mirror Hinge Rover`.
  ADDED to the rover (vessel-local +y forward, +x right, +z down), seven parts on the free top
  node of its front `adapterSmallMiniShort` (part 26):
    27. `structuralMiniNode` (six stack nodes at 0.1823258 m), `parent = 26`, by its BOTTOM
        node; identity rotation, so its right / left / front / top nodes face +x / -x / up /
        forward. attN in cfg order: right 28, left 30, back -1, front -1, top 32, bottom 26.
    28. `hinge.01` (G-11), the RIGHT half, `parent = 27`, BOTTOM node on the micronode's
        right node: local +y -> +x (outward), local X (the pivot, `mainAxis = X`) -> +y
        (forward), local z -> -z. `mirrorRotation = False`, `sym = 30`.
    29. `batteryBankMini` (Z-200) on 28's TOP node (its moving side, `servoAttachNodes =
        top`), the hinge's rotation, `sym = 31`.
    30. `hinge.01`, the LEFT half, its geometric mirror across x = 0: local +y -> -x, local
        X -> +y, local z -> +z. `mirrorRotation = True`, `sym = 28`.
    31. `batteryBankMini` on 30's top node, `sym = 29`.
    32. `hinge.03` (G-12L Alligator, `modelInitialAngle = 90`), `parent = 27`, its BOTTOM node
        (hinge_03.cfg `0.218749, 0.937497, 0`, facing local +x) on the micronode's TOP node:
        local +x -> -y, local +y -> +z, local z (the pivot, `mainAxis` default Z) -> -x, so its
        base arm rises from the micronode and its moving arm points forward, clear of the
        pair. LOCKED at its rest `targetAngle = 90` (JointTargetAngle 0): it never moves; it
        is on the ghost so the build logs its pose offset and the in-game cell can read its
        spawn pose against stock's.
    33. `batteryBankMini` on 32's TOP node (hinge_03.cfg `-0.937497, -0.218749, 0`, facing
        local -y -> up), turned so its own +y points up.
  WHY THE MIRRORED HALF IS A G-11 AND NOT AN ALLIGATOR. Stock poses a hinge at
  `JointTargetAngle = (mirrorRotation ? -targetAngle : targetAngle) - modelInitialAngle`
  (decompiled ModuleRoboticServoHinge, KSP 1.12.5) and limits the joint at
  `+|softMin - modelInitialAngle|` / `-(softMax - modelInitialAngle)`, i.e. +/-90 deg about
  the rest for every stock hinge, mirrored or not. A mirrored Alligator's JointTargetAngle is
  `-targetAngle - 90`, outside that range for every target above 0, so a file-constructed
  mirrored offset hinge cannot hold a posed angle; the G-11's symmetric range (-90, 90) can.
  The pair's soft limits stay SYMMETRIC (the cfg's -90, 90) for the same reason: the joint
  limits ignore `mirrorRotation`, so an asymmetric range would pinch one half.
  WHY `mirrorRotation = True` IS STOCK'S OWN ANSWER FOR THIS PLACEMENT. Stock's CheckSymmetry
  reflects the counterpart's pivot axis across the plane between the two parts (here x = 0)
  and sets `mirrorRotation = !counterpart's` when the reflection points along its own pivot.
  Both pivots are +y, which the reflection keeps, so the left half reads True. Physically: a
  turn of +a about +y, mirrored across x = 0, is a turn of -a about +y, which is what
  `-targetAngle` gives the left half for the same `targetAngle`.
  THE POSE AT LOAD. Both G-11s load commanded to `targetAngle = 60` (`launchPosition = 60`,
  what the editor's OnSaveShip writes) with no `servoTransformRotation`, so their moving
  transforms load at rest and the stock drive turns them at 90 deg/s to +60 / -60 (both up,
  mirror images) in the first second after load, before SRV-3's StartRecording at UT 33.
  So the recording's start snapshot, written by stock from the posed vessel, carries
  `targetAngle = 60` on both halves and `mirrorRotation = True` on the left one: the baseline
  the mirror fix exists for. `servoTransformPosition`, `servoTransformRotation` and
  `jointParentRotation` are DELIBERATELY absent on all three hinges (the `srv-hinge-rover`
  rule: `BaseServo.OnLoad` applies each only when present; stock writes all three live).
  A turn about the pivot keeps the pair's batteries in the vertical plane through the hinge,
  clear of the rover; every part keeps stock's default `sameVesselCollision = False`.
  HEADERS: each new PART's scalar header and EVENTS / ACTIONS / PARTDATA tail are the
  adapter's own lines with name, cid, uid, persistentId, parent, position, rotation, attN,
  mass and rTrf rewritten, `sym` added after `sameVesselCollision` on the pairs, and the
  adapter's variant / cargo-stack keys dropped (`build_srv_hinge_rover` helpers). cid, uid
  and persistentId are derived (sha256 of `SEED` and a tag) and asserted unique in the save;
  SRV-3 pins the left hinge's and the Alligator's persistentId (`rover_part_ids`).

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_srv_mirror_hinge_rover.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import math
import os
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
OUT_DIR = os.path.join(SAVES, "srv-mirror-hinge-rover")

sys.path.insert(0, _HERE)
import build_srv_hinge_rover as hinge  # noqa: E402

landed = hinge.landed
variants = hinge.variants
watched = hinge.watched

BASE_DIR = hinge.BASE_DIR
LF = "\n"
EDITED = ("persistent.sfs",)
SEED = "srv-mirror-hinge-rover/v1"
TITLE = "srv-mirror-hinge-rover (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: a real rover with a mirrored pair "
               "of G-11 hinges and an Alligator hinge, each carrying a Z-200, on its front, "
               "and a neighbouring rover).")

ROVER_BASE_NAME = hinge.ROVER_BASE_NAME
ROVER_NAME = "SRV3 Mirror Hinge Rover"
WATCHER_NAME = hinge.WATCHER_NAME
BASE_PART_COUNT = hinge.BASE_PART_COUNT
ADAPTER_INDEX = hinge.ADAPTER_INDEX
ADAPTER_NAME = hinge.ADAPTER_NAME
ADAPTER_TOP_NODE = hinge.ADAPTER_TOP_NODE

NODE_INDEX = 27
RIGHT_HINGE_INDEX = 28
RIGHT_BATTERY_INDEX = 29
LEFT_HINGE_INDEX = 30
LEFT_BATTERY_INDEX = 31
OFFSET_HINGE_INDEX = 32
OFFSET_BATTERY_INDEX = 33
ADDED_PARTS = 7

MININODE = "structuralMiniNode"
PAIR_HINGE = hinge.HINGE
OFFSET_HINGE = "hinge.03"
BATTERY = hinge.BATTERY

# structuralMicronode.cfg: every stack node 0.1823258 m from the centre.
NODE_HALF = 0.1823258
HINGE_HALF = hinge.HINGE_HALF
BATTERY_HALF = hinge.BATTERY_HALF
# hinge_03.cfg node_stack_bottom / node_stack_top positions (local).
ALLIGATOR_BOTTOM = (0.218749, 0.937497, 0.0)
ALLIGATOR_TOP = (-0.937497, -0.218749, 0.0)

NODE_MASS = "0.150000006"
HINGE_MASS = hinge.HINGE_MASS
ALLIGATOR_MASS = "0.200000003"
BATTERY_MASS = hinge.BATTERY_MASS

PAIR_TARGET = "60"
ALLIGATOR_TARGET = "90"
ALLIGATOR_MODEL_INITIAL_ANGLE = 90.0

S = "0.707106769"
H = "0.5"
# x, y, z, w (the save's order). Each is asserted against its basis map in verify().
ROT_IDENTITY = "0,0,0,1"
ROT_RIGHT = ",".join((S, S, "0", "0"))           # x -> +y, y -> +x, z -> -z
ROT_LEFT = ",".join(("0", "0", S, S))            # x -> +y, y -> -x, z -> +z
ROT_ALLIGATOR = ",".join((H, "-" + H, "-" + H, H))  # x -> -y, y -> +z, z -> -x
ROT_ALLIGATOR_BATTERY = ",".join(("-" + S, "0", "0", S))  # y -> -z (up), z -> +y

# The basis each rotation must produce: (R*x, R*y, R*z) in vessel-local axes.
BASIS_MAPS = {
    ROT_RIGHT: ((0, 1, 0), (1, 0, 0), (0, 0, -1)),
    ROT_LEFT: ((0, 1, 0), (-1, 0, 0), (0, 0, 1)),
    ROT_ALLIGATOR: ((0, -1, 0), (0, 0, 1), (-1, 0, 0)),
    ROT_ALLIGATOR_BATTERY: ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
}

P4 = hinge.P4
P5 = hinge.P5


def _pair_module(mirror: bool) -> Tuple[Tuple[str, str], ...]:
    return (
        ("name", "ModuleRoboticServoHinge"),
        ("isEnabled", "True"),
        ("softMinMaxAngles", "(-90, 90)"),
        ("targetAngle", PAIR_TARGET),
        ("mirrorRotation", "True" if mirror else "False"),
        ("traverseVelocity", "90"),
        ("hingeDamping", "100"),
        ("previousTargetAngle", "0"),
        ("servoIsLocked", "False"),
        ("servoIsMotorized", "True"),
        ("servoMotorIsEngaged", "True"),
        ("launchPosition", PAIR_TARGET),
        ("servoMotorSize", "100"),
        ("servoMotorLimit", "100"),
        ("lockPartOnPowerLoss", "True"),
        ("useMultipleDragCubes", "False"),
        ("stagingEnabled", "True"),
    )


ALLIGATOR_MODULE_VALUES = (
    ("name", "ModuleRoboticServoHinge"),
    ("isEnabled", "True"),
    ("softMinMaxAngles", "(0, 180)"),
    ("targetAngle", ALLIGATOR_TARGET),
    ("mirrorRotation", "False"),
    ("traverseVelocity", "90"),
    ("hingeDamping", "100"),
    ("previousTargetAngle", "0"),
    ("servoIsLocked", "True"),
    ("servoIsMotorized", "True"),
    ("servoMotorIsEngaged", "True"),
    ("launchPosition", ALLIGATOR_TARGET),
    ("servoMotorSize", "100"),
    ("servoMotorLimit", "100"),
    ("lockPartOnPowerLoss", "True"),
    ("useMultipleDragCubes", "True"),
    ("stagingEnabled", "True"),
)


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _module(values) -> List[str]:
    body = [P5 + k + " = " + v for k, v in values]
    for child in ("EVENTS", "ACTIONS", "UPGRADESAPPLIED"):
        body += hinge._block(P5, child, [])
    return hinge._block(P4, "MODULE", body)


def _parse_quat(text: str) -> Tuple[float, float, float, float]:
    x, y, z, w = (float(v) for v in text.split(","))
    return x, y, z, w


def rotate(quat: str, v: Tuple[float, float, float]) -> Tuple[float, float, float]:
    """Rotates `v` by the save-format quaternion `quat` (x,y,z,w), Unity's formula."""
    x, y, z, w = _parse_quat(quat)
    vx, vy, vz = v
    # t = 2 * cross(q.xyz, v); v' = v + w * t + cross(q.xyz, t)
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def _add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _scale(v, s):
    return (v[0] * s, v[1] * s, v[2] * s)


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def build_part(adapter: List[str], tag: str, name: str, parent: int,
               pos: Tuple[float, float, float], rotation: str, att_n: List[str], mass: str,
               extra: List[str], used_pids: set, used_uids: set, sym: int = None) -> List[str]:
    part = hinge._adapter_header_and_tail(adapter)
    pid = _derive_uint(tag + "|persistentId")
    uid = _derive_uint(tag + "|uid")
    assert str(pid) not in used_pids and str(uid) not in used_uids, "%s id collision" % tag
    used_pids.add(str(pid))
    used_uids.add(str(uid))
    for key, value in (
            ("name", name),
            ("cid", str(_derive_uint(tag + "|cid"))),
            ("uid", str(uid)),
            ("persistentId", str(pid)),
            ("parent", str(parent)),
            ("position", ",".join(repr(c) for c in pos)),
            ("rotation", rotation),
            ("mass", mass),
            ("rTrf", name)):
        hinge._set_part(part, key, value)
    attn = [i for i, l in enumerate(part) if hinge._ATTN_RE.match(l)]
    assert len(attn) == 2, "adapter header carries %d attN lines" % len(attn)
    part[attn[0]:attn[1] + 1] = [P4 + "attN = " + a for a in att_n]
    if sym is not None:
        svc = hinge._part_key(part, "sameVesselCollision")
        part.insert(svc + 1, P4 + "sym = %d" % sym)
    # MODULE / RESOURCE nodes go after PARTDATA, before the PART's closing brace.
    part[-1:-1] = extra
    return part


def layout(adapter_pos: Tuple[float, float, float]) -> Dict[int, Tuple[float, float, float]]:
    """Every added part's position, from the adapter's (the node geometry in the docstring)."""
    top = _add(adapter_pos, (0.0, ADAPTER_TOP_NODE, 0.0))
    node = _add(top, (0.0, NODE_HALF, 0.0))
    right_node = _add(node, (NODE_HALF, 0.0, 0.0))
    left_node = _add(node, (-NODE_HALF, 0.0, 0.0))
    fwd_node = _add(node, (0.0, NODE_HALF, 0.0))
    # Each G-11's local +y points outward; its bottom node sits on the micronode's side node.
    right_hinge = _add(right_node, (HINGE_HALF, 0.0, 0.0))
    left_hinge = _add(left_node, (-HINGE_HALF, 0.0, 0.0))
    right_battery = _add(right_hinge, (HINGE_HALF + BATTERY_HALF, 0.0, 0.0))
    left_battery = _add(left_hinge, (-(HINGE_HALF + BATTERY_HALF), 0.0, 0.0))
    # The Alligator's origin puts its rotated bottom node on the micronode's forward node.
    alligator = _sub(fwd_node, rotate(ROT_ALLIGATOR, ALLIGATOR_BOTTOM))
    alligator_top = _add(alligator, rotate(ROT_ALLIGATOR, ALLIGATOR_TOP))
    alligator_battery = _add(alligator_top, (0.0, 0.0, -BATTERY_HALF))
    return {
        NODE_INDEX: node,
        RIGHT_HINGE_INDEX: right_hinge,
        RIGHT_BATTERY_INDEX: right_battery,
        LEFT_HINGE_INDEX: left_hinge,
        LEFT_BATTERY_INDEX: left_battery,
        OFFSET_HINGE_INDEX: alligator,
        OFFSET_BATTERY_INDEX: alligator_battery,
    }


def add_parts(rover: List[str], used_pids: set, used_uids: set) -> List[str]:
    blocks = variants.part_blocks(rover)
    assert len(blocks) == BASE_PART_COUNT, "base rover has %d parts" % len(blocks)
    a_start, a_end = blocks[ADAPTER_INDEX]
    adapter = rover[a_start:a_end]
    assert adapter[2] == P4 + "name = " + ADAPTER_NAME, adapter[2]
    top = [i for i, l in enumerate(adapter) if l == P4 + "attN = top, -1"]
    assert len(top) == 1, "the adapter's top node must be free"
    apos = tuple(float(c) for c in hinge._get_part(adapter, "position").split(","))
    pos = layout(apos)
    battery = hinge._battery_resource()
    new = []
    new += build_part(adapter, "mininode", MININODE, ADAPTER_INDEX, pos[NODE_INDEX],
                      ROT_IDENTITY,
                      ["right, %d" % RIGHT_HINGE_INDEX, "left, %d" % LEFT_HINGE_INDEX,
                       "back, -1", "front, -1", "top, %d" % OFFSET_HINGE_INDEX,
                       "bottom, %d" % ADAPTER_INDEX],
                      NODE_MASS, [], used_pids, used_uids)
    new += build_part(adapter, "hingeRight", PAIR_HINGE, NODE_INDEX, pos[RIGHT_HINGE_INDEX],
                      ROT_RIGHT, ["top, %d" % RIGHT_BATTERY_INDEX, "bottom, %d" % NODE_INDEX],
                      HINGE_MASS, _module(_pair_module(False)), used_pids, used_uids,
                      sym=LEFT_HINGE_INDEX)
    new += build_part(adapter, "batteryRight", BATTERY, RIGHT_HINGE_INDEX,
                      pos[RIGHT_BATTERY_INDEX], ROT_RIGHT,
                      ["top, -1", "bottom, %d" % RIGHT_HINGE_INDEX],
                      BATTERY_MASS, battery, used_pids, used_uids, sym=LEFT_BATTERY_INDEX)
    new += build_part(adapter, "hingeLeft", PAIR_HINGE, NODE_INDEX, pos[LEFT_HINGE_INDEX],
                      ROT_LEFT, ["top, %d" % LEFT_BATTERY_INDEX, "bottom, %d" % NODE_INDEX],
                      HINGE_MASS, _module(_pair_module(True)), used_pids, used_uids,
                      sym=RIGHT_HINGE_INDEX)
    new += build_part(adapter, "batteryLeft", BATTERY, LEFT_HINGE_INDEX,
                      pos[LEFT_BATTERY_INDEX], ROT_LEFT,
                      ["top, -1", "bottom, %d" % LEFT_HINGE_INDEX],
                      BATTERY_MASS, battery, used_pids, used_uids, sym=RIGHT_BATTERY_INDEX)
    new += build_part(adapter, "alligator", OFFSET_HINGE, NODE_INDEX, pos[OFFSET_HINGE_INDEX],
                      ROT_ALLIGATOR,
                      ["top, %d" % OFFSET_BATTERY_INDEX, "bottom, %d" % NODE_INDEX],
                      ALLIGATOR_MASS, _module(ALLIGATOR_MODULE_VALUES), used_pids, used_uids)
    new += build_part(adapter, "alligatorBattery", BATTERY, OFFSET_HINGE_INDEX,
                      pos[OFFSET_BATTERY_INDEX], ROT_ALLIGATOR_BATTERY,
                      ["top, -1", "bottom, %d" % OFFSET_HINGE_INDEX],
                      BATTERY_MASS, battery, used_pids, used_uids)
    out = list(rover)
    out[a_start + top[0]] = P4 + "attN = top, %d" % NODE_INDEX
    end = blocks[-1][1]
    out[end:end] = new
    assert len(variants.part_blocks(out)) == BASE_PART_COUNT + ADDED_PARTS
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
    rover = add_parts(lines[r_start:r_end], used_pids, used_uids)
    landed._set_top(rover, "name", ROVER_NAME)
    lines[r_start:r_end] = rover
    assert "\t\tactiveVessel = 0" in lines, "the rover stays the active vessel"
    title = lines.index("\tTitle = " + watched.TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + watched.DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    return LF.join(lines)


def rover_part_ids() -> Dict[str, int]:
    """The ids a lane names: the two vessels' pids and the three hinges' part pids."""
    lines = build_sfs().split(LF)
    blocks = landed.vessel_blocks(lines)
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = variants.part_blocks(rover)

    def part_pid(index: int) -> int:
        block = rover[parts[index][0]:parts[index][1]]
        return int(hinge._get_part(block, "persistentId"))

    def vessel_pid(block: List[str]) -> int:
        return int([l for l in block if l.startswith("\t\t\tpersistentId = ")][0].split(" = ")[1])
    return {"roverPersistentId": vessel_pid(rover),
            "watcherPersistentId": vessel_pid(lines[blocks[1][0]:blocks[1][1]]),
            "rightHingePersistentId": part_pid(RIGHT_HINGE_INDEX),
            "leftHingePersistentId": part_pid(LEFT_HINGE_INDEX),
            "offsetHingePersistentId": part_pid(OFFSET_HINGE_INDEX)}


def _close(a, b, tol=1e-6) -> bool:
    return all(abs(x - y) <= tol for x, y in zip(a, b))


def verify(sfs: str) -> List[str]:
    """Structural problems in a built save (empty when the shape SRV-3 relies on holds)."""
    problems: List[str] = []
    for quat, (bx, by, bz) in BASIS_MAPS.items():
        x, y, z, w = _parse_quat(quat)
        if abs(x * x + y * y + z * z + w * w - 1.0) > 1e-6:
            problems.append("rotation %s is not a unit quaternion" % quat)
        for axis, want in (((1, 0, 0), bx), ((0, 1, 0), by), ((0, 0, 1), bz)):
            if not _close(rotate(quat, axis), want):
                problems.append("rotation %s maps %r to %r, want %r"
                                % (quat, axis, rotate(quat, axis), want))
    lines = sfs.split(LF)
    blocks = landed.vessel_blocks(lines)
    names = [b[2] for b in blocks]
    if names != [ROVER_NAME, WATCHER_NAME]:
        problems.append("vessels must be [%s, %s], got %r" % (ROVER_NAME, WATCHER_NAME, names))
        return problems
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = variants.part_blocks(rover)
    if len(parts) != BASE_PART_COUNT + ADDED_PARTS:
        problems.append("the rover must carry %d parts, got %d"
                        % (BASE_PART_COUNT + ADDED_PARTS, len(parts)))
        return problems

    def block(index: int) -> List[str]:
        return rover[parts[index][0]:parts[index][1]]

    adapter = block(ADAPTER_INDEX)
    node = block(NODE_INDEX)
    if P4 + "attN = top, %d" % NODE_INDEX not in adapter:
        problems.append("the adapter's top node must carry the micronode")
    if P4 + "name = " + MININODE not in node or P4 + "parent = %d" % ADAPTER_INDEX not in node:
        problems.append("part %d must be the micronode, child of the adapter" % NODE_INDEX)
    for want in ("right, %d" % RIGHT_HINGE_INDEX, "left, %d" % LEFT_HINGE_INDEX,
                 "top, %d" % OFFSET_HINGE_INDEX, "bottom, %d" % ADAPTER_INDEX):
        if P4 + "attN = " + want not in node:
            problems.append("the micronode must carry attN = %s" % want)

    # Each hinge: its bottom (base) node on the micronode, its battery on its TOP (moving)
    # node. Swap them and the replay's follower plan inverts.
    for h_index, b_index, name in ((RIGHT_HINGE_INDEX, RIGHT_BATTERY_INDEX, PAIR_HINGE),
                                   (LEFT_HINGE_INDEX, LEFT_BATTERY_INDEX, PAIR_HINGE),
                                   (OFFSET_HINGE_INDEX, OFFSET_BATTERY_INDEX, OFFSET_HINGE)):
        h, b = block(h_index), block(b_index)
        if P4 + "name = " + name not in h or P4 + "parent = %d" % NODE_INDEX not in h:
            problems.append("part %d must be %s, child of the micronode" % (h_index, name))
        if P4 + "attN = top, %d" % b_index not in h:
            problems.append("part %d's battery must sit on its top (moving) node" % h_index)
        if P4 + "attN = bottom, %d" % NODE_INDEX not in h:
            problems.append("part %d's base (bottom) node must be on the micronode" % h_index)
        if P4 + "name = " + BATTERY not in b or P4 + "parent = %d" % h_index not in b:
            problems.append("part %d must be a battery, child of part %d" % (b_index, h_index))
        if P5 + "amount = " + hinge.BATTERY_EC not in b:
            problems.append("part %d (battery) must hold its charge" % b_index)
        if P5 + "name = ModuleRoboticServoHinge" not in h:
            problems.append("part %d must carry its ModuleRoboticServoHinge node" % h_index)
        for key in ("servoTransformPosition", "servoTransformRotation", "jointParentRotation"):
            if any(l.startswith(P5 + key + " = ") for l in h):
                problems.append("part %d must not pin %s (stock writes it live)" % (h_index, key))

    right, left, alligator = (block(RIGHT_HINGE_INDEX), block(LEFT_HINGE_INDEX),
                              block(OFFSET_HINGE_INDEX))
    # The pair: symmetric soft limits, commanded to the same non-zero target, free, powered,
    # linked as a symmetry pair, and only the LEFT half mirrored (stock's CheckSymmetry answer).
    for h, label, mirror, sym in ((right, "right", "False", LEFT_HINGE_INDEX),
                                  (left, "left", "True", RIGHT_HINGE_INDEX)):
        for key, want in (("mirrorRotation", mirror), ("targetAngle", PAIR_TARGET),
                          ("softMinMaxAngles", "(-90, 90)"), ("servoIsLocked", "False"),
                          ("servoMotorIsEngaged", "True"), ("servoIsMotorized", "True")):
            if P5 + key + " = " + want not in h:
                problems.append("the %s hinge must load with %s = %s" % (label, key, want))
        if P4 + "sym = %d" % sym not in h:
            problems.append("the %s hinge must name its counterpart (sym = %d)" % (label, sym))
    if hinge._get_part(right, "rotation") != ROT_RIGHT or hinge._get_part(left, "rotation") != ROT_LEFT:
        problems.append("the pair's rotations must be the mirror placement")
    # Both pivots on +y (mainAxis X), the moving sides outward and opposite: the geometry that
    # makes mirrorRotation = True stock's own answer and -targetAngle the mirror image.
    if not (_close(rotate(ROT_RIGHT, (1, 0, 0)), rotate(ROT_LEFT, (1, 0, 0)))
            and _close(rotate(ROT_RIGHT, (0, 1, 0)), _scale(rotate(ROT_LEFT, (0, 1, 0)), -1.0))):
        problems.append("the pair must share its pivot axis and face opposite ways")
    rpos = tuple(float(c) for c in hinge._get_part(right, "position").split(","))
    lpos = tuple(float(c) for c in hinge._get_part(left, "position").split(","))
    if not _close(rpos, (-lpos[0], lpos[1], lpos[2])) or not rpos[0] > 0.0:
        problems.append("the pair must stand mirror-imaged across x = 0, right half at +x")
    # The Alligator: an offset hinge locked at its rest, unmirrored.
    for key, want in (("mirrorRotation", "False"), ("targetAngle", ALLIGATOR_TARGET),
                      ("servoIsLocked", "True"), ("softMinMaxAngles", "(0, 180)")):
        if P5 + key + " = " + want not in alligator:
            problems.append("the Alligator must load with %s = %s" % (key, want))
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
    # Files are overwritten in place; a stale file the builder no longer writes is reported
    # by --check ("unexpected"), never deleted here.
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
