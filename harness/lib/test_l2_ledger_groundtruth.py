"""Per-claim unit cells for `L2-ledger-groundtruth-career` (career-ledger B.2/B.3).

CLAIM IS NOT GATE. The coverage registry is a promise: a dimension value listed in
a spec's `[dimensionsCovered]` asserts that THIS spec would red if that thing broke.
Nothing in the harness enforces that on its own - `validate_spec` checks only that a
claimed value EXISTS in `coverage/registry.toml` - so a spec can claim a cell it does
not gate and the coverage report will quietly count it. `Cl2CoverageClaimTests`
(harness/missions/lib/test_cl2_crew_loss_ledger.py) is the pattern that closes that
gap per spec, and the plan doc's B.3 row says why L2 gets one: the six L1 specs and
B10 have no such cells, and repeating that omission on the first D8
`ground-truth-harness` claim in the tree would leave the claim unfalsifiable.

Four groups:

  1. THE CLAIM AND ITS BACKING GATE. Every claimed value exists in the registry, and
     the D8 cell is backed by required tokens in this spec's own logContracts.
  2. THE SCOPE FENCE. What `ground-truth-harness` does NOT claim, and why. B.1's
     triage measured the subject as thin, so the per-facet D8 cells stay unclaimed.
  3. THE ARMED BLOCKS. The ledger and unityExceptions blocks are declared and
     validate through the very code the harness runs post-flight.
  4. THE B.4 FENCE. No `strict` arg on the RunTests step: arming strict here would
     be a gate that cannot bite (`reportOnly=0` on this fixture).

NO KSP, no network, no krpc. Import path matches the sibling suites in this package.
"""

import os
import re
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import hlib  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "L2-ledger-groundtruth-career.toml")
REGISTRY_PATH = os.path.join(_HARNESS, "coverage", "registry.toml")

# The one D8 cell this spec claims. Named as a constant so the scope fence below can
# be written as "every OTHER D8 value stays unclaimed" rather than as a hand-copied
# list that goes stale the day the registry grows a value.
CLAIMED_D8 = "ground-truth-harness"


def _load(path):
    with open(path, "rb") as fh:
        return tomllib.load(fh)


