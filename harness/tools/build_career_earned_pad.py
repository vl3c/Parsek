#!/usr/bin/env python3
"""Build the `career-earned-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. The career-ledger lane's B.4 strict-per-identity gate needs a
subject with POPULATED per-identity facets, and it needs to run the in-game
`LedgerGroundTruth` category, which is `Scene = GameScenes.FLIGHT`. Exactly one
committed career carries those facets - the save harness run
`2026-08-19_2130_L3-career-science-recover` produced, committed as
`Source/Parsek.Tests/Fixtures/C2CareerPostFix/` - and it carries ZERO `VESSEL`
nodes, because the flight that earned it RECOVERED the craft. A save with no
vessel routes `LoadGame` to the NoVesselSpaceCenter path, so a FLIGHT-scene
batch scene-skips its only declaration: the exact vacuity defect B10 and
L1-passive-sandbox were re-flown to fix.

This tool closes that gap the same way `build_career_pad_craft.py` closed R11:
splice a focusable PRELAUNCH craft into a career that otherwise has none. The
difference is which half is precious. There the career was empty
(`fresh-career`) and the craft was the payload; HERE THE CAREER IS THE PAYLOAD -
14 ledger actions, three `ScienceEarning` rows, a vessel-recovery `FundsEarning`
credit, five milestones, a recorded crewed recovery and Jebediah's career log -
and the craft is only the key that opens the FLIGHT scene.

WHAT IT SPLICES. Six edits against the harvested career SAVE, one against the
career's copied `ledger.pgld`, plus a `persistent.loadmeta` restamp (vessel count
only - see `build_loadmeta`):

  1. `career-science-pad`'s single `type = Ship` `VESSEL` node is inserted into
     the base's own (vessel-less) `FLIGHTSTATE`. THE BASE'S `FLIGHTSTATE` IS
     KEPT, not replaced - which is the one place this builder deliberately
     departs from `build_career_pad_craft.py`'s whole-node swap. That save's
     `FLIGHTSTATE` was `fresh-career`'s and carried nothing; this one carries
     `UT = 408.72`, the clock the whole 14-action ledger was written against.
     Replacing it would rewind the career's clock to the donor's `UT = 9.06`,
     i.e. BEFORE every action in the ledger it is being spliced into.
  2. The spliced vessel's `pid` and `persistentId` are RE-STAMPED, and this is
     the load-bearing edit rather than hygiene. The donor craft IS the craft
     this career flew and recovered: its `pid` is
     `f77e42072e3d4c59b04581daba628b55` and its `persistentId` is `2905720181`,
     byte-identical to both recordings' `recordedVesselGuid` /
     `vesselPersistentId`. `LedgerGroundTruthDiff.CompareRecovery` treats a
     recovery credit whose vessel is STILL PRESENT in the save as a divergence:
     guid-corroborated it is ALWAYS HARD, pid-only it is report-only - which
     strict promotes to hard anyway. A verbatim splice would therefore red the
     armed run on a pure fixture artifact that looks exactly like a product
     defect. Both stamps are asserted non-colliding in `verify`.
  3. The vessel's `lct` / `lastUT` are moved to the base's `UT`, so the craft
     rolled out onto the pad at the career's current time rather than 400
     seconds before it.
  4. The `rewindSave = parsek_rw_*` hint is STRIPPED from every RECORDING node.
     `CommittedFixtureRewindSaveTests` pins both halves of one rule: no fixture
     may commit a `Parsek/Saves/parsek_rw_*.sfs` (harvest exhaust nothing
     automated reads), and none may carry a hint pointing at one that is not
     committed (a dangling reference `Inv9RewindPoint` WARNs on, and FAILs on
     when the owning recording is `CommittedProvisional` - which would turn the
     analyzer RED under the harness's Forbid fresh-save gate). The copy step
     below drops `Parsek/Saves/` for the first half; this edit is the second.

  5. ONE of the career's seven `Offered` `CONTRACT` nodes is RE-STATED AS
     `Active`, and one matching `type = 5` (`ContractAccept`) `GAME_ACTION` row
     is appended to the copied `Parsek/GameState/ledger.pgld`. Together they are
     the D8 `contracts` cell: `LedgerGroundTruthDiff.CompareContracts` already
     ran inside this spec's `facetsCompared=10`, but VACUOUSLY - the seven
     Offered rows keep it from skipping while both sides read
     `reconActive=0 saveActive=0`, so a zero there stated nothing. With these two
     edits the compare reads `reconActive=1 saveActive=1` and the strict gate
     covers a real contract identity. See `_restate_contract_as_active` and
     `build_ledger` for the arithmetic each edit has to honor.

  6. Jebediah's ROSTER row has `state` flipped `Available` -> `Assigned`,
     because the spliced craft carries him. HIS ROW IS OTHERWISE UNTOUCHED, and
     that is deliberate: `build_career_pad_craft.py` swaps the whole row for the
     donor's, which HERE would delete his `CAREER_LOG` (`flight = 1`,
     `Land,Kerbin`, `Flight,Kerbin`, `Recover`) - the SAVE SIDE of the diff's
     `KerbalXp` facet. The reconstruction credits those entries off the ledger,
     so deleting them would manufacture a `PhantomInRecon` divergence: again
     report-only by default, again promoted by strict, again a fixture artifact
     wearing a defect's clothes.

WHAT IS CARRIED VERBATIM. Everything that makes the career a subject: the
`Parsek/` sidecar tree (both recordings, `ledger.pgld`, the GameState
baselines), the POPULATED `SCENARIO{name=ParsekScenario}` node with its single
committed `RECORDING_TREE`, all three earned pools (funds 536558, science
111.599998, reputation 1.99999881), the roster's six kerbals and the full
facility / tech / contract state. The donor contributes ONE `VESSEL` node and
its `AddOns/` directory, and nothing else.

WHY THE POPULATED ParsekScenario NODE IS NOT REPLACED by the donor's inert one:
it IS the payload. `build_career_pad_craft.py` copies the donor's because
`fresh-career` has none; here the base's node carries the committed tree the
recovery credit resolves through, and the tree is NOT active (`isActive` is
absent), so the in-game cell's "no pending / no active uncommitted tree" guard
still passes.

Usage:
    python harness/tools/build_career_earned_pad.py            # write the fixture
    python harness/tools/build_career_earned_pad.py --check    # verify only, no write

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture.
It is WIRED, not decorative: `CareerEarnedPadFixtureDriftTests` runs the same
`verify` in-process AND re-runs `build` over the CURRENT inputs asserting
byte-identity with the committed save, so a re-harvest of `C2CareerPostFix` or a
change to `career-science-pad` reds in the harness suite rather than drifting
silently into a live flight.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_REPO_ROOT = os.path.dirname(_HARNESS_ROOT)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text helpers, for the same reason
# build_career_science_pad.py imports them: a second implementation is a second
# thing to drift.
import build_career_pad_craft as base_builder  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value
set_top_value = base_builder.set_top_value
contains_line = base_builder.contains_line

# The BASE is the xUnit fixture rather than a harness one, and that is
# deliberate: it is the ONE committed copy of the save run 2026-08-19_2130
# produced, and `C2CareerPostFixReplayTests` already makes the closes-to-zero
# claim about those exact bytes. Deriving from it keeps a single source of truth;
# the drift test is what makes the derivation enforced rather than assumed.
BASE_DIR = os.path.join(_REPO_ROOT, "Source", "Parsek.Tests", "Fixtures", "C2CareerPostFix")
DONOR_NAME = "career-science-pad"
TARGET_NAME = "career-earned-pad"
CREW_NAME = base_builder.CREW_NAME

# The re-stamped vessel identity. FIXED LITERALS, not generated: the fixture must
# be byte-reproducible so the drift test can compare against the committed bytes.
#
# Neither value is derived from the donor's - that is the whole point. `verify`
# asserts both against every `recordedVesselGuid` / `vesselPersistentId` the
# save's recordings carry, so a future re-harvest that happened to reuse one of
# these reds rather than shipping a colliding fixture.
NEW_VESSEL_GUID = "3d90c5b1a47e4f28b6c1e0d752843af6"
NEW_VESSEL_PERSISTENT_ID = "1846203977"

# Post-conditions on the produced (or committed) save. The three pools are the
# EARNED ones, not the seed - a fixture that came out at 500000 / 100 / 0 would
# mean the splice had dropped the career payload it exists to carry.
EXPECT_MODE = "CAREER"
EXPECT_SITUATION = "PRELAUNCH"
EXPECT_FUNDS = "536558"
EXPECT_SCIENCE = "111.599998"
EXPECT_REPUTATION = "1.99999881"

# The recorded launch this career flew and recovered. Held as constants so
# `verify` states the collision it is guarding against rather than merely
# computing it, and so a re-harvest that changed them reds loudly.
RECORDED_VESSEL_GUID = "f77e42072e3d4c59b04581daba628b55"
RECORDED_VESSEL_PERSISTENT_ID = "2905720181"

# Jebediah's save-side career log, the KerbalXp facet's ground truth. Asserted
# present because the roster edit below is one `set_value` away from the
# whole-row swap that would delete it.
EXPECT_CAREER_LOG_ENTRY = "0 = Recover"

# The Rewind-to-LAUNCH hint, matched exactly as CommittedFixtureRewindSaveTests
# matches it so the strip and the gate cannot drift apart.
_REWIND_HINT_RE = re.compile(r"^\s*rewindSave = parsek_rw_\w+\s*$")

# ---------------------------------------------------------------------------
# THE D8 `contracts` CELL. Fixed literals, for the same byte-reproducibility
# reason the vessel identity above is fixed.
#
# WHY THE CLAIM IS FIXTURE-CARRIED RATHER THAN DRIVEN, stated here because that
# is the first question a reader should ask: the M-A2 command seam's `KscAction`
# has exactly four kinds (ResearchNode, UpgradeFacility, HireKerbal,
# DismissKerbal - see `Source/Parsek/TestCommands/TestCommandKscAction.cs`), so
# there is no verb that accepts a contract; and `Contract.Accept()` is a
# UI-only entry point that Parsek itself Harmony-PREFIXES to block
# (`Source/Parsek/Patches/ContractAcceptPatch.cs`). A driven accept is not
# merely expensive here, it is unreachable.
#
# IT IS STILL FAITHFUL, and that is measured rather than asserted: the real
# hand-played c2 career's ledger carries a PartTest accept+complete pair whose
# accept row has NO `recordingId` and originates at KSC, i.e. exactly the shape
# below. The one thing NOT copied from it is a completion, deliberately - see
# TRAP 3 on `build_ledger`.
#
# RE-DERIVED 2026-08-20 (branch `kerbal-xp-row`) AGAINST THE SECOND HARVEST, and
# the re-derivation was FORCED rather than optional: KSP mints fresh contract
# guids per career run, so every literal in this block moved when
# `C2CareerPostFix` was re-harvested from run
# `2026-08-20_1925_L3-career-science-recover_run2`. The builder caught it loudly
# ("base carries no CONTRACT with guid ... - the harvest's contract set moved and
# this recipe must be re-derived against the new one"), which is that guard
# earning its keep. The SELECTION RULE below is what carried over; the values are
# read off the new save.
#
# WHICH CONTRACT, and why this one of the base's seven Offered rows:
#   - `PartTest` is inert - it can only advance when its part is activated
#     through the staging sequence.
#   - `Decoupler.1` (TD-12) IS NOT ON THE PAD CRAFT. The craft carries
#     mk1pod.v2 / parachuteSingle / 2x GooExperiment / solidBooster.sm.v2 /
#     3x basicFin / SurfAntenna / 2x batteryPack, so this contract's test can
#     NEVER be performed by the vessel the batch focuses. That rules out the new
#     set's `375b4446...`, whose subject is `solidBooster.sm.v2` - i.e. the part
#     the parked craft carries.
#   - Its `sit = ESCAPING` is unreachable from a craft parked PRELAUNCH on the
#     pad, so even the situation parameter cannot complete. This is a STRONGER
#     second guard than the previous harvest's `sit = LANDED` and stronger than
#     the remaining alternative `8a2b7d40...` (`solidBooster.v2` at
#     `sit = FLYING`), whose situation a launched craft could in principle reach
#     even though its part is absent.
# A contract that completes or fails mid-batch would leave the cell's quicksave
# reading `saveActive=0` and red the run on a fixture artifact.
#
# THE TITLE IS DERIVED, NOT INVENTED. KSP generates PartTest titles at runtime
# and the save carries none, so the accept row's `contractTitle` is authored
# here - but it is composed from the shipped dictionary rather than guessed:
# `#autoLOC_6100005 = Test <<1>> <<2>>.` with <<1>> the part title
# (`#autoLOC_501784 = TD-12 Decoupler`) and <<2>> the TEST-direction escape
# phrase (`#autoLOC_6100020 = on an escape trajectory out of <<1>>`; the
# `into ...` sibling 6100019 is the HAUL direction). Cosmetic either way -
# `LedgerGroundTruthDiff.CompareContracts` matches on GUID alone - but a
# fixture that states a title should state the right one.
ACTIVE_CONTRACT_GUID = "07c8e34d-0464-4416-a973-1e2b472bc347"
ACTIVE_CONTRACT_TYPE = "PartTest"
ACTIVE_CONTRACT_PART = "Decoupler.1"
ACTIVE_CONTRACT_TITLE = 'Test TD-12 Decoupler on an escape trajectory out of Kerbin.'

# The accept UT. 360 sits in the KSC gap between the career's last ledger action
# (the recovery credit at 348.08) and its FLIGHTSTATE clock (409.56, which is
# also where the splice puts the craft's rollout), so the story the fixture tells
# is the ordinary one: land, recover, accept a contract at Mission Control, roll
# out. It is NOT a free choice in one direction - see TRAP 2.
CONTRACT_ACCEPT_UT = "360"
CONTRACT_ACCEPT_ACTION_ID = "act_5c1a7f0b6d3e42a9b8f04e17c92d6a35"
CONTRACT_ACCEPT_SEQ = "2"

# The KSP-side `values` pack is a 12-float CSV whose layout is read off the c2
# snapshot's own accepted-then-completed PartTest row (its [9] is byte-identical
# to that contract's ledger accept UT and its [10] is [9] + [1]):
#   [0] expiry duration   [1] deadline duration  [2] advance funds
#   [3] completion funds  [4] failure funds      [5] science completion
#   [6] rep completion    [7] rep failure        [8] dateExpire
#   [9] dateAccepted      [10] dateDeadline      [11] dateFinished
# THREE SLOTS ARE RE-STAMPED on this harvest's contract, where the previous one
# needed six - the difference is not a change of recipe but of luck: this
# contract's [1] / [4] / [7] are ALREADY float-exact integers (9201600 / 27225.000590086
# rounds to 27225 / 12), so only one of the three coherence edits has any work to
# do. The traps are identical.
#   [2] 24750 -> 0                TRAP 1
#   [9] 0 -> 360                  the contract was accepted, so it has a date
#   [10] 0 -> 9201960             = [9] + [1], TRAP 2's save-side half
#   [4] 27225.000590086 -> 27225  the one number here the ledger row MIRRORS that
#                                 is not already exact, moved onto a float-exact
#                                 value so both sides carry the same literal text
#                                 rather than two roundings of one number.
#                                 ([1] = 9201600 and [7] = 12 need no such move.)
# The other slots are the harvest's own and are inert here.
BASE_CONTRACT_VALUES = ("21600,9201600,24750,"
                        "68062.501475215,27225.000590086,9,14.54545,12,"
                        "21615.14,0,0,0")
ACTIVE_CONTRACT_VALUES = ("21600,9201600,0,"
                          "68062.501475215,27225,9,14.54545,12,"
                          "21615.14,360,9201960,0")

# The ledger row's mirrors of [1] / [4] / [7]. `deadlineUT` is a DURATION and not
# an absolute date, which looks wrong and is not: the c2 accept row's
# `deadlineUT = 8228571.5` is float(values[1]) of the contract it accepted, not
# that contract's `dateDeadline`. Mirroring the recorder rather than correcting it
# is the whole point of a fixture-carried row.
CONTRACT_DEADLINE_UT = "9201600"
CONTRACT_FUNDS_PENALTY = "27225"
CONTRACT_REP_PENALTY = "12"

# The base's last KSC-scoped action (`FirstCrewToSurvive`) carries `seq = 1`, so
# the next KSC action is 2. Asserted rather than assumed - see `verify_ledger`.
LEDGER_LAST_KSC_SEQ = 1

# Harvest exhaust the fixture must not carry (same cell, first half).
PRUNED_PARSEK_SUBDIRS = ("Saves",)


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def build(base_lines: List[str], donor_lines: List[str],
          crew_name: str, title: str) -> List[str]:
    """Return the spliced save. Pure over the two input line lists."""
    base = list(base_lines)

    # ---- the donor's single Ship vessel, extracted and re-stamped ----
    donor_fs = find_node(donor_lines, "FLIGHTSTATE")
    if donor_fs is None:
        raise SystemExit("donor has no FLIGHTSTATE node")
    donor_vessels = child_nodes(donor_lines, donor_fs, "VESSEL")
    ships = [v for v in donor_vessels
             if get_value(donor_lines, v, "type") == "Ship"]
    if len(ships) != 1:
        raise SystemExit("expected exactly one type=Ship VESSEL in the donor, got %d"
                         % len(ships))
    vessel = list(donor_lines[ships[0][0]:ships[0][1]])
    vessel_node = (0, len(vessel))

    if get_value(vessel, vessel_node, "pid") != RECORDED_VESSEL_GUID:
        raise SystemExit(
            "donor vessel pid is %r, expected the recorded launch's %r - the "
            "re-stamp below is sized against that identity, so an unexpected "
            "one means the donor moved and this recipe must be re-derived"
            % (get_value(vessel, vessel_node, "pid"), RECORDED_VESSEL_GUID))
    if get_value(vessel, vessel_node, "persistentId") != RECORDED_VESSEL_PERSISTENT_ID:
        raise SystemExit(
            "donor vessel persistentId is %r, expected %r (see above)"
            % (get_value(vessel, vessel_node, "persistentId"),
               RECORDED_VESSEL_PERSISTENT_ID))

    if not set_value(vessel, vessel_node, "pid", NEW_VESSEL_GUID):
        raise SystemExit("could not re-stamp the vessel pid")
    if not set_value(vessel, vessel_node, "persistentId", NEW_VESSEL_PERSISTENT_ID):
        raise SystemExit("could not re-stamp the vessel persistentId")

    # ---- insert into the BASE's own FLIGHTSTATE, keeping its clock ----
    base_fs = find_node(base, "FLIGHTSTATE")
    if base_fs is None:
        raise SystemExit("base has no FLIGHTSTATE node")
    if child_nodes(base, base_fs, "VESSEL"):
        raise SystemExit("base FLIGHTSTATE already carries a VESSEL - this recipe "
                         "assumes the recovered-craft shape and would double it")
    base_ut = get_value(base, base_fs, "UT")
    if not base_ut:
        raise SystemExit("base FLIGHTSTATE has no UT")

    # The craft rolls out NOW, not at the donor's launch time. Return values are
    # CHECKED, like the two re-stamps above: `verify` compares the FLIGHTSTATE UT
    # against the recordings, not the vessel's own clock, so a donor that had lost
    # either key would silently keep its launch time and nothing downstream would
    # notice.
    for key in ("lct", "lastUT"):
        if not set_value(vessel, vessel_node, key, base_ut):
            raise SystemExit(
                "donor vessel has no %r key to move onto the career's clock - the "
                "donor shape changed and this recipe must be re-derived" % key)

    # Indent the donor's lines are already at (FLIGHTSTATE child depth) matches
    # the base's, both being GAME > FLIGHTSTATE > VESSEL, so the block is
    # inserted verbatim before the FLIGHTSTATE close brace.
    insert_at = base_fs[1] - 1
    base[insert_at:insert_at] = vessel

    base_fs = find_node(base, "FLIGHTSTATE")
    if len(child_nodes(base, base_fs, "VESSEL")) != 1:
        raise SystemExit("post-splice vessel count is not 1")
    if not set_value(base, base_fs, "activeVessel", "0"):
        raise SystemExit("could not set activeVessel")

    # ---- strip the rewind-to-LAUNCH hints (the payload is not committed) ----
    base = [line for line in base
            if not _REWIND_HINT_RE.match(line)]

    # ---- roster: flip the crew kerbal Available -> Assigned, nothing else ----
    roster = find_node(base, "ROSTER")
    if roster is None:
        raise SystemExit("base has no ROSTER node")
    kerbal = _kerbal_named(base, roster, crew_name)
    if kerbal is None:
        raise SystemExit("base roster has no %s" % crew_name)
    if not set_value(base, kerbal, "state", "Assigned"):
        raise SystemExit("could not set %s state" % crew_name)

    # ---- the D8 `contracts` cell: one Offered contract re-stated Active ----
    _restate_contract_as_active(base)

    set_top_value(base, "Title", title)
    return base


def contracts_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    """The `ContractSystem` SCENARIO's own `CONTRACTS` node, or None.

    SCOPED through the scenario rather than found by a bare `find_node`, for the
    same reason `child_nodes` is depth-scoped: `CONTRACTS` is a common enough
    node name that a bare scan is a bet on nothing else ever using it."""
    scn = _scenario_node(lines, "ContractSystem")
    if scn is None:
        return None
    return find_node(lines, "CONTRACTS", scn[0], scn[1])


def contract_named(lines: List[str], guid: str) -> Optional[Tuple[int, int]]:
    """The direct `CONTRACT` child of `CONTRACTS` carrying ``guid``, or None."""
    node = contracts_node(lines)
    if node is None:
        return None
    for contract in child_nodes(lines, node, "CONTRACT"):
        if get_value(lines, contract, "guid") == guid:
            return contract
    return None


def active_contract_guids(lines: List[str]) -> List[str]:
    """Every `CONTRACT` guid whose `state` is `Active`, in file order.

    Mirrors `CareerSaveParser.ParseContracts`: `CONTRACT` nodes only (a
    `CONTRACT_FINISHED` node is a different name and is not counted) and an
    ORDINAL `Active` match on the `state` value."""
    node = contracts_node(lines)
    if node is None:
        return []
    out: List[str] = []
    for contract in child_nodes(lines, node, "CONTRACT"):
        if get_value(lines, contract, "state") == "Active":
            guid = get_value(lines, contract, "guid")
            if guid:
                out.append(guid)
    return out


def _restate_contract_as_active(lines: List[str]) -> None:
    """Re-state one committed `Offered` PartTest row as an `Active` contract.

    RE-STATED RATHER THAN ADDED, and the choice is load-bearing. Adding a second
    `CONTRACT` node would have to author a contract KSP has never loaded from
    this save, and the only way to make one that is certain to load is to clone an
    existing node - which duplicates that node's `part`, `seed` and parameter
    `uniqueID` and produces a save state stock's own generator never emits (two
    live PartTest contracts for one part, under `repeatability = ONCEPERPART`).
    The node below already loads in this exact save today, on the exact instance
    profile the spec flies. Its guid is therefore deterministic in the strongest
    sense available: it is committed, not generated.

    The base shape is ASSERTED before the edit rather than pattern-matched, so a
    re-harvest that moved this contract reds here naming what moved, instead of
    silently producing a fixture whose Active row is somebody else's."""
    contract = contract_named(lines, ACTIVE_CONTRACT_GUID)
    if contract is None:
        raise SystemExit(
            "base carries no CONTRACT with guid %r - the harvest's contract set "
            "moved and this recipe must be re-derived against the new one"
            % ACTIVE_CONTRACT_GUID)

    for key, expected in (("type", ACTIVE_CONTRACT_TYPE),
                          ("part", ACTIVE_CONTRACT_PART),
                          ("state", "Offered"),
                          ("values", BASE_CONTRACT_VALUES)):
        got = get_value(lines, contract, key)
        if got != expected:
            raise SystemExit(
                "base CONTRACT %s has %s = %r, expected %r - the harvest moved "
                "and the re-stamp below is sized against the expected shape"
                % (ACTIVE_CONTRACT_GUID, key, got, expected))

    if len(active_contract_guids(lines)) != 0:
        raise SystemExit(
            "base already carries an Active contract - this recipe assumes the "
            "all-Offered shape and would produce two")

    for key, value in (("state", "Active"),
                       # An accepted contract has necessarily been read.
                       ("viewed", "Read"),
                       ("values", ACTIVE_CONTRACT_VALUES)):
        if not set_value(lines, contract, key, value):
            raise SystemExit("could not set CONTRACT %s" % key)


