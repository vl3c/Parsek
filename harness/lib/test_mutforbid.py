"""Unit tests for mutforbid (the forbidden-token mutation checker, phase 2 PR 3).

The sampler cells hold every regex shape the committed specs use (lookarounds,
back-references, negated classes, ``[\\s\\S]*``, bounded repeats) to a string the
regex itself matches, and one cell runs that over EVERY committed spec's forbidden
list, so a newly added pattern the checker cannot inject reds here rather than
reading UNCHECKED in a sweep nobody runs. The injection cells have a positive case
(a literal token reds on its framed line), the anchored cases (bare-only, and
never), and a vacuity cell that breaks the real evaluator's forbidden loop.
"""

import os
import re
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import hlib  # noqa: E402
import mutforbid  # noqa: E402
import mutlib  # noqa: E402
from test_mutsave import Patched  # noqa: E402

LOG = ("[LOG 10:00:00.000] [Parsek][INFO][Boot] Parsek loaded\n"
       "[LOG 10:00:01.000] [Parsek][INFO][Flight] tick ut=12.6\n"
       "[LOG 10:00:02.000] [Parsek][INFO][TestCommands] flushandquit: Application.Quit\n")


def _spec(*forbidden):
    return {"id": "T-forbid", "expectations": {"logContracts": {"forbidden": list(forbidden)}}}


def _gates(*forbidden, log=LOG, corpus=None):
    check = mutforbid.check_forbidden(_spec(*forbidden)["expectations"], log, corpus)
    return check, {g.label[len("forbidden["):-1]: g for g in check.gates}


class SampleTests(unittest.TestCase):

    SHAPES = [
        r"\[Parsek\]\[ERROR\]",
        r"timejump refused reason=backward-jump",
        r"faithful-parity summary sampled=\d+ overTolerance=[1-9][0-9]*",
        r"No ghost node for key=cn-relay-[ab] vessel=\"[^\"]*\": (?!before-window)",
        r"Ground part placed: tree member created [^\r\n]*rec=([0-9a-f]+) [\s\S]*MISSIONMARK "
        r"label=eva9-spawned-count-[^\r\n]*-\1-false-",
        r"Scene load complete \((EDITOR|FLIGHT)\): (?!9 recordings,)[0-9]+ recordings,",
        r"Backfilled launch guid [0-9a-f]{32} from snapshot",
        r"(?s)Recomputed after tombstones: .*exec id=[0-9]+ cmd=LoadGame start.*PostWalk",
        r"\[Parsek\]\[ERROR\](?!\[RecordingStore\] SaveRecordingFiles failed: )",
        r"(?i)allowing DECLINE",
    ]

    def test_every_shape_samples_to_a_self_matching_string(self):
        for pat in self.SHAPES:
            s = mutforbid.sample_match(pat)
            self.assertIsNotNone(s, pat)
            self.assertIsNotNone(re.search(pat, s), (pat, s))

    def test_an_unsatisfiable_regex_samples_to_none(self):
        self.assertIsNone(mutforbid.sample_match(r"a\bb"))
        self.assertIsNone(mutforbid.sample_match(r"(unclosed"))

    def test_every_committed_forbidden_pattern_is_injectable(self):
        sdir = os.path.join(os.path.dirname(_HERE), "scenarios")
        seen = set()
        for name in sorted(os.listdir(sdir)):
            if not name.endswith(".toml"):
                continue
            with open(os.path.join(sdir, name), "rb") as fh:
                spec = tomllib.load(fh)
            lc = (spec.get("expectations") or {}).get("logContracts") or {}
            for pat in lc.get("forbidden", []) or []:
                if pat in seen:
                    continue
                seen.add(pat)
                check, gates = _gates(pat)
                self.assertEqual("PROVEN", gates[pat].verdict, (name, pat, gates[pat].reason))
        self.assertGreater(len(seen), 100)


