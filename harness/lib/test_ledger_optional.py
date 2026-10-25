"""Unit tests for optional ledger awards (todo CL-2-POD-LANDING-AWARD-VARIANCE):
`[[expectations.ledger.optional]]` entries, admitted into the declared set only
when the event-time capture and the produced save both book the milestone. The
decision table is oracle.decide_optional_award; the wiring is
ledgerverify.resolve_optional_awards inside ledgerverify.evaluate."""

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

SEED = oracle.SeedBaseline(funds=500000.0, science=100.0, reputation=0.0)
LANDING = "Kerbin/Landing"
LANDING_LINE = ("[LOG 01:02:03.456] [Parsek][INFO][GameStateRecorder] Milestone enriched: "
                "'Kerbin/Landing' funds=800 rep=0.0 sci=0.0\n")


def _career(funds, completed=(), science=100.0, rep=0.0):
    return {"parsed": True, "hasFunds": True, "funds": funds,
            "hasScience": True, "sciencePool": science,
            "hasRep": True, "reputation": rep,
            "subjectScience": {}, "activeContractGuids": [],
            "completedMilestoneIds": list(completed)}


def _block(optional=None, manifest=None, **extra):
    block = {"seedFrom": "template", "tolerances": "default",
             "manifest": manifest if manifest is not None else
             [{"seq": 0, "kind": "milestone", "funds": 29600.0}]}
    if optional is not None:
        block["optional"] = optional
    block.update(extra)
    return block


OPTIONAL_LANDING = [{"milestone": LANDING, "seq": 4, "kind": "milestone", "funds": 800.0}]


class OptionalAwardDecisionTests(unittest.TestCase):
    """Admitted only on both sides; dropped on neither; red on any other shape."""

    def _d(self, captured, saved, funds=800.0, sci=0.0):
        return oracle.decide_optional_award(LANDING, captured, saved, funds, sci)

    def test_booked_on_both_sides_is_admitted(self):
        d = self._d([(800.0, 0.0, 0.0)], True)
        self.assertEqual((oracle.OPTIONAL_ADMITTED, True, False), (d.decision, d.admitted, d.hard))
        self.assertIsNone(oracle.optional_decision_divergence(d))

    def test_booked_on_neither_side_is_dropped_quietly(self):
        d = self._d([], False)
        self.assertEqual((oracle.OPTIONAL_ABSENT, False, False), (d.decision, d.admitted, d.hard))
        self.assertIsNone(oracle.optional_decision_divergence(d))

    def test_one_side_only_is_hard_either_way(self):
        for captured, saved in (([(800.0, 0.0, 0.0)], False), ([], True)):
            with self.subTest(captured=captured, saved=saved):
                d = self._d(captured, saved)
                self.assertEqual(oracle.OPTIONAL_DISAGREEMENT, d.decision)
                div = oracle.optional_decision_divergence(d)
                self.assertTrue(div.hard)
                self.assertEqual("optional-award-disagreement", div.kind)
                self.assertEqual(LANDING, div.identity)

    def test_a_repeated_award_is_hard(self):
        d = self._d([(800.0, 0.0, 0.0), (800.0, 0.0, 0.0)], True)
        self.assertEqual((oracle.OPTIONAL_REPEATED, True), (d.decision, d.hard))

    def test_another_size_is_hard_and_tolerance_bounded(self):
        self.assertEqual(oracle.OPTIONAL_AMOUNT_MISMATCH,
                         self._d([(1600.0, 0.0, 0.0)], True).decision)
        self.assertEqual(oracle.OPTIONAL_AMOUNT_MISMATCH,
                         self._d([(800.0, 0.0, 2.0)], True).decision)
        # Inside the funds tolerance (1.0): the F0-rounded capture still matches.
        self.assertEqual(oracle.OPTIONAL_ADMITTED, self._d([(800.4, 0.0, 0.0)], True).decision)