def _kerbal_named(lines: List[str], roster: Tuple[int, int],
                  name: str) -> Optional[Tuple[int, int]]:
    for kerbal in child_nodes(lines, roster, "KERBAL"):
        if get_value(lines, kerbal, "name") == name:
            return kerbal
    return None


def build_loadmeta(base_meta: List[str], lines: List[str]) -> List[str]:
    """Restamp the base loadmeta's vessel count from the spliced save.

    The UT is NOT restamped, unlike `build_career_pad_craft.build_loadmeta`:
    there the spliced FLIGHTSTATE brought a new clock with it, here the base's
    own clock is kept, so the base loadmeta's UT is already correct. Every other
    field (gameMode, the pool readouts, the timestamp) stays as the harvest
    wrote it: the splice moves no pool."""
    out = list(base_meta)
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        raise SystemExit("spliced save has no FLIGHTSTATE")
    count = len(child_nodes(lines, fs, "VESSEL"))
    for i, line in enumerate(out):
        if line.startswith("vesselCount = "):
            out[i] = "vesselCount = %d" % count
    return out


# ---------------------------------------------------------------------------
# The ledger half of the D8 `contracts` cell.
# ---------------------------------------------------------------------------


# The appended row, field for field off the c2 snapshot's real accept row
# (`c2-snapshot-20260817/Parsek/GameState/ledger.pgld`): `ut`, `type`, `actionId`,
# `seq`, then the five contract fields, with NO `recordingId` because a contract
# is accepted at Mission Control and not inside a flight. The key SET is not a
# style choice either - `GameAction.SerializeContractAccept` writes `advanceFunds`
# unconditionally and the other four only when non-default, so this is exactly
# what Parsek would have written for this action.
#
# THE THREE ARITHMETIC TRAPS, each of which corrupts a DIFFERENT already-armed
# facet if it is got wrong:
#
#   TRAP 1 - `advanceFunds` MUST be 0. `FundsModule.ProcessContractAccept`
#   credits the advance to the running balance unconditionally (its only guard is
#   `advance <= 0.0 -> return`), and the funds pool is HARD-gated in this cell
#   rather than report-only. A nonzero advance would move the reconstruction off
#   the save's 536558 and red the run on the pool facet, which reads exactly like
#   a product defect. The contract node's own `values[2]` is re-stamped to 0 for
#   the same reason, so the two sides tell one story.
#
#   TRAP 2 - the deadline MUST NOT have elapsed. `ContractsModule.PrePass` scans
#   every accept with a non-NaN deadline and INJECTS a synthetic `ContractFail`
#   at the deadline UT once `HasContractDeadlineElapsed(nowUT, deadline)`, where
#   `nowUT` falls back to the last surviving action's UT; `ProcessAction` then
#   re-checks via `CheckDeadlines(action.UT)` on every single action. Either path
#   empties the active set and re-vacuifies the compare - and the injected fail
#   would ALSO apply `fundsPenalty` + `repPenalty`, moving two hard-gated pools.
#   8680754 against a walk whose largest UT is this row's own 360 is roughly four
#   orders of magnitude of margin. (Omitting the key entirely is the other legal
#   answer - `DeserializeContractAccept` defaults a missing `deadlineUT` to NaN
#   and NaN deadlines never expire - but c2's real row carries one, so this one
#   does too.)
#
#   TRAP 3 - NO `type = 6` (`ContractComplete`) ROW. A completion would remove the
#   id from `activeContracts` (the slot is freed regardless of effectiveness),
#   putting `reconActive` back to 0 and making the compare vacuous again - the
#   exact condition this cell exists to end. It would also move THREE hard-gated
#   pools at once through `fundsReward` / `repReward` / `scienceReward`. The
#   accepted-and-unresolved state is the one that says something.
CONTRACT_ACCEPT_ROW = [
    "GAME_ACTION",
    "{",
    "\tut = %s" % CONTRACT_ACCEPT_UT,
    "\ttype = 5",
    "\tactionId = %s" % CONTRACT_ACCEPT_ACTION_ID,
    "\tseq = %s" % CONTRACT_ACCEPT_SEQ,
    "\tcontractId = %s" % ACTIVE_CONTRACT_GUID,
    "\tcontractType = %s" % ACTIVE_CONTRACT_TYPE,
    "\tcontractTitle = %s" % ACTIVE_CONTRACT_TITLE,
    "\tadvanceFunds = 0",
    "\tdeadlineUT = %s" % CONTRACT_DEADLINE_UT,
    "\tfundsPenalty = %s" % CONTRACT_FUNDS_PENALTY,
    "\trepPenalty = %s" % CONTRACT_REP_PENALTY,
    "}",
]