class InjectionTests(unittest.TestCase):

    def test_a_literal_token_reds_on_its_framed_line(self):
        check, gates = _gates(r"\[Parsek\]\[ERROR\]", "routecommand rejected")
        for pat in (r"\[Parsek\]\[ERROR\]", "routecommand rejected"):
            self.assertEqual("PROVEN", gates[pat].verdict)
            self.assertEqual("red on the framed line", gates[pat].reason)
        self.assertEqual(2, len(check.mutations))

    def test_a_token_the_log_already_carries_is_unchecked(self):
        _c, gates = _gates("Parsek loaded")
        self.assertEqual("UNCHECKED", gates["Parsek loaded"].verdict)

    def test_a_multiline_anchor_kills_on_the_bare_line(self):
        _c, gates = _gates(r"(?m)^\tParsek\.Probe")
        self.assertEqual("PROVEN", gates[r"(?m)^\tParsek\.Probe"].verdict)
        self.assertEqual("red on the bare line", gates[r"(?m)^\tParsek\.Probe"].reason)

    def test_a_text_start_anchor_can_never_fire_inside_a_log(self):
        for pat in (r"^ParsekNeverHere", r"\AParsekNeverHere"):
            _c, gates = _gates(pat)
            self.assertEqual("VACUOUS", gates[pat].verdict, pat)
            self.assertIn("framed and bare", gates[pat].reason)

    def test_an_unsatisfiable_token_is_unchecked(self):
        _c, gates = _gates(r"a\bb")
        self.assertEqual("UNCHECKED", gates[r"a\bb"].verdict)

    def test_the_lane_inserter_lands_before_the_quit_marker(self):
        model = mutlib.build_log_model(LOG)
        text = mutforbid.lane_inserter(model)(LOG, ["INJECTED"])
        lines = text.splitlines()
        self.assertEqual("INJECTED", lines[-2])
        self.assertIn("flushandquit", lines[-1])


class VacuityTests(unittest.TestCase):

    def test_an_evaluator_that_drops_the_forbidden_loop_is_vacuous(self):
        real = hlib.evaluate_expectations

        def no_forbidden(expectations, count, text):
            exp = dict(expectations)
            exp["logContracts"] = {}
            return real(exp, count, text)

        with Patched(hlib, "evaluate_expectations", no_forbidden):
            _c, gates = _gates("routecommand rejected")
        self.assertEqual("VACUOUS", gates["routecommand rejected"].verdict)

    def test_a_sampler_that_returns_nothing_is_unchecked_not_proven(self):
        with Patched(mutforbid, "sample_match", lambda p: None):
            _c, gates = _gates("routecommand rejected")
        self.assertEqual("UNCHECKED", gates["routecommand rejected"].verdict)


class EmitterTests(unittest.TestCase):

    def test_words_absent_from_the_source_are_listed(self):
        corpus = 'parseklog.info("routecommand", "rejected " + x)'.lower()
        self.assertEqual([], mutforbid.unemitted_words("routecommand rejected", corpus))
        self.assertEqual(["renamedmessage"],
                         mutforbid.unemitted_words("renamedMessage rejected", corpus))
        check, _g = _gates("renamedMessage rejected", corpus=corpus)
        self.assertEqual([("renamedMessage rejected", ["renamedmessage"])], check.unemitted)

    def test_ids_and_class_contents_are_not_words(self):
        self.assertEqual(["guid"], mutforbid.emitter_words(r"guid=90e4faaf-2029 [abcdefgh]+ x{1,9}"))
        self.assertEqual(["phaselock", "applied"], mutforbid.emitter_words("PhaseLock APPLIED"))


class LaneTests(unittest.TestCase):

    def test_forbidden_only_lane_and_report(self):
        lane = mutlib.check_forbidden_lane(_spec(r"^NeverAnywhere", "routecommand rejected"),
                                           "test", LOG)
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline)
        text = mutlib.render_report([lane], [])
        self.assertIn("## Vacuous forbidden tokens", text)
        self.assertIn("forbidden[^NeverAnywhere]", text)
        self.assertIn("forbiddenGates(proven=1 vacuous=1 unchecked=0)", mutlib.summary_line(lane))
        t = mutlib.sweep_totals([lane], 0)
        self.assertEqual((1, 1), (t.forbidden_proven, t.forbidden_vacuous))

    def test_a_log_that_already_matches_is_not_green(self):
        lane = mutlib.check_forbidden_lane(_spec("Parsek loaded"), "test", LOG)
        self.assertEqual(mutlib.BASELINE_NOT_GREEN, lane.baseline)

    def test_the_full_lane_adds_forbidden_gates(self):
        spec = {"id": "T", "expectations": {"logContracts": {
            "required": ["Parsek loaded"], "forbidden": ["routecommand rejected"]}}}
        lane = mutlib.check_lane(spec, mutlib.ArchiveInputs("t", LOG, None, None))
        self.assertEqual(["PROVEN"], [g.verdict for g in lane.forbidden_gates])
        self.assertIn("logContracts.forbidden", mutlib.spec_gating_surfaces(spec))


if __name__ == "__main__":
    unittest.main()
