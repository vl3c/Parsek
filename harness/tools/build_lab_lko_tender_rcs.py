#!/usr/bin/env python3
"""Build the committed `lab-lko-tender-rcs` fixture BY CONSTRUCTION.

WHY THIS FIXTURE EXISTS. The science-lab plan's LAB-13 is a TRUE dock / undock pair: the probe
tender separates from the LABX Orbital Lab, docks back, the lab processes an experiment while
docked, the pair undocks (onVesselsUndocking, an Undock branch) and the lab transmits. The
reduced `LAB-13-lab-process-separate-transmit` could not fly the re-dock: docking in the corpus
is MechJeb's docking autopilot (mission `d5_redock`, the B-DOCK DOCK phase), which translates
on RCS, and neither the lab nor the tender (an OKTO, a Z-200 and a Jr port, no engine) carries
any. `LAB-13b-lab-dock-undock` flies the true pair on this sibling: the tender of
`lab-lko-seeded-transmit` with RCS.

WHY A SIBLING RATHER THAN A CRAFT REVISION. Revising the LABX craft itself means a LABX-1
re-flight and a re-harvest of `lab-lko-sandbox`, which ripples through every derived lab fixture
and the lanes that fly them. This builder touches none of them: it derives a new save from the
committed `lab-lko-seeded-transmit`, and existing lanes keep their bytes.

WHAT IS SPLICED. Six `PART` nodes appended to the single `VESSEL`, AFTER the existing 26 so no
index moves, every one surface-attached (`attm = 1`, `srfN = srfAttach, 6`) to part 6, the
tender's OKTO (`probeCoreOcto.v2`, 0.625 m across):

  26-29  `RCSBlock.v2` (RV-105) x4, a Radial symmetry group at the OKTO's own height (y =
         1.1242809295654297 in the vessel frame, about the tender's centre of mass: the OKTO is
         0.1 t of the bare tender's 0.13 t) at 0 / 90 / 180 / 270 degrees about the stack axis.
         Poses are the donor's ring (`bdock-recorded`'s four blocks on a 2.5 m tank, radius
         1.1995 against the tank's 1.25: inset 0.0505) moved to the OKTO's radius 0.3125 at the
         same inset (radius 0.262); the rotations are the donor's four, written clean.
  30-31  `radialRCSTank` (Stratus-V roundified, 20 MonoPropellant each, full) x2, a Radial pair
         at 45 / 225 degrees, between the blocks. The donor (`miner-rover-full`, on a 1.25 m
         tank) sits 0.2179 beyond the tank surface, so here at radius 0.5304. A PAIR, not one:
         one radial tank (0.1 t full) would put the 0.37 t tender's centre of mass 0.14 m off
         the stack axis, and the docking autopilot's translation would then torque the craft;
         the pair keeps it on the axis. 40 MonoPropellant is far more than a short re-dock
         needs (Isp 240 s: over 400 m/s on a 0.49 t tender).

Each spliced part's body (EVENTS onward: ACTIONS, PARTDATA, every MODULE and RESOURCE) is the
donor's byte for byte, except that the tank gains the `KOSNameTag` module every part of this
save carries (kOS is installed on the instance that flew LABX-1), copied from the RCS donor's.
Each header is written here field by field in the donor's key order, with fresh identities
(`cid`, `uid` and `persistentId` asserted absent from the base save), the OKTO's `mid` /
`launchID` / temperatures, the tender's staging values (`istg = -1`, `dstg = 0`, `sepI = 1`),
and the donor's `mass`, `expt`, `rTrf` and `moduleVariantName`. Both part names are researched
in the base (`advFlightControl`, state Available), asserted below.

ONE VESSEL EDIT beyond the splice: the base's RCS action group reads ON (`RCS = True,
323.651533203074`, left by the LABX-1 ascent; inert there, since the base carries no RCS
thruster). It is turned OFF here (`RCS = False`, the toggle time kept), so the new blocks do
not spend the tender's monopropellant on SAS corrections while the pair is joined; the mission
turns RCS on for the tender alone.

WHAT IS NOT TOUCHED. Every other line of the save is `lab-lko-seeded-transmit`'s: the orbit,
the lab holding 12.5 science and no data, the two PreAttached Jr ports joining the tender, the
crew, the inert ParsekScenario node. Edited in the save: the `Title` and `Description`. `persistent.loadmeta` and
`AddOns/` are copied byte for byte (loadmeta counts vessels, not parts).

`--check` re-derives and compares every file against the committed bytes;
`harness/lib/test_lab_lko_tender_rcs.py` runs it in-process. The donors are read at build time,
so a re-harvest of `bdock-recorded` or `miner-rover-full` reds the check: re-run this builder.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import shutil
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_NAME = "lab-lko-seeded-transmit"
BASE_DIR = os.path.join(SAVES, BASE_NAME)
NAME = "lab-lko-tender-rcs"

RCS_DONOR = ("bdock-recorded", "RCSBlock.v2")
TANK_DONOR = ("miner-rover-full", "radialRCSTank")

CRLF = "\r\n"
LF = "\n"
EDITED = ("persistent.sfs",)

VESSEL_NAME = "LABX Orbital Lab"
BASE_TITLE = "lab-lko-seeded-transmit (SCIENCE_SANDBOX)"
BASE_DESCRIPTION = ("Parsek autotest fixture (file-constructed: the LABX Orbital Lab in low "
                    "Kerbin orbit with its probe tender joined, its lab holding 12.5 science "
                    "and no data).")
TITLE = "lab-lko-tender-rcs (SCIENCE_SANDBOX)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: the LABX Orbital Lab in low Kerbin "
               "orbit with its probe tender joined, the tender carrying four RCS blocks and two "
               "monopropellant tanks, its lab holding 12.5 science and no data).")

BASE_PART_COUNT = 26
OKTO_INDEX = 6
OKTO_NAME = "probeCoreOcto.v2"
# The OKTO's own height in the vessel frame, copied as KSP wrote it.
RING_Y = "1.1242809295654297"
RCS_RADIUS = "0.262"
TANK_RADIUS = "0.5304"
TANK_DIAG = "0.375049"          # 0.5304 / sqrt(2), rounded to 6 places
RESEARCH_NODE = "advFlightControl"
RCS_GROUP_ON = "\t\t\t\tRCS = True, 323.651533203074"
RCS_GROUP_OFF = "\t\t\t\tRCS = False, 323.651533203074"

_PART = "\t\t\tPART"
_P = "\t\t\t\t"
_MODULE = _P + "MODULE"


class Splice(object):
    def __init__(self, donor, cid, uid, pid, position, rotation, sym):
        self.donor = donor
        self.cid = cid
        self.uid = uid
        self.pid = pid
        self.position = position
        self.rotation = rotation
        self.sym = sym


# Indices 26..31. `sym` lists the OTHER members of the part's symmetry group by vessel index.
SPLICES: Tuple[Splice, ...] = (
    Splice(RCS_DONOR, "4260000030", "1904300026", "1700300026",
           "-%s,%s,0" % (RCS_RADIUS, RING_Y), "0,0,0,1", (27, 28, 29)),
    Splice(RCS_DONOR, "4260000031", "1904300027", "1700300027",
           "0,%s,%s" % (RING_Y, RCS_RADIUS), "0,0.707106829,0,0.707106829", (26, 28, 29)),
    Splice(RCS_DONOR, "4260000032", "1904300028", "1700300028",
           "%s,%s,0" % (RCS_RADIUS, RING_Y), "0,1,0,0", (26, 27, 29)),
    Splice(RCS_DONOR, "4260000033", "1904300029", "1700300029",
           "0,%s,-%s" % (RING_Y, RCS_RADIUS), "0,0.707106829,0,-0.707106829", (26, 27, 28)),
    # The tank's identity rotation attaches it on the -z side (the donor's pose: azimuth -150
    # degrees from +x with a +60 degree yaw), so 45 and 225 degrees between the blocks are
    # yaws of 135 and 315 degrees about +Y.
    Splice(TANK_DONOR, "4260000034", "1904300030", "1700300030",
           "-%s,%s,%s" % (TANK_DIAG, RING_Y, TANK_DIAG), "0,0.923879504,0,0.382683456", (31,)),
    Splice(TANK_DONOR, "4260000035", "1904300031", "1700300031",
           "%s,%s,-%s" % (TANK_DIAG, RING_Y, TANK_DIAG), "0,0.382683456,0,-0.923879504", (30,)),
)


def _load_seeded_builder():
    path = os.path.join(_HERE, "build_lab_seeded_variants.py")
    spec = importlib.util.spec_from_file_location("build_lab_seeded_variants", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_SEEDED = _load_seeded_builder()
vessel_blocks = _SEEDED.vessel_blocks
_block_end = _SEEDED._block_end


def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _lines(name: str) -> List[str]:
    return _read(os.path.join(SAVES, name, "persistent.sfs")).replace(CRLF, LF).split(LF)


def part_ranges(vessel: List[str]) -> List[Tuple[int, int]]:
    """(start, end) of every direct PART child of a VESSEL block, in order."""
    out = []
    i = 0
    while i < len(vessel):
        if vessel[i] == _PART:
            end = _block_end(vessel, i)
            out.append((i, end))
            i = end
        else:
            i += 1
    return out


def part_name(vessel: List[str], start: int) -> str:
    line = vessel[start + 2]
    assert line.startswith(_P + "name = "), line
    return line[len(_P + "name = "):]


def _header_value(block: List[str], key: str) -> str:
    hits = [l for l in block if l.startswith(_P + key + " = ")]
    assert len(hits) == 1, "part key %s found %d times" % (key, len(hits))
    return hits[0][len(_P + key + " = "):]


def donor_part(donor: Tuple[str, str]) -> List[str]:
    """The donor save's FIRST PART node named donor[1], as lines (header to close brace)."""
    save, name = donor
    lines = _lines(save)
    for i, line in enumerate(lines):
        if line == _PART and lines[i + 2] == _P + "name = " + name:
            return lines[i:_block_end(lines, i)]
    raise AssertionError("no %s part in %s" % (name, save))


