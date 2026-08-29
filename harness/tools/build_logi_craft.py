#!/usr/bin/env python3
"""Build the committed `Logi Cargo Rig` .craft BY CONSTRUCTION (the H38 pad fixture).

WHY THIS SCRIPT EXISTS. Same reason `build_gs1_craft.py` exists, and this file is a
deliberate copy of its structure and its discipline. The harness has no other
craft-authoring route: `build_career_pad_craft.py` SPLICES whole existing VESSEL
nodes, and every other committed pad fixture got its craft from a real KSP session.
The future ISOLATED Logistics lane (H38) needs a pad craft that no stock craft and no
committed fixture provides -- ONE vessel that simultaneously satisfies every
precondition the `Logistics` in-game category skips on:

  L1  a launchable PRELAUNCH pad rocket with at least one `ModuleEngines`, because
      `UnloadedFuelVesselFixture` snapshots the ACTIVE pad rocket and re-spawns it
      into a 250 km parking orbit as the UNLOADED depot the origin-debit / pickup /
      multi-stop tests need.
  L2  at least one part carrying a `LiquidFuel` RESOURCE node. The fixture rewrites
      the FIRST LF tank's `amount` / `maxAmount` / `flowState` on the snapshot; with
      NO LF node at all it returns `reason = "no-liquidfuel-resource"` and every
      unloaded-depot test skips (`InGameTests/Helpers/UnloadedFuelVesselFixture.cs`).
  L3  a PARTIALLY filled, flow-enabled LF tank on the LIVE craft, because the
      loaded-path `Delivery_LoadedVessel_AppliesResourceTransfer` pre-drains the
      first LF tank and asserts a +5.0 LF top-up, so it needs BOTH debitable fuel
      and free capacity. The in-game floors are `FixtureMinStoredLf` (10.0 for
      origin-debit / pickup, 14.0 for multi-stop = 5+4+5) and `FixtureMinFreeCapacity`
      (up to 18.0 = (5+4)*2 for multi-stop).
  L4  a `BaseConverter`-derived module. Two `HarvestCapture` cells skip with
      "carries no BaseConverter-derived module (harvester / converter / drill); a
      stock fuel cell suffices - add one to the test craft"
      (`InGameTests/LogisticsHarvestRuntimeTests.cs`). A stock `FuelCell` is the
      cheapest module that can actually ACTIVATE on the pad (a drill cannot: it
      wants ground contact and ore, and its own cell says so).
  L5  TWO `ModuleInventoryPart` containers in PROBE ORDER, the first one nearly
      full and a LATER one with an empty slot.
      `Delivery_MultiModule_FirstContainerFullSecondReceives` collects inventory
      modules in "vessel part order, then module order within the part", skips when
      it finds fewer than two, fills EVERY empty slot of the FIRST one, and then
      requires a later module with an empty slot or it skips
      "No later inventory module has an empty slot left".

So the craft is authored here, deterministically, from data rather than by hand, and
this file IS the derivation record. Its `--check` mode re-derives and compares against
the committed bytes, and `harness/lib/test_logi_cargo_rig.py::CraftDriftTests` runs
both in-process, so a hand edit to the .craft (or a change to the derivation) reds in
the unit suite instead of in a live forge flight.

WHAT IS DERIVED AND WHAT IS COPIED, stated exactly, because the two carry very
different risk (the GS-1 rule, unchanged):

  COPIED VERBATIM -- every PART's tail (EVENTS / ACTIONS / PARTDATA / MODULE* /
  RESOURCE*) is lifted byte-for-byte out of a craft KSP or Squad itself wrote.
  `TAILS` at the bottom of this file records which craft each came from, and every
  donor ships INSIDE the stock game (two stock VAB craft, one Squad prebuilt
  contract craft, plus two tails reused verbatim from `build_gs1_craft.py`, which
  took them from stock craft in turn). No MODULE block here is invented, so no
  module-index mismatch can be authored in.

  DERIVED -- the scalar header of each PART (pos / rot / attN / srfN / istg / links).
  The stack arithmetic is the GS-1 formula
      child.pos = parent.pos + parent_node_offset - child_opposite_node_offset
  where the node offsets are the EFFECTIVE (post-rescale) values. Every offset in
  NODE_OFFSET below was READ OFF a stock craft's own `attN` token, never computed
  from a part cfg, precisely so no rescaleFactor / scale ambiguity enters -- the
  trap `build_gs1_craft.py` documents for `parachuteSingle` (cfg -0.120649,
  effective -0.01508113).

  RADIAL placement of the fuel cell reuses the GS-1 azimuth formula. `FuelCell`'s
  cfg declares `node_attach = 0, 0, 0, 1, 0, 0, 0`, i.e. local +X, the same axis as
  `basicFin`, so psi = 180 deg - phi and the rotation is a pure quaternion about
  world Y. That branch was VALIDATED against stock in `build_gs1_craft.py` (Jumping
  Flea's three basicFins at phi = 90 / 210 / 330), and nothing about it is re-derived
  here.

THE TWO SURGICAL SUBSTITUTIONS, both asserted by `verify()`:

  1. `_tank_tail()` rewrites the FL-T400's two `amount =` lines to the PARTIAL load
     (L3). `maxAmount` and every other line stay byte-identical to the stock donor,
     so the tank keeps its real 180 LF / 220 Ox capacity.
  2. `_small_container_tail()` swaps the Squad prebuilt container's own (empty)
     `ModuleInventoryPart` MODULE block for a KSP-1.12.5-written FILLED one, with
     its first STOREDPART replaced by an `evaRepairKit` STOREDPART and its
     `inventory =` roster line rewritten. Both donor blocks are verbatim KSP output;
     see the provenance notes on `FILLED_INVENTORY_MODULE` / `REPAIR_KIT_STOREDPART`.

HOW A FAILED FORGE RUN IS DIAGNOSED WITHOUT RE-DERIVING ANY OF THIS. The assumptions
a live run can falsify, in the order they would bite:

  A1  ROOT-RELATIVE ORIGIN. The root sits at y = 15 because that is where stock puts
      the Jumping Flea's root. .craft positions are editor-space and KSP re-seats the
      whole assembly onto the pad at launch, so the absolute value is cosmetic; only
      the DELTAS between parts matter. A craft that loads at a silly height on the
      pad means this assumption is wrong, not the deltas.
  A2  RADIAL RADIUS. RADIAL_RADIUS = 0.623 is the value `build_gs1_craft.py` MEASURED
      for a radial attachment on a 1.25 m stock body. The FL-T400 is a 1.25 m body,
      so the number carries over. A fuel cell visibly floating off the tank, or sunk
      inside it, is this number, and it is cosmetic: the craft never flies.
  A3  UID CEILING. KSP parses the `part = <name>_<uid>` suffix as a UInt32, and
      `build_dd1_craft.py` cost a whole forge attempt-set learning it (kRPC
      `launch_vessel` threw server-side). Every uid here is well under 2^32 and
      `verify()` asserts it, so this can only regress by a hand edit.
  A4  INVENTORY PROBE ORDER. L5 rests on the FIRST `ModuleInventoryPart` in vessel
      part order being the 3-slot small container and a LATER one being the 9-slot
      cargo container. The ROOT is deliberately a `probeCoreOcto2.v2`, which carries
      NO inventory module -- every stock COMMAND POD does (`mk1pod.v2` has
      `InventorySlots = 1`, too few for two kits plus an empty slot; only the 2.5 m
      `mk1-3pod` reaches 3), so a pod root would silently BECOME the first module and
      break the whole lane. If a live run reports the multi-module cell skipping,
      this is the assumption to check first.
  A5  RESOURCE FLOORS. The tank ships 100 / 180 LF, i.e. 100 debitable against a
      14.0 floor and 80 free against an 18.0 floor -- both roughly 5x margin, chosen
      so the in-game constants can move without re-forging the fixture. Only the H38
      lane itself can falsify this one; the forge never runs a Logistics test.

Usage:
    python harness/tools/build_logi_craft.py --check     # verify the committed bytes
    python harness/tools/build_logi_craft.py --write     # regenerate the committed file

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import math
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)                       # harness/

# WHERE THE CRAFT LIVES. ONE copy, in the shared library at
# `harness/fixtures/ships/`; `run.py::stage_fixture` overlays it into each consuming
# save's `Ships/VAB` at stage time (the directory kRPC launch_vessel resolves
# `<save>/Ships/VAB/<craftName>.craft` against). Consumers are declared in
# `harness/fixtures/shared-ships.toml` -- TWO of them: `bdock-forge-base`, the save
# FORGE-logi-pad boots, and `logi-cargo-pad`, the fixture that forge produced.
#
# IT DID NOT START HERE, and the route is worth recording because it is the one
# every forge craft takes. `hlib.validate_shared_ships_manifest` refuses a library
# craft with fewer than TWO consumers ("a craft used once belongs in that fixture's
# own Ships/VAB, not the library"), so while FORGE-logi-pad was unflown this file
# was committed PHYSICALLY into `fixtures/saves/bdock-forge-base/Ships/VAB/` -- how
# `GS1 Auto-Chute Booster` and `Duna Rocket` were first committed too (commits
# 79bfe6f16 and cabdca93f). THE PROMOTION NOTE THAT USED TO STAND HERE PREDICTED THE
# REST EXACTLY: the harvest copies `Ships/` verbatim, so the produced fixture arrived
# holding a byte-identical second copy (sha256 6311fed9...abba, both). Both physical
# copies were then deleted, this single library copy took their place, and both
# consumers were declared in one commit. `SharedShipsManifestTests` gates every half
# of that -- including the content-addressed sweep that reds if a fixture file ever
# duplicates a library craft again.
SHIP_NAME = "Logi Cargo Rig"
CRAFT_PATH = os.path.join(_HARNESS_ROOT, "fixtures", "ships", SHIP_NAME + ".craft")

# Root placement (assumption A1).
ROOT_Y = 15.0
# Measured surface radius for a radial attachment on a 1.25 m stock body
# (assumption A2; measured by build_gs1_craft.py, reused unchanged).
RADIAL_RADIUS = 0.623

# EFFECTIVE stack-node offsets, every one read off a stock craft's own attN token.
# (part name) -> {"top": dy, "bottom": dy}
NODE_OFFSET = {
    # Z-MAP Satellite Launch Kit: attN = top,...|0.0610621|0, bottom -0.0610621
    "probeCoreOcto2.v2":    {"top": 0.0610621, "bottom": -0.0610621},
    # Squad prebuilt Contract Rover 10a: attN = top,...|0.349999994|0 / bottom
    "smallCargoContainer":  {"top": 0.349999994, "bottom": -0.349999994},
    # Squad prebuilt Contract Rover 10a: attN = top,...|0.300000012|0 / bottom
    "cargoContainer":       {"top": 0.300000012, "bottom": -0.300000012},
    # Stock VAB/Kerbal X: attN = top,...|0.981725|0 and bottom,...|-0.9125|0.
    # ASYMMETRIC, and that is real: the FL-T400 model is not centred on its nodes.
    "fuelTank":             {"top": 0.981725, "bottom": -0.9125},
    # Orbiter One: attN = top,...|0.9018263|0 and attN = bottom,...|-0.7179225|0
    "liquidEngine2":        {"top": 0.9018263, "bottom": -0.7179225},
}

# Local attach direction per surface-attachable part, from its cfg node_attach
# orientation. Drives the azimuth -> rotation formula (see the module docstring).
SRF_ATTACH_AXIS = {
    "FuelCell": "+X",          # node_attach = 0, 0, 0, 1, 0, 0, 0
}

# Deterministic part ids. KSP only requires them to be unique within the file; these
# are fixed so the generated bytes are stable across runs (the drift gate compares
# bytes). EVERY VALUE MUST STAY BELOW 2^32 = 4294967296 -- KSP parses the
# `part = <name>_<uid>` suffix as a UInt32 and kRPC launch_vessel throws server-side
# otherwise (assumption A3; measured on the first FORGE-b17-duna-pad attempt-set).
UID_CEILING = 4294967296
_PART_UID = {
    "probe": 4210000001, "small_container": 4210000002, "cargo_container": 4210000003,
    "tank": 4210000004, "engine": 4210000005, "fuelcell": 4210000006,
}
_PERSISTENT_ID = {k: 1600000000 + i * 7717 for i, k in enumerate(sorted(_PART_UID))}

# ONE stage: the LV-T45 ignites on the single pad click. Nothing else stages, and
# nothing separates -- unlike GS-1 this craft has no staging contract to prove, it
# only has to LAUNCH and sit there while the harvest reads the pad state.
STAGE_IGNITE = 0

# The PARTIAL propellant load (L3 / assumption A5). The FL-T400 keeps its stock
# 180 LF / 220 Ox capacity; only the two `amount =` lines move. 100 / 180 LF is
# 100 debitable against the 14.0 in-game floor and 80 free against the 18.0 one.
TANK_LIQUID_FUEL = 100.0
TANK_OXIDIZER = 122.0
TANK_MAX_LIQUID_FUEL = 180.0
TANK_MAX_OXIDIZER = 220.0

# The in-game floors this craft is sized against, restated here so `verify()` can
# assert the margins rather than the raw numbers. Sources:
#   LogisticsMultiStopRuntimeTests.FixtureMinStoredLf     = 5 + 4 + 5 = 14
#   LogisticsMultiStopRuntimeTests.FixtureMinFreeCapacity = (5 + 4) * 2 = 18
MIN_DEBITABLE_LIQUID_FUEL = 14.0
MIN_FREE_LIQUID_FUEL_CAPACITY = 18.0

# The stored cargo roster of the FIRST inventory module, in slot order (L5).
# Both are stock `ModuleCargoPart`s with `stackableQuantity = 4` and
# `packedVolume = 5` (Squad/Parts/Cargo/RepairKit/RepairKit.cfg and
# Squad/Parts/Cargo/ScienceKit/evaScienceKit.cfg), so two of them occupy 10 of the
# small container's 180 packed-volume limit and two of its three slots.
# NOTE THE SPELLING: the science kit's PART NAME is `evaScienceKit`, not
# `evaScience` -- the in-game multi-module cell probes a candidate list that
# includes the shorter string, and PartLoader returns null for it.
STORED_CARGO = ("evaRepairKit", "evaScienceKit")
# InventorySlots from the part cfgs (Squad/Parts/Cargo/CargoContainers/*.cfg).
FIRST_INVENTORY_SLOTS = 3       # smallCargoContainer: 3 slots / 180 packed volume
LATER_INVENTORY_SLOTS = 9       # cargoContainer:      9 slots / 650 packed volume


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
    (measured from +X toward +Z) on a vertical cylinder. Derivation and stock
    validation: build_gs1_craft.py's module docstring."""
    axis = SRF_ATTACH_AXIS[part_name]
    if axis == "+X":
        psi = 180.0 - azimuth_deg
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


