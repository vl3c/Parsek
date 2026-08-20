#!/usr/bin/env python3
"""Build the `career-contract-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. Parsek's contract state machine has four transitions and only
ONE of them had ever fired under a harness gate. `L4-ledger-groundtruth-strict`
carries a fixture-spliced `type = 5` (`ContractAccept`) row and gates the
ACCEPTED-AND-UNRESOLVED state; `ContractComplete`, `ContractFail` and
`ContractCancel` had no committed gate at all, and neither did
`ContractsModule.PrePass`'s synthetic-fail injection - the branch that turns an
elapsed deadline into a real terminal row with real penalties.

THIS FIXTURE CLOSES THE FAIL SIDE, and it closes it ENTIRELY INSIDE THE LEDGER.
It carries TWO fixture-authored accept rows and NO fixture-authored terminal row:

  A  a long-deadline accept that stays ACTIVE for the whole walk. It gates
     `ProcessAccept` and the slot reservation, and it is the control - if A ever
     stops reading `Accept:`, the sidecar itself stopped loading.
  B  an ELAPSED-deadline accept. Nothing else. The transition under test is
     PRODUCED BY THE CODE rather than carried by the fixture: once the walk's
     `nowUT` passes B's deadline, `ContractsModule.PrePass` SYNTHESIZES a
     `ContractFail` at the deadline UT, `CheckDeadlines` retires B, and
     `ProcessFail` applies the penalties. Three log lines, none of which any
     committed spec had ever required.

WHY THE COMPLETE SIDE IS NOT HERE, and it is a measurement rather than a
preference. The first build of this fixture spliced a `state = Active` `PartTest`
CONTRACT into the save so the flight's launch staging would complete it live.
Run `2026-08-20_2217_L5-career-contract-complete` flew MISSION-OK with every
verifier green and the completion NEVER FIRED: the contract was gone from
`ContractSystem` before the mission's first frame, and stock re-OFFERED a fresh
contract with the identical subject 8 s later.

The same run proved WHY, and the proof is a decompiled guard rather than a
guess. `KSPAchievements.FirstLaunch.TestFlight` ends with

    CrewSensitiveComplete(v);
    if (!base.IsComplete) { Complete(); AwardProgressStandard(...); }

and `ProgressNode.Complete()` calls `Reach()` only when `!reached`, while
`ProgressNode.Load` sets `reached = true` UNCONDITIONALLY on its first line. The
flight logged BOTH `[Progress Node Reached]: FirstLaunch` and `[Progress Node
Complete]: FirstLaunch`, so `reached` and `complete` were both false at launch:
the `Progress { FirstLaunch { completedManned = 4 } }` node this builder had
spliced DID NOT RESTORE. `Contracts.Templates.PartTest.MeetRequirements()` reads
`ProgressTracking.NodeComplete("FirstLaunch")`, and `Contract.Update()` re-checks
it on EVERY tick of an ACTIVE contract, retiring it to `OfferExpired` - which is
removed outright rather than kept in `ContractsFinished`, matching the observed
`KSP has 0 current contracts, 0 finished contracts`.

The finding is NOT that ScenarioModule child nodes are lost: the same run's
produced save grew `ResearchAndDevelopment`'s `Tech` node from 13 `part =` lines
to 23, so that module's children loaded and were written back. It is specific,
it is filed as SAVE-AUTHORED-PROGRESS-NODE-DOES-NOT-RESTORE in
`docs/dev/todo-and-known-bugs.md`, and until it is explained a save-authored
Active `PartTest` cannot be made to survive in this lineage. Guessing at a
second shape would be a second flight spent on a hypothesis; the ledger-side
gate below needs no hypothesis at all, because `PrePass` is pure arithmetic over
rows this file authors.

WHAT IT SPLICES. ONE edit against `career-science-pad`'s save - the `Title` - and
ONE new sidecar. The single `VESSEL` node is carried BYTE FOR BYTE, which is what
lets `L5-career-contract-complete` reuse every one of `L3`'s flight-leg
parameters, and no pool, roster row or scenario node moves.

Usage:
    python harness/tools/build_career_contract_pad.py            # write it
    python harness/tools/build_career_contract_pad.py --check    # verify only

`--check` re-runs every post-condition against the ALREADY COMMITTED bytes. It is
WIRED, not decorative: `CareerContractPadFixtureDriftTests` in
`harness/lib/test_career_contract_pad.py` runs the same `verify` in-process AND
re-runs `build` over the CURRENT `career-science-pad` bytes, asserting
byte-identity, so a change to the donor reds in the suite instead of drifting
silently into a live flight.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import List, Optional

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text helpers and ONE copy of the donor's own
# post-conditions, for the reason both sibling builders already state: a second
# implementation is a second thing to drift.
import build_career_pad_craft as base_builder  # noqa: E402
import build_career_science_pad as donor_builder  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value
set_top_value = base_builder.set_top_value

FIXTURE_NAME = "career-contract-pad"
BASE_NAME = "career-science-pad"
TITLE = "career-contract-pad (CAREER)"

# ---------------------------------------------------------------------------
# The two accept rows.
# ---------------------------------------------------------------------------

# BOTH GUIDS ARE MINTED HERE, and unlike `build_career_earned_pad.py`'s they
# could not have been read off a harvest: `career-science-pad` carries an EMPTY
# `CONTRACTS` node, so there is no committed contract to re-state. That is safe
# because nothing in this recipe asks KSP to LOAD a contract - these rows exist
# purely as ledger identities, and `ContractsModule` keys on the string.
CONTRACT_A_GUID = "5f2c1b84-93ae-4d07-b6c1-0e8a4d51f3b9"
CONTRACT_B_GUID = "c47d0a91-6b25-4e83-9f1a-2d60be3c7845"
CONTRACT_TYPE = "PartTest"

# THE TITLES ARE DERIVED, NOT INVENTED, exactly as the earned-pad recipe derives
# its own: `#autoLOC_6100005 = Test <<1>> <<2>>.` with <<1>> the part title and
# <<2>> the TEST-direction phrase (`#autoLOC_6100022 = at the Launch Site`,
# `#autoLOC_6100024 = in flight over <<g:2,1>>`). Cosmetic - nothing matches on
# a title - but a fixture that states one should state the right one.
CONTRACT_A_TITLE = 'Test RT-5 "Flea" Solid Fuel Booster at the Launch Site.'
CONTRACT_B_TITLE = 'Test RT-10 "Hammer" Solid Fuel Booster in flight over Kerbin.'

# THE ACCEPT UTs, and neither is a free choice. `Ledger.Reconcile` prunes any
# contract-lifecycle row whose `UT` exceeds the save clock on cold load, so both
# must sit below this fixture's `UT = 9.0599999999998957`. The story they tell is
# the ordinary one: two contracts accepted at Mission Control, then this craft
# rolled out at 9.06.
CONTRACT_A_UT = "5"
CONTRACT_B_UT = "6"
SAVE_CLOCK_UT = 9.0599999999998957

CONTRACT_A_ACTION_ID = "act_3b7e15d0c4a9425f8ad612e70f9c4b83"
CONTRACT_B_ACTION_ID = "act_9d24c8be71f04a6cb35e802917dcae64"

# `LedgerOrchestrator.AllocateKscSequence` hands out 1, 2, 3 ... to actions
# carrying no `recordingId`, and these two are the only such actions in the file,
# so 1 and 2 are the next values rather than a guess. `verify` asserts the file
# holds exactly two rows, which is what keeps that true.
CONTRACT_A_SEQ = "1"
CONTRACT_B_SEQ = "2"

# A's DEADLINE MUST NOT ELAPSE, and B's MUST. Both are the DURATION form the
# recorder writes: `GameStateRecorder`'s accept handler records
# `Contract.TimeDeadline`, i.e. float(values[1]) of the stock contract, not its
# absolute `dateDeadline`. Mirroring the recorder rather than correcting it is
# the whole point of a fixture-carried row.
#
# THE TWO NUMBERS ARE SIZED AGAINST THE SAME CLOCK, the walk's `nowUT`, which
# `ContractsModule.PrePass` takes from the LAST SURVIVING ACTION's UT when no
# rewind cutoff is in play:
#
#   A = 9201600. Four orders of magnitude past the ~350 s this flight spans, so
#       `HasContractDeadlineElapsed` is false on every walk and A stays ACTIVE
#       from load to commit. A is the CONTROL: `Accept:` reading for A is what
#       says the sidecar loaded at all.
#
#   B = 100. Deliberately BETWEEN the two clocks this fixture is walked against.
#       On the COLD-LOAD walk the largest UT is B's own 6, so 100 has not
#       elapsed and B loads ACTIVE alongside A - `activeSlots=2/2`. By the
#       COMMIT-time walk the flight has written milestone, science and recovery
#       rows out to ~348, so `nowUT` passes 100 and PrePass injects. Choosing a
#       deadline BELOW the load clock would have retired B before it was ever
#       active and measured nothing; choosing one above ~348 would never fire.
CONTRACT_A_DEADLINE_UT = "9201600"
CONTRACT_B_DEADLINE_UT = "100"

# The penalty pack the synthetic fail applies. `FundsModule` and the reputation
# module subtract these from the RECONSTRUCTION - and that is where they stop.
#
# MEASURED, run `2026-08-20_2240`: the walk really does spend the pack
# (`running=527558` against `live=536558` for funds, `running=0.99999749660491943`
# against `live=1.9999988079071045` for reputation), and `KspStatePatcher`'s
# guarded drawdown then refuses to write either pool back, so NEITHER LIVE POOL
# MOVES in this fixture's shape. That is the guard working as designed here: this
# save's stock `CONTRACTS` node is EMPTY by construction, so stock never debited
# the pools for B and the recalc reaches the patcher as a bare drawdown with no
# time-travel context. An earlier revision of this comment predicted the opposite
# ("these two numbers MOVE TWO REAL POOLS"); the flight refuted it. Full finding:
# SYNTHETIC-CONTRACT-FAIL-PENALTY-CLAMPED-BY-DRAWDOWN-GUARD in
# `docs/dev/todo-and-known-bugs.md`.
#
# SIZED TO KEEP BOTH POOLS POSITIVE ON A YOUNG CAREER - which is now a DEFENSIVE
# choice rather than a live constraint, since the guard clamps and no live pool
# reaches these numbers at all today. The sizing holds against a future change of
# guard POLICY: if the drawdown guard is ever taught to let a contract penalty
# through, this fixture must not be the run that first drives a pool negative.
# The funds figure is a real generated PartTest's `values[4]` (`FundsFailure`) off
# `375b4446-c861-4b4d-bf97-ef38407246a4` in
# `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`, and 9000 against a career that
# ends the flight near 536000 is comfortably inside the black. The reputation
# figure is NOT that contract's `values[7]` of 4: this career ends the flight at
# reputation 2, and a 4-point penalty would AIM the pool NEGATIVE - a state stock
# supports but that no committed run has ever exercised, which would put an
# unrelated first on the same flight as this one. 1 keeps the sizing safe and
# still moves the reconstruction, which is what the gate needs.
CONTRACT_FUNDS_PENALTY = "9000"
CONTRACT_A_REP_PENALTY = "4"
CONTRACT_B_REP_PENALTY = "1"

# TRAP 1, inherited verbatim from `build_career_earned_pad.py` because it is a
# fact about the module rather than about that fixture: `advanceFunds` MUST be 0.
# `FundsModule.ProcessContractAccept` credits an advance to the running balance
# unconditionally, so a nonzero one would move the funds pool off the save's
# 500000 before the flight even starts.
CONTRACT_ADVANCE_FUNDS = "0"


def _accept_row(ut, action_id, seq, guid, title, deadline, rep_penalty):
    """One `type = 5` row, field for field off `SerializeContractAccept`.

    The key ORDER is the writer's (`ut`, `type`, `actionId`, then `seq`, then the
    payload), and there is deliberately NO `recordingId`: a contract is accepted
    at Mission Control, not inside a flight, and `Ledger.Reconcile` prunes a
    contract-lifecycle row whose tag names a recording the save does not hold."""
    return [
        "GAME_ACTION",
        "{",
        "\tut = %s" % ut,
        "\ttype = 5",
        "\tactionId = %s" % action_id,
        "\tseq = %s" % seq,
        "\tcontractId = %s" % guid,
        "\tcontractType = %s" % CONTRACT_TYPE,
        "\tcontractTitle = %s" % title,
        "\tadvanceFunds = %s" % CONTRACT_ADVANCE_FUNDS,
        "\tdeadlineUT = %s" % deadline,
        "\tfundsPenalty = %s" % CONTRACT_FUNDS_PENALTY,
        "\trepPenalty = %s" % rep_penalty,
        "}",
    ]


# THERE IS NO `type = 6` AND NO `type = 7` ROW HERE, and their absence is the
# whole design. A fixture-carried terminal row would measure the fixture; the
# terminal state this spec gates has to be SYNTHESIZED by `ContractsModule` from
# B's elapsed deadline, or the run has proved nothing.
LEDGER_LINES = (
    [
        "version = 0",
        "recordingSchemaGeneration = 4",
    ]
    + _accept_row(CONTRACT_A_UT, CONTRACT_A_ACTION_ID, CONTRACT_A_SEQ,
                  CONTRACT_A_GUID, CONTRACT_A_TITLE, CONTRACT_A_DEADLINE_UT,
                  CONTRACT_A_REP_PENALTY)
    + _accept_row(CONTRACT_B_UT, CONTRACT_B_ACTION_ID, CONTRACT_B_SEQ,
                  CONTRACT_B_GUID, CONTRACT_B_TITLE, CONTRACT_B_DEADLINE_UT,
                  CONTRACT_B_REP_PENALTY)
    # `Ledger.SaveToFile` goes through `ConfigNode.Save`, which terminates the
    # last line; a file that did not would join its close brace to whatever a
    # later append wrote.
    + [""]
)

# The ledger's path inside the save, mirroring
# `RecordingPaths.BuildLedgerRelativePath()`.
LEDGER_RELATIVE_PATH = ("Parsek", "GameState", "ledger.pgld")

# `career-science-pad` carries no `Parsek/` tree at all, so this fixture's is
# built from nothing rather than copied. The sibling sidecars (`events.pgse`,
# `milestones.pgsm`, `baseline_*.pgsb`) are DELIBERATELY ABSENT: each loader logs
# a benign "starting fresh" line when its file is missing, and authoring an event
# store by hand would put a second unmeasured surface in the path of the one
# measurement this fixture exists to take. The consequence is that neither guid
# has a `GameStateStore` CONTRACT_SNAPSHOT, so `KspStatePatcher.PatchContracts`
# logs `no snapshot for contractId=... - skipping` per ledger-active contract and
# restores nothing into `ContractSystem`. That is expected and inert: this
# fixture makes no claim about KSP-side contract state.


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def build(base_lines: List[str], title: str) -> List[str]:
    """Return `career-science-pad`'s save with its Title restamped.

    ONE EDIT, and the shortness is the point. The first build of this fixture
    also spliced a `state = Active` CONTRACT and a `FirstLaunch` progress node
    into the save; run `2026-08-20_2217_L5-career-contract-complete` measured
    both as inert - see the module docstring. Everything this fixture now claims
    lives in the ledger sidecar, which that same run measured as loading
    correctly. THE QUOTE IS HISTORICAL: that build carried ONE accept row and
    logged `Loaded ledger ... actions=1, parseErrors=0` followed by `Accept:` for
    the spliced guid. The SHIPPED fixture carries TWO accept rows (see
    `LEDGER_LINES`), so the current expectation is `actions=2`."""
    lines = list(base_lines)
    if not set_top_value(lines, "Title", title):
        raise SystemExit("base save has no GAME-level Title line")
    return lines


def _scenario_node(lines: List[str], scenario_name: str):
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == scenario_name:
            return node
        i = node[1]


def _contracts_node(lines: List[str]):
    scn = _scenario_node(lines, "ContractSystem")
    if scn is None:
        return None
    return find_node(lines, "CONTRACTS", scn[0], scn[1])


def _ledger_rows(ledger_lines: List[str]):
    rows = []
    i = 0
    while True:
        node = find_node(ledger_lines, "GAME_ACTION", i)
        if node is None:
            return rows
        rows.append(node)
        i = node[1]


# ---------------------------------------------------------------------------
# Post-conditions.
# ---------------------------------------------------------------------------


def verify(lines: List[str], base_lines: Optional[List[str]] = None,
           ledger_lines: Optional[List[str]] = None) -> List[str]:
    """Every post-condition, layered: the donor's first, then this file's.

    `base_lines` is `career-science-pad`'s save, and it is passed to
    `donor_builder.verify` as None ON PURPOSE: that parameter means
    `career-PAD-CRAFT` there, i.e. the donor's OWN base, and handing it this
    fixture's base would assert an 8-part craft against an 11-part one. The
    additive-splice promise that cell makes is re-made below against the right
    base instead."""
    problems: List[str] = donor_builder.verify(lines, None)

    # THE SPLICE TOUCHES NOTHING BUT THE TITLE. Everything below the GAME node
    # must be byte-identical to the donor, because the craft's flight profile -
    # the apoapsis window, the chute-arming rate, the staging sequence - is
    # measured on `career-science-pad` and nowhere else, and because a moved pool
    # would silently invalidate any ledger-oracle manifest built here later.
    if base_lines is not None:
        problems.extend(_verify_save_is_title_only_edit(lines, base_lines))

    # --- the stock contract set is untouched -------------------------------
    contracts = _contracts_node(lines)
    if contracts is None:
        problems.append("no ContractSystem/CONTRACTS node")
    elif contracts[1] - contracts[0] != 3:
        problems.append(
            "CONTRACTS is not empty (%d lines) - this fixture makes NO claim "
            "about KSP-side contract state, and a save-authored contract in "
            "this lineage does not survive to the flight anyway (see the module "
            "docstring)" % (contracts[1] - contracts[0]))

    # --- the pools are untouched -------------------------------------------
    for scenario, key, expected in (("Funding", "funds", "500000"),
                                    ("ResearchAndDevelopment", "sci", "100"),
                                    ("Reputation", "rep", "0")):
        node = _scenario_node(lines, scenario)
        got = None if node is None else get_value(lines, node, key)
        if got != expected:
            problems.append("%s.%s is %r, expected %r (the splice moves no pool)"
                            % (scenario, key, got, expected))

    # --- the ledger half ---------------------------------------------------
    if ledger_lines is not None:
        problems.extend(verify_ledger(ledger_lines))

    return problems


def _verify_save_is_title_only_edit(lines: List[str],
                                    base_lines: List[str]) -> List[str]:
    """The fixture's save differs from the donor's on the Title line and nowhere
    else. Reported as a line index so a drift names where it happened."""
    problems: List[str] = []
    if len(lines) != len(base_lines):
        problems.append("save has %d lines, donor has %d - this recipe changes "
                        "no line COUNT" % (len(lines), len(base_lines)))
        return problems
    differing = [i for i, (a, b) in enumerate(zip(lines, base_lines)) if a != b]
    if differing != [i for i in differing if lines[i].startswith("\tTitle = ")]:
        problems.append("save differs from the donor outside the Title line "
                        "(first at index %d: %r)"
                        % (differing[0], lines[differing[0]]))
    elif len(differing) != 1:
        problems.append("expected exactly 1 differing line, found %d"
                        % len(differing))
    return problems


def verify_ledger(ledger_lines: List[str]) -> List[str]:
    """The ledger's own post-conditions - where every claim of this fixture is."""
    problems: List[str] = []
    if ledger_lines[:2] != ["version = 0", "recordingSchemaGeneration = 4"]:
        problems.append(
            "ledger header is %r, expected the two flat top-level values "
            "`Ledger.LoadFromFile` exact-matches - a mismatch drops the whole "
            "file and starts with an empty ledger" % (ledger_lines[:2],))

    rows = _ledger_rows(ledger_lines)
    if len(rows) != 2:
        problems.append("expected exactly 2 GAME_ACTION rows, found %d"
                        % len(rows))
        return problems

    expected = (
        (CONTRACT_A_UT, CONTRACT_A_ACTION_ID, CONTRACT_A_SEQ, CONTRACT_A_GUID,
         CONTRACT_A_DEADLINE_UT, CONTRACT_A_REP_PENALTY),
        (CONTRACT_B_UT, CONTRACT_B_ACTION_ID, CONTRACT_B_SEQ, CONTRACT_B_GUID,
         CONTRACT_B_DEADLINE_UT, CONTRACT_B_REP_PENALTY),
    )
    seen_ids = []
    for row, (ut, action_id, seq, guid, deadline, rep) in zip(rows, expected):
        for key, want in (("type", "5"),
                          ("ut", ut),
                          ("seq", seq),
                          ("actionId", action_id),
                          ("contractId", guid),
                          ("contractType", CONTRACT_TYPE),
                          ("advanceFunds", CONTRACT_ADVANCE_FUNDS),
                          ("deadlineUT", deadline),
                          ("fundsPenalty", CONTRACT_FUNDS_PENALTY),
                          ("repPenalty", rep)):
            got = get_value(ledger_lines, row, key)
            if got != want:
                problems.append("ledger row %s (%s) is %r, expected %r"
                                % (key, guid[:8], got, want))
        if get_value(ledger_lines, row, "recordingId") is not None:
            problems.append(
                "ledger row %s carries a recordingId - a contract is accepted "
                "at Mission Control, and Ledger.Reconcile prunes a "
                "contract-lifecycle row whose tag names a recording the save "
                "does not hold" % guid[:8])
        try:
            if float(ut) > SAVE_CLOCK_UT:
                problems.append(
                    "ledger row %s ut %s is past the save clock %r - "
                    "Ledger.Reconcile prunes contract-lifecycle rows whose UT "
                    "exceeds it" % (guid[:8], ut, SAVE_CLOCK_UT))
        except ValueError:
            problems.append("ledger row %s ut is not numeric" % guid[:8])
        seen_ids.append(guid)

    if len(set(seen_ids)) != 2:
        problems.append("the two rows do not carry distinct contract ids")

    # THE TWO DEADLINES ARE THE EXPERIMENT. A must outlive every walk and B must
    # not; asserting the ORDER rather than the literals is what keeps a future
    # re-sizing honest.
    try:
        a_deadline = float(CONTRACT_A_DEADLINE_UT)
        b_deadline = float(CONTRACT_B_DEADLINE_UT)
        if not b_deadline > float(CONTRACT_B_UT):
            problems.append(
                "B's deadline %r is not past its own accept UT - it would be "
                "elapsed on the COLD-LOAD walk and never be active at all"
                % CONTRACT_B_DEADLINE_UT)
        if not a_deadline > 100000.0:
            problems.append(
                "A's deadline %r is not far past the flight's ~350 s span - A "
                "is the control and must stay ACTIVE on every walk"
                % CONTRACT_A_DEADLINE_UT)
        if not b_deadline < a_deadline:
            problems.append("B's deadline is not shorter than A's")
    except ValueError:
        problems.append("a deadline is not numeric")

    if _count_rows_of_type(ledger_lines, "6"):
        problems.append("the ledger carries a ContractComplete row - a "
                        "fixture-carried terminal would measure the fixture")
    if _count_rows_of_type(ledger_lines, "7"):
        problems.append("the ledger carries a ContractFail row - the fail this "
                        "spec gates must be SYNTHESIZED by ContractsModule.PrePass")

    if ledger_lines[-1] != "":
        problems.append("ledger does not end with a trailing newline")

    return problems


