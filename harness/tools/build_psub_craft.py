#!/usr/bin/env python3
"""Build the committed `PSUB-1.craft` (the stock submarine test kit's craft) BY CONSTRUCTION.

WHAT IT IS FOR. No lane puts a vessel UNDER the water. The recorded-depth spawn rule
(`VesselSpawner.ResolveSplashedSpawnAltitude`, owner ruling 2026-11-17) and the vertical
leaf check (`LeafVesselPlacement.CompareHeight`, the `LeafPlacement height:` line) have no
live subject below sea level. Stock KSP has no ballast tank or pump, so a stock submarine
changes its buoyancy the only way stock allows: by DROPPING parts. PSUB-1 is an uncrewed
probe hull wearing two rings of radial pairs on TT-70 decouplers:

  FLOATS   three pairs of EMPTY FL-T100 tanks (volume, almost no mass). Dropping a float
           pair makes the sub heavier for its volume; the dive drops them one pair at a
           time until it sinks.
  BALLAST  three pairs of RadialOreTank full or part full of Ore (mass, little volume).
           Dropping a ballast pair makes it lighter; the optional rise drops them.

THE PARTS, top to bottom (the root first, KSP takes the first PART as the root):
  probeCoreOcto2.v2 (root) / sasModule / batteryBankMini / fuelTankSmall (FL-T200, EMPTY,
  the hull), then 12 TT-70 (radialDecoupler1-2) on the hull: the float ring 0.25 m above
  the hull's centre at azimuths 0/180, 60/240, 120/300, each carrying an FL-T100
  (fuelTankSmallFlat); the ballast ring 0.30 m below it at 30/210, 90/270, 150/330, each
  carrying a RadialOreTank. 28 parts. No crew, no engine, no chute, no action group.

THE STAGING TABLE (`istg` is the authority KSP reads; it fires the HIGHEST istg first):
  istg 5  float pair A (az 0 / 180)           istg 2  ballast pair 1 (30 / 210), Ore 75 + 75
  istg 4  float pair B (60 / 240)             istg 1  ballast pair 2 (90 / 270), Ore 75 + 75
  istg 3  float pair C (120 / 300)            istg 0  ballast pair 3 (150 / 330), Ore 40 + 40
So the first three stage clicks drop the floats, one pair each, and the next three drop the
ballast, heaviest first. A tank carries its decoupler's istg (the Kerbal X pattern); the
four core parts carry istg -1 (never staged). Every pair carries `sym =` lines.

WHAT IS COPIED AND WHAT IS DERIVED (build_air_drone_carrier.py's contract):

  COPIED - every PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE / RESOURCE) is lifted
  at BUILD TIME from a committed craft whose tails KSP wrote (read every run, so a donor
  change reds `--check`):
    probeCoreOcto2.v2, sasModule, batteryBankMini  `harness/fixtures/ships/DD1 Duna Direct
        Probe.craft` (its tails are byte-for-byte stock, build_dd1_craft.py);
    radialDecoupler1-2                             `harness/fixtures/ships/Kerbal X.craft`
        (stock, KSP-written);
    fuelTankSmallFlat                              `harness/fixtures/ships/CS Flea Probe.craft`
        (the stock Kerbal 1 tail, build_cs_flea_probe_craft.py --verify-donors);
    fuelTankSmall                                  `CS Mun Flyby.craft` in
        `fixtures/saves/cs-career-mun1/Ships/VAB` (the stock ComSat Lx tail,
        build_cs_orbiter_craft.py --verify-donors);
    RadialOreTank                                  `harness/fixtures/donors/isru-mining-rover/
        Mining Rover RC.craft` (V's own KSP-written craft; its RadialOreTank tail carries no
        ReStock module).
  The ONLY tail edits are resource AMOUNTS: every LiquidFuel / Oxidizer `amount` of the
  three fuel-tank kinds goes to 0, and each RadialOreTank's Ore `amount` goes to its pair's
  fill (75 / 75 / 40, never above the donor's maxAmount 75).

  DERIVED - the scalar header of each PART. Stack positions use the validated formula
  child.pos = parent.pos + parent_node - child_opposite_node with node offsets read off
  committed pipe-format attN tokens (Octo2 +/-0.0610621, sasModule +/-0.0911109,
  batteryBankMini +/-0.1, FL-T200 +/-0.55525). Every radial pair copies ONE committed
  relative pose and turns it about the hull's axis:
    - decoupler + float: Kerbal X's booster pair (radialDecoupler1-2_4294042434 on the
      Rockomax32 core, fuelTank_4294017080 on it). The decoupler origin keeps its depth
      under the parent's skin (0.05 m: r = 1.2 on a 1.25 m core, so 0.575 on this
      0.625 m hull), the tank keeps the decoupler-relative offset (0.789 m out, 0.098 m
      down) and the same rotation. The FL-T400 there and the FL-T100 here share their
      0.625 m radius, so the tank's attach node lands where Kerbal X's does.
    - ore tank on its decoupler: the Mining Rover's RadialOreTank_4294057998 (KSP wrote
      it) shows the part's frame: its origin, the srfAttach node `0|0|0`, sits on its
      parent's skin (0.632 m from the axis of a 0.625 m MiniISRU), and its local +Z is the
      OUTWARD normal there (rot maps +Z to parent-local -X, the hex collider's face
      normal at that point). So the
      origin goes on the decoupler's outer face, the point Kerbal X's tank attach node
      touches (decoupler-local x = -0.789 + 0.625 = -0.164, y = -0.098), with local +Z
      outward and local +Y up (the rover's own roll, +Y down, followed its SPH-horizontal
      parent; the roll about the normal is cosmetic).
  Headers use the 1.12.5 token forms DD1 flew (`srfN = srfAttach,<parent>`,
  `attN = top,<part>_0|<dy>|0`), and every parent carries a `link =` line for every
  child (KSP drops an unlinked child at load, silently; build_kerbal_x_sweep_craft.py).

MASS AND VOLUME (part cfg masses; volumes are the parts' drag-cube box volumes times the
cylinder fill, which is how stock PartBuoyancy sizes displacement; the real displacement is
k times these, k an unknown of order 1 the first dive measures):
    part                               volume m3   mass t
    hull FL-T200 (empty)                  1.36      0.125
    core: Octo2 + sasModule + Z-200       0.15      0.10
    FL-T100 float (empty), each           0.69      0.0625
    TT-70 decoupler, each                 0.02      0.05
    RadialOreTank, each                   0.30      0.125 dry + 0.01 per unit of Ore
  Total 5.75 t (Ore 3.8 t of it). Net buoyancy in tonnes-force (displaced water at Kerbin's
  1.0 t/m3, times k, minus mass), each pair taken with its decoupler (a TT-70 leaves with
  the part it holds; if it stays on the hull instead, each drop below is 0.06 t heavier):
    k     all aboard  -1F    -2F    -3F  |  then -B1  -B2  -B3 (all floats gone)
    0.7     -0.37    -1.14  -1.91  -2.67 |    -1.27  +0.13  +0.83
    0.8     +0.40    -0.51  -1.42  -2.33 |    -0.99  +0.35  +0.98
    1.0     +1.94    +0.75  -0.45  -1.64 |    -0.44  +0.77  +1.28
    1.2     +3.48    +2.00  +0.52  -0.96 |    +0.12  +1.20  +1.59
    1.4     +5.02    +3.25  +1.49  -0.27 |    +0.68  +1.63  +1.89
    1.5     +5.79    +3.88  +1.97  +0.07 |    +0.96  +1.85  +2.04
  So for every k in about [0.75, 1.45] the sub floats as delivered, sinks after one to
  three float pairs, and rises again after one to three ballast pairs; below 0.75 it sinks
  as delivered (the dive then drops nothing), above 1.45 it never sinks (the dive's named
  give-up). Upright: the ballast ring is below the hull's centre and the floats above it,
  so under water the centre of buoyancy sits 0.2 - 0.5 m above the centre of mass while
  any ballast is aboard. With every pair gone the bare stack is top-light and may roll.

ASSUMPTIONS THE FIRST FLIGHT CAN FALSIFY, in the order they bite:
  C1 THE FORGE LAUNCH. The craft is VAB-format and rolls out to the LaunchPad (the forge,
     FORGE-psub-pad). A craft loading at a silly height or with parts missing from the
     launched vessel is a header (link / attN / srfN) or root-placement fault here.
  C2 STAGING. Six stages, floats first. A stage click that drops ballast first, or two
     pairs at once, is the istg table above.
  C3 THE ORE TANK POSE. An ore tank facing into the hull, or floating off its decoupler,
     is the "+Z is outward" reading of the rover above. Cosmetic unless the joint fails.
  C4 k. The table above; the dive measures it and logs every reading.
  C5 WATER PRESSURE. The dive stays shallow (tens of metres, under 1 MPa); parts in 1.12
     tolerate 4 MPa by default, so a crush is not expected here.

Usage:
    python harness/tools/build_psub_craft.py --check     # verify the committed bytes
    python harness/tools/build_psub_craft.py --write     # regenerate the file

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys
from typing import Dict, List, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
FIXTURES = os.path.join(ROOT, "harness", "fixtures")
SHIP_NAME = "PSUB-1"
# Single consumer (FORGE-psub-pad boots bdock-forge-base), so the craft lives physically
# in that fixture's Ships/VAB (fixtures/shared-ships.toml: a craft used by one fixture stays
# in it). kRPC launch_vessel resolves <save>/Ships/VAB/<craftName>.craft there.
OUT = os.path.join(FIXTURES, "saves", "bdock-forge-base", "Ships", "VAB",
                   SHIP_NAME + ".craft")

DONOR_DD1 = os.path.join(FIXTURES, "ships", "DD1 Duna Direct Probe.craft")
DONOR_KX = os.path.join(FIXTURES, "ships", "Kerbal X.craft")
DONOR_FLEA_PROBE = os.path.join(FIXTURES, "ships", "CS Flea Probe.craft")
DONOR_MUN_FLYBY = os.path.join(FIXTURES, "saves", "cs-career-mun1", "Ships", "VAB",
                               "CS Mun Flyby.craft")
DONOR_ROVER = os.path.join(FIXTURES, "donors", "isru-mining-rover", "Mining Rover RC.craft")

# The committed relative poses (see the docstring).
KX_DECOUPLER = "radialDecoupler1-2_4294042434"
KX_TANK = "fuelTank_4294017080"
KX_CORE_RADIUS = 1.25
KX_TANK_RADIUS = 0.625           # FL-T400, its srfN node `0.625|0|0`
ROVER_ORE_TANK = "RadialOreTank_4294057998"

ROOT_Y = 15.0
HULL_RADIUS = 0.625              # FL-T200
FLOAT_RING_DY = 0.25             # decoupler height above the hull's centre
BALLAST_RING_DY = -0.30

# EFFECTIVE stack-node offsets, each read off a committed pipe-format attN token.
NODE_OFFSET = {
    "probeCoreOcto2.v2": {"top": 0.0610621, "bottom": -0.0610621},   # build_dd1_craft.py
    "sasModule": {"top": 0.0911109, "bottom": -0.0911109},           # build_dd1_craft.py
    "batteryBankMini": {"top": 0.1, "bottom": -0.1},                 # build_dd1_craft.py
    "fuelTankSmall": {"top": 0.55525, "bottom": -0.55525},           # CS Mun Flyby attN
}

# (pair key, ring, azimuth deg of the first member, istg, Ore units per tank or None).
PAIRS: Tuple[Tuple[str, str, float, int, object], ...] = (
    ("floatA", "float", 0.0, 5, None),
    ("floatB", "float", 60.0, 4, None),
    ("floatC", "float", 120.0, 3, None),
    ("ballast1", "ballast", 30.0, 2, 75),
    ("ballast2", "ballast", 90.0, 1, 75),
    ("ballast3", "ballast", 150.0, 0, 40),
)
FLOAT_PAIRS = sum(1 for p in PAIRS if p[1] == "float")
BALLAST_PAIRS = sum(1 for p in PAIRS if p[1] == "ballast")
ORE_MAX = 75

# Deterministic ids (UInt32-safe, unique within the file; the drift gate compares bytes).
_CORE_KEYS = ("probe", "sas", "batt", "hull")


def _all_keys() -> List[str]:
    keys = list(_CORE_KEYS)
    for pair, _ring, _az, _istg, _ore in PAIRS:
        for member in (0, 1):
            keys.append("%s%d_dec" % (pair, member))
            keys.append("%s%d_tank" % (pair, member))
    return keys


_UID = {k: 4251000001 + i for i, k in enumerate(_all_keys())}
_PERSISTENT_ID = {k: 1700000000 + i * 7717 for i, k in enumerate(_all_keys())}
assert max(_UID.values()) <= 0xFFFFFFFF and max(_PERSISTENT_ID.values()) <= 0xFFFFFFFF
SHIP_PERSISTENT_ID = 2300110077


# ---------------------------------------------------------------------------
# Quaternion and formatting helpers (build_air_drone_carrier.py's).
# ---------------------------------------------------------------------------

def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _qrot(q, v):
    r = _qmul(_qmul(q, (v[0], v[1], v[2], 0.0)), (-q[0], -q[1], -q[2], q[3]))
    return r[:3]


def _qinv(q):
    return (-q[0], -q[1], -q[2], q[3])


def _qy(theta_deg):
    h = math.radians(theta_deg) / 2.0
    return (0.0, math.sin(h), 0.0, math.cos(h))


def _add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def _sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def _fmt(v):
    v = round(float(v), 7)
    if v == 0:
        return "0"
    s = repr(v)
    return s[:-2] if s.endswith(".0") else s


def _vec(v):
    return ",".join(_fmt(c) for c in v)


def _read(path):
    with open(path, "r", encoding="utf-8-sig", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _blocks(text):
    """{part token: (header text, tail text)} for every PART in ``text``."""
    out = {}
    for blk in text.split("\nPART\n{")[1:]:
        m = re.search(r"^\tpart = (\S+)$", blk, re.M)
        i = blk.index("\n\tEVENTS\n")
        out[m.group(1)] = (blk[:i], blk[i + 1:].rsplit("\n}", 1)[0])
    return out


def _first_tail(blocks, name):
    for token, (_head, tail) in blocks.items():
        if token.rsplit("_", 1)[0] == name:
            return tail
    raise SystemExit("donor has no %s" % name)


def _header_vec(head, key):
    m = re.search(r"^\t%s = (\S+)$" % key, head, re.M)
    if not m:
        raise SystemExit("donor header has no %s" % key)
    return tuple(float(x) for x in m.group(1).split(","))


def _empty_propellant(tail):
    out, n = re.subn(r"^\t\tamount = \S+$", "\t\tamount = 0", tail, flags=re.M)
    if n != 2:
        raise SystemExit("a fuel tank donor must carry exactly two RESOURCE amounts")
    return out


def _ore_fill(tail, units):
    if not (0 <= units <= ORE_MAX):
        raise SystemExit("ore fill %r outside 0..%d" % (units, ORE_MAX))
    if not re.search(r"^\t\tname = Ore\n\t\tamount = \S+\n\t\tmaxAmount = %d$" % ORE_MAX,
                     tail, re.M):
        raise SystemExit("the RadialOreTank donor must carry Ore with maxAmount %d" % ORE_MAX)
    return re.sub(r"^(\t\tname = Ore\n)\t\tamount = \S+$", r"\g<1>\t\tamount = %d" % units,
                  tail, flags=re.M)


def tails() -> Dict[str, str]:
    """Every tail this craft uses, lifted from its donor (see the docstring)."""
    dd1 = _blocks(_read(DONOR_DD1))
    kx = _blocks(_read(DONOR_KX))
    flea = _blocks(_read(DONOR_FLEA_PROBE))
    mun = _blocks(_read(DONOR_MUN_FLYBY))
    rover = _blocks(_read(DONOR_ROVER))
    ore = rover[ROVER_ORE_TANK][1]
    if "ModuleRestock" in ore:
        raise SystemExit("the RadialOreTank donor tail carries a ReStock module")
    return {
        "probeCoreOcto2.v2": _first_tail(dd1, "probeCoreOcto2.v2"),
        "sasModule": _first_tail(dd1, "sasModule"),
        "batteryBankMini": _first_tail(dd1, "batteryBankMini"),
        "fuelTankSmall": _empty_propellant(_first_tail(mun, "fuelTankSmall")),
        "radialDecoupler1-2": _first_tail(kx, "radialDecoupler1-2"),
        "fuelTankSmallFlat": _empty_propellant(_first_tail(flea, "fuelTankSmallFlat")),
        "RadialOreTank": ore,
    }


# ---------------------------------------------------------------------------
# Layout.
# ---------------------------------------------------------------------------

class Part(object):
    def __init__(self, key, name, pos, rot=(0.0, 0.0, 0.0, 1.0), istg=-1, sidx=-1,
                 sqor=-1, attm=0, parent=None, ore=None):
        self.key = key
        self.name = name
        self.pos = pos
        self.rot = rot
        self.istg = istg
        self.sidx = sidx
        self.sqor = sqor
        self.attm = attm
        self.parent = parent
        self.ore = ore
        self.links: List[str] = []
        self.syms: List[str] = []
        self.attn: List[Tuple[str, str, float]] = []
        self.srfn = None

    @property
    def uid(self):
        return "%s_%d" % (self.name, _UID[self.key])


def outward(az_deg):
    a = math.radians(az_deg)
    return (math.cos(a), 0.0, math.sin(a))


def azimuth_rotation(az_deg):
    """The rotation about the hull axis that takes Kerbal X's outward direction (-Z,
    the side its booster pair sits on) to the outward direction at ``az_deg``
    (measured from +X toward +Z)."""
    return _qy(-(az_deg + 90.0))


def kx_pose():
    """Kerbal X's decoupler and tank: (decoupler pos, decoupler rot, tank delta from the
    decoupler in the ship frame, tank rot, the core's pos)."""
    kx = _blocks(_read(DONOR_KX))
    dec_head, tank_head = kx[KX_DECOUPLER][0], kx[KX_TANK][0]
    core = re.search(r"^\tsrfN = srfAttach,(\S+?),", dec_head, re.M).group(1)
    core_head = kx[core][0]
    if _header_vec(core_head, "rot") != (0.0, 0.0, 0.0, 1.0):
        raise SystemExit("the Kerbal X core is not upright; the pose copy assumes it is")
    return (_header_vec(dec_head, "pos"), _header_vec(dec_head, "rot"),
            _sub(_header_vec(tank_head, "pos"), _header_vec(dec_head, "pos")),
            _header_vec(tank_head, "rot"), _header_vec(core_head, "pos"))


