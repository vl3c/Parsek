#!/usr/bin/env python3
"""Build the committed `srv-hinge-rover` fixture BY CONSTRUCTION (the SRV-1 host).

WHY THIS FIXTURE EXISTS. Todo GHOST-SERVO-CHILDREN-STATIC: parts on a Breaking Ground servo's
moving side stayed frozen on the ghost while the servo turned. SRV-1 records a hinge swing and
replays it. Its first host, `srv-hinge-pad` (a VAB rig launched onto the pad), could not work
on two counts (first flight 2026-11-20): a vessel that never leaves the launch site has its
ghost hidden by design (`LaunchSiteOnlyGhost`: every point inside the 50 m pad / runway circle,
climb under 15 m, ended parked there), and the recorder never saw the hinge move (todo
RECORDER-SERVO-ANGLE-PAW-ONLY, fixed with this fixture). This host is a rover parked 820 m
from the runway threshold, the proven `miner-rover-watched` pair, with a hinge on its front.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: the committed `fixtures/saves/miner-rover-watched`
  (V's real `Mining Rover RC` at index 0, active, LANDED at lat 0.0296 lon -74.7247, its
  every clock at UT 30; the crewless `Miner Watcher` 120 m north at index 1; the inert
  ParsekScenario node; `AddOns/`; `persistent.loadmeta` vesselCount 2, UT 30). That pair is
  MINE-6's: after Rewind-to-Launch strips the recorded rover, a reload of the rewound save
  lands FLIGHT on the watcher (vessel 0), inside the physics bubble of the replayed ghost.
  RENAMED: the rover's vessel `name` becomes `SRV1 Hinge Rover`, so SRV-1's log lines never
  read as a mining lane's.
  ADDED to the rover, the `miner-rover-depot` port precedent (MINE-5b, live-proven): two
  parts stacked on the free top node of its `adapterSmallMiniShort` (part 26, child of the
  probe core, the rover's front end along its driving axis +y; vessel-local +z is down):
    27. `hinge.01` (G-11 Hinge), `parent = 26`, by its BOTTOM node (hinge_01.cfg
        `node_stack_bottom` y -0.3125): `position` y = the adapter's + its top node (0.1875)
        + 0.3125. The adapter's `attN = top, -1` becomes `attN = top, 27`.
    28. `batteryBankMini` (Z-200, 200 EC), `parent = 27`, on the hinge's TOP node, which is
        its MOVING side (hinge_01.cfg `servoAttachNodes = top`): `position` y = the hinge's
        + 0.3125 + 0.1 (z-200Battery.cfg `node_stack_bottom` y -0.1).
  Both carry `rotation` (0, 0.7071068, 0, 0.7071068), a 90 deg turn about the stack axis, so
  the hinge's own pivot axis (`mainAxis = X`) maps to vessel-local z (vertical) and the
  battery swings sideways, level with the rover, never into the ground. A turn about the
  stack axis leaves the stack nodes where they are.
  So the replay's follower plan must read servos=1 followers=1 invertedServos=0: the
  battery follows, the adapter and the rest of the rover (the base side) do not.
  THE HINGE'S MODULE: one `ModuleRoboticServoHinge` node with the keys stock writes for a
  hinge (`Val-Thopter.craft`'s, the stock Breaking Ground example) set to a free, powered
  hinge at rest: unlocked (`servoIsLocked = False`), motorized and engaged, soft limits
  (-90, 90), `targetAngle` / `previousTargetAngle` / `launchPosition` 0, `traverseVelocity`
  90 deg/s, `servoMotorSize` / `servoMotorLimit` 100. Decompiled `BaseServo` /
  `ModuleRoboticServoHinge` (KSP 1.12.5): `MaximumAngleAction` -> `SetMaximumAngle` writes
  `targetAngle = softMinMaxAngles.y` unless locked, and `OnFixedUpdate` drives
  `driveTargetAngle` toward it at `traverseVelocity * dt` while `servoMotorIsEngaged` and not
  locked. `servoTransformPosition`, `servoTransformRotation` and `jointParentRotation` are
  DELIBERATELY absent: `BaseServo.OnLoad` applies each only when the key is present, so the
  model's own pose is used; stock writes all three from the live transform in the recording's
  snapshot, which is what the ghost bind reads. The hinge's other modules (ModulePartVariants,
  ModuleResourceAutoShiftState, ModuleCargoPart) and the battery's ModuleCargoPart are not
  written: a ProtoPartSnapshot with fewer MODULE nodes keeps the prefab's own (the seat-rover-
  pad precedent, flown), and an empty `affectedResourceName` / `affectedModuleName` would
  overwrite the auto-shift module's cfg values. The battery carries its RESOURCE node (200 of
  200 ElectricCharge, the rover's own battery RESOURCE shape).
  HEADERS: each new PART's scalar header and EVENTS / ACTIONS / PARTDATA tail are the
  adapter's own lines (same vessel, mission, launch and thermal state) with name, cid, uid,
  persistentId, parent, position, rotation, attN, mass and rTrf rewritten and the adapter's
  variant / cargo-stack keys dropped. cid, uid and persistentId are derived (sha256 of `SEED`
  and a tag) and asserted unique in the save.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_srv_hinge_rover.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from typing import Dict, List

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_DIR = os.path.join(SAVES, "miner-rover-watched")
OUT_DIR = os.path.join(SAVES, "srv-hinge-rover")

sys.path.insert(0, _HERE)
import build_miner_rover_landed as landed  # noqa: E402
import build_miner_rover_variants as variants  # noqa: E402
import build_miner_rover_watched as watched  # noqa: E402

LF = "\n"
EDITED = ("persistent.sfs",)
SEED = "srv-hinge-rover/v1"
TITLE = "srv-hinge-rover (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: a real rover with a G-11 hinge "
               "carrying a Z-200 on its front, and a neighbouring rover).")

ROVER_BASE_NAME = landed.ROVER_NAME
ROVER_NAME = "SRV1 Hinge Rover"
WATCHER_NAME = watched.WATCHER_NAME
BASE_PART_COUNT = 27
ADAPTER_INDEX = 26
ADAPTER_NAME = "adapterSmallMiniShort"
ADAPTER_TOP_NODE = 0.1875
HINGE_INDEX = 27
BATTERY_INDEX = 28
HINGE = "hinge.01"
BATTERY = "batteryBankMini"
HINGE_HALF = 0.3125
BATTERY_HALF = 0.1
# 90 degrees about the stack axis (vessel-local y): the hinge's X pivot becomes vertical.
PART_ROTATION = "0,0.707106769,0,0.707106769"
HINGE_MASS = "0.0799999982"
BATTERY_MASS = "0.00999999978"
BATTERY_EC = "200"

# Part-level keys of the adapter that belong to its own modules, not to the new parts.
DROPPED_HEADER_KEYS = ("moduleVariantName", "moduleCargoStackableQuantity")

P4 = "\t\t\t\t"
P5 = "\t\t\t\t\t"

HINGE_MODULE_VALUES = (
    ("name", "ModuleRoboticServoHinge"),
    ("isEnabled", "True"),
    ("softMinMaxAngles", "(-90, 90)"),
    ("targetAngle", "0"),
    ("mirrorRotation", "False"),
    ("traverseVelocity", "90"),
    ("hingeDamping", "100"),
    ("previousTargetAngle", "0"),
    ("servoIsLocked", "False"),
    ("servoIsMotorized", "True"),
    ("servoMotorIsEngaged", "True"),
    ("launchPosition", "0"),
    ("servoMotorSize", "100"),
    ("servoMotorLimit", "100"),
    ("lockPartOnPowerLoss", "True"),
    ("useMultipleDragCubes", "False"),
    ("stagingEnabled", "True"),
)

_ATTN_RE = re.compile(r"^\t\t\t\tattN = ")


def _derive_uint(tag: str) -> int:
    value = int(hashlib.sha256((SEED + "|" + tag).encode("ascii")).hexdigest()[:8], 16)
    return value or 1


def _ids(lines: List[str], key: str) -> set:
    rx = re.compile(r"^\t+" + key + r" = (\d+)$")
    return {m.group(1) for m in (rx.match(l) for l in lines) if m}


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


def _hinge_module() -> List[str]:
    body = [P5 + k + " = " + v for k, v in HINGE_MODULE_VALUES]
    for child in ("EVENTS", "ACTIONS", "UPGRADESAPPLIED"):
        body += _block(P5, child, [])
    return _block(P4, "MODULE", body)


def _battery_resource() -> List[str]:
    body = [P5 + "name = ElectricCharge",
            P5 + "amount = " + BATTERY_EC,
            P5 + "maxAmount = " + BATTERY_EC,
            P5 + "flowState = True",
            P5 + "isTweakable = True",
            P5 + "hideFlow = False",
            P5 + "isVisible = True",
            P5 + "flowMode = Both"]
    return _block(P4, "RESOURCE", body)


def _adapter_header_and_tail(adapter: List[str]) -> List[str]:
    """The adapter's PART lines up to (excluding) its first MODULE node, closing brace added."""
    first_module = adapter.index(P4 + "MODULE")
    head = adapter[:first_module]
    return [l for l in head
            if not any(l.startswith(P4 + k + " = ") for k in DROPPED_HEADER_KEYS)] + ["\t\t\t}"]