# ---------------------------------------------------------------------------
# The two surgical substitutions (see the module docstring). Everything else is
# a verbatim stock tail.
# ---------------------------------------------------------------------------


def _node_span(lines, header, indent):
    """[start, end] line indices of the node whose header line is ``header`` at
    ``indent`` tabs, inclusive of its closing brace. Raises if absent."""
    tab = "\t" * indent
    for i, line in enumerate(lines):
        if line != tab + header:
            continue
        depth = 0
        j = i + 1
        while j < len(lines):
            stripped = lines[j].strip()
            if stripped == "{":
                depth += 1
            elif stripped == "}":
                depth -= 1
                if depth == 0:
                    return i, j
            j += 1
    raise ValueError("no %r node at indent %d" % (header, indent))


def _module_span(lines, module_name, indent):
    """[start, end] line indices of the MODULE node whose `name =` is
    ``module_name``, at ``indent`` tabs."""
    tab = "\t" * indent
    for i, line in enumerate(lines):
        if line != tab + "MODULE":
            continue
        if lines[i + 2].strip() != "name = " + module_name:
            continue
        depth = 0
        j = i + 1
        while j < len(lines):
            stripped = lines[j].strip()
            if stripped == "{":
                depth += 1
            elif stripped == "}":
                depth -= 1
                if depth == 0:
                    return i, j
            j += 1
    raise ValueError("no MODULE %s at indent %d" % (module_name, indent))


