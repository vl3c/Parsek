#!/usr/bin/env python3
"""Build the committed DD1 Duna Direct Probe .craft BY CONSTRUCTION.

WHY THIS SCRIPT EXISTS. The duna-direct lane needs a craft no stock craft
provides: a PROBE-CONTROLLED direct Kerbin->Duna transfer-and-capture vehicle
with (1) a comfortable dV margin for ejection + corrections + capture, (2) real
torque (the B7 Ike flakiness and the whole DIY-correction-burner machinery exist
partly because the Kerbal X is low-torque: mlib's burnNoStartSeconds=600 covers
its ~340 s flip, maxAttitudeErrorDeg=30 covers its 0.06 deg/s AP convergence),
(3) a comm link that keeps FULL probe control throughout the Duna mission
envelope, and (4) NO deployables, NO action groups, NO radial decouplers -- the
mission-shell conventions and the recording-cleanliness constraints of the
"Looped re-aim interplanetary transfer" todo entry (no parking-orbit loiter, no
Ike encounter) leave no room for click-me-first parts. The stock Kerbal X flies
every B-lane today, but it is crewed, low-torque, and its committed budgets are
inertia-derived for THAT hull; this lane re-measures its own.

The construction method is build_gs1_craft.py's, verbatim: this file IS the
derivation record, `--check` re-derives and compares against the committed
bytes, and test_b17_duna_direct.py::CraftDriftTests runs both in-process so a
hand edit to the .craft (or a drifted derivation) reds in the unit suite
instead of in a live forge flight.

WHAT IS DERIVED AND WHAT IS COPIED, stated exactly:

  COPIED VERBATIM -- every PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE* /
  RESOURCE*) is lifted byte-for-byte out of a stock craft KSP itself wrote (the
  source craft is named above each TAILS entry). Unlike GS-1, NO tail line is
  rewritten at all: the donor tails already carry stock defaults everywhere
  (full propellant loads, engines unignited, every actionGroup = None), which
  the generator sweep verified before embedding and verify() re-asserts.

  DERIVED -- the scalar header of each PART (pos / rot / attN / srfN / istg /
  links). Stack arithmetic is GS-1's validated formula
      child.pos = parent.pos + parent_node_offset - child_opposite_node_offset
  with every stack-node offset read off a donor craft's own PIPE-FORMAT attN
  token (never a part cfg -- the parachuteSingle rescale trap). The one donor
  that lacks pipe tokens in the whole stock VAB set is the 1.2.0-era Viewmatic
  Survey Satellite, which is exactly why this craft carries OKTO2 + 2x
  inline reaction wheels instead of Viewmatic's HECS2, and RA-2 relays instead
  of its RA-100.

  RADIAL placement reuses GS-1's azimuth formulas for the two attach-axis
  families it validated (+X for winglet3, exactly basicFin's; -Z for
  solarPanels5, exactly parachuteRadial's) and adds ONE new family for the
  RA-2 (assumption A5 below).

  Radial-formula validation against stock, this craft's own parts:
  - winglet3 (+X family, psi = 180 - azimuth): Kerbal 2's three winglets sit
    at r = sqrt(1.46311688^2 + 0.844730914^2) = 1.6895 and their rot
    quaternions are this formula's outputs at azimuths 270/150/30 up to the
    quaternion double cover (e.g. az=30 -> psi=150 -> (0,0.9659,0,0.2588),
    stock stores the negation).
  - solarPanels5 (-Z family, psi = 90 - azimuth): Kerbal 1's four panels sit
    at r = 0.6218 on a 1.25 m tank and carry exactly psi = -90/0/90/-180 rot
    for azimuths 180/90/0/270.

HOW A FAILED FORGE RUN IS DIAGNOSED WITHOUT RE-DERIVING ANY OF THIS:

  A1  ROOT-RELATIVE ORIGIN. The probe core is placed at y = 15 (the Jumping
      Flea root convention). Editor-space absolute height is cosmetic; only
      the part DELTAS matter. Silly pad height = this assumption, not deltas.
  A2  RADIAL RADII. Winglet3 r = 1.6895 is MEASURED on Kerbal 2's 2.5 m tank
      and reused on the same-diameter X200-32. SolarPanels5 r = 0.6218 is
      MEASURED on Kerbal 1's 1.25 m tank and reused on the same-diameter
      FL-T800. The RA-2 radius 0.52603 is the one EXTRAPOLATED number: Space
      Station Core mounts an RA-2 at 1.15103 on a 1.25-radius body, giving a
      base sink of -0.09897, applied here to the 0.625-radius FL-T800. Radial
      parts clip freely (GS-1 A2), so an RA-2 visibly floating or sunk is this
      number and is cosmetic unless the joint fails.
  A3  STAGING. `istg` is the authority KSP reads. Two stages only:
      istg 1 ignites the Skipper (the pad click); istg 0 fires the TD-25 AND
      ignites the Terrier in the same stage (a standard hot-stage, which
      MechJeb autostage drives without any extra click). Booster tanks and
      winglets carry istg 0 (their separator's stage, the Kerbal X pattern);
      every transfer-stage part carries istg -1 (never staged).
  A4  MODULE DEFAULTS. NO tail line was rewritten: the donor tails carry
      stock cfg defaults (all resources full, thrustPercentage 100, all
      actionGroups None, OX-STAT deployState EXTENDED which is its static
      built-in state). verify() asserts the full-load amounts and that no
      actionGroup binding exists anywhere in the file.
  A5  RA-2 ATTACH FAMILY. The RA-2 surface-attaches along its local +Y (base
      at -Y, dish axis radially outward), so the rotation taking +Y to the
      outward normal (cos az, 0, sin az) is a 90-degree turn about the axis
      (sin az, 0, -cos az):
          quaternion = (0.7071*sin az, 0, -0.7071*cos az, 0.7071)
      VALIDATED against Space Station Core's RA-2 at azimuth 90:
      (0.70710665, 0, 0, 0.707107008) is this formula's output. The formula's
      free roll degree (spin about the dish axis) is canonicalized to zero,
      matching that donor. An RA-2 pointing along the tank instead of outward
      means this family assignment is wrong, and the remedy is reading the
      part's node_attach out of RA-5.cfg, not new arithmetic.
  A6  DV / TWR ARITHMETIC (every mass and Isp from the 1.12.5 part cfgs):
      transfer stage wet = 0.04 (OKTO2) + 2*0.01 (Z-200) + 2*0.05 (wheel)
      + 4*0.005 (OX-STAT) + 3*0.15 (RA-2) + 4.5 (FL-T800) + 0.5 (Terrier)
      = 5.63 t, dry 1.63 t; dv = 345 * 9.80665 * ln(5.63/1.63) = 4,193 m/s
      vacuum against a mission requirement of ~2,060 (ejection ~1,080 from a
      ~100 km park + corrections <= 450 per the B15 sizing + capture to a
      300 km circular Duna park ~460 + trim ~70). Booster: TD-25 0.16 +
      X200-16 9.0 + X200-32 18.0 + Skipper 3.0 + 4*0.078 winglet = 30.47 t;
      pad mass 36.10 t; pad TWR = 650*(280/320) / (36.10*9.80665) = 1.61;
      booster vacuum dv = 320 * 9.80665 * ln(36.10/12.10) = 3,429 m/s. The
      Terrier tops off the ascent; the margin left for the interplanetary
      legs stays > 1,700 m/s. If the forge or the B17 flight runs dry, this
      arithmetic (or the ascent-loss estimate ~3,400-3,500) was wrong.
  A7  COMM LINK. Three combinable RA-2 relays: combined power
      2G * 3^0.75 = 4.56G; range to the sandbox level-3 DSN (250G) =
      sqrt(4.56e9 * 2.5e11) = 33.8 Gm, against a worst-case Kerbin-craft
      control distance of ~26 Gm across the whole transfer + capture. The
      fixture save has requireSignalForControl = False, so losing link would
      degrade to PARTIAL control rather than none -- but MechJeb's executor
      is only trusted under FULL control, hence the margin. A mission that
      stalls mid-coast with attitude frozen is this assumption.
  A8  SINGLE ModuleCommand, DELIBERATELY. GS-1 put a probe core on its
      booster to force a multi-controllable split (its scenario needs the
      RewindPoint). This lane wants the OPPOSITE: the committed tree must be
      a clean main recording plus plain retired debris, so the booster has NO
      command part and the split authors debris only. verify() asserts
      exactly ONE ModuleCommand block in the whole file.

Usage:
    python harness/tools/build_dd1_craft.py --check     # verify committed bytes
    python harness/tools/build_dd1_craft.py --write     # regenerate the file

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)                       # harness/

# The committed craft lives in the FORGE BASE's Ships/VAB (the save the
# FORGE-b17-duna-pad scenario boots, and the directory kRPC launch_vessel
# resolves <save>/Ships/VAB/<craftName>.craft against). bdock-forge-base is
# reused rather than forked, the same call FORGE-gs1-two-stage made: one
# bootable base serves several forges, and adding a craft file costs no new
# fixture directory and no re-pin of test_saveparse's committed-fixture sweep.
BASE_NAME = "bdock-forge-base"
SHIP_NAME = "DD1 Duna Direct Probe"
CRAFT_PATH = os.path.join(_HARNESS_ROOT, "fixtures", "saves", BASE_NAME,
                          "Ships", "VAB", SHIP_NAME + ".craft")
# EVERY committed copy of the craft. The forge launches from
# bdock-forge-base, but b17-duna-pad and duna-direct-recorded each carry
# their own harvested copy under Ships/VAB -- and B17 boots b17-duna-pad,
# so a drift gate that checks only the forge base would stay green while
# the copy the nightly actually flies goes stale. --check and --write
# both walk this whole list.
COMMITTED_CRAFT_PATHS = tuple(
    os.path.join(_HARNESS_ROOT, "fixtures", "saves", base,
                 "Ships", "VAB", SHIP_NAME + ".craft")
    for base in (BASE_NAME, "b17-duna-pad", "duna-direct-recorded"))

# Root placement (assumption A1).
ROOT_Y = 15.0

# MEASURED radial radii (assumption A2).
WINGLET_RADIUS = 1.6895        # Kerbal 2's winglet3 on a 2.5 m body
SOLAR_RADIUS = 0.6218          # Kerbal 1's solarPanels5 on a 1.25 m body
RA2_RADIUS = 0.52603           # 0.625 - 0.09897 (SSC base sink, see A2)

# EFFECTIVE stack-node offsets, every one read off a donor craft's own
# pipe-format attN token. (part name) -> {"top": dy, "bottom": dy}
NODE_OFFSET = {
    # Z-MAP Satellite Launch Kit: attN = top,...|0.0610621|0, bottom mirrored
    "probeCoreOcto2.v2":     {"top": 0.0610621, "bottom": -0.0610621},
    # Z-MAP Satellite Launch Kit: attN = top,...|0.0911109|0, bottom mirrored
    "sasModule":             {"top": 0.0911109, "bottom": -0.0911109},
    # ComSat Lx: attN = top,...|0.1|0 and attN = bottom,...|-0.1|0
    "batteryBankMini":       {"top": 0.1, "bottom": -0.1},
    # Z-MAP Satellite Launch Kit: attN = top,...|1.875|0, bottom |-1.8875|0
    "fuelTank.long":         {"top": 1.875, "bottom": -1.8875},
    # Z-MAP Satellite Launch Kit: attN = top,...|0|0 (origin AT the top node)
    # and attN = bottom,...|-0.8|0
    "liquidEngine3.v2":      {"top": 0.0, "bottom": -0.8},
    # Kerbal X: attN = top,...|0.1|0 and attN = bottom,...|-0.1|0
    "Decoupler.2":           {"top": 0.1, "bottom": -0.1},
    # Kerbal X: attN = top,...|0.92|0 and attN = bottom,...|-0.92|0
    "Rockomax16.BW":         {"top": 0.92, "bottom": -0.92},
    # Kerbal X: attN = top,...|1.86|0 and attN = bottom,...|-1.86|0
    "Rockomax32.BW":         {"top": 1.86, "bottom": -1.86},
    # GDLV3: attN = top,...|1.01300001|0 (nothing stacks below an engine)
    "engineLargeSkipper.v2": {"top": 1.01300001},
}

# Local attach direction per surface-attachable part. Two families are GS-1's
# validated ones; +Y is new here (assumption A5).
SRF_ATTACH_AXIS = {
    "winglet3": "+X",        # same family as basicFin, validated on Kerbal 2
    "solarPanels5": "-Z",    # same family as parachuteRadial, val. on Kerbal 1
    "RelayAntenna5": "+Y",   # new family, validated on Space Station Core
}

# Deterministic part ids (unique within the file; fixed so the bytes are
# stable across runs -- the drift gate compares bytes).
#
# THE UInt32 CEILING IS LOAD-BEARING, measured the expensive way: KSP parses
# the `part = <name>_<uid>` suffix with System.UInt32.Parse, so any uid above
# 4,294,967,295 makes kRPC launch_vessel throw `RPCError: Value was either too
# large or too small for a UInt32` before the craft ever reaches the pad (the
# first FORGE-b17-duna-pad run, 2026-08-06, with the original 43xxxxxxxx ids).
# GS-1's 42xxxxxxxx ids fit by luck of the prefix; these sit just under the
# ceiling with room for the whole block.
_PART_UID = {
    "probe": 4250000001, "batt0": 4250000002, "batt1": 4250000003,
    "sas0": 4250000004, "sas1": 4250000005, "tank": 4250000006,
    "terrier": 4250000007, "dec": 4250000008, "x16": 4250000009,
    "x32": 4250000010, "skipper": 4250000011,
    "solar0": 4250000020, "solar1": 4250000021, "solar2": 4250000022,
    "solar3": 4250000023,
    "ra0": 4250000030, "ra1": 4250000031, "ra2": 4250000032,
    "wing0": 4250000040, "wing1": 4250000041, "wing2": 4250000042,
    "wing3": 4250000043,
}
assert max(_PART_UID.values()) <= 0xFFFFFFFF, "part uid exceeds UInt32"
assert len(set(_PART_UID.values())) == len(_PART_UID), \
    "duplicate part uid: part references would be ambiguous"
_PERSISTENT_ID = {k: 1600000000 + i * 7717 for i, k in enumerate(sorted(_PART_UID))}
# The UInt32 ceiling applies to EVERY id kRPC/KSP parses, not just part
# uids: the per-part persistentIds and the ship-level persistentId ride the
# same parse (the original >UInt32 uids cost a live forge run).
assert max(_PERSISTENT_ID.values()) <= 0xFFFFFFFF, \
    "persistentId exceeds UInt32"

# THE STAGING TABLE (assumption A3). KSP fires stages in DESCENDING istg
# order: istg 1 ignites the Skipper on the pad click; istg 0 fires the TD-25
# and ignites the Terrier together (hot-stage).
STAGE_IGNITE = 1
STAGE_SEPARATE = 0


def _fmt(v: float) -> str:
    """Format a coordinate the way KSP writes one: shortest round-trippable
    form, no exponent for the magnitudes this craft uses."""
    if v == 0:
        return "0"
    s = repr(round(v, 9))
    if s.endswith(".0"):
        s = s[:-2]
    return s


def _vec(x: float, y: float, z: float) -> str:
    return "%s,%s,%s" % (_fmt(x), _fmt(y), _fmt(z))


def _quat_y(psi_deg: float):
    """Quaternion (x, y, z, w) for a pure rotation of psi about world Y."""
    h = math.radians(psi_deg) / 2.0
    return (0.0, math.sin(h), 0.0, math.cos(h))


def _fmt_quat(q) -> str:
    return ",".join(_fmt(round(c, 9)) for c in q)


def srf_rotation(part_name: str, azimuth_deg: float) -> str:
    """The rot quaternion for ``part_name`` surface-attached at
    ``azimuth_deg`` (measured from +X toward +Z) on a vertical cylinder."""
    axis = SRF_ATTACH_AXIS[part_name]
    if axis == "+X":
        return _fmt_quat(_quat_y(180.0 - azimuth_deg))
    if axis == "-Z":
        return _fmt_quat(_quat_y(90.0 - azimuth_deg))
    if axis == "+Y":
        a = math.radians(azimuth_deg)
        s = math.sin(math.radians(45.0))
        return _fmt_quat((s * math.sin(a), 0.0, -s * math.cos(a),
                          math.cos(math.radians(45.0))))
    raise ValueError("unhandled surface attach axis %r" % (axis,))


def srf_position(center, azimuth_deg: float, dy: float, radius: float):
    """World position of a surface attachment at ``azimuth_deg`` on the
    cylinder whose axis passes through ``center``, offset ``dy`` along it."""
    a = math.radians(azimuth_deg)
    return (center[0] + radius * math.cos(a),
            center[1] + dy,
            center[2] + radius * math.sin(a))


class Part(object):
    """One authored PART: identity, geometry, staging, and links."""

    def __init__(self, key, name, pos, rot="0,0,0,1", istg=-1, dstg=0, sidx=-1,
                 sqor=-1, attm=0):
        self.key = key
        self.name = name
        self.pos = pos
        self.rot = rot
        self.istg = istg
        self.dstg = dstg
        self.sidx = sidx
        self.sqor = sqor
        self.attm = attm
        self.links = []
        self.syms = []
        self.attn = []
        self.srfn = None

    @property
    def uid(self):
        return "%s_%d" % (self.name, _PART_UID[self.key])


def tail_for(part: Part) -> str:
    return TAILS[part.name]


def _stack_down(parent: Part, child_key: str, child_name: str, **kw) -> Part:
    """Place ``child_name`` below ``parent`` on the stack axis."""
    y = (parent.pos[1] + NODE_OFFSET[parent.name]["bottom"]
         - NODE_OFFSET[child_name]["top"])
    return Part(child_key, child_name, (0.0, y, 0.0), **kw)


def _stack_up(parent: Part, child_key: str, child_name: str, **kw) -> Part:
    """Place ``child_name`` above ``parent`` on the stack axis."""
    y = (parent.pos[1] + NODE_OFFSET[parent.name]["top"]
         - NODE_OFFSET[child_name]["bottom"])
    return Part(child_key, child_name, (0.0, y, 0.0), **kw)


def layout():
    """Build the full ordered part list with every position derived. The
    root is element 0 (KSP takes the first PART as the root)."""
    probe = Part("probe", "probeCoreOcto2.v2", (0.0, ROOT_Y, 0.0))

    batt0 = _stack_up(probe, "batt0", "batteryBankMini")
    batt1 = _stack_up(batt0, "batt1", "batteryBankMini")

    sas0 = _stack_down(probe, "sas0", "sasModule")
    sas1 = _stack_down(sas0, "sas1", "sasModule")
    tank = _stack_down(sas1, "tank", "fuelTank.long")
    terrier = _stack_down(tank, "terrier", "liquidEngine3.v2",
                          istg=STAGE_SEPARATE, dstg=1, sidx=1,
                          sqor=STAGE_SEPARATE)
    dec = _stack_down(terrier, "dec", "Decoupler.2",
                      istg=STAGE_SEPARATE, dstg=2, sidx=0,
                      sqor=STAGE_SEPARATE)
    x16 = _stack_down(dec, "x16", "Rockomax16.BW", istg=STAGE_SEPARATE, dstg=3)
    x32 = _stack_down(x16, "x32", "Rockomax32.BW", istg=STAGE_SEPARATE, dstg=3)
    skipper = _stack_down(x32, "skipper", "engineLargeSkipper.v2",
                          istg=STAGE_IGNITE, dstg=3, sidx=0, sqor=STAGE_IGNITE)

    # Stack links + attN, parent -> child downward (plus the root's upward
    # chain), mirroring GS-1 / stock conventions.
    probe.links = [batt0.uid, sas0.uid]
    probe.attn = [("top", batt0.uid, NODE_OFFSET["probeCoreOcto2.v2"]["top"]),
                  ("bottom", sas0.uid,
                   NODE_OFFSET["probeCoreOcto2.v2"]["bottom"])]
    batt0.links = [batt1.uid]
    batt0.attn = [("top", batt1.uid, NODE_OFFSET["batteryBankMini"]["top"]),
                  ("bottom", probe.uid,
                   NODE_OFFSET["batteryBankMini"]["bottom"])]
    batt1.attn = [("bottom", batt0.uid,
                   NODE_OFFSET["batteryBankMini"]["bottom"])]
    sas0.links = [sas1.uid]
    sas0.attn = [("top", probe.uid, NODE_OFFSET["sasModule"]["top"]),
                 ("bottom", sas1.uid, NODE_OFFSET["sasModule"]["bottom"])]
    sas1.links = [tank.uid]
    sas1.attn = [("top", sas0.uid, NODE_OFFSET["sasModule"]["top"]),
                 ("bottom", tank.uid, NODE_OFFSET["sasModule"]["bottom"])]
    tank.links = [terrier.uid]
    tank.attn = [("top", sas1.uid, NODE_OFFSET["fuelTank.long"]["top"]),
                 ("bottom", terrier.uid,
                  NODE_OFFSET["fuelTank.long"]["bottom"])]
    terrier.links = [dec.uid]
    terrier.attn = [("top", tank.uid, NODE_OFFSET["liquidEngine3.v2"]["top"]),
                    ("bottom", dec.uid,
                     NODE_OFFSET["liquidEngine3.v2"]["bottom"])]
    dec.links = [x16.uid]
    dec.attn = [("top", terrier.uid, NODE_OFFSET["Decoupler.2"]["top"]),
                ("bottom", x16.uid, NODE_OFFSET["Decoupler.2"]["bottom"])]
    x16.links = [x32.uid]
    x16.attn = [("top", dec.uid, NODE_OFFSET["Rockomax16.BW"]["top"]),
                ("bottom", x32.uid, NODE_OFFSET["Rockomax16.BW"]["bottom"])]
    x32.links = [skipper.uid]
    x32.attn = [("top", x16.uid, NODE_OFFSET["Rockomax32.BW"]["top"]),
                ("bottom", skipper.uid,
                 NODE_OFFSET["Rockomax32.BW"]["bottom"])]
    skipper.attn = [("top", x32.uid,
                     NODE_OFFSET["engineLargeSkipper.v2"]["top"])]

    # FOUR static OX-STAT panels at 90-degree spacing, upper band of the
    # FL-T800; at least one faces the sun in any attitude, and 4 * 0.35 EC/s
    # (Kerbin-distance rate) against a ~0.5 EC/s worst-case wheel draw plus
    # 405 banked EC keeps the probe powered through every maneuver (A6/A7).
    solars = []
    for i in range(4):
        az = 90.0 * i
        p = Part("solar%d" % i, "solarPanels5",
                 srf_position(tank.pos, az, 0.6, SOLAR_RADIUS),
                 rot=srf_rotation("solarPanels5", az), attm=1)
        p.srfn = tank.uid
        solars.append(p)
    for p in solars:
        p.syms = [o.uid for o in solars if o is not p]

    # THREE RA-2 relays at 120-degree spacing, lower band (assumption A5/A7).
    ras = []
    for i, az in enumerate((30.0, 150.0, 270.0)):
        p = Part("ra%d" % i, "RelayAntenna5",
                 srf_position(tank.pos, az, -0.7, RA2_RADIUS),
                 rot=srf_rotation("RelayAntenna5", az), attm=1)
        p.srfn = tank.uid
        ras.append(p)
    for p in ras:
        p.syms = [o.uid for o in ras if o is not p]

    # FOUR Delta-Deluxe winglets at 45/135/225/315, low on the X200-32:
    # control authority through maxQ before the gimbals take over fully.
    wings = []
    for i, az in enumerate((45.0, 135.0, 225.0, 315.0)):
        p = Part("wing%d" % i, "winglet3",
                 srf_position(x32.pos, az, -1.2, WINGLET_RADIUS),
                 rot=srf_rotation("winglet3", az),
                 istg=STAGE_SEPARATE, dstg=3, attm=1)
        p.srfn = x32.uid
        wings.append(p)
    for p in wings:
        p.syms = [o.uid for o in wings if o is not p]

    tank.links += [p.uid for p in solars] + [p.uid for p in ras]
    x32.links += [p.uid for p in wings]
    return ([probe, batt0, batt1, sas0, sas1, tank, terrier, dec, x16, x32,
             skipper] + solars + ras + wings)


def render_part(part: Part, root_pos, is_root: bool = False) -> str:
    rel = (part.pos if is_root else
           (part.pos[0] - root_pos[0], part.pos[1] - root_pos[1],
            part.pos[2] - root_pos[2]))
    out = ["PART", "{"]
    out.append("\tpart = %s" % part.uid)
    out.append("\tpartName = Part")
    out.append("\tpersistentId = %d" % _PERSISTENT_ID[part.key])
    out.append("\tpos = %s" % _vec(*part.pos))
    out.append("\tattPos = 0,0,0")
    out.append("\tattPos0 = %s" % _vec(*rel))
    out.append("\trot = %s" % part.rot)
    out.append("\tattRot = 0,0,0,1")
    out.append("\tattRot0 = %s" % (part.rot if part.attm else "0,0,0,1"))
    out.append("\tmir = 1,1,1")
    out.append("\tsymMethod = Radial")
    out.append("\tautostrutMode = Off")
    out.append("\trigidAttachment = False")
    out.append("\tistg = %d" % part.istg)
    out.append("\tresPri = 0")
    out.append("\tdstg = %d" % part.dstg)
    out.append("\tsidx = %d" % part.sidx)
    out.append("\tsqor = %d" % part.sqor)
    out.append("\tsepI = -1")
    out.append("\tattm = %d" % part.attm)
    out.append("\tmodCost = 0")
    out.append("\tmodMass = 0")
    out.append("\tmodSize = 0,0,0")
    for link in part.links:
        out.append("\tlink = %s" % link)
    for sym in part.syms:
        out.append("\tsym = %s" % sym)
    for node_id, other, dy in part.attn:
        out.append("\tattN = %s,%s_0|%s|0" % (node_id, other, _fmt(dy)))
    if part.srfn:
        out.append("\tsrfN = srfAttach,%s" % part.srfn)
    out.append(tail_for(part))
    out.append("}")
    return "\n".join(out)


def build():
    """Render the whole .craft as a list of lines."""
    parts = layout()
    root = parts[0].pos
    xs = [p.pos[0] for p in parts]
    ys = [p.pos[1] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % SHIP_NAME,
        "version = 1.12.5",
        "description = DD1 Duna Direct Probe. Probe-controlled direct "
        "Kerbin-Duna transfer-and-capture vehicle: OKTO2 + two inline "
        "reaction wheels + three RA-2 relays + FL-T800/Terrier transfer "
        "stage on a Skipper/X200 booster. No deployables, no action groups, "
        "single ModuleCommand. Built by construction by "
        "harness/tools/build_dd1_craft.py.",
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 2.5, max(ys) - min(ys) + 2.0,
                           max(zs) - min(zs) + 2.5),
        "steamPublishedFileId = 0",
        "persistentId = 2200110044",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Probe",
    ]
    body = [render_part(p, root, is_root=(i == 0)) for i, p in enumerate(parts)]
    return ("\n".join(header) + "\n" + "\n".join(body) + "\n").split("\n")


# ---------------------------------------------------------------------------
# Post-conditions. `verify` is the `--check` path and is run in-process by the
# unit suite, so a hand-edited craft reds there rather than in a forge flight.
# ---------------------------------------------------------------------------

EXPECTED_PARTS = {
    "probeCoreOcto2.v2": 1, "batteryBankMini": 2, "sasModule": 2,
    "fuelTank.long": 1, "liquidEngine3.v2": 1, "solarPanels5": 4,
    "RelayAntenna5": 3, "Decoupler.2": 1, "Rockomax16.BW": 1,
    "Rockomax32.BW": 1, "engineLargeSkipper.v2": 1, "winglet3": 4,
}


def read_lines(path: str):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


def part_records(lines):
    """[(partName, {key: value})] for every PART, scalar header fields only."""
    records = []
    current = None
    depth = 0
    for line in lines:
        s = line.strip()
        if s == "PART" and current is None:
            current = {}
            depth = 0
            continue
        if current is None:
            continue
        if s == "{":
            depth += 1
            continue
        if s == "}":
            depth -= 1
            if depth == 0:
                records.append(current)
                current = None
            continue
        if depth == 1 and " = " in s:
            k, v = s.split(" = ", 1)
            if k in ("link", "sym", "attN"):
                current.setdefault(k, []).append(v)
            else:
                current[k] = v
    return [(r.get("part", "?").rsplit("_", 1)[0], r) for r in records]


def verify(lines):
    """Every post-condition the craft must satisfy. Returns a list of
    problems (empty = good). Pure text, no KSP."""
    problems = []
    # Reference resolvability: every link/sym/attN/srfN target must be a
    # declared part. Part names are dot-form in a .craft, so 'name_uid' has
    # exactly one underscore and parses unambiguously.
    declared = set()
    for ln in lines:
        m = re.match(r"\s*part = ([^_\s]+_\d+)\s*$", ln)
        if m:
            declared.add(m.group(1))
    for ln in lines:
        m = re.match(r"\s*(link|sym) = ([^_\s]+_\d+)\s*$", ln)
        if m and m.group(2) not in declared:
            problems.append("%s references undeclared part %s"
                            % (m.group(1), m.group(2)))
        m = re.match(r"\s*(attN|srfN) = [^,]+,([^_\s|]+_\d+)", ln)
        if m and m.group(2) not in declared:
            problems.append("%s references undeclared part %s"
                            % (m.group(1), m.group(2)))
    text = "\n".join(lines)
    if not lines or not lines[0].startswith("ship = %s" % SHIP_NAME):
        problems.append("first line must be `ship = %s`" % SHIP_NAME)
    if "version = 1.12.5" not in text:
        problems.append("craft must declare version = 1.12.5 (an older "
                        "version raises the compat dialog no unattended run "
                        "can click)")
    if "type = VAB" not in text:
        problems.append("craft must be a VAB craft (type = VAB)")

    records = part_records(lines)
    counts = {}
    for name, _rec in records:
        counts[name] = counts.get(name, 0) + 1
    if counts != EXPECTED_PARTS:
        problems.append("part census %r != expected %r" % (counts, EXPECTED_PARTS))
    if not records or records[0][0] != "probeCoreOcto2.v2":
        problems.append("the ROOT (first PART) must be the OKTO2 probe core; "
                        "KSP takes the first PART as the root and the "
                        "mission flies the transfer stage")

    by_name = {}
    for name, rec in records:
        by_name.setdefault(name, []).append(rec)

    def istg_of(name):
        return sorted({int(r["istg"]) for r in by_name.get(name, [])})

    # THE STAGING CONTRACT (assumption A3).
    if istg_of("engineLargeSkipper.v2") != [STAGE_IGNITE]:
        problems.append("the Skipper must ignite alone in istg %d, got %r"
                        % (STAGE_IGNITE, istg_of("engineLargeSkipper.v2")))
    if istg_of("Decoupler.2") != [STAGE_SEPARATE]:
        problems.append("the TD-25 must fire in istg %d, got %r"
                        % (STAGE_SEPARATE, istg_of("Decoupler.2")))
    if istg_of("liquidEngine3.v2") != [STAGE_SEPARATE]:
        problems.append("the Terrier must hot-stage in the SAME istg as the "
                        "TD-25 (%d), got %r"
                        % (STAGE_SEPARATE, istg_of("liquidEngine3.v2")))
    if not STAGE_IGNITE > STAGE_SEPARATE:
        problems.append("KSP fires stages in DESCENDING istg order; ignite "
                        "must be strictly above separate")

    # SINGLE COMMAND PART (assumption A8): the booster split must author
    # plain debris, never a multi-controllable split.
    n_command = text.count("name = ModuleCommand")
    if n_command != 1:
        problems.append("expected exactly ONE ModuleCommand (the OKTO2; the "
                        "booster must become plain debris), found %d"
                        % n_command)

    # NO ACTION-GROUP BINDINGS anywhere (assumption A4): every actionGroup
    # line a donor carried is None, and the mission conventions require it.
    for line in lines:
        s = line.strip()
        if s.startswith("actionGroup = ") and s != "actionGroup = None":
            problems.append("unexpected action-group binding %r; the craft "
                            "must carry none" % s)

    # FULL PROPELLANT LOADS at stock cfg values (assumption A4/A6).
    for token, label in (("\t\tamount = 360", "FL-T800 LiquidFuel 360"),
                         ("\t\tamount = 440", "FL-T800 Oxidizer 440"),
                         ("\t\tamount = 720", "X200-16 LiquidFuel 720"),
                         ("\t\tamount = 880", "X200-16 Oxidizer 880"),
                         ("\t\tamount = 1440", "X200-32 LiquidFuel 1440"),
                         ("\t\tamount = 1760", "X200-32 Oxidizer 1760")):
        if token not in text:
            problems.append("missing full propellant load: %s" % label)

    # THE COMM LINK (assumption A7): three RA-2 relays, no deployable
    # antennas anywhere.
    if len(by_name.get("RelayAntenna5", [])) != 3:
        problems.append("the comm link needs exactly THREE RA-2 relays")
    if "ModuleDeployableAntenna" in text:
        problems.append("a deployable antenna crept in; the craft must have "
                        "no deployables")

    # Radial parts must reference the body they hang off.
    for name, host in (("solarPanels5", "fuelTank.long"),
                       ("RelayAntenna5", "fuelTank.long"),
                       ("winglet3", "Rockomax32.BW")):
        for rec in by_name.get(name, []):
            if not rec.get("srfN", "").startswith("srfAttach,%s" % host):
                problems.append("%s must be surface-attached to the %s, "
                                "got %r" % (name, host, rec.get("srfN")))
    return problems


def main(argv=None) -> int:
    p = argparse.ArgumentParser(
        prog="build_dd1_craft",
        description="Build or verify the committed DD1 Duna Direct Probe "
                    ".craft.")
    p.add_argument("--check", action="store_true",
                   help="verify the COMMITTED craft against every "
                        "post-condition AND against a fresh rebuild; writes "
                        "nothing")
    p.add_argument("--write", action="store_true",
                   help="regenerate the committed craft file")
    args = p.parse_args(argv)
    if not args.check and not args.write:
        p.error("pass --check or --write")

    built = build()
    if args.write:
        for path in COMMITTED_CRAFT_PATHS:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            # CRLF, matching what KSP itself writes on Windows and what the
            # sibling committed craft carry. read_lines normalizes, so the
            # drift comparison is line-based either way.
            with open(path, "w", encoding="utf-8", newline="\r\n") as fh:
                fh.write("\n".join(built))
            sys.stdout.write("[DD1Craft] wrote %s (%d lines)\n"
                             % (path, len(built)))

    problems = verify(built)
    for path in COMMITTED_CRAFT_PATHS:
        if os.path.isfile(path):
            committed = read_lines(path)
            problems.extend(verify(committed))
            if committed != built:
                problems.append("the committed craft at %s has DRIFTED from "
                                "what this script produces; re-run with "
                                "--write and commit, or explain the "
                                "divergence" % path)
        else:
            problems.append("committed craft not found at %s" % path)

    for problem in problems:
        sys.stdout.write("[DD1Craft] PROBLEM: %s\n" % problem)
    if problems:
        return 1
    sys.stdout.write("[DD1Craft] OK: %s satisfies every post-condition\n"
                     % CRAFT_PATH)
    return 0


# ---------------------------------------------------------------------------
# VERBATIM STOCK PART TAILS. Each entry is the EVENTS-onward span of one PART
# node, lifted byte-for-byte out of a stock KSP craft that KSP itself wrote
# (the source craft is named above each entry). Nothing here is authored and
# nothing is rewritten: the donor tails already carry stock defaults (full
# resources, engines unignited, every actionGroup None), which verify()
# asserts.
# ---------------------------------------------------------------------------
TAILS = {
    # verbatim from stock VAB/Z-MAP Satellite Launch Kit.craft
    'probeCoreOcto2.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSAS\n\t\tisEnabled = True\n\t\tstandaloneToggle = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleKerbNetAccess\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOpenKerbNetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 5\n\t\tmaxAmount = 5\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Z-MAP Satellite Launch Kit.craft
    'sasModule': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleReactionWheel\n\t\tisEnabled = True\n\t\tactuatorModeCycle = 0\n\t\tauthorityLimiter = 100\n\t\tstateString = Active\n\t\tstagingEnabled = True\n\t\tWheelState = Active\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCycleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDeactivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Z-MAP Satellite Launch Kit.craft
    'fuelTank.long': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 360\n\t\tmaxAmount = 360\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 440\n\t\tmaxAmount = 440\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Z-MAP Satellite Launch Kit.craft
    'liquidEngine3.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = TallShroud\n\t\tisJettisoned = True\n\t\tshroudHideOverride = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tselectedVariant = TrussMount\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/ComSat Lx.craft
    'batteryBankMini': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 200\n\t\tmaxAmount = 200\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Kerbal 1.craft
    'solarPanels5': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDeployableSolarPanel\n\t\tisEnabled = True\n\t\tefficiencyMult = 1\n\t\tlaunchUT = -1\n\t\tcurrentRotation = (0, -6.057208E-07, 0, 1)\n\t\tstoredAnimationTime = 0\n\t\tstoredAnimationSpeed = 0\n\t\tdeployState = EXTENDED\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tExtendPanelsAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tExtendAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tRetractAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Space Station Core.craft
    'RelayAntenna5': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Kerbal X.craft
    'Decoupler.2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDecouple\n\t\tisEnabled = True\n\t\tejectionForcePercent = 100\n\t\tisDecoupled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleToggleCrossfeed\n\t\tisEnabled = True\n\t\tcrossfeedStatus = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tEnableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDisableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Kerbal X.craft
    'Rockomax16.BW': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 720\n\t\tmaxAmount = 720\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 880\n\t\tmaxAmount = 880\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Kerbal X.craft
    'Rockomax32.BW': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 1440\n\t\tmaxAmount = 1440\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 1760\n\t\tmaxAmount = 1760\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/GDLV3.craft
    'engineLargeSkipper.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = obj_fairing\n\t\tisJettisoned = True\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAlternator\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Kerbal 2.craft
    'winglet3': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleControlSurface\n\t\tisEnabled = True\n\t\tmirrorDeploy = False\n\t\tusesMirrorDeploy = True\n\t\tignorePitch = False\n\t\tignoreYaw = False\n\t\tignoreRoll = False\n\t\tdeploy = False\n\t\tdeployInvert = False\n\t\tpartDeployInvert = False\n\t\tauthorityLimiter = 100\n\t\tstagingEnabled = True\n\t\tdeployAngle = 25\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tActionToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActionExtend\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActionRetract\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}

if __name__ == "__main__":
    sys.exit(main())
