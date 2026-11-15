#!/usr/bin/env python3
"""Build the four committed Klaw / asteroid fixtures BY CONSTRUCTION.

WHY THESE FIXTURES EXIST. The asteroid / comet lanes (AST-1, AST-2, AST-3, AST-4, COM-1;
owner rulings V 2026-11-15) need a live Klaw grab of a real rock in orbit: a rock is not
recorded before the grab, the grab is a Dock merge of connection kind Grapple, a rewind
claims and hold-ghosts the rock, a released rock gets its own recording and no Re-Fly
slot. No committed fixture carries a Klaw (`GrapplingDevice`) at all, and none parks a
craft beside a rock, so every lane needs a new host. Flying a probe from the pad to a
rendezvous with a stock asteroid would cost a whole ascent plus a rendezvous per run;
these saves start the lane already parked.

THE BASE. `lab-lko-sandbox` (the LABX-1 harvest): SANDBOX, KSP 1.12.5, the inert
ParsekScenario node, the LABX Orbital Lab ORBITING Kerbin at about 100 km (ORBIT SMA
699745.83, ECC 0.0104, EPH = the save's UT 355.83) and one untracked asteroid, `Ast.
VPA-167`, parked around the Sun. Every vessel of the base is replaced; everything else is
the base's, with five edits listed below.

WHAT EACH FIXTURE CARRIES (all vessels share the LABX's ORBIT; only MNA differs, offset
by along-track distance / SMA radians, so the vessels sit on one track and stay put
relative to each other to within the orbit's 1 percent eccentricity):

  ast-klaw-lko        `AST Klaw` (active) at 0 m, `Ast. VPA-167` 60 m ahead,
                      `AST Watcher` 300 m behind (the post-rewind FLIGHT focus).  AST-1, AST-2
  com-klaw-lko        the same with the base comet `UnknownComet` of
                      `depot-route-recorded` 150 m ahead (its nucleus radius is 44.47 m).  COM-1
  ast-miner-lko       `AST Miner` (the Klaw probe plus a MiniDrill, a RadialOreTank and two
                      Z-400 packs) at 0 m, the rock 60 m ahead, `AST Depot` (an OKTO, a
                      Jr port facing the miner track, an empty RadialOreTank) 150 m behind. AST-3
  ast-mothership-lko  `AST Klaw` = the Klaw probe with a mothership (Jr port, OKTO, Z-200)
                      PreAttached under its bottom Jr port, the rock 100 m ahead.          AST-4

THE KLAW PROBE (root = an OKTO, frame = the OKTO's, +Y forward): an OKTO
(`probeCoreOcto.v2`), a Z-200 (`batteryBankMini`) stacked above it, the Klaw
(`GrapplingDevice`) surface-attached on the battery's top face pointing +Y, a Jr port
(`dockingPort3`) stacked under the OKTO facing -Y, four RV-105 blocks (`RCSBlock.v2`) and
two Stratus-V radial tanks (`radialRCSTank`, 20 MonoPropellant each) on the OKTO at the
ring poses `build_lab_lko_tender_rcs.py` derived for the same OKTO (radius 0.262 / 0.5304,
the donor rotations). The OKTO's own reaction wheel is the only torque source; no solar
panel (the Z-200 plus the OKTO's 50 EC cover a lane of under an hour at the OKTO's draw).

SOURCED PART TEXT. Every part body (EVENTS onward) is a donor's, byte for byte, re-indented
nowhere (all donors sit at the same depth): `probeCoreOcto.v2`, `batteryBankMini`,
`dockingPort3` from `lab-lko-sandbox`; `RCSBlock.v2` from `bdock-recorded`; `radialRCSTank`,
`MiniDrill`, `RadialOreTank` from `miner-rover-full`; `ksp.r.largeBatteryPack` from
`lab-lko-sandbox`. Edits inside a body: a Jr port's ModuleDockingNode `state` (Ready for a
free port, PreAttached for the AST-4 pair) and an ore tank's Ore `amount` (0). The rocks are
the stock-written unloaded vessels (no MODULE node on the PotatoRoid / PotatoComet PART, the
shape stock writes for every asteroid it has never loaded) with ORBIT replaced and the
DISCOVERY node edited.

INVENTED, NOT SOURCED FROM ANY STOCK OUTPUT (no committed save or craft carries a Klaw):
  * the whole `GrapplingDevice` PART header: mass 0.15 (the stock part.cfg value as
    remembered, unverified), expt 0.5, rTrf GrapplingDevice, surface attachment
    (`attm = 1`, `srfN = srfAttach, <battery>`) because the Klaw's stack node names are
    unknown here, and its position (0.29 m above the battery's centre);
  * the Klaw's body: EVENTS / ACTIONS / PARTDATA empty and ONE MODULE node,
    ModuleGrappleNode with `state = Disabled` (the arm animation keeps its prefab default:
    disarmed). The node is required: ModuleGrappleNode creates its grapple AttachNode only
    in OnLoad, which stock calls only for a saved module, and the first flight (2026-11-15,
    a module-less Klaw) threw a NullReferenceException from its FSM every frame and could
    never grab;
  * a TARGET node on the Klaw-carrying vessel naming the rock (type Vessel, the rock's
    `pid` as a hyphenated Guid), which stock restores on the active vessel at load. kRPC's
    `SpaceCenter.vessels` omits SpaceObject vessels, so the mission reaches the rock only
    through `sc.target_vessel`;
  * the comet's `CometVessel` `hasName = True` (the donor's `False` makes stock generate a
    random name at every load);
  * the rock DISCOVERY edits: `state = 29` (fully discovered, unowned), `lifetime` and
    `refTime` = Infinity (the owned-vessel spelling stock writes), `size = 0` (class A) for
    the asteroid; the comet keeps its donor `size = 5` and nucleus radius (no small-class
    comet exists in any committed save, and no radius was invented);
  * every non-donor position: the Klaw, the Z-200 above the OKTO (the tender's own 0.287
    offset), the Jr ports (0.187 below / above an OKTO, 0.295 port to port as in the
    LABX), the drill / ore tank / packs on the OKTO's side at radius 0.3 to 0.45, the
    mothership stack; radial rotations other than the RCS ring are identity or the donor's.
    Positions inside one vessel never collide (same-vessel collisions are off) and only the
    Klaw's (it must be the forward-most point) and the RCS ring's (thrust axes) matter;
  * identities: cid / uid / persistentId / vessel pid Guid / launchID, asserted absent from
    the base and every donor.

SAVE-LEVEL EDITS (beyond FLIGHTSTATE): `Title` / `Description`; `EnableCommNet = False` (an
OKTO's internal antenna loses KSC behind Kerbin, and an unconnected probe cannot translate on
RCS); `EnableFullSASInSandbox = True` (the approach holds SAS Target mode, which an OKTO
cannot select otherwise); the two crew the removed LABX carried (Bob, Valentina) set
`Available` with `idx = -1`; `activeVessel` points at the Klaw probe / miner.
`persistent.loadmeta` is the base's with `vesselCount` = the non-SpaceObject vessel count.
`AddOns/` is copied byte for byte.

`--check` re-derives every file and compares it with the committed bytes;
`harness/lib/test_ast_klaw_fixtures.py` runs it in-process and pins the shapes the lanes read.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sys
from typing import Dict, List, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(HARNESS, "fixtures", "saves")
BASE_NAME = "lab-lko-sandbox"
BASE_DIR = os.path.join(SAVES, BASE_NAME)

LF = "\n"
CRLF = "\r\n"
_V = "\t\t"
_VK = "\t\t\t"
_PART = "\t\t\tPART"
_P = "\t\t\t\t"

BASE_TITLE = "lab-lko-sandbox (SANDBOX)"
BASE_ROCK_NAME = "Ast. VPA-167"
BASE_SHIP_NAME = "LABX Orbital Lab"
COMET_DONOR = ("depot-route-recorded", "UnknownComet")

# Donors: (save, part name) -> the FIRST PART of that name in the save.
D_OKTO = (BASE_NAME, "probeCoreOcto.v2")
D_Z200 = (BASE_NAME, "batteryBankMini")
D_JR = (BASE_NAME, "dockingPort3")
D_Z400 = (BASE_NAME, "ksp.r.largeBatteryPack")
D_RCS = ("bdock-recorded", "RCSBlock.v2")
D_MONO = ("miner-rover-full", "radialRCSTank")
D_DRILL = ("miner-rover-full", "MiniDrill")
D_ORE = ("miner-rover-full", "RadialOreTank")
KLAW = "GrapplingDevice"

# Fixture names.
AST_KLAW = "ast-klaw-lko"
COM_KLAW = "com-klaw-lko"
AST_MINER = "ast-miner-lko"
AST_MOTHERSHIP = "ast-mothership-lko"
FIXTURES = (AST_KLAW, COM_KLAW, AST_MINER, AST_MOTHERSHIP)

# Vessel names and persistentIds the specs name.
KLAW_NAME = "AST Klaw"
MINER_NAME = "AST Miner"
WATCHER_NAME = "AST Watcher"
DEPOT_NAME = "AST Depot"
ROCK_NAME = BASE_ROCK_NAME
COMET_NAME = "UnknownComet"
VESSEL_PIDS = {KLAW_NAME: 2710401000, MINER_NAME: 2710402000, WATCHER_NAME: 2710403000,
               DEPOT_NAME: 2710404000}
ROCK_PID = 1490391000          # the base asteroid's own persistentId, kept
COMET_PID = 3656867723         # the donor comet's own persistentId, kept

# Along-track offsets (metres; + = ahead of the active probe).
ROCK_AHEAD_M = 60.0
COMET_AHEAD_M = 150.0
WATCHER_BEHIND_M = 300.0
DEPOT_BEHIND_M = 150.0
MOTHERSHIP_ROCK_AHEAD_M = 100.0

# The RCS ring on the OKTO (build_lab_lko_tender_rcs.py's poses at y = 0).
RCS_R = "0.262"
TANK_D = "0.375049"
OKTO_TO_PORT = 0.187           # LABX: OKTO y 1.1243, its Jr port y 0.9372
OKTO_TO_Z200 = 0.287           # LABX: Z-200 y 1.4114 above the OKTO
PORT_TO_PORT = 0.295           # LABX: the two PreAttached Jr ports, y 0.6424 / 0.9372
Z200_TO_KLAW = 0.29            # invented

GAME_EDITS = (("\t\t\tEnableCommNet = True", "\t\t\tEnableCommNet = False"),
              ("\t\t\tEnableFullSASInSandbox = False", "\t\t\tEnableFullSASInSandbox = True"))
FREED_CREW = ("Bob Kerman", "Valentina Kerman")


class PartSpec(object):
    """One PART to write: donor (or None for the invented Klaw), parent index, pose,
    attachment, symmetry partners, and body edits."""

    def __init__(self, donor, parent, position, rotation, srf_parent=None, att=(),
                 sym=(), dock_state=None, ore=None):
        self.donor = donor
        self.parent = parent
        self.position = position
        self.rotation = rotation
        self.srf_parent = srf_parent
        self.att = tuple(att)
        self.sym = tuple(sym)
        self.dock_state = dock_state
        self.ore = ore

    @property
    def name(self) -> str:
        return self.donor[1] if self.donor else KLAW


def _fmt(x: float) -> str:
    text = repr(float(x))
    return "0" if text in ("0.0", "-0.0") else (text[:-2] if text.endswith(".0") else text)


def _pos(x: float, y: float, z: float) -> str:
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


IDENT = "0,0,0,1"
FLIP = "1,0,0,0"


def klaw_probe(offset: int = 0) -> List[PartSpec]:
    """The Klaw probe's ten parts; indices are relative to `offset` (the probe's OKTO)."""
    o = offset
    ring_y = "0"
    return [
        PartSpec(D_OKTO, o, "0,0,0", IDENT, att=(("bottom", o + 3), ("top", o + 1))),
        PartSpec(D_Z200, o, _pos(0, OKTO_TO_Z200, 0), IDENT, att=(("top", -1), ("bottom", o))),
        PartSpec(None, o + 1, _pos(0, OKTO_TO_Z200 + Z200_TO_KLAW, 0), IDENT, srf_parent=o + 1),
        PartSpec(D_JR, o, _pos(0, -OKTO_TO_PORT, 0), FLIP, att=(("top", -1), ("bottom", o)),
                 dock_state="Ready"),
        PartSpec(D_RCS, o, "-%s,%s,0" % (RCS_R, ring_y), "0,0,0,1", srf_parent=o,
                 sym=(o + 5, o + 6, o + 7)),
        PartSpec(D_RCS, o, "0,%s,%s" % (ring_y, RCS_R), "0,0.707106829,0,0.707106829",
                 srf_parent=o, sym=(o + 4, o + 6, o + 7)),
        PartSpec(D_RCS, o, "%s,%s,0" % (RCS_R, ring_y), "0,1,0,0", srf_parent=o,
                 sym=(o + 4, o + 5, o + 7)),
        PartSpec(D_RCS, o, "0,%s,-%s" % (ring_y, RCS_R), "0,0.707106829,0,-0.707106829",
                 srf_parent=o, sym=(o + 4, o + 5, o + 6)),
        PartSpec(D_MONO, o, "-%s,%s,%s" % (TANK_D, ring_y, TANK_D), "0,0.923879504,0,0.382683456",
                 srf_parent=o, sym=(o + 9,)),
        PartSpec(D_MONO, o, "%s,%s,-%s" % (TANK_D, ring_y, TANK_D), "0,0.382683456,0,-0.923879504",
                 srf_parent=o, sym=(o + 8,)),
    ]


def miner_parts() -> List[PartSpec]:
    parts = klaw_probe()
    parts += [
        PartSpec(D_DRILL, 0, "-0.3,0.12,0.3", "-0.707106948,0,-4.21468549E-08,0.707106948",
                 srf_parent=0),
        PartSpec(D_ORE, 0, "0.32,0.12,0.32", IDENT, srf_parent=0, ore=0.0),
        PartSpec(D_Z400, 0, "0.45,-0.05,0", "0,-0.707106829,0,0.707106829", srf_parent=0,
                 sym=(13,)),
        PartSpec(D_Z400, 0, "-0.45,-0.05,0", "0,0.707106829,0,0.707106829", srf_parent=0,
                 sym=(12,)),
    ]
    return parts


def mothership_parts() -> List[PartSpec]:
    parts = klaw_probe()
    # The probe's Jr port (index 3) is PreAttached to the mothership's (index 10).
    probe_port = parts[3]
    probe_port.att = (("top", 10), ("bottom", 0))
    probe_port.dock_state = "PreAttached"
    port_y = -OKTO_TO_PORT - PORT_TO_PORT
    okto_y = port_y - OKTO_TO_PORT
    parts += [
        PartSpec(D_JR, 3, _pos(0, port_y, 0), IDENT, att=(("top", 3), ("bottom", 11)),
                 dock_state="PreAttached"),
        PartSpec(D_OKTO, 10, _pos(0, okto_y, 0), IDENT, att=(("bottom", 12), ("top", 10))),
        PartSpec(D_Z200, 11, _pos(0, okto_y - OKTO_TO_Z200, 0), FLIP,
                 att=(("top", -1), ("bottom", 11))),
    ]
    return parts


def watcher_parts() -> List[PartSpec]:
    return [PartSpec(D_OKTO, 0, "0,0,0", IDENT, att=(("bottom", -1), ("top", 1))),
            PartSpec(D_Z200, 0, _pos(0, OKTO_TO_Z200, 0), IDENT, att=(("top", -1), ("bottom", 0)))]


def depot_parts() -> List[PartSpec]:
    return [PartSpec(D_OKTO, 0, "0,0,0", IDENT, att=(("bottom", 2), ("top", 1))),
            PartSpec(D_JR, 0, _pos(0, OKTO_TO_PORT, 0), IDENT, att=(("top", -1), ("bottom", 0)),
                     dock_state="Ready"),
            PartSpec(D_Z200, 0, _pos(0, -OKTO_TO_Z200, 0), FLIP, att=(("top", -1), ("bottom", 0))),
            PartSpec(D_ORE, 0, "0.32,0,0.32", IDENT, srf_parent=0, ore=0.0)]


# Part identities by fixture-vessel slot k (1-based) and part index i.
def part_ids(k: int, i: int) -> Tuple[str, str, str]:
    return ("42710%02d%03d" % (k, i), str(1710000000 + k * 1000 + i),
            str(2710000000 + k * 1000 + i))


def vessel_guid(tag: str) -> str:
    return hashlib.md5(("parsek-ast-fixture:" + tag).encode("ascii")).hexdigest()


# ---------------------------------------------------------------------------
# Line helpers.
# ---------------------------------------------------------------------------

def _read(path: str) -> str:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def _lines(name: str) -> List[str]:
    return _read(os.path.join(SAVES, name, "persistent.sfs")).replace(CRLF, LF).split(LF)


def _block_end(lines: Sequence[str], start: int) -> int:
    """Index one past the closing brace of the block whose header is lines[start]."""
    indent = lines[start][:len(lines[start]) - len(lines[start].lstrip("\t"))]
    assert lines[start + 1] == indent + "{", (start, lines[start + 1])
    for j in range(start + 2, len(lines)):
        if lines[j] == indent + "}":
            return j + 1
    raise AssertionError("unterminated block at %d" % start)


def vessel_blocks(lines: Sequence[str]) -> List[Tuple[int, int, str]]:
    out = []
    for i, line in enumerate(lines):
        if line == _V + "VESSEL":
            end = _block_end(lines, i)
            name = [l for l in lines[i:end] if l.startswith(_VK + "name = ")][0]
            out.append((i, end, name[len(_VK + "name = "):]))
    return out


def part_ranges(vessel: Sequence[str]) -> List[Tuple[int, int]]:
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


def _value(block: Sequence[str], indent: str, key: str) -> str:
    hits = [l for l in block if l.startswith(indent + key + " = ")]
    assert len(hits) == 1, "key %s found %d times" % (key, len(hits))
    return hits[0][len(indent + key + " = "):]


def donor_part(donor: Tuple[str, str]) -> List[str]:
    save, name = donor
    lines = _lines(save)
    for i, line in enumerate(lines):
        if line == _PART and lines[i + 2] == _P + "name = " + name:
            return lines[i:_block_end(lines, i)]
    raise AssertionError("no %s part in %s" % (name, save))


def split_part(block: List[str]) -> Tuple[List[str], List[str]]:
    assert block[0] == _PART and block[1] == _VK + "{" and block[-1] == _VK + "}"
    ev = block.index(_P + "EVENTS")
    return block[2:ev], block[ev:-1]


def _edit_body(spec: PartSpec, body: List[str]) -> List[str]:
    out = list(body)
    if spec.dock_state is not None:
        hits = [i for i, l in enumerate(out) if l.startswith(_P + "\tstate = ")
                and any(out[j] == _P + "\tname = ModuleDockingNode" for j in range(max(0, i - 12), i))]
        assert len(hits) == 1, "dock state lines %d" % len(hits)
        out[hits[0]] = _P + "\tstate = " + spec.dock_state
    if spec.ore is not None:
        at = out.index(_P + "\tname = Ore")
        assert out[at + 1].startswith(_P + "\tamount = "), out[at + 1]
        out[at + 1] = _P + "\tamount = " + _fmt(spec.ore)
    return out


HEADER_KEYS = ("name", "cid", "uid", "mid", "persistentId", "launchID", "parent", "position",
               "rotation", "mirror", "symMethod", "istg", "resPri", "dstg", "sqor", "sepI",
               "sidx", "attm", "sameVesselCollision", "srfN", "mass", "shielded", "temp",
               "tempExt", "tempExtUnexp", "staticPressureAtm", "expt", "state", "PreFailState",
               "attached", "autostrutMode", "rigidAttachment", "flag", "rTrf", "modCost",
               "modMass", "moduleVariantName", "moduleCargoStackableQuantity")
KLAW_HEADER = {"mass": "0.150000006", "expt": "0.5", "rTrf": KLAW, "moduleVariantName": "",
               "srfN": "srfAttach, -1"}
_M = _P + "\t"
KLAW_BODY = [_P + "EVENTS", _P + "{", _P + "}", _P + "ACTIONS", _P + "{", _P + "}",
             _P + "PARTDATA", _P + "{", _P + "}",
             # ModuleGrappleNode builds its grapple AttachNode only in OnLoad, which stock
             # calls only for a module with a saved MODULE node: without one every frame of
             # the FSM's Disable event throws (grappleNode null) and the Klaw cannot grab.
             _P + "MODULE", _P + "{", _M + "name = ModuleGrappleNode", _M + "isEnabled = True",
             _M + "state = Disabled", _M + "dockUId = 0", _M + "stagingEnabled = True",
             _M + "EVENTS", _M + "{", _M + "}", _M + "ACTIONS", _M + "{", _M + "}",
             _M + "UPGRADESAPPLIED", _M + "{", _M + "}", _P + "}"]
TEMP = "290"


def render_part(spec: PartSpec, ids: Tuple[str, str, str], mid: str, launch_id: str) -> List[str]:
    if spec.donor is None:
        donor_header, body = None, list(KLAW_BODY)
        src = KLAW_HEADER
    else:
        donor_header, body = split_part(donor_part(spec.donor))
        keys = [l.strip("\t").split(" = ", 1)[0] for l in donor_header]
        extra = sorted(set(keys) - set(HEADER_KEYS) - {"sym", "attN"})
        assert not extra, "donor %s header keys outside the writer's set: %s" % (spec.donor, extra)
        src = {k: _value(donor_header, _P, k)
               for k in ("mass", "expt", "rTrf", "moduleVariantName", "srfN")}
        body = _edit_body(spec, body)
    cid, uid, pid = ids
    if spec.srf_parent is not None:
        srfn = "srfAttach, %d" % spec.srf_parent
        attm = "1"
    else:
        srfn = src["srfN"].rsplit(",", 1)[0] + ", -1"
        attm = "0"
    values = {
        "name": spec.name, "cid": cid, "uid": uid, "mid": mid, "persistentId": pid,
        "launchID": launch_id, "parent": str(spec.parent), "position": spec.position,
        "rotation": spec.rotation, "mirror": "1,1,1", "symMethod": "Radial", "istg": "-1",
        "resPri": "0", "dstg": "0", "sqor": "-1", "sepI": "0", "sidx": "-1", "attm": attm,
        "sameVesselCollision": "False", "srfN": srfn, "mass": src["mass"], "shielded": "False",
        "temp": TEMP, "tempExt": TEMP, "tempExtUnexp": "4", "staticPressureAtm": "0",
        "expt": src["expt"], "state": "0", "PreFailState": "0", "attached": "True",
        "autostrutMode": "Off", "rigidAttachment": "False", "flag": "", "rTrf": src["rTrf"],
        "modCost": "0", "modMass": "0", "moduleVariantName": src["moduleVariantName"],
        "moduleCargoStackableQuantity": "1",
    }
    out = [_PART, _VK + "{"]
    for key in HEADER_KEYS:
        if key == "srfN":
            out.extend(_P + "sym = %d" % s for s in spec.sym)
        out.append(_P + key + " = " + values[key])
        if key == "srfN":
            out.extend(_P + "attN = %s, %d" % (n, idx) for n, idx in spec.att)
    out.extend(body)
    out.append(_VK + "}")
    return out


# ---------------------------------------------------------------------------
# Vessels.
# ---------------------------------------------------------------------------

def _labx(lines: List[str]) -> List[str]:
    blocks = vessel_blocks(lines)
    hit = [b for b in blocks if b[2] == BASE_SHIP_NAME]
    assert len(hit) == 1
    return lines[hit[0][0]:hit[0][1]]


def _orbit(labx: List[str], ahead_m: float) -> List[str]:
    at = labx.index(_VK + "ORBIT")
    orbit = labx[at:_block_end(labx, at)]
    sma = float(_value(orbit, _P, "SMA"))
    mna = float(_value(orbit, _P, "MNA"))
    out = list(orbit)
    i = out.index(_P + "MNA = " + _value(orbit, _P, "MNA"))
    out[i] = _P + "MNA = " + repr(mna + ahead_m / sma)
    return out


def _labx_tail(labx: List[str]) -> List[str]:
    """ACTIONGROUPS through VESSELMODULES of the LABX, with RCS / SAS off and the
    CTRLSTATE zeroed."""
    at = labx.index(_VK + "ACTIONGROUPS")
    tail = labx[at:-1]
    out = []
    for l in tail:
        if l.startswith(_P + "RCS = ") or l.startswith(_P + "SAS = "):
            l = l.split(" = ")[0] + " = False, 0"
        elif l.startswith(_P) and any(l.startswith(_P + k + " = ") for k in
                                      ("pitch", "yaw", "roll")):
            l = l.split(" = ")[0] + " = 0"
        elif l == _P + "\tcontrolState = KerbalFull":
            l = _P + "\tcontrolState = ProbeFull"
        out.append(l)
    return out


def _target_block(target_guid: str) -> List[str]:
    """The TARGET node stock writes for a vessel targeting another vessel (decompiled
    ProtoVessel.Save / ProtoTargetInfo.Save: type, then vesselID as Guid.ToString(), the
    hyphenated form). Stock's Vessel.ResumeTarget restores it on the active vessel at load."""
    g = target_guid.replace("-", "")
    assert len(g) == 32, target_guid
    dashed = "%s-%s-%s-%s-%s" % (g[:8], g[8:12], g[12:16], g[16:20], g[20:])
    return [_VK + "TARGET", _VK + "{", _P + "type = Vessel", _P + "vesselID = " + dashed,
            _VK + "}"]