def build_ledger(base_ledger: List[str]) -> List[str]:
    """Return the base `ledger.pgld` with the accept row APPENDED.

    Appended rather than UT-ordered on purpose: the committed ledger is not
    sorted by UT (its two `ut = 0` seed rows sit fifth and ninth), because
    `Ledger` writes in list order and the engine sorts on read. Appending is
    what a later KSC action actually produces."""
    lines = list(base_ledger)
    if not lines or lines[-1] != "":
        raise SystemExit("base ledger does not end with a trailing newline - "
                         "the append below would join two lines")
    if _count_game_action_rows(lines, "type = 5") != 0:
        raise SystemExit("base ledger already carries a ContractAccept row - "
                         "this recipe assumes the contract-free career")

    # The KSC-scoped sequence the appended row continues. Checked rather than
    # assumed: `LedgerOrchestrator.AllocateKscSequence` hands out 1, 2, 3 ... to
    # actions with no `recordingId`, so a base whose highest one moved would make
    # the appended `seq = 2` a duplicate rather than the next value.
    highest = 0
    i = 0
    while True:
        node = find_node(lines, "GAME_ACTION", i)
        if node is None:
            break
        if get_value(lines, node, "recordingId") is None:
            seq = get_value(lines, node, "seq")
            if seq is not None:
                highest = max(highest, int(seq))
        i = node[1]
    if highest != LEDGER_LAST_KSC_SEQ:
        raise SystemExit(
            "base ledger's highest KSC-scoped seq is %d, expected %d - the "
            "appended row's seq = %s is sized against that and would now collide"
            % (highest, LEDGER_LAST_KSC_SEQ, CONTRACT_ACCEPT_SEQ))

    lines[len(lines) - 1:len(lines) - 1] = CONTRACT_ACCEPT_ROW
    return lines


