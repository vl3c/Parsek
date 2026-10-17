#!/usr/bin/env python3
"""Build the `lab-lko-science` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. The science-lab (ModuleScienceLab / ModuleScienceConverter) lanes
need the `LABX Orbital Lab` ORBITING in LKO with the scientist IN the lab, in a game
mode where the lab actually stores data and pays science. In SANDBOX the lab stores
no data and pays nothing (lab value needs an R&D subject; SANDBOX has no
ResearchAndDevelopment), so the harvested LABX-1 save (`lab-lko-sandbox`, SANDBOX)
cannot host those lanes by itself. This tool stamps its orbiting vessel into
`fresh-science` (SCIENCE_SANDBOX) and researches every tech node the craft's parts
need, so both stock's part availability and the lab's science value work.

THE RECIPE IS `career-pad-craft`'s (build_career_pad_craft.py), WITH THESE EDITS
against the `fresh-science` save, plus a loadmeta restamp (vessel count + UT):

  1. `fresh-science`'s empty `FLIGHTSTATE` is replaced by `lab-lko-sandbox`'s, with
     the donor's one non-`Ship` VESSEL dropped (the stock asteroid `Ast. VPA-167`,
     which `fresh-science`'s `DiscoverableObjects` never registered) and
     `activeVessel` re-indexed onto the surviving `LABX Orbital Lab` (1 -> 0). The
     vessel node is the donor's BYTE FOR BYTE: same pid, persistentIds, orbit,
     resources, and crew lines (Valentina in mk1pod.v2, Bob in Large.Crewed.Lab).
  2. The `ROSTER` rows for Valentina Kerman and Bob Kerman are replaced by the
     donor's, the `state = Assigned` rows KSP itself wrote alongside this vessel.
  3. The donor's `SCENARIO{name=ParsekScenario}` node is copied in WITH its
     `MILESTONE_STATE` children excised (the `duna-park-probe` precedent): they
     index the run's `Parsek/GameState/milestones.pgsm` sidecar, which no fixture
     ships, so they are dead references. `scene` and `gameStateEventCount` stay;
     the node itself is LOAD-BEARING (a FLIGHT-route load needs it, CL-1 flight 1).
  4. The donor's `SCENARIO{name=ProgressTracking}` replaces the base's empty one.
     It holds only THIS flight's milestones (FirstLaunch, the altitude / speed /
     distance records, ReachedSpace, Kerbin Orbit, crewed by Valentina and Bob), so
     loading the fixture does not award "reached orbit" a second time on the first
     situation check and move the science pool under a lab lane.
  5. The ResearchAndDevelopment node gains one `Tech` node per entry of
     TECH_NODES, inserted after `start`, each `state = Available` with its tree cost
     and the full part list of that node (in SCIENCE_SANDBOX a researched node lists
     every part it unlocks, as `fresh-science`'s own `start` node does). The SCIENCE
     POOL IS LEFT AT `fresh-science`'s 100: the nodes are granted, not bought, so no
     ledger or pool consumer sees a spend that never happened.
  6. `Title` becomes `lab-lko-science (SCIENCE_SANDBOX)`.

THE TECH TREE IS THE AUTOMATION INSTANCE'S, NOT STOCK'S. `fresh-science`'s
`TechTreeUrl` is `GameData/ModuleManager.TechTree`, and the provisioned
`automation/stock-minimal` instance runs CommunityTechTree + ProbesBeforeCrew, which
move parts between nodes. So the Mk1 pod sits in `simpleCommandModules` (not
`start`), the lab in `specializedCommandModules` (not stock `advExploration`), the
barometer in `stability`, the X200-16 in `advFuelSystems`, the Z-400 in `electrics`,
the TD-25 in `specializedConstruction`. TECH_NODES below was DERIVED ONCE from that
instance (2026-10-17) and is embedded so the drift gate runs anywhere, CI included:
    needed  = the effective TechRequired (GameData/ModuleManager.ConfigCache) of every
              part of the LABX Orbital Lab craft, the booster's included, so a lab
              lane can also launch a second lab from this save;
    closure = needed plus every ancestor via RDNode Parent entries
              (GameData/ModuleManager.TechTree; for `anyToUnlock` nodes ALL parents,
              a superset), minus `start` (already present);
    order   = TechTree order; cost = the RDNode cost; parts = every ConfigCache PART
              whose TechRequired is that node (dot-form names), in ConfigCache order.
29 nodes, 4668 science of tree cost. VESSEL_PART_TECH pins the per-part node so
verify() can prove every craft part is unlocked. If the instance's tree pins move
(a CTT or PBC upgrade), re-derive and re-run this builder.

NOT COPIED: the donor's `Ships/` (the craft arrives through the shared-ships overlay,
see harness/fixtures/shared-ships.toml) and every other donor SCENARIO (CommNet,
DeployedScience, Sentinel, DiscoverableObjects, ...): `fresh-science`'s own stay.
`AddOns/` comes from the base, as for `career-pad-craft`.

Usage:
    python harness/tools/build_lab_lko_science.py            # write the fixture
    python harness/tools/build_lab_lko_science.py --check    # re-derive, compare

`--check` rebuilds from the committed inputs and compares the bytes with the committed
fixture; `harness/lib/test_lab_lko_science.py` runs it in-process, so a change to
`fresh-science`, `lab-lko-sandbox` or the shared recipe reds in the suite instead of
drifting into a flight.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import filecmp
import os
import shutil
import sys
from typing import Dict, List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import build_career_pad_craft as _recipe  # noqa: E402

_SAVES = os.path.join(os.path.dirname(_HERE), "fixtures", "saves")

BASE_NAME = "fresh-science"
DONOR_NAME = "lab-lko-sandbox"
TARGET_NAME = "lab-lko-science"
VESSEL_NAME = "LABX Orbital Lab"
VESSEL_PERSISTENT_ID = "2200110055"
CREW = (("Valentina Kerman", "mk1pod.v2", "Pilot"),
        ("Bob Kerman", "Large.Crewed.Lab", "Scientist"))
TITLE = "%s (SCIENCE_SANDBOX)" % TARGET_NAME
WRITTEN_FILES = ("persistent.sfs", "persistent.loadmeta")

EXPECT_MODE = "SCIENCE_SANDBOX"
EXPECT_SITUATION = "ORBITING"
EXPECT_SCIENCE = "100"
EXPECT_TECH_TREE_URL = "GameData/ModuleManager.TechTree"

# Effective TechRequired (automation instance, CTT + ProbesBeforeCrew) of every part
# the LABX Orbital Lab craft carries, booster included.
VESSEL_PART_TECH = {
    "mk1pod.v2": "simpleCommandModules",
    "sensorThermometer": "start",
    "sensorBarometer": "stability",
    "science.module": "basicScience",
    "GooExperiment": "start",
    "Large.Crewed.Lab": "specializedCommandModules",
    "asasmodule1-2": "specializedControl",
    "Rockomax16.BW": "advFuelSystems",
    "ksp.r.largeBatteryPack": "electrics",
    "rtg": "experimentalElectrics",
    "liquidEngine2-2.v2": "heavyRocketry",
    "Decoupler.2": "specializedConstruction",
    "Rockomax64.BW": "advFuelSystems",
    "liquidEngineMainsail.v2": "heavierRocketry",
}

# (id, cost, parts) in TechTree order. See the module docstring for the derivation.
TECH_NODES = (
    ('basicRocketry', '5', (
        'liquidEngine2', 'liquidEngine2.v2', 'solidBooster.v2', 'Shrimp',
        'fuelTankSmallFlat',
    )),
    ('engineering101', '5', (
        'Decoupler.1', 'adapterSmallMiniShort', 'trussPiece1x',
    )),
    ('survivability', '15', (
        'HeatShield0', 'probeCoreOcto.v2', 'solarPanels5', 'batteryBankMini',
        'radPanelSm', 'longAntenna', 'miniLandingLeg', 'parachuteSingle',
    )),
    ('stability', '18', (
        'noseCone', 'standardNoseCone', 'winglet', 'sasModule', 'sensorBarometer',
        'adapterSmallMiniTall', 'radialDecoupler', 'Size1to0ServiceModule',
    )),
    ('generalRocketry', '20', (
        'Decoupler.0', 'liquidEngine', 'liquidEngine.v2', 'solidBooster1-1',
        'fuelTankSmall',
    )),
    ('basicScience', '45', (
        'probeCoreHex.v2', 'science.module', 'ScienceBox', 'radPanelEdge',
        'radPanelLg', 'HighGainAntenna5', 'HighGainAntenna5.v2',
    )),
    ('flightControl', '45', (
        'mumech.MJ2.AR202', 'R8winglet', 'winglet3', 'advSasModule', 'rcsTankMini',
        'linearRcs', 'RCSLinearSmall',
    )),
    ('advRocketry', '45', (
        'liquidEngine3.v2', 'radialLiquidEngine1-2', 'fuelTank',
        'fireworksLauncherBig', 'fireworksLauncherSmall', 'Size1p5.Size1.Adapter.02',
        'Size1p5.Tank.01',
    )),
    ('generalConstruction', '45', (
        'pointyNoseConeA', 'pointyNoseConeB', 'fairingSize1', 'strutConnector',
        'Separator.1', 'Magnetometer', 'Mk1FuselageStructural', 'flagPartSize1',
        'launchClamp1', 'ServiceBay.125.v2', 'EnginePlate5',
    )),
    ('propulsionSystems', '90', (
        'Separator.0', 'liquidEngineMini.v2', 'microEngine.v2', 'externalTankCapsule',
        'miniFuelTank',
    )),
    ('spaceExploration', '90', (
        'smallCargoContainer', 'evaRepairKit', 'ConformalStorageUnit', 'roverBody.v2',
        'telescopicLadder', 'groundLight1', 'groundLight2', 'roverWheel2',
        'DeployedCentralStation',
    )),
    ('advFlightControl', '90', (
        'mumech.MJ2.AR202.features1', 'radialRCSTank', 'RCSBlock.v2',
        'RCSblock.01.small', 'monopropMiniSphere',
    )),
    ('electrics', '90', (
        'solarPanels4', 'solarPanels3', 'ksp.r.largeBatteryPack', 'LgRadialSolarPanel',
        'FuelCell', 'foldingRadSmall', 'domeLight1', 'spotLight3', 'spotLight1',
        'spotLight1.v2', 'spotLight2', 'spotLight2.v2', 'DeployedSolarPanel',
    )),
    ('heavyRocketry', '90', (
        'liquidEngine2-2.v2', 'engineLargeSkipper.v2', 'MassiveBooster', 'Rockomax8BW',
        'LiquidEngineLV-TX87',
    )),
    ('fuelSystems', '90', (
        'fuelLine', 'ReleaseValve', 'RCSFuelTank', 'fuelTank.long',
        'Size1p5.Size0.Adapter.01', 'Size1p5.Size1.Adapter.01', 'Size1p5.Tank.02',
        'Size1p5.Tank.03',
    )),
    ('advConstruction', '90', (
        'rocketNoseCone.v3', 'stationHub', 'radialDecoupler2', 'dockingPort3',
        'flagPartSize0', 'largeAdapter', 'largeAdapter2', 'stackBiCoupler.v2',
        'Size.1.5.Cone', 'Decoupler.1p5', 'Separator.1p5', 'Size1p5.Strut.Decoupler',
        'fairingSize1p5', 'ServiceModule18', 'Tube1', 'Tube1p5', 'flagPartSize1p5',
    )),
    ('commandModules', '160', (
        'landerCabinSmall', 'mk2LanderCabin', 'kv2Pod', 'Mk2Pod', 'MEMLander',
    )),
    ('heavierRocketry', '160', (
        'liquidEngineMainsail.v2', 'Size2LFB', 'Size2LFB.v2', 'Thoroughbred',
        'LiquidEngineLV-T91', 'LiquidEngineRE-I2', 'LiquidEngineRK-7', 'Pollux',
    )),
    ('specializedControl', '160', (
        'avionicsNoseCone', 'asasmodule1-2', 'rcsTankRadialLong', 'vernierEngine',
    )),
    ('advFuelSystems', '160', (
        'adapterSize2-Size1', 'adapterSize2-Size1Slant', 'Rockomax16.BW',
        'Rockomax32.BW', 'Rockomax64.BW', 'Size1p5.Monoprop',
        'Size1p5.Size2.Adapter.01', 'Size1p5.Tank.04', 'Size1p5.Tank.05',
    )),
    ('advElectrics', '160', (
        'solarPanels2', 'solarPanels1', 'batteryBank', 'mediumDishAntenna',
        'stripLight1', 'RelayAntenna5',
    )),
    ('specializedConstruction', '160', (
        'rocketNoseCone.v2', 'fairingSize2', 'groundAnchor', 'crewCabin',
        'Decoupler.2', 'Separator.2', 'structuralIBeam3', 'structuralIBeam1',
        'structuralPanel1', 'trussAdapter', 'trussPiece3x', 'radialDecoupler1-2',
        'dockingPort2', 'flagPartSize2', 'stackPoint1', 'ServiceBay.250.v2',
        'stackQuadCoupler', 'stackTriCoupler.v2', 'EnginePlate1p5', 'ServiceModule25',
        'EquiTriangle0', 'EquiTriangle1', 'Panel0', 'Panel1', 'Triangle0', 'Triangle1',
        'Tube2',
    )),
    ('largeElectrics', '300', (
        'solarPanelOX10C', 'solarPanelSP10C', 'solarPanelOX10L', 'solarPanelSP10L',
        'largeSolarPanel', 'HighGainAntenna',
    )),
    ('specializedElectrics', '550', (
        'batteryBankLarge', 'FuelCellArray', 'RelayAntenna50',
    )),
    ('experimentalElectrics', '1000', (
        'rtg', 'commDish', 'DeployedRTG',
    )),
    ('enhancedSurvivability', '45', (
        'HeatShield1', 'radialDrogue', 'parachuteRadial', 'HeatShield1p5',
    )),
    ('simpleCommandModules', '90', (
        'evaJetpack', 'evaChute', 'mk1pod.v2', 'ladder1', 'kv1Pod',
    )),
    ('heavyCommandModules', '300', (
        'cupola', 'mk1-3pod', 'mk2LanderCabin.v2', 'kv3Pod',
    )),
    ('specializedCommandModules', '550', (
        'Large.Crewed.Lab',
    )),
)


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


def _tech_block(tech_id: str, cost: str, parts) -> List[str]:
    out = ["\t\tTech", "\t\t{", "\t\t\tid = %s" % tech_id, "\t\t\tstate = Available",
           "\t\t\tcost = %s" % cost]
    out += ["\t\t\tpart = %s" % p for p in parts]
    out.append("\t\t}")
    return out


def build(base_lines: List[str], donor_lines: List[str]) -> List[str]:
    """The spliced save. Pure over the two input line lists."""
    base = list(base_lines)

    # 1. FLIGHTSTATE: the donor's, Ship vessels only, activeVessel re-indexed.
    donor_fs = _recipe.find_node(donor_lines, "FLIGHTSTATE")
    if donor_fs is None:
        raise SystemExit("donor has no FLIGHTSTATE node")
    fs = donor_lines[donor_fs[0]:donor_fs[1]]
    vessels = _recipe.child_nodes(fs, (0, len(fs)), "VESSEL")
    drop = [v for v in vessels if _recipe.get_value(fs, v, "type") != "Ship"]
    if len(vessels) - len(drop) != 1:
        raise SystemExit("expected exactly one type=Ship VESSEL in the donor, got %d"
                         % (len(vessels) - len(drop)))
    for start, end in sorted(drop, reverse=True):
        del fs[start:end]
    if not _recipe.set_value(fs, (0, len(fs)), "activeVessel", "0"):
        raise SystemExit("could not re-index activeVessel")
    base_fs = _recipe.find_node(base, "FLIGHTSTATE")
    if base_fs is None:
        raise SystemExit("base has no FLIGHTSTATE node")
    base[base_fs[0]:base_fs[1]] = fs

    # 2. ROSTER: the donor's Assigned rows for both crew.
    for crew_name, _part, _trait in CREW:
        donor_roster = _recipe.find_node(donor_lines, "ROSTER")
        base_roster = _recipe.find_node(base, "ROSTER")
        donor_kerbal = _recipe._kerbal_named(donor_lines, donor_roster, crew_name)
        base_kerbal = _recipe._kerbal_named(base, base_roster, crew_name)
        if donor_kerbal is None or base_kerbal is None:
            raise SystemExit("roster row for %s missing in donor or base" % crew_name)
        base[base_kerbal[0]:base_kerbal[1]] = \
            donor_lines[donor_kerbal[0]:donor_kerbal[1]]

    # 4. ProgressTracking: the donor's (this flight's milestones) over the base's.
    donor_pt = _scenario(donor_lines, "ProgressTracking")
    base_pt = _scenario(base, "ProgressTracking")
    if donor_pt is None or base_pt is None:
        raise SystemExit("a ProgressTracking SCENARIO is missing")
    base[base_pt[0]:base_pt[1]] = donor_lines[donor_pt[0]:donor_pt[1]]

    # 3. ParsekScenario: the donor's node minus its dead MILESTONE_STATE children.
    donor_scn = _scenario(donor_lines, "ParsekScenario")
    if donor_scn is None:
        raise SystemExit("donor has no ParsekScenario SCENARIO node to copy")
    scn = donor_lines[donor_scn[0]:donor_scn[1]]
    for start, end in sorted(_recipe.child_nodes(scn, (0, len(scn)),
                                                 "MILESTONE_STATE"), reverse=True):
        del scn[start:end]
    if _scenario(base, "ParsekScenario") is not None:
        raise SystemExit("base unexpectedly carries a ParsekScenario node")
    anchor = _scenario(base, "ProgressTracking")
    base[anchor[1]:anchor[1]] = scn

    # 5. Tech nodes after `start`.
    rnd = _scenario(base, "ResearchAndDevelopment")
    if rnd is None:
        raise SystemExit("base has no ResearchAndDevelopment SCENARIO")
    techs = _recipe.child_nodes(base, rnd, "Tech")
    ids = [_recipe.get_value(base, t, "id") for t in techs]
    if ids != ["start"]:
        raise SystemExit("base R&D Tech nodes are %r, expected only start" % ids)
    block: List[str] = []
    for tech_id, cost, parts in TECH_NODES:
        block += _tech_block(tech_id, cost, parts)
    base[techs[0][1]:techs[0][1]] = block

    # 6. Title.
    _recipe.set_top_value(base, "Title", TITLE)
    return base


def build_all() -> Dict[str, List[str]]:
    """The fixture's two text files, as line lists. Pure over the committed inputs."""
    base_lines = _recipe.read_lines(_path(BASE_NAME, "persistent.sfs"))
    donor_lines = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
    built = build(base_lines, donor_lines)
    meta = _recipe.build_loadmeta(
        _recipe.read_lines(_path(BASE_NAME, "persistent.loadmeta")), built)
    return {"persistent.sfs": built, "persistent.loadmeta": meta}