def split_part(block: List[str]) -> Tuple[List[str], List[str]]:
    """(header value lines, body) of a PART block: the header is every line between the
    opening brace and EVENTS, the body EVENTS through the line before the closing brace."""
    assert block[0] == _PART and block[1] == "\t\t\t{" and block[-1] == "\t\t\t}"
    ev = block.index(_P + "EVENTS")
    return block[2:ev], block[ev:-1]


def header_keys(header: List[str]) -> List[str]:
    return [l.strip("\t").split(" = ", 1)[0] for l in header]


def kos_name_tag_module(body: List[str]) -> List[str]:
    mods = [i for i, l in enumerate(body)
            if l == _MODULE and body[i + 2] == _P + "\tname = KOSNameTag"]
    assert len(mods) == 1, "KOSNameTag modules %d" % len(mods)
    return body[mods[0]:_block_end(body, mods[0])]


def tank_body(body: List[str], kos: List[str]) -> List[str]:
    """The tank donor's body with the KOSNameTag module after its last MODULE."""
    assert not any(l == _P + "\tname = KOSNameTag" for l in body), "donor already has kOS"
    mods = [i for i, l in enumerate(body) if l == _MODULE]
    assert mods, "the tank donor has no module"
    at = _block_end(body, mods[-1])
    assert body[at] == _P + "RESOURCE", "the tank's MonoPropellant follows its modules"
    return body[:at] + kos + body[at:]