def build_part(adapter: List[str], tag: str, name: str, parent: int, y: float,
               att_n: List[str], mass: str, extra: List[str],
               used_pids: set, used_uids: set) -> List[str]:
    part = _adapter_header_and_tail(adapter)
    pid = _derive_uint(tag + "|persistentId")
    uid = _derive_uint(tag + "|uid")
    assert str(pid) not in used_pids and str(uid) not in used_uids, "%s id collision" % tag
    used_pids.add(str(pid))
    used_uids.add(str(uid))
    pos = _get_part(adapter, "position").split(",")
    for key, value in (
            ("name", name),
            ("cid", str(_derive_uint(tag + "|cid"))),
            ("uid", str(uid)),
            ("persistentId", str(pid)),
            ("parent", str(parent)),
            ("position", ",".join((pos[0], repr(y), pos[2]))),
            ("rotation", PART_ROTATION),
            ("mass", mass),
            ("rTrf", name)):
        _set_part(part, key, value)
    attn = [i for i, l in enumerate(part) if _ATTN_RE.match(l)]
    assert len(attn) == 2, "adapter header carries %d attN lines" % len(attn)
    part[attn[0]:attn[1] + 1] = [P4 + "attN = " + a for a in att_n]
    # MODULE / RESOURCE nodes go after PARTDATA, before the PART's closing brace.
    part[-1:-1] = extra
    return part


