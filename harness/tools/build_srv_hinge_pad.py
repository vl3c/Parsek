#!/usr/bin/env python3
"""Build the committed `srv-hinge-pad` fixture BY CONSTRUCTION (the SRV-1 host).

WHY THIS FIXTURE EXISTS. Todo GHOST-SERVO-CHILDREN-STATIC: parts on a Breaking Ground servo's
moving side stayed frozen on the ghost while the servo turned. The fix (ServoFollowerPlanner /
BindServoFollowers / PropagateServoFollowers) is proven headless and by the in-game
`ServoFollowers` cell over a synthetic snapshot, but no lane ever RECORDED a moving servo and
replayed it. No committed craft carries a hinge, so the lane needs one, launched from the VAB
onto an empty pad, plus a second craft to watch the replay from after the rewind.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: `fixtures/saves/fresh-sandbox` (SANDBOX, so every
  part is available with no tech; no vessels, so `LoadGame` takes the no-vessel Space Center
  route and the pad is free for `GoToEditor` + `LaunchFromEditor`; no Parsek footprint).
  EDITED: the save `Title` / `Description` (so the Load menu names the fixture).
  ADDED: two VAB craft, every part tail AUTHORED with no MODULE / RESOURCE block: KSP's craft
  loader instantiates every module from the part prefab and loads values only onto modules a
  MODULE node names (Part.LoadModule, decompiled), so each module loads at its cfg defaults
  (the seat-pad, Hitchhiker and LABX precedent, all flown). For the hinge that is: motorized,
  motor engaged, unlocked, soft limits -90 / 90 (hinge_01.cfg), traverseVelocity 90 deg/s
  (the KSPField default), targetAngle 0; resources full.

  `SRV1 Hinge Rig`, a vertical stack, root first (file order):
    1. probeCoreOcto2.v2 (OKTO2, the root and control source, 5 EC).
    2. fuelTankSmallFlat (FL-T100) on the OKTO2's bottom node: a 1.25 m base to stand on.
    3. hinge.01 (G-11 Hinge) on the OKTO2's top node by its BOTTOM node. Its prefab declares
       `servoAttachNodes = top`, so its top node is the MOVING side and its parent (the OKTO2,
       on the bottom node) is the base side.
    4. batteryBankMini (Z-200, 200 EC: the hinge's motor power) on the hinge's TOP node: the
       one part riding the hinge's moving half.
  So the replay's follower plan must read servos=1 followers=1 invertedServos=0: the battery
  follows, the OKTO2 and the tank (base side) do not.

  `SRV1 Watcher`: the same OKTO2 on the same FL-T100, nothing else. It gives the rewound
  FLIGHT scene a live vessel on the pad to watch the ghost from. A different craft (its own
  baked persistentIds) so no live vessel shares a pid with the recorded one.

  DERIVED: each PART's scalar header. Stack offsets come from the part cfgs (rescaleFactor 1):
  OKTO2 nodes +-0.0610621 (probeCoreOcto2_v2.cfg, also Val-Thopter's KSP-written attN token),
  FL-T100 +-0.3125 (fuelTankT100.cfg, Kerbal 1's attN tokens), G-11 +-0.3125 (hinge_01.cfg),
  Z-200 +-0.1 (z-200Battery.cfg). The root sits at pos (0,15,0) like the Jumping Flea; the
  launch places the craft on the pad by its own bounds.

ASSUMPTIONS THE FIRST FLIGHT CAN FALSIFY: the stack stands on the pad (a 1.25 m base under a
0.32 t craft); the hinge reaches +90 and then -90 inside the lane's 1x waits (90 deg/s); the
swung battery hits nothing (same-vessel collision is off by default, `sameVesselCollision =
False`).

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_srv_hinge_pad.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
FIXTURES = os.path.join(HARNESS, "fixtures")
SAVES = os.path.join(FIXTURES, "saves")
BASE_DIR = os.path.join(SAVES, "fresh-sandbox")
OUT_DIR = os.path.join(SAVES, "srv-hinge-pad")

RIG_NAME = "SRV1 Hinge Rig"
WATCHER_NAME = "SRV1 Watcher"
RIG_REL = os.path.join("Ships", "VAB", RIG_NAME + ".craft")
WATCHER_REL = os.path.join("Ships", "VAB", WATCHER_NAME + ".craft")

BASE_TITLE = "fresh-sandbox (SANDBOX)"
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."
TITLE = "srv-hinge-pad (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: fresh-sandbox plus the "
               "SRV1 Hinge Rig and SRV1 Watcher craft in the VAB).")

LF = "\n"

# Runtime (dot-form) part names.
OKTO2 = "probeCoreOcto2.v2"
TANK = "fuelTankSmallFlat"
HINGE = "hinge.01"
BATTERY = "batteryBankMini"

# Stack-node half heights (metres, part-local y), from the part cfgs.
OKTO2_HALF = 0.0610621
TANK_HALF = 0.3125
HINGE_HALF = 0.3125
BATTERY_HALF = 0.1

ROOT_Y = 15.0

# Identities: fixed, distinct, in a band no committed craft uses.
RIG_SHIP_PID = 2200250011
WATCHER_SHIP_PID = 2200250021
RIG_UID = {"okto": 4260100001, "tank": 4260100002, "hinge": 4260100003, "battery": 4260100004}
RIG_PID = {"okto": 1700250001, "tank": 1700250002, "hinge": 1700250003, "battery": 1700250004}
WATCHER_UID = {"okto": 4260100011, "tank": 4260100012}
WATCHER_PID = {"okto": 1700250011, "tank": 1700250012}

EMPTY_TAIL = [
    "\tEVENTS",
    "\t{",
    "\t}",
    "\tACTIONS",
    "\t{",
    "\t}",
    "\tPARTDATA",
    "\t{",
    "\t}",
]


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _fmt(v: float) -> str:
    v = round(float(v), 9)
    if v == 0:
        return "0"
    s = repr(v)
    if "e" in s or "E" in s:
        s = ("%.9f" % v).rstrip("0").rstrip(".")
    if s.endswith(".0"):
        s = s[:-2]
    return "0" if s == "-0" else s


def _vec(x: float, y: float, z: float) -> str:
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


def _ref(name: str, uid: int) -> str:
    return "%s_%d" % (name, uid)


def _node(node_id: str, other_ref: str, own_offset: float) -> str:
    # KSP's craft form: <own node id>,<attached part ref>_<own node position x|y|z>.
    return "%s,%s_0|%s|0" % (node_id, other_ref, _fmt(own_offset))


def _header(name: str, uid: int, pid: int, pos_y: float, att_y: float,
            links: List[str], att_n: List[str]) -> List[str]:
    out = [
        "\tpart = " + _ref(name, uid),
        "\tpartName = Part",
        "\tpersistentId = %d" % pid,
        "\tpos = " + _vec(0, pos_y, 0),
        "\tattPos = 0,0,0",
        "\tattPos0 = " + _vec(0, att_y, 0),
        "\trot = 0,0,0,1",
        "\tattRot = 0,0,0,1",
        "\tattRot0 = 0,0,0,1",
        "\tmir = 1,1,1",
        "\tsymMethod = Radial",
        "\tautostrutMode = Off",
        "\trigidAttachment = False",
        "\tistg = -1",
        "\tresPri = 0",
        "\tdstg = 0",
        "\tsidx = -1",
        "\tsqor = -1",
        "\tsepI = -1",
        "\tattm = 0",
        "\tsameVesselCollision = False",
        "\tmodCost = 0",
        "\tmodMass = 0",
        "\tmodSize = 0,0,0",
    ]
    out.extend("\tlink = " + l for l in links)
    out.extend("\tattN = " + a for a in att_n)
    return out


def _craft(ship: str, ship_pid: int, description: str, height: float,
           bodies: List[List[str]]) -> str:
    lines = [
        "ship = " + ship,
        "version = 1.12.5",
        "description = " + description,
        "type = VAB",
        "size = 1.25," + _fmt(round(height, 6)) + ",1.25",
        "steamPublishedFileId = 0",
        "persistentId = %d" % ship_pid,
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Probe",
    ]
    for body in bodies:
        lines.append("PART")
        lines.append("{")
        lines.extend(body + EMPTY_TAIL)
        lines.append("}")
    return LF.join(lines) + LF


def _okto_and_tank(uid: Dict[str, int], pid: Dict[str, int],
                   extra_links: List[str], extra_att: List[str]) -> Tuple[List[str], List[str]]:
    okto_ref = _ref(OKTO2, uid["okto"])
    tank_ref = _ref(TANK, uid["tank"])
    okto = _header(OKTO2, uid["okto"], pid["okto"], ROOT_Y, ROOT_Y,
                   [tank_ref] + extra_links,
                   [_node("bottom", tank_ref, -OKTO2_HALF)] + extra_att)
    tank_rel = -(OKTO2_HALF + TANK_HALF)
    tank = _header(TANK, uid["tank"], pid["tank"], ROOT_Y + tank_rel, tank_rel,
                   [], [_node("top", okto_ref, TANK_HALF)])
    return okto, tank


def build_rig() -> str:
    okto_ref = _ref(OKTO2, RIG_UID["okto"])
    hinge_ref = _ref(HINGE, RIG_UID["hinge"])
    battery_ref = _ref(BATTERY, RIG_UID["battery"])
    okto, tank = _okto_and_tank(RIG_UID, RIG_PID, [hinge_ref],
                                [_node("top", hinge_ref, OKTO2_HALF)])
    hinge_rel = OKTO2_HALF + HINGE_HALF
    hinge_y = ROOT_Y + hinge_rel
    hinge = _header(HINGE, RIG_UID["hinge"], RIG_PID["hinge"], hinge_y, hinge_rel,
                    [battery_ref],
                    [_node("bottom", okto_ref, -HINGE_HALF),
                     _node("top", battery_ref, HINGE_HALF)])
    battery_rel = HINGE_HALF + BATTERY_HALF
    battery = _header(BATTERY, RIG_UID["battery"], RIG_PID["battery"], hinge_y + battery_rel,
                      battery_rel, [], [_node("bottom", hinge_ref, -BATTERY_HALF)])
    height = 2 * (TANK_HALF + OKTO2_HALF + HINGE_HALF + BATTERY_HALF)
    return _craft(RIG_NAME, RIG_SHIP_PID,
                  "SRV1 Hinge Rig. An OKTO2 on an FL-T100 with a G-11 Hinge on top and a Z-200 "
                  "battery on the hinge's moving (top) node. The SRV-1 lane swings the hinge "
                  "while recording and reads the battery following it on the ghost. Built by "
                  "construction by harness/tools/build_srv_hinge_pad.py.",
                  height, [okto, tank, hinge, battery])


def build_watcher() -> str:
    okto, tank = _okto_and_tank(WATCHER_UID, WATCHER_PID, [], [])
    height = 2 * (TANK_HALF + OKTO2_HALF)
    return _craft(WATCHER_NAME, WATCHER_SHIP_PID,
                  "SRV1 Watcher. An OKTO2 on an FL-T100: the live vessel the SRV-1 lane watches "
                  "the hinge rig's replay from after the rewind. Built by construction by "
                  "harness/tools/build_srv_hinge_pad.py.",
                  height, [okto, tank])


def build_files() -> Dict[str, bytes]:
    files: Dict[str, bytes] = {}
    sfs = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    assert "\r" not in sfs, "fresh-sandbox is committed LF"
    assert "\n\t\tVESSEL\n" not in sfs, "fresh-sandbox must be vessel-less"
    assert "name = ParsekScenario" not in sfs, "fresh-sandbox has no Parsek node"
    old_title = "\tTitle = " + BASE_TITLE + LF
    old_desc = "\tDescription = " + BASE_DESCRIPTION + LF
    assert sfs.count(old_title) == 1, "base Title line not found"
    assert sfs.count(old_desc) == 1, "base Description line not found"
    sfs = sfs.replace(old_title, "\tTitle = " + TITLE + LF)
    sfs = sfs.replace(old_desc, "\tDescription = " + DESCRIPTION + LF)
    files["persistent.sfs"] = sfs.encode("utf-8")
    for rel in ("persistent.loadmeta", os.path.join("AddOns", "DistantObject", "Settings.cfg")):
        with open(os.path.join(BASE_DIR, rel), "rb") as fh:
            files[rel] = fh.read()
    files[RIG_REL] = build_rig().encode("utf-8")
    files[WATCHER_REL] = build_watcher().encode("utf-8")
    return files


def _part_names(craft: str) -> List[str]:
    parts = [l.strip().partition(" = ")[2] for l in craft.split(LF) if l.startswith("\tpart = ")]
    return [p.rsplit("_", 1)[0] for p in parts]


def _pids(craft: str) -> List[str]:
    return [l.strip().partition(" = ")[2] for l in craft.split(LF)
            if l.startswith("\tpersistentId = ")]


def verify(files: Dict[str, bytes]) -> List[str]:
    problems: List[str] = []
    rig = files[RIG_REL].decode("utf-8")
    watcher = files[WATCHER_REL].decode("utf-8")
    if _part_names(rig) != [OKTO2, TANK, HINGE, BATTERY]:
        problems.append("rig parts must be [OKTO2, FL-T100, G-11, Z-200], got %r" % _part_names(rig))
    if _part_names(watcher) != [OKTO2, TANK]:
        problems.append("watcher parts must be [OKTO2, FL-T100], got %r" % _part_names(watcher))
    hinge_ref = _ref(HINGE, RIG_UID["hinge"])
    battery_ref = _ref(BATTERY, RIG_UID["battery"])
    okto_ref = _ref(OKTO2, RIG_UID["okto"])
    hinge_block = rig.split("\tpart = " + hinge_ref, 1)[1].split("PART\n", 1)[0]
    # The battery rides the hinge's MOVING node (servoAttachNodes = top); the OKTO2 is on the
    # base node. Swap them and the replay's follower plan inverts.
    if ("\tattN = top," + battery_ref + "_") not in hinge_block:
        problems.append("the battery must sit on the hinge's top (moving) node")
    if ("\tattN = bottom," + okto_ref + "_") not in hinge_block:
        problems.append("the OKTO2 must sit on the hinge's bottom (base) node")
    if ("\tlink = " + battery_ref) not in hinge_block:
        problems.append("the hinge must link the battery (KSP builds the tree from links)")
    okto_block = rig.split("\tpart = " + okto_ref, 1)[1].split("PART\n", 1)[0]
    if ("\tlink = " + hinge_ref) not in okto_block:
        problems.append("the OKTO2 must link the hinge")
    for name, craft in (("rig", rig), ("watcher", watcher)):
        if "\tMODULE" in craft or "\tRESOURCE" in craft:
            problems.append("%s: authored tails carry no MODULE / RESOURCE block" % name)
        if not craft.isascii():
            problems.append("%s: craft must be ASCII" % name)
    if set(_pids(rig)) & set(_pids(watcher)):
        problems.append("the rig and the watcher must not share a baked persistentId")
    return problems


def _write(files: Dict[str, bytes]) -> None:
    for rel, data in files.items():
        path = os.path.join(OUT_DIR, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)


def check() -> List[str]:
    files = build_files()
    problems = verify(files)
    for rel, data in files.items():
        path = os.path.join(OUT_DIR, rel)
        if not os.path.isfile(path):
            problems.append("missing committed file: " + rel)
            continue
        with open(path, "rb") as fh:
            if fh.read() != data:
                problems.append("committed file drifted from the builder: " + rel)
    committed = set()
    for root, _dirs, names in os.walk(OUT_DIR):
        for n in names:
            committed.add(os.path.relpath(os.path.join(root, n), OUT_DIR))
    extra = sorted(committed - set(files))
    if extra:
        problems.append("unexpected committed files: %r" % extra)
    return problems


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--check", action="store_true")
    g.add_argument("--write", action="store_true")
    args = ap.parse_args(argv)
    if args.write:
        files = build_files()
        problems = verify(files)
        if problems:
            for p in problems:
                print("PROBLEM: " + p)
            return 1
        _write(files)
        print("wrote %d file(s) under %s" % (len(files), OUT_DIR))
        return 0
    problems = check()
    for p in problems:
        print("PROBLEM: " + p)
    print("OK" if not problems else "FAIL")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
