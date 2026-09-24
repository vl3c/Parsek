"""Unit tests for mutlib (the report-only mutation checker, trust risk 8 phase 1).

Tiny synthetic specs and logs only: every case builds its own KSP.log text, so
nothing here depends on an archive being present on the machine.
"""

import os
import re
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import hlib  # noqa: E402
import mutlib  # noqa: E402
import saveparse  # noqa: E402
from test_saveparse import B9_MERGED_SFS  # noqa: E402


def _p(msg, level="INFO", tag="Test"):
    return "[LOG 12:00:00.000] [Parsek][%s][%s] %s" % (level, tag, msg)


def _exec(i, cmd):
    return _p("exec id=%04d cmd=%s start" % (i, cmd), tag="TestCommands")


QUIT = _p("flushandquit: Application.Quit", tag="TestCommands")


def _log(boot=(), run=(), teardown=()):
    lines = list(boot) + [_exec(1, "LoadGame")] + list(run) + [QUIT] + list(teardown)
    return "\n".join(lines) + "\n"


def _spec(required=(), forbidden=(), count=None, **extra):
    exp = {"logContracts": {"required": list(required), "forbidden": list(forbidden)}}
    if count is not None:
        exp["recordings"] = {"count": count}
    exp.update(extra)
    return {"id": "T-1", "expectations": exp}


def _inputs(text, count=None, snapshot=None):
    return mutlib.ArchiveInputs("synthetic", text, count, snapshot)


def _by_kind(lane, kind):
    return [m for m in lane.mutations if m.kind == kind]


class ChainSplitTests(unittest.TestCase):

    def test_single_line_pattern_is_one_whole_match_link(self):
        plan = mutlib.split_chain(r"Recording started: pid=[0-9]+")
        self.assertFalse(plan.is_chain)
        self.assertEqual(plan.link_groups, (0,))

    def test_lazy_gap_chain_renumbers_backreferences(self):
        pat = r"A pid=([0-9]+)[\s\S]*?B pid=\1 rec=([a-f]+)[\s\S]*?C rec=\2"
        plan = mutlib.split_chain(pat)
        self.assertEqual(len(plan.links), 3)
        text = "A pid=7\nnoise\nB pid=7 rec=ab\nC rec=ab\n"
        m0 = re.search(pat, text)
        m1 = re.search(plan.instrumented, text)
        self.assertEqual(m0.span(), m1.span())
        self.assertEqual(m1.group(plan.link_groups[1]), "B pid=7 rec=ab")
        self.assertEqual(m1.group(plan.link_groups[2]), "C rec=ab")

    def test_dotall_dot_star_is_a_gap_only_under_s(self):
        self.assertTrue(mutlib.split_chain(r"(?s)X.*Y.*Z").is_chain)
        self.assertFalse(mutlib.split_chain(r"X.*Y.*Z").is_chain)

    def test_adjacent_line_chain_splits_on_newline(self):
        plan = mutlib.split_chain(r"first ([0-9]+)\r?\n[^\r\n]*second (?!\1 )[0-9]+")
        self.assertEqual(len(plan.links), 2)

    def test_top_level_alternation_is_not_decomposed(self):
        plan = mutlib.split_chain(r"A[\s\S]*?B|C")
        self.assertFalse(plan.is_chain)
        self.assertEqual(plan.reason, "top-level alternation")


class LogModelTests(unittest.TestCase):

    def test_phases_follow_the_first_exec_and_the_quit_marker(self):
        model = mutlib.build_log_model(_log(boot=["boot line"], run=["run line"],
                                            teardown=["late line"]))
        self.assertEqual(model.phase_of(0), mutlib.PHASE_BOOT)
        self.assertEqual(model.phase_of(2), mutlib.PHASE_RUN)
        self.assertEqual(model.phase_of(4), mutlib.PHASE_TEARDOWN)
        self.assertEqual(model.steps[2], "0001")

    def test_join_is_byte_identical(self):
        text = "a\r\nb\nc"
        self.assertEqual(mutlib.build_log_model(text).text, text)


