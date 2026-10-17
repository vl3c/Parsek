"""Unit tests for the rewind-aware ledger model's spec surface and verifier wiring
(TC-4): `timeline = "rewind-aware"` in `[expectations.ledger]`, the
`retiredAttempts` list, the per-entry `attempt` / `onceKey` labels, and the
produced save's clock (`careerSave.universalTime`) the verifier places every row
against. The pure math is covered in test_oracle.RewindAwareTimelineTests."""

import copy
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import hlib  # noqa: E402
import ledgerverify  # noqa: E402
import mutledger  # noqa: E402
import oracle  # noqa: E402
from test_hlib import load_registry, load_spec  # noqa: E402

SEED = oracle.SeedBaseline(funds=100000.0, science=10.0, reputation=0.0)


def _career(funds, science=10.0, rep=0.0, ut=None):
    block = {"parsed": True, "hasFunds": True, "funds": funds,
             "hasScience": True, "sciencePool": science,
             "hasRep": True, "reputation": rep,
             "subjectScience": {}, "activeContractGuids": []}
    if ut is not None:
        block["hasUniversalTime"] = True
        block["universalTime"] = ut
    return block


def _ledger(manifest, **extra):
    block = {"seedFrom": "template", "tolerances": "default",
             "timeline": "rewind-aware", "manifest": manifest}
    block.update(extra)
    return block


class RewindAwareSpecValidationTests(unittest.TestCase):
    """A rewind lane's ledger block is checked pre-launch: each rule otherwise costs a
    flight."""

    def setUp(self):
        self.reg = load_registry()
        self.base = load_spec("B10-career-passive-safety.toml")

    def _validate(self, mutate):
        spec = copy.deepcopy(self.base)
        mutate(spec)
        return hlib.validate_spec(spec, self.reg)

    def test_rewind_aware_block_may_pair_with_every_timeline_verb(self):
        def m(s):
            s["expectations"]["ledger"] = _ledger(
                [{"kind": "milestone", "ut": 12.0, "funds": 800.0}])
            for verb in ("InvokeRewindToLaunch", "InvokeRewind", "AnswerMergeDialog"):
                s["driver"]["steps"].insert(1, {"cmd": verb, "expect": "OK"})
        v = self._validate(m)
        self.assertFalse(any("cannot pair with" in e for e in v.errors), list(v.errors))

    def test_sum_model_still_refuses_a_rewind(self):
        def m(s):
            s["driver"]["steps"].insert(1, {"cmd": "InvokeRewindToLaunch", "expect": "OK"})
        v = self._validate(m)
        self.assertTrue(any("cannot pair with" in e and "rewind-aware" in e
                            for e in v.errors), list(v.errors))

    def test_unknown_model_is_rejected(self):
        v = self._validate(lambda s: s["expectations"]["ledger"].__setitem__(
            "timeline", "rewound"))
        self.assertTrue(any("expectations.ledger.timeline" in e for e in v.errors))

    def test_entry_off_the_clock_is_rejected_under_the_rewind_model(self):
        v = self._validate(lambda s: s["expectations"].__setitem__(
            "ledger", _ledger([{"kind": "milestone", "funds": 800.0}])))
        self.assertTrue(any("needs ut or utWindow" in e for e in v.errors), list(v.errors))

    def test_retired_attempts_shape_and_typo_are_rejected(self):
        manifest = [{"kind": "milestone", "ut": 1.0, "funds": 5.0, "attempt": "flight-a"}]
        for retired, needle in ((["flight-b"], "names no manifest entry"),
                                ("flight-a", "must be an array"),
                                ([""], "must be an array")):
            with self.subTest(retired=retired):
                v = self._validate(lambda s: s["expectations"].__setitem__(
                    "ledger", _ledger(manifest, retiredAttempts=retired)))
                self.assertTrue(any(needle in e for e in v.errors), list(v.errors))
        v = self._validate(lambda s: s["expectations"].__setitem__(
            "ledger", _ledger(manifest, retiredAttempts=["flight-a"])))
        self.assertFalse(any("retiredAttempts" in e for e in v.errors), list(v.errors))

    def test_retired_attempts_under_the_sum_model_is_rejected(self):
        def m(s):
            s["expectations"]["ledger"]["retiredAttempts"] = ["a"]
        v = self._validate(m)
        self.assertTrue(any("retiredAttempts" in e and "would retire nothing" in e
                            for e in v.errors), list(v.errors))

    def test_empty_attempt_label_is_rejected_by_the_oracle_parser(self):
        v = self._validate(lambda s: s["expectations"].__setitem__(
            "ledger", _ledger([{"kind": "milestone", "ut": 1.0, "attempt": " "}])))
        self.assertTrue(any("attempt" in e and "non-empty" in e for e in v.errors))


