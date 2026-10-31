#!/usr/bin/env python3
"""Build `CS Flea Probe.craft`, RW-5's craft, BY CONSTRUCTION as a DERIVATIVE of the Flea.

WHY THIS CRAFT EXISTS. A Re-Fly rewind point is authored only when a split leaves two or more
CONTROLLABLE vessels (`SegmentBoundaryLogic.IsMultiControllableSplit`; controllable is
`ParsekFlight.IsTrackableVessel`: a part with ModuleCommand, an EVA kerbal or a space
object; an EVA split is refused by `RewindPointAuthor.IsReFlySplitType`). Every craft the
`cs-career-mun4` career can launch carries ONE command part, so no lane on that host could
author a rewind point. This craft adds a second command part that separates on the pad.

THE CRAFT. The shared `harness/fixtures/ships/Jumping Flea.craft` (Mk16 chute, Mk1 pod with
two Goo units, RT-5 Flea booster, three fins), plus a PROBE STAND radially attached to the
booster at azimuth 270 (60 degrees from the fins at 210 and 330):

    TT-38K radial decoupler   on the booster's side, staging DISABLED-FORCE (see below)
    FL-T100 fuel tank         surface-attached to the decoupler, standing upright, its
                              bottom level with the booster's bottom
    Stayputnik probe core     on the tank's top node

Every added part is researched and purchased on the host (persistent.sfs ResearchAndDevelopment:
radialDecoupler on `stability`, fuelTankSmallFlat on `basicRocketry`, probeCoreSphere.v2 on
`basicScience`; BypassEntryPurchaseAfterResearch is True there).

WHY NOT A TD-12 STACK, which was the first plan. (1) The Stayputnik has ONE stack node
(probeStackSphere_v2.cfg: only `node_stack_bottom`), so nothing can sit on it, and the Mk1
pod's top node is the chute's. (2) A vertical split leaves the upper half RESTING on the
lower one, and stock sets `Vessel.Landed` only from a collision with layer 15 (terrain,
the pad; decompiled `Part.OnCollisionEnter` / `CheckOtherGroundContact`), so the upper half
reads FLYING: its recording ends SubOrbital, and an unloaded vessel flying low in the
atmosphere is not safe to leave behind at a scene change. Side by side, both halves stand
on the pad after the decouple: the Flea on its booster (the shape RW-2 and RW-3 EVA from)
and the stand on its tank.

THE SPLIT. `PartAction button=decouple part=radialDecoupler` on the pad. The TT-38K's
explosive node is `srf` (decouplerRadialTT-38K.cfg), so it breaks its own attachment to
the booster and leaves WITH the stand: vessel (a) keeps the root (pod, Goo, chute, booster,
fins: the Jumping Flea, crewed), vessel (b) is the decoupler, the tank and the Stayputnik.
The control point stays on the root, so stock does not switch focus. Both carry
ModuleCommand, so the split is multi-controllable and authors a rewind point. The
Stayputnik needs no battery: IsTrackableVessel counts ModuleCommand, not power, and the
part carries 10 EC of its own anyway.

STAGING (istg; KSP fires the highest first): stage 2 = the TT-38K (drops the stand on the
pad), stage 1 = the RT-5 (the Flea's own, unchanged), stage 0 = the Mk16 chute (unchanged).
The lane never stages: it decouples through the part menu.

WHAT IS COPIED AND WHAT IS DERIVED (build_cs_flea_antenna_craft.py's contract):
  COPIED - every Jumping Flea PART block byte for byte, except its `persistentId` (below)
  and the booster's one new `link` line; each appended PART's tail (EVENTS / ACTIONS /
  PARTDATA / MODULE* / RESOURCE*) is lifted byte for byte out of a stock craft KSP wrote
  (TAILS names the donor; `--verify-donors <Ships dir>` re-extracts and compares).
  ONE TAIL VALUE IS REWRITTEN: the TT-38K's `ejectionForcePercent = 100` becomes `0` (the
  part's own editor slider, ModuleDecouplerBase). A full 250 kN kick throws a 0.64 t stand
  sideways at several m/s into the pad, against the FL-T100's crash tolerance of 6; at 0 the
  joint just goes and the stand stays where it stood.
  FRESH persistentIds for the vessel header and every part: a craft-baked persistentId is
  reused on every launch (.claude/CLAUDE.md, the persistentId trap), and RW-5 records a
  Jumping Flea and this craft in the same save, so they must not share one.
  DERIVED - the appended parts' scalar header (pos / rot / attPos0 / attRot0 / attN / srfN /
  links / istg), and the header's `size` z extent.

THE GEOMETRY, every number from a donor's own tokens:
  - TT-38K on a 1.25 m cylinder: Kerbal 1 (stock VAB, 1.12.0) carries two, on its FL-T400 at
    azimuths 180 and 0, at radius 0.630227923 from the host axis with rot yaw(0) and
    yaw(180): the fin rule psi = 180 - azimuth (a +X attach node), validated for basicFin
    by build_gs1_craft.py. The RT-5 is 1.25 m as well (its node_attach radius 0.625).
  - The child on the decoupler: Kerbal 1's RT-10 sits at attPos0 x = -0.786410213 in the
    decoupler's frame on both decouplers (its srf node is 0.625 off its axis, as the
    FL-T100's is), so the tank's centre is that far outward. An FL-T100 attaches along its
    local +X (fuelTankT100.cfg node_attach), the decoupler's own direction, so its rotation
    relative to the decoupler is identity and its world rotation is the decoupler's.
  - Heights: the RT-5's bottom node is -0.9975 (solidBoosterRT-5_v2.cfg, rescaleFactor 1,
    scale 1; no stock craft attaches it); the FL-T100's nodes are +-0.3125 (Kerbal 1's attN
    tokens); the Stayputnik's bottom node is -0.27448 (Bug-E Buggy's attN token). The tank's
    bottom is set level with the booster's, so the craft stands on both. The decoupler
    sits at the fins' band (the base fins' attPos0 y, -0.118541718 below the booster's
    centre), the one height where the Flea's own file measures the body radius (its fins
    at 0.622972488), so the TT-38K's Kerbal 1 radius is known to sit on the surface there.
    The tank hangs below it at the matching offset in the decoupler's frame (Kerbal 1's
    RT-10 hangs -0.49296093 below its own TT-38K the same way).

THE NUMBERS (1.12.5 part cfgs; a part's cost includes its full resources): 11 parts (the
level-0 VAB allows 30); cost 3982 = the Flea's 2932 (the rollout CS-3b / CS-4b / CS-6b
measured on this career line) + TT-38K 600 + FL-T100 150 + Stayputnik 300; mass the Flea's
plus 0.025 + 0.5625 (0.0625 dry, 45 LF + 55 Ox) + 0.05 = 0.6375 t. Not flown: whether the
editor reports exactly 3982 is owed to RW-5's first flight (its rollout line).

ASSUMPTIONS A LIVE RUN CAN FALSIFY, in the order they would bite: the stand's bottom height
(a stand hanging above the pad drops a few cm at the decouple; one below it tilts the craft
at rollout), the 0.630227923 / 0.786410213 radial offsets on the RT-5 (cosmetic clipping),
and the stand clearing the fins after the decouple (it stands 60 degrees from each).

Two consumers (the RW lanes on `cs-career-mun4`, IR-3 on `rover-route-recorded`), so per
`fixtures/shared-ships.toml` it lives in the shared library `fixtures/ships/` and is overlaid
into both saves at staging.

Usage:
    python harness/tools/build_cs_flea_probe_craft.py --check
    python harness/tools/build_cs_flea_probe_craft.py --write
    python harness/tools/build_cs_flea_probe_craft.py --verify-donors <stock-tree Ships dir>

Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
BASE = os.path.join(ROOT, "harness", "fixtures", "ships", "Jumping Flea.craft")
OUT = os.path.join(ROOT, "harness", "fixtures", "ships", "CS Flea Probe.craft")
DEFAULT_DONOR_SHIPS = os.path.join(os.path.dirname(ROOT), "automation", "stock-tree",
                                   "Ships")

SHIP_NAME = "CS Flea Probe"
HOST = "solidBooster.sm.v2_4294392496"   # the Flea's RT-5, the stand's srfAttach host
FIN_PREFIX = "basicFin_"                  # the base's three fins, on one band of the RT-5

# Fresh craft-baked persistentIds: the vessel header first, then every part in file order.
HEADER_PERSISTENT_ID = 3615170000
PART_PERSISTENT_ID_BASE = 3615170001

AZIMUTH_DEG = 270.0
# Kerbal 1's TT-38K: radius from its 1.25 m host's axis.
DECOUPLER_RADIUS = 0.630227923
# Kerbal 1's RT-10 on that TT-38K: its attPos0 x in the decoupler's frame (outward = -X).
CHILD_OFFSET = 0.786410213
RT5_BOTTOM_NODE = -0.9975
T100_HALF = 0.3125
STAYPUTNIK_BOTTOM_NODE = -0.27448

DECOUPLER_UID = 4261170001
TANK_UID = 4261170002
PROBE_UID = 4261170003

# The stand's staging: its own first stage (2), ahead of the Flea's RT-5 (1) and chute (0).
STAND_STAGE = 2

EJECTION_FROM = "\t\tejectionForcePercent = 100\n"
EJECTION_TO = "\t\tejectionForcePercent = 0\n"

# (part, donor path under Ships/, occurrence of that part in file order).
DONORS = {
    "radialDecoupler": ("VAB/Kerbal 1.craft", 0),
    "fuelTankSmallFlat": ("VAB/Kerbal 1.craft", 0),
    "probeCoreSphere.v2": ("SPH/Bug-E Buggy.craft", 0),
}

# Tails lifted BYTE-FOR-BYTE from the donor named above each entry (see DONORS and
# --verify-donors). Never edit one by hand; the one rewrite is applied in build().
TAILS = {
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written, 1.12.0]
    'radialDecoupler': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAnchoredDecoupler\n\t\tisEnabled = True\n\t\tejectionForcePercent = 100\n\t\tisDecoupled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleToggleCrossfeed\n\t\tisEnabled = True\n\t\tcrossfeedStatus = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tEnableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDisableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # <- Ships/VAB/Kerbal 1.craft, occurrence 0  [stock, KSP-written, 1.12.0]
    'fuelTankSmallFlat': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tselectedVariant = BlackAndWhite\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 45\n\t\tmaxAmount = 45\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 55\n\t\tmaxAmount = 55\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # <- Ships/SPH/Bug-E Buggy.craft, occurrence 0  [stock, KSP-written, 1.12.0]
    'probeCoreSphere.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleKerbNetAccess\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOpenKerbNetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 10\n\t\tmaxAmount = 10\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
}


def _fmt(v):
    v = round(float(v), 9)
    if v == 0:
        return "0"     # also folds a rounded -0.0 into "0"
    s = repr(v)
    return s[:-2] if s.endswith(".0") else s


def _vec(v):
    return ",".join(_fmt(c) for c in v)


def _quat_y(psi_deg):
    h = math.radians(psi_deg) / 2.0
    return (0.0, math.sin(h), 0.0, math.cos(h))


def _scalar(block, key):
    m = re.search(r"^\t%s = (.*)$" % re.escape(key), block, flags=re.M)
    if not m:
        raise ValueError("key %s missing" % key)
    return m.group(1)


def _split_parts(text):
    idx = [m.start() for m in re.finditer(r"^PART\n\{\n", text, flags=re.M)]
    if not idx:
        raise ValueError("no PART blocks")
    return text[:idx[0]], [text[s:e] for s, e in zip(idx, idx[1:] + [len(text)])]


def decoupler_tail():
    tail = TAILS["radialDecoupler"]
    if tail.count(EJECTION_FROM) != 1:
        raise ValueError("TT-38K tail does not carry exactly one %r" % EJECTION_FROM)
    return tail.replace(EJECTION_FROM, EJECTION_TO)


def layout(host_pos, fin_band_dy):
    """The three appended parts as dicts of their derived scalars, parent first.
    ``fin_band_dy`` is the base fins' attPos0 y on the booster."""
    a = math.radians(AZIMUTH_DEG)
    out_dir = (math.cos(a), 0.0, math.sin(a))
    bottom_y = host_pos[1] + RT5_BOTTOM_NODE
    tank_y = bottom_y + T100_HALF
    rot = _quat_y(180.0 - AZIMUTH_DEG)
    dec_pos = (host_pos[0] + DECOUPLER_RADIUS * out_dir[0], host_pos[1] + fin_band_dy,
               host_pos[2] + DECOUPLER_RADIUS * out_dir[2])
    tank_pos = (dec_pos[0] + CHILD_OFFSET * out_dir[0], tank_y,
                dec_pos[2] + CHILD_OFFSET * out_dir[2])
    probe_dy = T100_HALF - STAYPUTNIK_BOTTOM_NODE
    probe_pos = (tank_pos[0], tank_y + probe_dy, tank_pos[2])
    dec = dict(name="radialDecoupler", uid=DECOUPLER_UID, pos=dec_pos,
               att_pos0=tuple(d - h for d, h in zip(dec_pos, host_pos)),
               rot=rot, att_rot0=rot, istg=STAND_STAGE, dstg=STAND_STAGE + 1, sidx=0,
               sqor=STAND_STAGE, sep=STAND_STAGE, attm=1,
               links=["fuelTankSmallFlat_%d" % TANK_UID], attn=[],
               srfn=HOST, tail=decoupler_tail())
    tank = dict(name="fuelTankSmallFlat", uid=TANK_UID, pos=tank_pos,
                att_pos0=(-CHILD_OFFSET, tank_y - dec_pos[1], 0.0), rot=rot, att_rot0=(0.0, 0.0, 0.0, 1.0),
                istg=STAND_STAGE, dstg=STAND_STAGE + 1, sidx=-1, sqor=-1, sep=STAND_STAGE,
                attm=1, links=["probeCoreSphere.v2_%d" % PROBE_UID],
                attn=[("top", "probeCoreSphere.v2_%d" % PROBE_UID, T100_HALF)],
                srfn="radialDecoupler_%d" % DECOUPLER_UID, tail=TAILS["fuelTankSmallFlat"])
    probe = dict(name="probeCoreSphere.v2", uid=PROBE_UID, pos=probe_pos,
                 att_pos0=(0.0, probe_dy, 0.0), rot=rot, att_rot0=(0.0, 0.0, 0.0, 1.0),
                 istg=STAND_STAGE, dstg=STAND_STAGE + 1, sidx=-1, sqor=-1, sep=STAND_STAGE,
                 attm=0, links=[],
                 attn=[("bottom", "fuelTankSmallFlat_%d" % TANK_UID, STAYPUTNIK_BOTTOM_NODE)],
                 srfn=None, tail=TAILS["probeCoreSphere.v2"])
    return [dec, tank, probe]