class MilestoneCaptureParseTests(unittest.TestCase):

    def test_both_line_shapes_parse_by_milestone_in_log_order(self):
        text = (LANDING_LINE
                + "[LOG] [Parsek][VERBOSE][GameStateRecorder] Milestone enriched (ledger only, "
                  "store skipped): 'RecordsSpeed' funds=4800 rep=1.0 sci=0.0\n"
                + "[LOG] [Parsek][INFO][GameStateRecorder] Milestone enriched: 'Kerbin/Landing' "
                  "funds=800 rep=0.0 sci=0.0\n")
        got = hlib.parse_milestone_award_lines(text)
        self.assertEqual([(800.0, 0.0, 0.0), (800.0, 0.0, 0.0)], got[LANDING])
        self.assertEqual([(4800.0, 1.0, 0.0)], got["RecordsSpeed"])
        self.assertEqual({}, hlib.parse_milestone_award_lines(
            "[Parsek][INFO][GameStateRecorder] MilestoneAchieved 'Kerbin/Landing'\n"))

    def test_the_pattern_tracks_the_csharp_literal(self):
        """The capture side reads GameStateRecorder's own line; reword it and every
        optional award would read as captured-nowhere (a disagreement red)."""
        root = os.path.abspath(os.path.join(_HERE, "..", ".."))
        path = os.path.join(root, "Source", "Parsek", "GameStateRecorder.Handlers.cs")
        with open(path, "r", encoding="utf-8") as fh:
            src = fh.read()
        self.assertIn("$\"Milestone enriched: '{evt.key}' funds={funds:F0} rep={rep:F1} "
                      "sci={sci:F1}\"", src)
        self.assertIn("$\"Milestone enriched (ledger only, store skipped): '{evt.key}' \" +", src)


class OptionalSpecValidationTests(unittest.TestCase):
    """The optional surface is checked pre-launch: each rule otherwise costs a flight."""

    def setUp(self):
        self.reg = load_registry()
        self.base = load_spec("CL-2-pod-impact-ledger.toml")

    def _errors(self, mutate):
        spec = copy.deepcopy(self.base)
        mutate(spec["expectations"]["ledger"])
        return [e for e in hlib.validate_spec(spec, self.reg).errors if "optional" in e]

    def test_cl2_declares_kerbin_landing_and_validates(self):
        opt = self.base["expectations"]["ledger"]["optional"]
        self.assertEqual([LANDING], [e["milestone"] for e in opt])
        self.assertEqual([800.0], [e["funds"] for e in opt])
        self.assertEqual([], self._errors(lambda b: None))

    def test_bad_shapes_are_refused(self):
        cases = {
            "not-array": lambda b: b.__setitem__("optional", {"milestone": LANDING}),
            "no-milestone": lambda b: b["optional"][0].pop("milestone"),
            "blank-milestone": lambda b: b["optional"][0].__setitem__("milestone", " "),
            "bad-kind": lambda b: b["optional"][0].__setitem__("kind", "landing"),
            "null-funds": lambda b: b["optional"][0].__setitem__("funds", None),
            "rewind-aware": lambda b: b.__setitem__("timeline", "rewind-aware"),
        }
        for name, mutate in cases.items():
            with self.subTest(name=name):
                self.assertTrue(self._errors(mutate), name)