def verify(lines: List[str]) -> List[str]:
    """Post-conditions on the built (or committed) save; empty = every one holds."""
    problems: List[str] = []
    mode = next((l.split("=", 1)[1].strip() for l in lines
                 if l.startswith("\tMode = ")), None)
    if mode != EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r" % (mode, EXPECT_MODE))
    if not any(l.strip() == "TechTreeUrl = %s" % EXPECT_TECH_TREE_URL for l in lines):
        problems.append("TechTreeUrl is not %s (TECH_NODES was derived against it)"
                        % EXPECT_TECH_TREE_URL)

    fs = _recipe.find_node(lines, "FLIGHTSTATE")
    vessels = _recipe.child_nodes(lines, fs, "VESSEL") if fs else []
    if len(vessels) != 1:
        problems.append("expected exactly 1 VESSEL, found %d" % len(vessels))
        return problems
    ship = vessels[0]
    for key, want in (("name", VESSEL_NAME), ("sit", EXPECT_SITUATION),
                      ("type", "Ship"), ("persistentId", VESSEL_PERSISTENT_ID)):
        if _recipe.get_value(lines, ship, key) != want:
            problems.append("vessel %s is %r, expected %r"
                            % (key, _recipe.get_value(lines, ship, key), want))
    if _recipe.get_value(lines, fs, "activeVessel") != "0":
        problems.append("activeVessel is not 0")
    donor = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
    donor_ship = _vessel_named(donor, VESSEL_NAME)
    if donor_ship is None or (lines[ship[0]:ship[1]]
                              != donor[donor_ship[0]:donor_ship[1]]):
        problems.append("the vessel is not the donor's VESSEL node byte for byte")

    # Crew seating: Valentina in the pod, Bob in the lab.
    seated = {}
    names_on_vessel = set()
    for part in _recipe.child_nodes(lines, ship, "PART"):
        pname = _recipe.get_value(lines, part, "name")
        names_on_vessel.add(pname)
        for i in range(part[0], part[1]):
            s = lines[i].strip()
            if s.startswith("crew = "):
                seated[s[len("crew = "):]] = pname
    roster = _recipe.find_node(lines, "ROSTER")
    for crew_name, part_name, trait in CREW:
        if seated.get(crew_name) != part_name:
            problems.append("%s is seated in %r, expected %s"
                            % (crew_name, seated.get(crew_name), part_name))
        kerbal = _recipe._kerbal_named(lines, roster, crew_name) if roster else None
        if kerbal is None:
            problems.append("roster has no %s" % crew_name)
            continue
        for key, want in (("state", "Assigned"), ("type", "Crew"), ("trait", trait)):
            if _recipe.get_value(lines, kerbal, key) != want:
                problems.append("roster %s %s is %r, expected %r"
                                % (crew_name, key,
                                   _recipe.get_value(lines, kerbal, key), want))

    # Every craft part is unlocked: its node is Available and lists it.
    rnd = _scenario(lines, "ResearchAndDevelopment")
    available: Dict[str, set] = {}
    if rnd is None:
        problems.append("no ResearchAndDevelopment SCENARIO")
    else:
        if _recipe.get_value(lines, rnd, "sci") != EXPECT_SCIENCE:
            problems.append("R&D sci is %r, expected %r (granted, not bought)"
                            % (_recipe.get_value(lines, rnd, "sci"), EXPECT_SCIENCE))
        for tech in _recipe.child_nodes(lines, rnd, "Tech"):
            if _recipe.get_value(lines, tech, "state") != "Available":
                continue
            listed = {lines[i].strip()[len("part = "):]
                      for i in range(tech[0], tech[1])
                      if lines[i].strip().startswith("part = ")}
            available[_recipe.get_value(lines, tech, "id")] = listed
    missing = sorted(names_on_vessel - set(VESSEL_PART_TECH))
    if missing:
        problems.append("vessel parts with no pinned tech node: %r" % missing)
    for part_name, tech_id in sorted(VESSEL_PART_TECH.items()):
        if part_name not in available.get(tech_id, ()):
            problems.append("%s is not unlocked (tech %s Available and listing it)"
                            % (part_name, tech_id))
    expected_ids = ["start"] + [t[0] for t in TECH_NODES]
    if rnd is not None and sorted(available) != sorted(expected_ids):
        problems.append("Available tech nodes %r != expected %r"
                        % (sorted(available), sorted(expected_ids)))

    # Parsek state: the inert node, no children carrying run state.
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


