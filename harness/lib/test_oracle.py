"""Unit tests for oracle.py, the pure M-B2 ledger oracle.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Each test names the regression it guards (design docs/dev/design-autotest-ledger-oracle.md
Test Plan: "Oracle math" ~761, "Manifest parse + capture" ~781 PARSE side, "Diff
facet policy" ~801). The oracle is the L-track's INDEPENDENT leg: a bug that lets a
real economy drift (BUG-A) or a cold-load wipe (BUG-F) read as PASS is the most
dangerous silent pass this module exists to prevent, so most tests assert the
red path fires.
"""

import copy
import math
import unittest

import oracle


def _entry(kind="contract-complete", ut=0.0, seq=0, funds=0.0, science=0.0,
           reputation=0.0, rep_mode="nominal", subject_ids=(), contract_guid="",
           provenance="seam-declared", rec3_row=""):
    """A fully-formed ManifestEntry for compute/diff tests (bypasses the raw parse)."""
    return oracle.ManifestEntry(
        ut=ut, seq=seq, kind=kind, funds=funds, science=science, reputation=reputation,
        rep_mode=rep_mode, subject_ids=tuple(subject_ids), contract_guid=contract_guid,
        provenance=provenance, rec3_row=rec3_row)


def _career(funds=None, science=None, reputation=None, subject=None, contracts=None):
    """A parsed careerSave block dict with the hasX flags derived from presence."""
    block = {
        "parsed": True,
        "hasFunds": funds is not None, "funds": funds if funds is not None else 0.0,
        "hasScience": science is not None, "sciencePool": science if science is not None else 0.0,
        "hasRep": reputation is not None, "reputation": reputation if reputation is not None else 0.0,
        "subjectScience": subject or {},
        "activeContractGuids": contracts or [],
    }
    return block


# ---------------------------------------------------------------------------
# Seed baseline parse.
# ---------------------------------------------------------------------------


class SeedBaselineParseTests(unittest.TestCase):
    """Guards: the seed must carry facet PRESENCE, not just a value; a Sandbox
    save (hasFunds=false) must present an ABSENT funds facet, never a spurious 0.0
    the oracle would then assert against the save (edge 5)."""

    def test_full_career_seed(self):
        seed = oracle.parse_seed_baseline(
            {"funds": 25000.0, "science": 0.0, "reputation": 0.0,
             "hasFunds": True, "hasScience": True, "hasRep": True})
        self.assertEqual((seed.funds, seed.science, seed.reputation), (25000.0, 0.0, 0.0))
        self.assertTrue(seed.has_funds and seed.has_science and seed.has_rep)

    def test_sandbox_seed_absent_pools(self):
        # hasFunds/hasScience/hasRep all false -> every pool absent (None), not 0.0.
        seed = oracle.parse_seed_baseline(
            {"funds": 0.0, "hasFunds": False, "hasScience": False, "hasRep": False})
        self.assertIsNone(seed.funds)
        self.assertIsNone(seed.science)
        self.assertIsNone(seed.reputation)
        self.assertFalse(seed.has_funds)

    def test_science_mode_seed(self):
        # Science mode: science present, funds/rep absent (edge 5).
        seed = oracle.parse_seed_baseline(
            {"science": 12.0, "hasFunds": False, "hasScience": True, "hasRep": False})
        self.assertIsNone(seed.funds)
        self.assertEqual(seed.science, 12.0)
        self.assertIsNone(seed.reputation)

    def test_missing_block_all_absent(self):
        seed = oracle.parse_seed_baseline(None)
        self.assertIsNone(seed.funds)
        self.assertIsNone(seed.science)
        self.assertIsNone(seed.reputation)


# ---------------------------------------------------------------------------
# Manifest entry parse + validation (Test Plan ~781, PARSE side).
# ---------------------------------------------------------------------------