def rock_guid(rock: List[str]) -> str:
    return _value(rock, _VK, "pid")


def build_ship(labx: List[str], name: str, vtype: str, slot: int, specs: List[PartSpec],
               ahead_m: float, used: set, target_guid: Optional[str] = None) -> List[str]:
    mid = str(1610000000 + slot)
    launch_id = str(100 + slot)
    parts: List[str] = []
    for i, spec in enumerate(specs):
        ids = part_ids(slot, i)
        for ident in ids:
            assert ident not in used, "identity %s collides" % ident
            used.add(ident)
        parts.extend(render_part(spec, ids, mid, launch_id))
    root_uid = part_ids(slot, 0)[1]
    header = []
    head_end = labx.index(_VK + "ORBIT")
    for l in labx[2:head_end]:
        key = l.strip("\t").split(" = ", 1)[0]
        repl = {"pid": vessel_guid(name + "|" + str(slot)),
                "persistentId": str(VESSEL_PIDS[name]), "name": name, "type": vtype,
                "ref": root_uid, "CoM": "0,0,0", "launchedFrom": "", "distanceTraveled": "0"}
        if key in repl:
            l = _VK + key + " = " + repl[key]
        header.append(l)
    tail = _labx_tail(labx)
    if target_guid is not None:
        # kRPC's SpaceCenter.vessels leaves SpaceObject vessels out, so the mission reaches
        # the rock only as sc.target_vessel: the probe saves the rock as its target, in
        # stock's own place (after FLIGHTPLAN, before CTRLSTATE).
        at = tail.index(_VK + "CTRLSTATE")
        tail = tail[:at] + _target_block(target_guid) + tail[at:]
    return ([_V + "VESSEL", _V + "{"] + header + _orbit(labx, ahead_m) + parts
            + tail + [_V + "}"])


