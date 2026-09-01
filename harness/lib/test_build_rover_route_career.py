"""Fixture gates for `rover-route-career`, the COSTED-DISPATCH lane host.

WHAT THIS FILE GUARDS. `rover-route-career` is `rover-route-recorded` stamped
into CAREER by `harness/tools/build_rover_route_career.py` - the roadmap's
Tier C item 9 subject, produced BY CONSTRUCTION so measuring the
`ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT` fix costs no harvest flight. Two
inputs are committed (`rover-route-recorded` and `fresh-career`), so unlike
`RoverRouteRecordedFixtureDriftTests` - whose input is a collected operator save
outside the repo - this one CAN re-run the build and assert byte-identity. That
is the `StrategyCareerFixtureDriftTests` shape, and it is what turns "a change to
either input" from a silent live-flight surprise into a local red.

FOUR CLAIMS NOTHING ELSE IN THE SUITE MAKES:

  * THE CAREER STAMP IS COMPLETE AND MINIMAL. The seven donor SCENARIO nodes are
    present exactly once each, `ScenarioNewGameIntro` (SANDBOX-only) is gone, and
    the resulting GAME-level scenario list is EXACTLY `fresh-career`'s plus
    `ParsekScenario`. A stamp that missed `Funding` would boot a career with a
    null `Funding.Instance`, `KspStatePatcher.PatchFunds` would log
    `funding-null (sandbox mode)`, and the lane would measure nothing while every
    driven step still answered OK.
  * THE PARSEK PAYLOAD IS UNTOUCHED. The `ParsekScenario` node and the whole
    `FLIGHTSTATE` are byte-identical to the sandbox sibling's, and the sidecar
    tree is a verbatim copy. THAT IS THE LANE'S ENTIRE PREMISE: RVR-4 is RVR-2's
    driver over the same bytes with `env.IsCareer` the only variable moved, so a
    payload difference would make the two runs incomparable.
  * THE FUNDS SEED SITS IN ITS SOLVED BAND, re-derived from the committed
    snapshot bytes rather than restated. See the builder's "THE FUNDS SEED"
    section for why the band is what a FUTURE funds-short lane needs and why THIS
    lane cannot collect that hold at all.
  * THE 18,200 OF COMMITTED MILESTONE EARNINGS IS STILL THERE. It is the reason
    the funds-short half is out of scope, so it is asserted as a POSITIVE fact:
    if a future ledger edit removed those rows the constraint would lift, and the
    next reader must find that out from a red rather than from the prose.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import re
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

SAVES_DIR = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES_DIR, "rover-route-career")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
BASE_SFS = os.path.join(SAVES_DIR, "rover-route-recorded", "persistent.sfs")
DONOR_SFS = os.path.join(SAVES_DIR, "fresh-career", "persistent.sfs")
SCENARIOS_DIR = os.path.join(_HARNESS, "scenarios")

# The five committed MilestoneAchievement awards in the fixture's own
# `Parsek/GameState/ledger.pgld`. Each has a DISTINCT milestoneId, so
# `MilestonesModule.ProcessAction` marks every one `Effective = true` on its
# first-hit branch and `FundsModule` pays all five.
LEDGER_MILESTONE_FUNDS = (4800, 4800, 5400, 2400, 800)


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_rover_route_career.py")
    spec = importlib.util.spec_from_file_location(
        "build_rover_route_career", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RoverRouteCareerFixtureDriftTests(unittest.TestCase):
    """WIRES `build_rover_route_career.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = cls.builder.read_lines(FIXTURE_SFS)
        cls.base_lines = cls.builder.read_lines(BASE_SFS)
        cls.donor_lines = cls.builder.read_lines(DONOR_SFS)

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        problems = self.builder.verify(
            FIXTURE_DIR, self.lines, self.base_lines, self.donor_lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_build_is_reproducible_from_the_current_inputs(self):
        """THE DERIVATION, RE-RUN. `RoverRouteRecordedFixtureDriftTests` cannot
        do this (its input is a collected save outside the repo); both of this
        one's inputs are committed, so byte-identity is assertable and a change
        to EITHER reds here instead of on a flight."""
        rebuilt = self.builder.build(self.base_lines, self.donor_lines)
        self.assertEqual(rebuilt, self.lines,
                         "the committed save is not what `build` produces from "
                         "the current rover-route-recorded + fresh-career bytes "
                         "- re-run harness/tools/build_rover_route_career.py")

    def test_the_scenario_list_is_exactly_fresh_careers_plus_parsek(self):
        """THE STAMP, stated as a SET-AND-ORDER equality rather than as seven
        presence checks, so a node lifted but mis-anchored also reds.

        KSP resolves ScenarioModules by name and does not care about order; a
        fixture whose ordering is arbitrary is a fixture whose next editor has to
        re-derive that. The ParsekScenario node is the base's own and is expected
        to stay last."""
        b = self.builder
        donor = b.scenario_names(self.donor_lines)
        mine = b.scenario_names(self.lines)
        self.assertEqual(donor + ["ParsekScenario"], mine)

    def test_the_sandbox_only_intro_node_is_gone(self):
        b = self.builder
        self.assertIn("ScenarioNewGameIntro", b.scenario_names(self.base_lines),
                      "the base no longer carries the node this build drops - "
                      "the drop step is now inert")
        self.assertNotIn("ScenarioNewGameIntro", b.scenario_names(self.lines))

    def test_the_parsek_payload_is_byte_identical_to_the_sandbox_sibling(self):
        """Run on its own as well as inside `verify`, because it is the lane's
        premise rather than one post-condition among many: RVR-4 is RVR-2's
        driver over the same recorded bytes with `env.IsCareer` the only variable
        moved."""
        problems = self.builder.verify_payload_unchanged(self.lines, self.base_lines)
        problems += self.builder.verify_flightstate_unchanged(self.lines, self.base_lines)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_sidecar_tree_is_a_verbatim_copy(self):
        """The `.prec` / `.pann` / craft / GameState files, compared as BYTES.

        `verify_tree` (inherited from the sandbox builder) asserts the SHAPE -
        the six GameState files by name, the sidecar floor, no harvest exhaust.
        It cannot notice a file whose CONTENT drifted, and the ledger is exactly
        such a file: it is what pays the 18,200 the seed reasoning rests on."""
        base_dir = os.path.join(SAVES_DIR, "rover-route-recorded")
        checked = 0
        for root, _dirs, files in os.walk(os.path.join(base_dir, "Parsek")):
            for name in files:
                src = os.path.join(root, name)
                dst = os.path.join(FIXTURE_DIR,
                                   os.path.relpath(src, base_dir))
                with self.subTest(sidecar=os.path.relpath(src, base_dir)):
                    self.assertTrue(os.path.isfile(dst),
                                    "%s missing from the career sibling" % name)
                    with open(src, "rb") as fh:
                        want = fh.read()
                    with open(dst, "rb") as fh:
                        got = fh.read()
                    self.assertEqual(want, got, name)
                checked += 1
        self.assertGreaterEqual(checked, 25,
                                "the sidecar walk found almost nothing - this "
                                "gate would be inert")

    def test_the_loadmeta_agrees_with_the_save(self):
        """Load-menu preview only, and nothing in the harness reads it - but a
        fixture whose two halves disagree teaches its next reader wrong."""
        b = self.builder
        meta = b.read_lines(os.path.join(FIXTURE_DIR, "persistent.loadmeta"))
        self.assertIn("gameMode = CAREER", meta)
        self.assertIn("funds = %d" % b.FUNDS_SEED, meta)
        self.assertIn("science = 100", meta)
        # Inherited from the base and NOT restamped: the world did not move.
        base_meta = b.read_lines(
            os.path.join(SAVES_DIR, "rover-route-recorded", "persistent.loadmeta"))
        for key in ("vesselCount = ", "UT = ", "reputationPercent = "):
            want = [l for l in base_meta if l.startswith(key)]
            self.assertEqual(want, [l for l in meta if l.startswith(key)], key)


class RoverRouteCareerSeedBandTests(unittest.TestCase):
    """THE FUNDS SEED, SOLVED RATHER THAN CHOSEN - and the constraint that keeps
    the roadmap's funds-short half out of this lane.

    Every number below is re-derived from the committed bytes plus the builder's
    pinned stock price table, so a re-harvest that changed the rover's part
    multiset, or an edit to the table, reds here rather than shipping a seed that
    no longer affords one dispatch (or affords two)."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = cls.builder.read_lines(FIXTURE_SFS)
        cls.readings = cls.builder.dispatch_cost_readings(FIXTURE_DIR, cls.lines)

    def test_the_seed_affords_exactly_one_dispatch_and_not_two(self):
        problems = self.builder.verify_seed_band(FIXTURE_DIR, self.lines)
        self.assertEqual([], problems, "\n".join(problems))
        lo = min(self.readings.values())
        hi = max(self.readings.values())
        seed = self.builder.FUNDS_SEED
        # The two bounds against EACH OTHER, so neither can move alone.
        self.assertGreaterEqual(seed, hi)
        self.assertLess(seed, 2.0 * lo)

    def test_the_three_cost_readings_are_the_three_bases_the_fallback_can_take(self):
        """WHICH reading is which, pinned by name.

        `ComputeDispatchFundsCostForRoute` prefers the ROOT's COMPLETE run
        manifest for the resource term and falls back through `SourceRefs` for
        the parts term, so the value it returns depends on two run-time
        decisions this suite cannot settle. Naming all three keeps the band
        honest AND tells a red flight which branch it took."""
        self.assertEqual(
            {"m2-launch-manifest", "legacy-expected-member",
             "legacy-alternate-member"},
            set(self.readings))
        # The parts term is the SAME for both candidate bases (both rovers carry
        # the identical 16-part multiset), so the three readings differ only in
        # their resource term. That is what makes the band narrow enough to size
        # a seed against at all.
        b = self.builder
        expected = b.snapshot_part_names(
            FIXTURE_DIR, b.EXPECTED_PARTS_BASIS_RECORDING_ID)
        alternate = b.snapshot_part_names(
            FIXTURE_DIR, b.ALTERNATE_PARTS_BASIS_RECORDING_ID)
        self.assertEqual(sorted(expected), sorted(alternate),
                         "the two candidate costing bases no longer carry the "
                         "same parts, so the band must be re-derived")
        self.assertEqual(b.parts_term(expected), b.parts_term(alternate))

    def test_the_root_still_carries_the_complete_launch_manifest(self):
        """The M2 basis's precondition, as a positive fact.

        A root whose manifest stopped being complete (`endCaptured` gone) drops
        the lane onto the legacy walk, which is still inside the band - but the
        EXPECTED reading would change, and a header claiming 7410 would be
        wrong."""
        launch = self.builder.root_launch_resources(self.lines)
        self.assertEqual({"LiquidFuel": 200.0}, launch)

    def test_the_committed_ledger_still_pays_the_milestones_that_block_funds_short(self):
        """THE CONSTRAINT, ASSERTED SO ITS REMOVAL IS A RED RATHER THAN A
        SURPRISE.

        `Parsek/GameState/ledger.pgld` carries five `MilestoneAchievement` rows
        totalling 18,200 funds, every one of which `MilestonesModule` marks
        Effective (distinct milestoneIds, first-hit branch) and `FundsModule`
        pays on top of whatever seed `LedgerOrchestrator.EnsureInitialFundsSeed`
        takes from the live pool. That is why effective funds at the first
        dispatch are `FUNDS_SEED + 18200` and why no positive seed can put a
        SECOND dispatch out of reach - the funds-short hold needs a different
        subject, not a different number. If a future edit removes these rows the
        constraint lifts, and this cell is where that must be noticed."""
        path = os.path.join(FIXTURE_DIR, "Parsek", "GameState", "ledger.pgld")
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        awards = [int(m) for m in
                  re.findall(r"milestoneFundsAwarded = (\d+)", text)]
        self.assertEqual(sorted(LEDGER_MILESTONE_FUNDS), sorted(awards))
        total = sum(awards)
        self.assertEqual(18200, total)
        # And the conclusion, from the numbers rather than from the prose.
        lo = min(self.readings.values())
        self.assertGreaterEqual(
            self.builder.FUNDS_SEED + total, 2.0 * lo,
            "the committed milestone awards no longer put a second dispatch "
            "inside reach - the funds-short hold may now be drivable on this "
            "fixture, so re-read the builder's seed section before pinning "
            "anything")
        # There is no FundsInitial row: the sandbox source never had one, which
        # is why the seed is taken from the live pool in the first place.
        self.assertNotIn("initialFunds", text)


class RoverRouteCareerSpecSyncTests(unittest.TestCase):
    """The spec-to-fixture pairing, which nothing else checks and which costs a
    live flight to get wrong (the `CL-1-pod-impact` lesson).

    TWO INSTRUMENTS, DELIBERATELY, because the two claims have different failure
    modes. The staged path and the tree id are checked as TEXT over the committed
    file - the sibling class's reasoning: a substring scan cannot be fooled by a
    spec that parses but names a different tree. The TOKEN claims are checked
    against the PARSED `expectations.logContracts.required` list instead, because
    those tokens also appear in the spec's own header prose: a whole-file
    substring scan for `fallback=1` passes against a spec whose required list has
    dropped it, which is exactly the "a fragment of pre-existing text passes"
    trap the DLL-verification recipe warns about, and it was measured passing
    against that mutation before this class was split."""

    SPEC = "RVR-4-rover-route-career-cost.toml"

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        path = os.path.join(SCENARIOS_DIR, cls.SPEC)
        with open(path, encoding="utf-8") as fh:
            cls.text = fh.read()
        with open(path, "rb") as fh:
            cls.spec = tomllib.load(fh)
        cls.required = list(
            ((cls.spec.get("expectations") or {}).get("logContracts") or {})
            .get("required") or [])

    def test_the_spec_stages_the_career_fixture_and_not_the_sandbox_one(self):
        self.assertIn('"fixtures/saves/rover-route-career"', self.text)
        self.assertNotIn('"fixtures/saves/rover-route-recorded"', self.text,
                         "RVR-4 must stage the CAREER sibling - the sandbox host "
                         "would make every career token unreachable while every "
                         "driven step still answered OK")

    def test_the_spec_names_the_transport_tree_id_verbatim(self):
        """The SealSlot and create steps address the tree by id. A spec naming
        the ENDPOINT tree would seal and promote the wrong one - and the endpoint
        tree carries no route window, so the create would answer
        `candidate-ineligible` rather than failing loudly."""
        recorded = self.builder.recorded
        self.assertIn(recorded.TRANSPORT_TREE_ID, self.text)
        self.assertNotIn('tree = "%s"' % recorded.ENDPOINT_TREE_ID, self.text)

    def test_the_spec_pins_the_career_tokens_the_lane_exists_for(self):
        """THE ANTI-VACUITY FLOOR for this lane's own product.

        RVR-4 is RVR-2's driver over a career save; without the two career
        tokens it would be RVR-2 again, passing green and proving nothing about
        the costing fix. Checked against the PARSED required list, not the file
        text: every fragment below also appears in the spec's header prose, so a
        whole-file scan would pass against a spec that had dropped the token
        itself (measured - dropping `fallback=1` from the required entry left a
        text-scanning version of this cell green)."""
        blob = "\n".join(self.required)
        for token in ("FundsCost basis=", "snapshotSource=", "fallback=1",
                      "DispatchDebit: route ", "careerKsc=1"):
            with self.subTest(token=token):
                self.assertIn(token, blob,
                              "the required logContract list no longer pins %r"
                              % token)
        # `fallback=1` and `careerKsc=1` are the two ASSERTIONS rather than
        # context, so each is additionally required to sit in the SAME entry as
        # the emitter that owns it - a spec that split them onto a bare
        # `fallback=1` line would match some other emitter's output.
        self.assertTrue(
            any("FundsCost basis=" in t and "fallback=1" in t for t in self.required),
            "`fallback=1` must be pinned inside the FundsCost token - alone it "
            "would match any line carrying that fragment")
        self.assertTrue(
            any("DispatchDebit: route " in t and "careerKsc=1" in t
                for t in self.required),
            "`careerKsc=1` must be pinned inside the DispatchDebit token")
        # And the charge must be pinned as NON-ZERO: `cost=0` is precisely what
        # the pre-fix defect produced, so a token matching any cost is vacuous.
        self.assertTrue(
            any("DispatchDebit: route " in t and "cost=[1-9]" in t
                for t in self.required),
            "the DispatchDebit token must require a NON-ZERO cost - cost=0 is "
            "what ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT produced")

    def test_the_spec_does_not_pin_a_funds_short_hold(self):
        """The half this fixture CANNOT buy, gated so it is not added by
        analogy. See `RoverRouteCareerSeedBandTests` and the builder's seed
        section: the committed ledger's 18,200 of milestone awards puts a second
        dispatch inside reach for any positive seed, and only one cycle ever
        charges (a blocked cycle returns before `EmitLoopCycle`).

        Over the PARSED required list, so the header may (and does) discuss
        FundsShort at length without tripping it."""
        for token in self.required:
            self.assertNotIn("FundsShort", token,
                             "a FundsShort token cannot fire on this fixture - "
                             "it would red a correct run")

    def test_the_spec_does_not_arm_render_composition_capture(self):
        """Same reason RVR-2 does not: declaring the block sets
        `PARSEK_RENDER_MANIFEST=1`, and a confirmed dock crossing appends
        `route-dock-crossing` CLOCK-EVENTs to that manifest."""
        self.assertNotIn("[expectations.renderComposition]", self.text)
        self.assertNotIn("ExportRenderManifest", self.text)


if __name__ == "__main__":
    unittest.main()
