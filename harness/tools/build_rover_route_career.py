#!/usr/bin/env python3
"""Build the `rover-route-career` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. Roadmap `docs/dev/autotest-roadmap.md` -> "The supply-route
coverage program" Tier C item 9 (Costed dispatch) is the only supply-route lane
whose subject a CAREER save, and every route fixture in the corpus is SANDBOX.
`ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT` was fixed 2026-09-01 (the
costing basis falls back to the first `SourceRefs` member carrying a
single-vessel snapshot, so the rover shape - a snapshot-less runway-stub root -
now prices a dispatch instead of returning 0), and the fix has NEVER been
measured live. This fixture is what makes measuring it cost no flight beyond the
lane's own: it is `rover-route-recorded` STAMPED INTO CAREER, so RVR-4 drives
byte-for-byte the same trees, the same route window and the same endpoint that
RVR-2 already flew green in sandbox, with `env.IsCareer` the ONLY variable
moved.

THE PRECEDENT is `build_strategy_career.py`, which builds a career fixture "BY
CONSTRUCTION (no KSP launch)" by splicing donor values into `fresh-career`. This
one goes the other way - it lifts `fresh-career`'s CAREER SCENARIO NODES,
verbatim, into a recorded SANDBOX save - but the contract is the same: pure over
the two committed inputs, re-derivable, and `--check` wired into the suite so a
change to either input reds locally instead of on a flight.

WHAT A SANDBOX -> CAREER STAMP NEEDS, read by diffing the two committed saves
rather than from memory. `rover-route-recorded` and `fresh-career` share
ROCScenario / AlarmClockScenario / PartUpgradeManager / ResourceScenario /
ProgressTracking / VesselRecovery / KerbalInventoryScenario /
ScenarioAchievements / ScenarioCustomWaypoints / ScenarioDestructibles /
SentinelScenario / CommNetScenario / DeployedScience / DiscoverableObjects, so
those need no work at all. The difference is exactly:

  GAME `Mode`      SANDBOX -> CAREER, and `Title` to this fixture's own leaf
                   (the leaf IS the runSaveName `run.py` stages into).
  ADD, verbatim    Funding, ResearchAndDevelopment, Reputation,
    from the donor  ScenarioUpgradeableFacilities, StrategySystem,
                   ScenarioContractEvents, ContractSystem.
  DROP             ScenarioNewGameIntro. It is SANDBOX-only (the donor career
                   save carries none), and a career save that carries it would
                   be a shape no `fresh-career` consumer has ever asserted.
  KEEP BYTE-IDENTICAL
                   the whole ParsekScenario node, `Parsek/Recordings`,
                   `Parsek/GameState`, FLIGHTSTATE (all 11 VESSEL nodes,
                   `activeVessel`), PARAMETERS, and every other GAME value.

The seven added nodes are inserted at the positions that reproduce
`fresh-career`'s OWN scenario ordering, so a reader diffing the two career saves
sees the same list in the same order. KSP itself resolves ScenarioModules by
name and does not care, but a fixture whose ordering is arbitrary is a fixture
whose next editor has to re-derive that it does not matter.

FACILITY LEVELS ARE THE DONOR'S, AND THE DONOR'S ARE ALL `lvl = 0`. All ten
(LaunchPad / Runway / VAB / SPH / TrackingStation / AstronautComplex /
MissionControl / R&D / Administration / FlagPole) come across verbatim at level
0, i.e. the mode default that every other career fixture in the corpus carries.
Nothing this lane drives reads a facility level: the lane loads straight into
FLIGHT on a landed rover, and `TimeJump` moves the clock with
`Planetarium.SetUniversalTime` rather than through TimeWarp, so no
tracking-station warp tier is in the path.

===========================================================================
THE FUNDS SEED, AND THE HALF OF THE ROADMAP ITEM IT CANNOT BUY
===========================================================================

The roadmap asks this lane for THREE things: a `DispatchDebit` > 0, the
funds-short hold, and the KSC recovery credit. ONE FIXTURE CANNOT SERVE THE
FIRST TWO, and the reason is a property of the recorded ledger rather than of
the seed. It is set out in full because the obvious reading - "seed funds
between one and two dispatch costs" - is wrong here, and rediscovering that on a
flight costs the flight.

STEP 1 - THE DISPATCH COST IS DERIVABLE, AND IT IS 7410.
`RouteOrchestrator.ComputeDispatchFundsCostForRoute` prices a KSC dispatch as
`sum(LookupPartCost(part)) + resource term`, where `LookupPartCost` is
`PartLoader.getPartInfoByName(name).cost` and the resource term comes from the
ROOT recording's COMPLETE `RouteRunCargoManifest` when it has one
(`RouteFundsCalculator.ComputeDispatchFundsCost`'s M2 overload) or from the
chosen snapshot's own per-part RESOURCE amounts otherwise (the legacy walk).

  PARTS BASIS. `SourceRefs[0]` is the transport tree's root
  `cf8d06fc...`, which carries NO `_vessel.craft` sidecar at all (it is in the
  builder's own `NO_VESSEL_CRAFT_RECORDING_IDS`), so the snapshot-less-root
  fallback runs and walks the remaining members in order. `f2fb77ea...` is the
  dock member and is SKIPPED by `IsCombinedVesselSourceMember` (32 direct PART
  children, 16 + 16 - the transport plus the endpoint - and it carries the
  `RouteConnectionWindow`).
  The next member `4370a799...` carries a 16-part single-vessel snapshot and is
  the expected basis. THE CHOICE BARELY MATTERS: the fixture's OTHER candidate
  `0996f1ba...` carries the SAME 16-part multiset, so the parts term is
  7250 either way. That multiset is read out of the committed sidecar by
  `snapshot_part_names` below, so it is a fact about the committed bytes and not
  a transcription.

  RESOURCE TERM. The root `cf8d06fc...` DOES carry a complete run manifest
  (`endCaptured = True`, `START_TRANSPORT_RESOURCES` = LiquidFuel 200), and the
  M2 overload reads it OFF THE ROOT even on the fallback path ("the launch load
  is the root's fact"), so the term is `200 * unitCost(LiquidFuel)` = 160.

  TOTAL = 7250 + 160 = 7410.

STEP 2 - WHERE THE UNIT PRICES COME FROM, AND WHY THEY ARE NOT A GUESS.
There is NO committed part-cost table anywhere in `harness/fixtures` or
`Source/Parsek.Tests` (a headless `LookupPartCost` returns 0 - it is a
`PartLoader` call), so `STOCK_PART_COSTS` below was MEASURED off the automation
instance's OWN fully-patched database,
`automation/stock-minimal/GameData/ModuleManager.ConfigCache`, which is exactly
what `PartLoader` builds its `AvailablePart.cost` from at run time. That matters
for one part: `ProbesBeforeCrew` (a `stock-minimal` devSourcedMod) patches
`@PART[dockingPort2]:NEEDS[CommunityTechTree] { @cost = 600 }`, and
CommunityTechTree IS installed, so the Clamp-O-Tron prices at 600 in the harness
and at the Squad cfg's 280 anywhere else. A table transcribed from
`GameData/Squad/**` alone would be wrong by 320.
THE TABLE CANNOT BE RE-DERIVED BY THIS SUITE (no KSP, no GameData in CI), so it
is an INPUT, pinned here with its provenance, and the FIRST FLIGHT is what
measures the real number: the lane pins `cost=` as a non-zero regex and the
exact figure is written in only after a run has printed it.

STEP 3 - AND THE FUNDS-SHORT HOLD IS STRUCTURALLY UNREACHABLE ON THIS SUBJECT,
whatever the seed is. Three facts compose:

  (a) THE COMMITTED LEDGER PAYS 18,200 FUNDS. `Parsek/GameState/ledger.pgld` -
      kept byte-identical, as it must be - carries FIVE `MilestoneAchievement`
      rows (RecordsDistance 4800, RecordsSpeed 4800, Kerbin/BaseConstruction
      5400, Kerbin/Docking 2400, Kerbin/Landing 800). Each has a DISTINCT
      `milestoneId`, so `MilestonesModule.ProcessAction` marks every one of them
      `Effective = true` on its first-hit branch, and `FundsModule` adds all
      five to the running balance. The sandbox source recorded the awards
      because KSP computes them regardless of mode; in CAREER they are payable.
  (b) THE LEDGER SEED IS THE LIVE POOL, so the awards land ON TOP of it.
      `LedgerOrchestrator.EnsureInitialFundsSeed` finds no `FundsInitial` row
      (the sandbox ledger has none) and `baseline_0.pgsb` carries `funds = 0`,
      so it falls through to `Ledger.SeedInitialFunds(Funding.Instance.Funds)`.
      `KspStatePatcher.PatchFunds` then patches the pool to
      `seed + 18200 - spendings`. Effective funds at the first dispatch are
      therefore `FUNDS_SEED + 18200`, and `FUNDS_SEED = 0` is not an escape: a
      zero pool makes `EnsureInitialFundsSeed` return false, `PatchFunds` skip
      on `!funds.HasSeed`, and the FIRST cycle block FundsShort.
  (c) ONLY ONE CYCLE EVER CHARGES. `ProcessLoopRoute` returns before
      `EmitLoopCycle` on a blocked cycle, so a blocked cycle emits no
      `RouteCargoDebited` and moves no funds - and RVR-2 measured cycle 1
      DELIVERING and cycle 2 blocking `DestinationFull` on these same bytes
      (the endpoint's 102.4 of LiquidFuel headroom against a 97.6 manifest).
      After the one delivery the destination is full forever, so no third
      dispatch is reachable to be refused for funds.
  => funds-short would need `FUNDS_SEED + 18200 < 2 * 7410 = 14820`, i.e. a
      NEGATIVE seed. It is not a tuning problem.

WHAT THE SEED IS SOLVED FOR INSTEAD. `FUNDS_SEED` is sized so the SEED ALONE -
before a single milestone award is counted - affords exactly ONE dispatch and
not two. That is the shape a future funds-short lane needs, and it leaves that
lane with exactly ONE thing to change (neutralize the five MilestoneAchievement
rows, which is a ledger edit and therefore a DIFFERENT fixture) rather than a
seed to re-solve as well:

  FLOOR   `DISPATCH_COST_MAX` = 7488.08, the highest reading the cost can take
          across every basis the fallback could pick: the legacy walk over
          `0996f1ba...` (7250 parts + 297.6 LiquidFuel * 0.8; its ElectricCharge
          is priced at unitCost 0). The expected M2 reading is 7410 and the
          third possibility is the legacy walk over `4370a799...` at 7331.92.
  CEILING `2 * DISPATCH_COST_MIN` = 14663.84, twice the LOWEST reading
          (7331.92), because at or above it a second dispatch would also
          afford.
  `FUNDS_SEED` is 11000, the nearest whole thousand to the centre of
  `[7488.08, 14663.84)` (11075.96). It clears the highest possible cost by 47%
  and sits 25% below twice the lowest, so a 20% error in the measured price
  table moves neither conclusion.

AND IT COSTS THE LANE NOTHING TODAY. 11000 + 18200 = 29200 is far above 7410, so
cycle 0 dispatches with no ambiguity whatever, and cycle 1 blocks
`DestinationFull reason=LiquidFuel` exactly as RVR-2's second flight measured -
which is what RVR-4 pins.

THE RECOVERY CREDIT, the roadmap's third ask, is likewise not collected here and
the reason is in the bytes: `EmitPendingRecoveryCredit` sums
`RouteRunCostCalculator.SumRecoveredCredits` over the source tree's ELS
recovery rows, and this ledger has none (neither rover was ever recovered), so
it is expected to log `credit-skip zero-recovery`. A recovery-credit lane needs
a recorded flight that ENDS in a KSC recovery, which no committed route fixture
is.

===========================================================================

Usage:
    python harness/tools/build_rover_route_career.py            # write it
    python harness/tools/build_rover_route_career.py --check    # verify only

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture. It
is WIRED, not decorative: `RoverRouteCareerFixtureDriftTests` in
`harness/lib/test_build_rover_route_career.py` runs the same `verify` in-process
AND re-runs `build` over the CURRENT `rover-route-recorded` + `fresh-career`
bytes, asserting byte-identity with the committed save - so a change to either
input reds in the suite instead of drifting silently into a live flight.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
import zlib
from typing import Dict, List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text helpers, and ONE copy of the recorded
# fixture's own post-conditions. Importing the sibling builder rather than
# re-implementing its checks is what makes "the Parsek payload is unchanged" a
# fact this file cannot drift away from: the SAME `verify_save` / `verify_tree`
# that gate `rover-route-recorded` are run against this save.
import build_rover_route_recorded as recorded  # noqa: E402

find_node = recorded.find_node
child_nodes = recorded.child_nodes
get_value = recorded.get_value
set_value = recorded.set_value
set_top_value = recorded.base_builder.set_top_value

BASE_NAME = "rover-route-recorded"
DONOR_NAME = "fresh-career"
TARGET_NAME = "rover-route-career"

# --- the stamp ------------------------------------------------------------

EXPECT_MODE = "CAREER"
SOURCE_MODE = "SANDBOX"

# SANDBOX-only; the donor career save carries none.
DROP_SCENARIOS = ("ScenarioNewGameIntro",)

# (anchor scenario already in the base, donor scenarios inserted BEFORE it).
# The anchors are chosen so the result reproduces `fresh-career`'s own scenario
# ordering; see the module docstring.
CAREER_SCENARIO_INSERTS = (
    ("ProgressTracking", ("Funding",)),
    ("VesselRecovery", ("ResearchAndDevelopment",)),
    ("KerbalInventoryScenario", ("Reputation",)),
    ("SentinelScenario", ("ScenarioUpgradeableFacilities", "StrategySystem",
                          "ScenarioContractEvents", "ContractSystem")),
)
CAREER_SCENARIOS = tuple(
    name for _anchor, names in CAREER_SCENARIO_INSERTS for name in names)

# --- the funds seed (see "THE FUNDS SEED" in the module docstring) --------

FUNDS_SEED = 11000

# Stock part prices as the AUTOMATION INSTANCE resolves them, read off
# `automation/stock-minimal/GameData/ModuleManager.ConfigCache` (the
# fully-MM-patched database `PartLoader` builds `AvailablePart.cost` from), NOT
# off `GameData/Squad/**`. The one divergence is `dockingPort2`: ProbesBeforeCrew
# patches it to 600 under `NEEDS[CommunityTechTree]`, and both mods are in the
# `stock-minimal` profile, so the Squad cfg's 280 is the WRONG number here.
#
# THIS TABLE IS AN INPUT, NOT A DERIVATION - no CI job can re-read it. It exists
# so the seed band below is arithmetic rather than a feeling, and the first
# flight is what measures the real `cost=` figure.
STOCK_PART_COSTS: Dict[str, float] = {
    "probeStackSmall": 2250.0,          # RC-001S Remote Guidance Unit
    "mk2FuselageShortLiquid": 750.0,    # Mk2 Liquid Fuel Fuselage Short
    "advSasModule": 1200.0,             # Advanced Inline Stabilizer
    "roverWheel1": 450.0,               # RoveMax Model M1
    "ConformalStorageUnit": 100.0,      # SEQ-3C Conformal Storage Unit
    "solarPanels5": 75.0,               # OX-STAT Photovoltaic Panels
    "dockingPort2": 600.0,              # Clamp-O-Tron (ProbesBeforeCrew @cost)
}
# `PartResourceLibrary` unit costs, same source.
STOCK_RESOURCE_UNIT_COSTS: Dict[str, float] = {
    "LiquidFuel": 0.8,
    "ElectricCharge": 0.0,
}

# The recording whose snapshot the snapshot-less-root fallback is expected to
# price, and the one other member that carries a single-vessel snapshot. Both
# are read from the committed sidecars by `snapshot_part_names`; the expected
# basis is named separately from the alternative so the band below states WHICH
# reading is which.
EXPECTED_PARTS_BASIS_RECORDING_ID = "4370a799d00644f68d9b4a2ca9f72d0c"
ALTERNATE_PARTS_BASIS_RECORDING_ID = "0996f1ba7c7b4d3a8d95cf8be77fbe6d"
# The root's COMPLETE run manifest's START_TRANSPORT_RESOURCES (the launch load
# the M2 basis prices). Read off the committed save by `root_launch_resources`.
ROOT_LAUNCH_MANIFEST_RECORDING_ID = "cf8d06fc7bf74e1a82bc70fc79290847"

# --- inherited donor values, asserted rather than assumed ----------------
EXPECT_SCIENCE = "100"
EXPECT_REPUTATION = "0"
EXPECT_FACILITY_LEVEL = "0"
EXPECT_FACILITY_COUNT = 10

# `.pgsb` / `.pgld` / `.pann` / `.prec` / craft sidecars - copied verbatim.
PAYLOAD_DIRS = ("Parsek", "AddOns")


# ---------------------------------------------------------------------------
# File I/O. The base is LF; so is this.
# ---------------------------------------------------------------------------


def newline_of(path: str) -> str:
    """The line terminator the file on disk actually uses.

    `harness/fixtures/**` is `-text` in `.gitattributes`, so what is committed is
    what lands in the working tree verbatim. Reading the terminator off the base
    is the version that cannot be wrong whichever way the base is stored."""
    with open(path, "rb") as fh:
        data = fh.read()
    return "\r\n" if b"\r\n" in data else "\n"


def read_lines(path: str) -> List[str]:
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


def write_lines(path: str, lines: List[str], newline: str = "\n") -> None:
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(newline.join(lines))


def scenario_node(lines: List[str], name: str) -> Optional[Tuple[int, int]]:
    """(header, end) of the GAME-level `SCENARIO` whose `name = <name>`."""
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == name:
            return node
        i = node[1]


def scenario_names(lines: List[str]) -> List[str]:
    """Every GAME-level SCENARIO's `name`, in file order."""
    out: List[str] = []
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return out
        out.append(get_value(lines, node, "name") or "")
        i = node[1]