class ManifestParseTests(unittest.TestCase):
    """Guards the three load-bearing validation rules: a malformed declaration must
    never silently drop an expected effect, a balance line must never be admitted as
    an amount, and a state-dependent facet must never fill from the (possibly
    corrupted) leg-B capture."""

    def test_spec_declared_parse_kinds_and_amounts(self):
        # A well-formed array parses into ManifestEntry list with the right amounts.
        parse = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "contract-complete", "funds": 50000.0, "contractGuid": "g1"},
            {"ut": 10.0, "kind": "science-transmit", "science": 5.0, "subjectIds": ["s1"]},
        ])
        self.assertTrue(parse.ok, parse.errors)
        self.assertEqual(len(parse.entries), 2)
        self.assertEqual(parse.entries[0].funds, 50000.0)
        self.assertEqual(parse.entries[0].contract_guid, "g1")
        self.assertEqual(parse.entries[1].subject_ids, ("s1",))

    def test_unknown_kind_rejects(self):
        # A kind outside the enum is a scenario-authoring defect, not a dropped effect.
        parse = oracle.parse_manifest_entries([{"kind": "teleport", "funds": 1.0}])
        self.assertFalse(parse.ok)
        self.assertEqual(len(parse.entries), 0)
        self.assertTrue(any("kind" in e and "teleport" in e for e in parse.errors))

    def test_balance_amount_rejected(self):
        # A post-grant running BALANCE is inadmissible (would double-count vs seed).
        parse = oracle.parse_manifest_entries(
            [{"kind": "science-recover", "science": 500.0, "amountKind": "balance",
              "subjectIds": ["s1"]}])
        self.assertFalse(parse.ok)
        self.assertEqual(len(parse.entries), 0)
        self.assertTrue(any("amountKind" in e and "DELTA" in e for e in parse.errors))

    def test_delta_amount_admitted(self):
        # The explicit delta amountKind is the admissible path.
        parse = oracle.parse_manifest_entries(
            [{"kind": "science-recover", "science": 5.0, "amountKind": "delta",
              "subjectIds": ["s1"]}])
        self.assertTrue(parse.ok, parse.errors)
        self.assertEqual(parse.entries[0].science, 5.0)

    def test_author_constant_required_for_reputation(self):
        # A null rep amount (fill-from-capture) on the state-dependent rep facet reds.
        parse = oracle.parse_manifest_entries(
            [{"kind": "contract-complete", "reputation": None, "contractGuid": "g"}])
        self.assertFalse(parse.ok)
        self.assertTrue(any("reputation" in e and "author constant" in e for e in parse.errors))

    def test_author_constant_required_for_subject_science(self):
        # A null science amount (fill-from-capture) on per-subject science reds.
        parse = oracle.parse_manifest_entries(
            [{"kind": "science-transmit", "science": None, "subjectIds": ["s1"]}])
        self.assertFalse(parse.ok)
        self.assertTrue(any("science" in e and "author constant" in e for e in parse.errors))

    def test_fill_from_capture_state_independent_single_match(self):
        # funds is state-independent: a null funds amount fills from EXACTLY ONE match.
        captured = oracle.parse_manifest_entries(
            [{"ut": 5.0, "kind": "contract-complete", "funds": 12345.0, "contractGuid": "g",
              "provenance": "stock-log-captured"}]).entries
        parse = oracle.parse_manifest_entries(
            [{"ut": 5.0, "kind": "contract-complete", "funds": None, "contractGuid": "g"}],
            captured=captured)
        self.assertTrue(parse.ok, parse.errors)
        self.assertEqual(parse.entries[0].funds, 12345.0)

    def test_fill_from_capture_ambiguous_zero_or_multiple(self):
        # Zero or multiple matches -> flagged ambiguous, not silently filled.
        parse = oracle.parse_manifest_entries(
            [{"ut": 5.0, "kind": "contract-complete", "funds": None, "contractGuid": "g"}],
            captured=[])
        self.assertFalse(parse.ok)
        self.assertTrue(any("ambiguous" in e for e in parse.errors))

    def test_rep_mode_defaults_by_provenance(self):
        # seam-declared -> nominal (pre-curve); gameevents-captured -> applied.
        parse = oracle.parse_manifest_entries([
            {"kind": "contract-complete", "reputation": 10.0, "provenance": "seam-declared"},
            {"kind": "contract-complete", "reputation": 10.0, "provenance": "gameevents-captured"},
        ])
        self.assertTrue(parse.ok, parse.errors)
        self.assertEqual(parse.entries[0].rep_mode, oracle.REP_MODE_NOMINAL)
        self.assertEqual(parse.entries[1].rep_mode, oracle.REP_MODE_APPLIED)

    def test_non_finite_amount_rejected(self):
        # A NaN/Inf author amount is rejected at parse (never silently summed).
        parse = oracle.parse_manifest_entries(
            [{"kind": "contract-complete", "funds": float("inf")}])
        self.assertFalse(parse.ok)