def _tank_tail() -> str:
    """The FL-T400 tail with the propellant load reduced to the PARTIAL fill (L3 /
    assumption A5). Only the two `amount =` lines move; `maxAmount` and every other
    line stay byte-identical to the stock donor."""
    out = []
    current = None
    for line in TAILS["fuelTank"].split("\n"):
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


def _filled_inventory_module() -> str:
    """The KSP-1.12.5-written FILLED `ModuleInventoryPart` block, retargeted to this
    craft's cargo roster: the donor's FIRST STOREDPART (an `evaChute`, slotIndex 0)
    is replaced by the verbatim `evaRepairKit` STOREDPART, and the `inventory =`
    roster line is rewritten to match. The donor's SECOND STOREDPART is already the
    `evaScienceKit` at slotIndex 1 and is untouched."""
    lines = FILLED_INVENTORY_MODULE.split("\n")
    start, end = _node_span(lines, "STOREDPART", 3)
    lines = lines[:start] + REPAIR_KIT_STOREDPART.split("\n") + lines[end + 1:]
    out = []
    for line in lines:
        if line.strip().startswith("inventory = "):
            out.append("\t\tinventory = %s" % ",".join(STORED_CARGO))
            continue
        out.append(line)
    return "\n".join(out)


def _small_container_tail() -> str:
    """The smallCargoContainer tail with its (empty) `ModuleInventoryPart` MODULE
    block swapped for the FILLED one. Every other line -- ACTIONS, PARTDATA, the
    ModuleCargoPart block -- stays byte-identical to the Squad prebuilt donor."""
    lines = TAILS["smallCargoContainer"].split("\n")
    start, end = _module_span(lines, "ModuleInventoryPart", 1)
    return "\n".join(lines[:start] + _filled_inventory_module().split("\n")
                     + lines[end + 1:])