def title_of(lines: List[str]) -> Optional[str]:
    for line in lines:
        if line.startswith("\tTitle = "):
            return line.split("=", 1)[1].strip()
    return None


def mode_of(lines: List[str]) -> Optional[str]:
    for line in lines:
        if line.startswith("\tMode = "):
            return line.split("=", 1)[1].strip()
    return None


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def build(base_lines: List[str], donor_lines: List[str],
          funds_seed: int = FUNDS_SEED,
          target_name: str = TARGET_NAME) -> List[str]:
    """Return the career-stamped save. Pure over the two input line lists."""
    out = list(base_lines)

    if mode_of(out) != SOURCE_MODE:
        raise SystemExit("base GAME Mode is %r, expected %r"
                         % (mode_of(out), SOURCE_MODE))
    if not set_top_value(out, "Mode", EXPECT_MODE):
        raise SystemExit("base has no GAME-level Mode line")
    if not set_top_value(out, "Title", "%s (CAREER)" % target_name):
        raise SystemExit("base has no GAME-level Title line")

    for name in DROP_SCENARIOS:
        node = scenario_node(out, name)
        if node is None:
            raise SystemExit("base carries no SCENARIO { name = %s } to drop"
                             % name)
        del out[node[0]:node[1]]

    # Each anchor is re-resolved against the CURRENT list, so the groups are
    # order-independent and a moved anchor fails loudly instead of splicing a
    # career node into the middle of something else.
    for anchor, names in CAREER_SCENARIO_INSERTS:
        at = scenario_node(out, anchor)
        if at is None:
            raise SystemExit("base carries no SCENARIO { name = %s } to anchor "
                             "the %s insert against" % (anchor, ", ".join(names)))
        block: List[str] = []
        for name in names:
            src = scenario_node(donor_lines, name)
            if src is None:
                raise SystemExit("donor %s carries no SCENARIO { name = %s }"
                                 % (DONOR_NAME, name))
            block.extend(donor_lines[src[0]:src[1]])
        out[at[0]:at[0]] = block

    funding = scenario_node(out, "Funding")
    if funding is None or not set_value(out, funding, "funds", str(funds_seed)):
        raise SystemExit("the spliced Funding SCENARIO carries no `funds` value")

    return out