class OptionalVerifierTests(unittest.TestCase):
    """End to end through ledgerverify.evaluate, CL-2's shape: seed 500000, a declared
    29600, an optional Kerbin/Landing worth 800."""

    def _eval(self, career, log_text="", block=None):
        return ledgerverify.evaluate(block or _block(OPTIONAL_LANDING), None, career, SEED,
                                     log_text)

    def test_a_landed_run_passes_with_the_award_admitted(self):
        v = self._eval(_career(530400.0, completed=[LANDING, "Landing"]), LANDING_LINE)
        self.assertEqual(oracle.ORACLE_STATUS_PASS, v.result["status"], v.divergences)
        self.assertEqual([{"milestone": LANDING, "decision": "admitted", "capturedCount": 1,
                           "saveCompleted": True, "entries": 1,
                           "detail": "booked on both sides"}], v.manifest["optional"])
        self.assertEqual([29600.0, 800.0], [e["funds"] for e in v.manifest["entries"]])

    def test_a_run_that_never_landed_passes_on_the_declared_total(self):
        v = self._eval(_career(529600.0))
        self.assertEqual(oracle.ORACLE_STATUS_PASS, v.result["status"], v.divergences)
        self.assertEqual("absent", v.manifest["optional"][0]["decision"])

    def test_without_the_optional_entry_the_landing_still_reds(self):
        v = self._eval(_career(530400.0, completed=[LANDING]), LANDING_LINE, block=_block())
        self.assertTrue(v.drift)
        self.assertTrue(any(d.facet == "funds" and d.hard for d in v.divergences))
        self.assertNotIn("optional", v.manifest)

    def test_an_award_one_side_lost_reds(self):
        # Captured but not completed in the save (the ledger dropped it), and the reverse.
        for career, text in ((_career(530400.0), LANDING_LINE),
                             (_career(530400.0, completed=[LANDING]), "")):
            with self.subTest(text=bool(text)):
                v = self._eval(career, text)
                self.assertTrue(v.drift)
                self.assertTrue(any(d.kind == "optional-award-disagreement" and d.hard
                                    for d in v.divergences))

    def test_an_admitted_award_missing_from_the_pool_reds(self):
        v = self._eval(_career(529600.0, completed=[LANDING]), LANDING_LINE)
        self.assertTrue(any(d.facet == "funds" and d.hard for d in v.divergences))

    def test_a_repeated_or_resized_award_reds(self):
        for text in (LANDING_LINE * 2, LANDING_LINE.replace("funds=800", "funds=1600")):
            with self.subTest(text=text[-40:]):
                v = self._eval(_career(530400.0, completed=[LANDING]), text)
                self.assertTrue(v.drift)
                self.assertTrue(any(d.kind.startswith("optional-award-") and d.hard
                                    for d in v.divergences))

    def test_an_admitted_rep_entry_corroborates_a_captured_award(self):
        opt = OPTIONAL_LANDING + [{"milestone": LANDING, "seq": 5,
                                   "kind": "stock-reputation-award", "reputation": 0.9999995,
                                   "repMode": "applied", "stockReason": "Progression",
                                   "utWindow": [100.0, 400.0],
                                   "provenance": "gameevents-captured"}]
        block = _block(opt, captureCrossCheck="gate")
        rep_line = ("[LOG] [Parsek][INFO][Recorder] sample ut=120.0\n"
                    "[LOG] Added 0.9999995 (1) reputation: 'Progression'.\n")
        landed = ledgerverify.evaluate(block, None,
                                       _career(530400.0, completed=[LANDING], rep=0.9999995),
                                       SEED, LANDING_LINE + rep_line)
        self.assertEqual(oracle.ORACLE_STATUS_PASS, landed.result["status"], landed.divergences)
        # The same captured award with the optional NOT booked is unexpected.
        unbooked = ledgerverify.evaluate(block, None, _career(529600.0, rep=0.9999995),
                                         SEED, rep_line)
        self.assertTrue(any(d.kind == "unexpected-award" and d.hard
                            for d in unbooked.divergences))

    def test_no_milestone_surface_is_tooling_invalid(self):
        career = _career(530400.0)
        del career["completedMilestoneIds"]
        v = self._eval(career, LANDING_LINE)
        self.assertTrue(v.tooling)
        self.assertEqual(oracle.ORACLE_STATUS_INVALID, v.result["status"])

    def test_a_malformed_optional_entry_reds_rather_than_dropping(self):
        v = self._eval(_career(529600.0),
                       block=_block([{"milestone": LANDING, "kind": "landing", "funds": 800.0}]))
        self.assertTrue(any(d.kind == "manifest-parse-error" and d.hard for d in v.divergences))

    def test_the_mutation_planner_sees_the_admitted_award(self):
        exp = mutledger._expected({"ledger": _block(OPTIONAL_LANDING)},
                                  {"funds": 500000.0, "sciencePool": 100.0, "reputation": 0.0,
                                   "hasFunds": True, "hasScience": True, "hasRep": True},
                                  LANDING_LINE, _career(530400.0, completed=[LANDING]))
        self.assertEqual(530400.0, exp.funds)


if __name__ == "__main__":
    unittest.main()