def tail_for(part: Part) -> str:
    if part.name == "fuelTank":
        return _tank_tail()
    if part.name == "smallCargoContainer":
        return _small_container_tail()
    return TAILS[part.name]


def layout():
    """Build the full ordered part list with every position derived. Returns the
    list; the root is element 0 (KSP takes the first PART as the root).

    PART ORDER IS THE CONTRACT (assumption A4): the in-game multi-module cell walks
    `vessel.parts` in order and takes moduleRefs[0] as the module it fills. The
    root carries no inventory, the small 3-slot container comes next, and the 9-slot
    cargo container comes after it."""
    probe_y = ROOT_Y
    probe = Part("probe", "probeCoreOcto2.v2", (0.0, probe_y, 0.0))

    small_y = probe_y + NODE_OFFSET["probeCoreOcto2.v2"]["bottom"] \
        - NODE_OFFSET["smallCargoContainer"]["top"]
    small = Part("small_container", "smallCargoContainer", (0.0, small_y, 0.0))

    cargo_y = small_y + NODE_OFFSET["smallCargoContainer"]["bottom"] \
        - NODE_OFFSET["cargoContainer"]["top"]
    cargo = Part("cargo_container", "cargoContainer", (0.0, cargo_y, 0.0))

    tank_y = cargo_y + NODE_OFFSET["cargoContainer"]["bottom"] \
        - NODE_OFFSET["fuelTank"]["top"]
    tank = Part("tank", "fuelTank", (0.0, tank_y, 0.0))

    eng_y = tank_y + NODE_OFFSET["fuelTank"]["bottom"] \
        - NODE_OFFSET["liquidEngine2"]["top"]
    eng = Part("engine", "liquidEngine2", (0.0, eng_y, 0.0),
               istg=STAGE_IGNITE, dstg=0, sidx=0, sqor=0)

    # The fuel cell hangs radially off the tank at the azimuth build_gs1_craft.py
    # validated against stock (phi = 90 for a local-+X attach part).
    fuelcell = Part("fuelcell", "FuelCell",
                    srf_position(tank.pos, 90.0, 0.0),
                    rot=srf_rotation("FuelCell", 90.0), attm=1)
    fuelcell.srfn = tank.uid

    probe.links = [small.uid]
    probe.attn = [("bottom", small.uid, NODE_OFFSET["probeCoreOcto2.v2"]["bottom"])]
    small.links = [cargo.uid]
    small.attn = [("top", probe.uid, NODE_OFFSET["smallCargoContainer"]["top"]),
                  ("bottom", cargo.uid, NODE_OFFSET["smallCargoContainer"]["bottom"])]
    cargo.links = [tank.uid]
    cargo.attn = [("top", small.uid, NODE_OFFSET["cargoContainer"]["top"]),
                  ("bottom", tank.uid, NODE_OFFSET["cargoContainer"]["bottom"])]
    tank.links = [eng.uid, fuelcell.uid]
    tank.attn = [("top", cargo.uid, NODE_OFFSET["fuelTank"]["top"]),
                 ("bottom", eng.uid, NODE_OFFSET["fuelTank"]["bottom"])]
    eng.attn = [("top", tank.uid, NODE_OFFSET["liquidEngine2"]["top"])]

    return [probe, small, cargo, tank, eng, fuelcell]


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
        "description = Logistics pad rig for the isolated H38 lane. One stage, "
        "uncrewed: an OKTO2 root over a 3-slot small cargo container (an EVA "
        "Repair Kit and an EVA Experiments Kit stowed, one slot free), an empty "
        "9-slot cargo container, a partly fuelled FL-T400, an LV-T45, and a fuel "
        "cell for the harvest-window tests. Built by construction by "
        "harness/tools/build_logi_craft.py.",
        "type = VAB",
        "size = %s" % _vec(max(xs) - min(xs) + 1.25, max(ys) - min(ys) + 1.0,
                           max(zs) - min(zs) + 1.25),
        "steamPublishedFileId = 0",
        "persistentId = 2200110038",
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
    "probeCoreOcto2.v2": 1, "smallCargoContainer": 1, "cargoContainer": 1,
    "fuelTank": 1, "liquidEngine2": 1, "FuelCell": 1,
}

# 38 quickload-restores have to fit the isolated lane's 540 s step budget and
# quickload cost scales with part count, so the rig is capped deliberately low.
MAX_PARTS = 9


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