class KilledMutationTests(unittest.TestCase):

    def test_deleting_the_matched_line_kills_the_required_pattern(self):
        text = _log(run=[_p("Recording started: vessel=Kerbal X")])
        lane = mutlib.check_lane(_spec(required=[r"Recording started: vessel=Kerbal X"]),
                                 _inputs(text))
        self.assertEqual(lane.baseline, mutlib.BASELINE_GREEN)
        [m] = _by_kind(lane, "delete-all")
        self.assertEqual(m.outcome, mutlib.KILLED)
        self.assertEqual(m.triage, "")


class TooBroadPatternTests(unittest.TestCase):

    def test_a_pattern_boot_alone_satisfies_survives_phase_isolation(self):
        text = _log(boot=[_p("Parsek settings loaded")], run=[_p("Parsek settings loaded")])
        lane = mutlib.check_lane(_spec(required=[r"settings loaded"]), _inputs(text))
        [facts] = lane.patterns
        self.assertTrue(facts.multi_phase)
        self.assertEqual(facts.phases, (mutlib.PHASE_BOOT, mutlib.PHASE_RUN))
        [iso] = _by_kind(lane, "phase-isolate")
        self.assertEqual(iso.outcome, mutlib.SURVIVED)
        self.assertEqual(iso.triage, mutlib.TRIAGE)
        self.assertIn("boot", iso.detail)
        # The same line in the run phase only is not flagged.
        lane2 = mutlib.check_lane(_spec(required=[r"settings loaded"]),
                                  _inputs(_log(run=[_p("Parsek settings loaded")])))
        self.assertFalse(lane2.patterns[0].multi_phase)
        self.assertEqual(_by_kind(lane2, "phase-isolate"), [])

    def test_every_instance_of_a_chain_is_peeled(self):
        # Two boosters each complete the chain; the first lazy match spans the
        # whole flight, so finditer alone would see one instance and every
        # mutation of it would "survive" on the second booster.
        pat = r"TTL set pid=([0-9]+)[\s\S]*?TTL expired pid=\1[\s\S]*?Finalize pid=\1 points=[1-9][0-9]*"
        run = [_p("TTL set pid=11"), _p("TTL set pid=22"), _p("TTL expired pid=22"),
               _p("TTL expired pid=11"), _p("Finalize pid=11 points=40"),
               _p("Finalize pid=22 points=35")]
        lane = mutlib.check_lane(_spec(required=[pat]), _inputs(_log(run=run)))
        self.assertEqual(lane.patterns[0].matches, 2)
        self.assertEqual(lane.patterns[0].chain_links, 3)
        drops = {m.detail.split(" (")[0]: m for m in _by_kind(lane, "drop-link")}
        self.assertEqual(sorted(drops), ["link 1/3", "link 2/3", "link 3/3"])
        self.assertIn("middle", drops["link 2/3"].detail)
        self.assertTrue(all(m.outcome == mutlib.KILLED for m in drops.values()))
        [delete_all] = _by_kind(lane, "delete-all")
        self.assertEqual(delete_all.outcome, mutlib.KILLED)
        zero = [m for m in _by_kind(lane, "numeric")
                if m.detail.startswith("points=") and "zero" in m.detail]
        self.assertEqual([(m.outcome, "2 site(s)" in m.detail) for m in zero],
                         [(mutlib.KILLED, True)])


class BaselineTests(unittest.TestCase):

    def test_a_non_green_baseline_is_skipped_with_its_reason(self):
        lane = mutlib.check_lane(_spec(required=[r"never printed"]),
                                 _inputs(_log(run=[_p("something else")])))
        self.assertEqual(lane.baseline, mutlib.BASELINE_NOT_GREEN)
        self.assertEqual(lane.mutations, [])
        self.assertTrue(any("never printed" in r for r in lane.baseline_reasons))
        self.assertIn("baseline not green", mutlib.summary_line(lane))

    def test_a_forbidden_hit_or_count_miss_is_not_green(self):
        text = _log(run=[_p("boom", level="ERROR")])
        self.assertEqual(mutlib.check_lane(_spec(forbidden=[r"\[Parsek\]\[ERROR\]"]),
                                           _inputs(text)).baseline, mutlib.BASELINE_NOT_GREEN)
        self.assertEqual(mutlib.check_lane(_spec(count={"min": 2}), _inputs(text, 1)).baseline,
                         mutlib.BASELINE_NOT_GREEN)