def build_loadmeta(base_meta: List[str], funds_seed: int = FUNDS_SEED) -> List[str]:
    """Restamp the three fields the mode change moves.

    `LoadGameDialog`'s save-info reader shows these in the Load menu and nothing
    in the harness reads them, but a fixture whose two halves disagree is a
    fixture that teaches its next reader wrong. `reputationPercent` is
    `(int)(rep / 10f)` = 0 at the donor's `rep = 0`, which is what the base
    already carries, so it is left alone."""
    out = list(base_meta)
    for i, line in enumerate(out):
        if line.startswith("gameMode = "):
            out[i] = "gameMode = %s" % EXPECT_MODE
        elif line.startswith("funds = "):
            out[i] = "funds = %d" % funds_seed
        elif line.startswith("science = "):
            out[i] = "science = %s" % EXPECT_SCIENCE
    return out


# ---------------------------------------------------------------------------
# The derived dispatch cost. Read from the COMMITTED bytes plus the pinned
# price table, so a re-harvest that changes the rover reds here.
# ---------------------------------------------------------------------------


def _decode_snapshot_sidecar(path: str) -> str:
    """The `SNAPSHOT_SIDECAR` ConfigNode text inside a `_vessel.craft`.

    `SnapshotSidecarCodec` writes `PSN0` + formatVersion + schemaGeneration +
    codec byte + uncompressed length + compressed length + CRC32 (25 bytes), then
    a RAW DEFLATE payload (`CodecDeflate = 1`, `CompressionLevel.Optimal`). The
    header length is `SnapshotSidecarCodec.HeaderByteCount`, restated here as the
    constant it is rather than searched for, so a codec change fails loudly."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:4] != b"PSN0":
        raise SystemExit("%s does not carry the PSN0 snapshot magic" % path)
    return zlib.decompress(data[25:], -15).decode("utf-8")


def snapshot_part_names(fixture_dir: str, recording_id: str) -> List[str]:
    """The `PART` node names of a recording's committed vessel snapshot.

    These are exactly the nodes `RouteFundsCalculator.ComputeDispatchFundsCost`
    walks (`vesselSnapshot.GetNodes("PART")`, then `name` falling back to
    `part`), so the multiset returned here IS the parts term's input."""
    path = os.path.join(fixture_dir, "Parsek", "Recordings",
                        recording_id + "_vessel.craft")
    lines = _decode_snapshot_sidecar(path).split("\n")
    sidecar = (0, len(lines))
    wrapper = find_node(lines, "SNAPSHOT_SIDECAR", 0)
    if wrapper is not None:
        sidecar = wrapper
    vessel = find_node(lines, "VESSEL", sidecar[0], sidecar[1])
    if vessel is None:
        raise SystemExit("%s carries no VESSEL node" % path)
    out: List[str] = []
    for part in child_nodes(lines, vessel, "PART"):
        name = get_value(lines, part, "name") or get_value(lines, part, "part")
        if name:
            out.append(name)
    return out


