#!/usr/bin/env python3
"""Build the committed `logi-cargo-career` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. `RouteRewindRedeliveryInGameTest` (category Logistics) asserts that
career funds stay flat across a supply-route delivery, a rewind past it and the
re-delivery (supply routes carry no money, LOGISTICS-WITHOUT-MONEY phase 1). Every host
that runs the category isolated (H38, H39, H40, RVR-6) is SANDBOX, where stock creates no
`Funding` singleton, so the cell logs "no Funding singleton (Sandbox) - funds-unchanged
assertion skipped" and the funds half never runs. This fixture is H38's host,
`logi-cargo-pad`'s `Logi Cargo Rig`, moved into a CAREER save, so the same 46 admitted
cells run with a live `Funding` singleton (host of `H73-logistics-isolated-career`).

THE RECIPE IS `career-pad-craft`'s / `lab-pad-career`'s splice shape (the helpers come
from build_career_pad_craft.py), WITH THESE EDITS against the `fresh-career` save (copied,
never modified), plus a loadmeta restamp (vessel count + UT):

  1. `fresh-career`'s empty `FLIGHTSTATE` is replaced by `logi-cargo-pad`'s with every
     vessel but the rig dropped (the stock asteroid `Ast. VPA-167`, `type = SpaceObject`,
     which `fresh-career`'s `DiscoverableObjects` never registered) and `activeVessel`
     re-indexed onto the rig (1 -> 0). The rig's VESSEL node is the donor's BYTE FOR BYTE
     (pid, persistentId 2200110038, `type = Probe`, `sit = PRELAUNCH`, the FL-T400 at LF
     100/180 + Ox 122/220, the FuelCell, the two inventory containers in probe order and
     their stored evaRepairKit / evaScienceKit). The save clock is the donor's (UT 25.26).
     `build_career_pad_craft.build()` does not fit as is: it keeps only `type = Ship`
     (the rig is `Probe`) and swaps a crew roster row (the rig is uncrewed), so this
     builder selects the vessel by NAME and leaves the ROSTER alone.
  2. NO roster edit. The rig carries no `crew = ` line, so `fresh-career`'s four Available
     kerbals and its applicant stay as they are.
  3. The donor's `SCENARIO{name=ParsekScenario}` node is inserted after ProgressTracking
     WITH its one `MILESTONE_STATE` child excised (the `lab-lko-science` /
     `duna-park-probe` precedent: it indexes the forge run's
     `Parsek/GameState/milestones.pgsm` sidecar, which no fixture ships, so it is a dead
     reference). `scene` and `gameStateEventCount` stay. The node is LOAD-BEARING: a
     FLIGHT-route LoadGame needs it or the module is never created (CL-1 flight 1).
  4. The ResearchAndDevelopment node gains the 29 `Tech` nodes of
     build_lab_lko_science.TECH_NODES, inserted after `start`, each `state = Available`
     with its tree cost and full part list, then the 3 RIG_EXTRA_TECH_NODES below
     (miniaturization, precisionEngineering, advExploration). The SCIENCE POOL STAYS
     100: the nodes are granted, not bought, so no ledger or pool consumer sees a spend
     that never happened. See "THE TECH GRANT" below for why the whole closure and not
     a guessed subset.
  5. `Title` becomes `logi-cargo-career (CAREER)` and the Description names the splice.

THE TECH GRANT. Unlocks are optional for this fixture: a persisted VESSEL loads in career
regardless of unlock state (fixtures README; `career-gs1-two-stage-pad` is a start-node-only
career whose booster is rooted on this rig's own unresearched `probeCoreOcto2.v2`, and
QL-2b live-proved it), and the Logistics cells never launch from an editor. They are granted anyway so the
career's R&D state is consistent with the craft on its pad. The rig's parts map onto the
automation instance's tree (CommunityTechTree + ProbesBeforeCrew, derived 2026-10-17 from
its ModuleManager.ConfigCache, see build_lab_lko_science.py) as RIG_PART_TECH below:
liquidEngine2 -> basicRocketry, fuelTank -> advRocketry, smallCargoContainer and the
stored evaRepairKit -> spaceExploration, FuelCell -> electrics. Their PREREQUISITE CHAINS
need the tree's RDNode Parent entries, which no committed file carries, so the
four-node closure cannot be derived here. TECH_NODES is the one committed set that is
ancestor-closed by derivation and contains all four, and it is exactly the set
`lab-pad-career` carries in career (LAB-12 flew it green, `2026-10-18_0229`), so the
whole closure is granted rather than a guessed subset.

THE OTHER THREE RIG PARTS appear in no TECH_NODES part list: `probeCoreOcto2.v2` (the
rig's root), `cargoContainer` (the 9-slot container) and `evaScienceKit` (stored). Their
nodes were READ 2026-10-21 on the operator PC from the automation instance's
`GameData/ModuleManager.ConfigCache` (the effective TechRequired) and
`GameData/ModuleManager.TechTree` (the RDNode cost, Parent entries and anyToUnlock):
    probeCoreOcto2.v2 -> precisionEngineering  cost 160, parents miniaturization OR
                                               electrics (anyToUnlock = True)
    cargoContainer    -> advExploration        cost 160, parent spaceExploration
    evaScienceKit     -> miniaturization       cost  90, parent basicScience
None of the three nodes is in TECH_NODES, and every parent is (basicScience, electrics,
spaceExploration) or is one of the three (miniaturization), so RIG_EXTRA_TECH_NODES
keeps the granted set ancestor-closed; both of precisionEngineering's parents are
granted, the same superset rule TECH_NODES uses for anyToUnlock nodes. UNLIKE TECH_NODES,
each extra node lists ONLY the rig part the read named, not every part the node unlocks
(the read did not enumerate them). In CAREER a Tech node's `part = ` lines are the
PURCHASED parts, so the other parts of these three nodes read as researched but not
purchased, a state stock loads (it is what research with entry purchase on leaves), and
nothing the Logistics cells do opens an editor. Every rig part is now mapped, and
verify() proves each is listed by a granted node and every declared parent is granted.
If the instance's tree pins move (a CTT or PBC upgrade), re-read and re-run this
builder.

WHAT IS NOT TOUCHED. Every other career surface is `fresh-career`'s: Mode CAREER, the
PARAMETERS block (note `ResourceTransferObeyCrossfeed = True`, False in the SANDBOX
donor), every facility at level 0 (a sandbox has every facility at its top level), Funding
500000, science 100, reputation 0, an EMPTY ProgressTracking tree, empty contracts and
strategies, the roster. `AddOns/` is the base's. No craft file is copied and no
`shared-ships.toml` row is added: H73 launches nothing (the overlay exists for kRPC's
`launch_vessel`), so a lane that ever launches the rig from this save adds the row.

Usage:
    python harness/tools/build_logi_cargo_career.py            # write the fixture
    python harness/tools/build_logi_cargo_career.py --check    # re-derive, compare

`--check` rebuilds from the committed inputs and compares the bytes with the committed
fixture; `harness/lib/test_logi_cargo_career.py` runs it in-process, so a change to
`fresh-career`, `logi-cargo-pad`, TECH_NODES or the shared recipe reds in the suite
instead of drifting into a flight.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import Dict, List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import build_career_pad_craft as _recipe  # noqa: E402
import build_lab_lko_science as _tech  # noqa: E402

_SAVES = os.path.join(os.path.dirname(_HERE), "fixtures", "saves")

BASE_NAME = "fresh-career"
DONOR_NAME = "logi-cargo-pad"
TARGET_NAME = "logi-cargo-career"
VESSEL_NAME = "Logi Cargo Rig"
VESSEL_PERSISTENT_ID = "2200110038"
VESSEL_TYPE = "Probe"
TITLE = "logi-cargo-career (CAREER)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: fresh-career with logi-cargo-pad's "
               "Logi Cargo Rig PRELAUNCH on the pad, uncrewed, 32 tech nodes granted).")
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."
TEXT_FILES = ("persistent.sfs", "persistent.loadmeta")

EXPECT_MODE = "CAREER"
EXPECT_SITUATION = "PRELAUNCH"
EXPECT_FUNDS = "500000"
EXPECT_SCIENCE = "100"
EXPECT_REPUTATION = "0"
EXPECT_TECH_TREE_URL = "GameData/ModuleManager.TechTree"

# The research nodes the rig needs beyond TECH_NODES, as (id, cost, parts, parents).
# Read 2026-10-21 on the operator PC from the automation instance's
# GameData/ModuleManager.ConfigCache (TechRequired) and ModuleManager.TechTree (cost,
# Parent entries); see the module docstring. `parts` is the rig's part on that node
# only (dot form), not the node's full list. Parent order is irrelevant; a parent may
# be any granted node (TECH_NODES or an earlier entry here). precisionEngineering is
# anyToUnlock = True, and both of its parents are granted.
RIG_EXTRA_TECH_NODES = (
    ("miniaturization", "90", ("evaScienceKit",), ("basicScience",)),
    ("precisionEngineering", "160", ("probeCoreOcto2.v2",), ("miniaturization", "electrics")),
    ("advExploration", "160", ("cargoContainer",), ("spaceExploration",)),
)

# Every part the rig carries (top-level PARTs plus the stored inventory parts) -> the
# node that unlocks it: the first five from TECH_NODES' automation-instance derivation,
# the last three from the 2026-10-21 ConfigCache read.
RIG_PART_TECH = {
    "liquidEngine2": "basicRocketry",
    "fuelTank": "advRocketry",
    "smallCargoContainer": "spaceExploration",
    "evaRepairKit": "spaceExploration",
    "FuelCell": "electrics",
    "probeCoreOcto2.v2": "precisionEngineering",
    "cargoContainer": "advExploration",
    "evaScienceKit": "miniaturization",
}


def _path(*parts: str) -> str:
    return os.path.join(_SAVES, *parts)


def _scenario(lines: List[str], name: str) -> Optional[Tuple[int, int]]:
    return _recipe._scenario_node(lines, name)


def _vessel_named(lines: List[str], name: str) -> Optional[Tuple[int, int]]:
    fs = _recipe.find_node(lines, "FLIGHTSTATE")
    if fs is None:
        return None
    for vessel in _recipe.child_nodes(lines, fs, "VESSEL"):
        if _recipe.get_value(lines, vessel, "name") == name:
            return vessel
    return None


def build(base_lines: List[str], donor_lines: List[str]) -> List[str]:
    """Return the spliced save. Pure over the two input line lists."""
    base = list(base_lines)

    # 1. FLIGHTSTATE: the donor's, the rig only, activeVessel re-indexed.
    donor_fs = _recipe.find_node(donor_lines, "FLIGHTSTATE")
    if donor_fs is None:
        raise SystemExit("donor has no FLIGHTSTATE node")
    fs = donor_lines[donor_fs[0]:donor_fs[1]]
    vessels = _recipe.child_nodes(fs, (0, len(fs)), "VESSEL")
    keep = [v for v in vessels if _recipe.get_value(fs, v, "name") == VESSEL_NAME]
    if len(keep) != 1:
        raise SystemExit("expected exactly one %r VESSEL in the donor, got %d"
                         % (VESSEL_NAME, len(keep)))
    for start, end in sorted((v for v in vessels if v not in keep), reverse=True):
        del fs[start:end]
    if len(_recipe.child_nodes(fs, (0, len(fs)), "VESSEL")) != 1:
        raise SystemExit("post-prune vessel count is not 1")
    if not _recipe.set_value(fs, (0, len(fs)), "activeVessel", "0"):
        raise SystemExit("could not re-index activeVessel")
    base_fs = _recipe.find_node(base, "FLIGHTSTATE")
    if base_fs is None:
        raise SystemExit("base has no FLIGHTSTATE node")
    if _recipe.child_nodes(base, base_fs, "VESSEL"):
        raise SystemExit("base FLIGHTSTATE unexpectedly carries a VESSEL")
    base[base_fs[0]:base_fs[1]] = fs

    # 3. ParsekScenario: the donor's node minus its dead MILESTONE_STATE children.
    donor_scn = _scenario(donor_lines, "ParsekScenario")
    if donor_scn is None:
        raise SystemExit("donor has no ParsekScenario SCENARIO node to copy")
    scn = donor_lines[donor_scn[0]:donor_scn[1]]
    for start, end in sorted(_recipe.child_nodes(scn, (0, len(scn)), "MILESTONE_STATE"),
                             reverse=True):
        del scn[start:end]
    if _scenario(base, "ParsekScenario") is not None:
        raise SystemExit("base unexpectedly carries a ParsekScenario node")
    anchor = _scenario(base, "ProgressTracking")
    if anchor is None:
        raise SystemExit("base has no ProgressTracking SCENARIO to anchor the insert")
    base[anchor[1]:anchor[1]] = scn

    # 4. Tech nodes after `start`; the science pool is left alone.
    rnd = _scenario(base, "ResearchAndDevelopment")
    if rnd is None:
        raise SystemExit("base has no ResearchAndDevelopment SCENARIO")
    techs = _recipe.child_nodes(base, rnd, "Tech")
    ids = [_recipe.get_value(base, t, "id") for t in techs]
    if ids != ["start"]:
        raise SystemExit("base R&D Tech nodes are %r, expected only start" % ids)
    block: List[str] = []
    for tech_id, cost, parts in _tech.TECH_NODES:
        block += _tech._tech_block(tech_id, cost, parts)
    for tech_id, cost, parts, _parents in RIG_EXTRA_TECH_NODES:
        block += _tech._tech_block(tech_id, cost, parts)
    base[techs[0][1]:techs[0][1]] = block

    # 5. Title and description.
    if not _recipe.set_top_value(base, "Title", TITLE):
        raise SystemExit("base has no Title line")
    desc = [i for i, l in enumerate(base) if l == "\tDescription = " + BASE_DESCRIPTION]
    if len(desc) != 1:
        raise SystemExit("base Description line not found")
    base[desc[0]] = "\tDescription = " + DESCRIPTION
    return base


def build_all() -> Dict[str, List[str]]:
    """The fixture's two text files, as line lists. Pure over the committed inputs."""
    base_lines = _recipe.read_lines(_path(BASE_NAME, "persistent.sfs"))
    donor_lines = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
    built = build(base_lines, donor_lines)
    meta = _recipe.build_loadmeta(
        _recipe.read_lines(_path(BASE_NAME, "persistent.loadmeta")), built)
    return {"persistent.sfs": built, "persistent.loadmeta": meta}