class NumericPerturbationTests(unittest.TestCase):

    def _numeric(self, lane, label):
        return [m for m in _by_kind(lane, "numeric") if m.detail.startswith(label + "=")]

    def test_a_pinned_value_is_killed_and_a_free_one_survives_as_info(self):
        text = _log(run=[_p("BATCH_COMPLETE v1 total=12 passed=12 failed=0 ut=1234.5")])
        lane = mutlib.check_lane(_spec(required=[r"BATCH_COMPLETE v1 total=12 passed=[0-9]+ failed=0 "
                                                 r"ut=[0-9.]+"]), _inputs(text))
        self.assertTrue(all(m.outcome == mutlib.KILLED for m in self._numeric(lane, "total")))
        self.assertTrue(all(m.outcome == mutlib.KILLED for m in self._numeric(lane, "failed")))
        ut = self._numeric(lane, "ut")
        self.assertTrue(ut and all(m.outcome == mutlib.SURVIVED and m.triage == mutlib.INFO
                                   for m in ut))
        # passed=[0-9]+ admits zero: a count-shaped field dropping to zero is triage.
        zero = [m for m in self._numeric(lane, "passed") if "zero" in m.detail]
        self.assertEqual([m.triage for m in zero], [mutlib.TRIAGE])

    def test_an_unpinned_failure_field_moving_off_zero_is_triage(self):
        text = _log(run=[_p("verify tally failed=0 skipped=0")])
        lane = mutlib.check_lane(_spec(required=[r"verify tally failed=[0-9]+ skipped=0"]),
                                 _inputs(text))
        [plus] = [m for m in self._numeric(lane, "failed") if "plus1" in m.detail]
        self.assertEqual((plus.outcome, plus.triage), (mutlib.SURVIVED, mutlib.TRIAGE))
        self.assertTrue(all(m.outcome == mutlib.KILLED for m in self._numeric(lane, "skipped")))

    def test_a_local_fixture_lane_numeric_survivor_is_intended(self):
        spec = _spec(required=[r"expand total=[0-9]+"])
        spec["fixture"] = {"saveTemplate": "fixtures/local-saves/c1-gui"}
        lane = mutlib.check_lane(spec, _inputs(_log(run=[_p("expand total=12")])))
        [zero] = [m for m in self._numeric(lane, "total") if "zero" in m.detail]
        self.assertEqual((zero.outcome, zero.triage), (mutlib.SURVIVED, mutlib.INTENDED))

    def test_every_copy_of_a_repeated_line_moves_together(self):
        text = _log(run=[_p("points=5"), _p("points=5")])
        lane = mutlib.check_lane(_spec(required=[r"points=5"]), _inputs(text))
        [plus] = [m for m in self._numeric(lane, "points") if "plus1" in m.detail]
        self.assertEqual(plus.outcome, mutlib.KILLED)
        self.assertIn("2 site(s)", plus.detail)

    def test_perturb_number_keeps_width_and_shape(self):
        self.assertEqual(mutlib.perturb_number("0007"), [("plus1", "0008"), ("zero", "0000")])
        self.assertEqual(mutlib.perturb_number("0"), [("plus1", "1")])
        self.assertEqual(mutlib.perturb_number("12.50"), [("plus1", "13.50"), ("zero", "0.00")])


