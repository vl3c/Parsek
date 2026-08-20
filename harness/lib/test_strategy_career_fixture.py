"""Fixture + spec gates for `strategy-career` and `L3-strategy-currency-conversion`.

WHAT THIS FILE GUARDS. `strategy-career` exists for ONE reason: it is
`fresh-career` with a reputation seed large enough to let stock
`LeadershipInitiative` activate, which is what lets
`OperationStrategy_RewardMultiplier_IsNotCaptured` - the `StrategyLifecycle`
category's NEGATIVE CONTROL - run at all instead of self-skipping on
"Cannot afford Setup Cost: Not enough Reputation".

Every fact that makes the fixture the right one is a number read off stock
config or stock code, and every one of them is a cell here, so a change to any
of them reds in this suite rather than as a silent Skip on the next nightly:

  - the seed clears `Mathf.Lerp(10, 100, factorSliderDefault=0.05)` = 14.5;
  - the seed stays UNDER the next stock reputation threshold (35.0), so the
    activatable-strategy set - and with it the subject the two probe-driven
    cells in the same category pick up - moves as little as the requirement
    allows;
  - the save is BYTE-DERIVABLE from the current `fresh-career`, so a base edit
    cannot arrive here as anything but a rebuild;
  - `fresh-career` ITSELF still seeds rep 0, which is the negative half of the
    same decision: six committed specs stage `career-pad-craft`, which
    `build_career_pad_craft.py` derives from `fresh-career`, and two of them pin
    POST-CURVE reputation amounts that are functions of the pool. Seeding the
    base instead of siblinging it would have moved those pins;
  - the spec stages THIS fixture and pins the closed tally.

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

SAVES = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES, "strategy-career")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
FIXTURE_META = os.path.join(FIXTURE_DIR, "persistent.loadmeta")
BASE_SFS = os.path.join(SAVES, "fresh-career", "persistent.sfs")
BASE_META = os.path.join(SAVES, "fresh-career", "persistent.loadmeta")
SPEC_PATH = os.path.join(_HARNESS, "scenarios",
                         "L3-strategy-currency-conversion.toml")

# The cell the whole fixture exists for.
NEGATIVE_CONTROL = "OperationStrategy_RewardMultiplier_IsNotCaptured"


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_strategy_career.py")
    spec = importlib.util.spec_from_file_location("build_strategy_career", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class StrategyCareerFixtureDriftTests(unittest.TestCase):
    """WIRES `build_strategy_career.py --check` INTO THE SUITE.

    The `--check` path proves the committed bytes satisfy every post-condition;
    the rebuild cell proves they are what the builder produces from the CURRENT
    base. Unwired, "derived by a committed script" is prose with a shebang."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        problems = self.builder.verify(
            self.builder.read_lines(FIXTURE_SFS),
            self.builder.read_lines(BASE_SFS))
        self.assertEqual([], problems)

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_rebuild(self):
        # THE DRIFT GUARD. Re-runs the two-line splice over the CURRENT base and
        # compares against the committed bytes, so a change to `fresh-career`
        # reds here rather than in a live flight.
        rebuilt = self.builder.build(
            self.builder.read_lines(BASE_SFS),
            "%s (CAREER)" % self.builder.TARGET_NAME)
        self.assertEqual(self.builder.read_lines(FIXTURE_SFS), rebuilt,
                         "strategy-career has drifted from what "
                         "build_strategy_career.py produces from the current "
                         "fresh-career; re-run the builder and commit, or "
                         "explain the divergence")

    def test_the_splice_is_exactly_two_lines(self):
        # ANTI-SCOPE-CREEP. A sibling whose value is "the base plus a seed" has
        # to stay that. Anything else in the diff is a second, undocumented
        # decision riding along.
        base = self.builder.read_lines(BASE_SFS)
        fixture = self.builder.read_lines(FIXTURE_SFS)
        self.assertEqual(len(base), len(fixture))
        differing = [(i, b, f) for i, (b, f)
                     in enumerate(zip(base, fixture)) if b != f]
        self.assertEqual(2, len(differing),
                         "expected exactly the Title and rep lines to differ, "
                         "got %r" % (differing,))
        self.assertEqual(["\tTitle = strategy-career (CAREER)",
                          "\t\trep = %d" % self.builder.REPUTATION_SEED],
                         [f for _, _, f in differing])

    def test_the_on_disk_terminator_matches_the_base(self):
        # `harness/fixtures/**` is `-text` in .gitattributes: what is committed
        # lands verbatim on every platform, so the sibling must be stored the
        # way its base is rather than the way whoever ran the builder was set
        # up. `build_career_pad_craft.write_lines` hardcodes CRLF, which is why
        # this builder reads the terminator off the base instead of reusing it.
        self.assertEqual(self.builder.newline_of(BASE_SFS),
                         self.builder.newline_of(FIXTURE_SFS))

    def test_the_loadmeta_reputation_percent_is_the_ksp_expression(self):
        # `LoadGameDialog`'s save-info reader computes it as
        # `(int)(float.Parse(rep) / 10f)`. Load-menu preview only - nothing in
        # the harness reads it - but a fixture whose two halves disagree teaches
        # the next reader wrong.
        with open(FIXTURE_META, "r", encoding="utf-8") as fh:
            meta = fh.read()
        expected = int(float(self.builder.REPUTATION_SEED) / 10.0)
        self.assertIn("reputationPercent = %d" % expected, meta)
        # And every other field is still the base's.
        self.assertEqual(
            self.builder.build_loadmeta(self.builder.read_lines(BASE_META)),
            self.builder.read_lines(FIXTURE_META))