# ---------------------------------------------------------------------------
# Oracle math: accumulation, tolerance, absence, determinism (Test Plan ~761).
# ---------------------------------------------------------------------------


class ComputeExpectedTests(unittest.TestCase):
    """Guards the accumulation math: an empty passive-safety run must compute
    expected == seed (else B10 could never PASS), and a real accumulation must be
    deterministic and order-stable."""

    def test_empty_manifest_expected_equals_seed(self):
        # The B10 cross-check: expected == seed on all three pools (design ~763).
        seed = oracle.SeedBaseline(25000.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [])
        self.assertEqual(exp.funds, 25000.0)
        self.assertEqual(exp.science, 0.0)
        self.assertEqual(exp.reputation, 0.0)
        self.assertEqual(exp.subject_science, {})
        self.assertEqual(exp.active_contract_guids, ())

    def test_single_entry_funds_accumulation(self):
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [_entry(funds=500.0)])
        self.assertEqual(exp.funds, 1500.0)

    def test_multi_entry_pool_accumulation(self):
        # N funds + science entries summing within the pools (design ~767).
        seed = oracle.SeedBaseline(1000.0, 10.0, 0.0)
        entries = [
            _entry(kind="contract-complete", ut=1.0, seq=0, funds=500.0),
            _entry(kind="contract-fail", ut=2.0, seq=1, funds=-200.0),
            _entry(kind="science-transmit", ut=3.0, seq=2, science=5.0, subject_ids=["s1"]),
            _entry(kind="science-recover", ut=4.0, seq=3, science=7.5, subject_ids=["s2"]),
        ]
        exp = oracle.compute_expected(seed, entries)
        self.assertEqual(exp.funds, 1300.0)          # 1000 + 500 - 200
        self.assertEqual(exp.science, 22.5)          # 10 + 5 + 7.5
        self.assertEqual(exp.subject_science, {"s1": 5.0, "s2": 7.5})

    def test_facet_absence_sandbox(self):
        # seed.hasFunds false -> funds absent from ExpectedCareer (edge 5 / ~779).
        seed = oracle.SeedBaseline(None, None, None)
        exp = oracle.compute_expected(seed, [_entry(funds=500.0)])
        self.assertIsNone(exp.funds)
        self.assertIsNone(exp.science)
        self.assertIsNone(exp.reputation)

    def test_active_contract_transitions(self):
        # accept adds, complete/fail remove (report-only guid set, edge 14).
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        entries = [
            _entry(kind="contract-accept", ut=1.0, seq=0, contract_guid="a"),
            _entry(kind="contract-accept", ut=2.0, seq=1, contract_guid="b"),
            _entry(kind="contract-complete", ut=3.0, seq=2, contract_guid="a"),
        ]
        exp = oracle.compute_expected(seed, entries)
        self.assertEqual(exp.active_contract_guids, ("b",))

    def test_accumulation_order_deterministic(self):
        # Sorting by (ut, seq) makes the sum reproducible regardless of input order
        # (edge 7 / ~769: float accumulation order must not be nondeterministic).
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        entries = [
            _entry(ut=3.0, seq=2, funds=0.1),
            _entry(ut=1.0, seq=0, funds=0.2),
            _entry(ut=2.0, seq=1, funds=0.3),
        ]
        exp_a = oracle.compute_expected(seed, entries)
        exp_b = oracle.compute_expected(seed, list(reversed(entries)))
        self.assertEqual(exp_a.funds, exp_b.funds)

    def test_ut_window_bounds_contributing_entries(self):
        # The funds window brackets the contributing entries (used for drift naming).
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        entries = [_entry(ut=10.0, seq=0, funds=1.0), _entry(ut=50.0, seq=1, funds=1.0)]
        exp = oracle.compute_expected(seed, entries)
        self.assertEqual(exp.ut_windows["funds"], (10.0, 50.0))