def inventory_part_order(lines):
    """[(owning part name, [stored partName, ...])] for every ModuleInventoryPart,
    in FILE order -- which is the vessel part order the in-game probe walks."""
    out = []
    owner = None
    in_module = False
    stored = None
    for line in lines:
        s = line.strip()
        if line.startswith("\tpart = "):
            owner = s[len("part = "):].rsplit("_", 1)[0]
        if s == "name = ModuleInventoryPart" and line.startswith("\t\t"):
            in_module = True
            stored = []
            continue
        if in_module:
            if s.startswith("partName = "):
                stored.append(s[len("partName = "):])
            # The module ends at the next part-level MODULE / RESOURCE header or at
            # the PART's own closing brace.
            elif line in ("\tMODULE", "\tRESOURCE", "}"):
                out.append((owner, stored))
                in_module = False
                stored = None
    if in_module:
        out.append((owner, stored))
    return out


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
    if len(records) > MAX_PARTS:
        problems.append("%d parts exceeds the %d-part cap; the isolated lane's 38 "
                        "quickload restores must fit its step budget and quickload "
                        "cost scales with part count" % (len(records), MAX_PARTS))
    if not records or records[0][0] != "probeCoreOcto2.v2":
        problems.append("the ROOT (first PART) must be the probeCoreOcto2.v2: KSP "
                        "takes the first PART as the root, and every stock command "
                        "POD carries a ModuleInventoryPart that would then become "
                        "the FIRST inventory module (assumption A4)")

    # A3: the UInt32 uid ceiling that cost the B17 forge an attempt-set.
    for key, uid in sorted(_PART_UID.items()):
        if uid >= UID_CEILING:
            problems.append("part uid %d (%s) is at or above the UInt32 ceiling %d; "
                            "kRPC launch_vessel throws server-side"
                            % (uid, key, UID_CEILING))

    # L1: a launchable pad rocket. UnloadedFuelVesselFixture snapshots the ACTIVE
    # PRELAUNCH vessel; without an engine there is nothing to launch and nothing to
    # clone into the parking orbit.
    if "name = ModuleEngines" not in text:
        problems.append("the rig must carry at least one ModuleEngines: the isolated "
                        "Logistics fixture snapshots a PRELAUNCH pad rocket")
    # Uncrewed control: exactly one ModuleCommand, on the probe core.
    if text.count("name = ModuleCommand") != 1:
        problems.append("expected exactly one ModuleCommand (the OKTO2 root, so the "
                        "rig is controllable UNCREWED); found %d"
                        % text.count("name = ModuleCommand"))

    # L4: the fuel cell. Two HarvestCapture cells skip without a BaseConverter.
    if "name = ModuleResourceConverter" not in text:
        problems.append("no ModuleResourceConverter (BaseConverter) block: the two "
                        "HarvestCapture cells skip with 'a stock fuel cell suffices "
                        "- add one to the test craft'")
    by_name = {}
    for name, rec in records:
        by_name.setdefault(name, []).append(rec)
    for rec in by_name.get("FuelCell", []):
        if not rec.get("srfN", "").startswith("srfAttach,fuelTank"):
            problems.append("the FuelCell must be surface-attached to the FL-T400, "
                            "got %r" % (rec.get("srfN"),))

    # L2 / L3: the LiquidFuel supply, partially filled and flowing.
    if "name = LiquidFuel" not in text:
        problems.append("no LiquidFuel RESOURCE node: UnloadedFuelVesselFixture "
                        "returns reason=no-liquidfuel-resource and every "
                        "unloaded-depot test skips")
    if "name = Oxidizer" not in text:
        problems.append("no Oxidizer RESOURCE node: the fuel cell consumes LF AND "
                        "Ox, and cannot activate without both")
    if ("\t\tamount = %s" % _fmt(TANK_LIQUID_FUEL)) not in text:
        problems.append("the FL-T400 does not carry the partial LiquidFuel load %s"
                        % _fmt(TANK_LIQUID_FUEL))
    if ("\t\tamount = %s" % _fmt(TANK_OXIDIZER)) not in text:
        problems.append("the FL-T400 does not carry the partial Oxidizer load %s"
                        % _fmt(TANK_OXIDIZER))
    if ("\t\tmaxAmount = %s" % _fmt(TANK_MAX_LIQUID_FUEL)) not in text:
        problems.append("the FL-T400's stock LiquidFuel capacity %s was rewritten; "
                        "only the `amount` lines may move"
                        % _fmt(TANK_MAX_LIQUID_FUEL))
    if ("\t\tmaxAmount = %s" % _fmt(TANK_MAX_OXIDIZER)) not in text:
        problems.append("the FL-T400's stock Oxidizer capacity %s was rewritten; "
                        "only the `amount` lines may move"
                        % _fmt(TANK_MAX_OXIDIZER))
    if TANK_LIQUID_FUEL < MIN_DEBITABLE_LIQUID_FUEL:
        problems.append("stored LiquidFuel %s is below the in-game floor %s "
                        "(LogisticsMultiStopRuntimeTests.FixtureMinStoredLf)"
                        % (_fmt(TANK_LIQUID_FUEL), _fmt(MIN_DEBITABLE_LIQUID_FUEL)))
    if TANK_MAX_LIQUID_FUEL - TANK_LIQUID_FUEL < MIN_FREE_LIQUID_FUEL_CAPACITY:
        problems.append("free LiquidFuel capacity %s is below the in-game floor %s "
                        "(LogisticsMultiStopRuntimeTests.FixtureMinFreeCapacity)"
                        % (_fmt(TANK_MAX_LIQUID_FUEL - TANK_LIQUID_FUEL),
                           _fmt(MIN_FREE_LIQUID_FUEL_CAPACITY)))
    if "flowState = False" in text:
        problems.append("a RESOURCE is flow-DISABLED; the production probe and the "
                        "delivery writer both need flowState = True")

    # L5: THE CELL THE WHOLE H38 INVENTORY SLICE RESTS ON. Two inventory modules,
    # the FIRST holding exactly the two kits with a slot to spare, a LATER one
    # empty.
    inventories = inventory_part_order(lines)
    if len(inventories) != 2:
        problems.append("expected exactly two ModuleInventoryPart modules (the "
                        "multi-module delivery cell skips below two); found %d"
                        % len(inventories))
    else:
        first_owner, first_stored = inventories[0]
        later_owner, later_stored = inventories[1]
        if first_owner != "smallCargoContainer":
            problems.append("the FIRST inventory module in part order must be the "
                            "3-slot smallCargoContainer, got %r" % (first_owner,))
        if later_owner != "cargoContainer":
            problems.append("the LATER inventory module must be the 9-slot "
                            "cargoContainer, got %r" % (later_owner,))
        if tuple(first_stored) != STORED_CARGO:
            problems.append("the first inventory must stow exactly %r in slot order, "
                            "got %r" % (list(STORED_CARGO), first_stored))
        if later_stored:
            problems.append("the LATER container must be EMPTY so the multi-module "
                            "cell has a free slot after it fills the first one; it "
                            "stows %r" % (later_stored,))
        if FIRST_INVENTORY_SLOTS - len(STORED_CARGO) < 1:
            problems.append("the first inventory module must keep at least one EMPTY "
                            "slot: %d slots, %d stowed"
                            % (FIRST_INVENTORY_SLOTS, len(STORED_CARGO)))
        if LATER_INVENTORY_SLOTS < 1:
            problems.append("the later container must declare at least one slot")

    for item in STORED_CARGO:
        if ("partName = %s" % item) not in text:
            problems.append("stored cargo %r is missing from the first inventory"
                            % (item,))
    if "partName = evaChute" in text:
        problems.append("the donor's evaChute STOREDPART survived the substitution; "
                        "slot 0 must carry the evaRepairKit")
    if "inventory = evaChute" in text:
        problems.append("the donor's `inventory =` roster line survived the "
                        "substitution")
    # Both kits are stowed at quantity 1 out of a stackableQuantity of 4 (their part
    # cfgs), so the first module sits far under its 180 packed-volume limit and the
    # multi-module cell's fill of the remaining slot has room.
    stowed_singly = sum(1 for line in lines if line == "\t\t\t\tquantity = 1")
    if stowed_singly != len(STORED_CARGO):
        problems.append("expected %d STOREDPART(s) stowed at quantity 1, found %d"
                        % (len(STORED_CARGO), stowed_singly))
    return problems