def _encoded(lines: List[str]) -> bytes:
    return "\r\n".join(lines).encode("utf-8")


def _same_tree(cmp: filecmp.dircmp) -> bool:
    if cmp.left_only or cmp.right_only or cmp.funny_files:
        return False
    _, mismatch, errors = filecmp.cmpfiles(cmp.left, cmp.right, cmp.common_files,
                                           shallow=False)
    if mismatch or errors:
        return False
    return all(_same_tree(sub) for sub in cmp.subdirs.values())


def check() -> List[str]:
    """Differences between a fresh build and the committed fixture (empty = OK)."""
    target = _path(TARGET_NAME)
    problems: List[str] = []
    built = build_all()
    for name in WRITTEN_FILES:
        path = os.path.join(target, name)
        if not os.path.isfile(path):
            problems.append("missing %s" % path)
            continue
        with open(path, "rb") as fh:
            if fh.read() != _encoded(built[name]):
                problems.append("%s differs from a fresh build" % path)
    if not _same_tree(filecmp.dircmp(_path(BASE_NAME, "AddOns"),
                                     os.path.join(target, "AddOns"))):
        problems.append("AddOns/ differs from %s's" % BASE_NAME)
    expected = sorted(WRITTEN_FILES + ("AddOns",))
    actual = sorted(os.listdir(target)) if os.path.isdir(target) else []
    if actual != expected:
        problems.append("fixture entries are %r, expected %r" % (actual, expected))
    problems += verify(built["persistent.sfs"])
    return problems


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="re-derive and compare against the committed fixture")
    args = parser.parse_args(argv)

    if args.check:
        problems = check()
        for p in problems:
            print("FAIL: %s" % p)
        print("%s: %s" % (TARGET_NAME,
                          "OK" if not problems else "%d problem(s)" % len(problems)))
        return 1 if problems else 0

    built = build_all()
    problems = verify(built["persistent.sfs"])
    if problems:
        for p in problems:
            print("FAIL: %s" % p)
        return 1
    target = _path(TARGET_NAME)
    os.makedirs(target, exist_ok=True)
    for name in WRITTEN_FILES:
        _recipe.write_lines(os.path.join(target, name), built[name])
    if not os.path.isdir(os.path.join(target, "AddOns")):
        shutil.copytree(_path(BASE_NAME, "AddOns"), os.path.join(target, "AddOns"))
    print("OK: wrote %s (base=%s donor=%s)" % (target, BASE_NAME, DONOR_NAME))
    return 0


if __name__ == "__main__":
    sys.exit(main())