def _part_names(lines: List[str], ship: Tuple[int, int]) -> List[str]:
    """Every `name = ` of a PART node anywhere inside the vessel, stored parts included."""
    names = []
    for i in range(ship[0], ship[1] - 1):
        if lines[i].strip() == "PART" and lines[i + 1].strip() == "{":
            node = _recipe.find_node(lines, "PART", i, ship[1])
            if node is not None:
                names.append(_recipe.get_value(lines, node, "name"))
    return names


def verify(lines: List[str]) -> List[str]:
    """Post-conditions on the built (or committed) save; empty = every one holds."""
    problems: List[str] = []
    mode = next((l.split("=", 1)[1].strip() for l in lines if l.startswith("\tMode = ")),
                None)
    if mode != EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r" % (mode, EXPECT_MODE))
    if not any(l.strip() == "TechTreeUrl = %s" % EXPECT_TECH_TREE_URL for l in lines):
        problems.append("TechTreeUrl is not %s (TECH_NODES was derived against it)"
                        % EXPECT_TECH_TREE_URL)

    fs = _recipe.find_node(lines, "FLIGHTSTATE")
    vessels = _recipe.child_nodes(lines, fs, "VESSEL") if fs else []
    if len(vessels) != 1:
        return problems + ["expected exactly 1 VESSEL, found %d" % len(vessels)]
    ship = vessels[0]
    for key, want in (("name", VESSEL_NAME), ("sit", EXPECT_SITUATION),
                      ("type", VESSEL_TYPE), ("persistentId", VESSEL_PERSISTENT_ID)):
        if _recipe.get_value(lines, ship, key) != want:
            problems.append("vessel %s is %r, expected %r"
                            % (key, _recipe.get_value(lines, ship, key), want))
    if _recipe.get_value(lines, fs, "activeVessel") != "0":
        problems.append("activeVessel is not 0")
    donor = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
    donor_ship = _vessel_named(donor, VESSEL_NAME)
    if donor_ship is None or lines[ship[0]:ship[1]] != donor[donor_ship[0]:donor_ship[1]]:
        problems.append("the vessel is not the donor's VESSEL node byte for byte")
    if any(lines[i].strip().startswith("crew = ") for i in range(ship[0], ship[1])):
        problems.append("the rig carries a crew line; it must stay uncrewed")
    # The Logistics capabilities H38's header derives (L1 - L5).
    for text, why in (("name = ModuleEngines", "an engine (L1)"),
                      ("name = LiquidFuel", "a LiquidFuel resource (L2)"),
                      ("name = ModuleResourceConverter", "a BaseConverter (L4)")):
        if not _recipe.contains_line(lines, ship, text):
            problems.append("the rig has no %s" % why)
    inv = sum(1 for i in range(ship[0], ship[1])
              if lines[i].strip() == "name = ModuleInventoryPart")
    if inv != 2:
        problems.append("the rig carries %d ModuleInventoryPart, expected 2 (L5)" % inv)

    for scenario, key, want in (("Funding", "funds", EXPECT_FUNDS),
                                ("Reputation", "rep", EXPECT_REPUTATION),
                                ("ResearchAndDevelopment", "sci", EXPECT_SCIENCE)):
        node = _scenario(lines, scenario)
        if node is None or _recipe.get_value(lines, node, key) != want:
            problems.append("%s %s is %r, expected %r" % (
                scenario, key, _recipe.get_value(lines, node, key) if node else None, want))

    # Tech: start plus exactly the TECH_NODES closure and RIG_EXTRA_TECH_NODES,
    # Available; every rig part mapped and listed by its granted node; every declared
    # parent of an extra node granted.
    rnd = _scenario(lines, "ResearchAndDevelopment")
    available: Dict[str, set] = {}
    for tech in _recipe.child_nodes(lines, rnd, "Tech") if rnd else []:
        if _recipe.get_value(lines, tech, "state") != "Available":
            continue
        available[_recipe.get_value(lines, tech, "id")] = {
            lines[i].strip()[len("part = "):] for i in range(tech[0], tech[1])
            if lines[i].strip().startswith("part = ")}
    expected_ids = (["start"] + [t[0] for t in _tech.TECH_NODES]
                    + [t[0] for t in RIG_EXTRA_TECH_NODES])
    if sorted(available) != sorted(expected_ids):
        problems.append("Available tech nodes %r != expected %r"
                        % (sorted(available), sorted(expected_ids)))
    for tech_id, _cost, _parts, parents in RIG_EXTRA_TECH_NODES:
        for parent in parents:
            if parent not in available:
                problems.append("tech %s is granted but its parent %s is not"
                                % (tech_id, parent))
    rig_parts = set(_part_names(lines, ship))
    unmapped = sorted(rig_parts - set(RIG_PART_TECH))
    if unmapped:
        problems.append("rig parts with no RIG_PART_TECH node: %r" % unmapped)
    for part_name, tech_id in sorted(RIG_PART_TECH.items()):
        if part_name not in rig_parts:
            problems.append("RIG_PART_TECH names %s, which the rig does not carry" % part_name)
        if part_name not in available.get(tech_id, ()):
            problems.append("%s is not unlocked (tech %s Available and listing it)"
                            % (part_name, tech_id))

    scn = _scenario(lines, "ParsekScenario")
    if scn is None:
        problems.append("no ParsekScenario SCENARIO node: a FLIGHT-route run would "
                        "persist nothing (CL-1 flight 1)")
    else:
        for child in ("MILESTONE_STATE", "RECORDING_TREE", "GROUP_HIERARCHY",
                      "LEDGER", "REWIND_POINT"):
            if _recipe.child_nodes(lines, scn, child):
                problems.append("ParsekScenario still carries %s" % child)
    if not any(l.strip() == "Title = %s" % TITLE for l in lines):
        problems.append("Title is not %r" % TITLE)
    return problems