class ReputationCurveTests(unittest.TestCase):
    """Guards the SetReputation-semantics exception: rep must be curve-composed, NOT
    linearly summed (summing nominal deltas linearly is the double-curve distortion
    15.1 warns of, read backward)."""

    def test_reputation_is_not_linear_sum(self):
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        entries = [_entry(ut=10.0, seq=0, reputation=100.0),
                   _entry(ut=50.0, seq=1, reputation=100.0)]
        exp = oracle.compute_expected(seed, entries)
        # Linear sum would be 200; the curve composes to strictly less (gain
        # diminishes as rep climbs). Must differ well beyond the rep tolerance.
        self.assertNotAlmostEqual(exp.reputation, 200.0, places=1)
        self.assertLess(exp.reputation, 200.0)

    def test_reputation_matches_sequential_curve(self):
        # compute_expected must apply the curve sequentially at the running rep, i.e.
        # equal the hand-composed apply_rep_curve chain (not two independent applies).
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        entries = [_entry(ut=10.0, seq=0, reputation=100.0),
                   _entry(ut=50.0, seq=1, reputation=100.0)]
        exp = oracle.compute_expected(seed, entries)
        _d1, r1 = oracle.apply_rep_curve(100.0, 0.0)
        _d2, r2 = oracle.apply_rep_curve(100.0, r1)
        self.assertAlmostEqual(exp.reputation, r2, places=6)

    def test_applied_rep_mode_not_re_curved(self):
        # An `applied` (post-curve) delta is added directly with no second curve pass
        # (re-curving an applied delta is the distortion 15.1 warns of).
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [_entry(reputation=25.0, rep_mode="applied",
                                                    provenance="gameevents-captured")])
        self.assertEqual(exp.reputation, 25.0)

    def test_rep_curve_deterministic(self):
        a = oracle.apply_rep_curve(37.5, 120.0)
        b = oracle.apply_rep_curve(37.5, 120.0)
        self.assertEqual(a, b)


class Rec3CarveOutTests(unittest.TestCase):
    """Guards the ratified-residual carve-out: a whitelisted non-rewind-discard route
    row must NOT read as drift, and a non-whitelisted one must still roll back."""

    def test_whitelisted_row_not_rolled_back(self):
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        # A discard that would roll a route row's funds back by 300.
        entry = _entry(kind="route-delivery", ut=5.0, funds=-300.0, rec3_row="route7")
        exp = oracle.compute_expected(seed, [entry], rec3_whitelist=["route7"])
        self.assertEqual(exp.funds, 1000.0)          # residual persists, no rollback
        self.assertEqual(exp.rec3_residual_rows, ("route7",))

    def test_non_whitelisted_row_rolls_back(self):
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        entry = _entry(kind="route-delivery", ut=5.0, funds=-300.0, rec3_row="route7")
        exp = oracle.compute_expected(seed, [entry], rec3_whitelist=[])
        self.assertEqual(exp.funds, 700.0)           # rolls back normally
        self.assertEqual(exp.rec3_residual_rows, ())

    def test_residual_surfaces_report_only_divergence(self):
        # The carve-out surfaces a report-only residual-expected divergence (a
        # [Rec-3 residual] diagnostic is expected, never a rollback-to-zero red).
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        entry = _entry(kind="route-delivery", ut=5.0, funds=-300.0, rec3_row="route7")
        exp = oracle.compute_expected(seed, [entry], rec3_whitelist=["route7"])
        diffs = oracle.diff_expected_vs_parsed(exp, _career(funds=1000.0, science=0.0, reputation=0.0))
        residual = [d for d in diffs if d.kind == "residual-expected"]
        self.assertEqual(len(residual), 1)
        self.assertFalse(residual[0].hard)


# ---------------------------------------------------------------------------
# Diff facet policy (Test Plan ~801).
# ---------------------------------------------------------------------------