class NumericClassificationTests(unittest.TestCase):
    """Identifier-shaped fields are free; every other field moving between zero and
    nonzero while the gate passes is triage."""

    FREE_FIELD_RE = re.compile(
        r"([A-Za-z_][\w.]*)=(?:\(\?:)?(?:\[0-9\]\+|\[0-9\.\]\+|\\d\+|\[1-9\]\[0-9\]\*|"
        r"\[0-9\]\*|\\S\+|\[-0-9\.\]\+|\[0-9\.\-\]\+|\[-0-9\.eE\+\]\+)")
    IDENTIFIERS = {"pid", "id", "index", "rec", "slot", "dist", "distance", "ut", "UT",
                   "currentUT", "expiryUT", "recId", "ghostIndex", "focusedPid", "midx",
                   "anchorDist", "originRoot", "focusSlot", "#", "#Mk1", "#b63f", "frame"}
    GAPS_THE_OLD_LIST_HID = {"overTolerance", "outsideSoi", "frames", "sampled", "evaluated",
                             "matches", "delivered", "created", "debris", "vessels", "parts",
                             "links", "steps"}

    def _committed_free_labels(self):
        import glob
        import tomllib
        labels = set()
        for path in glob.glob(os.path.join(os.path.dirname(_HERE), "scenarios", "*.toml")):
            with open(path, "rb") as fh:
                spec = tomllib.load(fh)
            lc = (spec.get("expectations") or {}).get("logContracts") or {}
            for pat in lc.get("required", []) or []:
                labels.update(m.group(1) for m in self.FREE_FIELD_RE.finditer(str(pat)))
        return labels

    def test_every_committed_free_field_label_classifies(self):
        labels = self._committed_free_labels()
        self.assertGreater(len(labels), 50)
        for label in sorted(labels | self.IDENTIFIERS | self.GAPS_THE_OLD_LIST_HID):
            ident = mutlib.is_identifier_label(label)
            off_zero = mutlib.classify_numeric_survivor(label, "plus1", ["0"])[0]
            to_zero = mutlib.classify_numeric_survivor(label, "zero", ["7"])[0]
            magnitude = mutlib.classify_numeric_survivor(label, "plus1", ["7"])[0]
            self.assertEqual(magnitude, mutlib.INFO, label)
            if ident:
                self.assertEqual((off_zero, to_zero), (mutlib.INFO, mutlib.INFO), label)
            else:
                self.assertEqual((off_zero, to_zero), (mutlib.TRIAGE, mutlib.TRIAGE), label)
        for label in self.IDENTIFIERS:
            self.assertTrue(mutlib.is_identifier_label(label), label)
        for label in self.GAPS_THE_OLD_LIST_HID:
            self.assertFalse(mutlib.is_identifier_label(label), label)
        # Names that merely END in an identifier-looking run stay countable.
        for label in ("invalid", "valid", "paid"):
            self.assertFalse(mutlib.is_identifier_label(label), label)