def render(p, persistent_id):
    lines = ["PART", "{",
             "\tpart = %s_%d" % (p["name"], p["uid"]),
             "\tpartName = Part",
             "\tpersistentId = %d" % persistent_id,
             "\tpos = %s" % _vec(p["pos"]),
             "\tattPos = 0,0,0",
             "\tattPos0 = %s" % _vec(p["att_pos0"]),
             "\trot = %s" % _vec(p["rot"]),
             "\tattRot = 0,0,0,1",
             "\tattRot0 = %s" % _vec(p["att_rot0"]),
             "\tmir = 1,1,1",
             "\tsymMethod = Radial",
             "\tautostrutMode = Off",
             "\trigidAttachment = False",
             "\tistg = %d" % p["istg"],
             "\tresPri = 0",
             "\tdstg = %d" % p["dstg"],
             "\tsidx = %d" % p["sidx"],
             "\tsqor = %d" % p["sqor"],
             "\tsepI = %d" % p["sep"],
             "\tattm = %d" % p["attm"],
             "\tmodCost = 0",
             "\tmodMass = 0",
             "\tmodSize = 0,0,0"]
    lines += ["\tlink = %s" % l for l in p["links"]]
    lines += ["\tattN = %s,%s_0|%s|0" % (node, other, _fmt(dy)) for node, other, dy in p["attn"]]
    if p["srfn"]:
        lines.append("\tsrfN = srfAttach,%s" % p["srfn"])
    lines.append(p["tail"])
    lines.append("}")
    return "\n".join(lines) + "\n"