def rover_ore_outward_axis():
    """The RadialOreTank's local axis that points away from its parent, read off the
    rover: the parent-local radial direction at the attach point, taken back into the
    tank's own frame. About (0, -0.32, 0.95) for the committed rover."""
    rover = _blocks(_read(DONOR_ROVER))
    head = rover[ROVER_ORE_TANK][0]
    parent = re.search(r"^\tsrfN = srfAttach,(\S+?),", head, re.M).group(1)
    phead = rover[parent][0]
    prot, trot = _header_vec(phead, "rot"), _header_vec(head, "rot")
    local = _qrot(_qinv(prot), _sub(_header_vec(head, "pos"), _header_vec(phead, "pos")))
    radial = (local[0], 0.0, local[2])
    n = math.hypot(radial[0], radial[2])
    world_out = _qrot(prot, (radial[0] / n, 0.0, radial[2] / n))
    return tuple(round(c, 6) for c in _qrot(_qinv(trot), world_out))


def ore_rotation(az_deg):
    """Ore tank rotation at ``az_deg``: local +Z outward, local +Y up."""
    return _qy(90.0 - az_deg)


def layout() -> List[Part]:
    probe = Part("probe", "probeCoreOcto2.v2", (0.0, ROOT_Y, 0.0))
    sas_y = ROOT_Y + NODE_OFFSET["probeCoreOcto2.v2"]["bottom"] - NODE_OFFSET["sasModule"]["top"]
    sas = Part("sas", "sasModule", (0.0, sas_y, 0.0), parent=probe)
    batt_y = sas_y + NODE_OFFSET["sasModule"]["bottom"] - NODE_OFFSET["batteryBankMini"]["top"]
    batt = Part("batt", "batteryBankMini", (0.0, batt_y, 0.0), parent=sas)
    hull_y = batt_y + NODE_OFFSET["batteryBankMini"]["bottom"] - NODE_OFFSET["fuelTankSmall"]["top"]
    hull = Part("hull", "fuelTankSmall", (0.0, hull_y, 0.0), parent=batt)

    probe.links = [sas.uid]
    probe.attn = [("bottom", sas.uid, NODE_OFFSET["probeCoreOcto2.v2"]["bottom"])]
    sas.links = [batt.uid]
    sas.attn = [("top", probe.uid, NODE_OFFSET["sasModule"]["top"]),
                ("bottom", batt.uid, NODE_OFFSET["sasModule"]["bottom"])]
    batt.links = [hull.uid]
    batt.attn = [("top", sas.uid, NODE_OFFSET["batteryBankMini"]["top"]),
                 ("bottom", hull.uid, NODE_OFFSET["batteryBankMini"]["bottom"])]
    hull.attn = [("top", batt.uid, NODE_OFFSET["fuelTankSmall"]["top"])]

    kx_dec_pos, kx_dec_rot, kx_tank_delta, kx_tank_rot, kx_core_pos = kx_pose()
    # The decoupler origin's depth under the parent's skin, kept.
    kx_dec_r = math.hypot(kx_dec_pos[0] - kx_core_pos[0], kx_dec_pos[2] - kx_core_pos[2])
    dec_r = HULL_RADIUS - (KX_CORE_RADIUS - kx_dec_r)
    # The decoupler's outer face in its own frame (where Kerbal X's tank node sits).
    tank_local = _qrot(_qinv(kx_dec_rot), kx_tank_delta)
    face_local = (tank_local[0] + KX_TANK_RADIUS, tank_local[1], 0.0)
    axis = rover_ore_outward_axis()
    # The rover's parent is a hexagonal collider, so the part's normal is the hex FACE
    # normal (parent-local -X) while the attach point sits 18 deg round from it: the
    # radial direction there reads within about 20 deg of +Z, never the exact axis.
    if axis[2] < 0.9:
        raise SystemExit("the rover's RadialOreTank outward axis reads %r, not near +Z"
                         % (axis,))

    radial: List[Part] = []
    for pair, ring, az0, istg, ore in PAIRS:
        decs, tanks = [], []
        for member in (0, 1):
            az = az0 + 180.0 * member
            r = azimuth_rotation(az)
            dy = FLOAT_RING_DY if ring == "float" else BALLAST_RING_DY
            out = outward(az)
            dec_pos = (dec_r * out[0], hull_y + dy, dec_r * out[2])
            dec_rot = _qmul(r, kx_dec_rot)
            dec = Part("%s%d_dec" % (pair, member), "radialDecoupler1-2", dec_pos, dec_rot,
                       istg=istg, sidx=member, sqor=istg, attm=1, parent=hull)
            dec.srfn = hull.uid
            if ring == "float":
                tank = Part("%s%d_tank" % (pair, member), "fuelTankSmallFlat",
                            _add(dec_pos, _qrot(r, kx_tank_delta)), _qmul(r, kx_tank_rot),
                            istg=istg, attm=1, parent=dec)
            else:
                tank = Part("%s%d_tank" % (pair, member), "RadialOreTank",
                            _add(dec_pos, _qrot(dec_rot, face_local)),
                            ore_rotation(az), istg=istg, attm=1, parent=dec,
                            ore=ore)
            tank.srfn = dec.uid
            dec.links = [tank.uid]
            decs.append(dec)
            tanks.append(tank)
        for group in (decs, tanks):
            for p in group:
                p.syms = [o.uid for o in group if o is not p]
        for dec, tank in zip(decs, tanks):
            radial += [dec, tank]
    hull.links = [p.uid for p in radial if p.name == "radialDecoupler1-2"]
    return [probe, sas, batt, hull] + radial