def build_rock(lines_or_donor: List[str], name: str, labx: List[str], ahead_m: float,
               size: Optional[str]) -> List[str]:
    blocks = vessel_blocks(lines_or_donor)
    hit = [b for b in blocks if b[2] == name]
    assert len(hit) == 1, (name, [b[2] for b in blocks])
    rock = lines_or_donor[hit[0][0]:hit[0][1]]
    at = rock.index(_VK + "ORBIT")
    out = rock[:at] + _orbit(labx, ahead_m) + rock[_block_end(rock, at):]
    out = [_VK + "sit = ORBITING" if l.startswith(_VK + "sit = ") else l for l in out]
    d = out.index(_VK + "DISCOVERY")
    for key, value in (("state", "29"), ("lifetime", "Infinity"), ("refTime", "Infinity")) + (
            (("size", size),) if size is not None else ()):
        i = out.index(_P + key + " = " + _value(out[d:_block_end(out, d)], _P, key), d)
        out[i] = _P + key + " = " + value
    assert [l for l in out if l.startswith(_P + "name = Potato")], "no rock PART"
    # A discovered comet with `hasName = False` is renamed at load (decompiled
    # CometVessel: DiscoverableObjectsUtil.GenerateCometName, a fresh random name per load;
    # COM-1's first flights saw 'Gerwise 1' then 'Lisdock 1'), so the lanes could never name it.
    named = [i for i, l in enumerate(out) if l == _P + "\thasName = False"]
    assert len(named) <= 1, named
    for i in named:
        out[i] = _P + "\thasName = True"
    return out