def _count_rows_of_type(ledger_lines: List[str], type_value: str) -> int:
    count = 0
    for row in _ledger_rows(ledger_lines):
        if get_value(ledger_lines, row, "type") == type_value:
            count += 1
    return count


# ---------------------------------------------------------------------------
# Shell.
# ---------------------------------------------------------------------------


def _ledger_path(root: str) -> str:
    return os.path.join(root, *LEDGER_RELATIVE_PATH)


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture without writing")
    args = parser.parse_args(argv)

    base_root = os.path.join(_SAVES, BASE_NAME)
    out_root = os.path.join(_SAVES, FIXTURE_NAME)
    base_sfs = os.path.join(base_root, "persistent.sfs")
    base_meta = os.path.join(base_root, "persistent.loadmeta")

    base_lines = read_lines(base_sfs)

    if args.check:
        lines = read_lines(os.path.join(out_root, "persistent.sfs"))
        ledger = read_lines(_ledger_path(out_root))
    else:
        lines = build(base_lines, TITLE)
        ledger = list(LEDGER_LINES)

    problems = verify(lines, base_lines, ledger)
    if problems:
        for problem in problems:
            sys.stderr.write("FAIL: %s\n" % problem)
        return 1

    if args.check:
        sys.stdout.write("OK: %s satisfies every post-condition\n" % FIXTURE_NAME)
        return 0

    if not os.path.isdir(out_root):
        os.makedirs(out_root)
    write_lines(os.path.join(out_root, "persistent.sfs"), lines)
    # The loadmeta is carried VERBATIM: the splice adds no vessel, moves no pool
    # and leaves the clock alone, and `ongoingContracts = 0` stays TRUE because
    # this fixture authors no stock contract at all.
    write_lines(os.path.join(out_root, "persistent.loadmeta"),
                read_lines(base_meta))

    ledger_path = _ledger_path(out_root)
    ledger_dir = os.path.dirname(ledger_path)
    if not os.path.isdir(ledger_dir):
        os.makedirs(ledger_dir)
    write_lines(ledger_path, ledger)

    addons_src = os.path.join(base_root, "AddOns")
    addons_dst = os.path.join(out_root, "AddOns")
    if os.path.isdir(addons_src) and not os.path.isdir(addons_dst):
        shutil.copytree(addons_src, addons_dst)

    sys.stdout.write("OK: wrote %s\n" % out_root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