class StrategyCareerSeedBandTests(unittest.TestCase):
    """The seed is SOLVED FOR, not chosen. These cells re-derive both bounds."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_seed_clears_the_leadership_initiative_setup_cost(self):
        # Stock: initialCostReputationMin 10.0, initialCostReputationMax 100.0,
        # factorSliderDefault 0.05; Strategy.InitialCostReputation is
        # FactorLerp(min, max) and CanBeActivated compares the CURRENT pool
        # against it. 14.5.
        self.assertAlmostEqual(
            14.5, self.builder.LEADERSHIP_INITIATIVE_SETUP_REP_COST, places=6)
        self.assertGreater(self.builder.REPUTATION_SEED,
                           self.builder.LEADERSHIP_INITIATIVE_SETUP_REP_COST,
                           "the seed must clear the activation gate, or the "
                           "negative control still self-skips")

    def test_the_seed_stays_under_the_next_stock_threshold(self):
        # `UnpaidResearchProgramCfg` lerps its reputation setup cost 30..130 at
        # the same 0.05 -> 35.0, and `AgressiveNegotiations` gates on
        # requiredReputationMin 38.0 above that. Staying under the lower of the
        # two keeps the activatable set - and the subject
        # `ProbeActivatableStockStrategy` hands the two probe-driven cells in
        # this category - as close to the rep-0 set as the requirement allows.
        self.assertAlmostEqual(
            35.0, self.builder.NEXT_STOCK_REP_THRESHOLD, places=6)
        self.assertLess(self.builder.REPUTATION_SEED,
                        self.builder.NEXT_STOCK_REP_THRESHOLD)

    def test_the_seed_is_the_centre_of_the_admissible_band(self):
        # Not roundness: the whole number nearest the midpoint of
        # [14.5, 35.0). A future edit that moves it should have to say why.
        low = self.builder.LEADERSHIP_INITIATIVE_SETUP_REP_COST
        high = self.builder.NEXT_STOCK_REP_THRESHOLD
        self.assertEqual(round((low + high) / 2.0),
                         self.builder.REPUTATION_SEED)


class StrategyCareerCellExclusivityTests(unittest.TestCase):
    """THE FACT RUN `2026-08-20_1817` MEASURED, pinned so nobody re-derives it.

    Two cells in the `StrategyLifecycle` category have MUTUALLY EXCLUSIVE
    reputation preconditions, and no fixture seed can satisfy both:

      OperationStrategy_RewardMultiplier_IsNotCaptured needs rep >= 14.5.
        Both stock CurrencyOperation strategies (LeadershipInitiative,
        AgressiveNegotiations) lerp initialCostReputation 10..100 at
        factorSliderDefault 0.05, and Strategy.CanBeActivated compares the
        CURRENT pool against it.

      ExchangerStrategy_OneShot_CapturesBothLegs needs rep <= 0.
        Both stock CurrencyExchanger strategies (researchIPsellout,
        BailoutGrant) declare requiredReputationMin = -1000 /
        requiredReputationMax = 0 - they are EMERGENCY strategies and do not
        offer themselves at positive reputation. The cell's own source says so
        ("REPUTATION GATE, CHECKED AND LOGGED FIRST"), written as a guard
        against a residue from the reputation cell; the seed makes it fire on
        purpose.

    [14.5, +inf) and (-inf, 0] do not intersect. The seam's RunTests selects by
    CATEGORY only, so there is no per-cell split either: one batch, one
    reputation value, one of the two skips. The chosen trade is the negative
    control, because it is the only declaration whose failure detects deletion
    of the scoping rule."""

    OPERATION_CELL_FLOOR = 14.5
    EXCHANGER_CELL_CEILING = 0.0

    def test_the_two_preconditions_do_not_intersect(self):
        self.assertGreater(self.OPERATION_CELL_FLOOR, self.EXCHANGER_CELL_CEILING,
                           "if these ever overlap, one seed could host both cells "
                           "and this whole trade-off is obsolete - re-read the "
                           "stock Strategies.cfg before believing it")

    def test_the_seed_sits_on_the_operation_cells_side(self):
        builder = _load_builder()
        self.assertGreater(builder.REPUTATION_SEED, self.OPERATION_CELL_FLOOR)
        self.assertGreater(builder.REPUTATION_SEED, self.EXCHANGER_CELL_CEILING,
                           "a seed above the exchanger ceiling is exactly what "
                           "makes that cell skip; this cell exists so the cost is "
                           "asserted rather than discovered again in a flight")


class FreshCareerStaysUnseededTests(unittest.TestCase):
    """THE NEGATIVE HALF OF THE DECISION, and the reason this fixture exists.

    Seeding `fresh-career` itself would have propagated through
    `build_career_pad_craft.py` into `career-pad-craft` and on into
    `career-science-pad`, re-opening thirteen committed specs. CL-2 pins KSP's
    own post-curve digits as an EXACT-DIGIT logContract regex, and KSP's granular
    reputation curve is STATE-DEPENDENT: `oracle.apply_rep_curve` - post PR
    #1508's residual-step port, which reproduces CL-2's `+1` Progression pin to
    all seven printed digits at rep 0, making it a calibrated instrument rather
    than an analogy - puts a -10 nominal at -9.9996061 from rep 0 against
    -10.0001546 from rep 25, a 5.5e-4 shift and ~500x the last printed digit.
    WHICH SURFACE BINDS MATTERS: the oracle's own reputation FACET would NOT red
    at that size (0.1 tolerance), so the cost is a re-flown logContract pin
    rather than a manifest amount. This cell is what makes a future "just seed
    the base" reopen the argument instead of the pins."""

    def test_fresh_career_still_seeds_reputation_zero(self):
        builder = _load_builder()
        lines = builder.read_lines(BASE_SFS)
        node = builder._scenario_node(lines, "Reputation")
        self.assertIsNotNone(node)
        self.assertEqual("0", builder.get_value(lines, node, "rep"),
                         "fresh-career must keep seeding rep 0: career-pad-craft "
                         "is derived from it and CL-2 pins post-curve reputation "
                         "amounts measured from that pool")

    def test_career_pad_craft_still_seeds_reputation_zero(self):
        builder = _load_builder()
        lines = builder.read_lines(
            os.path.join(SAVES, "career-pad-craft", "persistent.sfs"))
        node = builder._scenario_node(lines, "Reputation")
        self.assertIsNotNone(node)
        self.assertEqual("0", builder.get_value(lines, node, "rep"))