def _with_link(block, token):
    """Insert `link = <token>` where KSP writes it: after the host's existing links,
    before its first attN / srfN. A child its host does not link is dropped at craft
    load, silently (GS-6 run `2026-09-02_1505`)."""
    rows = block.split("\n")
    for k, row in enumerate(rows):
        if row.startswith("\tattN = ") or row.startswith("\tsrfN = "):
            rows[k:k] = ["\tlink = " + token]
            return "\n".join(rows)
    raise ValueError("cannot place link= in the host block")


def build():
    with open(BASE, encoding="utf-8", newline="") as fh:
        base = fh.read().replace("\r\n", "\n")
    header, blocks = _split_parts(base)
    header = re.sub(r"^ship = .*$", "ship = %s" % SHIP_NAME, header, count=1, flags=re.M)
    header = re.sub(
        r"^description = .*$",
        "description = RW-5 craft (rewind ledger campaign, Career): the stock Jumping Flea "
        "plus a probe stand on the booster's side, a TT-38K radial decoupler holding an "
        "FL-T100 with a Stayputnik on top. Decoupling it on the pad leaves two controllable "
        "vessels. Built by construction by harness/tools/build_cs_flea_probe_craft.py.",
        header, count=1, flags=re.M)
    header = re.sub(r"^persistentId = .*$", "persistentId = %d" % HEADER_PERSISTENT_ID,
                    header, count=1, flags=re.M)
    host_index = None
    for i, b in enumerate(blocks):
        if _scalar(b, "part") == HOST:
            host_index = i
    if host_index is None:
        raise ValueError("base craft lacks %s" % HOST)
    host_pos = tuple(float(c) for c in _scalar(blocks[host_index], "pos").split(","))
    fin_band = {_scalar(b, "attPos0").split(",")[1] for b in blocks
                if _scalar(b, "part").startswith(FIN_PREFIX)}
    if len(fin_band) != 1:
        raise ValueError("base fins are not on one band: %r" % sorted(fin_band))
    added = layout(host_pos, float(fin_band.pop()))
    # The z extent grows by the stand: from the base's centred extent to the tank's far side.
    m = re.search(r"^size = ([^,\n]+),([^,\n]+),([^,\n]+)$", header, flags=re.M)
    far = -(DECOUPLER_RADIUS + CHILD_OFFSET + 0.625)
    size_z = float(m.group(3)) / 2.0 - far
    header = header.replace(m.group(0), "size = %s,%s,%s" % (m.group(1), m.group(2),
                                                             _fmt(size_z)))
    blocks[host_index] = _with_link(blocks[host_index], "%s_%d" % (added[0]["name"],
                                                                 added[0]["uid"]))
    pid = PART_PERSISTENT_ID_BASE
    out_blocks = []
    for b in blocks:
        out_blocks.append(re.sub(r"^(\tpersistentId = ).*$",
                                 lambda mm: mm.group(1) + str(pid), b, count=1, flags=re.M))
        pid += 1
    for p in added:
        out_blocks.append(render(p, pid))
        pid += 1
    return header + "".join(out_blocks)


