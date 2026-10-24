#!/usr/bin/env python3
"""Build the `career-requirements-loss-pad` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. Todo STOCK-OFFER-EXPIRED-ACTIVE-CONTRACT-NO-TERMINAL-ROW (fixed in
PR #2184, unit cells only): stock's `Contract.Update()` ends an ACTIVE contract whose
requirements no longer hold with `SetState(State.OfferExpired)`, unpenalized, and
before the fix Parsek filed no terminal ledger row for it. The fix
(`Patches/ContractOfferExpiredCapturePatch.cs` + `GameStateRecorder.
OnActiveContractOfferExpired`) files a zero-penalty `ContractCancel` row flagged
`requirementsLost`. `L14-contract-requirements-loss-terminal-row` flies it, and this
fixture is its host: a save whose one Active contract CANNOT meet its requirements.

THE REQUIREMENTS LOSS IS A MEASUREMENT, NOT A HYPOTHESIS. The first build of
`career-contract-pad` (same donor as here) spliced a `state = Active` `PartTest` into
the save, and run `2026-08-20_2217_L5-career-contract-complete` measured stock
retiring it before the mission's first frame (`PatchContracts: ledger has 1 active
contracts, ... KSP has 0 current contracts, 0 finished contracts`). The decompiled
reason, quoted in `build_career_contract_pad.py`'s docstring:
`Contracts.Templates.PartTest.MeetRequirements()` reads
`ProgressTracking.NodeComplete("FirstLaunch")`, and `Contract.Update()` re-checks it
on every tick of an Active contract, retiring it to `OfferExpired`. That run's
spliced `FirstLaunch` node did not restore (todo
SAVE-AUTHORED-PROGRESS-NODE-DOES-NOT-RESTORE); this fixture authors NONE, so
`FirstLaunch` is incomplete whichever explanation of that finding is right: the
donor's `Progress` node is empty, asserted below.

WHAT IT SPLICES, against `career-science-pad` (fresh-career lineage, the Jumping Flea
PRELAUNCH on the pad, UT 9.06, funds 500000 / science 100 / reputation 0):

  1. The `Title`.
  2. ONE `CONTRACT` into the empty `ContractSystem/CONTRACTS` node: the stock-written
     `PartTest` `375b4446-...` (RT-5 Flea, in flight over Kerbin at 59-64 km) copied
     line for line out of `career-earned-pad` (itself from the hand-played
     `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`), with five fields re-stated:
     `state` Offered -> Active, `viewed` Unseen -> Read, and in `values` the advance
     (index 2) 9000 -> 0, `dateAccepted` (index 9) 0 -> 6 and `dateDeadline`
     (index 10) 0 -> 9201606 (accept + the contract's own TimeDeadline 9201600).
     WHY THIS ONE: its part is a START-tech part this career owns (the Flea), and its
     test needs flight at 59-64 km, which a pad that never launches cannot reach, so
     no live completion can race the retirement.
  3. A `Parsek/GameState/ledger.pgld` with ONE `type = 5` (`ContractAccept`) row for
     it, field for field off `SerializeContractAccept` (the shape
     `build_career_contract_pad.py` already flew green), no terminal row.
  4. The loadmeta's `ongoingContracts` 0 -> 1.

THE TRAPS, each asserted by `verify`:
  - `advanceFunds = 0` on the row and values[2] = 0 on the contract: an advance is
    credited unconditionally by `FundsModule.ProcessContractAccept` and would move the
    funds pool the lane's ledger oracle gates HARD.
  - The accept UT (6) sits below the save clock (9.06): `Ledger.Reconcile` prunes a
    contract-lifecycle row past it on cold load.
  - The deadline (9201606) is far past anything the lane's ~16 s reaches, so
    `ContractsModule.PrePass` never injects a synthetic fail: the only terminal the
    walk can see is the one the fix files.
  - The penalties stay NONZERO (funds 9000 = values[4], reputation 4 = values[7]):
    stock charges nothing for this end and the fix files zero penalties, so a
    regression that charged the pack moves the HARD-gated pools.
  - No recordingId on the row: an accept is a Mission Control act, and a tag naming a
    recording the save does not hold is pruned on load.

Usage:
    python harness/tools/build_career_requirements_loss_pad.py            # write it
    python harness/tools/build_career_requirements_loss_pad.py --check    # verify only

`--check` re-runs every post-condition against the committed bytes; the same verify,
plus a byte-identity rebuild from the current donors, runs in the suite
(`harness/lib/test_career_requirements_loss_pad.py`).

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import List, Optional, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import build_career_pad_craft as base_builder  # noqa: E402

read_lines = base_builder.read_lines
write_lines = base_builder.write_lines
find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value
set_top_value = base_builder.set_top_value

FIXTURE_NAME = "career-requirements-loss-pad"
BASE_NAME = "career-science-pad"
CONTRACT_DONOR_NAME = "career-earned-pad"
TITLE = "career-requirements-loss-pad (CAREER)"

SAVE_CLOCK_UT = 9.0599999999998957

CONTRACT_GUID = "375b4446-c861-4b4d-bf97-ef38407246a4"
CONTRACT_TYPE = "PartTest"
CONTRACT_PART = "solidBooster.sm.v2"
CONTRACT_SIT = "FLYING"
# `#autoLOC_6100005 = Test <<1>> <<2>>.` with `#autoLOC_6100024 = in flight over
# <<g:2,1>>` (the derivation `build_career_contract_pad.py` uses). Cosmetic: nothing
# matches on a title.
CONTRACT_TITLE = 'Test RT-5 "Flea" Solid Fuel Booster in flight over Kerbin.'

CONTRACT_ACCEPT_UT = "6"
CONTRACT_TIME_DEADLINE = "9201600"   # values[1], the donor contract's own duration
CONTRACT_DEADLINE_UT = "9201606"     # values[10] = accept + values[1]
CONTRACT_FUNDS_PENALTY = "9000"      # values[4], FundsFailure
CONTRACT_REP_PENALTY = "4"           # values[7], ReputationFailure
CONTRACT_ADVANCE_FUNDS = "0"

# The donor contract's `values`, and the re-stated ones. Index map (stock
# `Contract.Save`): 0 TimeExpiry, 1 TimeDeadline, 2 FundsAdvance, 3 FundsCompletion,
# 4 FundsFailure, 5 ScienceCompletion, 6 ReputationCompletion, 7 ReputationFailure,
# 8 dateExpire, 9 dateAccepted, 10 dateDeadline, 11 dateFinished.
DONOR_VALUES = "43200,9201600,9000,22500,9000,3,6,4,43215.14,0,0,0"
SPLICED_VALUES = "43200,9201600,0,22500,9000,3,6,4,43215.14,6,9201606,0"

CONTRACT_ACTION_ID = "act_8e41c27a5b9d4f03a6e1d72c4b05f918"
CONTRACT_SEQ = "1"

LEDGER_LINES = [
    "version = 0",
    "recordingSchemaGeneration = 4",
    "GAME_ACTION",
    "{",
    "\tut = %s" % CONTRACT_ACCEPT_UT,
    "\ttype = 5",
    "\tactionId = %s" % CONTRACT_ACTION_ID,
    "\tseq = %s" % CONTRACT_SEQ,
    "\tcontractId = %s" % CONTRACT_GUID,
    "\tcontractType = %s" % CONTRACT_TYPE,
    "\tcontractTitle = %s" % CONTRACT_TITLE,
    "\tadvanceFunds = %s" % CONTRACT_ADVANCE_FUNDS,
    "\tdeadlineAbsUT = %s" % CONTRACT_DEADLINE_UT,
    "\tfundsPenalty = %s" % CONTRACT_FUNDS_PENALTY,
    "\trepPenalty = %s" % CONTRACT_REP_PENALTY,
    "}",
    # `Ledger.SaveToFile` goes through `ConfigNode.Save`, which terminates the last line.
    "",
]

LEDGER_RELATIVE_PATH = ("Parsek", "GameState", "ledger.pgld")


# ---------------------------------------------------------------------------
# Node helpers.
# ---------------------------------------------------------------------------


def _scenario_node(lines: List[str], scenario_name: str) -> Optional[Tuple[int, int]]:
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == scenario_name:
            return node
        i = node[1]


def _contracts_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    scn = _scenario_node(lines, "ContractSystem")
    if scn is None:
        return None
    return find_node(lines, "CONTRACTS", scn[0], scn[1])


def _contract_nodes(lines: List[str]) -> List[Tuple[int, int]]:
    contracts = _contracts_node(lines)
    if contracts is None:
        return []
    return child_nodes(lines, contracts, "CONTRACT")


def _contract_by_guid(lines: List[str], guid: str) -> Optional[Tuple[int, int]]:
    for node in _contract_nodes(lines):
        if get_value(lines, node, "guid") == guid:
            return node
    return None


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def spliced_contract(donor_lines: List[str]) -> List[str]:
    """The donor's `375b4446` CONTRACT node with the five re-stated fields."""
    node = _contract_by_guid(donor_lines, CONTRACT_GUID)
    if node is None:
        raise SystemExit("contract donor has no CONTRACT guid=%s" % CONTRACT_GUID)
    block = list(donor_lines[node[0]:node[1]])
    local = (0, len(block))
    for key, want_before, value in (("state", "Offered", "Active"),
                                    ("viewed", "Unseen", "Read"),
                                    ("values", DONOR_VALUES, SPLICED_VALUES)):
        got = get_value(block, local, key)
        if got != want_before:
            raise SystemExit("donor contract %s is %r, expected %r"
                             % (key, got, want_before))
        set_value(block, local, key, value)
    return block