class CountAndSideVerifierTests(unittest.TestCase):

    def test_a_missing_archived_save_is_noted(self):
        lane = mutlib.check_lane(_spec(count={"min": 1}), _inputs(_log(run=[_p("x")]), None))
        self.assertEqual(lane.baseline, mutlib.BASELINE_GREEN)
        self.assertTrue(any("recordings.count" in n and "no archived save" in n
                            for n in lane.notes), lane.notes)
        self.assertEqual(_by_kind(lane, "count"), [])

    def test_a_count_window_admitting_zero_is_triage(self):
        text = _log(run=[_p("x")])
        lane = mutlib.check_lane(_spec(count={"min": 0, "max": 5}), _inputs(text, 2))
        zero = [m for m in _by_kind(lane, "count") if "(zero)" in m.detail]
        self.assertEqual([(m.outcome, m.triage) for m in zero],
                         [(mutlib.SURVIVED, mutlib.TRIAGE)])
        tight = mutlib.check_lane(_spec(count={"min": 2, "max": 2}), _inputs(text, 2))
        self.assertTrue(all(m.outcome == mutlib.KILLED for m in _by_kind(tight, "count")))

    def test_unity_injection_kills_an_armed_parsek_frame_ceiling(self):
        text = _log(run=[_p("x")])
        lane = mutlib.check_lane(_spec(unityExceptions={"maxParsekFrames": 0}), _inputs(text))
        kinds = {m.kind: m for m in lane.mutations if m.verifier == "unityExceptions"}
        self.assertEqual(kinds["inject-parsek-throw-site"].outcome, mutlib.KILLED)
        self.assertEqual(kinds["inject-parsek-caller"].outcome, mutlib.KILLED)

    def test_caller_shape_survivor_is_intended_under_a_throw_site_ceiling(self):
        text = _log(run=[_p("x")])
        lane = mutlib.check_lane(_spec(unityExceptions={"maxParsekThrowSite": 0}), _inputs(text))
        kinds = {m.kind: m for m in lane.mutations if m.verifier == "unityExceptions"}
        self.assertEqual(kinds["inject-parsek-throw-site"].outcome, mutlib.KILLED)
        self.assertEqual((kinds["inject-parsek-caller"].outcome, kinds["inject-parsek-caller"].triage),
                         (mutlib.SURVIVED, mutlib.INTENDED))
        # A count-only budget with headroom lets a Parsek throw site through: triage.
        loose = mutlib.check_lane(_spec(unityExceptions={"maxTotal": 5}), _inputs(text))
        ts = [m for m in loose.mutations if m.kind == "inject-parsek-throw-site"][0]
        self.assertEqual((ts.outcome, ts.triage), (mutlib.SURVIVED, mutlib.TRIAGE))

    def test_unity_injection_scans_as_the_run_would(self):
        model = mutlib.build_log_model(_log(run=[_p("x")]))
        text = mutlib.text_with_inserted(model, mutlib.insertion_index(model),
                                         mutlib.PARSEK_THROW_SITE_BLOCK)
        scan = hlib.scan_unity_exception_stacks(text)
        self.assertEqual((scan.parsek_frames, scan.parsek_throw_site, scan.after_quit), (1, 1, 0))

    def test_an_allowed_anomaly_survives_as_intended(self):
        tok = hlib.ANOMALY_TOKENS[0]
        lane = mutlib.check_lane(_spec(allowedAnomalies=[tok]), _inputs(_log(run=[_p("x")])))
        anomalies = {m.target: m for m in lane.mutations if m.verifier == "anomalySweep"}
        self.assertEqual((anomalies[tok].outcome, anomalies[tok].triage),
                         (mutlib.SURVIVED, mutlib.INTENDED))
        other = hlib.ANOMALY_TOKENS[1]
        self.assertEqual(anomalies[other].outcome, mutlib.KILLED)


class SaveWindowTests(unittest.TestCase):

    def setUp(self):
        self.snapshot = saveparse.parse_parsek_scenario(B9_MERGED_SFS)
        self.assertTrue(self.snapshot.parsed and self.snapshot.scenario_found)

    def test_window_walk_matches_the_evaluator_labels(self):
        facets = saveparse.observed_structure_facets(self.snapshot)
        exp = {"rewind": {"gating": True, "supersedeRows": 0, "tombstones": 0, "rewindPoints": 0},
               "recordings": {"structure": {"gating": True, "trees": 0, "recordings": 0,
                                            "terminalStates": {"Orbiting": 0},
                                            "branchPoints": {"Undock": 0},
                                            "vesselNames": {"B9 Stack": 0}},
                              "points": {"gating": True, "total": 0}},
               "routes": {"gating": True, "count": 0, "stops": 0,
                          "statuses": {"Active": 0}, "connectionKinds": {"None": 0},
                          "originBodies": {"Kerbin": 0}, "destinationBodies": {"Mun": 0}}}
        walked = mutlib.save_windows(exp, self.snapshot)
        # Pin every walked window to measured+1: the evaluator must name each label.
        for label, _w, measured, _a in walked:
            block, _, key = label.rpartition(".")
            node = exp
            for part in block.split("."):
                node = node[part]
            node[key] = measured + 1
        sp = saveparse.evaluate_save_structure(exp, self.snapshot)
        # Group labels can carry spaces (a vessel name), so compare by prefix.
        walked_labels = {label for label, _w, _m, _a in walked}
        for label in walked_labels:
            self.assertTrue(any(m.startswith(label + " ") for m in sp.mismatches), label)
        self.assertEqual(len(sp.mismatches), len(walked_labels))
        for label in ("recordings.structure.branchPoints.Undock",
                      "recordings.structure.vesselNames.B9 Stack", "routes.count",
                      "routes.stops", "routes.statuses.Active", "routes.connectionKinds.None",
                      "routes.originBodies.Kerbin", "routes.destinationBodies.Mun"):
            self.assertIn(label, walked_labels)
        self.assertEqual(facets["recordings"]["structure"]["trees"],
                         dict((l, m) for l, _w, m, _a in walked)["recordings.structure.trees"])

    def test_a_wide_armed_window_survives_zero_and_a_pin_kills(self):
        trees = saveparse.observed_structure_facets(self.snapshot)["recordings"]["structure"]["trees"]
        wide = {"recordings": {"structure": {"gating": True, "trees": {"min": 0, "max": 99}}}}
        muts = mutlib.mutate_save_windows(wide, self.snapshot)
        zero = [m for m in muts if "(zero)" in m.detail]
        self.assertEqual([(m.outcome, m.triage) for m in zero], [(mutlib.SURVIVED, mutlib.TRIAGE)])
        pinned = {"recordings": {"structure": {"gating": True, "trees": trees}}}
        self.assertTrue(all(m.outcome == mutlib.KILLED
                            for m in mutlib.mutate_save_windows(pinned, self.snapshot)))
        report_only = {"recordings": {"structure": {"trees": {"min": 0}}}}
        self.assertEqual(mutlib.mutate_save_windows(report_only, self.snapshot), [])