def main(argv=None) -> int:
    p = argparse.ArgumentParser(
        prog="build_logi_craft",
        description="Build or verify the committed Logi Cargo Rig .craft (the H38 "
                    "isolated-Logistics pad fixture's craft).")
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
        # committed craft carry. `read_lines` normalizes, so the drift comparison is
        # line-based and unaffected either way; this is only about the file on disk
        # being byte-shaped like one KSP produced.
        with open(CRAFT_PATH, "w", encoding="utf-8", newline="\r\n") as fh:
            fh.write("\n".join(built))
        sys.stdout.write("[LogiCraft] wrote %s (%d lines)\n"
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
        sys.stdout.write("[LogiCraft] PROBLEM: %s\n" % problem)
    if problems:
        return 1
    sys.stdout.write("[LogiCraft] OK: %s satisfies every post-condition\n" % CRAFT_PATH)
    return 0


# ---------------------------------------------------------------------------
# VERBATIM STOCK PART TAILS. Each entry is the EVENTS-onward span of one PART
# node, lifted byte-for-byte out of a craft KSP or Squad itself wrote; the source
# craft is named above each entry, and every one of them SHIPS INSIDE THE STOCK
# GAME. Nothing here is authored: every MODULE block, its field set and its
# ordering are KSP's own, which is what makes a module-index mismatch impossible
# to introduce by hand. The two rewrites this file DOES apply (the tank's
# propellant load and the small container's inventory module) are surgical
# substitutions in _tank_tail / _small_container_tail, and both are asserted by
# verify().
# ---------------------------------------------------------------------------
TAILS = {
    # verbatim from stock VAB/Z-MAP Satellite Launch Kit.craft (reused byte for
    # byte from build_gs1_craft.py's TAILS, same donor).
    'probeCoreOcto2.v2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCommand\n\t\tisEnabled = True\n\t\thibernation = False\n\t\thibernateOnWarp = False\n\t\tactiveControlPointName = _default\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tMakeReferenceToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tHibernateToggle\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSAS\n\t\tisEnabled = True\n\t\tstandaloneToggle = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleKerbNetAccess\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOpenKerbNetAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleDataTransmitter\n\t\tisEnabled = True\n\t\txmitIncomplete = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStartTransmissionAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\tactive = False\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTripLogger\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tLog\n\t\t{\n\t\t\tflight = 0\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 5\n\t\tmaxAmount = 5\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from Squad's own prebuilt contract craft
    # GameData/Squad/Contracts/PrebuiltCraft/RoverContract/Contract Rover 10a.craft.
    # NO stock VAB/SPH craft carries a cargo container at all (checked, all 48), and
    # the RoverContract craft ship inside the stock game, so they are the closest
    # thing to a stock donor that exists. _small_container_tail() replaces the
    # (empty) ModuleInventoryPart block below.
    'smallCargoContainer': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleInventoryPart\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tSTACKAMOUNTS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from Squad's own prebuilt contract craft
    # GameData/Squad/Contracts/PrebuiltCraft/RoverContract/Contract Rover 10a.craft.
    # Used UNMODIFIED: its inventory is empty, which is exactly what the LATER
    # container has to be.
    'cargoContainer': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t\tToggleSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t}\n\t\tSetSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t}\n\t\tRemoveSameVesselInteraction\n\t\t{\n\t\t\tactionGroup = None\n\t\t}\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleInventoryPart\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tSTACKAMOUNTS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleCargoPart\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Kerbal X.craft (the FL-T400 booster tanks).
    # _tank_tail() rewrites its two `amount =` lines to the partial load.
    'fuelTank': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModulePartVariants\n\t\tisEnabled = True\n\t\tuseVariantMass = True\n\t\tstagingEnabled = True\n\t\tselectedVariant = BlackAndWhite\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 180\n\t\tmaxAmount = 180\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}\n\tRESOURCE\n\t{\n\t\tname = Oxidizer\n\t\tamount = 220\n\t\tmaxAmount = 220\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
    # verbatim from stock VAB/Orbiter One.craft (reused byte for byte from
    # build_gs1_craft.py's TAILS, same donor).
    'liquidEngine2': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t\tisEnabled = True\n\t\tstaged = False\n\t\tflameout = False\n\t\tEngineIgnited = False\n\t\tengineShutdown = False\n\t\tcurrentThrottle = 0\n\t\tthrustPercentage = 100\n\t\tmanuallyOverridden = False\n\t\tincludeinDVCalcs = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tOnAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tShutdownAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tActivateAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleJettison\n\t\tisEnabled = True\n\t\tactivejettisonName = fairing\n\t\tisJettisoned = False\n\t\tshroudHideOverride = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tJettisonAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleGimbal\n\t\tisEnabled = True\n\t\tgimbalLock = False\n\t\tgimbalLimiter = 100\n\t\tcurrentShowToggles = False\n\t\tenableYaw = True\n\t\tenablePitch = True\n\t\tenableRoll = True\n\t\tgimbalActive = False\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tToggleAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tLockAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tFreeAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tTogglePitchAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleYawAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleRollAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = FXModuleAnimateThrottle\n\t\tisEnabled = True\n\t\tanimState = 0\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleAlternator\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleSurfaceFX\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleTestSubject\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}',
    # verbatim from stock VAB/Orbiter 1A.craft, the ONLY stock VAB craft that
    # carries a FuelCell (Prospector Rover is the SPH one).
    'FuelCell': '\tEVENTS\n\t{\n\t}\n\tACTIONS\n\t{\n\t}\n\tPARTDATA\n\t{\n\t}\n\tMODULE\n\t{\n\t\tname = ModuleResourceConverter\n\t\tisEnabled = True\n\t\tEfficiencyBonus = 1\n\t\tIsActivated = False\n\t\tstagingEnabled = True\n\t\tlastUpdateTime = 0\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t\tStopResourceConverterAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tStartResourceConverterAction\n\t\t\t{\n\t\t\t\tactionGroup = None\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t\tToggleResourceConverterAction\n\t\t\t{\n\t\t\t\tactionGroup = Custom04\n\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n\tRESOURCE\n\t{\n\t\tname = ElectricCharge\n\t\tamount = 50\n\t\tmaxAmount = 50\n\t\tflowState = True\n\t\tisTweakable = True\n\t\thideFlow = False\n\t\tisVisible = True\n\t\tflowMode = Both\n\t}',
}


