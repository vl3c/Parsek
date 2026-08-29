"""Fixture + spec gates for `career-contract-pad` and `L5-career-contract-complete`.

WHAT THIS FILE GUARDS. `career-contract-pad` is the only committed fixture whose
ledger carries a contract the CODE has to resolve, and
`L5-career-contract-complete` is the only committed spec that gates any contract
transition past `ProcessAccept`. Both properties come down to a handful of
literals in a 376-byte sidecar, and none of them is visible from the bytes:

  - B's deadline has to sit STRICTLY BETWEEN the cold-load walk's clock (B's own
    accept UT) and the commit-time walk's (~348 s of flight rows). Below the
    first and B is retired before it is ever active; above the second and the
    injection never fires. Either way the run passes nothing and proves nothing;
    the second one would even look green.
  - A's deadline has to outlive every walk, because A is the control.
  - Both accept UTs have to sit below the save clock or `Ledger.Reconcile`
    prunes them on cold load.
  - Neither row may be a terminal: a fixture-carried `type = 6` or `type = 7`
    would measure the fixture rather than `ContractsModule`.
  - and the spec's pinned penalty numbers have to be the fixture's own, or the
    tokens gate nothing.

Every one of those is a cell below. Getting any of them wrong costs a live
flight, and it fails in the specific way that is hardest to read: the log simply
has no contract lines in it.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

try:
    import tomllib
except ImportError:  # pragma: no cover - Python < 3.11
    import tomli as tomllib  # type: ignore

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "career-contract-pad")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
FIXTURE_LEDGER = os.path.join(FIXTURE_DIR, "Parsek", "GameState", "ledger.pgld")
FIXTURE_META = os.path.join(FIXTURE_DIR, "persistent.loadmeta")
DONOR_DIR = os.path.join(_HARNESS, "fixtures", "saves", "career-science-pad")
DONOR_SFS = os.path.join(DONOR_DIR, "persistent.sfs")
SPEC_PATH = os.path.join(_HARNESS, "scenarios",
                         "L5-career-contract-complete.toml")
L3_SPEC_PATH = os.path.join(_HARNESS, "scenarios",
                            "L3-career-science-recover.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_career_contract_pad.py")
    spec = importlib.util.spec_from_file_location(
        "build_career_contract_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CareerContractPadFixtureDriftTests(unittest.TestCase):
    """WIRES `build_career_contract_pad.py --check` INTO THE SUITE.

    Same discipline as `CareerEarnedPadFixtureDriftTests` one lane over: the
    committed bytes must be what the recipe produces from the CURRENT donor, so a
    change to `career-science-pad` reds here instead of drifting into a flight."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def _donor_lines(self):
        return self.builder.read_lines(DONOR_SFS)

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        # The `--check` path, in-process. Layers in the DONOR builder's own
        # post-conditions (11 parts, one DIRECT antenna, the transmit EC floor,
        # career legality) rather than restating them.
        builder = self.builder
        problems = builder.verify(builder.read_lines(FIXTURE_SFS),
                                  self._donor_lines(),
                                  builder.read_lines(FIXTURE_LEDGER))
        self.assertEqual([], problems)

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_rebuild(self):
        # THE DRIFT GUARD, comparing RAW BYTES rather than the line lists
        # `read_lines` hands back: that helper normalizes CRLF on the way in, so
        # a line-list comparison would call a fixture that had lost its CRLFs (or
        # grown a BOM, or a trailing byte) identical to one that had not.
        builder = self.builder
        rebuilt = builder.build(self._donor_lines(), builder.TITLE)
        produced = "\r\n".join(rebuilt).encode("utf-8")
        with open(FIXTURE_SFS, "rb") as fh:
            committed = fh.read()
        if committed != produced:
            offset = next(
                (i for i, (a, b) in enumerate(zip(committed, produced))
                 if a != b),
                min(len(committed), len(produced)))
            self.fail("career-contract-pad has drifted from what "
                      "build_career_contract_pad.py produces from the current "
                      "career-science-pad; re-run the builder and commit, or "
                      "explain the divergence. First difference at byte %d "
                      "(committed %d bytes, rebuilt %d bytes)"
                      % (offset, len(committed), len(produced)))

    def test_the_committed_ledger_is_byte_identical_to_the_recipe(self):
        # The ledger needs its own cell because the cell above rebuilds only
        # `persistent.sfs`. This sidecar is authored whole rather than derived
        # from a donor file - `career-science-pad` has no `Parsek/` tree at all -
        # so what it is compared against is the recipe's own literal.
        builder = self.builder
        produced = "\r\n".join(builder.LEDGER_LINES).encode("utf-8")
        with open(FIXTURE_LEDGER, "rb") as fh:
            committed = fh.read()
        self.assertEqual(committed, produced,
                         "career-contract-pad's ledger.pgld has drifted from "
                         "build_career_contract_pad.LEDGER_LINES; re-run the "
                         "builder and commit")

    def test_the_save_differs_from_the_donor_on_the_title_alone(self):
        # THE PROMISE THAT LETS THE SPEC REUSE L3's FLIGHT-LEG PARAMS AND POOLS.
        # Everything this fixture claims lives in the sidecar; the save is the
        # donor's, and the craft's measured flight profile therefore still
        # describes it. A one-line diff is also the cheapest possible statement
        # that no pool, roster row or scenario node moved.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        donor = self._donor_lines()
        self.assertEqual(len(donor), len(lines))
        differing = [i for i, (a, b) in enumerate(zip(lines, donor)) if a != b]
        self.assertEqual(1, len(differing),
                         "expected exactly one differing line, found %d"
                         % len(differing))
        self.assertTrue(lines[differing[0]].startswith("\tTitle = "),
                        "the differing line is %r, not the Title"
                        % lines[differing[0]])

    def test_the_loadmeta_is_the_donors_verbatim(self):
        # Nothing moved: no vessel, no pool, no clock - and `ongoingContracts`
        # stays 0 because this fixture authors no STOCK contract at all.
        builder = self.builder
        self.assertEqual(
            builder.read_lines(os.path.join(DONOR_DIR, "persistent.loadmeta")),
            builder.read_lines(FIXTURE_META))

    def test_the_stock_contract_set_stays_empty(self):
        # The fixture makes NO claim about KSP-side contract state, and that is
        # a measurement rather than modesty - see
        # SAVE-AUTHORED-PROGRESS-NODE-DOES-NOT-RESTORE. A future author who
        # splices a CONTRACT back in reds here and has to read why first.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        contracts = builder._contracts_node(lines)
        self.assertIsNotNone(contracts)
        self.assertEqual([], builder.child_nodes(lines, contracts, "CONTRACT"))


