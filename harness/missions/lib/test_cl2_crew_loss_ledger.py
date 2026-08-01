"""Unit cells for the CL-1 extension, stage A (scenario `CL-2-pod-impact-ledger`).

CL-2 flies CL-1's mission and CL-1's fixture UNCHANGED and adds exactly three
things: `SetSetting autoMerge=true` before the flight, `ExitToSpaceCenter` after
the crash, and `[expectations.ledger]`. So these cells are deliberately NOT a
second copy of the CL-1 machine suite - `test_cl1_crew_loss.py` owns the pure
machine, the shell and the roster read, and CL-2 reuses all three verbatim.

Four groups, each guarding a failure mode the live flight would otherwise be the
first to discover:

  1. THE INVERSION OF CL-1's PIN. `test_cl1_crew_loss.py` asserts CL-1 drives no
     commit and declares no ledger block; these are the mirror cells - CL-2 DOES
     drive a commit route and DOES declare a ledger block - plus the cell that
     keeps CL-1 itself untouched, which is the whole reason this is a second spec
     rather than an edit.
  2. THE STEP LIST. `autoMerge = true` is MANDATORY (the exit verb's wedge guard
     REFUSES `variant=RegularMerge` without it) and its ORDER matters; the exit's
     budget must clear its C# deferral budget; `CommitTree` must stay absent.
  3. THE LEDGER BLOCK, checked through the SAME code the harness runs:
     `hlib.validate_ledger_expectations`, `oracle.parse_manifest_entries` and
     `oracle.compute_expected` against the committed fixture's real seed pools.
     A manifest that does not close arithmetically is a self-inflicted
     PARSEK-FAIL(ledger) that costs a whole flight to discover.
  4. CLAIM-IS-NOT-GATE. Every claimed dimension value must exist in the registry
     AND have a proving token in this spec's own logContracts; the re-fly cells
     the scope fence excludes must stay unclaimed.

NO krpc, NO KSP, NO network. Import path matches the sibling suites.
"""

import os
import re
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import hlib                     # noqa: E402
import mlib                     # noqa: E402
import oracle                   # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "CL-2-pod-impact-ledger.toml")
CL1_SPEC_PATH = os.path.join(_HARNESS, "scenarios", "CL-1-pod-impact.toml")
REGISTRY_PATH = os.path.join(_HARNESS, "coverage", "registry.toml")

# MEASURED career pools, identical across the two archived CL-1 flights
# (logs/2026-07-28_1913_CL-1-pod-impact) and re-measured on CL-2's own first
# flight. The seed values are read from the COMMITTED fixture below rather than
# hard-coded, so a fixture edit reds here; only the PRODUCED side is a constant,
# because it is an observation of the game and has no in-repo source.
PRODUCED_FUNDS = 529600.0
PRODUCED_SCIENCE = 100.0
PRODUCED_REPUTATION = -7.99982834


def _read_spec(path):
    with open(path, "rb") as fh:
        return tomllib.load(fh)


def _fixture_seed(spec):
    """The committed fixture's career pools, parsed out of its persistent.sfs.

    Deliberately read from the FIXTURE rather than restated as a constant: the
    oracle's expected totals are `seed + manifest`, so a fixture whose pools moved
    silently invalidates every manifest number below."""
    sfs = os.path.join(_HARNESS, spec["fixture"]["saveTemplate"], "persistent.sfs")
    with open(sfs, "r", encoding="utf-8", errors="replace") as fh:
        lines = [l.strip() for l in fh.read().split("\n")]
    got = {}
    for key, field in (("funds", "funds"), ("sci", "science"), ("rep", "reputation")):
        for line in lines:
            if line.startswith(key + " = "):
                got[field] = float(line.split("=", 1)[1].strip())
                break
    return oracle.SeedBaseline(funds=got.get("funds"), science=got.get("science"),
                               reputation=got.get("reputation"))


def _steps(spec):
    return spec["driver"]["steps"]


def _cmds(spec):
    return [s.get("cmd") for s in _steps(spec)]


def _index_of_cmd(spec, cmd):
    for i, s in enumerate(_steps(spec)):
        if s.get("cmd") == cmd:
            return i
    raise AssertionError("no %s step" % cmd)


def _index_of_mission(spec):
    for i, s in enumerate(_steps(spec)):
        if s.get("phase") == "mission":
            return i
    raise AssertionError("no mission step")


# ---------------------------------------------------------------------------
# 1. The inversion of CL-1's pin.
# ---------------------------------------------------------------------------


