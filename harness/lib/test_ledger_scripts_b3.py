"""Pure tests for the M-B3 L1 ledger-action scripts (design
docs/dev/design-autotest-ledger-scripts-b3.md, Test Plan ~994).

M-B3 authors the FIRST-EVER nonzero seam-declared manifests. The pure logic those
scripts exercise (compute_expected / parse_manifest_entries / diff / the capture
cross-check) already has M-B2 coverage; this file adds the NONZERO-manifest coverage
M-B2 never exercised, and -- the load-bearing M-B3 test -- drives EACH committed L1
spec's ACTUAL declared manifest through the oracle and proves it computes the intended
expected totals over a synthetic seed. A regression that let a real economy drift read
green (BUG-A) or let a declared nonzero effect silently drop is the dangerous silent
pass this guards.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib
"""

import os
import tomllib
import unittest

import hlib
import oracle


HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")


def _career(funds=None, science=None, reputation=None, subject=None, contracts=None):
    """A parsed careerSave block dict (leg B) with the hasX flags derived from presence,
    mirroring the test_oracle helper so the diff can be driven directly."""
    return {
        "parsed": True,
        "hasFunds": funds is not None, "funds": funds if funds is not None else 0.0,
        "hasScience": science is not None, "sciencePool": science if science is not None else 0.0,
        "hasRep": reputation is not None, "reputation": reputation if reputation is not None else 0.0,
        "subjectScience": subject or {},
        "activeContractGuids": contracts or [],
    }


def _load_spec(name):
    with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
        return tomllib.load(fh)


def _spec_manifest(spec):
    return (spec.get("expectations", {}).get("ledger", {}) or {}).get("manifest", []) or []


# A synthetic Career seed well above every scripted spend (mirrors the fixture-spec
# funds/science seeds so the affordability arithmetic in the spec comments holds).
CAREER_SEED = oracle.SeedBaseline(funds=500000.0, science=100.0, reputation=0.0)
SCIENCE_SEED = oracle.SeedBaseline(funds=None, science=100.0, reputation=None)
SANDBOX_SEED = oracle.SeedBaseline(funds=None, science=None, reputation=None)


class SpecManifestComputesIntendedTotalsTests(unittest.TestCase):
    """The M-B3-specific gate (design Test Plan item 3): parse EACH committed L1 spec's
    declared manifest through oracle.parse_manifest_entries with its REAL declared values
    and prove compute_expected yields the intended per-facet totals over a synthetic seed.
    A stale/malformed author constant, a dropped facet, or a rejected entry reds here
    BEFORE any KSP boot. Also proves every declared entry PARSES clean (e.g. the tech-unlock
    science author constant is admissible, not a forbidden null fill)."""

    def _parse_ok(self, spec):
        parse = oracle.parse_manifest_entries(_spec_manifest(spec))
        self.assertEqual((), parse.errors,
                         "declared manifest must parse clean: %s" % (parse.errors,))
        return parse.entries

    def test_research_node_career_science_drops_by_node_cost(self):
        spec = _load_spec("L1-research-node-career.toml")
        entries = self._parse_ok(spec)
        exp = oracle.compute_expected(CAREER_SEED, entries)
        self.assertAlmostEqual(exp.science, 95.0)   # 100 + (-5) node cost
        self.assertAlmostEqual(exp.funds, 500000.0)  # untouched
        self.assertAlmostEqual(exp.reputation, 0.0)  # untouched

    def test_research_node_science_mode_asserts_science_only(self):
        spec = _load_spec("L1-research-node-science.toml")
        entries = self._parse_ok(spec)
        exp = oracle.compute_expected(SCIENCE_SEED, entries)
        self.assertAlmostEqual(exp.science, 95.0)
        self.assertIsNone(exp.funds)       # absent pool -> None -> diff-skipped
        self.assertIsNone(exp.reputation)

    def test_upgrade_facility_career_funds_drops_by_upgrade_cost(self):
        spec = _load_spec("L1-upgrade-facility-career.toml")
        entries = self._parse_ok(spec)
        exp = oracle.compute_expected(CAREER_SEED, entries)
        self.assertAlmostEqual(exp.funds, 350000.0)  # 500000 + (-150000)
        self.assertAlmostEqual(exp.science, 100.0)   # untouched
        self.assertAlmostEqual(exp.reputation, 0.0)

    def test_hire_kerbal_career_funds_drops_by_pinned_hire_cost(self):
        spec = _load_spec("L1-hire-kerbal-career.toml")
        entries = self._parse_ok(spec)
        exp = oracle.compute_expected(CAREER_SEED, entries)
        self.assertAlmostEqual(exp.funds, 437887.0)  # 500000 + (-62113) fixture-pinned
        self.assertAlmostEqual(exp.science, 100.0)
        self.assertAlmostEqual(exp.reputation, 0.0)

    def test_dismiss_kerbal_career_is_pool_neutral(self):
        spec = _load_spec("L1-dismiss-kerbal-career.toml")
        entries = self._parse_ok(spec)
        self.assertEqual(1, len(entries))
        self.assertEqual("kerbal-dismiss", entries[0].kind)
        exp = oracle.compute_expected(CAREER_SEED, entries)
        self.assertAlmostEqual(exp.funds, 500000.0)  # expected == seed on every pool
        self.assertAlmostEqual(exp.science, 100.0)
        self.assertAlmostEqual(exp.reputation, 0.0)

    def test_passive_sandbox_empty_manifest_expected_equals_absent_seed(self):
        spec = _load_spec("L1-passive-sandbox.toml")
        entries = self._parse_ok(spec)
        self.assertEqual([], list(entries), "sandbox passive spec declares no manifest entry")
        exp = oracle.compute_expected(SANDBOX_SEED, entries)
        self.assertIsNone(exp.funds)
        self.assertIsNone(exp.science)
        self.assertIsNone(exp.reputation)

    def test_every_committed_L1_spec_manifest_parses_clean(self):
        """Sweep: any scenarios/L1-*.toml must carry a manifest oracle accepts, so a new
        L1 script with a bad entry cannot slip in without this test reding."""
        names = sorted(n for n in os.listdir(SCENARIOS_DIR)
                       if n.startswith("L1-") and n.endswith(".toml"))
        self.assertTrue(names, "expected committed L1 specs under %s" % SCENARIOS_DIR)
        for name in names:
            with self.subTest(spec=name):
                parse = oracle.parse_manifest_entries(_spec_manifest(_load_spec(name)))
                self.assertEqual((), parse.errors, "%s manifest: %s" % (name, parse.errors))