# The KSP-1.12.5-written FILLED ModuleInventoryPart block, verbatim from a real
# VAB-authored craft (a Mk1-3 pod carrying two stowed cargo items). Squad's own
# prebuilt rover craft above predate the stored-cargo shape - their inventory
# modules write an empty STACKAMOUNTS node and no STOREDPARTS at all - so a
# POPULATED inventory has no stock-shipped donor and this is the nearest thing:
# bytes KSP's own ModuleInventoryPart.OnSave produced. Nothing in the block is
# mod-authored (it is stock module output; the donor install's mods contribute
# their own separate MODULE blocks, none of which are copied here).
# _filled_inventory_module() swaps its first STOREDPART for the repair kit below
# and rewrites the `inventory =` roster line.
FILLED_INVENTORY_MODULE = '\tMODULE\n\t{\n\t\tname = ModuleInventoryPart\n\t\tisEnabled = True\n\t\tstagingEnabled = True\n\t\tinventory = evaChute,evaScienceKit\n\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tSTOREDPARTS\n\t\t{\n\t\t\tSTOREDPART\n\t\t\t{\n\t\t\t\tslotIndex = 0\n\t\t\t\tpartName = evaChute\n\t\t\t\tquantity = 1\n\t\t\t\tstackCapacity = 1\n\t\t\t\tvariantName = \n\t\t\t\tPART\n\t\t\t\t{\n\t\t\t\t\tname = evaChute\n\t\t\t\t\tcid = 4294395490\n\t\t\t\t\tuid = 0\n\t\t\t\t\tmid = 0\n\t\t\t\t\tpersistentId = 1495494027\n\t\t\t\t\tlaunchID = 0\n\t\t\t\t\tparent = 0\n\t\t\t\t\tposition = 0,0,0\n\t\t\t\t\trotation = 0,0,0,0\n\t\t\t\t\tmirror = 1,1,1\n\t\t\t\t\tsymMethod = Radial\n\t\t\t\t\tistg = 0\n\t\t\t\t\tresPri = 0\n\t\t\t\t\tdstg = 0\n\t\t\t\t\tsqor = -1\n\t\t\t\t\tsepI = 0\n\t\t\t\t\tsidx = -1\n\t\t\t\t\tattm = 0\n\t\t\t\t\tsameVesselCollision = False\n\t\t\t\t\tsrfN = None, -1\n\t\t\t\t\tmass = 0.00400000019\n\t\t\t\t\tshielded = False\n\t\t\t\t\ttemp = -1\n\t\t\t\t\ttempExt = 0\n\t\t\t\t\ttempExtUnexp = 0\n\t\t\t\t\tstaticPressureAtm = 0\n\t\t\t\t\texpt = 0.5\n\t\t\t\t\tstate = 0\n\t\t\t\t\tPreFailState = 0\n\t\t\t\t\tattached = True\n\t\t\t\t\tautostrutMode = Off\n\t\t\t\t\trigidAttachment = False\n\t\t\t\t\tflag = \n\t\t\t\t\trTrf = evaChute\n\t\t\t\t\tmodCost = 0\n\t\t\t\t\tmodMass = 0\n\t\t\t\t\tmoduleVariantName = \n\t\t\t\t\tmoduleCargoStackableQuantity = 1\n\t\t\t\t\tEVENTS\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tACTIONS\n\t\t\t\t\t{\n\t\t\t\t\t\tToggleSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tSetSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tRemoveSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutOff\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutRoot\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutHeaviest\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutGrandparent\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tResourcesEnableFlow\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\tactive = False\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tResourcesDisableFlow\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\tactive = False\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t\tPARTDATA\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tMODULE\n\t\t\t\t\t{\n\t\t\t\t\t\tname = ModuleCargoPart\n\t\t\t\t\t\tisEnabled = True\n\t\t\t\t\t\tpackedVolume = 10\n\t\t\t\t\t\tbeingAttached = False\n\t\t\t\t\t\tbeingSettled = False\n\t\t\t\t\t\treinitResourcesOnStoreInVessel = False\n\t\t\t\t\t\tstagingEnabled = True\n\t\t\t\t\t\tEVENTS\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t\tACTIONS\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t\tUPGRADESAPPLIED\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t\tSTOREDPART\n\t\t\t{\n\t\t\t\tslotIndex = 1\n\t\t\t\tpartName = evaScienceKit\n\t\t\t\tquantity = 1\n\t\t\t\tstackCapacity = 4\n\t\t\t\tvariantName = \n\t\t\t\tPART\n\t\t\t\t{\n\t\t\t\t\tname = evaScienceKit\n\t\t\t\t\tcid = 4294394926\n\t\t\t\t\tuid = 0\n\t\t\t\t\tmid = 0\n\t\t\t\t\tpersistentId = 3476418211\n\t\t\t\t\tlaunchID = 0\n\t\t\t\t\tparent = 0\n\t\t\t\t\tposition = 0,0,0\n\t\t\t\t\trotation = 0,0,0,0\n\t\t\t\t\tmirror = 1,1,1\n\t\t\t\t\tsymMethod = Radial\n\t\t\t\t\tistg = 0\n\t\t\t\t\tresPri = 0\n\t\t\t\t\tdstg = 0\n\t\t\t\t\tsqor = -1\n\t\t\t\t\tsepI = 0\n\t\t\t\t\tsidx = -1\n\t\t\t\t\tattm = 0\n\t\t\t\t\tsameVesselCollision = False\n\t\t\t\t\tsrfN = None, -1\n\t\t\t\t\tmass = 0.0149999997\n\t\t\t\t\tshielded = False\n\t\t\t\t\ttemp = -1\n\t\t\t\t\ttempExt = 0\n\t\t\t\t\ttempExtUnexp = 0\n\t\t\t\t\tstaticPressureAtm = 0\n\t\t\t\t\texpt = 0.5\n\t\t\t\t\tstate = 0\n\t\t\t\t\tPreFailState = 0\n\t\t\t\t\tattached = True\n\t\t\t\t\tautostrutMode = Off\n\t\t\t\t\trigidAttachment = False\n\t\t\t\t\tflag = \n\t\t\t\t\trTrf = evaScienceKit\n\t\t\t\t\tmodCost = 0\n\t\t\t\t\tmodMass = 0\n\t\t\t\t\tmoduleVariantName = \n\t\t\t\t\tmoduleCargoStackableQuantity = 4\n\t\t\t\t\tEVENTS\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tACTIONS\n\t\t\t\t\t{\n\t\t\t\t\t\tToggleSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tSetSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tRemoveSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutOff\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutRoot\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutHeaviest\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutGrandparent\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tResourcesEnableFlow\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\tactive = False\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tResourcesDisableFlow\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\tactive = False\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t\tPARTDATA\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tMODULE\n\t\t\t\t\t{\n\t\t\t\t\t\tname = ModuleCargoPart\n\t\t\t\t\t\tisEnabled = True\n\t\t\t\t\t\tpackedVolume = 5\n\t\t\t\t\t\tbeingAttached = False\n\t\t\t\t\t\tbeingSettled = False\n\t\t\t\t\t\treinitResourcesOnStoreInVessel = False\n\t\t\t\t\t\tstagingEnabled = True\n\t\t\t\t\t\tEVENTS\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t\tACTIONS\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t\tUPGRADESAPPLIED\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}'


