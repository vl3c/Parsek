#!/usr/bin/env python3
"""Build the five committed `<source>-career` route fixtures BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. Every save that runs a real supply route is SANDBOX: the recorded route
hosts were hand-flown operator campaigns, and none can be re-flown automatically. So the
route lanes never run with a live `Funding` / `ResearchAndDevelopment` / `Reputation`
singleton, career PARAMETERS, or a career ledger. This tool turns a committed SANDBOX
route save into a CAREER copy in which ONLY THE GAME MODE MOVES (the H73 rule), so a lane
can fly its source lane's driver steps verbatim over it:

    rover-relay-c-recorded -> rover-relay-c-recorded-career   (H74 = RVR-7, H75 = RVR-8)
    depot-route-recorded   -> depot-route-recorded-career     (H76 = H40)
    gloops-airshow         -> gloops-airshow-career           (H77 = H34)
    bdock-recorded         -> bdock-recorded-career           (H78 = H39)
    rover-route-recorded   -> rover-route-recorded-career     (H79 = H58)

THE EDITS, per save, against the source (read, never modified) and `fresh-career` (the
career donor, read, never modified):

  1. GAME: `Mode = CAREER`; `Title` = `<target> (CAREER)`; `Description` names this
     builder. The whole `PARAMETERS` node becomes `fresh-career`'s (career difficulty,
     `TechTreeUrl = GameData/ModuleManager.TechTree` - the tree TECH_NODES was derived
     against), with ONE child carried over from the source: its `ParsekSettings` node, if
     it has one. Parsek's own settings (sidecar mirrors, route lines, map tracing) are not
     a game-mode property, so they stay what the source lane flew with.
  2. Career scenarios from `fresh-career`, inserted at `fresh-career`'s relative
     positions: `Funding` (funds 500000) before ProgressTracking; `ResearchAndDevelopment`
     (sci 100) after it; `Reputation` (rep 0) after VesselRecovery; then
     `ScenarioUpgradeableFacilities`, `StrategySystem`, `ScenarioContractEvents` and
     `ContractSystem` after ScenarioDestructibles. A source that already carries any of
     them is refused. Every other scenario, ProgressTracking included, is the source's:
     its milestones are the recorded flights' own, so a first career load does not award
     them a second time.
  3. Tech: the R&D node gains build_lab_lko_science.TECH_NODES (29 nodes) plus
     build_logi_cargo_career.RIG_EXTRA_TECH_NODES (3), after `start`, granted and not
     bought (science stays 100). A persisted vessel loads in career regardless of
     unlocks (the logi-cargo-career docstring), so this is for R&D consistency only; no
     other node is guessed.
  4. Facilities: every `ScenarioUpgradeableFacilities` building at its TOP level
     (`lvl = 1`, the normalized maximum). A sandbox has every facility at its top level,
     and a level-0 Tracking Station or Mission Control changes map lines, patched
     conics and EVA rules, which would confound a route lane.
  5. The ledger sidecar `Parsek/GameState/ledger.pgld` (these saves carry no embedded
     `GAME_STATE` node) loses three row families and keeps everything else in order:
       a. the starting-value rows written while SANDBOX: `ScienceInitial` (21, science 0)
          and `ReputationInitial` (22). `FundsInitial` (20) is dropped too should one
          ever appear. Kept, `LedgerOrchestrator.EnsureInitialScienceSeed` finds a seed
          and the career's science stays 0.
       b. every row that moves a currency pool in career: here only
          `MilestoneAchievement` (4) rows with a non-zero funds / science / reputation
          award (stock paid nothing in SANDBOX, so the career pools never held those
          awards). Kept, they defeat (a): the funds seed reads the live pool and the walk
          adds the awards on top (running above live, the drawdown guard's UPLIFT clamp
          WARNs on every recalc), and science / reputation history sends
          `EnsureInitialScienceSeed` / `EnsureInitialReputationSeed` to the refusal
          branch, which seeds career start 0 (depot and bdock carry science awards).
          With neither family left the ledger carries no currency history, so Parsek
          seeds funds (500000) and science (100) off the career save's live pools at
          the first load (the sandbox `baseline_0` reads funds 0, which the funds seed
          skips, and an unread science reading beside an empty tech list) and the walk
          equals the save. Reputation 0 with no history keeps deferring its seed, as on
          H73's `logi-cargo-career`, so nothing patches it either. Any OTHER pool-moving type
          (science / funds / reputation earnings or spendings, contracts, facility
          upgrades, hires, strategies, lab earnings) is refused rather than guessed at.
       c. legacy supply-route rows (23-30, 36). `Ledger` drops them at every load since
          LOGISTICS-WITHOUT-MONEY phase 2 (`LedgerLoadMigration.IsLegacyRouteNode`), so
          the strip changes no loaded state; no committed spec reads the load's
          `Dropped N legacy supply-route rows` line.
     Zero-award milestone rows and every non-currency row (KerbalAssignment and the like)
     stay byte for byte. Note: a ledger left EMPTY with committed recordings makes the
     first load run `MigrateOldSaveEvents` over the save's events file, which is empty
     in every source here ("no events to migrate").
  6. `persistent.loadmeta`: `gameMode`, `funds`, `science`, `reputationPercent` and
     `ongoingContracts` from `fresh-career`'s; vessel count, UT and timestamp stay the
     source's.

WHAT IS NOT TOUCHED: FLIGHTSTATE (every vessel), ROSTER, the ParsekScenario node (trees,
routes, rewind points), ProgressTracking, every recording sidecar, every other GameState
sidecar (baselines, milestones, events), RewindPoints quicksaves (they stay SANDBOX
saves: a Re-Fly into one of bdock-recorded's rewind points would load a sandbox game, and
no lane built on these copies invokes one), AddOns and Ships. Each file keeps its own
line endings (the sources mix LF and CRLF).

NOT FIT FOR LEDGER GROUND TRUTH. The sources were flown in SANDBOX, so they carry no
launch-cost, recovery or hire rows; a ground-truth (L2-style) reconstruction over these
saves would compare against a history that never charged anything.

Usage:
    python harness/tools/build_route_career.py              # write all five fixtures
    python harness/tools/build_route_career.py --check      # re-derive, compare bytes
    python harness/tools/build_route_career.py --only bdock-recorded

`--check` rebuilds from the committed inputs and compares bytes; the drift test
`harness/lib/test_route_career.py` runs it in-process.

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
import build_logi_cargo_career as _logi  # noqa: E402

_SAVES = os.path.join(os.path.dirname(_HERE), "fixtures", "saves")

BASE_NAME = "fresh-career"
TARGET_SUFFIX = "-career"
SOURCES = (
    "rover-relay-c-recorded",
    "depot-route-recorded",
    "gloops-airshow",
    "bdock-recorded",
    "rover-route-recorded",
)

SFS = "persistent.sfs"
LOADMETA = "persistent.loadmeta"
LEDGER = os.path.join("Parsek", "GameState", "ledger.pgld")
REWRITTEN = (SFS, LOADMETA, LEDGER)

EXPECT_SOURCE_MODE = "SANDBOX"
EXPECT_MODE = "CAREER"
EXPECT_FUNDS = "500000"
EXPECT_SCIENCE = "100"
EXPECT_REPUTATION = "0"
EXPECT_TECH_TREE_URL = "GameData/ModuleManager.TechTree"
TOP_FACILITY_LEVEL = "1"
DESCRIPTION = ("Parsek autotest fixture (file-constructed: %s converted to CAREER by "
               "harness/tools/build_route_career.py).")

# (scenario, anchor scenario in the source, insert "before" or "after" the anchor).
# Applied in order; each anchor is looked up again after the previous insert.
CAREER_SCENARIOS = (
    ("Funding", "ProgressTracking", "before"),
    ("ResearchAndDevelopment", "ProgressTracking", "after"),
    ("Reputation", "VesselRecovery", "after"),
    ("ScenarioUpgradeableFacilities", "ScenarioDestructibles", "after"),
    ("StrategySystem", "ScenarioUpgradeableFacilities", "after"),
    ("ScenarioContractEvents", "StrategySystem", "after"),
    ("ContractSystem", "ScenarioContractEvents", "after"),
)

LOADMETA_FROM_BASE = ("gameMode", "funds", "science", "reputationPercent",
                      "ongoingContracts")

# GameActionType ids (Source/Parsek/GameActions/GameAction.cs).
SEED_TYPES = {20: "FundsInitial", 21: "ScienceInitial", 22: "ReputationInitial"}
LEGACY_ROUTE_TYPES = {23: "RouteDispatched", 24: "RouteCargoDebited",
                      25: "RouteCargoDelivered", 26: "RoutePaused",
                      27: "RouteEndpointLost", 28: "RouteRecoveryCredited",
                      29: "RouteCargoPickedUp", 30: "RouteResumed", 36: "RouteHeld"}
MILESTONE_TYPE = 4
MILESTONE_AWARD_KEYS = ("milestoneFundsAwarded", "milestoneSciAwarded",
                        "milestoneRepAwarded")
# Every other type that can move a career pool (LedgerOrchestrator.ActionTouches*Budget
# plus the lab and strategy-exchange legs). None appears in any source; one that ever
# does is refused rather than silently kept or dropped.
REFUSED_POOL_TYPES = {0: "ScienceEarning", 1: "ScienceSpending", 2: "FundsEarning",
                      3: "FundsSpending", 5: "ContractAccept", 6: "ContractComplete",
                      7: "ContractFail", 8: "ContractCancel", 9: "ReputationEarning",
                      10: "ReputationPenalty", 12: "KerbalHire", 15: "FacilityUpgrade",
                      17: "FacilityRepair", 18: "StrategyActivate",
                      32: "StrategyScienceDebit", 33: "StrategyScienceCredit",
                      38: "ScienceLabEarning", 39: "StockPoolAdjustment"}


def target_name(source: str) -> str:
    return source + TARGET_SUFFIX


def _path(*parts: str) -> str:
    return os.path.join(_SAVES, *parts)


# ---------------------------------------------------------------------------
# Text I/O that keeps each file's own line endings.
# ---------------------------------------------------------------------------


def read_text(path: str) -> Tuple[List[str], str]:
    """(lines, newline) for a text file whose line endings are uniformly LF or CRLF."""
    with open(path, "rb") as fh:
        data = fh.read().decode("utf-8")
    crlf = data.count("\r\n")
    if crlf and crlf != data.count("\n"):
        raise SystemExit("%s mixes LF and CRLF line endings" % path)
    nl = "\r\n" if crlf else "\n"
    return data.split(nl), nl


def render_text(lines: List[str], nl: str) -> bytes:
    return nl.join(lines).encode("utf-8")


# ---------------------------------------------------------------------------
# persistent.sfs
# ---------------------------------------------------------------------------


def _top_value(lines: List[str], key: str) -> Optional[str]:
    prefix = "\t%s = " % key
    for line in lines:
        if line.startswith(prefix):
            return line[len(prefix):]
    return None


def _top_node(lines: List[str], name: str) -> Optional[Tuple[int, int]]:
    """A GAME-level (single-tab) node."""
    for i in range(len(lines) - 1):
        if lines[i] == "\t" + name and lines[i + 1] == "\t{":
            return _recipe.find_node(lines, name, i)
    return None


def _insert_scenario(lines: List[str], node_lines: List[str], anchor: str,
                     where: str) -> None:
    node = _recipe._scenario_node(lines, anchor)
    if node is None:
        raise SystemExit("source has no %s SCENARIO to anchor an insert" % anchor)
    at = node[0] if where == "before" else node[1]
    lines[at:at] = node_lines


def build_sfs(source: str, src: List[str], base: List[str]) -> List[str]:
    """The career persistent.sfs. Pure over the two input line lists."""
    out = list(src)

    mode = _top_value(out, "Mode")
    if mode != EXPECT_SOURCE_MODE:
        raise SystemExit("%s: GAME Mode is %r, expected %r" % (source, mode, EXPECT_SOURCE_MODE))
    _recipe.set_top_value(out, "Mode", EXPECT_MODE)
    if not _recipe.set_top_value(out, "Title", "%s (CAREER)" % target_name(source)):
        raise SystemExit("%s: no Title line" % source)
    if not _recipe.set_top_value(out, "Description", DESCRIPTION % source):
        raise SystemExit("%s: no Description line" % source)

    # 1. PARAMETERS: fresh-career's, plus the source's ParsekSettings child.
    src_params = _top_node(out, "PARAMETERS")
    base_params = _top_node(base, "PARAMETERS")
    if src_params is None or base_params is None:
        raise SystemExit("%s: a PARAMETERS node is missing" % source)
    params = base[base_params[0]:base_params[1]]
    settings = _recipe.child_nodes(out, src_params, "ParsekSettings")
    if len(settings) > 1:
        raise SystemExit("%s: more than one ParsekSettings node" % source)
    if _recipe.child_nodes(params, (0, len(params)), "ParsekSettings"):
        raise SystemExit("fresh-career unexpectedly carries ParsekSettings")
    if settings:
        params[-1:-1] = out[settings[0][0]:settings[0][1]]
    out[src_params[0]:src_params[1]] = params

    # 2-4. Career scenarios, tech grant, facilities at their top level.
    for name, _anchor, _where in CAREER_SCENARIOS:
        if _recipe._scenario_node(out, name) is not None:
            raise SystemExit("%s already carries a %s SCENARIO" % (source, name))
    for name, anchor, where in CAREER_SCENARIOS:
        node = _recipe._scenario_node(base, name)
        if node is None:
            raise SystemExit("fresh-career has no %s SCENARIO" % name)
        block = base[node[0]:node[1]]
        if name == "ResearchAndDevelopment":
            block = _grant_tech(block)
        elif name == "ScenarioUpgradeableFacilities":
            block = _top_facilities(block)
        _insert_scenario(out, block, anchor, where)
    return out


def _grant_tech(block: List[str]) -> List[str]:
    out = list(block)
    techs = _recipe.child_nodes(out, (0, len(out)), "Tech")
    ids = [_recipe.get_value(out, t, "id") for t in techs]
    if ids != ["start"]:
        raise SystemExit("fresh-career R&D Tech nodes are %r, expected only start" % ids)
    grant: List[str] = []
    for tech_id, cost, parts in _tech.TECH_NODES:
        grant += _tech._tech_block(tech_id, cost, parts)
    for tech_id, cost, parts, _parents in _logi.RIG_EXTRA_TECH_NODES:
        grant += _tech._tech_block(tech_id, cost, parts)
    out[techs[0][1]:techs[0][1]] = grant
    return out


def _top_facilities(block: List[str]) -> List[str]:
    out = list(block)
    changed = 0
    for i, line in enumerate(out):
        if line.strip().startswith("lvl = "):
            indent = line[:len(line) - len(line.lstrip("\t"))]
            out[i] = "%slvl = %s" % (indent, TOP_FACILITY_LEVEL)
            changed += 1
    if changed == 0:
        raise SystemExit("fresh-career facilities carry no lvl lines")
    return out


# ---------------------------------------------------------------------------
# persistent.loadmeta
# ---------------------------------------------------------------------------


def build_loadmeta(src: List[str], base: List[str]) -> List[str]:
    base_values = {}
    for line in base:
        key = line.split(" = ", 1)[0]
        if key in LOADMETA_FROM_BASE:
            base_values[key] = line
    out = list(src)
    seen = set()
    for i, line in enumerate(out):
        key = line.split(" = ", 1)[0]
        if key in base_values:
            out[i] = base_values[key]
            seen.add(key)
    missing = sorted(set(LOADMETA_FROM_BASE) - seen)
    if missing:
        raise SystemExit("loadmeta lacks %r" % missing)
    return out


# ---------------------------------------------------------------------------
# Parsek/GameState/ledger.pgld
# ---------------------------------------------------------------------------


def _ledger_rows(lines: List[str]) -> List[Tuple[int, int]]:
    """(start, end_exclusive) of every top-level GAME_ACTION node."""
    rows = []
    i = 0
    while i < len(lines):
        if lines[i] == "GAME_ACTION" and i + 1 < len(lines) and lines[i + 1] == "{":
            depth = 0
            j = i + 1
            while j < len(lines):
                if lines[j].strip() == "{":
                    depth += 1
                elif lines[j].strip() == "}":
                    depth -= 1
                    if depth == 0:
                        break
                j += 1
            rows.append((i, j + 1))
            i = j + 1
            continue
        i += 1
    return rows


def _row_value(lines: List[str], row: Tuple[int, int], key: str) -> Optional[str]:
    prefix = "\t%s = " % key
    for i in range(row[0], row[1]):
        if lines[i].startswith(prefix):
            return lines[i][len(prefix):]
    return None


def classify_row(lines: List[str], row: Tuple[int, int]) -> str:
    """'seed' / 'route' / 'award' (dropped), 'keep', or raises for a refused type."""
    raw = _row_value(lines, row, "type")
    try:
        t = int(raw)
    except (TypeError, ValueError):
        raise SystemExit("ledger row at line %d has no integer type (%r)" % (row[0] + 1, raw))
    if t in SEED_TYPES:
        return "seed"
    if t in LEGACY_ROUTE_TYPES:
        return "route"
    if t in REFUSED_POOL_TYPES:
        raise SystemExit("ledger row at line %d is a %s (%d): a pool-moving type this "
                         "builder does not model" % (row[0] + 1, REFUSED_POOL_TYPES[t], t))
    if t == MILESTONE_TYPE:
        for key in MILESTONE_AWARD_KEYS:
            if float(_row_value(lines, row, key) or "0") != 0.0:
                return "award"
    return "keep"


def build_ledger(src: List[str]) -> Tuple[List[str], Dict[str, int]]:
    out = list(src)
    rows = _ledger_rows(out)
    counts = {"seed": 0, "route": 0, "award": 0, "keep": 0}
    for row in sorted(rows, reverse=True):
        kind = classify_row(out, row)
        counts[kind] += 1
        if kind != "keep":
            del out[row[0]:row[1]]
    return out, counts


# ---------------------------------------------------------------------------
# The whole fixture
# ---------------------------------------------------------------------------


def _source_files(source: str) -> List[str]:
    root = _path(source)
    rels = []
    for dirpath, _dirs, names in os.walk(root):
        for name in names:
            rels.append(os.path.relpath(os.path.join(dirpath, name), root))
    return sorted(rels)


def build_all(source: str) -> Dict[str, bytes]:
    """Every file of the career fixture, rel path -> bytes. Pure over the inputs."""
    base_sfs, _ = read_text(_path(BASE_NAME, SFS))
    base_meta, _ = read_text(_path(BASE_NAME, LOADMETA))
    files: Dict[str, bytes] = {}
    for rel in _source_files(source):
        src_path = _path(source, rel)
        if rel == SFS:
            lines, nl = read_text(src_path)
            files[rel] = render_text(build_sfs(source, lines, base_sfs), nl)
        elif rel == LOADMETA:
            lines, nl = read_text(src_path)
            files[rel] = render_text(build_loadmeta(lines, base_meta), nl)
        elif rel == LEDGER:
            lines, nl = read_text(src_path)
            files[rel] = render_text(build_ledger(lines)[0], nl)
        else:
            with open(src_path, "rb") as fh:
                files[rel] = fh.read()
    if SFS not in files or LOADMETA not in files:
        raise SystemExit("%s lacks persistent.sfs or persistent.loadmeta" % source)
    return files


def ledger_counts(source: str) -> Optional[Dict[str, int]]:
    """What the strip removes from (and keeps in) the source ledger; None without one."""
    path = _path(source, LEDGER)
    if not os.path.isfile(path):
        return None
    return build_ledger(read_text(path)[0])[1]


# ---------------------------------------------------------------------------
# Post-conditions
# ---------------------------------------------------------------------------


def verify_sfs(lines: List[str]) -> List[str]:
    problems: List[str] = []
    if _top_value(lines, "Mode") != EXPECT_MODE:
        problems.append("GAME Mode is %r" % _top_value(lines, "Mode"))
    if not any(l.strip() == "TechTreeUrl = %s" % EXPECT_TECH_TREE_URL for l in lines):
        problems.append("TechTreeUrl is not %s" % EXPECT_TECH_TREE_URL)
    for scenario, key, want in (("Funding", "funds", EXPECT_FUNDS),
                                ("ResearchAndDevelopment", "sci", EXPECT_SCIENCE),
                                ("Reputation", "rep", EXPECT_REPUTATION)):
        node = _recipe._scenario_node(lines, scenario)
        got = _recipe.get_value(lines, node, key) if node else None
        if got != want:
            problems.append("%s %s is %r, expected %r" % (scenario, key, got, want))
    for name, _anchor, _where in CAREER_SCENARIOS:
        if _recipe._scenario_node(lines, name) is None:
            problems.append("no %s SCENARIO" % name)
    fac = _recipe._scenario_node(lines, "ScenarioUpgradeableFacilities")
    if fac is not None:
        levels = [lines[i].strip() for i in range(fac[0], fac[1])
                  if lines[i].strip().startswith("lvl = ")]
        if not levels or any(l != "lvl = %s" % TOP_FACILITY_LEVEL for l in levels):
            problems.append("facilities not all at the top level: %r" % levels)
    rnd = _recipe._scenario_node(lines, "ResearchAndDevelopment")
    if rnd is not None:
        ids = [_recipe.get_value(lines, t, "id")
               for t in _recipe.child_nodes(lines, rnd, "Tech")]
        want_ids = (["start"] + [t[0] for t in _tech.TECH_NODES]
                    + [t[0] for t in _logi.RIG_EXTRA_TECH_NODES])
        if ids != want_ids:
            problems.append("R&D Tech ids %r != %r" % (ids, want_ids))
    if _recipe._scenario_node(lines, "ParsekScenario") is None:
        problems.append("no ParsekScenario SCENARIO")
    return problems


def verify_ledger(lines: List[str]) -> List[str]:
    problems: List[str] = []
    for row in _ledger_rows(lines):
        try:
            kind = classify_row(lines, row)
        except SystemExit as exc:
            problems.append(str(exc))
            continue
        if kind != "keep":
            problems.append("ledger still carries a %s row at line %d" % (kind, row[0] + 1))
    return problems


def committed_problems(source: str) -> List[str]:
    """Differences between a fresh build and the committed fixture (empty = OK)."""
    want = build_all(source)
    target = _path(target_name(source))
    problems: List[str] = []
    if not os.path.isdir(target):
        return ["missing fixture directory %s" % target_name(source)]
    have = set()
    for dirpath, _dirs, names in os.walk(target):
        for name in names:
            have.add(os.path.relpath(os.path.join(dirpath, name), target))
    for rel in sorted(have - set(want)):
        problems.append("unexpected file %s" % rel.replace(os.sep, "/"))
    for rel, data in sorted(want.items()):
        path = os.path.join(target, rel)
        if not os.path.isfile(path):
            problems.append("missing %s" % rel.replace(os.sep, "/"))
            continue
        with open(path, "rb") as fh:
            if fh.read() != data:
                problems.append("drift in %s" % rel.replace(os.sep, "/"))
    problems += verify_sfs(read_text(os.path.join(target, SFS))[0]) \
        if os.path.isfile(os.path.join(target, SFS)) else []
    if os.path.isfile(os.path.join(target, LEDGER)):
        problems += verify_ledger(read_text(os.path.join(target, LEDGER))[0])
    return problems


def write(source: str) -> List[str]:
    files = build_all(source)
    problems = verify_sfs(files[SFS].decode("utf-8").replace("\r\n", "\n").split("\n"))
    if LEDGER in files:
        problems += verify_ledger(files[LEDGER].decode("utf-8").replace("\r\n", "\n").split("\n"))
    if problems:
        return problems
    target = _path(target_name(source))
    if os.path.isdir(target):
        shutil.rmtree(target)
    for rel, data in files.items():
        path = os.path.join(target, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)
    return []


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true",
                    help="re-derive and compare against the committed fixtures")
    ap.add_argument("--only", action="append", choices=SOURCES,
                    help="limit to one source save (repeatable)")
    args = ap.parse_args(argv)
    failed = 0
    for source in args.only or SOURCES:
        if args.check:
            problems = committed_problems(source)
            for p in problems:
                print("DRIFT: %s: %s" % (target_name(source), p))
            print("%s: %s" % (target_name(source), "OK" if not problems else "FAIL"))
        else:
            problems = write(source)
            for p in problems:
                print("POSTCONDITION: %s: %s" % (target_name(source), p))
            if not problems:
                print("wrote %s %s" % (target_name(source), ledger_counts(source) or ""))
        failed += bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