class NonzeroManifestOracleTests(unittest.TestCase):
    """The nonzero-manifest oracle coverage M-B2 never exercised (design Test Plan
    "Pure Python" ~996). The seam-declared-vs-save diff is the SOLE trusted leg, so most
    tests assert the RED path fires on a drifted save."""

    def test_nonzero_science_spend_expected_and_diff(self):
        entry = oracle.parse_manifest_entries(
            [{"ut": 0.0, "kind": "tech-unlock", "science": -5.0}]).entries
        exp = oracle.compute_expected(CAREER_SEED, entry)
        self.assertAlmostEqual(exp.science, 95.0)
        # Correct save (science spent) PASSES; an unspent/refunded node (science == seed)
        # reds HARD on sciencePool.
        clean = oracle.diff_expected_vs_parsed(exp, _career(funds=500000.0, science=95.0, reputation=0.0))
        self.assertFalse(oracle.has_hard_drift(clean))
        drift = oracle.diff_expected_vs_parsed(exp, _career(funds=500000.0, science=100.0, reputation=0.0))
        self.assertTrue(oracle.has_hard_drift(drift))
        self.assertTrue(any(d.facet == "sciencePool" and d.hard for d in drift))

    def test_nonzero_funds_spend_phantom_refund_reds(self):
        entry = oracle.parse_manifest_entries(
            [{"ut": 0.0, "kind": "facility-upgrade", "funds": -150000.0}]).entries
        exp = oracle.compute_expected(CAREER_SEED, entry)
        self.assertAlmostEqual(exp.funds, 350000.0)
        # A BUG-G phantom refund leaves the produced funds HIGHER than expected -> hard red.
        refunded = oracle.diff_expected_vs_parsed(exp, _career(funds=500000.0, science=100.0, reputation=0.0))
        self.assertTrue(oracle.has_hard_drift(refunded))
        self.assertTrue(any(d.facet == "funds" and d.hard for d in refunded))

    def test_tech_unlock_null_science_rejected(self):
        """A tech-unlock entry with a NULL science amount (intending fill-from-capture) is
        REJECTED: science is state-dependent, fill forbidden (design edge 18). A dropped
        expected effect could false-PASS, so this must never parse."""
        parse = oracle.parse_manifest_entries([{"ut": 0.0, "kind": "tech-unlock", "science": None}])
        self.assertEqual((), parse.entries)
        self.assertTrue(parse.errors)
        self.assertIn("science", parse.errors[0])

    def test_hire_fixture_pinned_constant_drift_reds(self):
        entry = oracle.parse_manifest_entries(
            [{"ut": 0.0, "kind": "kerbal-hire", "funds": -62113.0}]).entries
        exp = oracle.compute_expected(CAREER_SEED, entry)
        self.assertAlmostEqual(exp.funds, 437887.0)
        # A save at a DIFFERENT hire cost (roster drift changed the recruit-cost curve
        # input, staling the pinned constant) reds hard.
        drift = oracle.diff_expected_vs_parsed(exp, _career(funds=470000.0, science=100.0, reputation=0.0))
        self.assertTrue(oracle.has_hard_drift(drift))

    def test_sandbox_and_science_pool_absence_never_reds(self):
        # Sandbox: all pools absent, empty manifest -> expected == seed, nothing to diff.
        exp_sb = oracle.compute_expected(SANDBOX_SEED, [])
        self.assertFalse(oracle.has_hard_drift(
            oracle.diff_expected_vs_parsed(exp_sb, _career())))  # career block with no pools
        # Science: asserts science only; a save carrying no funds/rep facet never reds on
        # the absent pools.
        entry = oracle.parse_manifest_entries(
            [{"ut": 0.0, "kind": "tech-unlock", "science": -5.0}]).entries
        exp_sci = oracle.compute_expected(SCIENCE_SEED, entry)
        clean = oracle.diff_expected_vs_parsed(exp_sci, _career(science=95.0))
        self.assertFalse(oracle.has_hard_drift(clean))