def render_part(spec: Splice, donor_header: List[str], body: List[str],
                okto: Dict[str, str]) -> List[str]:
    """One spliced PART block: a header in the donor's key order, then the body."""
    values = {
        "name": spec.donor[1],
        "cid": spec.cid,
        "uid": spec.uid,
        "mid": okto["mid"],
        "persistentId": spec.pid,
        "launchID": okto["launchID"],
        "parent": str(OKTO_INDEX),
        "position": spec.position,
        "rotation": spec.rotation,
        "mirror": "1,1,1",
        "symMethod": "Radial",
        "istg": "-1",
        "resPri": "0",
        "dstg": "0",
        "sqor": "-1",
        "sepI": "1",
        "sidx": "-1",
        "attm": "1",
        "sameVesselCollision": "False",
        "srfN": "srfAttach, %d" % OKTO_INDEX,
        "mass": _header_value(donor_header, "mass"),
        "shielded": "False",
        "temp": okto["temp"],
        "tempExt": okto["tempExt"],
        "tempExtUnexp": okto["tempExtUnexp"],
        "staticPressureAtm": "0",
        "expt": _header_value(donor_header, "expt"),
        "state": "0",
        "PreFailState": "0",
        "attached": "True",
        "autostrutMode": "Off",
        "rigidAttachment": "False",
        "flag": "",
        "rTrf": _header_value(donor_header, "rTrf"),
        "modCost": "0",
        "modMass": "0",
        "moduleVariantName": _header_value(donor_header, "moduleVariantName"),
        "moduleCargoStackableQuantity": "1",
    }
    keys = [k for k in header_keys(donor_header) if k != "sym"]
    assert sorted(keys) == sorted(values), (
        "the donor's header keys moved: %s" % sorted(set(keys) ^ set(values)))
    out = [_PART, "\t\t\t{"]
    for key in keys:
        if key == "srfN":
            out.extend(_P + "sym = %d" % s for s in spec.sym)
        out.append(_P + key + " = " + values[key])
    out.extend(body)
    out.append("\t\t\t}")
    return out


