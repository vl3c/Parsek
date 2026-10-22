#!/usr/bin/env python3
"""Build the committed `hitchhiker-pad` fixture BY CONSTRUCTION (the HH-1 host).

WHY THIS FIXTURE EXISTS. SPLIT-OF-A-CREWED-COMMANDLESS-PART-READS-AS-DEBRIS: a crewed
part with no command module (a PPD-10 Hitchhiker, a Mk1 crew cabin) that leaves the
recorded vessel with a kerbal inside fails `ParsekFlight.IsTrackableVessel`, so Parsek
treats it as debris and the kerbal can read as lost. No committed fixture or craft
carries a Hitchhiker (the only `crewCabin` lines in the corpus are tech-tree
`part = crewCabin` entries), so the lane needs a craft that has one, launched from the
VAB onto an empty pad.

WHAT IS COPIED AND WHAT IS DERIVED.
  BASE, verbatim apart from the edits below: `fixtures/saves/fresh-sandbox` (SANDBOX, no
  vessels, no Parsek footprint, so `LoadGame` takes the no-vessel Space Center route and
  the pad is free for `GoToEditor` + `LaunchFromEditor`).
  EDITED: the save `Title` / `Description` (so the Load menu names the fixture).
  ADDED: `Ships/VAB/HH1 Hitchhiker Drop.craft`, three parts stacked root first:
    1. mk1-3pod (the root and the only control source, three seats). Its tail (EVENTS /
       ACTIONS / PARTDATA / MODULE* / RESOURCE*) is lifted byte for byte from the
       committed stock `Kerbal X.craft` (KSP 1.12.5 wrote it).
    2. Decoupler.2 (TD-25, 2.5 m), tail lifted the same way from Kerbal X's upper TD-25.
    3. crewCabin (PPD-10 Hitchhiker, four seats, NO ModuleCommand) under the decoupler.
       Its tail is AUTHORED with no MODULE block: KSP's craft loader instantiates every
       module from the part prefab and loads values only onto modules a MODULE node
       names (Part.LoadModule, decompiled), so a part with no MODULE nodes loads with
       every module at its cfg default (the LABX lab's precedent, flown).
  DERIVED: each PART's scalar header. Stack arithmetic is GS-1's validated formula
      child.pos = parent.pos + parent_node_offset - child_opposite_node_offset
  with the pod's bottom (-0.47924) and the TD-25's top / bottom (+-0.1) node offsets
  read off Kerbal X's own PIPE-FORMAT attN tokens. The Hitchhiker's top / bottom offsets
  (`CABIN_NODE_TOP` / `CABIN_NODE_BOTTOM`) come from its part cfg (hitchikerStorageContainer.cfg
  node_stack_top / node_stack_bottom); no KSP-written craft token exists for it. A
  visible gap or overlap between the TD-25 and the cabin is that number, and it is
  cosmetic (the joint is made from the node pair, not from the gap).

THE SEATING (the lane depends on it). The VAB's default crew fill
(`KerbalRoster.DefaultCrewForVessel`, decompiled KSP 1.12.5) seats only parts that are
control sources, so the pod takes three kerbals and the Hitchhiker launches EMPTY. The
lane then moves one kerbal into the cabin with the stock crew transfer (the `CrewTransfer`
seam verb), the move a player makes from the pod's right-click menu.

THE SEPARATION. `PartAction button=decouple part=Decoupler.2` fires the TD-25. The TD-25
stays on the pod (its parent side), the Hitchhiker leaves under a fresh pid with its
kerbal inside, and the pod keeps the root and the focus. On the pad the pod hops a few
centimetres and settles back onto the cabin.

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_hitchhiker_pad.py` runs it in-process.

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
DONOR_CRAFT = os.path.join(FIXTURES, "ships", "Kerbal X.craft")
OUT_DIR = os.path.join(SAVES, "hitchhiker-pad")

CRAFT_NAME = "HH1 Hitchhiker Drop"
CRAFT_REL = os.path.join("Ships", "VAB", CRAFT_NAME + ".craft")

BASE_TITLE = "fresh-sandbox (SANDBOX)"
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."
TITLE = "hitchhiker-pad (SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: fresh-sandbox plus the "
               "HH1 Hitchhiker Drop craft in the VAB).")

LF = "\n"

# The donor parts in Kerbal X.craft whose tails are lifted verbatim.
DONOR_POD = "mk1-3pod_4294320818"
DONOR_DECOUPLER = "Decoupler.2_4294282032"

# Stack-node offsets (metres, part-local y). Pod and TD-25 from Kerbal X's attN tokens.
POD_NODE_BOTTOM = -0.47924
POD_NODE_TOP = 1.19318998
DECOUPLER_NODE_TOP = 0.100000001
DECOUPLER_NODE_BOTTOM = -0.100000001
# hitchikerStorageContainer.cfg (KSP 1.12.5, part crewCabin) node_stack_top / node_stack_bottom, float32 form.
CABIN_NODE_TOP = 0.986899018
CABIN_NODE_BOTTOM = -0.986899018

POD_Y = 15.0

# Identities: fixed, distinct, in the 0x8xxxxxxx band no stock craft in the corpus uses.
SHIP_PERSISTENT_ID = 2200220011
PART_UID = {"pod": 4250000001, "decoupler": 4250000002, "cabin": 4250000003}
PERSISTENT_ID = {"pod": 1700220001, "decoupler": 1700220002, "cabin": 1700220003}

CABIN_TAIL = [
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
            att_pos0: Tuple[float, float, float], istg: int, dstg: int, sidx: int,
            sqor: int, sep_i: int, links: List[str], att_n: List[str]) -> List[str]:
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
        "\tistg = %d" % istg,
        "\tresPri = 0",
        "\tdstg = %d" % dstg,
        "\tsidx = %d" % sidx,
        "\tsqor = %d" % sqor,
        "\tsepI = %d" % sep_i,
        "\tattm = 0",
        "\tsameVesselCollision = False",
        "\tmodCost = 0",
        "\tmodMass = 0",
        "\tmodSize = 0,0,0",
    ]
    out.extend("\tlink = " + l for l in links)
    out.extend("\tattN = " + a for a in att_n)
    return out


def build_craft() -> str:
    donors = donor_part_blocks(_read(DONOR_CRAFT))
    pod_tail = tail_of(donors[DONOR_POD])
    dec_tail = tail_of(donors[DONOR_DECOUPLER])

    pod_id = "mk1-3pod_%d" % PART_UID["pod"]
    dec_id = "Decoupler.2_%d" % PART_UID["decoupler"]
    cab_id = "crewCabin_%d" % PART_UID["cabin"]

    pod_y = POD_Y
    dec_y = pod_y + POD_NODE_BOTTOM - DECOUPLER_NODE_TOP
    cab_y = dec_y + DECOUPLER_NODE_BOTTOM - CABIN_NODE_TOP

    def node(nid, other, off):
        return "%s,%s_0|%s|0" % (nid, other, _fmt(off))

    pod = _header("mk1-3pod", PART_UID["pod"], PERSISTENT_ID["pod"],
                  (0, pod_y, 0), (0, pod_y, 0), -1, 0, -1, -1, -1,
                  [dec_id],
                  [node("bottom", dec_id, POD_NODE_BOTTOM),
                   node("top", "Null_0", POD_NODE_TOP)])
    dec = _header("Decoupler.2", PART_UID["decoupler"], PERSISTENT_ID["decoupler"],
                  (0, dec_y, 0), (0, dec_y - pod_y, 0), 0, 1, 0, 0, 0,
                  [cab_id],
                  [node("top", pod_id, DECOUPLER_NODE_TOP),
                   node("bottom", cab_id, DECOUPLER_NODE_BOTTOM)])
    cab = _header("crewCabin", PART_UID["cabin"], PERSISTENT_ID["cabin"],
                  (0, cab_y, 0), (0, cab_y - dec_y, 0), -1, 0, -1, -1, -1,
                  [],
                  [node("top", dec_id, CABIN_NODE_TOP),
                   node("bottom", "Null_0", CABIN_NODE_BOTTOM)])

    top = pod_y + POD_NODE_TOP
    bottom = cab_y + CABIN_NODE_BOTTOM
    lines = [
        "ship = " + CRAFT_NAME,
        "version = 1.12.5",
        "description = HH1 Hitchhiker Drop. A Mk1-3 pod over a TD-25 decoupler over a "
        "PPD-10 Hitchhiker crew cabin (four seats, no command module). The VAB seats the "
        "pod only; the lane moves one kerbal into the cabin and decouples it on the pad. "
        "Built by construction by harness/tools/build_hitchhiker_pad.py.",
        "type = VAB",
        "size = 2.5," + _fmt(round(top - bottom, 6)) + ",2.5",
        "steamPublishedFileId = 0",
        "persistentId = %d" % SHIP_PERSISTENT_ID,
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    for body in ((pod + pod_tail), (dec + dec_tail), (cab + CABIN_TAIL)):
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
    if [p.rsplit("_", 1)[0] for p in parts] != ["mk1-3pod", "Decoupler.2", "crewCabin"]:
        problems.append("craft parts must be [mk1-3pod, Decoupler.2, crewCabin], got %r" % parts)
    cabin = craft.split("\tpart = crewCabin_", 1)[1]
    if "\tMODULE" in cabin:
        problems.append("the authored crewCabin tail must carry no MODULE block")
    pod = craft.split("\tpart = mk1-3pod_", 1)[1].split("\tpart = Decoupler.2_", 1)[0]
    if "name = ModuleCommand" not in pod:
        problems.append("the pod tail must carry ModuleCommand (the only control source)")
    if craft.count("name = ModuleCommand") != 1:
        problems.append("exactly one ModuleCommand aboard (the pod)")
    if "name = ModuleDecouple" not in craft:
        problems.append("the TD-25 tail must carry ModuleDecouple")
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