def add_hinge_and_battery(rover: List[str], used_pids: set, used_uids: set) -> List[str]:
    blocks = variants.part_blocks(rover)
    assert len(blocks) == BASE_PART_COUNT, "base rover has %d parts" % len(blocks)
    a_start, a_end = blocks[ADAPTER_INDEX]
    adapter = rover[a_start:a_end]
    assert adapter[2] == P4 + "name = " + ADAPTER_NAME, adapter[2]
    top = [i for i, l in enumerate(adapter) if l == P4 + "attN = top, -1"]
    assert len(top) == 1, "the adapter's top node must be free"
    adapter_y = float(_get_part(adapter, "position").split(",")[1])
    hinge_y = adapter_y + ADAPTER_TOP_NODE + HINGE_HALF
    battery_y = hinge_y + HINGE_HALF + BATTERY_HALF
    hinge = build_part(adapter, "hinge", HINGE, ADAPTER_INDEX, hinge_y,
                       ["top, %d" % BATTERY_INDEX, "bottom, %d" % ADAPTER_INDEX],
                       HINGE_MASS, _hinge_module(), used_pids, used_uids)
    battery = build_part(adapter, "battery", BATTERY, HINGE_INDEX, battery_y,
                         ["top, -1", "bottom, %d" % HINGE_INDEX],
                         BATTERY_MASS, _battery_resource(), used_pids, used_uids)
    out = list(rover)
    out[a_start + top[0]] = P4 + "attN = top, %d" % HINGE_INDEX
    end = blocks[-1][1]
    out[end:end] = hinge + battery
    assert len(variants.part_blocks(out)) == BASE_PART_COUNT + 2
    return out