class LaneEvaluatorTests(unittest.TestCase):
    """The incremental evaluator must answer exactly what the real
    hlib.evaluate_expectations answers over the full mutated text."""

    def test_line_locality_is_conservative(self):
        self.assertTrue(mutlib.is_line_local(r"Recording started: pid=[0-9]+ \(x\)"))
        self.assertTrue(mutlib.is_line_local(r"digest=([0-9a-f]{8}) expected=\1"))
        for pat in (r"a\s+b", r"a[^x]b", r"(?s)a.b", r"^a", r"a$", r"a[\s\S]*?b",
                    r"(?<=x)a", r"a\nb", r"a\Db", "a\nb", "a\rb", "done count=3\n"):
            self.assertFalse(mutlib.is_line_local(pat), pat)

    def test_shortcut_agrees_with_the_real_evaluator(self):
        import random
        rng = random.Random(7)
        run = []
        for i in range(40):
            run.append(_p("Recording started: vessel=V%d points=%d failed=0" % (i % 3, i)))
            run.append(_p("tick ut=%d.5" % i))
        run += [_p("Chain A pid=4"), _p("filler"), _p("Chain B pid=4"), _p("done count=3")]
        spec = _spec(required=[r"Recording started: vessel=V2 points=[0-9]+ failed=0",
                               r"Chain A pid=([0-9]+)[\s\S]*?Chain B pid=\1",
                               r"done\s+count=[1-9]",
                               # a lookahead reading past the matched text
                               r"vessel=V1(?= points=[0-9]*7 )",
                               # a LITERAL line break in the pattern text
                               "Chain B pid=4\n",
                               # a hit set capped below its real size (see below)
                               r"tick ut=[0-9]+\.5"],
                     forbidden=[r"failed=[1-9]", r"count=0\b", r"ut=40\.5[\s\S]*?done"])
        text = _log(run=run)
        model = mutlib.build_log_model(text)
        exp = spec["expectations"]
        saved_cap = mutlib.MAX_MATCHES_PER_PATTERN
        mutlib.MAX_MATCHES_PER_PATTERN = 5
        try:
            hits = {}
            ev = mutlib.LaneEvaluator(exp, None, model, hits)
        finally:
            mutlib.MAX_MATCHES_PER_PATTERN = saved_cap
        self.assertTrue(hits[r"tick ut=[0-9]+\.5"].truncated)
        self.assertFalse(hits["Chain B pid=4\n"].line_local)
        self.assertEqual(ev.confirm((), {}), mutlib.SURVIVED)   # the baseline is green
        numbers = [(i, m.start(), m.end(), m.group(0)) for i, ln in enumerate(model.lines)
                   for m in mutlib.NUMBER_TOKEN_RE.finditer(ln)]
        disagreements = []
        # Scripted cases first: a multi-line forbidden pattern created by one edit,
        # and a non-local required pattern whose only match loses its line.
        ut39 = next(t for t in numbers if t[3] == "39.5")
        done = next(i for i, ln in enumerate(model.lines) if "done count" in ln)
        for deleted, edits in (((), {ut39[0]: [(ut39[1], ut39[2], "40.5")]}),
                               ({done}, {})):
            self.assertEqual(ev.outcome(deleted, edits), mutlib.KILLED)
            self.assertEqual(ev.confirm(deleted, edits), mutlib.KILLED)
        for trial in range(300):
            deleted = set(rng.sample(range(len(model.lines)), rng.randint(0, 6)))
            edits = {}
            for li, s, e, tok in rng.sample(numbers, rng.randint(0, 4)):
                edits.setdefault(li, []).append((s, e, rng.choice(mutlib.perturb_number(tok))[1]))
            if trial % 3 == 0:
                deleted = set()
            got = ev.outcome(deleted, edits)
            real = ev.confirm(deleted, edits)
            if got != real:
                disagreements.append((sorted(deleted), edits, got, real))
        self.assertEqual(disagreements, [])

    def test_anomaly_shortcut_matches_a_text_replay(self):
        model = mutlib.build_log_model(_log(run=[_p("x")]))
        spec = _spec(allowedAnomalies=[hlib.ANOMALY_TOKENS[2]])
        exp = spec["expectations"]
        by_tok = {m.target: m.outcome for m in mutlib.mutate_anomaly_sweep(exp, model)}
        for tok in hlib.ANOMALY_TOKENS:
            text = mutlib.text_with_inserted(model, mutlib.insertion_index(model),
                                             [mutlib.anomaly_injection_line(tok)])
            unallowed = hlib.evaluate_anomaly_sweep(
                hlib.grep_anomaly_tokens(text), exp["allowedAnomalies"],
                hlib.count_anomaly_tokens(text))
            self.assertEqual(by_tok[tok], mutlib.KILLED if tok in unallowed else mutlib.SURVIVED)