class L2CoverageClaimTests(unittest.TestCase):
    """Group 1: the claim, and the token that makes it falsifiable."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _load(SPEC_PATH)
        cls.registry = _load(REGISTRY_PATH)
        cls.claimed = cls.spec["dimensionsCovered"]
        cls.required = cls.spec["expectations"]["logContracts"]["required"]

    def test_every_claimed_value_exists_in_the_registry(self):
        for dim, values in self.claimed.items():
            for value in values:
                self.assertIn(value, self.registry[dim]["values"],
                              "%s value %r is not in coverage/registry.toml" % (dim, value))

    def test_the_d8_claim_is_exactly_the_ground_truth_harness_cell(self):
        # B.1's triage (plan doc 4b, finding 4) is what fixes the SIZE of this claim.
        # The loop is what runs unattended; the facets that flow through it are thin on
        # career-pad-craft, so exactly one cell is claimed and it is the loop's.
        self.assertEqual([CLAIMED_D8], self.claimed["D8"])

    def test_the_d8_claim_is_backed_by_the_diff_result_token(self):
        # THE BACKING GATE. LedgerGroundTruthHarness logs
        #   result: hardFailures=N reportOnly=N facetsCompared=N strict=B
        # from Step 10, immediately before InGameAssert.IsTrue(hard == 0). Requiring
        # the measured line pins the same number the assertion reads, so the spec and
        # the cell cannot drift apart.
        matching = [p for p in self.required if p.startswith("result: hardFailures=")]
        self.assertEqual(1, len(matching),
                         "exactly one ground-truth result token expected: %r" % (matching,))
        self.assertIn("hardFailures=0", matching[0])

    def test_the_result_token_pins_the_facet_count_as_the_anti_vacuity_half(self):
        # A diff that compared NOTHING reports zero hard failures too. `facetsCompared`
        # is what separates "the loop closed clean" from "the loop did not run", so the
        # count is pinned rather than left as a wildcard. 7 is B.1's measurement.
        matching = [p for p in self.required if p.startswith("result: hardFailures=")][0]
        self.assertIn("facetsCompared=7", matching)

    def test_the_result_token_pins_strict_off(self):
        # career-ledger B.4's deferral, made mechanical: this spec runs the DEFAULT
        # diff, and a future edit that arms `strict` must re-pin this line rather than
        # slip the change past a token that never mentioned it.
        matching = [p for p in self.required if p.startswith("result: hardFailures=")][0]
        self.assertIn("strict=False", matching)

    def test_the_batch_tally_is_the_second_half_of_the_backing_gate(self):
        # `failed=0` gates the cell's own assertion at the RUNNER level, and the whole
        # tally is pinned (hlib.batch_contract_vacuity_gap rejects a RunTests spec whose
        # contract an empty or all-skipped batch could satisfy). `passed=1` is the cell
        # EXECUTING rather than merely being admitted - which is the property a
        # ground-truth claim needs and the one B.1 flew to establish.
        matching = [p for p in self.required if p.startswith("BATCH_COMPLETE")]
        self.assertEqual(1, len(matching), "exactly one tally pin expected")
        for token in ("total=2", "passed=1", "failed=0", "skipped=1",
                      "category=LedgerGroundTruth", "scene=FLIGHT"):
            self.assertIn(token, matching[0])

    def test_the_environment_claims_are_the_boot_facts(self):
        self.assertEqual(sorted(["career", "scene-flight"]), sorted(self.claimed["D14"]))


class L2ScopeFenceTests(unittest.TestCase):
    """Group 2: what the claim deliberately does NOT cover.

    The temptation on a spec that diffs funds, science, reputation, facilities, roster
    and tech is to claim those D8 cells too. B.1 measured why that would be false: of
    the 7 compared facets only the three seeded pools are two-sided, per-subject
    science compares 0 against 0, and facilities / roster / tech compare an EMPTY
    delta-only reconstruction against a populated save. Claiming them would hide the
    backlog the coverage report exists to show.
    """

    @classmethod
    def setUpClass(cls):
        cls.spec = _load(SPEC_PATH)
        cls.registry = _load(REGISTRY_PATH)
        cls.claimed = cls.spec["dimensionsCovered"]

    def test_no_other_d8_cell_is_claimed(self):
        # Written as "every other value", so a registry that grows a new D8 cell does
        # not need this list edited - and a spec that quietly starts claiming one does
        # red here.
        for value in self.registry["D8"]["values"]:
            if value == CLAIMED_D8:
                continue
            self.assertNotIn(value, self.claimed["D8"],
                             "D8 %r claimed on a fixture whose facet is not two-sided "
                             "(see the B.1 triage in the spec's tally note)" % (value,))

    def test_only_d8_and_d14_are_claimed(self):
        self.assertEqual(sorted(["D8", "D14"]), sorted(self.claimed))


class L2ArmedBlockTests(unittest.TestCase):
    """Group 3: the two blocks B.2 armed, checked through the harness's own code."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _load(SPEC_PATH)
        cls.expectations = cls.spec["expectations"]

    def test_the_ledger_block_is_declared_and_empty_manifest(self):
        # The B10 / L1-passive-sandbox shape: expected == seed. An entry appearing here
        # would mean somebody expected the batch to MOVE a pool, which this scenario
        # never does - the only pool writes it performs are RecalculateAndPatch's
        # measured no-op and the finally-restore that no-op leaves inert.
        ledger = self.expectations["ledger"]
        self.assertEqual("template", ledger["seedFrom"])
        self.assertEqual("default", ledger["tolerances"])
        self.assertFalse(ledger["rec3CarveOut"])
        self.assertEqual([], ledger.get("manifest", []))

    def test_the_ledger_block_passes_the_pre_launch_validator(self):
        # The same function run.py front-runs the flight with, so a block that would
        # PARSEK-FAIL(ledger) after a boot reds here instead.
        self.assertEqual([], hlib.validate_ledger_expectations(self.expectations["ledger"]))

    def test_capture_cross_check_stays_report_only(self):
        # The armed set for the gate mode is CL-2 only (test_hlib pins it). An empty
        # manifest plus a gating cross-check would red on any stock award the batch
        # happened to produce, and this spec has never measured one.
        self.assertFalse(hlib.capture_cross_check_gates(self.expectations["ledger"]))

    def test_the_unity_exception_ceiling_is_armed_at_zero(self):
        block = self.expectations[hlib.UNITY_EXCEPTIONS_BLOCK]
        self.assertEqual(0, block[hlib.UNITY_EXCEPTIONS_MAX_TOTAL_KEY])

    def test_no_rewind_or_merge_dialog_step_pairs_with_the_ledger_block(self):
        # hlib hard-rejects [expectations.ledger] paired with InvokeRewind or
        # AnswerMergeDialog: a rewind rewrites the pools from a quicksave the
        # seed+manifest contract cannot reconstruct. This spec is batch-only, and the
        # cell keeps it that way - a future step of either kind has to drop the block.
        cmds = [(s or {}).get("cmd") for s in self.spec["driver"]["steps"]]
        for cmd in ("InvokeRewind", "AnswerMergeDialog"):
            self.assertNotIn(cmd, cmds)

    def test_the_recording_free_pin_is_intact(self):
        # count.max == 0 also selects the B1 recording-rules log-validation suppression,
        # so a legitimately recording-free run does not red on REC-001/REC-003 - and it
        # is a real assertion: auto-record is off and the in-game cell writes nothing.
        rec = self.expectations["recordings"]["count"]
        self.assertEqual(0, rec["min"])
        self.assertEqual(0, rec["max"])
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"])

    def test_the_autorecord_setting_step_precedes_the_batch(self):
        # LOAD-BEARING rather than cosmetic: a live recorder makes the in-game cell
        # Skip (guard 4), which would turn passed=1 into skipped=2 and red the tally
        # for a reason that reads like a product defect.
        steps = self.spec["driver"]["steps"]
        names = [(s or {}).get("cmd") for s in steps]
        set_idx = next(i for i, s in enumerate(steps)
                       if (s or {}).get("cmd") == "SetSetting"
                       and ((s or {}).get("args") or {}).get("name") == "autoRecordOnLaunch")
        self.assertEqual("false", steps[set_idx]["args"]["value"])
        self.assertLess(set_idx, names.index("RunTests"))