def render_part(part: Part, tail: str) -> str:
    if part.parent is None:
        rel, rel_rot = part.pos, (0.0, 0.0, 0.0, 1.0)
    else:
        rel = _qrot(_qinv(part.parent.rot), _sub(part.pos, part.parent.pos))
        rel_rot = _qmul(_qinv(part.parent.rot), part.rot)
    out = ["PART", "{",
           "\tpart = %s" % part.uid,
           "\tpartName = Part",
           "\tpersistentId = %d" % _PERSISTENT_ID[part.key],
           "\tpos = %s" % _vec(part.pos),
           "\tattPos = 0,0,0",
           "\tattPos0 = %s" % _vec(rel),
           "\trot = %s" % _vec(part.rot),
           "\tattRot = 0,0,0,1",
           "\tattRot0 = %s" % _vec(rel_rot),
           "\tmir = 1,1,1",
           "\tsymMethod = Radial",
           "\tautostrutMode = Off",
           "\trigidAttachment = False",
           "\tistg = %d" % part.istg,
           "\tresPri = 0",
           "\tdstg = 0",
           "\tsidx = %d" % part.sidx,
           "\tsqor = %d" % part.sqor,
           "\tsepI = -1",
           "\tattm = %d" % part.attm,
           "\tmodCost = 0",
           "\tmodMass = 0",
           "\tmodSize = 0,0,0"]
    out += ["\tlink = %s" % link for link in part.links]
    out += ["\tsym = %s" % sym for sym in part.syms]
    for node_id, other, dy in part.attn:
        out.append("\tattN = %s,%s_0|%s|0" % (node_id, other, _fmt(dy)))
    if part.srfn:
        out.append("\tsrfN = srfAttach,%s" % part.srfn)
    out.append(tail)
    out.append("}")
    return "\n".join(out)