def splice_tender(vessel: List[str]) -> List[str]:
    parts = part_ranges(vessel)
    assert len(parts) == BASE_PART_COUNT, "base parts %d" % len(parts)
    okto_start, okto_end = parts[OKTO_INDEX]
    assert part_name(vessel, okto_start) == OKTO_NAME
    okto_block = vessel[okto_start:okto_end]
    okto = {k: _header_value(okto_block, k)
            for k in ("mid", "launchID", "temp", "tempExt", "tempExtUnexp")}
    assert _header_value(okto_block, "parent") == "5", "the OKTO hangs off the tender's port"

    rcs_header, rcs_body = split_part(donor_part(RCS_DONOR))
    kos = kos_name_tag_module(rcs_body)
    tank_header, tank_raw = split_part(donor_part(TANK_DONOR))
    bodies = {RCS_DONOR: (rcs_header, rcs_body),
              TANK_DONOR: (tank_header, tank_body(tank_raw, kos))}
    assert any(l == _P + "\tname = MonoPropellant" for l in bodies[TANK_DONOR][1])
    assert _P + "\tamount = 20" in bodies[TANK_DONOR][1], "the tank donor is full"
    assert _P + "\tmaxAmount = 20" in bodies[TANK_DONOR][1]

    added: List[str] = []
    for spec in SPLICES:
        header, body = bodies[spec.donor]
        added.extend(render_part(spec, header, body, okto))
    at = parts[-1][1]
    out = vessel[:at] + added + vessel[at:]
    groups = [i for i, l in enumerate(out) if l == RCS_GROUP_ON]
    assert len(groups) == 1, "the base's RCS action group must read ON once"
    out[groups[0]] = RCS_GROUP_OFF
    return out


def build_sfs() -> str:
    base = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    for spec in SPLICES:
        for ident in (spec.cid, spec.uid, spec.pid):
            assert ident not in base, "identity %s already in the base save" % ident
    lines = base.replace(CRLF, LF).split(LF)
    blocks = vessel_blocks(lines)
    assert [b[2] for b in blocks] == [VESSEL_NAME], [b[2] for b in blocks]
    v_start, v_end, _ = blocks[0]
    vessel = lines[v_start:v_end]
    assert "\t\t\tsit = ORBITING" in vessel, "the base vessel is ORBITING"
    assert len([l for l in vessel if l == _P + "name = dockingPort3"]) == 2, \
        "the tender's two Jr ports must be aboard"
    lines[v_start:v_end] = splice_tender(vessel)
    for tech_part in (RCS_DONOR[1], TANK_DONOR[1]):
        assert tech_part in researched_parts(lines, RESEARCH_NODE), tech_part
    assert "\t\tactiveVessel = 0" in lines, "the lab stays the active vessel"
    lines[lines.index("\tTitle = " + BASE_TITLE)] = "\tTitle = " + TITLE
    lines[lines.index("\tDescription = " + BASE_DESCRIPTION)] = "\tDescription = " + DESCRIPTION
    return CRLF.join(lines)


def researched_parts(lines: List[str], node_id: str) -> List[str]:
    """The `part = ` values of the R&D Tech node `node_id`, which must read Available."""
    hits = [i for i, l in enumerate(lines)
            if l == "\t\tTech" and lines[i + 2] == "\t\t\tid = " + node_id]
    assert len(hits) == 1, "Tech %s nodes %d" % (node_id, len(hits))
    block = lines[hits[0]:_block_end(lines, hits[0])]
    assert "\t\t\tstate = Available" in block, "%s is not researched" % node_id
    return [l[len("\t\t\tpart = "):] for l in block if l.startswith("\t\t\tpart = ")]


def expected_files() -> Dict[str, bytes]:
    files = {}
    for root, _dirs, names in os.walk(BASE_DIR):
        for name in names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, BASE_DIR).replace(os.sep, "/")
            if rel in EDITED:
                continue
            files[rel] = _read_bytes(path)
    files["persistent.sfs"] = build_sfs().encode("utf-8")
    return files


def out_dir() -> str:
    return os.path.join(SAVES, NAME)


def committed_problems() -> List[str]:
    want = expected_files()
    have = {}
    target = out_dir()
    if os.path.isdir(target):
        for root, _dirs, names in os.walk(target):
            for name in names:
                path = os.path.join(root, name)
                have[os.path.relpath(path, target).replace(os.sep, "/")] = _read_bytes(path)
    problems = []
    for rel in sorted(set(want) | set(have)):
        if rel not in have:
            problems.append("%s: missing %s" % (NAME, rel))
        elif rel not in want:
            problems.append("%s: unexpected %s" % (NAME, rel))
        elif have[rel] != want[rel]:
            problems.append("%s: drift in %s" % (NAME, rel))
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
    target = out_dir()
    if os.path.isdir(target):
        shutil.rmtree(target)
    for rel, data in expected_files().items():
        path = os.path.join(target, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    print("wrote " + target)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