def build(base_lines: List[str], donor_lines: List[str], title: str) -> List[str]:
    """`career-science-pad`'s save with its Title restamped and one Active contract."""
    lines = list(base_lines)
    if not set_top_value(lines, "Title", title):
        raise SystemExit("base save has no GAME-level Title line")
    contracts = _contracts_node(lines)
    if contracts is None or contracts[1] - contracts[0] != 3:
        raise SystemExit("base save's ContractSystem/CONTRACTS node is missing or not empty")
    # Insert before the CONTRACTS node's closing brace.
    insert_at = contracts[1] - 1
    lines[insert_at:insert_at] = spliced_contract(donor_lines)
    return lines


def build_loadmeta(base_meta: List[str]) -> List[str]:
    meta = list(base_meta)
    for i, line in enumerate(meta):
        if line == "ongoingContracts = 0":
            meta[i] = "ongoingContracts = 1"
            return meta
    raise SystemExit("base loadmeta has no `ongoingContracts = 0` line")


# ---------------------------------------------------------------------------
# Post-conditions.
# ---------------------------------------------------------------------------


def verify(lines: List[str], base_lines: Optional[List[str]] = None,
           ledger_lines: Optional[List[str]] = None) -> List[str]:
    problems: List[str] = []

    if not any(l == "\tTitle = %s" % TITLE for l in lines):
        problems.append("Title is not %r" % TITLE)

    # --- the requirements loss: FirstLaunch must be incomplete ---------------
    progress_scn = _scenario_node(lines, "ProgressTracking")
    progress = None if progress_scn is None else find_node(
        lines, "Progress", progress_scn[0], progress_scn[1])
    if progress is None:
        problems.append("no ProgressTracking/Progress node")
    elif progress[1] - progress[0] != 3:
        problems.append(
            "ProgressTracking/Progress is not empty (%d lines): a FirstLaunch node "
            "here would make PartTest.MeetRequirements() read true and the lane would "
            "measure nothing" % (progress[1] - progress[0]))
    if any(l.strip() == "FirstLaunch" for l in lines):
        problems.append("the save carries a FirstLaunch node")

    # --- exactly one contract, Active, the spliced one ------------------------
    nodes = _contract_nodes(lines)
    if len(nodes) != 1:
        problems.append("expected exactly 1 CONTRACT node, found %d" % len(nodes))
    else:
        node = nodes[0]
        for key, want in (("guid", CONTRACT_GUID),
                          ("type", CONTRACT_TYPE),
                          ("state", "Active"),
                          ("part", CONTRACT_PART),
                          ("sit", CONTRACT_SIT),
                          ("values", SPLICED_VALUES)):
            got = get_value(lines, node, key)
            if got != want:
                problems.append("contract %s is %r, expected %r" % (key, got, want))
        values = SPLICED_VALUES.split(",")
        if values[2] != CONTRACT_ADVANCE_FUNDS:
            problems.append("contract advance values[2] is not 0")
        if values[9] != CONTRACT_ACCEPT_UT:
            problems.append("contract dateAccepted values[9] is not the row's accept UT")
        if values[10] != CONTRACT_DEADLINE_UT:
            problems.append("contract dateDeadline values[10] is not the row's deadline")
        if float(values[9]) + float(values[1]) != float(values[10]):
            problems.append("dateDeadline is not dateAccepted + TimeDeadline")
        if values[4] != CONTRACT_FUNDS_PENALTY or values[7] != CONTRACT_REP_PENALTY:
            problems.append("contract penalty values do not match the ledger row")
        if base_lines is not None and CONTRACT_PART not in _start_tech_parts(base_lines):
            problems.append("contract part %s is not a researched part of the donor"
                            % CONTRACT_PART)

    # --- the pools are the donor's ---------------------------------------------
    for scenario, key, expected in (("Funding", "funds", "500000"),
                                    ("ResearchAndDevelopment", "sci", "100"),
                                    ("Reputation", "rep", "0")):
        node = _scenario_node(lines, scenario)
        got = None if node is None else get_value(lines, node, key)
        if got != expected:
            problems.append("%s.%s is %r, expected %r" % (scenario, key, got, expected))

    # --- outside the Title and the contract, the save is the donor's -----------
    if base_lines is not None and len(nodes) == 1:
        node = nodes[0]
        stripped = lines[:node[0]] + lines[node[1]:]
        if len(stripped) != len(base_lines):
            problems.append("save minus the contract has %d lines, donor %d"
                            % (len(stripped), len(base_lines)))
        else:
            differing = [i for i, (a, b) in enumerate(zip(stripped, base_lines)) if a != b]
            if len(differing) != 1 or not stripped[differing[0]].startswith("\tTitle = "):
                problems.append("save differs from the donor outside the Title and the "
                                "spliced contract (%d differing lines)" % len(differing))

    if ledger_lines is not None:
        problems.extend(verify_ledger(ledger_lines))
    return problems


