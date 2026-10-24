#!/usr/bin/env python3
"""Build the committed `seat-pad` fixture BY CONSTRUCTION (the SEAT-1 host).

WHY THIS FIXTURE EXISTS. Owner ruling 2026-10-24: a kerbal leaving an external command
seat is an EVA (a vehicle and a kerbal at the end), undock is vehicle + vehicle. Stock's
seat dismount (`KerbalEVA.OnDeboardSeat` -> `EjectFromSeat` -> `part.Undock`) reaches
Parsek only through `onVesselsUndocking`, so before the fix a pod-cored vehicle recorded
`Tree branch created: type=Undock` plus a Rewind Point at the dismount. No committed
fixture or craft carries an external command seat (`seatExternalCmd` appears in the
corpus only as tech-tree `part =` entries), so the lane needs a craft that has one,
launched from the VAB onto an empty pad.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: `fixtures/saves/fresh-sandbox` (SANDBOX, no
  vessels, no Parsek footprint, so `LoadGame` takes the no-vessel Space Center route and
  the pad is free for `GoToEditor` + `LaunchFromEditor`).
  EDITED: the save `Title` / `Description` (so the Load menu names the fixture).
  ADDED: `Ships/VAB/SEAT1 Seat Pod.craft`, two parts, root first:
    1. mk1pod.v2 (Mk1 Command Pod: the root, one seat, the only crewed part). Its tail
       (EVENTS / ACTIONS / PARTDATA / MODULE* / RESOURCE*) is lifted byte for byte from
       the committed `GS1 Auto-Chute Booster.craft`, whose pod tail is itself the stock
       Jumping Flea's (flown green by every GS-1 lane).
    2. seatExternalCmd (EAS-1 External Command Seat) SURFACE-attached on the pod's top,
       upright (identity rotation), at the pod's top stack node height. Its tail is
       AUTHORED with no MODULE block: KSP's craft loader instantiates every module from
       the part prefab and loads values only onto modules a MODULE node names
       (Part.LoadModule, decompiled), so its KerbalSeat and ModuleCommand load at their
       cfg defaults (the Hitchhiker's and the LABX lab's precedent, both flown).
  DERIVED: each PART's scalar header. The pod sits at pos (0,15,0) like the Jumping Flea
  and its two stack nodes stay free; the seat's pos is the pod's pos plus the pod's top
  node offset (+0.6423756, the Jumping Flea's own `attN = top` token). The seat's own
  srfAttach node offset is NOT in any KSP-written token in the corpus, so the builder
  takes it as 0 (`SEAT_ATTACH_OFFSET`): a visible gap or overlap between the pod's top and
  the seat base is that guess, and it is cosmetic (a surface attach joint is made at the
  recorded position, and same-vessel parts do not collide). The first flight confirms
  the craft stands and the seat is boardable.

THE SEATING (the lane depends on it). The VAB's default crew fill
(`KerbalRoster.DefaultCrewForVessel`, decompiled KSP 1.12.5) seats crew only into parts
with crew capacity, and the seat has none (a seat is boarded on EVA, never crewed in the
editor), so Jebediah Kerman (fresh-sandbox's first roster pilot) launches in the pod and
the seat launches empty. The lane takes him out through the hatch (`EvaExit`), seats him
(`EvaSeatBoard`) and makes him leave the seat (`EvaSeatLeave`).

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_seat_pad.py` runs it in-process.

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
DONOR_CRAFT = os.path.join(FIXTURES, "ships", "GS1 Auto-Chute Booster.craft")
OUT_DIR = os.path.join(SAVES, "seat-pad")

CRAFT_NAME = "SEAT1 Seat Pod"
CRAFT_REL = os.path.join("Ships", "VAB", CRAFT_NAME + ".craft")

BASE_TITLE = "fresh-sandbox (SANDBOX)"
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."
TITLE = "seat-pad (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: fresh-sandbox plus the "
               "SEAT1 Seat Pod craft in the VAB).")

LF = "\n"

# The donor part in GS1 Auto-Chute Booster.craft whose tail is lifted verbatim.
DONOR_POD = "mk1pod.v2_4200000001"

# Stack-node offsets (metres, part-local y), the Jumping Flea's own attN tokens.
POD_NODE_BOTTOM = -0.4050379
POD_NODE_TOP = 0.6423756
# The seat's srfAttach node offset along its local y: a GUESS (no KSP-written token).
SEAT_ATTACH_OFFSET = 0.0
# The seat's height above its attach point, for the craft `size` line only (cosmetic).
SEAT_HEIGHT_GUESS = 0.6

POD_Y = 15.0

# Identities: fixed, distinct, in a band no committed craft uses.
SHIP_PERSISTENT_ID = 2200240011
PART_UID = {"pod": 4260000001, "seat": 4260000002}
PERSISTENT_ID = {"pod": 1700240001, "seat": 1700240002}

SEAT_TAIL = [
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
    if v == 0:
        return "0"
    s = repr(round(v, 9))
    if "e" in s or "E" in s:
        s = ("%.9f" % v).rstrip("0").rstrip(".")
    if s.endswith(".0"):
        s = s[:-2]
    return "0" if s == "-0" else s


def _vec(x: float, y: float, z: float) -> str:
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


def donor_part_blocks(text: str) -> Dict[str, List[str]]:
    """Maps `part = <name>_<uid>` -> the PART block's body lines (between `{` and `}`)."""
    lines = text.split(LF)
    blocks: Dict[str, List[str]] = {}
    i = 0
    while i < len(lines):
        if lines[i] == "PART" and i + 1 < len(lines) and lines[i + 1] == "{":
            j = i + 2
            while lines[j] != "}":
                j += 1
            body = lines[i + 2:j]
            key = body[0].strip().partition(" = ")[2]
            blocks[key] = body
            i = j + 1
        else:
            i += 1
    return blocks