def snapshot_part_resources(fixture_dir: str,
                            recording_id: str) -> Dict[str, float]:
    """Summed per-PART `RESOURCE.amount` of a snapshot (the LEGACY walk's term)."""
    path = os.path.join(fixture_dir, "Parsek", "Recordings",
                        recording_id + "_vessel.craft")
    lines = _decode_snapshot_sidecar(path).split("\n")
    wrapper = find_node(lines, "SNAPSHOT_SIDECAR", 0) or (0, len(lines))
    vessel = find_node(lines, "VESSEL", wrapper[0], wrapper[1])
    if vessel is None:
        raise SystemExit("%s carries no VESSEL node" % path)
    out: Dict[str, float] = {}
    for part in child_nodes(lines, vessel, "PART"):
        for res in child_nodes(lines, part, "RESOURCE"):
            name = get_value(lines, res, "name")
            amount = get_value(lines, res, "amount")
            if not name or amount is None:
                continue
            out[name] = out.get(name, 0.0) + float(amount)
    return out


def root_launch_resources(lines: List[str]) -> Dict[str, float]:
    """The root recording's COMPLETE run manifest START_TRANSPORT_RESOURCES.

    Returns {} when the manifest is not complete (`endCaptured` absent/False),
    which is the same gate `ComputeDispatchFundsCostForRoute` applies before
    taking the M2 launch-manifest basis."""
    scn = recorded.parsek_scenario(lines)
    if scn is None:
        return {}
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        for rec in child_nodes(lines, tree, "RECORDING"):
            if get_value(lines, rec, "recordingId") != ROOT_LAUNCH_MANIFEST_RECORDING_ID:
                continue
            manifests = child_nodes(lines, rec, "ROUTE_RUN_MANIFEST")
            if not manifests:
                return {}
            manifest = manifests[0]
            if get_value(lines, manifest, "endCaptured") != "True":
                return {}
            starts = child_nodes(lines, manifest, "START_TRANSPORT_RESOURCES")
            if not starts:
                return {}
            out: Dict[str, float] = {}
            for res in child_nodes(lines, starts[0], "RESOURCE"):
                name = get_value(lines, res, "name")
                amount = get_value(lines, res, "amount")
                if name and amount is not None:
                    out[name] = out.get(name, 0.0) + float(amount)
            return out
    return {}