def _count_game_action_rows(lines: List[str], type_line: str) -> int:
    """How many top-level `GAME_ACTION` blocks carry ``type_line``."""
    count = 0
    i = 0
    while True:
        node = find_node(lines, "GAME_ACTION", i)
        if node is None:
            return count
        if contains_line(lines, node, type_line):
            count += 1
        i = node[1]


def verify_ledger(lines: List[str]) -> List[str]:
    """Return a list of failure strings for the produced/committed ledger."""
    problems: List[str] = []

    accepts = []
    i = 0
    while True:
        node = find_node(lines, "GAME_ACTION", i)
        if node is None:
            break
        if contains_line(lines, node, "type = 5"):
            accepts.append(node)
        if contains_line(lines, node, "type = 6"):
            # TRAP 3, asserted rather than merely documented.
            problems.append(
                "ledger carries a ContractComplete (type = 6) row: the completion "
                "frees the contract's slot, so reconActive returns to 0 and "
                "CompareContracts is vacuous again - and three hard-gated pools move")
        i = node[1]

    if len(accepts) != 1:
        problems.append("expected exactly 1 ContractAccept (type = 5) row, found %d"
                        % len(accepts))
        return problems
    accept = accepts[0]

    for key, expected in (("contractId", ACTIVE_CONTRACT_GUID),
                          ("contractType", ACTIVE_CONTRACT_TYPE),
                          ("contractTitle", ACTIVE_CONTRACT_TITLE),
                          ("actionId", CONTRACT_ACCEPT_ACTION_ID),
                          ("ut", CONTRACT_ACCEPT_UT),
                          ("seq", CONTRACT_ACCEPT_SEQ)):
        got = get_value(lines, accept, key)
        if got != expected:
            problems.append("accept row %s is %r, expected %r" % (key, got, expected))

    # TRAP 1.
    advance = get_value(lines, accept, "advanceFunds")
    if advance is None or float(advance) != 0.0:
        problems.append(
            "accept row advanceFunds is %r, expected '0': FundsModule credits any "
            "positive advance to the running balance, which would move the "
            "HARD-gated funds pool off the save's %s" % (advance, EXPECT_FUNDS))

    # TRAP 2. The margin is stated against the walk's own largest UT, which after
    # the append is this row's, rather than against the FLIGHTSTATE clock: PrePass
    # and CheckDeadlines both compare against action UTs, not against "now".
    deadline = get_value(lines, accept, "deadlineUT")
    if deadline is None:
        problems.append("accept row has no deadlineUT (a missing key is legal - it "
                        "reads back NaN - but this recipe writes one, so its "
                        "absence means the row was rewritten)")
    elif float(deadline) <= float(CONTRACT_ACCEPT_UT):
        problems.append(
            "accept row deadlineUT %r has already elapsed at the accept UT %s: "
            "ContractsModule.PrePass would inject a synthetic ContractFail, "
            "emptying the active set AND applying fundsPenalty/repPenalty to two "
            "hard-gated pools" % (deadline, CONTRACT_ACCEPT_UT))

    if get_value(lines, accept, "recordingId") is not None:
        problems.append("accept row carries a recordingId: a contract is accepted "
                        "at Mission Control, and c2's real row carries none")
    return problems