class ArchiveSelectionTests(unittest.TestCase):

    def test_stamped_names_and_newest_first_ordering(self):
        self.assertEqual(mutlib.parse_stamped_name("2026-09-24_0041_SD-1-same-tree-redock"),
                         ("2026-09-24_0041", "SD-1-same-tree-redock"))
        self.assertIsNone(mutlib.parse_stamped_name("wave-0910"))

        def ref(stamp, source, verdict=None, run=None):
            return mutlib.ArchiveRef(stamp, "S", source, run or stamp + "_S", "k", None, None, verdict)
        refs = [ref("2026-09-01_1000", "results", "PASS"), ref("2026-09-02_1000", "collect"),
                ref("2026-09-02_1000", "results", "PARSEK-FAIL"),
                ref("2026-09-01_1000", "results", "PASS")]
        got = [(r.stamp, r.source) for r in mutlib.order_candidates(refs, "S")]
        self.assertEqual(got, [("2026-09-02_1000", "results"), ("2026-09-02_1000", "collect"),
                               ("2026-09-01_1000", "results")])

    def test_expected_fail_needs_a_bug_id(self):
        self.assertFalse(mutlib.is_expected_fail_lane({"expectedFail": {"bugId": ""}}))
        self.assertTrue(mutlib.is_expected_fail_lane({"expectedFail": {"bugId": "X-1"}}))

    def test_report_lists_triage_survivors(self):
        text = _log(boot=[_p("settings loaded")], run=[_p("settings loaded")])
        lane = mutlib.check_lane(_spec(required=[r"settings loaded"]), _inputs(text))
        report = mutlib.render_report([lane], ["NO-ARCHIVE-1"])
        self.assertIn("## Survivors needing triage", report)
        self.assertIn("phase-isolate", report)
        self.assertIn("No archive: NO-ARCHIVE-1", report)


if __name__ == "__main__":
    unittest.main()