def parts_term(part_names: List[str]) -> float:
    """`sum(LookupPartCost(name))` over a snapshot's PART names."""
    total = 0.0
    for name in part_names:
        if name not in STOCK_PART_COSTS:
            raise SystemExit(
                "no pinned stock cost for part %r - the snapshot changed, so "
                "STOCK_PART_COSTS must be re-read off the automation instance's "
                "ModuleManager.ConfigCache" % name)
        total += STOCK_PART_COSTS[name]
    return total


def resource_term(amounts: Dict[str, float]) -> float:
    total = 0.0
    for name, amount in amounts.items():
        if name not in STOCK_RESOURCE_UNIT_COSTS:
            raise SystemExit("no pinned unit cost for resource %r" % name)
        total += amount * STOCK_RESOURCE_UNIT_COSTS[name]
    return total


def dispatch_cost_readings(fixture_dir: str,
                           lines: List[str]) -> Dict[str, float]:
    """Every value `ComputeDispatchFundsCostForRoute` could return here.

    Three readings, keyed by which basis produced them. The M2 one is what the
    lane expects; the two legacy ones bound the band the seed is sized against,
    because which member the fallback picks and whether the root's manifest is
    judged complete are RUN-TIME decisions this file cannot settle."""
    launch = root_launch_resources(lines)
    readings: Dict[str, float] = {}
    expected_parts = parts_term(
        snapshot_part_names(fixture_dir, EXPECTED_PARTS_BASIS_RECORDING_ID))
    alternate_parts = parts_term(
        snapshot_part_names(fixture_dir, ALTERNATE_PARTS_BASIS_RECORDING_ID))
    if launch:
        readings["m2-launch-manifest"] = expected_parts + resource_term(launch)
    readings["legacy-expected-member"] = expected_parts + resource_term(
        snapshot_part_resources(fixture_dir, EXPECTED_PARTS_BASIS_RECORDING_ID))
    readings["legacy-alternate-member"] = alternate_parts + resource_term(
        snapshot_part_resources(fixture_dir, ALTERNATE_PARTS_BASIS_RECORDING_ID))
    return readings