class L3SpecStagesTheSeededFixtureTests(unittest.TestCase):
    """The spec and the fixture must not drift apart."""

    @classmethod
    def setUpClass(cls):
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)
        with open(SPEC_PATH, "r", encoding="utf-8") as fh:
            cls.text = fh.read()

    def test_the_spec_stages_strategy_career(self):
        self.assertEqual("fixtures/saves/strategy-career",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_spec_pins_the_measured_tally(self):
        # MEASURED on run `2026-08-20_1817`, not predicted. Still passed=6
        # skipped=1, because the seed TRADED which cell skips rather than
        # eliminating the skip - see StrategyCareerCellExclusivityTests.
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertIn(
            "BATCH_COMPLETE v1 total=7 passed=6 failed=0 skipped=1 "
            "category=StrategyLifecycle scene=SPACECENTER",
            required)

    def test_the_spec_names_the_cell_the_seed_costs(self):
        # The old skip was NAMED in the prose, not merely counted; the new one
        # must be too, or the tally silently stops meaning anything.
        self.assertIn("ExchangerStrategy_OneShot_CapturesBothLegs", self.text)

    def test_the_spec_still_names_the_cell_the_fixture_exists_for(self):
        # The prose that explains WHY this spec does not stage `fresh-career`
        # like its six siblings has to keep naming its subject.
        self.assertIn(NEGATIVE_CONTROL, self.text)


if __name__ == "__main__":
    unittest.main()