class Cl2ExtendsCl1WithoutEditingItTests(unittest.TestCase):
    """CL-1 stays the ATOM and CL-2 is the extension. If a future change collapses
    them back into one spec, `test_cl1_crew_loss.py`'s pins and these have to be
    reconciled deliberately rather than silently."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec(SPEC_PATH)
        cls.cl1 = _read_spec(CL1_SPEC_PATH)

    def test_cl1_is_untouched_and_still_declares_no_ledger_block(self):
        # The mirror of test_cl1_crew_loss.py's own cell, asserted from THIS side:
        # the whole reason CL-2 exists as a separate spec id is that adding a ledger
        # block or a commit route to CL-1 would red that cell. A future author who
        # "consolidates" the two specs reds here as well as there.
        self.assertNotIn("ledger", self.cl1["expectations"])
        self.assertNotIn("CommitTree", [s.get("cmd") for s in self.cl1["driver"]["steps"]])
        self.assertNotIn("ExitToSpaceCenter",
                         [s.get("cmd") for s in self.cl1["driver"]["steps"]])

    def test_the_two_specs_are_distinct_ids(self):
        self.assertEqual("CL-2-pod-impact-ledger", self.spec["id"])
        self.assertNotEqual(self.cl1["id"], self.spec["id"])

    def test_the_fixture_is_shared_verbatim(self):
        # NOT a new fixture: `FixtureDriftTests` (test_cl1_crew_loss.py) is what keeps
        # career-pad-craft honest, and it only guards the ONE path. A CL-2 that forked
        # the fixture would be unguarded.
        self.assertEqual(self.cl1["fixture"], self.spec["fixture"])

    def test_the_mission_and_its_params_are_shared_verbatim(self):
        # The flight profile is the claim "same atom, one more step". A divergent
        # missionParams value here would fork the profile the two specs share, and the
        # measured numbers in CL-2's ledger manifest come from CL-1's flights.
        self.assertEqual("cl1_pod_impact", self.spec["driver"]["mission"])
        self.assertEqual(self.cl1["driver"]["mission"], self.spec["driver"]["mission"])
        self.assertEqual(self.cl1["driver"]["missionParams"],
                         self.spec["driver"]["missionParams"])

    def test_this_spec_does_drive_a_commit_route_and_does_declare_a_ledger_block(self):
        # THE INVERSION, in one cell. CL-1's cell asserts the negatives because the
        # commit was UNREACHABLE on its profile; CL-2 exists because R12's
        # ExitToSpaceCenter made it reachable, so the positives are the contract.
        self.assertIn("ExitToSpaceCenter", _cmds(self.spec))
        self.assertIn("ledger", self.spec["expectations"])
        self.assertIsInstance(self.spec["expectations"]["ledger"], dict)

    def test_the_mission_still_declares_no_handoff_contract(self):
        # ExitToSpaceCenter is a POST_MISSION_ROLE_RECORDING verb, so the mission
        # still terminates ON the outcome it certifies and a handoff disclaimer would
        # be false. (A future OUTCOME-role post-mission verb would change this.)
        self.assertIsNone(mlib.mission_handoff_contract(self.spec["driver"]["mission"]))

    def test_every_cl1_required_token_survives_into_cl2(self):
        # CL-2 is a SUPERSET of the atom, not a divergent fork: the recorder-side
        # tokens must all still be gated, so a regression in the destruction path
        # reds on both specs rather than only on the nightly one.
        cl2_required = self.spec["expectations"]["logContracts"]["required"]
        for token in self.cl1["expectations"]["logContracts"]["required"]:
            self.assertIn(token, cl2_required,
                          "CL-2 dropped CL-1's required token %r; it must remain a "
                          "superset of the atom" % token)

    def test_the_forbidden_pattern_is_unchanged(self):
        self.assertEqual(self.cl1["expectations"]["logContracts"]["forbidden"],
                         self.spec["expectations"]["logContracts"]["forbidden"])


# ---------------------------------------------------------------------------
# 2. The step list.
# ---------------------------------------------------------------------------


class Cl2StepListTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec(SPEC_PATH)

    def test_automerge_is_set_true_before_the_flight(self):
        # LOAD-BEARING AND DERIVED FROM THE PRODUCT'S OWN PURE FUNCTIONS.
        # SceneExitInterceptor.ShouldShowDialogBeforeSceneChange returns
        # DialogVariant.RegularMerge when `!isAutoMerge`, and
        # TestCommandExitToSpaceCenter.DecideExitGate refuses on any non-None pending
        # tree variant - so with autoMerge OFF the exit REJECTS with
        # msg=dialog-required and the whole scenario proves nothing. It must also be
        # set BEFORE the mission, so the setting is already live on every path the
        # destruction handler takes.
        setting = [(i, s) for i, s in enumerate(_steps(self.spec))
                   if s.get("cmd") == "SetSetting"
                   and (s.get("args") or {}).get("name") == "autoMerge"]
        self.assertEqual(1, len(setting), "exactly one autoMerge SetSetting step")
        i, step = setting[0]
        self.assertEqual("true", step["args"]["value"],
                         "the value is the STRING 'true' (the SetSetting arg convention)")
        self.assertLess(i, _index_of_mission(self.spec),
                        "autoMerge must be set BEFORE the mission flies")

    def test_auto_record_on_launch_is_set_before_the_flight(self):
        setting = [(i, s) for i, s in enumerate(_steps(self.spec))
                   if s.get("cmd") == "SetSetting"
                   and (s.get("args") or {}).get("name") == "autoRecordOnLaunch"]
        self.assertEqual(1, len(setting))
        self.assertEqual("true", setting[0][1]["args"]["value"])
        self.assertLess(setting[0][0], _index_of_mission(self.spec))

    def test_the_exit_runs_after_the_mission(self):
        # The exit is what reaches the auto-commit, and the tree it commits is the one
        # the CRASH stashed. Driving it before the flight would exit an empty scene.
        self.assertLess(_index_of_mission(self.spec),
                        _index_of_cmd(self.spec, "ExitToSpaceCenter"))

    def test_the_exit_step_budget_clears_its_c_sharp_deferral_budget(self):
        # A per-step budget below the C# DeferralBudget would time the step out from
        # the harness side while the verb was still legitimately waiting for the scene
        # to settle - an INVALID that looks like a product hang.
        step = _steps(self.spec)[_index_of_cmd(self.spec, "ExitToSpaceCenter")]
        self.assertGreaterEqual(float(step["budget"]),
                                hlib.DISPATCH_DEFERRAL_BUDGET_SECONDS["ExitToSpaceCenter"])

    def test_the_exit_is_a_recording_role_post_mission_verb(self):
        # ORTHOGONALITY, pinned. A RECORDING-role post-mission verb does NOT gate as
        # PARSEK-FAIL(mission-outcome): whether the pending tree auto-committed is
        # proven by the grep-stable commit tokens, so a good flight Parsek then failed
        # to commit is a PARSEK-FAIL(expectation), never a retryable driver-INVALID.
        self.assertEqual(hlib.POST_MISSION_ROLE_RECORDING,
                         hlib.SEAM_VERB_POST_MISSION_ROLE["ExitToSpaceCenter"])

    def test_the_exit_is_world_mutating_so_an_unmet_mission_skips_it(self):
        # The other half of the same guarantee: hlib.plan_unmet_mission_tail runs
        # CLEANUP steps only, so a mission that did not fly never commits its junk
        # tree or applies its resource deltas to the career.
        self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                         hlib.SEAM_VERB_TAIL_ROLE["ExitToSpaceCenter"])

    def test_no_commit_tree_step(self):
        # STILL unreachable, for CL-1's exact reason: the destroyed-vessel path nulls
        # activeTree, so CommitTreeImpl fails its HasActiveTree guard. The commit this
        # spec proves arrives by the SCENE-EXIT route, not by the verb.
        self.assertNotIn("CommitTree", _cmds(self.spec))

    def test_no_stop_recording_step(self):
        # The destruction path stops the recorder itself, inside FinalizeTreeRecordings.
        self.assertNotIn("StopRecording", _cmds(self.spec))

    def test_the_mission_step_expects_mission_ok(self):
        # The inversion CL-1 established, restated here: the death is the SUCCESS
        # terminal, and an unmet mission would SKIP every verifier below driverValidity
        # and produce no evidence about the commit at all.
        step = _steps(self.spec)[_index_of_mission(self.spec)]
        self.assertEqual("MISSION-OK", step["expect"])

    def test_the_wall_budget_covers_every_step_budget(self):
        # budgetSeconds bounds the WHOLE drive phase INCLUDING KSP boot, so it must
        # exceed the sum of the per-step budgets with room for boot.
        total = sum(float(s.get("budget", 0) or 0) for s in _steps(self.spec))
        self.assertGreater(float(self.spec["runtime"]["budgetSeconds"]), total,
                           "the wall budget must leave headroom over the step budgets "
                           "for KSP boot")

    def test_the_last_step_flushes_and_quits(self):
        self.assertEqual("FlushAndQuit", _cmds(self.spec)[-1])


# ---------------------------------------------------------------------------
# 3. The ledger block, through the harness's own code.
# ---------------------------------------------------------------------------


class Cl2LedgerBlockTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec(SPEC_PATH)
        cls.block = cls.spec["expectations"]["ledger"]
        cls.seed = _fixture_seed(cls.spec)

    def test_the_block_passes_the_pre_launch_validator(self):
        # The same call run.py makes before it will launch KSP. A malformed block is
        # a terminal spec error; catching it here costs nothing instead of an attempt.
        self.assertEqual([], hlib.validate_ledger_expectations(self.block))

    def test_the_seed_comes_from_the_template(self):
        self.assertEqual("template", self.block.get("seedFrom", "template"))

    def test_the_capture_cross_check_is_armed_as_a_gate(self):
        # THE RECORDED DECISION. This cell asserted "report" from the day the spec
        # landed, precisely so that arming would be a deliberate act rather than a
        # drift. That act was taken 2026-07-31 and this cell is its record: the
        # scenario now GATES, and a later silent retreat to "report" reds here.
        #
        # hlib's own note said the cross-check "was WRITTEN as a hard gate, but it has
        # never once run with a working capture", and that arming wants a
        # reputation-producing scenario - which this is, making it the intended first
        # arm site. It is also the ONLY viable one: the capture is reputation-only
        # (KSP logs no funds and no science award line), and no other committed spec
        # is measured producing reputation, so arming anywhere else would arm a check
        # whose input is provably always empty.
        #
        # THE EVIDENCE, one flight per checklist step, all PASS on attempt 1:
        #   `2026-07-31_1630`  baseline, spec unchanged: stockLines=3 deduped=3
        #                      seamRejected=0, all three awards UNEXPECTED
        #                      (report-only) at ut 12.5 / 19.1 / 119.9.
        #   `2026-07-31_1638`  utWindows declared, still "report": ZERO unexpected
        #                      rows, ledgerOracle hardDivergences=0 reportOnly=0
        #                      (3 -> 0), expected totals byte-identical.
        #   `2026-07-31_1645`  armed: PASS with the gate live.
        self.assertEqual("gate", self.block.get("captureCrossCheck", "report"))
        self.assertTrue(hlib.capture_cross_check_gates(self.block))

    def test_the_armed_windows_are_phase_bounds_not_pins(self):
        # WHAT MAKES THE GATE SURVIVABLE. The exact award UTs are NOT stable - the
        # second Progression measured 15.98 (archived CL-1), 19.0 (2026-07-30),
        # 19.1 / 19.0 / 19.1 (the three arming flights), and the impact measured
        # 119.7 / 119.8 / 119.9 across six archived runs - so every armed entry must
        # carry a WINDOW and never an exact `ut`. An entry that regressed to an exact
        # ut would red the next flight for a reason that is not a defect.
        self.assertEqual(4, len(self.block["manifest"]),
                         "a 5th manifest entry appeared: re-derive the windows and "
                         "this cell rather than letting it ride unasserted")
        windows = {}
        for e in self.block["manifest"]:
            self.assertIsNone(e.get("ut"), "an armed entry must not pin an exact ut")
            self.assertIn("seq", e, "every entry pins its own seq ordinal")
            windows[e["seq"]] = e.get("utWindow")
        self.assertEqual([0.0, 100.0], windows[0])
        self.assertEqual([0.0, 100.0], windows[1])
        self.assertEqual([100.0, 400.0], windows[2])
        # The funds milestone entry stays window-free ON PURPOSE: KSP logs no funds
        # award at all (hlib.STOCK_AWARD_PATTERNS_DEAD), so it is never
        # capture-matched and a window on it would be decoration implying a check
        # that cannot run.
        self.assertIsNone(windows[3])

    def test_the_armed_windows_are_bounded_in_absolute_ut_with_pad_dwell_headroom(self):
        # THE CLOCK. The matched value is the captured award's raw Planetarium
        # `ut=`, which is `fixtureUT + padDwell + T+` - and pad dwell is a
        # wall-clock quantity (game time runs 1:1 here, no warp), so the ceilings
        # must clear the harness's own connect budget, not just the flight profile.
        # The first draft used 140 and left less headroom than
        # CONNECT_BUDGET_SECONDS, which would red a good flight with no retry.
        from mission_runner import CONNECT_BUDGET_SECONDS
        windows = {e["seq"]: e.get("utWindow") for e in self.block["manifest"]}
        worst_measured = {0: 12.5, 1: 19.1, 2: 119.9}
        for seq, measured in worst_measured.items():
            headroom = windows[seq][1] - measured
            self.assertGreater(
                headroom, CONNECT_BUDGET_SECONDS,
                "seq=%d headroom %.1f s must exceed the %.1f s connect budget: the "
                "award UT shifts one-for-one with pad dwell, and should_retry never "
                "retries a PARSEK-FAIL" % (seq, headroom, CONNECT_BUDGET_SECONDS))

    def test_the_armed_entries_keep_their_stock_reason_pins(self):
        # WITHOUT THESE THE GATE SILENTLY WIDENS. `stockReason` is what makes an
        # entry explain one NAMED stock effect; drop it and the entry accepts any
        # award of the right amount inside its window, so a genuinely new stock
        # reputation effect of the same size would be corroborated instead of
        # red. Nothing else in the suite pins the two Progression entries' reasons.
        reasons = {e["seq"]: e.get("stockReason") for e in self.block["manifest"]}
        self.assertEqual("Progression", reasons[0])
        self.assertEqual("Progression", reasons[1])
        self.assertEqual("VesselLoss", reasons[2])
        # Demonstrate the consequence rather than asserting it abstractly: an
        # undeclared ContractReward of the same size must stay unexpected.
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        tol = oracle.default_tolerances()
        ctol = {"funds": tol.funds, "science": tol.science, "reputation": tol.reputation}
        novel = hlib.CapturedAward(
            kind="stock-reputation-award", facet="reputation", amount=0.9999995,
            contract_guid="", subject_id="", ut=30.0, seq=7, raw_line="",
            reason="ContractReward")
        self.assertEqual([30.0], [u.ut for u in hlib.unmatched_captured_awards(
            parse.entries, [novel], ctol)])

    def test_the_armed_windows_corroborate_the_measured_award_set(self):
        # THE CELL THAT WOULD HAVE CAUGHT A BAD ARMING WITHOUT A FLIGHT. Replays the
        # three awards the 1630 baseline actually captured through the SAME matcher
        # run.py runs, at the SAME default tolerances: with the gate armed, a single
        # unmatched award here is a PARSEK-FAIL(ledger) on the next real flight.
        measured = [(12.5, 0.9999995, "Progression"),
                    (19.1, 0.9999995, "Progression"),
                    (119.9, -9.999828, "VesselLoss")]
        awards = [hlib.CapturedAward(
            kind="stock-reputation-award", facet="reputation", amount=amt,
            contract_guid="", subject_id="", ut=ut, seq=i, raw_line="", reason=reason)
            for i, (ut, amt, reason) in enumerate(measured)]
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        self.assertEqual([], list(parse.errors))
        tol = oracle.default_tolerances()
        unmatched = hlib.unmatched_captured_awards(
            parse.entries, awards,
            {"funds": tol.funds, "science": tol.science, "reputation": tol.reputation})
        self.assertEqual([], [(u.ut, u.reason) for u in unmatched])
        # AND THE GATE STILL BITES, tested so that the WINDOW is what decides.
        # An earlier version appended the stray AFTER the three measured awards,
        # which had already consumed all three entries under the one-to-one rule -
        # so the stray came back unexpected no matter how wide the windows were, and
        # the cell was insensitive to the very bounds it claimed to test. Judge the
        # stray ALONE against the entries instead.
        ctol = {"funds": tol.funds, "science": tol.science, "reputation": tol.reputation}
        out_of_phase = hlib.CapturedAward(
            kind="stock-reputation-award", facet="reputation", amount=0.9999995,
            contract_guid="", subject_id="", ut=900.0, seq=9, raw_line="",
            reason="Progression")
        self.assertEqual([900.0], [u.ut for u in hlib.unmatched_captured_awards(
            parse.entries, [out_of_phase], ctol)],
            "a Progression award far outside the ascent window must stay unexpected")
        # A SECOND VesselLoss inside the impact window is the case the one-to-one
        # rule owns: the first consumes the entry, the second has nothing left.
        def vessel_loss(ut, seq):
            return hlib.CapturedAward(
                kind="stock-reputation-award", facet="reputation", amount=-9.999828,
                contract_guid="", subject_id="", ut=ut, seq=seq, raw_line="",
                reason="VesselLoss")
        doubled = hlib.unmatched_captured_awards(
            parse.entries, [vessel_loss(119.9, 3), vessel_loss(150.0, 4)], ctol)
        self.assertEqual([150.0], [u.ut for u in doubled],
                         "a second VesselLoss must stay unexpected even in-window")

    def test_every_manifest_entry_parses_with_no_rejections(self):
        # run.py warn-logs a rejected seam entry and the caller reds it as a DROPPED
        # EXPECTED EFFECT. A rejection here is a scenario-authoring defect that would
        # silently remove a declared delta from the expected totals.
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        self.assertEqual([], list(parse.errors))
        self.assertEqual(len(self.block["manifest"]), len(parse.entries))

    def test_the_fixture_seed_is_the_career_baseline_the_manifest_was_written_against(self):
        # The manifest declares DELTAS, so every expected total is seed + delta. If the
        # committed fixture's pools move, the numbers below stop meaning what they say.
        self.assertEqual(500000.0, self.seed.funds)
        self.assertEqual(100.0, self.seed.science)
        self.assertEqual(0.0, self.seed.reputation)

    def test_the_arithmetic_closes_against_the_measured_produced_pools(self):
        # THE CELL THAT EARNS ITS KEEP. A manifest that does not close is a
        # self-inflicted PARSEK-FAIL(ledger) on a HARD facet, and the only way to find
        # it without this cell is to burn a live flight. Run through the SAME
        # compute_expected + diff the harness runs, at the SAME default tolerances.
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        expected = oracle.compute_expected(self.seed, parse.entries,
                                           oracle.default_tolerances())
        produced = {"parsed": True,
                    "hasFunds": True, "funds": PRODUCED_FUNDS,
                    "hasScience": True, "sciencePool": PRODUCED_SCIENCE,
                    "hasRep": True, "reputation": PRODUCED_REPUTATION}
        divergences = oracle.diff_expected_vs_parsed(expected, produced,
                                                     oracle.default_tolerances())
        hard = [d for d in divergences if d.facet in oracle.HARD_FACETS]
        self.assertEqual([], hard,
                         "the declared manifest does not reproduce the measured "
                         "produced pools within the default tolerances: %r"
                         % ([(d.facet, d.expected, d.actual) for d in hard],))

    def test_the_reputation_entries_are_applied_not_nominal(self):
        # The measured amounts are what stock applied AFTER its own reputation curve,
        # so running them through apply_rep_curve a second time would double-distort
        # them. `gameevents-captured` defaults to `applied`; the entries state it
        # explicitly so the intent survives a default change, and this cell is what
        # notices if one is written as `nominal`.
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        for e in parse.entries:
            if e.reputation != 0.0:
                self.assertEqual(oracle.REP_MODE_APPLIED, e.rep_mode)

    def test_the_crew_death_rep_row_names_vessel_loss_not_crew_killed(self):
        # SETTLED BY MEASUREMENT, and the product map was corrected to match:
        # Assembly-CSharp has no `CrewKilled` member on TransactionReasons at all, and
        # the stock line on this profile reads `... reputation: 'VesselLoss'.`
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        deaths = [e for e in parse.entries if e.reputation < 0]
        self.assertEqual(1, len(deaths), "exactly one negative reputation row")
        self.assertIn("VesselLoss", deaths[0].stock_reasons)
        self.assertNotIn("CrewKilled", deaths[0].stock_reasons)

    def test_no_science_delta_is_declared(self):
        # An uncrewed-of-experiments 12 km hop with no recovery transmits nothing, and
        # the produced science pool is the seed pool unchanged. Declaring a science
        # delta would be inventing one.
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        self.assertEqual(0.0, sum(e.science for e in parse.entries))

    def test_the_funds_delta_matches_the_measured_pool_movement(self):
        parse = oracle.parse_manifest_entries(self.block["manifest"])
        self.assertAlmostEqual(PRODUCED_FUNDS - self.seed.funds,
                               sum(e.funds for e in parse.entries), places=6)

    def test_no_rewind_or_merge_dialog_step_pairs_with_the_ledger_block(self):
        # hlib hard-rejects [expectations.ledger] paired with InvokeRewind or
        # AnswerMergeDialog (a rewind rewrites the pools from a quicksave the
        # seed+manifest contract cannot reconstruct). ExitToSpaceCenter is legal
        # BECAUSE the effects were applied live and the commit only marks the tree
        # applied - see the spec banner. This cell pins that CL-2 stays on the legal
        # side, and stage B (which needs InvokeRewind) must therefore be its own spec
        # with no ledger block.
        for cmd in ("InvokeRewind", "AnswerMergeDialog"):
            self.assertNotIn(cmd, _cmds(self.spec))


# ---------------------------------------------------------------------------
# 4. Claim-is-not-gate.
# ---------------------------------------------------------------------------


class Cl2CoverageClaimTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec(SPEC_PATH)
        with open(REGISTRY_PATH, "rb") as fh:
            cls.registry = tomllib.load(fh)
        cls.claimed = cls.spec["dimensionsCovered"]
        cls.required = cls.spec["expectations"]["logContracts"]["required"]

    def test_every_claimed_value_exists_in_the_registry(self):
        for dim, values in self.claimed.items():
            for value in values:
                self.assertIn(value, self.registry[dim]["values"],
                              "%s value %r is not in coverage/registry.toml" % (dim, value))

    def test_the_two_values_s07_had_to_drop_are_claimed_here_with_a_proving_token(self):
        # S0.7 drafted `commit-scene-exit` and `auto-merge` and EMPTIED its D1 after
        # its live runs showed the tree was idle-discarded rather than committed. CL-2
        # is the flight that reaches the commit, so it claims both - and the claim is
        # only honest with the token that proves it in the same file.
        for value in ("commit-scene-exit", "auto-merge"):
            self.assertIn(value, self.claimed["D1"])
        self.assertTrue(
            any("Silent full-fidelity auto-commit" in p for p in self.required),
            "D1 commit-scene-exit / auto-merge are claimed with no auto-commit token "
            "in this spec's own logContracts - that is exactly the claim-is-not-gate "
            "error S0.7 had to correct")

    def test_the_kerbals_ledger_claim_has_its_proving_token(self):
        # D8 `kerbals` and `orchestrator` both ride the one commit-time line that
        # flight 1 actually measured, emitted only through
        # LedgerOrchestrator.OnRecordingCommitted's call chain.
        self.assertIn("kerbals", self.claimed["D8"])
        self.assertIn("orchestrator", self.claimed["D8"])
        self.assertTrue(
            any("CreateKerbalAssignmentActions: 1 crew members" in p
                for p in self.required),
            "D8 kerbals/orchestrator claimed without the CreateKerbalAssignmentActions "
            "token that proves a KerbalAssignment row was created at commit")

    def test_the_crew_end_state_token_is_deliberately_not_required(self):
        # A FINDING PINNED AS A CELL, so a future author does not "helpfully" add the
        # token back from the call site. `PopulateCrewEndStates` NEVER RUNS on a
        # destroyed-vessel commit: NeedsCrewEndStatePopulation requires
        # `rec.VesselSnapshot != null` and a destroyed vessel has none, so the
        # KerbalAssignment row is created with `KerbalEndState.Unknown` where the
        # product's own comment says `Dead`. Flight 1 measured ZERO occurrences of the
        # token anywhere in the log. Requiring it would red a correct run on a line
        # the product does not emit; the DEFECT is filed in
        # docs/dev/todo-and-known-bugs.md and is what has to change first.
        self.assertFalse(
            any("PopulateCrewEndStates" in p for p in self.required),
            "PopulateCrewEndStates is not emitted on the destroyed-vessel commit path "
            "(see docs/dev/todo-and-known-bugs.md); requiring it reds a correct run")
        self.assertFalse(any("dead=1" in p for p in self.required))

    def test_the_hard_oracle_facets_back_the_funds_and_reputation_claims(self):
        # These two are gated by the ledger oracle's HARD facets rather than by a log
        # token, so the claim requires a ledger block that actually moves them.
        for value in ("funds", "reputation"):
            self.assertIn(value, self.claimed["D8"])
        entries = oracle.parse_manifest_entries(
            self.spec["expectations"]["ledger"]["manifest"]).entries
        self.assertNotEqual(0.0, sum(e.funds for e in entries))
        self.assertNotEqual(0.0, sum(e.reputation for e in entries))

    def test_the_scope_fenced_re_fly_cells_stay_unclaimed(self):
        # THE SCOPE FENCE. SupersedeCommit is the ONLY producer of a LedgerTombstone
        # and CommitTombstones runs strictly inside the re-fly merge tail, which no
        # auto-commit reaches. Claiming any of them here would hide the backlog the
        # coverage report exists to show - and stage B is what closes them.
        self.assertNotIn("tombstones", self.claimed.get("D8", []))
        self.assertNotIn("tombstones", self.claimed.get("D9", []))
        for value in ("dead-crew-strip", "tombstone-rep-penalty"):
            self.assertNotIn(value, self.claimed.get("D12", []))

    def test_the_crew_death_value_is_still_claimed(self):
        self.assertIn("crew-death-in-flight", self.claimed["D12"])

    def test_scene_ksc_is_claimed_and_gated_by_the_completion_token(self):
        # DecideExitCompletion emits `complete scene=SPACECENTER game-loaded=true`
        # only from its CompleteOk branch, which requires a settled KSC with a game
        # loaded - so it is simultaneously the verb's OK and the scene-ksc assertion.
        self.assertIn("scene-ksc", self.claimed["D14"])
        self.assertTrue(any("complete scene=SPACECENTER game-loaded=true" in p
                            for p in self.required))

    def test_career_is_claimed_because_the_ledger_half_requires_it(self):
        self.assertIn("career", self.claimed["D14"])


class Cl2LogContractHygieneTests(unittest.TestCase):
    """CL-1's rules, restated for the new tokens: the emitting format strings use
    U+2192 and U+2014, so a spec author reaching for the obvious ASCII arrow writes
    a regex that matches nothing."""

    @classmethod
    def setUpClass(cls):
        cls.contracts = _read_spec(SPEC_PATH)["expectations"]["logContracts"]

    def test_every_pattern_compiles_and_is_ascii(self):
        for pattern in self.contracts["required"] + self.contracts["forbidden"]:
            re.compile(pattern)
            pattern.encode("ascii")

    def test_no_required_pattern_hard_codes_a_unicode_arrow(self):
        for pattern in self.contracts["required"]:
            self.assertNotIn("->", pattern,
                             "the CrewStatusChanged separator is U+2192, not '->'")

    def test_the_exit_start_line_pins_the_whole_guard_input_set(self):
        # This is what makes the wedge-guard claim falsifiable, and it is the MIRROR
        # IMAGE of S0.7's input set (`hasActiveTree=true hasPendingTree=false`): CL-2
        # exits with a STASHED tree, S0.7 with a LIVE one. A run that lost the
        # autoMerge pin, or arrived with a live active tree, reds HERE on the state.
        matching = [p for p in self.contracts["required"]
                    if p.startswith("exittospacecenter start")]
        self.assertEqual(1, len(matching))
        for token in ("scene=FLIGHT", "autoMerge=true", "hasActiveTree=false",
                      "hasPendingTree=true", "switchSegmentSession=false",
                      "activeTreeVariant=None", "pendingTreeVariant=None"):
            self.assertIn(token, matching[0])

    def test_the_scene_exit_stash_token_is_not_copied_from_s07(self):
        # On THIS profile the tree is stashed by the DESTRUCTION path minutes before
        # the exit, not by the scene-exit interceptor, so S0.7's
        # `CommitTreeSceneExit: stashed pending tree` is NOT guaranteed here.
        # Requiring it would red a correct run on a token that belongs to a different
        # entry path.
        self.assertFalse(any("CommitTreeSceneExit" in p for p in self.contracts["required"]))

    def test_the_commit_tokens_carry_their_numeric_guards(self):
        # logContract patterns are presence-only (re.search), so the LITERALS are the
        # only thing stopping a commit of an EMPTY tree, or a commit with no crew row,
        # from satisfying the pattern. Each literal below was measured verbatim on
        # flight 1; a future edit that relaxes one to `.*` reds here.
        joined = "\n".join(self.contracts["required"])
        for literal in ("recordings=1 spawnable=0",
                        "Total committed: 1 recordings, 1 trees",
                        "CreateKerbalAssignmentActions: 1 crew members",
                        "saving 1 committed tree"):
            self.assertIn(literal, joined,
                          "the commit tokens lost their numeric guard %r" % literal)

    def test_the_committed_tree_count_token_is_the_b1_negative_control_inverted(self):
        # THE SINGLE STRONGEST LINE IN THE FILE. The archived pre-commit run of this
        # exact craft logs `OnSave: saving 0 committed tree(s)`; flight 1 logged
        # `saving 0` three times BEFORE the exit and `saving 1` after it. Pinning the
        # ONE-tree form is what turns a wording coincidence into a before/after
        # discriminator, and a pattern that matched `saving 0` too would prove nothing.
        matching = [p for p in self.contracts["required"] if "committed tree" in p]
        self.assertEqual(1, len(matching))
        self.assertRegex("[Parsek][INFO][Scenario] OnSave: saving 1 committed tree(s)",
                         matching[0])
        self.assertNotRegex("[Parsek][INFO][Scenario] OnSave: saving 0 committed tree(s)",
                            matching[0])

    def test_every_required_pattern_matches_its_measured_line(self):
        # THE PATTERNS ARE REGEXES OVER MEASURED TEXT, and an escaping slip (an
        # unescaped paren, a stray `.`) produces a pattern that compiles and matches
        # NOTHING - which reds a green run and looks exactly like a product defect.
        # Each line below is quoted verbatim from flight 1's collected KSP.log.
        measured = {
            "Silent full-fidelity auto-commit":
                "[Parsek][INFO][Scenario] Silent full-fidelity auto-commit "
                "(scene-exit): tree='Jumping Flea' recordings=1 spawnable=0",
            "Committed tree":
                "[Parsek][INFO][RecordingStore] Committed tree 'Jumping Flea' "
                "(1 recordings). Total committed: 1 recordings, 1 trees",
            "CreateKerbalAssignmentActions":
                "[Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: "
                "1 crew members from '5c0ed4fa57c841d3afd3ead9ab33640b'",
            "exittospacecenter start":
                "[Parsek][INFO][TestCommands] exittospacecenter start scene=FLIGHT "
                "autoMerge=true hasActiveTree=false hasPendingTree=true "
                "switchSegmentSession=false activeTreeVariant=None "
                "pendingTreeVariant=None",
            "exittospacecenter driving":
                "[Parsek][INFO][TestCommands] exittospacecenter driving scene-exit "
                "dest=SPACECENTER persisted=true - the finalize/stash pipeline runs "
                "on the transition and the pending tree auto-commits on arrival",
            "exittospacecenter complete":
                "[Parsek][INFO][TestCommands] exittospacecenter complete "
                "scene=SPACECENTER game-loaded=true elapsed=2.4s",
        }
        for needle, line in measured.items():
            pattern = [p for p in self.contracts["required"] if needle in p]
            self.assertEqual(1, len(pattern),
                             "expected exactly one required pattern containing %r" % needle)
            self.assertRegex(line, pattern[0],
                             "the required pattern does not match the line flight 1 "
                             "actually emitted")

    def test_the_forbidden_error_pattern_is_kept(self):
        # The NEW error surfaces this spec adds - a throwing auto-commit,
        # `exittospacecenter failed-returned-to-menu` and `... timeout` - are all
        # failures that SHOULD red. Keeping the pattern is what makes them red.
        self.assertIn("\\[Parsek\\]\\[ERROR\\]", self.contracts["forbidden"])


if __name__ == "__main__":
    unittest.main()