class L2StrictModeFenceTests(unittest.TestCase):
    """Group 4: career-ledger B.4's deferral, pinned on this spec specifically.

    The lane-wide version of this fence lives in test_hlib
    (`test_no_committed_spec_arms_the_runtests_strict_arg`). This one is narrower and
    says WHY for the one spec the arg was written for: strict promotes report-only
    per-identity divergences to hard failures, and B.1 measured `reportOnly=0` here.
    """

    @classmethod
    def setUpClass(cls):
        cls.spec = _load(SPEC_PATH)

    def test_the_runtests_step_declares_no_strict_arg(self):
        for step in self.spec["driver"]["steps"]:
            if (step or {}).get("cmd") != "RunTests":
                continue
            self.assertNotIn(hlib.RUNTESTS_STRICT_KEY, (step.get("args") or {}),
                             "arming strict on career-pad-craft is a gate that cannot "
                             "bite (reportOnly=0); record the subject first")

    def test_the_required_token_would_red_if_strict_were_armed(self):
        # The fence is not only a cell in this file: the pinned `strict=False` token
        # means an armed run reds on its own logContract. Two independent signals for
        # one decision, deliberately.
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertTrue(any("strict=False" in p for p in required))


class L2LogContractHygieneTests(unittest.TestCase):
    """The CL-1/CL-2 hygiene rules, applied to this spec's two tokens."""

    @classmethod
    def setUpClass(cls):
        cls.contracts = _load(SPEC_PATH)["expectations"]["logContracts"]

    def test_every_pattern_compiles_and_is_ascii(self):
        for pattern in self.contracts["required"] + self.contracts["forbidden"]:
            re.compile(pattern)
            pattern.encode("ascii")

    def test_the_required_tokens_carry_no_unescaped_regex_metacharacters(self):
        # Both required tokens are meant as LITERALS. A stray metacharacter would make
        # the pattern match something other than the emitted line - or nothing at all -
        # and the failure would read as a missing log line rather than a bad regex.
        for pattern in self.contracts["required"]:
            for meta in ("[", "]", "(", ")", "|", "\\", "*", "+", "?"):
                self.assertNotIn(meta, pattern,
                                 "%r carries a regex metacharacter: %r" % (pattern, meta))

    def test_the_error_forbid_is_still_declared(self):
        # The cell reports every facet at Warn (report.Format()), never at Error, so a
        # Parsek ERROR line is a genuine defect signal and not the diff talking.
        self.assertIn("\\[Parsek\\]\\[ERROR\\]", self.contracts["forbidden"])


if __name__ == "__main__":
    unittest.main()
