#!/usr/bin/env python3
"""Build the committed `lab-pad-career` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. LAB-12 needs the science lab's transmit in a CAREER game with a
science-taking strategy active: stock's `Strategies.Effects.CurrencyConverter` only runs in
career, and every lab fixture so far is SCIENCE_SANDBOX. The two inputs are committed and
already proven: `strategy-career` (the L3 strategy lanes' career: funds 500,000, science
100, reputation 25, all facilities at level 0, an EMPTY `STRATEGIES` node, no vessel, no
ParsekScenario node) and `lab-pad-seeded` (the LABX Orbital Lab PRELAUNCH on the pad, its
lab holding 12.5 science / 204 data, Valentina and Bob aboard, the craft's tech
researched). The splice between them is mechanical, in `build_career_pad_craft`'s shape.

WHAT IT SPLICES, against the `strategy-career` SAVE:
  1. Its empty `FLIGHTSTATE` is replaced by `lab-pad-seeded`'s, verbatim: the save clock
     (UT 355.83), `activeVessel = 0` and the one VESSEL, byte for byte (persistentId
     2200110055, the seeded lab, the Communotron 16-S).
  2. The base `ROSTER`'s Valentina and Bob rows are replaced by the donor's `Assigned`
     rows, which KSP wrote alongside this vessel.
  3. The donor's inert `SCENARIO{name=ParsekScenario}` node is inserted after
     ProgressTracking (LOAD-BEARING: a focusable save routes the seam's LoadGame to FLIGHT,
     where the module is never created without it; CL-1 flight 1).
  4. The base `ResearchAndDevelopment` SCENARIO's Tech children (only `start`) are
     replaced by the donor's (`start` plus the 29 nodes the craft needs), and `sci` is
     seeded to 400.

THE SCIENCE SEED. Patents Licensing (`PatentsLicensingCfg`, the stock strategy whose second
CurrencyConverter takes a share of ScienceTransmission / VesselRecovery / Progression
science and pays funds for it) costs `initialCostScience` = lerp(135, 2700, factor); the
seam's `KscAction activate-strategy` writes stock's slider default, factor 0.05, so the
setup cost is 263.25 science. Seeded at 400, the pool after activation is 136.75, and
stock's `Strategy.CanBeActivated` sees enough. Funds (500,000) and reputation (25) are the
base's: Patents costs neither, and its required reputation band is -500 .. 750.

THE STRATEGY SLOT. Administration is at level 0 in the base, and stock's
`GameVariables.GetActiveStrategyLimit(0)` is 1 (decompiled 1.12.5), with a commitment
range of 0.25 (`GetStrategyCommitRange`), above the 0.05 factor: one slot, free.

WHAT IS NOT TOUCHED. Every other career surface is `strategy-career`'s: Mode CAREER, the
PARAMETERS block (CommNet on, ground stations on; the KSC is next to the pad), funds,
reputation, facilities, contracts, the empty STRATEGIES node, ProgressTracking (a fresh
career: the pad lab triggers no milestone without a launch), the applicant. The loadmeta is
the base's with the vessel count, UT and science restamped.

`--check` re-derives and compares both files against the committed fixture;
`harness/lib/test_lab_pad_career.py` runs it in-process.

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

_SAVES = os.path.join(os.path.dirname(_HERE), "fixtures", "saves")

BASE_NAME = "strategy-career"
DONOR_NAME = "lab-pad-seeded"
TARGET_NAME = "lab-pad-career"
VESSEL_NAME = "LABX Orbital Lab"
VESSEL_PERSISTENT_ID = "2200110055"
CREW = ("Valentina Kerman", "Bob Kerman")
TITLE = "lab-pad-career (CAREER)"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: strategy-career with the LABX Orbital "
               "Lab PRELAUNCH on the pad, its lab holding 12.5 science, the craft's tech "
               "researched, science 400 for a Patents Licensing activation).")
BASE_DESCRIPTION = "Parsek autotest fixture (synthetic, file-constructed)."

SCIENCE_SEED = "400"
# Stock Patents Licensing at the seam's factor 0.05 (Strategies.cfg PatentsLicensingCfg).
PATENTS_SETUP_SCIENCE = 135.0 + 0.05 * (2700.0 - 135.0)
EXPECT_MODE = "CAREER"
EXPECT_FUNDS = "500000"
EXPECT_REPUTATION = "25"
EXPECT_SITUATION = "PRELAUNCH"
TEXT_FILES = ("persistent.sfs", "persistent.loadmeta")


def _path(name: str, rel: str) -> str:
    return os.path.join(_SAVES, name, rel)


def _scenario(lines: List[str], name: str) -> Optional[Tuple[int, int]]:
    return _recipe._scenario_node(lines, name)


def _vessel_named(lines: List[str], name: str) -> Optional[Tuple[int, int]]:
    fs = _recipe.find_node(lines, "FLIGHTSTATE")
    if fs is None:
        return None
    for v in _recipe.child_nodes(lines, fs, "VESSEL"):
        if _recipe.get_value(lines, v, "name") == name:
            return v
    return None


def build(base_lines: List[str], donor_lines: List[str]) -> List[str]:
    """Return the spliced save. Pure over the two input line lists."""
    base = list(base_lines)

    # 1. FLIGHTSTATE, verbatim.
    donor_fs = _recipe.find_node(donor_lines, "FLIGHTSTATE")
    base_fs = _recipe.find_node(base, "FLIGHTSTATE")
    if donor_fs is None or base_fs is None:
        raise SystemExit("a FLIGHTSTATE node is missing")
    if _recipe.child_nodes(base, base_fs, "VESSEL"):
        raise SystemExit("base FLIGHTSTATE unexpectedly carries a VESSEL")
    fs = donor_lines[donor_fs[0]:donor_fs[1]]
    if len(_recipe.child_nodes(fs, (0, len(fs)), "VESSEL")) != 1:
        raise SystemExit("donor FLIGHTSTATE must carry exactly one VESSEL")
    base[base_fs[0]:base_fs[1]] = fs

    # 2. Roster rows for the crew aboard.
    for crew_name in CREW:
        base_roster = _recipe.find_node(base, "ROSTER")
        donor_roster = _recipe.find_node(donor_lines, "ROSTER")
        theirs = _recipe._kerbal_named(donor_lines, donor_roster, crew_name)
        ours = _recipe._kerbal_named(base, base_roster, crew_name)
        if theirs is None or ours is None:
            raise SystemExit("roster row for %s missing" % crew_name)
        base[ours[0]:ours[1]] = donor_lines[theirs[0]:theirs[1]]

    # 3. The inert ParsekScenario node.
    donor_scn = _scenario(donor_lines, "ParsekScenario")
    if donor_scn is None:
        raise SystemExit("donor has no ParsekScenario SCENARIO node")
    if _scenario(base, "ParsekScenario") is not None:
        raise SystemExit("base unexpectedly carries a ParsekScenario node")
    anchor = _scenario(base, "ProgressTracking")
    if anchor is None:
        raise SystemExit("base has no ProgressTracking SCENARIO to anchor the insert")
    base[anchor[1]:anchor[1]] = donor_lines[donor_scn[0]:donor_scn[1]]

    # 4. The craft's tech, and the science seed.
    rnd = _scenario(base, "ResearchAndDevelopment")
    donor_rnd = _scenario(donor_lines, "ResearchAndDevelopment")
    if rnd is None or donor_rnd is None:
        raise SystemExit("a ResearchAndDevelopment SCENARIO is missing")
    techs = _recipe.child_nodes(base, rnd, "Tech")
    if [_recipe.get_value(base, t, "id") for t in techs] != ["start"]:
        raise SystemExit("base R&D must carry only the start node")
    donor_techs = _recipe.child_nodes(donor_lines, donor_rnd, "Tech")
    block: List[str] = []
    for t in donor_techs:
        block += donor_lines[t[0]:t[1]]
    base[techs[0][0]:techs[-1][1]] = block
    rnd = _scenario(base, "ResearchAndDevelopment")
    if not _recipe.set_value(base, rnd, "sci", SCIENCE_SEED):
        raise SystemExit("could not seed R&D sci")

    # 5. Title and description.
    _recipe.set_top_value(base, "Title", TITLE)
    desc = [i for i, l in enumerate(base) if l == "\tDescription = " + BASE_DESCRIPTION]
    if len(desc) != 1:
        raise SystemExit("base Description line not found")
    base[desc[0]] = "\tDescription = " + DESCRIPTION
    return base


def build_loadmeta(base_meta: List[str], lines: List[str]) -> List[str]:
    """The base loadmeta with vessel count, UT and the science pool restamped."""
    out = _recipe.build_loadmeta(base_meta, lines)
    for i, line in enumerate(out):
        if line.startswith("science = "):
            out[i] = "science = %s" % SCIENCE_SEED
    return out


def build_all() -> Dict[str, List[str]]:
    base_lines = _recipe.read_lines(_path(BASE_NAME, "persistent.sfs"))
    donor_lines = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
    built = build(base_lines, donor_lines)
    meta = build_loadmeta(_recipe.read_lines(_path(BASE_NAME, "persistent.loadmeta")), built)
    return {"persistent.sfs": built, "persistent.loadmeta": meta}


def verify(lines: List[str]) -> List[str]:
    """Post-conditions on the built (or committed) save; empty = every one holds."""
    problems: List[str] = []
    mode = next((l.split("=", 1)[1].strip() for l in lines if l.startswith("\tMode = ")), None)
    if mode != EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r" % (mode, EXPECT_MODE))

    fs = _recipe.find_node(lines, "FLIGHTSTATE")
    vessels = _recipe.child_nodes(lines, fs, "VESSEL") if fs else []
    if len(vessels) != 1:
        return problems + ["expected exactly 1 VESSEL, found %d" % len(vessels)]
    ship = vessels[0]
    for key, want in (("name", VESSEL_NAME), ("sit", EXPECT_SITUATION),
                      ("persistentId", VESSEL_PERSISTENT_ID)):
        if _recipe.get_value(lines, ship, key) != want:
            problems.append("vessel %s is %r, expected %r"
                            % (key, _recipe.get_value(lines, ship, key), want))
    if _recipe.get_value(lines, fs, "activeVessel") != "0":
        problems.append("activeVessel is not 0")
    donor = _recipe.read_lines(_path(DONOR_NAME, "persistent.sfs"))
    donor_ship = _vessel_named(donor, VESSEL_NAME)
    if donor_ship is None or lines[ship[0]:ship[1]] != donor[donor_ship[0]:donor_ship[1]]:
        problems.append("the vessel is not the donor's VESSEL node byte for byte")
    if not _recipe.contains_line(lines, ship, "storedScience = 12.5"):
        problems.append("the lab does not hold the seeded storedScience = 12.5")
    if not _recipe.contains_line(lines, ship, "name = SurfAntenna"):
        problems.append("the vessel has no Communotron 16-S")

    roster = _recipe.find_node(lines, "ROSTER")
    for crew_name in CREW:
        if not _recipe.contains_line(lines, ship, "crew = %s" % crew_name):
            problems.append("%s is not aboard" % crew_name)
        kerbal = _recipe._kerbal_named(lines, roster, crew_name) if roster else None
        if kerbal is None or _recipe.get_value(lines, kerbal, "state") != "Assigned":
            problems.append("roster %s is not Assigned" % crew_name)

    for scenario, key, want in (("Funding", "funds", EXPECT_FUNDS),
                                ("Reputation", "rep", EXPECT_REPUTATION),
                                ("ResearchAndDevelopment", "sci", SCIENCE_SEED)):
        node = _scenario(lines, scenario)
        if node is None or _recipe.get_value(lines, node, key) != want:
            problems.append("%s %s is %r, expected %r" % (
                scenario, key, _recipe.get_value(lines, node, key) if node else None, want))
    if float(SCIENCE_SEED) <= PATENTS_SETUP_SCIENCE:
        problems.append("the science seed does not cover Patents Licensing's setup cost")

    # The strategy system: present and EMPTY, so every strategy is built fresh from config.
    strat = _scenario(lines, "StrategySystem")
    if strat is None:
        problems.append("no StrategySystem SCENARIO")
    else:
        nodes = _recipe.child_nodes(lines, strat, "STRATEGIES")
        if len(nodes) != 1 or _recipe.child_nodes(lines, nodes[0], "STRATEGY"):
            problems.append("the STRATEGIES node must exist and be empty")
    # Administration at level 0: one strategy slot.
    fac = _scenario(lines, "ScenarioUpgradeableFacilities")
    admin = _recipe.find_node(lines, "SpaceCenter/Administration", fac[0], fac[1]) if fac else None
    if admin is None or _recipe.get_value(lines, admin, "lvl") != "0":
        problems.append("Administration is not at level 0")

    # Every craft part is unlocked.
    rnd = _scenario(lines, "ResearchAndDevelopment")
    available = set()
    for tech in _recipe.child_nodes(lines, rnd, "Tech") if rnd else []:
        if _recipe.get_value(lines, tech, "state") == "Available":
            available.update(lines[i].strip()[len("part = "):] for i in range(tech[0], tech[1])
                             if lines[i].strip().startswith("part = "))
    parts = {_recipe.get_value(lines, p, "name") for p in _recipe.child_nodes(lines, ship, "PART")}
    missing = sorted(parts - available)
    if missing:
        problems.append("craft parts not unlocked: %r" % missing)

    if _scenario(lines, "ParsekScenario") is None:
        problems.append("no ParsekScenario SCENARIO node (the FLIGHT route would persist nothing)")
    return problems


def committed_problems() -> List[str]:
    want = build_all()
    target = os.path.join(_SAVES, TARGET_NAME)
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
    base_addons = os.path.join(_SAVES, BASE_NAME, "AddOns")
    ours_addons = os.path.join(target, "AddOns")
    for root, _dirs, names in os.walk(base_addons):
        for name in names:
            src = os.path.join(root, name)
            rel = os.path.relpath(src, base_addons)
            dst = os.path.join(ours_addons, rel)
            if not os.path.isfile(dst):
                problems.append("missing AddOns/%s" % rel.replace(os.sep, "/"))
                continue
            with open(src, "rb") as a, open(dst, "rb") as b:
                if a.read() != b.read():
                    problems.append("drift in AddOns/%s" % rel.replace(os.sep, "/"))
    have = set()
    for root, _dirs, names in os.walk(target):
        for name in names:
            have.add(os.path.relpath(os.path.join(root, name), target).replace(os.sep, "/"))
    expected = set(TEXT_FILES) | {
        "AddOns/" + os.path.relpath(os.path.join(r, n), base_addons).replace(os.sep, "/")
        for r, _d, ns in os.walk(base_addons) for n in ns}
    for extra in sorted(have - expected):
        problems.append("unexpected file %s" % extra)
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
        print("OK" if not problems else "FAIL")
        return 0 if not problems else 1
    built = build_all()
    problems = verify(built["persistent.sfs"])
    if problems:
        for p in problems:
            print("POSTCONDITION: " + p)
        return 1
    target = os.path.join(_SAVES, TARGET_NAME)
    if os.path.isdir(target):
        shutil.rmtree(target)
    shutil.copytree(os.path.join(_SAVES, BASE_NAME, "AddOns"), os.path.join(target, "AddOns"))
    for rel in TEXT_FILES:
        _recipe.write_lines(os.path.join(target, rel), built[rel])
    print("wrote " + target)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