class CareerContractPadDeadlineArithmeticTests(unittest.TestCase):
    """THE CELLS THAT SAY WHY THIS FIXTURE FIRES AT ALL.

    `career-earned-pad`'s sibling class asserts that its Active contract can
    never resolve, because a mid-batch resolution would re-vacuify the compare
    that cell exists for. Here a resolution IS the measurement, so each of those
    guards is inverted into a requirement - and the requirement is arithmetic
    over two clocks."""

    # The commit-time walk's `nowUT` is the last surviving action's UT, which on
    # this profile is the recovery credit. MEASURED 348.08 on run
    # `2026-08-19_2130_L3-career-science-recover` (same craft, same mission) and
    # again on `2026-08-20_2217` on this fixture. The floor below is deliberately
    # conservative: any flight that lands and recovers writes rows well past 120.
    COMMIT_WALK_NOW_UT_FLOOR = 120.0

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.ledger = cls.builder.read_lines(FIXTURE_LEDGER)

    def _rows(self):
        return self.builder._ledger_rows(self.ledger)

    def test_the_ledger_carries_exactly_two_accepts_and_no_terminal(self):
        builder = self.builder
        rows = self._rows()
        self.assertEqual(2, len(rows))
        self.assertEqual(["5", "5"],
                         [builder.get_value(self.ledger, r, "type")
                          for r in rows])

    def test_no_fixture_carried_terminal_row(self):
        # THE INVERTED TRAP 3. `career-earned-pad` must not carry a completion
        # because it would empty its active set; this fixture must not carry a
        # FAIL because the fail is the thing being measured. A fixture-carried
        # `type = 7` would make every token below pass without
        # `ContractsModule.PrePass` ever running.
        builder = self.builder
        self.assertEqual(0, builder._count_rows_of_type(self.ledger, "6"))
        self.assertEqual(0, builder._count_rows_of_type(self.ledger, "7"))

    def test_both_accepts_sit_below_the_save_clock(self):
        # `Ledger.Reconcile` prunes any contract-lifecycle row whose UT exceeds
        # the save's `UniversalTime` on cold load. A row authored in the future
        # would simply vanish, and the flight would log nothing at all.
        builder = self.builder
        for row in self._rows():
            ut = float(builder.get_value(self.ledger, row, "ut"))
            self.assertLessEqual(ut, builder.SAVE_CLOCK_UT)

    def test_b_survives_the_cold_load_walk(self):
        # On the COLD-LOAD walk `PrePass` takes `nowUT` from the last surviving
        # action's UT, which is B's own accept. A deadline at or below that would
        # be elapsed IMMEDIATELY: B would be retired before it was ever active,
        # `activeSlots=2/2` would never appear, and the injection the spec pins
        # would fire on the wrong walk.
        builder = self.builder
        self.assertGreater(float(builder.CONTRACT_B_DEADLINE_UT),
                           float(builder.CONTRACT_B_UT))

    def test_b_expires_by_the_commit_walk(self):
        # And the other side of the window. A deadline past the flight's own
        # clock would never elapse, `PrePass` would inject nothing, and the run
        # would red with three missing tokens and no clue why.
        builder = self.builder
        self.assertLess(float(builder.CONTRACT_B_DEADLINE_UT),
                        self.COMMIT_WALK_NOW_UT_FLOOR)

    def test_a_outlives_every_walk(self):
        # A is the CONTROL: its `Accept:` line is what says the sidecar loaded.
        # If A's deadline ever came inside the flight's clock, A would resolve
        # too and the fixture would no longer distinguish "the sidecar loaded"
        # from "the fail path ran".
        builder = self.builder
        self.assertGreater(float(builder.CONTRACT_A_DEADLINE_UT), 100000.0)

    def test_both_accepts_honour_the_advance_funds_trap(self):
        # TRAP 1, inherited from `build_career_earned_pad.py` because it is a
        # fact about `FundsModule.ProcessContractAccept` rather than about that
        # fixture: the advance is credited unconditionally, so a nonzero one
        # moves the funds pool off 500000 before the flight starts.
        builder = self.builder
        for row in self._rows():
            self.assertEqual("0",
                             builder.get_value(self.ledger, row, "advanceFunds"))

    def test_the_two_rows_carry_distinct_guids(self):
        builder = self.builder
        ids = [builder.get_value(self.ledger, r, "contractId")
               for r in self._rows()]
        self.assertEqual(2, len(set(ids)))

    def test_neither_row_is_tagged_with_a_recording(self):
        # A contract is accepted at Mission Control, not inside a flight, and
        # `Ledger.Reconcile` prunes a contract-lifecycle row whose tag names a
        # recording the save does not hold - which this save does not.
        builder = self.builder
        for row in self._rows():
            self.assertIsNone(
                builder.get_value(self.ledger, row, "recordingId"))

    def test_the_seq_values_are_the_next_ksc_scoped_ones(self):
        # `LedgerOrchestrator.AllocateKscSequence` hands out 1, 2, 3 ... to
        # actions carrying no `recordingId`. These are the only two such actions
        # in the file, so 1 and 2 are the next values rather than a guess - and a
        # duplicate would make the walk's tie-break ordering ambiguous.
        builder = self.builder
        self.assertEqual(["1", "2"],
                         [builder.get_value(self.ledger, r, "seq")
                          for r in self._rows()])