class RewindAwareVerifierTests(unittest.TestCase):
    """The verifier reads the clock off the produced careerSave block and compares
    the AVAILABLE pools at it."""

    MANIFEST = [
        {"kind": "vessel-recovery", "ut": 50.0, "funds": 20000.0, "attempt": "a"},
        {"kind": "facility-upgrade", "ut": 60.0, "funds": -75000.0},
        {"kind": "tech-unlock", "ut": 60.0, "science": -5.0},
    ]

    def test_available_pools_at_a_rewound_clock_pass(self):
        # Clock 10 (rewound): running 100000; future +20000 then -75000 -> min 45000.
        v = ledgerverify.evaluate(_ledger(self.MANIFEST), None,
                                  _career(45000.0, science=5.0, ut=10.0), SEED, "")
        self.assertEqual(v.result["status"], oracle.ORACLE_STATUS_PASS, v.divergences)
        self.assertEqual(v.manifest["timeline"]["endUt"], 10.0)
        self.assertEqual(v.manifest["timeline"]["funds"]["reserved"], 55000.0)

    def test_the_sum_value_reds_at_a_rewound_clock(self):
        # A save that showed seed + every delta (the reservation ignored) is drift.
        v = ledgerverify.evaluate(_ledger(self.MANIFEST), None,
                                  _career(45000.0 + 0.0 + 0.0, science=10.0 - 5.0 + 5.0,
                                          ut=10.0), SEED, "")
        self.assertTrue(v.drift)
        self.assertTrue(any(d.facet == "sciencePool" and d.hard for d in v.divergences))

    def test_retired_attempt_moves_the_expectation(self):
        block = _ledger(self.MANIFEST, retiredAttempts=["a"])
        # Without the recovery: running 100000, future -75000 -> 25000.
        v = ledgerverify.evaluate(block, None, _career(25000.0, science=5.0, ut=10.0),
                                  SEED, "")
        self.assertEqual(v.result["status"], oracle.ORACLE_STATUS_PASS, v.divergences)

    def test_missing_clock_is_tooling_invalid_never_a_sum_fallback(self):
        v = ledgerverify.evaluate(_ledger(self.MANIFEST), None, _career(45000.0), SEED, "")
        self.assertTrue(v.tooling)
        self.assertEqual(v.result["status"], oracle.ORACLE_STATUS_INVALID)
        self.assertIn("clock", v.result["reason"])

    def test_straddling_window_reds_hard(self):
        manifest = [{"kind": "milestone", "utWindow": [5.0, 15.0], "funds": 800.0}]
        v = ledgerverify.evaluate(_ledger(manifest), None, _career(100000.0, ut=10.0),
                                  SEED, "")
        self.assertTrue(any(d.kind == "timeline-unplaceable" and d.hard
                            for d in v.divergences))

    def test_sum_model_ignores_the_clock_and_keeps_its_manifest_shape(self):
        block = {"seedFrom": "template", "tolerances": "default",
                 "manifest": [{"kind": "milestone", "ut": 0.0, "funds": 800.0}]}
        v = ledgerverify.evaluate(block, None, _career(100800.0, ut=10.0), SEED, "")
        self.assertEqual(v.result["status"], oracle.ORACLE_STATUS_PASS)
        self.assertNotIn("timeline", v.manifest)
        self.assertNotIn("attempt", v.manifest["entries"][0])

    def test_clock_reader_rejects_absent_and_non_finite_values(self):
        self.assertIsNone(ledgerverify.career_save_clock(None))
        self.assertIsNone(ledgerverify.career_save_clock({"universalTime": 5.0}))
        self.assertIsNone(ledgerverify.career_save_clock(
            {"hasUniversalTime": True, "universalTime": None}))
        self.assertIsNone(ledgerverify.career_save_clock(
            {"hasUniversalTime": True, "universalTime": float("nan")}))
        self.assertEqual(ledgerverify.career_save_clock(
            {"hasUniversalTime": True, "universalTime": 12}), 12.0)

    def test_mutation_planner_uses_the_rewind_model(self):
        exps = {"ledger": _ledger(self.MANIFEST)}
        seed_block = {"funds": 100000.0, "sciencePool": 10.0, "reputation": 0.0,
                      "hasFunds": True, "hasScience": True, "hasRep": True}
        exp = mutledger._expected(exps, seed_block, "", _career(0.0, ut=10.0))
        self.assertEqual(exp.funds, 45000.0)
        self.assertIsNone(mutledger._expected(exps, seed_block, "", _career(0.0)))


if __name__ == "__main__":
    unittest.main()
