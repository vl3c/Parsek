#!/usr/bin/env python3
"""Build the committed GS-1 two-stage auto-chute-booster .craft BY CONSTRUCTION.

WHY THIS SCRIPT EXISTS. The harness has no craft-authoring route. `career-pad-craft`
is built by SPLICING WHOLE EXISTING NODES (build_career_pad_craft.py copies
b1-pad-craft's entire Jumping Flea VESSEL node verbatim), and every other committed
pad fixture got its craft from a REAL KSP session: a stock craft, or an operator VAB
save (the docking Kerbal X). GS-1 needs a craft no stock craft and no committed
fixture provides -- a booster stage carrying BOTH a ModuleCommand probe core (so the
staging split is MULTI-CONTROLLABLE and authors a RewindPoint) AND parachutes (so it
lands Immutable instead of crashing to CommittedProvisional). Every stock VAB craft
was checked; not one has a chute on its booster stage.

So the craft is authored here, deterministically, from data rather than by hand, and
this file IS the derivation record. Its `--check` mode re-derives and compares against
the committed bytes, and `test_gs1_auto_chute_booster.py::CraftDriftTests` runs both
in-process, so a hand edit to the .craft (or a change to the derivation) reds in the
unit suite instead of in a live forge flight.

WHAT IS DERIVED AND WHAT IS COPIED, stated exactly, because the two carry very
different risk:

  COPIED VERBATIM -- every PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE* /
  RESOURCE*) is lifted byte-for-byte out of a stock KSP 1.12.5 craft that KSP itself
  wrote. `TAILS` at the bottom of this file records which craft each came from. No
  MODULE block here is invented, so no module-index mismatch can be authored in.

  DERIVED -- the scalar header of each PART (pos / rot / attN / srfN / istg / links).
  The stack arithmetic is
      child.pos = parent.pos + parent_node_offset - child_opposite_node_offset
  where the node offsets are the EFFECTIVE (post-rescale) values, which a .craft
  records verbatim in its own `attN` tokens. Every offset in NODE_OFFSET below was
  read off a stock craft's attN token, NOT computed from a part cfg, precisely so no
  rescaleFactor / scale ambiguity enters (parachuteSingle is the trap: its cfg says
  -0.120649 and its effective offset is -0.01508113, a factor of 0.125).

  The formula was VALIDATED against stock data before being used. Jumping Flea:
  pod at y=15, pod top node +0.6423756, chute bottom node -0.01508113
  -> 15 + 0.6423756 + 0.01508113 = 15.65745673, and the stock file says 15.6574574.
  Orbiter One: tank bottom -0.55525, liquidEngine2 top +0.9018263
  -> 15.9825821 - 0.55525 - 0.9018263 = 14.5255058, stock says 14.525506.

  RADIAL placement is derived from the surface azimuth. For a part surface-attached
  at azimuth phi (measured from +X toward +Z) the part's own attach direction must
  point along the INWARD normal, which fixes a pure rotation about world Y:
      attach direction local +X (basicFin)      -> psi = 180 deg - phi
      attach direction local -Z (parachuteRadial) -> psi =  90 deg - phi
      quaternion = (0, sin(psi/2), 0, cos(psi/2))
  VALIDATED against stock, exactly: Jumping Flea's three basicFins sit at phi =
  90 / 210 / 330 and carry rot (0,0.707106829,0,0.707106829),
  (0,0.965925872,0,-0.258819103), (0,0.258819014,0,-0.965925872) -- which are this
  formula's outputs at those azimuths, up to the quaternion double cover (q and -q
  are the same rotation, and stock stores both signs).

HOW A FAILED FORGE RUN IS DIAGNOSED WITHOUT RE-DERIVING ANY OF THIS. The assumptions
that a live run can falsify, in the order they would bite:

  A1  ROOT-RELATIVE ORIGIN. The pod is placed at y = 15 because that is where stock
      puts the Jumping Flea's root. .craft positions are editor-space and KSP
      re-seats the whole assembly onto the pad at launch, so the absolute value is
      cosmetic; only the DELTAS between parts matter. A craft that loads at a silly
      height on the pad means this assumption is wrong, not the deltas.
  A2  RADIAL RADIUS. RADIAL_RADIUS = 0.623 is MEASURED, not nominal: it is the radius
      at which stock placed the Jumping Flea's fins on a 1.25 m body. (Orbiter One
      placed fins on this exact fuelTankSmall at 0.576, so the VAB's raycast varies
      with the model; the difference is centimetres and radial parts clip freely.) A
      chute visibly floating off the tank, or sunk inside it, is this number.
  A3  STAGING. `istg` is the authority KSP reads (Part.inverseStage); `dstg` / `sidx`
      / `sqor` / `sepI` are caches StageManager regenerates at load, and are written
      here mirroring stock Orbiter One's upper-stage pattern. If the forge's craft
      shows the decoupler and the booster chutes in DIFFERENT stages, the istg table
      in PARTS is what to look at, not the caches.
  A4  CHUTE MODULE DEFAULTS. The parachuteRadial tail comes from an SPH craft whose
      designer had tweaked it (deployAltitude 500, minAirPressureToOpen 0.01,
      DeployAction bound to the Abort group). Those three are REWRITTEN here to the
      part cfg's stock defaults (1000 / 0.04 / None), so the committed craft carries
      what a freshly-placed VAB part carries. A booster chute that opens at an
      unexpected altitude is this rewrite.
  A5  MASS / TOUCHDOWN. The booster's descent mass is ~2.3 t under six Mk2-R
      canopies. Scaling B1's own computed anchor (1.52 t under one Mk16 touches down
      at ~8-9 m/s, v ~ sqrt(m/n)) gives ~4.3 m/s here, against the BINDING crash
      tolerance in the booster, which is fuelTankSmall's 6 (LV-T45 is 7,
      probeCoreOcto2 12, parachuteRadial 12, basicFin 4). The fins are expected to
      break on a tip-over; the tank and engine are not. If the forge or the GS-1 run
      shows the booster breaking up at touchdown, this is the number that was wrong
      and the remedy is more canopy, not a different scenario.

Usage:
    python harness/tools/build_gs1_craft.py --check     # verify the committed bytes
    python harness/tools/build_gs1_craft.py --write     # regenerate the committed file

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import math
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)                       # harness/

# The craft is committed ONCE, in the shared library at harness/fixtures/ships/,
# and `run.py::stage_fixture` overlays it into each consuming save's Ships/VAB at
# stage time (the directory kRPC launch_vessel resolves
# <save>/Ships/VAB/<craftName>.craft against). Consumers are declared in
# harness/fixtures/shared-ships.toml -- seven of them today, headed by
# bdock-forge-base, the base FORGE-gs1-two-stage boots. Committing one copy per
# consuming save is what the shared library replaced: seven files to keep in step,
# and a drift gate that had to walk all of them to mean anything.
SHIP_NAME = "GS1 Auto-Chute Booster"
CRAFT_PATH = os.path.join(_HARNESS_ROOT, "fixtures", "ships", SHIP_NAME + ".craft")

# Root placement (assumption A1).
ROOT_Y = 15.0
# Measured surface radius for a radial attachment on a 1.25 m stock body
# (assumption A2).
RADIAL_RADIUS = 0.623

# EFFECTIVE stack-node offsets, every one read off a stock craft's own attN token.
# (part name) -> {"top": dy, "bottom": dy}
NODE_OFFSET = {
    # Jumping Flea: attN = top,...|0.6423756|0 and attN = bottom,...|-0.4050379|0
    "mk1pod.v2":         {"top": 0.6423756, "bottom": -0.4050379},
    # Jumping Flea: attN = bottom,...|-0.01508113|0 (NOT the cfg's -0.120649)
    "parachuteSingle":   {"bottom": -0.01508113},
    # Orbiter One: attN = top,...|0.05|0 and attN = bottom,...|-0.05|0
    "Decoupler.1":       {"top": 0.05, "bottom": -0.05},
    # Z-MAP Satellite Launch Kit: attN = top,...|0.0610621|0, bottom -0.0610621
    "probeCoreOcto2.v2": {"top": 0.0610621, "bottom": -0.0610621},
    # Orbiter One: attN = top,...|0.55525|0 and attN = bottom,...|-0.55525|0
    "fuelTankSmall":     {"top": 0.55525, "bottom": -0.55525},
    # Orbiter One: attN = top,...|0.9018263|0 and attN = bottom,...|-0.7179225|0
    "liquidEngine2":     {"top": 0.9018263, "bottom": -0.7179225},
}

# Local attach direction per surface-attachable part, from its cfg node_attach
# orientation. Drives the azimuth -> rotation formula (see the module docstring).
SRF_ATTACH_AXIS = {
    "basicFin": "+X",          # node_attach = 0,0,0, 1,0,0, 1
    "parachuteRadial": "-Z",   # node_attach = 0,0,0, 0,0,-1
}

# Deterministic part ids. KSP only requires them to be unique within the file; these
# are fixed so the generated bytes are stable across runs (the drift gate compares
# bytes).
_PART_UID = {
    "pod": 4200000001, "chute_up": 4200000002, "decoupler": 4200000003,
    "probe": 4200000004, "tank": 4200000005, "engine": 4200000006,
    "chute_b0": 4200000010, "chute_b1": 4200000011, "chute_b2": 4200000012,
    "chute_b3": 4200000013, "chute_b4": 4200000014, "chute_b5": 4200000015,
    "fin0": 4200000020, "fin1": 4200000021, "fin2": 4200000022,
}
_PERSISTENT_ID = {k: 1500000000 + i * 7717 for i, k in enumerate(sorted(_PART_UID))}

# THE STAGING TABLE, and it is the load-bearing part of the whole craft.
# KSP fires stages in DESCENDING istg order, so:
#   istg 2  the LV-T45 ignites (the pad click)
#   istg 1  the decoupler blows AND all six booster chutes arm -- ONE click, which
#           is what "auto-chute booster" means and what this scenario is named for
#   istg 0  the upper stage's own Mk16, which the mission arms during descent
# The booster's non-staged parts (probe core, tank, fins) carry the booster's own
# stage number so the VAB staging list groups them with it, mirroring stock Orbiter
# One (its upper tanks carry the upper decoupler's istg).
STAGE_IGNITE = 2
STAGE_SEPARATE = 1
STAGE_UPPER_CHUTE = 0

# Reduced propellant load. A FULL FL-T200 (90 LF / 110 Ox = 1.0 t) on this 3.1 t
# stack gives a pad TWR over 4 and a burn long enough to put the hop kilometres up,
# which loses the booster to stock's unloaded-vessel deletion. At 20 percent the
# whole tank cannot lift the craft out of the physics bubble even at full throttle
# for the whole burn: dv is about 250*9.81*ln(3.285/3.085) = 154 m/s, minus gravity
# losses over the ~3 s burn, so the apex lands in the high hundreds of metres. The
# ceiling is enforced twice more, by the mission's stagingApoapsisMeters target and
# by the stagedBelowCeiling assertion; this is the belt.
TANK_LIQUID_FUEL = 18.0
TANK_OXIDIZER = 22.0

# The three parachuteRadial module values the SPH donor had tweaked, rewritten to the
# part cfg's stock defaults (assumption A4).
CHUTE_DEPLOY_ALTITUDE = "1000"
CHUTE_MIN_AIR_PRESSURE = "0.0399999991"


def _fmt(v: float) -> str:
    """Format a coordinate the way KSP writes one: shortest round-trippable form,
    no exponent for the magnitudes this craft uses."""
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
    """The rot quaternion for ``part_name`` surface-attached at ``azimuth_deg``
    (measured from +X toward +Z) on a vertical cylinder. See the module docstring
    for the derivation and the stock validation."""
    axis = SRF_ATTACH_AXIS[part_name]
    if axis == "+X":
        psi = 180.0 - azimuth_deg
    elif axis == "-Z":
        psi = 90.0 - azimuth_deg
    else:
        raise ValueError("unhandled surface attach axis %r" % (axis,))
    return _fmt_quat(_quat_y(psi))


def srf_position(center, azimuth_deg: float, dy: float):
    """World position of a surface attachment at ``azimuth_deg`` on the cylinder
    whose axis passes through ``center``, offset ``dy`` along the axis."""
    a = math.radians(azimuth_deg)
    return (center[0] + RADIAL_RADIUS * math.cos(a),
            center[1] + dy,
            center[2] + RADIAL_RADIUS * math.sin(a))


class Part(object):
    """One authored PART: identity, geometry, staging, and the links KSP needs."""

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


def _tank_tail() -> str:
    """The fuelTankSmall tail with the propellant load reduced (see
    TANK_LIQUID_FUEL / TANK_OXIDIZER). Only the two `amount =` lines move;
    `maxAmount` and every other line stay byte-identical to the stock donor."""
    out = []
    current = None
    for line in TAILS["fuelTankSmall"].split("\n"):
        stripped = line.strip()
        if stripped.startswith("name = "):
            current = stripped[7:]
        if stripped.startswith("amount = ") and current == "LiquidFuel":
            out.append("\t\tamount = %s" % _fmt(TANK_LIQUID_FUEL))
            continue
        if stripped.startswith("amount = ") and current == "Oxidizer":
            out.append("\t\tamount = %s" % _fmt(TANK_OXIDIZER))
            continue
        out.append(line)
    return "\n".join(out)


def _radial_chute_tail() -> str:
    """The parachuteRadial tail normalized to the part cfg's stock defaults
    (assumption A4): the SPH donor had deployAltitude 500, minAirPressureToOpen
    0.01, and DeployAction bound to the Abort action group."""
    out = []
    for line in TAILS["parachuteRadial"].split("\n"):
        stripped = line.strip()
        if stripped.startswith("deployAltitude = "):
            out.append("\t\tdeployAltitude = %s" % CHUTE_DEPLOY_ALTITUDE)
            continue
        if stripped.startswith("minAirPressureToOpen = "):
            out.append("\t\tminAirPressureToOpen = %s" % CHUTE_MIN_AIR_PRESSURE)
            continue
        if stripped == "actionGroup = Abort":
            out.append("\t\t\t\tactionGroup = None")
            continue
        out.append(line)
    return "\n".join(out)


def tail_for(part: Part) -> str:
    if part.name == "fuelTankSmall":
        return _tank_tail()
    if part.name == "parachuteRadial":
        return _radial_chute_tail()
    return TAILS[part.name]


def layout():
    """Build the full ordered part list with every position derived. Returns the
    list; the root is element 0 (KSP takes the first PART as the root)."""
    pod_y = ROOT_Y
    pod = Part("pod", "mk1pod.v2", (0.0, pod_y, 0.0), istg=-1, dstg=0)

    chute_up_y = pod_y + NODE_OFFSET["mk1pod.v2"]["top"] \
        - NODE_OFFSET["parachuteSingle"]["bottom"]
    chute_up = Part("chute_up", "parachuteSingle", (0.0, chute_up_y, 0.0),
                    istg=STAGE_UPPER_CHUTE, dstg=0, sidx=0, sqor=0)

    dec_y = pod_y + NODE_OFFSET["mk1pod.v2"]["bottom"] \
        - NODE_OFFSET["Decoupler.1"]["top"]
    dec = Part("decoupler", "Decoupler.1", (0.0, dec_y, 0.0),
               istg=STAGE_SEPARATE, dstg=1, sidx=0, sqor=1)

    probe_y = dec_y + NODE_OFFSET["Decoupler.1"]["bottom"] \
        - NODE_OFFSET["probeCoreOcto2.v2"]["top"]
    probe = Part("probe", "probeCoreOcto2.v2", (0.0, probe_y, 0.0),
                 istg=STAGE_SEPARATE, dstg=2)

    tank_y = probe_y + NODE_OFFSET["probeCoreOcto2.v2"]["bottom"] \
        - NODE_OFFSET["fuelTankSmall"]["top"]
    tank = Part("tank", "fuelTankSmall", (0.0, tank_y, 0.0),
                istg=STAGE_SEPARATE, dstg=2)

    eng_y = tank_y + NODE_OFFSET["fuelTankSmall"]["bottom"] \
        - NODE_OFFSET["liquidEngine2"]["top"]
    eng = Part("engine", "liquidEngine2", (0.0, eng_y, 0.0),
               istg=STAGE_IGNITE, dstg=2, sidx=0, sqor=2)

    pod.links = [chute_up.uid, dec.uid]
    pod.attn = [("bottom", dec.uid, NODE_OFFSET["mk1pod.v2"]["bottom"]),
                ("top", chute_up.uid, NODE_OFFSET["mk1pod.v2"]["top"])]
    chute_up.attn = [("bottom", pod.uid, NODE_OFFSET["parachuteSingle"]["bottom"])]
    dec.links = [probe.uid]
    dec.attn = [("top", pod.uid, NODE_OFFSET["Decoupler.1"]["top"]),
                ("bottom", probe.uid, NODE_OFFSET["Decoupler.1"]["bottom"])]
    probe.links = [tank.uid]
    probe.attn = [("top", dec.uid, NODE_OFFSET["probeCoreOcto2.v2"]["top"]),
                  ("bottom", tank.uid, NODE_OFFSET["probeCoreOcto2.v2"]["bottom"])]
    tank.links = [eng.uid]
    tank.attn = [("top", probe.uid, NODE_OFFSET["fuelTankSmall"]["top"]),
                 ("bottom", eng.uid, NODE_OFFSET["fuelTankSmall"]["bottom"])]
    eng.attn = [("top", tank.uid, NODE_OFFSET["liquidEngine2"]["top"])]

    tank_center = tank.pos
    # SIX radial canopies at 60 degree spacing. Six rather than two is a mass
    # decision, not decoration: see assumption A5 in the module docstring.
    chutes = []
    for i in range(6):
        az = 60.0 * i
        key = "chute_b%d" % i
        p = Part(key, "parachuteRadial",
                 srf_position(tank_center, az, 0.25),
                 rot=srf_rotation("parachuteRadial", az),
                 istg=STAGE_SEPARATE, dstg=2, attm=1)
        p.srfn = tank.uid
        chutes.append(p)
    for p in chutes:
        p.syms = [o.uid for o in chutes if o is not p]

    # THREE fins at the Jumping Flea's own azimuths (90 / 210 / 330), low on the
    # tank. Aerodynamic stability for a short vertical burn; they are also the only
    # parts expected to break on a tip-over (crashTolerance 4).
    fins = []
    for i, az in enumerate((90.0, 210.0, 330.0)):
        p = Part("fin%d" % i, "basicFin",
                 srf_position(tank_center, az, -0.30),
                 rot=srf_rotation("basicFin", az),
                 istg=STAGE_SEPARATE, dstg=2, attm=1)
        p.srfn = tank.uid
        fins.append(p)
    for p in fins:
        p.syms = [o.uid for o in fins if o is not p]

    tank.links += [p.uid for p in chutes] + [p.uid for p in fins]
    return [pod, chute_up, dec, probe, tank, eng] + chutes + fins


def render_part(part: Part, root_pos, is_root: bool = False) -> str:
    # attPos0 is the part's offset from the ROOT -- except on the root itself, where
    # stock writes the root's own pos (Jumping Flea: the pod's pos and attPos0 are
    # both 0,15,0 while the chute's attPos0 is its pos MINUS the root's).
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
    """Render the whole .craft as a list of lines (no trailing newline element)."""
    parts = layout()
    root = parts[0].pos
    xs = [p.pos[0] for p in parts]
    ys = [p.pos[1] for p in parts]
    zs = [p.pos[2] for p in parts]
    header = [
        "ship = %s" % SHIP_NAME,
        "version = 1.12.5",
        "description = GS-1 auto-chute booster. Two stages, both controllable: a "
        "crewed Mk1 pod with a Mk16 on top, and a probe-cored FL-T200 + LV-T45 "
        "booster whose six Mk2-R canopies arm in the same stage that fires the "
        "decoupler. Built by construction by harness/tools/build_gs1_craft.py.",
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 1.25, max(ys) - min(ys) + 1.0,
                           max(zs) - min(zs) + 1.25),
        "steamPublishedFileId = 0",
        "persistentId = 2200110033",
        "rot = 0,0,0,1",
        "missionFlag = Squad/Flags/default",
        "vesselType = Ship",
    ]
    body = [render_part(p, root, is_root=(i == 0)) for i, p in enumerate(parts)]
    return ("\n".join(header) + "\n" + "\n".join(body) + "\n").split("\n")


# ---------------------------------------------------------------------------
# Post-conditions. `verify` is the `--check` path and is run in-process by the
# unit suite, so a hand-edited craft reds there rather than in a forge flight.
# ---------------------------------------------------------------------------

EXPECTED_PARTS = {
    "mk1pod.v2": 1, "parachuteSingle": 1, "Decoupler.1": 1,
    "probeCoreOcto2.v2": 1, "fuelTankSmall": 1, "liquidEngine2": 1,
    "parachuteRadial": 6, "basicFin": 3,
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
    """Every post-condition the craft must satisfy. Returns a list of problems
    (empty = good). Pure text, no KSP."""
    problems = []
    text = "\n".join(lines)
    if not lines or not lines[0].startswith("ship = %s" % SHIP_NAME):
        problems.append("first line must be `ship = %s`" % SHIP_NAME)
    if "type = VAB" not in text:
        problems.append("craft must be a VAB craft (type = VAB)")

    records = part_records(lines)
    counts = {}
    for name, _rec in records:
        counts[name] = counts.get(name, 0) + 1
    if counts != EXPECTED_PARTS:
        problems.append("part census %r != expected %r" % (counts, EXPECTED_PARTS))
    if not records or records[0][0] != "mk1pod.v2":
        problems.append("the ROOT (first PART) must be the mk1pod.v2 upper stage; "
                        "KSP takes the first PART as the root and the mission flies "
                        "the upper stage")

    by_name = {}
    for name, rec in records:
        by_name.setdefault(name, []).append(rec)

    def istg_of(name):
        return sorted({int(r["istg"]) for r in by_name.get(name, [])})

    # THE STAGING CONTRACT, which is what the scenario is named for.
    if istg_of("liquidEngine2") != [STAGE_IGNITE]:
        problems.append("the LV-T45 must ignite alone in istg %d, got %r"
                        % (STAGE_IGNITE, istg_of("liquidEngine2")))
    if istg_of("Decoupler.1") != [STAGE_SEPARATE]:
        problems.append("the decoupler must fire in istg %d, got %r"
                        % (STAGE_SEPARATE, istg_of("Decoupler.1")))
    if istg_of("parachuteRadial") != [STAGE_SEPARATE]:
        problems.append("ALL booster chutes must arm in the SAME stage that fires "
                        "the decoupler (istg %d) -- that IS the auto-chute booster; "
                        "got %r" % (STAGE_SEPARATE, istg_of("parachuteRadial")))
    if istg_of("parachuteSingle") != [STAGE_UPPER_CHUTE]:
        problems.append("the upper stage's Mk16 must sit in istg %d (the mission "
                        "arms it during descent), got %r"
                        % (STAGE_UPPER_CHUTE, istg_of("parachuteSingle")))
    if not (STAGE_IGNITE > STAGE_SEPARATE > STAGE_UPPER_CHUTE):
        problems.append("KSP fires stages in DESCENDING istg order; the ignite / "
                        "separate / upper-chute stages must be strictly descending")

    # MULTI-CONTROLLABLE SPLIT. Both sides of the decoupler must carry a
    # ModuleCommand, or SegmentBoundaryLogic.IdentifyControllableChildren returns
    # one controllable, IsMultiControllableSplit is false, and NO RewindPoint is
    # authored -- which would make the whole scenario vacuous.
    for command_part in ("mk1pod.v2", "probeCoreOcto2.v2"):
        if command_part not in counts:
            problems.append("the split needs a ModuleCommand on BOTH sides; %s is "
                            "missing" % command_part)
    if "ModuleCommand" not in text:
        problems.append("no ModuleCommand block found at all")
    if text.count("name = ModuleCommand") < 2:
        problems.append("expected a ModuleCommand block on each side of the split "
                        "(pod + probe core); found %d"
                        % text.count("name = ModuleCommand"))

    # The chute modules must carry the stock defaults, not the SPH donor's tweaks.
    if "deployAltitude = 500" in text:
        problems.append("a parachute still carries the SPH donor's deployAltitude "
                        "500; the committed craft uses the part cfg default %s"
                        % CHUTE_DEPLOY_ALTITUDE)
    if "actionGroup = Abort" in text:
        problems.append("a parachute DeployAction is still bound to the Abort "
                        "action group (SPH donor tweak); it must be None so only "
                        "staging arms it")

    # The reduced propellant load (assumption A5's other half).
    if ("\t\tamount = %s" % _fmt(TANK_LIQUID_FUEL)) not in text:
        problems.append("the FL-T200 does not carry the reduced LiquidFuel load %s"
                        % _fmt(TANK_LIQUID_FUEL))
    if ("\t\tamount = %s" % _fmt(TANK_OXIDIZER)) not in text:
        problems.append("the FL-T200 does not carry the reduced Oxidizer load %s"
                        % _fmt(TANK_OXIDIZER))

    # Radial parts must actually reference the tank they hang off.
    for name in ("parachuteRadial", "basicFin"):
        for rec in by_name.get(name, []):
            if not rec.get("srfN", "").startswith("srfAttach,fuelTankSmall"):
                problems.append("%s must be surface-attached to the FL-T200, got %r"
                                % (name, rec.get("srfN")))
    return problems


def main(argv=None) -> int:
    p = argparse.ArgumentParser(
        prog="build_gs1_craft",
        description="Build or verify the committed GS-1 two-stage "
                    "auto-chute-booster .craft.")
    p.add_argument("--check", action="store_true",
                   help="verify the COMMITTED craft against every post-condition "
                        "AND against a fresh rebuild; writes nothing")
    p.add_argument("--write", action="store_true",
                   help="regenerate the committed craft file")
    args = p.parse_args(argv)
    if not args.check and not args.write:
        p.error("pass --check or --write")

    built = build()
    if args.write:
        os.makedirs(os.path.dirname(CRAFT_PATH), exist_ok=True)
        # CRLF, matching what KSP itself writes on Windows and what the sibling
        # committed craft (`Kerbal X.craft`) carries. `read_lines` normalizes, so
        # the drift comparison is line-based and unaffected either way; this is
        # only about the file on disk being byte-shaped like one KSP produced.
        with open(CRAFT_PATH, "w", encoding="utf-8", newline="\r\n") as fh:
            fh.write("\n".join(built))
        sys.stdout.write("[GS1Craft] wrote %s (%d lines)\n"
                         % (CRAFT_PATH, len(built)))

    problems = verify(built)
    if os.path.isfile(CRAFT_PATH):
        committed = read_lines(CRAFT_PATH)
        problems.extend(verify(committed))
        if committed != built:
            problems.append("the committed craft has DRIFTED from what this script "
                            "produces; re-run with --write and commit, or explain "
                            "the divergence")
    else:
        problems.append("committed craft not found at %s" % CRAFT_PATH)

    for problem in problems:
        sys.stdout.write("[GS1Craft] PROBLEM: %s\n" % problem)
    if problems:
        return 1
    sys.stdout.write("[GS1Craft] OK: %s satisfies every post-condition\n" % CRAFT_PATH)
    return 0


# ---------------------------------------------------------------------------
# VERBATIM STOCK PART TAILS. Each entry is the EVENTS-onward span of one PART
# node, lifted byte-for-byte out of a stock KSP 1.12.5 craft that KSP itself
# wrote (the source craft is named above each entry). Nothing here is authored:
# every MODULE block, its field set and its ordering are KSP's own, which is
# what makes a module-index mismatch impossible to introduce by hand. The two
# rewrites this file DOES apply (the tank's propellant load and the radial
# chute's three donor tweaks) are surgical line substitutions in _tank_tail /
# _radial_chute_tail, and both are asserted by verify().
# ---------------------------------------------------------------------------
TAILS = {
    # verbatim from stock VAB/Jumping Flea.craft
    'mk1pod.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleReactionWheel\n\t\tisEnabled = True\n\t\tactuatorModeCycle = 0\n\t\tauthorityLimiter = 100\n\t\tstateString = Active\n\t\tstagingEnabled = True\n\t\tWheelState = Active\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCycleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDeactivate\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleColorChanger\n\t\tisEnabled = True\n\t\tanimState = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = Light\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceExperiment\n\t\tisEnabled = True\n\t\tDeployed = False\n\t\tInoperable = False\n\t\tcooldownToGo = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tResetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleScienceContainer\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tCollectAllAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleConductionMultiplier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleLiftingSurface\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FlagDecal\n\t\tisEnabled = True\n\t\tflagDisplayed = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 50\n\t\tmaxAmount = 50\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = MonoPropellant\n\t\tamount = 10\n\t\tmaxAmount = 10\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Jumping Flea.craft
    'parachuteSingle': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleParachute\n\t\tisEnabled = True\n\t\tpersistentState = STOWED\n\t\tanimTime = 0\n\t\tminAirPressureToOpen = 0.0399999991\n\t\tdeployAltitude = 1000\n\t\tspreadAngle = 7\n\t\tautomateSafeDeploy = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tCutAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Jumping Flea.craft
    'basicFin': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleLiftingSurface\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Orbiter One.craft
    'Decoupler.1': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDecouple\n\t\tisEnabled = True\n\t\tejectionForcePercent = 100\n\t\tisDecoupled = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDecoupleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleToggleCrossfeed\n\t\tisEnabled = True\n\t\tcrossfeedStatus = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tEnableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tDisableAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Orbiter One.craft
    'fuelTankSmall': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 90\n\t\tmaxAmount = 90\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 110\n\t\tmaxAmount = 110\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Orbiter One.craft
    'liquidEngine2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = fairing\n\t\tisJettisoned = False\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAlternator\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Z-MAP Satellite Launch Kit.craft
    'probeCoreOcto2.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSAS\n\t\tisEnabled = True\n\t\tstandaloneToggle = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleKerbNetAccess\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOpenKerbNetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 5\n\t\tmaxAmount = 5\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock SPH/Rocket-power VTOL.craft
    'parachuteRadial': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleParachute\n\t\tisEnabled = True\n\t\tpersistentState = STOWED\n\t\tanimTime = 0\n\t\tminAirPressureToOpen = 0.00999999978\n\t\tdeployAltitude = 500\n\t\tspreadAngle = 7\n\t\tautomateSafeDeploy = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tDeployAction\n\t\t\t{\n\t\t\t\tactionGroup = Abort\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tCutAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDragModifier\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
}

if __name__ == "__main__":
    sys.exit(main())