def committed_problems() -> List[str]:
    """Differences between a fresh build and the committed fixture (empty = OK)."""
    want = build_all()
    target = _path(TARGET_NAME)
    problems: List[str] = []
    for rel in TEXT_FILES:
        path = os.path.join(target, rel)
        if not os.path.isfile(path):
            problems.append("missing %s" % rel)
            continue
        if _recipe.read_lines(path) != want[rel]:
            problems.append("drift in %s" % rel)
        with open(path, "rb") as fh:
            if b"\r\n" not in fh.read():
                problems.append("%s is not CRLF" % rel)
    base_addons = _path(BASE_NAME, "AddOns")
    ours_addons = os.path.join(target, "AddOns")
    expected = set(TEXT_FILES)
    for root, _dirs, names in os.walk(base_addons):
        for name in names:
            src = os.path.join(root, name)
            rel = os.path.relpath(src, base_addons).replace(os.sep, "/")
            expected.add("AddOns/" + rel)
            dst = os.path.join(ours_addons, rel)
            if not os.path.isfile(dst):
                problems.append("missing AddOns/%s" % rel)
                continue
            with open(src, "rb") as a, open(dst, "rb") as b:
                if a.read() != b.read():
                    problems.append("drift in AddOns/%s" % rel)
    have = set()
    for root, _dirs, names in os.walk(target):
        for name in names:
            have.add(os.path.relpath(os.path.join(root, name), target).replace(os.sep, "/"))
    for extra in sorted(have - expected):
        problems.append("unexpected file %s" % extra)
    if os.path.isfile(os.path.join(target, "persistent.sfs")):
        problems += verify(_recipe.read_lines(os.path.join(target, "persistent.sfs")))
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
        print("%s: %s" % (TARGET_NAME, "OK" if not problems else "FAIL"))
        return 0 if not problems else 1
    built = build_all()
    problems = verify(built["persistent.sfs"])
    if problems:
        for p in problems:
            print("POSTCONDITION: " + p)
        return 1
    target = _path(TARGET_NAME)
    if os.path.isdir(target):
        shutil.rmtree(target)
    shutil.copytree(_path(BASE_NAME, "AddOns"), os.path.join(target, "AddOns"))
    for rel in TEXT_FILES:
        _recipe.write_lines(os.path.join(target, rel), built[rel])
    print("wrote " + target)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