def build() -> str:
    t = tails()
    parts = layout()
    xs = [p.pos[0] for p in parts]
    ys = [p.pos[1] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % SHIP_NAME,
        "version = 1.12.5",
        "description = PSUB-1 stock submarine test craft. An uncrewed Octo2 probe on an "
        "empty FL-T200 hull with three pairs of empty FL-T100 floats and three pairs of "
        "Ore-filled RadialOreTank ballast on TT-70 decouplers. Stages fire the floats "
        "first (sink), then the ballast (rise). Built by construction by "
        "harness/tools/build_psub_craft.py.",
        "type = VAB",
        "size = %s" % _vec((max(xs) - min(xs) + 1.25, max(ys) - min(ys) + 1.2,
                            max(zs) - min(zs) + 1.25)),
        "steamPublishedFileId = 0",
        "persistentId = %d" % SHIP_PERSISTENT_ID,
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Probe",
    ]
    body = []
    for p in parts:
        tail = t[p.name]
        if p.name == "RadialOreTank":
            tail = _ore_fill(tail, p.ore)
        body.append(render_part(p, tail))
    return "\n".join(header) + "\n" + "\n".join(body) + "\n"


# ---------------------------------------------------------------------------
# Post-conditions (run in-process by harness/lib/test_psub_craft.py).
# ---------------------------------------------------------------------------