def tail_of(body: List[str]) -> List[str]:
    """The donor body from its first top-level EVENTS line on (verbatim)."""
    start = body.index("\tEVENTS")
    return body[start:]


def _header(name: str, uid: int, pid: int, pos: Tuple[float, float, float],
            att_pos0: Tuple[float, float, float], attm: int,
            links: List[str], att_n: List[str], srf_n: List[str]) -> List[str]:
    out = [
        "\tpart = %s_%d" % (name, uid),
        "\tpartName = Part",
        "\tpersistentId = %d" % pid,
        "\tpos = " + _vec(*pos),
        "\tattPos = 0,0,0",
        "\tattPos0 = " + _vec(*att_pos0),
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
        "\tattm = %d" % attm,
        "\tsameVesselCollision = False",
        "\tmodCost = 0",
        "\tmodMass = 0",
        "\tmodSize = 0,0,0",
    ]
    out.extend("\tlink = " + l for l in links)
    out.extend("\tattN = " + a for a in att_n)
    out.extend("\tsrfN = " + s for s in srf_n)
    return out


def build_craft() -> str:
    donors = donor_part_blocks(_read(DONOR_CRAFT))
    pod_tail = tail_of(donors[DONOR_POD])

    pod_id = "mk1pod.v2_%d" % PART_UID["pod"]
    seat_id = "seatExternalCmd_%d" % PART_UID["seat"]

    pod_y = POD_Y
    seat_rel_y = POD_NODE_TOP - SEAT_ATTACH_OFFSET
    seat_y = pod_y + seat_rel_y

    def free_node(nid, off):
        return "%s,Null_0_0|%s|0" % (nid, _fmt(off))

    pod = _header("mk1pod.v2", PART_UID["pod"], PERSISTENT_ID["pod"],
                  (0, pod_y, 0), (0, pod_y, 0), 0,
                  [seat_id],
                  [free_node("bottom", POD_NODE_BOTTOM),
                   free_node("top", POD_NODE_TOP)],
                  [])
    seat = _header("seatExternalCmd", PART_UID["seat"], PERSISTENT_ID["seat"],
                   (0, seat_y, 0), (0, seat_rel_y, 0), 1,
                   [], [],
                   ["srfAttach," + pod_id])

    height = (POD_NODE_TOP + SEAT_HEIGHT_GUESS) - POD_NODE_BOTTOM
    lines = [
        "ship = " + CRAFT_NAME,
        "version = 1.12.5",
        "description = SEAT1 Seat Pod. A Mk1 Command Pod with an EAS-1 External Command "
        "Seat on its top. The VAB crews the pod only; the lane takes the kerbal out on "
        "EVA, seats him and makes him leave the seat on the pad. Built by construction "
        "by harness/tools/build_seat_pad.py.",
        "type = VAB",
        "size = 1.25," + _fmt(round(height, 6)) + ",1.25",
        "steamPublishedFileId = 0",
        "persistentId = %d" % SHIP_PERSISTENT_ID,
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    for body in ((pod + pod_tail), (seat + SEAT_TAIL)):
        lines.append("PART")
        lines.append("{")
        lines.extend(body)
        lines.append("}")
    return LF.join(lines) + LF


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
    files[CRAFT_REL] = build_craft().encode("utf-8")
    return files


def verify(files: Dict[str, bytes]) -> List[str]:
    problems: List[str] = []
    craft = files[CRAFT_REL].decode("utf-8")
    parts = [l.strip().partition(" = ")[2] for l in craft.split(LF) if l.startswith("\tpart = ")]
    if [p.rsplit("_", 1)[0] for p in parts] != ["mk1pod.v2", "seatExternalCmd"]:
        problems.append("craft parts must be [mk1pod.v2, seatExternalCmd], got %r" % parts)
    seat = craft.split("\tpart = seatExternalCmd_", 1)[1]
    if "\tMODULE" in seat:
        problems.append("the authored seatExternalCmd tail must carry no MODULE block")
    if "\tsrfN = srfAttach,mk1pod.v2_" not in seat or "\tattm = 1\n" not in seat:
        problems.append("the seat must be surface-attached to the pod")
    pod = craft.split("\tpart = mk1pod.v2_", 1)[1].split("\tpart = seatExternalCmd_", 1)[0]
    if "name = ModuleCommand" not in pod:
        problems.append("the pod tail must carry ModuleCommand")
    if craft.count("name = ModuleCommand") != 1:
        problems.append("exactly one ModuleCommand node aboard (the pod's; the seat's loads from its prefab)")
    if "name = TrajectoriesVesselSettings" in craft:
        problems.append("no modded module may ride the stock-minimal craft")
    if not craft.isascii():
        problems.append("craft must be ASCII")
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