# ---------------------------------------------------------------------------
# Post-conditions. Run on both the freshly built save and on --check.
# ---------------------------------------------------------------------------


def verify(lines: List[str], crew_name: str) -> List[str]:
    """Return a list of failure strings (empty = every post-condition holds)."""
    problems: List[str] = []

    mode = None
    for line in lines:
        if line.startswith("\tMode = "):
            mode = line.split("=", 1)[1].strip()
            break
    if mode != EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r" % (mode, EXPECT_MODE))

    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        problems.append("no FLIGHTSTATE node")
        return problems
    vessels = child_nodes(lines, fs, "VESSEL")
    if len(vessels) != 1:
        problems.append("expected exactly 1 VESSEL, found %d" % len(vessels))
        return problems
    ship = vessels[0]

    if get_value(lines, ship, "sit") != EXPECT_SITUATION:
        problems.append("vessel sit is %r, expected %r"
                        % (get_value(lines, ship, "sit"), EXPECT_SITUATION))
    if get_value(lines, fs, "activeVessel") != "0":
        problems.append("activeVessel is %r, expected '0'"
                        % get_value(lines, fs, "activeVessel"))
    if not contains_line(lines, ship, "crew = %s" % crew_name):
        problems.append("no `crew = %s` line inside the vessel" % crew_name)

    # ---- THE COLLISION GUARDS, and they are the reason this builder exists ----
    #
    # `LedgerGroundTruthDiff.CompareRecovery` correlates every recovery credit
    # against the save's live vessels: a guid match is an ALWAYS-HARD consistency
    # divergence, a pid-only match is report-only and therefore promoted by the
    # strict mode this fixture is built to arm. The donor craft IS the recovered
    # one, so both stamps must differ from every recorded identity in the save.
    vessel_guid = get_value(lines, ship, "pid")
    vessel_pid = get_value(lines, ship, "persistentId")
    recorded_guids = _values_named(lines, "recordedVesselGuid")
    recorded_pids = _values_named(lines, "vesselPersistentId")
    if not recorded_guids:
        problems.append("no recordedVesselGuid in the save: the career payload "
                        "(the committed RECORDING_TREE) did not survive the splice")
    if vessel_guid in recorded_guids:
        problems.append(
            "spliced vessel pid %r collides with a recording's recordedVesselGuid: "
            "CompareRecovery would raise a guid-corroborated ALWAYS-HARD "
            "consistency divergence" % vessel_guid)
    if vessel_pid in recorded_pids:
        problems.append(
            "spliced vessel persistentId %r collides with a recording's "
            "vesselPersistentId: CompareRecovery would raise a pid-only "
            "consistency divergence, which strict promotes to hard" % vessel_pid)

    dangling = [i for i, line in enumerate(lines, 1) if _REWIND_HINT_RE.match(line)]
    if dangling:
        problems.append(
            "a rewindSave = parsek_rw_* hint survived at line(s) %s: the payload is "
            "not committed, so Inv9RewindPoint would WARN on a dangling reference "
            "(and FAIL on a CommittedProvisional owner, reddening the analyzer under "
            "the harness Forbid gate)" % (dangling,))

    # The clock must still be the career's own, not the donor's.
    ut = get_value(lines, fs, "UT")
    end_uts = [float(v) for v in _values_named(lines, "explicitEndUT")]
    if not end_uts:
        problems.append("no explicitEndUT in the save: no recording survived")
    elif ut is None or float(ut) < max(end_uts):
        problems.append("FLIGHTSTATE UT %r is before the last recording's "
                        "explicitEndUT %r: the donor's clock replaced the career's"
                        % (ut, max(end_uts)))

    # ---- the career payload ----
    scn = _scenario_node(lines, "ParsekScenario")
    if scn is None:
        problems.append("no ParsekScenario SCENARIO node: the career payload is gone")
    elif not contains_line(lines, scn, "RECORDING_TREE"):
        problems.append("ParsekScenario carries no RECORDING_TREE: the recovery "
                        "credit would resolve no recording")
    elif contains_line(lines, scn, "isActive = True"):
        problems.append("the committed tree is marked isActive: the in-game cell's "
                        "no-active-uncommitted-tree guard would Skip the batch")

    roster = find_node(lines, "ROSTER")
    if roster is None:
        problems.append("no ROSTER node")
    else:
        kerbal = _kerbal_named(lines, roster, crew_name)
        if kerbal is None:
            problems.append("roster has no %s" % crew_name)
        else:
            state = get_value(lines, kerbal, "state")
            if state != "Assigned":
                problems.append("roster %s state is %r, expected 'Assigned'"
                                % (crew_name, state))
            if get_value(lines, kerbal, "type") != "Crew":
                problems.append("roster %s is not type=Crew" % crew_name)
            # The KerbalXp facet's SAVE side. Its loss is what the roster edit
            # was deliberately narrowed to avoid.
            if not contains_line(lines, kerbal, EXPECT_CAREER_LOG_ENTRY):
                problems.append(
                    "roster %s lost its CAREER_LOG %r: the KerbalXp facet would "
                    "read PhantomInRecon, a fixture artifact strict promotes to hard"
                    % (crew_name, EXPECT_CAREER_LOG_ENTRY))

    # ---- THE D8 `contracts` CELL, save side ----
    #
    # EXACTLY ONE Active contract, and it is ours. Both halves matter: a zero
    # would make `CompareContracts` read `reconActive=1 saveActive=0` and raise a
    # `MissingInRecon` hard failure under strict, and a second one would put a
    # guid in the save's active set that no ledger row accepts - the same
    # divergence in the other direction.
    active = active_contract_guids(lines)
    if active != [ACTIVE_CONTRACT_GUID]:
        problems.append(
            "save's Active contract guids are %r, expected exactly [%r]"
            % (active, ACTIVE_CONTRACT_GUID))
    else:
        contract = contract_named(lines, ACTIVE_CONTRACT_GUID)
        got_values = get_value(lines, contract, "values")
        if got_values != ACTIVE_CONTRACT_VALUES:
            problems.append("Active CONTRACT values pack is %r, expected %r"
                            % (got_values, ACTIVE_CONTRACT_VALUES))
        else:
            pack = got_values.split(",")
            # TRAP 1's save-side half: the contract advertises no advance
            # payment, so the pools the save carries and the pools the ledger
            # reconstructs tell one story.
            if float(pack[2]) != 0.0:
                problems.append("Active CONTRACT advance funds (values[2]) is %r, "
                                "expected '0' to match the accept row" % pack[2])
            # TRAP 2's save-side half: the contract's own dateDeadline must sit
            # far past the career clock, or KSP fails it during the batch and the
            # cell's quicksave reads saveActive=0.
            if ut is not None and float(pack[10]) <= float(ut):
                problems.append(
                    "Active CONTRACT dateDeadline (values[10]) is %r, at or before "
                    "the career clock %r: KSP would fail the contract during the "
                    "batch" % (pack[10], ut))
            if float(pack[9]) != float(CONTRACT_ACCEPT_UT):
                problems.append("Active CONTRACT dateAccepted (values[9]) is %r, "
                                "expected the accept row's UT %s"
                                % (pack[9], CONTRACT_ACCEPT_UT))

    for node_name, key, expected in (("Funding", "funds", EXPECT_FUNDS),
                                     ("ResearchAndDevelopment", "sci", EXPECT_SCIENCE),
                                     ("Reputation", "rep", EXPECT_REPUTATION)):
        got = _scenario_value(lines, node_name, key)
        if got != expected:
            problems.append("%s.%s is %r, expected the EARNED %r"
                            % (node_name, key, got, expected))
    return problems