# ---------------------------------------------------------------------------
# Post-conditions. Run on the freshly built save AND on --check.
# ---------------------------------------------------------------------------


def verify_career_stamp(lines: List[str], funds_seed: int = FUNDS_SEED,
                        target_name: str = TARGET_NAME) -> List[str]:
    """The CAREER half: mode, title, the seven added nodes, the dropped one."""
    problems: List[str] = []

    if mode_of(lines) != EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r"
                        % (mode_of(lines), EXPECT_MODE))
    want_title = "%s (CAREER)" % target_name
    if title_of(lines) != want_title:
        problems.append("GAME Title is %r, expected %r"
                        % (title_of(lines), want_title))

    names = scenario_names(lines)
    for name in DROP_SCENARIOS:
        if name in names:
            problems.append(
                "SCENARIO { name = %s } survived - it is SANDBOX-only and the "
                "donor career save carries none" % name)
    for name in CAREER_SCENARIOS:
        count = names.count(name)
        if count != 1:
            problems.append("SCENARIO { name = %s } appears %d time(s), "
                            "expected exactly 1" % (name, count))

    funding = scenario_node(lines, "Funding")
    if funding is not None:
        got = get_value(lines, funding, "funds")
        if got != str(funds_seed):
            problems.append("Funding funds is %r, expected the seed %r"
                            % (got, str(funds_seed)))
    rnd = scenario_node(lines, "ResearchAndDevelopment")
    if rnd is not None and get_value(lines, rnd, "sci") != EXPECT_SCIENCE:
        problems.append("ResearchAndDevelopment sci is %r, expected the donor's %r"
                        % (get_value(lines, rnd, "sci"), EXPECT_SCIENCE))
    rep = scenario_node(lines, "Reputation")
    if rep is not None and get_value(lines, rep, "rep") != EXPECT_REPUTATION:
        problems.append("Reputation rep is %r, expected the donor's %r"
                        % (get_value(lines, rep, "rep"), EXPECT_REPUTATION))

    # Facilities: the donor's ten, all at the mode default. Stated as a count
    # plus a level so a partial lift (or a future upgraded donor) reds.
    facilities = scenario_node(lines, "ScenarioUpgradeableFacilities")
    if facilities is not None:
        levels = [line.strip() for line in lines[facilities[0]:facilities[1]]
                  if line.strip().startswith("lvl = ")]
        if len(levels) != EXPECT_FACILITY_COUNT:
            problems.append("ScenarioUpgradeableFacilities carries %d lvl rows, "
                            "expected %d" % (len(levels), EXPECT_FACILITY_COUNT))
        bad = sorted({l for l in levels if l != "lvl = " + EXPECT_FACILITY_LEVEL})
        if bad:
            problems.append("facility levels are not all %r: %r"
                            % (EXPECT_FACILITY_LEVEL, bad))

    # An EMPTY STRATEGIES node and an EMPTY CONTRACTS node: the donor's own
    # clean-slate shape, and what keeps the career passive under the lane.
    strategy = scenario_node(lines, "StrategySystem")
    if strategy is not None:
        node = find_node(lines, "STRATEGIES", strategy[0], strategy[1])
        if node is None:
            problems.append("the StrategySystem SCENARIO carries no STRATEGIES node")
        elif node[1] - node[0] != 3:
            problems.append("the STRATEGIES node is not empty (%d lines)"
                            % (node[1] - node[0]))
    contracts = scenario_node(lines, "ContractSystem")
    if contracts is not None:
        node = find_node(lines, "CONTRACTS", contracts[0], contracts[1])
        if node is None:
            problems.append("the ContractSystem SCENARIO carries no CONTRACTS node")
        elif node[1] - node[0] != 3:
            problems.append("the CONTRACTS node is not empty (%d lines) - an "
                            "active contract would put ledger rows this lane "
                            "does not model into the run" % (node[1] - node[0]))

    return problems