class DiffFacetPolicyTests(unittest.TestCase):
    """Guards the hard-vs-report-only split: a pool drift must red, a per-identity
    mixed-history difference must NOT red (the exact false-positive
    LedgerGroundTruthDiff avoids), and a NaN/Inf must never be absorbed by tolerance."""

    def test_hard_funds_drift_reds(self):
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [])
        # Save shows 500 funds; expected 1000, well beyond funds tol 1.0.
        diffs = oracle.diff_expected_vs_parsed(exp, _career(funds=500.0, science=0.0, reputation=0.0))
        hard = [d for d in diffs if d.hard]
        self.assertTrue(oracle.has_hard_drift(diffs))
        self.assertEqual(hard[0].facet, "funds")
        self.assertEqual(hard[0].kind, "value-mismatch")

    def test_report_only_subject_science_not_red(self):
        # A subject-science-only drift is report-only: logged, never red.
        seed = oracle.SeedBaseline(0.0, 100.0, 0.0)
        exp = oracle.compute_expected(seed, [
            _entry(kind="science-transmit", science=0.0, subject_ids=["s1"])])
        # expected subject s1 = 0.0; save shows 9.0 (a mixed-history per-subject diff).
        # The hard pools all match so ONLY the report-only subject facet can diverge.
        block = _career(funds=0.0, science=100.0, reputation=0.0, subject={"s1": 9.0})
        diffs = oracle.diff_expected_vs_parsed(exp, block)
        self.assertFalse(oracle.has_hard_drift(diffs))
        subj = [d for d in diffs if d.facet == "subjectScience"]
        self.assertTrue(subj and not subj[0].hard)

    def test_tolerance_boundary_inclusive(self):
        # Exactly-at-tolerance passes (inclusive <=); one epsilon beyond reds.
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [])
        at_edge = oracle.diff_expected_vs_parsed(exp, _career(funds=1001.0, science=0.0, reputation=0.0))
        self.assertFalse(oracle.has_hard_drift(at_edge))     # |1001-1000| == 1.0 == tol
        beyond = oracle.diff_expected_vs_parsed(exp, _career(funds=1001.001, science=0.0, reputation=0.0))
        self.assertTrue(oracle.has_hard_drift(beyond))

    def test_nan_never_passes(self):
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [])
        diffs = oracle.diff_expected_vs_parsed(exp, _career(funds=float("nan"), science=0.0, reputation=0.0))
        self.assertTrue(oracle.has_hard_drift(diffs))
        self.assertFalse(oracle.within_tolerance(1000.0, float("nan"), 1.0))

    def test_inf_never_passes(self):
        self.assertFalse(oracle.within_tolerance(1000.0, float("inf"), 1.0))
        self.assertFalse(oracle.within_tolerance(float("inf"), 1000.0, 1.0))

    def test_missing_hard_pool_reds(self):
        # expected funds present but the save block carries no funds facet -> hard missing.
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [])
        block = _career(funds=None, science=0.0, reputation=0.0)  # hasFunds False
        diffs = oracle.diff_expected_vs_parsed(exp, block)
        hard = [d for d in diffs if d.hard and d.facet == "funds"]
        self.assertEqual(hard[0].kind, "missing")

    def test_facet_absent_both_sides_skipped(self):
        # Sandbox: expected funds absent + save funds absent -> no funds divergence.
        seed = oracle.SeedBaseline(None, None, None)
        exp = oracle.compute_expected(seed, [])
        block = _career(funds=None, science=None, reputation=None)
        diffs = oracle.diff_expected_vs_parsed(exp, block)
        self.assertEqual([d for d in diffs if d.facet == "funds"], [])

    def test_ut_window_naming_on_drift(self):
        # A drift bracketed by entries at ut=10 and ut=50 names utWindow=[10,50].
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        entries = [_entry(ut=10.0, seq=0, funds=100.0), _entry(ut=50.0, seq=1, funds=100.0)]
        exp = oracle.compute_expected(seed, entries)  # expected funds 200
        diffs = oracle.diff_expected_vs_parsed(exp, _career(funds=999.0, science=0.0, reputation=0.0))
        hard = [d for d in diffs if d.hard][0]
        self.assertEqual(hard.ut_window, (10.0, 50.0))

    def test_ut_window_none_on_pool_residual(self):
        # A pool residual with no bracketing entry names [None, None] (design ~810).
        seed = oracle.SeedBaseline(1000.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [])  # empty manifest, no bracketing entry
        diffs = oracle.diff_expected_vs_parsed(exp, _career(funds=500.0, science=0.0, reputation=0.0))
        hard = [d for d in diffs if d.hard][0]
        self.assertEqual(hard.ut_window, (None, None))

    def test_contract_transition_report_only(self):
        # A wrong contract STATE is report-only (edge 14); only a wrong payout reds.
        seed = oracle.SeedBaseline(0.0, 0.0, 0.0)
        exp = oracle.compute_expected(seed, [_entry(kind="contract-accept", contract_guid="a")])
        # Hard pools all match the seed; only the report-only contract facet diverges.
        block = _career(funds=0.0, science=0.0, reputation=0.0, contracts=[])  # 'a' absent
        diffs = oracle.diff_expected_vs_parsed(exp, block)
        self.assertFalse(oracle.has_hard_drift(diffs))
        con = [d for d in diffs if d.facet == "contract"]
        self.assertEqual(con[0].kind, "missing")