def _start_tech_parts(lines: List[str]) -> List[str]:
    rnd = _scenario_node(lines, "ResearchAndDevelopment")
    if rnd is None:
        return []
    parts: List[str] = []
    for tech in child_nodes(lines, rnd, "Tech"):
        for i in range(tech[0] + 2, tech[1] - 1):
            s = lines[i].strip()
            if s.startswith("part = "):
                parts.append(s[len("part = "):])
    return parts


def verify_ledger(ledger_lines: List[str]) -> List[str]:
    problems: List[str] = []
    if ledger_lines != LEDGER_LINES:
        problems.append("ledger is not the recipe's LEDGER_LINES")
    rows = []
    i = 0
    while True:
        node = find_node(ledger_lines, "GAME_ACTION", i)
        if node is None:
            break
        rows.append(node)
        i = node[1]
    if len(rows) != 1:
        problems.append("expected exactly 1 GAME_ACTION row, found %d" % len(rows))
        return problems
    row = rows[0]
    if get_value(ledger_lines, row, "type") != "5":
        problems.append("the one row is not a ContractAccept (type = 5)")
    if get_value(ledger_lines, row, "recordingId") is not None:
        problems.append("the accept row carries a recordingId")
    if float(get_value(ledger_lines, row, "ut") or "nan") >= SAVE_CLOCK_UT:
        problems.append("the accept UT is not below the save clock")
    if float(get_value(ledger_lines, row, "deadlineAbsUT") or "0") < 100000.0:
        problems.append("the deadline is close enough for PrePass to inject a fail")
    if get_value(ledger_lines, row, "advanceFunds") != "0":
        problems.append("advanceFunds is not 0")
    return problems


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
    donor_root = os.path.join(_SAVES, CONTRACT_DONOR_NAME)
    out_root = os.path.join(_SAVES, FIXTURE_NAME)

    base_lines = read_lines(os.path.join(base_root, "persistent.sfs"))
    donor_lines = read_lines(os.path.join(donor_root, "persistent.sfs"))

    if args.check:
        lines = read_lines(os.path.join(out_root, "persistent.sfs"))
        ledger = read_lines(_ledger_path(out_root))
    else:
        lines = build(base_lines, donor_lines, TITLE)
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
    write_lines(os.path.join(out_root, "persistent.loadmeta"),
                build_loadmeta(read_lines(os.path.join(base_root, "persistent.loadmeta"))))
    ledger_path = _ledger_path(out_root)
    if not os.path.isdir(os.path.dirname(ledger_path)):
        os.makedirs(os.path.dirname(ledger_path))
    write_lines(ledger_path, ledger)

    addons_src = os.path.join(base_root, "AddOns")
    addons_dst = os.path.join(out_root, "AddOns")
    if os.path.isdir(addons_src) and not os.path.isdir(addons_dst):
        shutil.copytree(addons_src, addons_dst)

    sys.stdout.write("OK: wrote %s\n" % out_root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