def build_fixture(name: str) -> Dict[str, bytes]:
    base_text = _read(os.path.join(BASE_DIR, "persistent.sfs"))
    lines = base_text.replace(CRLF, LF).split(LF)
    labx = _labx(lines)
    donors_text = base_text + "".join(
        _read(os.path.join(SAVES, s, "persistent.sfs"))
        for s in sorted({d[0] for d in (D_RCS, D_MONO, D_DRILL, D_ORE)} | {COMET_DONOR[0]}))
    used: set = set()
    vessels: List[List[str]] = []
    active = 0
    if name in (AST_KLAW, COM_KLAW):
        if name == AST_KLAW:
            vessels.append(build_rock(lines, ROCK_NAME, labx, ROCK_AHEAD_M, "0"))
        else:
            vessels.append(build_rock(_lines(COMET_DONOR[0]), COMET_NAME, labx, COMET_AHEAD_M,
                                      None))
        vessels.append(build_ship(labx, KLAW_NAME, "Probe", 1, klaw_probe(), 0.0, used,
                                  rock_guid(vessels[0])))
        vessels.append(build_ship(labx, WATCHER_NAME, "Probe", 3, watcher_parts(),
                                  -WATCHER_BEHIND_M, used))
        active = 1
    elif name == AST_MINER:
        vessels.append(build_rock(lines, ROCK_NAME, labx, ROCK_AHEAD_M, "0"))
        vessels.append(build_ship(labx, MINER_NAME, "Probe", 2, miner_parts(), 0.0, used,
                                  rock_guid(vessels[0])))
        vessels.append(build_ship(labx, DEPOT_NAME, "Station", 4, depot_parts(),
                                  -DEPOT_BEHIND_M, used))
        active = 1
    elif name == AST_MOTHERSHIP:
        vessels.append(build_rock(lines, ROCK_NAME, labx, MOTHERSHIP_ROCK_AHEAD_M, "0"))
        vessels.append(build_ship(labx, KLAW_NAME, "Probe", 1, mothership_parts(), 0.0, used,
                                  rock_guid(vessels[0])))
        active = 1
    else:
        raise ValueError(name)
    for ident in used:
        assert ident not in donors_text, "identity %s already in a donor / the base" % ident
    blocks = vessel_blocks(lines)
    start, end = blocks[0][0], blocks[-1][1]
    out = lines[:start] + [l for v in vessels for l in v] + lines[end:]
    out[out.index("\t\tactiveVessel = 1")] = "\t\tactiveVessel = %d" % active
    out[out.index("\tTitle = " + BASE_TITLE)] = "\tTitle = %s (SANDBOX)" % name
    desc = [i for i, l in enumerate(out) if l.startswith("\tDescription = ")]
    assert len(desc) == 1
    out[desc[0]] = ("\tDescription = Parsek autotest fixture (file-constructed by "
                    "build_ast_klaw_fixtures.py: a Klaw probe parked beside a rock in low "
                    "Kerbin orbit).")
    for old, new in GAME_EDITS:
        assert out.count(old) == 1, old
        out[out.index(old)] = new
    out = _free_crew(out)
    sfs = LF.join(out).encode("ascii")
    meta = _read(os.path.join(BASE_DIR, "persistent.loadmeta"))
    count = sum(1 for v in vessels if (_VK + "type = SpaceObject") not in v)
    assert "vesselCount = 1\r\n" in meta
    meta = meta.replace("vesselCount = 1\r\n", "vesselCount = %d\r\n" % count, 1)
    files = {"persistent.sfs": sfs, "persistent.loadmeta": meta.encode("ascii")}
    addons = os.path.join(BASE_DIR, "AddOns")
    for root, _dirs, names in os.walk(addons):
        for n in names:
            full = os.path.join(root, n)
            rel = os.path.relpath(full, BASE_DIR).replace(os.sep, "/")
            files[rel] = _read_bytes(full)
    return files