EXPECTED_PARTS = {"probeCoreOcto2.v2": 1, "sasModule": 1, "batteryBankMini": 1,
                  "fuelTankSmall": 1, "radialDecoupler1-2": 12, "fuelTankSmallFlat": 6,
                  "RadialOreTank": 6}


def part_headers(text) -> List[Tuple[str, Dict[str, List[str]], str]]:
    """[(token, {key: [values]}, tail)] for every PART, in file order."""
    out = []
    for token, (head, tail) in _blocks(text).items():
        d: Dict[str, List[str]] = {}
        for line in head.split("\n"):
            if line.startswith("\t") and not line.startswith("\t\t") and " = " in line:
                k, v = line[1:].split(" = ", 1)
                d.setdefault(k, []).append(v)
        out.append((token, d, tail))
    return out


def stage_table(text) -> Dict[int, List[str]]:
    """{istg: [part tokens of the decouplers it fires]}."""
    table: Dict[int, List[str]] = {}
    for token, d, _tail in part_headers(text):
        if token.startswith("radialDecoupler1-2_"):
            table.setdefault(int(d["istg"][0]), []).append(token)
    return table


def verify(text) -> List[str]:
    problems: List[str] = []
    if not text.startswith("ship = %s\nversion = 1.12.5\n" % SHIP_NAME):
        problems.append("first lines must be `ship = %s` / `version = 1.12.5`" % SHIP_NAME)
    if "—" in text or any(ord(c) > 127 for c in text):
        problems.append("non-ASCII byte in the craft")
    rows = part_headers(text)
    tokens = [t for t, _d, _tail in rows]
    if len(set(tokens)) != len(tokens):
        problems.append("duplicate part token")
    counts: Dict[str, int] = {}
    for t in tokens:
        counts[t.rsplit("_", 1)[0]] = counts.get(t.rsplit("_", 1)[0], 0) + 1
    if counts != EXPECTED_PARTS:
        problems.append("part census %r, want %r" % (counts, EXPECTED_PARTS))
    if tokens and not tokens[0].startswith("probeCoreOcto2.v2_"):
        problems.append("the root (first PART) must be the Octo2")
    tokenset = set(tokens)
    parent: Dict[str, str] = {}
    for token, d, _tail in rows:
        for link in d.get("link", []):
            if link not in tokenset:
                problems.append("%s links a missing part %s" % (token, link))
            if link in parent:
                problems.append("%s is linked from two parents" % link)
            parent[link] = token
        for srf in d.get("srfN", []):
            other = srf.split(",", 1)[1]
            if other not in tokenset:
                problems.append("%s surface-attaches to a missing part" % token)
            elif parent.get(token, other) != other and token in parent:
                problems.append("%s surface-attaches to %s but is linked from %s"
                                % (token, other, parent[token]))
        for sym in d.get("sym", []):
            if sym not in tokenset:
                problems.append("%s names a missing sym partner" % token)
    for token in tokens[1:]:
        if token not in parent:
            problems.append("%s has no parent `link =` line (KSP would drop it)" % token)
    # Every srfN names the linking parent.
    for token, d, _tail in rows:
        for srf in d.get("srfN", []):
            if parent.get(token) != srf.split(",", 1)[1]:
                problems.append("%s: srfN parent %s is not its link parent %s"
                                % (token, srf.split(",", 1)[1], parent.get(token)))
    # Staging: six pairs, floats at the three highest istg, then ballast heaviest first.
    table = stage_table(text)
    if sorted(table) != list(range(len(PAIRS))):
        problems.append("decoupler stages %r, want 0..%d" % (sorted(table), len(PAIRS) - 1))
    by_tank: Dict[str, str] = {}
    ore_by_stage: Dict[int, List[int]] = {}
    for token, d, tail in rows:
        name = token.rsplit("_", 1)[0]
        istg = int(d["istg"][0])
        if name in ("fuelTankSmallFlat", "RadialOreTank"):
            dec = parent.get(token, "")
            if not dec.startswith("radialDecoupler1-2_"):
                problems.append("%s is not held by a TT-70" % token)
                continue
            dec_istg = int(dict((t, dd) for t, dd, _ in rows)[dec]["istg"][0])
            if istg != dec_istg:
                problems.append("%s istg %d differs from its decoupler's %d"
                                % (token, istg, dec_istg))
            by_tank[token] = name
            if name == "RadialOreTank":
                m = re.search(r"^\t\tname = Ore\n\t\tamount = (\S+)$", tail, re.M)
                ore_by_stage.setdefault(istg, []).append(int(float(m.group(1))) if m else -1)
            else:
                if re.search(r"^\t\tamount = (?!0$)\S+$", tail, re.M):
                    problems.append("%s must be empty" % token)
        elif name in ("probeCoreOcto2.v2", "sasModule", "batteryBankMini", "fuelTankSmall"):
            if istg != -1:
                problems.append("%s istg %d, the core is never staged" % (token, istg))
            if name == "fuelTankSmall" and re.search(r"^\t\tamount = (?!0$)\S+$", tail, re.M):
                problems.append("the hull must be empty")
    for pair, ring, _az, istg, ore in PAIRS:
        decs = table.get(istg, [])
        if len(decs) != 2:
            problems.append("stage %d fires %d decouplers, want one pair" % (istg, len(decs)))
            continue
        kinds = set()
        for dec in decs:
            for t, kind in by_tank.items():
                if parent.get(t) == dec:
                    kinds.add(kind)
        want = "fuelTankSmallFlat" if ring == "float" else "RadialOreTank"
        if kinds != {want}:
            problems.append("stage %d drops %r, want a %s pair" % (istg, sorted(kinds), want))
        if ring == "ballast" and sorted(ore_by_stage.get(istg, [])) != [ore, ore]:
            problems.append("stage %d ore %r, want %d each" % (istg, ore_by_stage.get(istg), ore))
    float_stages = sorted(i for _p, r, _a, i, _o in PAIRS if r == "float")
    ballast_stages = sorted(i for _p, r, _a, i, _o in PAIRS if r == "ballast")
    if float_stages and ballast_stages and min(float_stages) <= max(ballast_stages):
        problems.append("a ballast stage fires before a float stage")
    if text.count("name = ModuleCommand") != 1:
        problems.append("exactly one ModuleCommand (the Octo2) expected")
    for forbidden in ("ModuleRestock", "ModuleParachute", "ModuleEngines", "crew = "):
        if forbidden in text:
            problems.append("unexpected %s in the craft" % forbidden)
    if re.search(r"actionGroup = (?!None$)\S+$", text, re.M):
        problems.append("an action group binding is set")
    return problems


def mass_tonnes(text) -> float:
    """The craft's mass from the part cfg masses named in the docstring."""
    dry = {"probeCoreOcto2.v2": 0.04, "sasModule": 0.05, "batteryBankMini": 0.01,
           "fuelTankSmall": 0.125, "radialDecoupler1-2": 0.05,
           "fuelTankSmallFlat": 0.0625, "RadialOreTank": 0.125}
    total = 0.0
    for token, _d, tail in part_headers(text):
        total += dry[token.rsplit("_", 1)[0]]
        m = re.search(r"^\t\tname = Ore\n\t\tamount = (\S+)$", tail, re.M)
        if m:
            total += 0.01 * float(m.group(1))
    return total


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args(argv)
    text = build()
    problems = verify(text)
    if problems:
        for p in problems:
            print("PROBLEM " + p)
        return 1
    if a.write:
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s (%d lines, %.2f t)" % (OUT, text.count("\n"), mass_tonnes(text)))
        return 0
    if not os.path.exists(OUT):
        print("MISSING %s - run --write" % OUT)
        return 1
    have = _read(OUT)
    ok = have == text
    print("OK committed craft matches the derivation" if ok
          else "DRIFT: committed craft differs from the derivation")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