class WorldVesselDiffTests(unittest.TestCase):
    """Guards the vessel-resource correlation: a guid-correlated drift is hard, a
    pid-only correlation is report-only (a bare persistentId is craft-baked, not
    launch-unique), so a pid collision never certifies the wrong vessel as matched."""

    def test_guid_correlated_resource_drift_hard(self):
        declared = [{"guid": "v1", "resources": {"LiquidFuel": {"expected": 90.0, "tol": 0.1}}}]
        parsed = [{"guid": "v1", "persistentId": 100000, "resourceTotals": {"LiquidFuel": 50.0}}]
        diffs = oracle.diff_world_vessels(declared, parsed)
        self.assertTrue(oracle.has_hard_drift(diffs))
        self.assertEqual(diffs[0].facet, "vessel")

    def test_pid_only_correlation_report_only(self):
        declared = [{"persistentId": 100000, "resources": {"LiquidFuel": {"expected": 90.0}}}]
        parsed = [{"guid": "v1", "persistentId": 100000, "resourceTotals": {"LiquidFuel": 50.0}}]
        diffs = oracle.diff_world_vessels(declared, parsed)
        self.assertFalse(oracle.has_hard_drift(diffs))
        self.assertTrue(diffs and not diffs[0].hard)

    def test_missing_declared_vessel(self):
        declared = [{"guid": "ghost", "resources": {"LiquidFuel": {"expected": 90.0}}}]
        diffs = oracle.diff_world_vessels(declared, [])
        self.assertEqual(diffs[0].kind, "missing")
        self.assertTrue(diffs[0].hard)          # guid-declared miss is hard

    def test_phantom_when_requested(self):
        parsed = [{"guid": "v9", "persistentId": 1, "resourceTotals": {}}]
        diffs = oracle.diff_world_vessels([], parsed, report_phantoms=True)
        self.assertEqual(diffs[0].kind, "phantom")
        self.assertFalse(diffs[0].hard)

    def test_within_tolerance_no_divergence(self):
        declared = [{"guid": "v1", "resources": {"LiquidFuel": {"expected": 90.0, "tol": 0.1}}}]
        parsed = [{"guid": "v1", "persistentId": 1, "resourceTotals": {"LiquidFuel": 90.05}}]
        self.assertEqual(oracle.diff_world_vessels(declared, parsed), [])


# ---------------------------------------------------------------------------
# Verifier-row result serialization (design ~478).
# ---------------------------------------------------------------------------