class CaptureLegDeadTests(unittest.TestCase):
    """The nonzero capture leg is UNTRUSTED (design Behavior "Stock-award capture
    sequencing" ~700). These tests characterize WHY M-B3 adds no STOCK_AWARD_PATTERNS
    entry: the shipped patterns are dead against real stock text (a no-op today), and a
    naive pattern rewrite WITHOUT the UT-agnostic match would false-red every nonzero run."""

    def test_shipped_patterns_dead_against_real_stock_text(self):
        """A KSP.log body with REAL stock-style lines yields ZERO captures: the stock R&D
        line matches no shipped pattern, and the Parsek delta= line is [Parsek]-skipped."""
        log = "\n".join([
            "[LOG 00:00:01.000] [Research & Development]: +5 data on Basic Rocketry.",
            "[Parsek][INFO][GameStateRecorder] Game state: TechResearched 'basicRocketry' (cost=5)",
            "[Parsek][VERBOSE][LedgerTrace] recalc ResearchAndDevelopment science delta=-5.0 subject=basicRocketry",
            "[Parsek][INFO][TestCommands] kscaction action=research-node target=basicRocketry applied=true manifestKind=tech-unlock observedAfter=science=95",
        ])
        result = hlib.parse_stock_award_lines(log)
        self.assertEqual((), result.captured, "shipped patterns are DEAD; expected zero captures")
        self.assertEqual(0, result.stock_lines)

    def test_latent_false_red_characterized_ut0_seam_never_matches_runtime_capture(self):
        """GIVEN a hypothetical captured award at a runtime seq_key and a seam entry at the
        author ut=0.0, unmatched_captured_awards returns it (non-empty) -> the verifier
        would hard-red it. This documents why a pattern that captured a nonzero action's
        own award WOULD false-red: the award's runtime seq_key never matches ut=0.0."""
        seam = oracle.ManifestEntry(
            ut=0.0, seq=0, kind="tech-unlock", funds=0.0, science=-5.0, reputation=0.0,
            rep_mode="nominal", subject_ids=(), contract_guid="", provenance="seam-declared")
        cap = hlib.CapturedAward(
            kind="tech-unlock", facet="science", amount=-5.0, contract_guid="", subject_id="",
            ut=42.0, seq=100, raw_line="(hypothetical rewritten-pattern capture)")
        self.assertEqual(1, len(hlib.unmatched_captured_awards([seam], [cap])))

    def test_b10_empty_manifest_any_capture_reds_stays_trusted(self):
        """The distinct TRUSTED case: with NO seam entries every captured award is unmatched
        for any seq_key, so the empty-manifest any-capture-reds signal (B10 / the dismiss
        pool-neutrality script) is sound. A regression that lost it would let an economy
        drift on a passive run read green."""
        cap = hlib.CapturedAward(
            kind="contract-complete", facet="funds", amount=1000.0, contract_guid="g",
            subject_id="", ut=3.0, seq=7, raw_line="(unexpected award on a passive run)")
        self.assertEqual([cap], hlib.unmatched_captured_awards([], [cap]))


if __name__ == "__main__":
    unittest.main()