# The evaRepairKit STOREDPART, verbatim from the same shape of donor craft, at
# slotIndex 0. Copied rather than hand-written because a STOREDPART carries a full
# PART snapshot (cid / persistentId / the ModuleCargoPart block / the nine
# Autostrut+SameVesselInteraction ACTIONS), and authoring one by hand is exactly
# the kind of invention this file exists to avoid.
REPAIR_KIT_STOREDPART = '\t\t\tSTOREDPART\n\t\t\t{\n\t\t\t\tslotIndex = 0\n\t\t\t\tpartName = evaRepairKit\n\t\t\t\tquantity = 1\n\t\t\t\tstackCapacity = 4\n\t\t\t\tvariantName = \n\t\t\t\tPART\n\t\t\t\t{\n\t\t\t\t\tname = evaRepairKit\n\t\t\t\t\tcid = 4294394552\n\t\t\t\t\tuid = 0\n\t\t\t\t\tmid = 0\n\t\t\t\t\tpersistentId = 2537949221\n\t\t\t\t\tlaunchID = 0\n\t\t\t\t\tparent = 0\n\t\t\t\t\tposition = 0,0,0\n\t\t\t\t\trotation = 0,0,0,0\n\t\t\t\t\tmirror = 1,1,1\n\t\t\t\t\tsymMethod = Radial\n\t\t\t\t\tistg = 0\n\t\t\t\t\tresPri = 0\n\t\t\t\t\tdstg = 0\n\t\t\t\t\tsqor = -1\n\t\t\t\t\tsepI = 0\n\t\t\t\t\tsidx = -1\n\t\t\t\t\tattm = 0\n\t\t\t\t\tsameVesselCollision = False\n\t\t\t\t\tsrfN = None, -1\n\t\t\t\t\tmass = 0.00499999989\n\t\t\t\t\tshielded = False\n\t\t\t\t\ttemp = -1\n\t\t\t\t\ttempExt = 0\n\t\t\t\t\ttempExtUnexp = 0\n\t\t\t\t\tstaticPressureAtm = 0\n\t\t\t\t\texpt = 0.5\n\t\t\t\t\tstate = 0\n\t\t\t\t\tPreFailState = 0\n\t\t\t\t\tattached = True\n\t\t\t\t\tautostrutMode = Off\n\t\t\t\t\trigidAttachment = False\n\t\t\t\t\tflag = \n\t\t\t\t\trTrf = evaRepairKit\n\t\t\t\t\tmodCost = 0\n\t\t\t\t\tmodMass = 0\n\t\t\t\t\tmoduleVariantName = \n\t\t\t\t\tmoduleCargoStackableQuantity = 4\n\t\t\t\t\tEVENTS\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tACTIONS\n\t\t\t\t\t{\n\t\t\t\t\t\tToggleSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tSetSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tRemoveSameVesselInteraction\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutOff\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutRoot\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutHeaviest\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tAutostrutGrandparent\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tResourcesEnableFlow\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\tactive = False\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t\tResourcesDisableFlow\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\tactionGroup = None\n\t\t\t\t\t\t\tactive = False\n\t\t\t\t\t\t\twasActiveBeforePartWasAdjusted = False\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t\tPARTDATA\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tMODULE\n\t\t\t\t\t{\n\t\t\t\t\t\tname = ModuleCargoPart\n\t\t\t\t\t\tisEnabled = True\n\t\t\t\t\t\tpackedVolume = 5\n\t\t\t\t\t\tbeingAttached = False\n\t\t\t\t\t\tbeingSettled = False\n\t\t\t\t\t\treinitResourcesOnStoreInVessel = False\n\t\t\t\t\t\tstagingEnabled = True\n\t\t\t\t\t\tEVENTS\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t\tACTIONS\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t\tUPGRADESAPPLIED\n\t\t\t\t\t\t{\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}'

if __name__ == "__main__":
    sys.exit(main())