def verify_payload_unchanged(lines: List[str],
                             base_lines: List[str]) -> List[str]:
    """The Parsek half: the ParsekScenario node is the base's, LINE FOR LINE.

    Stated over the NODE rather than over a checksum of the file so a failure
    names the payload rather than the whole save, and so the career stamp - which
    touches only GAME-level values and GAME-level SCENARIO siblings - is provably
    outside it."""
    problems: List[str] = []
    mine = recorded.parsek_scenario(lines)
    theirs = recorded.parsek_scenario(base_lines)
    if mine is None:
        return ["no ParsekScenario SCENARIO node"]
    if theirs is None:
        return ["base %s carries no ParsekScenario node" % BASE_NAME]
    if lines[mine[0]:mine[1]] != base_lines[theirs[0]:theirs[1]]:
        problems.append(
            "the ParsekScenario node is NOT byte-identical to %s's - the career "
            "stamp must touch GAME values and GAME-level SCENARIO siblings only"
            % BASE_NAME)
    return problems


def verify_flightstate_unchanged(lines: List[str],
                                 base_lines: List[str]) -> List[str]:
    """FLIGHTSTATE too: same 11 VESSEL nodes, same activeVessel, same bytes."""
    mine = recorded.flightstate_node(lines)
    theirs = recorded.flightstate_node(base_lines)
    if mine is None:
        return ["no FLIGHTSTATE node"]
    if theirs is None:
        return ["base %s carries no FLIGHTSTATE node" % BASE_NAME]
    if lines[mine[0]:mine[1]] != base_lines[theirs[0]:theirs[1]]:
        return ["the FLIGHTSTATE node is NOT byte-identical to %s's - the route "
                "endpoint vessel, its LiquidFuel headroom and its repaired "
                "inventory are what RVR-2 measured and RVR-4 re-measures in "
                "career" % BASE_NAME]
    return []


def verify_seed_band(fixture_dir: str, lines: List[str],
                     funds_seed: int = FUNDS_SEED) -> List[str]:
    """THE SEED IS SOLVED, AND THIS IS THE SOLVING RE-RUN.

    Both bounds are recomputed from the committed snapshot bytes plus the pinned
    price table, so a re-harvest that changed the rover - or an edit to the price
    table - reds here instead of shipping a seed that no longer affords one
    dispatch (or affords two). See "THE FUNDS SEED" in the module docstring for
    why the band is what a FUTURE funds-short lane needs rather than something
    this lane can collect."""
    problems: List[str] = []
    readings = dispatch_cost_readings(fixture_dir, lines)
    if "m2-launch-manifest" not in readings:
        problems.append(
            "the root recording %s no longer carries a COMPLETE "
            "ROUTE_RUN_MANIFEST, so the M2 launch-manifest basis is gone and "
            "the expected cost reading with it"
            % ROOT_LAUNCH_MANIFEST_RECORDING_ID)
    lo = min(readings.values())
    hi = max(readings.values())
    if funds_seed < hi:
        problems.append(
            "FUNDS_SEED %d is below the highest possible dispatch cost %.2f - "
            "the seed alone would not afford one dispatch" % (funds_seed, hi))
    if funds_seed >= 2.0 * lo:
        problems.append(
            "FUNDS_SEED %d reaches twice the lowest possible dispatch cost "
            "%.2f - the seed alone would afford TWO dispatches, which is the "
            "shape a funds-short lane cannot use" % (funds_seed, 2.0 * lo))
    if funds_seed <= 0:
        problems.append(
            "FUNDS_SEED must be positive: a zero pool makes "
            "LedgerOrchestrator.EnsureInitialFundsSeed return false, "
            "KspStatePatcher.PatchFunds skip on !HasSeed, and the FIRST cycle "
            "block FundsShort")
    return problems