class OracleResultTests(unittest.TestCase):
    """Guards the ledgerOracle verifier-row: PASS iff no hard drift, FAIL naming the
    UT window on hard drift, report-only counted but never flipping the status, and
    byte-identical serialization so the result diffs cleanly."""

    def test_pass_on_no_hard_drift(self):
        result = oracle.build_oracle_result([])
        self.assertEqual(result["status"], oracle.ORACLE_STATUS_PASS)
        self.assertEqual(result["hardDivergences"], 0)
        self.assertEqual(result["utWindow"], [None, None])

    def test_fail_names_ut_window(self):
        hard = oracle.OracleDivergence("funds", "value-mismatch", "", 200.0, 999.0,
                                       (10.0, 50.0), True, "drift")
        report = oracle.OracleDivergence("subjectScience", "value-mismatch", "s1", 1.0, 2.0,
                                         (None, None), False, "subj")
        result = oracle.build_oracle_result([hard, report])
        self.assertEqual(result["status"], oracle.ORACLE_STATUS_FAIL)
        self.assertEqual(result["hardDivergences"], 1)
        self.assertEqual(result["reportOnly"], 1)
        self.assertEqual(result["utWindow"], [10.0, 50.0])

    def test_report_only_does_not_flip_status(self):
        report = oracle.OracleDivergence("contract", "missing", "a", None, None,
                                         (None, None), False, "c")
        result = oracle.build_oracle_result([report])
        self.assertEqual(result["status"], oracle.ORACLE_STATUS_PASS)
        self.assertEqual(result["reportOnly"], 1)

    def test_status_override_for_tooling(self):
        # A missing careerSave block / KILLED run is stamped by the caller (edge 13/11).
        result = oracle.build_oracle_result([], status_override=oracle.ORACLE_STATUS_INVALID,
                                            reason="careerSave block absent")
        self.assertEqual(result["status"], oracle.ORACLE_STATUS_INVALID)
        self.assertEqual(result["reason"], "careerSave block absent")

    def test_serialization_deterministic(self):
        result = oracle.build_oracle_result([
            oracle.OracleDivergence("funds", "value-mismatch", "", 1.0, 2.0, (1.0, 2.0), True, "d")])
        a = oracle.serialize_oracle_result(result)
        b = oracle.serialize_oracle_result(copy.deepcopy(result))
        self.assertEqual(a, b)
        self.assertTrue(a.endswith("\n"))
        self.assertNotIn("\r\n", a)

    def test_divergence_to_dict_round_shape(self):
        d = oracle.OracleDivergence("funds", "value-mismatch", "", 1.0, 2.0, (1.0, 2.0), True, "detail")
        obj = oracle.divergence_to_dict(d)
        self.assertEqual(obj["facet"], "funds")
        self.assertEqual(obj["utWindow"], [1.0, 2.0])
        self.assertTrue(obj["hard"])


# ---------------------------------------------------------------------------
# End-to-end (fake save JSON, no KSP) -- the two flagship guards (design ~831).
# ---------------------------------------------------------------------------


class EndToEndTests(unittest.TestCase):
    """Guards the two flagship outcomes: the B10 empty-manifest zero-drift PASS (the
    R2 cold-load + no-op passivity guard) and an injected economy drift -> red (the
    most dangerous silent pass this module exists to prevent, BUG-A / BUG-F)."""

    def test_b10_empty_manifest_zero_drift_pass(self):
        seed = oracle.parse_seed_baseline(
            {"funds": 25000.0, "science": 0.0, "reputation": 0.0,
             "hasFunds": True, "hasScience": True, "hasRep": True})
        parse = oracle.parse_manifest_entries([])   # B10 empty manifest
        self.assertTrue(parse.ok)
        exp = oracle.compute_expected(seed, parse.entries)
        block = _career(funds=25000.0, science=0.0, reputation=0.0)
        diffs = oracle.diff_expected_vs_parsed(exp, block)
        result = oracle.build_oracle_result(diffs)
        self.assertEqual(result["status"], oracle.ORACLE_STATUS_PASS)
        self.assertEqual(result["hardDivergences"], 0)

    def test_injected_funds_drift_reds_with_window(self):
        seed = oracle.parse_seed_baseline(
            {"funds": 25000.0, "science": 0.0, "reputation": 0.0,
             "hasFunds": True, "hasScience": True, "hasRep": True})
        exp = oracle.compute_expected(seed, [])     # empty manifest, expected == seed
        # The produced save's funds moved beyond tolerance (a cold-load wipe / drift).
        block = _career(funds=1.0, science=0.0, reputation=0.0)
        diffs = oracle.diff_expected_vs_parsed(exp, block)
        self.assertTrue(oracle.has_hard_drift(diffs))
        result = oracle.build_oracle_result(diffs)
        self.assertEqual(result["status"], oracle.ORACLE_STATUS_FAIL)
        # An unexpected award / drift on an empty manifest names the pool residual window.
        self.assertEqual(result["utWindow"], [None, None])


if __name__ == "__main__":
    unittest.main()