class L5SpecFixtureSyncTests(unittest.TestCase):
    """The SPEC and the FIXTURE must agree, and nothing else checks that."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.ledger = cls.builder.read_lines(FIXTURE_LEDGER)
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)
        with open(L3_SPEC_PATH, "rb") as fh:
            cls.l3 = tomllib.load(fh)

    def _required(self):
        return self.spec["expectations"]["logContracts"]["required"]

    def test_the_spec_points_at_the_contract_pad_fixture(self):
        self.assertEqual("fixtures/saves/career-contract-pad",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_store_starts_empty(self):
        # `recordings.count` only means "this flight's own" when nothing was
        # injected.
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"])

    def test_every_required_contract_token_names_a_fixture_guid(self):
        # A token naming SOME contract would pass against a run that resolved
        # something else entirely. Both guids are minted by the builder, so
        # coupling them here is what keeps a re-mint from silently un-gating the
        # spec.
        builder = self.builder
        a, b = builder.CONTRACT_A_GUID, builder.CONTRACT_B_GUID
        naming_a = [t for t in self._required() if a in t]
        naming_b = [t for t in self._required() if b in t]
        self.assertEqual(1, len(naming_a),
                         "expected exactly one token (the accept) naming the "
                         "control contract %s" % a)
        self.assertEqual(4, len(naming_b),
                         "expected the accept, the injection, the retirement "
                         "and the penalty tokens to name %s" % b)

    def test_the_pinned_penalties_are_the_fixtures_own(self):
        # The injection line and the fail line both carry B's penalty pack, so a
        # fail built from the wrong accept - or a fixture whose numbers were
        # re-derived without the spec - reds here rather than passing.
        builder = self.builder
        row = builder._ledger_rows(self.ledger)[1]
        funds = builder.get_value(self.ledger, row, "fundsPenalty")
        rep = builder.get_value(self.ledger, row, "repPenalty")
        self.assertEqual(builder.CONTRACT_FUNDS_PENALTY, funds)
        self.assertEqual(builder.CONTRACT_B_REP_PENALTY, rep)
        pack = "fundsPenalty=%s repPenalty=%s" % (funds, rep)
        carrying = [t for t in self._required() if pack in t]
        self.assertEqual(2, len(carrying),
                         "expected the injection and fail tokens to carry %r"
                         % pack)

    def test_the_pinned_deadline_is_the_fixtures_own(self):
        builder = self.builder
        row = builder._ledger_rows(self.ledger)[1]
        deadline = builder.get_value(self.ledger, row, "deadlineAbsUT")
        self.assertTrue(
            any("at deadlineUT=%s " % deadline in t for t in self._required()),
            "no required token pins the injection at deadlineUT=%s" % deadline)

    def test_the_slot_reservation_is_pinned_for_both_accepts(self):
        # `activeSlots` is a fixture constant on a level-1 Mission Control: A
        # takes the first slot at UT 5, B the second at UT 6. Pinning it whole is
        # what makes the accept tokens say "in this order, into these slots"
        # rather than merely "some accept happened".
        required = self._required()
        self.assertTrue(any("activeSlots=1/2" in t for t in required))
        self.assertTrue(any("activeSlots=2/2" in t for t in required))

    def test_the_drawdown_clamp_is_pinned_without_the_flights_numbers(self):
        # MEASURED, and it corrected a prediction this spec had got wrong: the
        # synthetic fail's penalties reach the RECONSTRUCTION and are then
        # CLAMPED by `KspStatePatcher`'s guarded-drawdown protection, so the live
        # career never sees them (SYNTHETIC-CONTRACT-FAIL-PENALTY-CLAMPED-BY-
        # DRAWDOWN-GUARD). Pinning the clamp makes a change in either direction
        # red here.
        #
        # THE NUMBERS MUST STAY OUT OF THE TOKEN. `running=` moves during the
        # recalc burst (run `2026-08-20_2240` logged clamps at both
        # `running=522200` and `running=527558`, before and after the recovery
        # credit landed), and `live=` is the flight's total earnings, which
        # `L3-career-science-recover` owns. A token carrying either would red on
        # a flight whose earnings moved for reasons this spec does not assert.
        clamp = [t for t in self._required() if "GUARDED DRAWDOWN" in t]
        self.assertEqual(1, len(clamp))
        for forbidden in ("running=", "live=", "wouldBeTarget=", "clampedTo="):
            self.assertNotIn(forbidden, clamp[0],
                             "the clamp token pins %r, which moves per run"
                             % forbidden)

    def test_the_d8_contracts_claim_is_gated(self):
        # The claim-vs-gate coupling `test_career_earned_pad.py` asserts for the
        # ACCEPT half, asserted here for the terminal half. A D8 claim with no
        # token naming a transition past accept is the fail-open shape.
        self.assertIn("contracts", self.spec["dimensionsCovered"]["D8"])
        self.assertTrue(
            any("injected synthetic ContractFail" in t
                for t in self._required()),
            "D8 contracts is claimed with no token gating PrePass's injection")
        self.assertTrue(
            any(t.startswith("Fail: contractId=") for t in self._required()),
            "D8 contracts is claimed with no token gating ProcessFail")

    def test_the_flight_leg_is_l3s_verbatim(self):
        # The craft is byte-identical to L3's, so a forked flight-leg parameter
        # would drift the first time either side is touched. The chute-arming
        # window in particular cost three flights to get right.
        keys = ("throttle", "apoapsisWindowMeters", "chuteArmMaxRateMps",
                "chuteFullDeployAltMeters", "landedSituations",
                "ascentTimeoutSeconds", "coastTimeoutSeconds",
                "descentTimeoutSeconds", "collectMinExperiments",
                "collectTimeoutSeconds", "transmitMinScienceGain",
                "transmitTimeoutSeconds", "recoverMinFundsGain",
                "recoverTimeoutSeconds")
        mine = self.spec["driver"]["missionParams"]
        theirs = self.l3["driver"]["missionParams"]
        for key in keys:
            self.assertEqual(theirs[key], mine[key],
                             "missionParams.%s forked from L3's" % key)

    def test_the_spec_flies_the_same_mission_as_l3(self):
        self.assertEqual(self.l3["driver"]["mission"],
                         self.spec["driver"]["mission"])

    def test_the_auto_merge_setting_is_present(self):
        # The injecting walk is the COMMIT-time recalc, and the commit on this
        # profile can only come from the scene-exit auto-merge: stock recovery
        # destroys the vessel, so `CommitTree` has nothing to commit. Without
        # this setting the exit raises a dialog no seam verb answers, no recalc
        # runs with a `nowUT` past B's deadline, and the three contract tokens
        # never appear.
        settings = [s for s in self.spec["driver"]["steps"]
                    if s.get("cmd") == "SetSetting"]
        pairs = {s["args"]["name"]: s["args"]["value"] for s in settings}
        self.assertEqual("true", pairs.get("autoMerge"))
        self.assertEqual("true", pairs.get("autoRecordOnLaunch"))


if __name__ == "__main__":
    unittest.main()