def _values_named(lines: List[str], key: str) -> List[str]:
    """Every `key = value` in the whole save, at any depth."""
    prefix = key + " ="
    out: List[str] = []
    for line in lines:
        s = line.strip()
        if s.startswith(prefix):
            out.append(s[len(prefix):].strip())
    return out


def _scenario_node(lines: List[str], scenario_name: str) -> Optional[Tuple[int, int]]:
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == scenario_name:
            return node
        i = node[1]


def _scenario_value(lines: List[str], scenario_name: str, key: str) -> Optional[str]:
    node = _scenario_node(lines, scenario_name)
    return get_value(lines, node, key) if node is not None else None


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of writing it")
    parser.add_argument("--crew", default=CREW_NAME)
    parser.add_argument("--target-name", default=TARGET_NAME)
    args = parser.parse_args(argv)

    target_dir = os.path.join(_SAVES, args.target_name)
    target_sfs = os.path.join(target_dir, "persistent.sfs")

    target_ledger = os.path.join(target_dir, "Parsek", "GameState", "ledger.pgld")

    if args.check:
        if not os.path.isfile(target_sfs):
            print("FAIL: %s does not exist" % target_sfs)
            return 1
        if not os.path.isfile(target_ledger):
            print("FAIL: %s does not exist" % target_ledger)
            return 1
        problems = verify(read_lines(target_sfs), args.crew)
        problems += verify_ledger(read_lines(target_ledger))
        for p in problems:
            print("FAIL: %s" % p)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    donor_dir = os.path.join(_SAVES, DONOR_NAME)
    for d in (BASE_DIR, donor_dir):
        if not os.path.isdir(d):
            print("FAIL: missing input fixture %s" % d)
            return 1

    base_lines = read_lines(os.path.join(BASE_DIR, "persistent.sfs"))
    donor_lines = read_lines(os.path.join(donor_dir, "persistent.sfs"))
    title = "%s (CAREER)" % args.target_name
    built = build(base_lines, donor_lines, args.crew, title)

    problems = verify(built, args.crew)
    if problems:
        for p in problems:
            print("FAIL: %s" % p)
        return 1

    os.makedirs(target_dir, exist_ok=True)
    write_lines(target_sfs, built)
    write_lines(os.path.join(target_dir, "persistent.loadmeta"),
                build_loadmeta(read_lines(os.path.join(BASE_DIR, "persistent.loadmeta")),
                               built))

    # The career payload: recordings, ledger, GameState baselines. Copied from
    # the BASE, because that is the half this fixture exists to carry.
    parsek_src = os.path.join(BASE_DIR, "Parsek")
    parsek_dst = os.path.join(target_dir, "Parsek")
    if not os.path.isdir(parsek_src):
        print("FAIL: base carries no Parsek/ sidecar directory")
        return 1
    shutil.rmtree(parsek_dst, ignore_errors=True)
    # `Saves/` is Rewind-to-LAUNCH harvest exhaust: nothing automated reads it and
    # CommittedFixtureRewindSaveTests forbids committing it. Not copying is the
    # whole of the prune - no fixture file is ever removed.
    shutil.copytree(parsek_src, parsek_dst,
                    ignore=shutil.ignore_patterns(*PRUNED_PARSEK_SUBDIRS))

    # The ledger half of the D8 `contracts` cell. Written AFTER the copy rather
    # than into the base, because the base is the xUnit fixture
    # `C2CareerPostFix` - the one committed copy of what a real harness run
    # produced - and `C2CareerPostFixReplayTests` makes its own closes-to-zero
    # claim about those exact bytes. A harness-side need never edits a harvest.
    base_ledger = os.path.join(BASE_DIR, "Parsek", "GameState", "ledger.pgld")
    if not os.path.isfile(base_ledger):
        print("FAIL: base carries no Parsek/GameState/ledger.pgld")
        return 1
    built_ledger = build_ledger(read_lines(base_ledger))
    ledger_problems = verify_ledger(built_ledger)
    if ledger_problems:
        for p in ledger_problems:
            print("FAIL: %s" % p)
        return 1
    write_lines(os.path.join(parsek_dst, "GameState", "ledger.pgld"), built_ledger)

    # POST-CONDITION ON THE PAYLOAD, checked here rather than in `verify` because
    # `verify` is pure over the save TEXT and this is a file-tree fact. Every
    # `.prec` needs its readable `.prec.txt` mirror (a live test input
    # OptimizerTransferCohesionTests globs), and no `.craft.txt` snapshot mirror
    # may come along (regenerable, and forbidden by the same test class).
    recordings = os.path.join(parsek_dst, "Recordings")
    names = os.listdir(recordings) if os.path.isdir(recordings) else []
    missing = sorted(n for n in names
                     if n.endswith(".prec") and (n + ".txt") not in names)
    snapshots = sorted(n for n in names
                       if n.endswith(("_vessel.craft.txt", "_ghost.craft.txt")))
    if missing or snapshots:
        if missing:
            print("FAIL: these trajectories have no .prec.txt mirror (add them to "
                  "the BASE fixture, which is where the derivation reads them): %s"
                  % (missing,))
        if snapshots:
            print("FAIL: the base carries forbidden snapshot mirrors: %s" % (snapshots,))
        return 1

    # AddOns comes from the DONOR: it is the harness-instance mod settings every
    # committed harness fixture carries, and the xUnit base has none.
    addons_src = os.path.join(donor_dir, "AddOns")
    addons_dst = os.path.join(target_dir, "AddOns")
    if os.path.isdir(addons_src):
        shutil.rmtree(addons_dst, ignore_errors=True)
        shutil.copytree(addons_src, addons_dst)

    print("OK: wrote %s (base=C2CareerPostFix donor=%s crew=%s)"
          % (target_dir, DONOR_NAME, args.crew))
    return 0


if __name__ == "__main__":
    sys.exit(main())