def check():
    """[] when the committed craft is byte-identical to a fresh derivation, else the
    problems as strings (the drift gate's contract)."""
    if not os.path.exists(OUT):
        return ["MISSING %s - run --write" % OUT]
    with open(OUT, encoding="utf-8", newline="") as fh:
        have = fh.read()
    return [] if have == build() else ["DRIFT: %s differs from the derivation" % OUT]


def _donor_tail(ships_dir, name, rel, occurrence):
    path = os.path.join(ships_dir, *rel.split("/"))
    with open(path, encoding="latin-1", newline="") as fh:
        text = fh.read().replace("\r\n", "\n")
    found = []
    for blk in text.split("\nPART\n{\n")[1:]:
        m = re.match(r"\tpart = (\S+)_\d+\n", blk)
        if not m or m.group(1) != name:
            continue
        body = blk.split("\n}\n", 1)[0] if "\n}\n" in blk else blk.rsplit("\n}", 1)[0]
        found.append(body[body.index("\n\tEVENTS\n") + 1:])
    return found[occurrence] if len(found) > occurrence else None


def verify_donors(ships_dir):
    """[] when every TAILS entry equals its donor's tail re-extracted from `ships_dir`."""
    problems = []
    for name, (rel, occ) in sorted(DONORS.items()):
        got = _donor_tail(ships_dir, name, rel, occ)
        if got is None:
            problems.append("%s: no occurrence %d in %s" % (name, occ, rel))
        elif got != TAILS[name]:
            problems.append("%s: tail differs from %s occurrence %d" % (name, rel, occ))
    return problems


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--verify-donors", metavar="SHIPS_DIR", nargs="?",
                    const=DEFAULT_DONOR_SHIPS, default=None)
    a = ap.parse_args(argv)
    if a.verify_donors:
        problems = verify_donors(a.verify_donors)
        print("\n".join(problems) or "OK every tail matches its donor in %s" % a.verify_donors)
        return 1 if problems else 0
    if a.write:
        text = build()
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print("wrote %s (%d lines)" % (OUT, text.count("\n")))
        return 0
    problems = check()
    print("\n".join(problems) or "OK committed craft matches the derivation")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