def build_sfs() -> str:
    base = landed._read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in base, "miner-rover-watched is committed LF"
    lines = base.split(LF)
    blocks = landed.vessel_blocks(lines)
    assert [b[2] for b in blocks] == [ROVER_BASE_NAME, WATCHER_NAME], [b[2] for b in blocks]
    r_start, r_end, _ = blocks[0]
    used_pids = _ids(lines, "persistentId")
    used_uids = _ids(lines, "uid")
    rover = add_hinge_and_battery(lines[r_start:r_end], used_pids, used_uids)
    landed._set_top(rover, "name", ROVER_NAME)
    lines[r_start:r_end] = rover
    assert "\t\tactiveVessel = 0" in lines, "the rover stays the active vessel"
    title = lines.index("\tTitle = " + watched.TITLE)
    lines[title] = "\tTitle = " + TITLE
    desc = lines.index("\tDescription = " + watched.DESCRIPTION)
    lines[desc] = "\tDescription = " + DESCRIPTION
    return LF.join(lines)


def rover_part_ids() -> Dict[str, int]:
    """The ids a lane names: the rover's and the watcher's vessel pids and the hinge's."""
    lines = build_sfs().split(LF)
    blocks = landed.vessel_blocks(lines)
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = variants.part_blocks(rover)
    hinge = rover[parts[HINGE_INDEX][0]:parts[HINGE_INDEX][1]]

    def vessel_pid(block: List[str]) -> int:
        return int([l for l in block if l.startswith("\t\t\tpersistentId = ")][0].split(" = ")[1])
    return {"roverPersistentId": vessel_pid(rover),
            "watcherPersistentId": vessel_pid(lines[blocks[1][0]:blocks[1][1]]),
            "hingePersistentId": int(_get_part(hinge, "persistentId"))}


def verify(sfs: str) -> List[str]:
    """Structural problems in a built save (empty when the shape SRV-1 relies on holds)."""
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
    hinge = rover[parts[HINGE_INDEX][0]:parts[HINGE_INDEX][1]]
    battery = rover[parts[BATTERY_INDEX][0]:parts[BATTERY_INDEX][1]]
    if P4 + "attN = top, %d" % HINGE_INDEX not in adapter:
        problems.append("the adapter's top node must carry the hinge")
    if P4 + "name = " + HINGE not in hinge or P4 + "parent = %d" % ADAPTER_INDEX not in hinge:
        problems.append("part %d must be the hinge, child of the adapter" % HINGE_INDEX)
    # The battery rides the hinge's MOVING node (servoAttachNodes = top); the adapter is on the
    # base node. Swap them and the replay's follower plan inverts.
    if P4 + "attN = top, %d" % BATTERY_INDEX not in hinge:
        problems.append("the battery must sit on the hinge's top (moving) node")
    if P4 + "attN = bottom, %d" % ADAPTER_INDEX not in hinge:
        problems.append("the adapter must sit on the hinge's bottom (base) node")
    if P4 + "name = " + BATTERY not in battery or P4 + "parent = %d" % HINGE_INDEX not in battery:
        problems.append("part %d must be the battery, child of the hinge" % BATTERY_INDEX)
    if P4 + "attN = bottom, %d" % HINGE_INDEX not in battery:
        problems.append("the battery's bottom node must be on the hinge")
    if P5 + "name = ModuleRoboticServoHinge" not in hinge:
        problems.append("the hinge must carry its ModuleRoboticServoHinge node")
    for key, want in (("servoIsLocked", "False"), ("servoMotorIsEngaged", "True"),
                      ("servoIsMotorized", "True")):
        if P5 + key + " = " + want not in hinge:
            problems.append("the hinge must load with %s = %s" % (key, want))
    for key in ("servoTransformPosition", "servoTransformRotation", "jointParentRotation"):
        if any(l.startswith(P5 + key + " = ") for l in hinge):
            problems.append("the hinge must not pin %s (stock writes it live)" % key)
    if P5 + "amount = " + BATTERY_EC not in battery:
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
