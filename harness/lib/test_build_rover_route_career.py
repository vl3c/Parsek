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
    snapshot bytes rather than restated - and flight 2 proved the band is THIS
    lane's own gate rather than a future lane's convenience. See the builder's
    "THE FUNDS SEED" section.
  * THE MEASURED COST AND SHORTFALL ARE RE-DERIVABLE FROM THE FIXTURE. 7410 and
    3820 are pinned in the spec; both are recomputed here from the committed
    snapshot's parts, the root's launch manifest and `FUNDS_SEED`, so a
    re-harvest that moved the rover reds locally instead of on the re-fly.
  * THE 18,200 OF COMMITTED MILESTONE EARNINGS IS STILL THERE, and it is now
    asserted as a LEDGER-SIDE fact. It raises the running balance to 29,200; the
    live pool never sees it, because `PatchFunds`' guarded uplift holds the pool
    at the spent value. That clamp is what makes the seed the pool, and the pool
    is what makes cycle 1 go short.

THIS FILE'S FIRST CUT ASSERTED THE OPPOSITE OF TWO OF THOSE, and the corrections
are kept visible in the cells rather than swapped in silently: it FORBADE a
`FundsShort` token and encoded the funds-short hold as structurally unreachable.
The missing step was `PatchFunds` - a ledger amount is not a live pool amount.

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
    """THE FUNDS SEED, SOLVED RATHER THAN CHOSEN - and, since flight 2, the gate
    that makes the roadmap's funds-short half land IN this lane.

    Every number below is re-derived from the committed bytes plus the builder's
    pinned stock price table, so a re-harvest that changed the rover's part
    multiset, or an edit to the table, reds here rather than shipping a seed that
    no longer affords one dispatch (or affords two) - either of which would move
    the lane's measured outcome without moving a single token."""

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

    def test_the_three_cost_readings_are_the_bases_the_resolver_can_take(self):
        """WHICH reading is which, pinned by name.

        `measured-root-ghost-launch-manifest` is the basis flight 2 took. The two
        legacy readings are KEPT rather than trimmed: which surface and which
        member the resolver reaches is a RUN-TIME decision this suite cannot
        settle for every future DLL - flight 2 proved that by taking a branch the
        spec had not imagined - and they are what the seed band is sized
        against."""
        self.assertEqual(
            {"measured-root-ghost-launch-manifest", "legacy-alternate-member",
             "legacy-second-alternate-member"},
            set(self.readings))
        # The parts term is the SAME for every candidate (all three snapshots
        # carry the identical 16-part multiset), so the readings differ only in
        # their resource term. That is what makes the band narrow enough to size
        # a seed against at all.
        b = self.builder
        measured = b.snapshot_part_names(
            FIXTURE_DIR, b.MEASURED_PARTS_BASIS_RECORDING_ID,
            b.MEASURED_PARTS_BASIS_SURFACE)
        alternate = b.snapshot_part_names(
            FIXTURE_DIR, b.ALTERNATE_PARTS_BASIS_RECORDING_ID)
        second = b.snapshot_part_names(
            FIXTURE_DIR, b.SECOND_ALTERNATE_PARTS_BASIS_RECORDING_ID)
        self.assertEqual(sorted(measured), sorted(alternate),
                         "the candidate costing bases no longer carry the same "
                         "parts, so the band must be re-derived")
        self.assertEqual(sorted(measured), sorted(second))
        self.assertEqual(b.parts_term(measured), b.parts_term(alternate))

    def test_the_measured_cost_and_shortfall_are_re_derivable(self):
        """THE TWO NUMBERS THE SPEC PINS, re-derived from the committed bytes.

        Flight 2 measured `cost=7410.0000023841858` and
        `shortfall=3820.0000047683716`; the spec pins their integer parts. This
        cell is what keeps those pins tied to the FIXTURE rather than to a
        transcription: the cost comes from the committed snapshot's parts plus
        the root's launch manifest, and the shortfall is `2 * cost - FUNDS_SEED`,
        which is the whole arithmetic of the lane.

        The float tails are float32 accumulation in `AvailablePart.cost` sums and
        are deliberately not re-derived - the integer part is the claim."""
        b = self.builder
        cost = self.readings["measured-root-ghost-launch-manifest"]
        self.assertEqual(7410, int(cost))
        self.assertAlmostEqual(b.MEASURED_DISPATCH_COST, cost, places=4)
        shortfall = b.expected_funds_shortfall(FIXTURE_DIR, self.lines)
        self.assertEqual(3820, int(shortfall))
        self.assertAlmostEqual(b.MEASURED_FUNDS_SHORTFALL, shortfall, places=4)
        # And the identity the spec header rests on, stated as arithmetic.
        self.assertAlmostEqual(2.0 * cost - b.FUNDS_SEED, shortfall, places=9)

    def test_the_measured_basis_is_the_roots_own_ghost_surface(self):
        """WHY `fallback=0 snapshotSurface=ghost` is pinnable at all.

        `VesselSnapshot` is the SPAWN surface, and ParsekScenario's OnLoad
        crew-auto-unreserve sweep nulls it in memory for every committed
        recording past its EndUT - which is why flight 1 read `UNCOSTED` with the
        `_vessel.craft` sidecars intact on disk. `ResolveCostingSnapshot` falls
        back to `GhostVisualSnapshot`, and the ROOT has one even though it has NO
        `_vessel.craft` at all. Both halves are facts about the committed sidecar
        set, so both are asserted here rather than left to the flight."""
        b = self.builder
        recordings = os.path.join(FIXTURE_DIR, "Parsek", "Recordings")
        root = b.MEASURED_PARTS_BASIS_RECORDING_ID
        self.assertFalse(
            os.path.isfile(os.path.join(recordings, root + "_vessel.craft")),
            "the root gained a _vessel.craft - the ghost-surface fallback this "
            "lane measures would no longer be the path taken, and the spec's "
            "snapshotSurface=ghost pin would red on a correct run")
        self.assertTrue(
            os.path.isfile(os.path.join(recordings, root + "_ghost.craft")),
            "the root lost its _ghost.craft - the measured costing basis is gone "
            "and the dispatch would be UNCOSTED again")
        self.assertEqual(
            16, len(b.snapshot_part_names(FIXTURE_DIR, root, "ghost")))

    def test_the_root_still_carries_the_complete_launch_manifest(self):
        """The M2 basis's precondition, as a positive fact.

        A root whose manifest stopped being complete (`endCaptured` gone) drops
        the lane onto the legacy walk, which is still inside the band - but the
        EXPECTED reading would change, and a header claiming 7410 would be
        wrong."""
        launch = self.builder.root_launch_resources(self.lines)
        self.assertEqual({"LiquidFuel": 200.0}, launch)

    def test_the_committed_ledger_milestone_awards_stay_ledger_side(self):
        """THE 18,200, AND THE CLAMP THAT KEEPS IT OUT OF THE LIVE POOL.

        THIS CELL USED TO ASSERT THE OPPOSITE CONCLUSION. It kept the same five
        rows as a positive fact but concluded from them that the funds-short hold
        was unreachable, because `FundsModule` adds every one (distinct
        milestoneIds, first-hit branch) to the running balance on top of a seed
        `EnsureInitialFundsSeed` takes from the live pool. FLIGHT 2 CONFIRMED THE
        PREMISE AND REFUTED THE CONCLUSION:

            PatchFunds: GUARDED UPLIFT clamped resource=Funds running=29200
                        live=11000 wouldBeTarget=29200 clampedTo=11000
                        - spent value held; ledger may be missing a spending channel

        `PatchFunds` runs its target through `ApplyDrawdownGuard`, and the "keep
        what you earned" guard REFUSES an upward patch whose running balance
        exceeds the live pool. So the awards are LEDGER-SIDE ONLY: the running
        balance is 11000 + 18200 = 29200 while the live pool stays at the 11000
        seed - which is what puts the seed band in charge of the lane.

        The rows are still asserted, for the mirrored reason: if a future edit
        removed them the running balance would stop exceeding the live pool, the
        clamp would stop firing, and the mechanism this lane's arithmetic depends
        on would have changed silently."""
        path = os.path.join(FIXTURE_DIR, "Parsek", "GameState", "ledger.pgld")
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        awards = [int(m) for m in
                  re.findall(r"milestoneFundsAwarded = (\d+)", text)]
        self.assertEqual(sorted(LEDGER_MILESTONE_FUNDS), sorted(awards))
        total = sum(awards)
        self.assertEqual(18200, total)
        # The clamp's PRECONDITION, from the numbers rather than the prose: the
        # running balance must EXCEED the live pool or there is no uplift to
        # clamp, and the live pool would then carry the awards after all.
        seed = self.builder.FUNDS_SEED
        self.assertGreater(
            total, 0,
            "the milestone awards are gone, so PatchFunds has no uplift to clamp "
            "and the live-pool arithmetic this lane pins has changed")
        self.assertEqual(
            29200, seed + total,
            "the measured `running=` reading moved - re-read the flight log "
            "before trusting the spec's shortfall pin")
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

    def test_the_spec_pins_the_measured_career_tokens(self):
        """THE ANTI-VACUITY FLOOR for this lane's own product.

        RVR-4 is RVR-2's driver over a career save; without these tokens it would
        be RVR-2 again, passing green and proving nothing about the costing fix.
        Checked against the PARSED required list, not the file text: every
        fragment below also appears in the spec's header prose, so a whole-file
        scan would pass against a spec that had dropped the token itself
        (measured - dropping `fallback=0` from the required entry left a
        text-scanning version of this cell green).

        All of it is MEASURED on run `2026-09-01_2228`; nothing here is a
        prediction."""
        blob = "\n".join(self.required)
        for token in ("FundsCost basis=launch-manifest", "snapshotSource=",
                      "fallback=0 snapshotSurface=ghost",
                      "DispatchDebit: route ", "careerKsc=1",
                      "kind=FundsShort", "credit-skip zero-recovery"):
            with self.subTest(token=token):
                self.assertIn(token, blob,
                              "the required logContract list no longer pins %r"
                              % token)
        # Each ASSERTION must sit in the SAME entry as the emitter that owns it;
        # split onto a bare line it would match some other emitter's output.
        self.assertTrue(
            any("FundsCost basis=" in t and "fallback=0 snapshotSurface=ghost" in t
                for t in self.required),
            "`fallback=0 snapshotSurface=ghost` must be pinned inside the "
            "FundsCost token, and CONTIGUOUS: subsetTerm is emitted between "
            "those two fields, so the contiguity is what asserts the "
            "transport-subset path did not run")
        self.assertTrue(
            any("DispatchDebit: route " in t and "careerKsc=1" in t
                for t in self.required),
            "`careerKsc=1` must be pinned inside the DispatchDebit token")
        # And the armed-pause PROVENANCE: `hold kind=FundsShort` must sit in the
        # `armedBy=send-once` entry, not merely somewhere in the list. Without
        # this conjunct the cell passed against a spec whose ArmedPause token had
        # dropped the hold kind entirely, because the cycle-1 BLOCKED token still
        # carried the `kind=FundsShort` fragment (measured).
        self.assertTrue(
            any("armedBy=send-once" in t and "hold kind=FundsShort" in t
                for t in self.required),
            "`hold kind=FundsShort` must be pinned inside the armed-pause token "
            "- it is what says WHICH refusal consumed the one-shot, and the same "
            "line reads `hold kind=DestinationFull` in sandbox and on round 1")

    def test_the_spec_pins_the_measured_cost_and_shortfall_figures(self):
        """THE TWO FIGURES, tied to the builder's derivation rather than typed.

        `cost=0` is exactly what ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT
        produced and what flight 1 measured, so a token matching any cost would
        be vacuous. Both pins are checked against the values
        `dispatch_cost_readings` / `expected_funds_shortfall` derive from the
        committed bytes, so a re-harvest that moved the parts multiset reds here
        instead of on the re-fly."""
        b = self.builder
        lines = b.read_lines(FIXTURE_SFS)
        cost = int(b.dispatch_cost_readings(
            FIXTURE_DIR, lines)["measured-root-ghost-launch-manifest"])
        shortfall = int(b.expected_funds_shortfall(FIXTURE_DIR, lines))
        self.assertTrue(
            any("DispatchDebit: route " in t and ("cost=%d" % cost) in t
                for t in self.required),
            "the DispatchDebit token must pin the DERIVED cost %d" % cost)
        self.assertTrue(
            any("FundsCost basis=" in t and ("cost=%d" % cost) in t
                for t in self.required),
            "the FundsCost token must pin the DERIVED cost %d" % cost)
        self.assertTrue(
            any("kind=FundsShort" in t and ("shortfall=%d" % shortfall) in t
                for t in self.required),
            "the FundsShort token must pin the DERIVED shortfall %d "
            "(2 * cost - FUNDS_SEED)" % shortfall)

    def test_the_spec_does_not_pin_a_destination_full_cycle_1(self):
        """THE REFUSAL THAT NO LONGER HAPPENS, gated so it is not restored by
        analogy with RVR-2 (or with this lane's own round-1 log).

        `CheckEligibility` runs the Career funds gate at step 7 and the
        destination-capacity gate at step 8. BOTH would refuse cycle 1 on these
        bytes - the endpoint has 4.8 of LiquidFuel headroom left against a 97.6
        manifest - and once the dispatch is actually charged, FUNDS WINS. Round 1
        measured the other side of that ordering, with `cost=0` leaving the funds
        gate nothing to refuse. A DestinationFull token here would red a correct
        run.

        Over the PARSED required list, so the header may (and does) discuss
        DestinationFull at length without tripping it."""
        for token in self.required:
            self.assertNotIn(
                "DestinationFull", token,
                "cycle 1 blocks FundsShort on this fixture, not "
                "DestinationFull - the funds gate is step 7 and capacity is "
                "step 8")

    def test_the_spec_does_not_arm_render_composition_capture(self):
        """Same reason RVR-2 does not: declaring the block sets
        `PARSEK_RENDER_MANIFEST=1`, and a confirmed dock crossing appends
        `route-dock-crossing` CLOCK-EVENTs to that manifest."""
        self.assertNotIn("[expectations.renderComposition]", self.text)
        self.assertNotIn("ExportRenderManifest", self.text)


if __name__ == "__main__":
    unittest.main()