def verify(fixture_dir: str, lines: List[str], base_lines: List[str],
           donor_lines: List[str], funds_seed: int = FUNDS_SEED,
           target_name: str = TARGET_NAME) -> List[str]:
    """Every post-condition. Empty list = the fixture is what it claims to be."""
    problems: List[str] = []
    problems += verify_career_stamp(lines, funds_seed, target_name)
    problems += verify_payload_unchanged(lines, base_lines)
    problems += verify_flightstate_unchanged(lines, base_lines)
    # The RECORDED fixture's OWN post-conditions, run verbatim against this save:
    # trees, recording ids, tree orders, the no-ROUTES assertion, the prompted
    # candidate, schema generation, the seal-state absence, the active vessel,
    # the route window and the endpoint inventory repair. None of them read Mode
    # or Title, so they apply unchanged to the career sibling.
    problems += recorded.verify_save(lines)
    problems += recorded.verify_tree(fixture_dir)
    problems += verify_seed_band(fixture_dir, lines, funds_seed)

    # THE DERIVATION ITSELF. Every line except the career stamp must be the
    # base's, so a change to either input cannot arrive here as anything but a
    # rebuild.
    expected = build(base_lines, donor_lines, funds_seed, target_name)
    if expected != lines:
        problems.append(
            "the save is not what `build` produces from the current %s + %s "
            "bytes - re-run this builder" % (BASE_NAME, DONOR_NAME))
    return problems


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of writing it")
    parser.add_argument("--target-name", default=TARGET_NAME)
    parser.add_argument("--funds", type=int, default=FUNDS_SEED)
    args = parser.parse_args(argv)

    base_dir = os.path.join(_SAVES, BASE_NAME)
    base_sfs = os.path.join(base_dir, "persistent.sfs")
    donor_sfs = os.path.join(_SAVES, DONOR_NAME, "persistent.sfs")
    target_dir = os.path.join(_SAVES, args.target_name)
    target_sfs = os.path.join(target_dir, "persistent.sfs")

    for path in (base_sfs, donor_sfs):
        if not os.path.isfile(path):
            print("FAIL: missing input fixture %s" % path)
            return 1
    base_lines = read_lines(base_sfs)
    donor_lines = read_lines(donor_sfs)

    if args.check:
        if not os.path.isfile(target_sfs):
            print("FAIL: %s does not exist" % target_sfs)
            return 1
        problems = verify(target_dir, read_lines(target_sfs), base_lines,
                          donor_lines, args.funds, args.target_name)
        for problem in problems:
            print("FAIL: %s" % problem)
        if problems:
            return 1
        readings = dispatch_cost_readings(target_dir, read_lines(target_sfs))
        print("OK: %s satisfies every post-condition (funds seed %d; derived "
              "dispatch cost readings %s)"
              % (args.target_name, args.funds,
                 ", ".join("%s=%.2f" % (k, v) for k, v in sorted(readings.items()))))
        return 0

    built = build(base_lines, donor_lines, args.funds, args.target_name)

    # The payload directories are copied FIRST: `verify` runs `verify_tree` and
    # the snapshot-derived cost band over the target directory, so the sidecars
    # must be in place before the post-conditions run.
    os.makedirs(target_dir, exist_ok=True)
    for name in PAYLOAD_DIRS:
        src = os.path.join(base_dir, name)
        if os.path.isdir(src):
            shutil.copytree(src, os.path.join(target_dir, name),
                            dirs_exist_ok=True)
    write_lines(target_sfs, built, newline_of(base_sfs))
    base_meta = os.path.join(base_dir, "persistent.loadmeta")
    write_lines(os.path.join(target_dir, "persistent.loadmeta"),
                build_loadmeta(read_lines(base_meta), args.funds),
                newline_of(base_meta))

    problems = verify(target_dir, read_lines(target_sfs), base_lines,
                      donor_lines, args.funds, args.target_name)
    if problems:
        for problem in problems:
            print("FAIL: %s" % problem)
        return 1

    readings = dispatch_cost_readings(target_dir, built)
    print("OK: wrote %s (base=%s donor=%s funds=%d; derived dispatch cost "
          "readings %s)"
          % (target_dir, BASE_NAME, DONOR_NAME, args.funds,
             ", ".join("%s=%.2f" % (k, v) for k, v in sorted(readings.items()))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