def _free_crew(lines: List[str]) -> List[str]:
    out = list(lines)
    for kerbal in FREED_CREW:
        at = out.index("\t\t\tname = " + kerbal)
        end = _block_end(out, at - 2)
        st = [i for i in range(at, end) if out[i] == "\t\t\tstate = Assigned"]
        ix = [i for i in range(at, end) if out[i].startswith("\t\t\tidx = ")]
        assert len(st) == 1 and len(ix) == 1, kerbal
        out[st[0]] = "\t\t\tstate = Available"
        out[ix[0]] = "\t\t\tidx = -1"
    return out


def out_dir(name: str) -> str:
    return os.path.join(SAVES, name)


def committed_problems(name: str) -> List[str]:
    problems = []
    expected = build_fixture(name)
    root = out_dir(name)
    for rel, data in sorted(expected.items()):
        path = os.path.join(root, rel)
        if not os.path.isfile(path):
            problems.append("%s/%s missing" % (name, rel))
        elif _read_bytes(path) != data:
            problems.append("%s/%s differs from the builder" % (name, rel))
    for dirpath, _dirs, names in os.walk(root):
        for n in names:
            rel = os.path.relpath(os.path.join(dirpath, n), root).replace(os.sep, "/")
            if rel not in expected:
                problems.append("%s/%s is not produced by the builder" % (name, rel))
    return problems


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args(argv)
    if args.check:
        problems = [p for n in FIXTURES for p in committed_problems(n)]
        for p in problems:
            print("DRIFT: " + p)
        print("check: %s" % ("OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0
    for name in FIXTURES:
        root = out_dir(name)
        if os.path.isdir(root):
            shutil.rmtree(root)
        for rel, data in build_fixture(name).items():
            path = os.path.join(root, rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as fh:
                fh.write(data)
        print("wrote %s" % root)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
