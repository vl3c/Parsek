"""Campaign chains: lib/campaign.py (pure), run.py's --stage-from, the committed
ladders, and tools/campaign_runner.py end to end over a fake run.py child.

The shell smoke (``RunnerSmokeTests``) runs the real runner against a temp
harness: real ``parallel_runner`` machinery, the real ``harvest_bdock_station``
harvest, and a child (``_fake_campaign_child.py``) that applies run.py's own
``--stage-from`` refusal and ``stage_fixture`` before it "flies". So the
handover is proven on the files a real chain moves: a stage's produced-save
snapshot, its harvest into the campaign folder, and the next stage's staged save.
"""

import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import tomllib
import unittest
import uuid
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
TOOLS = os.path.join(HARNESS_ROOT, "tools")
PROVISION = os.path.join(HARNESS_ROOT, "provision")
for _p in (HERE, HARNESS_ROOT, TOOLS, PROVISION):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import campaign as C  # noqa: E402
import campaign_runner as cr  # noqa: E402
import parallel  # noqa: E402
import parallel_runner as pr  # noqa: E402
import run  # noqa: E402
import slotrun  # noqa: E402
from test_parallel import FakeBridge, FakePrefs  # noqa: E402

FAKE_CHILD = os.path.join(HERE, "_fake_campaign_child.py")


def _spec(sid, leaf, profile="stock-tree", craft=None):
    spec = {"id": sid, "tier": "daily", "instanceProfile": profile,
            "fixture": {"saveTemplate": "fixtures/saves/" + leaf},
            "driver": {"kind": "seam", "steps": []}}
    if craft:
        spec["driver"]["steps"] = [{"cmd": "GoToEditor", "args": {"craft": c}} for c in craft]
    return spec


SPECS = {s["id"]: s for s in (
    _spec("A", "la"), _spec("B", "lb"), _spec("C", "lc"),
    _spec("X", "lx"), _spec("Y", "ly"))}


def _ladder(*chains, **top):
    obj = {"schema": 1, "name": "t", "chain": [
        {"name": n, "stage": stages} for n, stages in chains]}
    obj.update(top)
    return obj


def _line():
    return C.parse_ladder(_ladder(("main", [{"id": "A", "label": "L-A"}, {"id": "B"},
                                            {"id": "C"}])), SPECS)


def _two():
    return C.parse_ladder(_ladder(
        ("main", [{"id": "A"}, {"id": "B"}, {"id": "C"}]),
        ("side", [{"id": "X"}, {"id": "Y", "after": ["A"]}])), SPECS)


def _green(sid, leaf="out"):
    return C.StageRecord(sid, "PASS", 1, ("run-" + sid,), C.HANDOVER_OK, "", "fixture", leaf)


def _red(sid, verdict="PARSEK-FAIL"):
    return C.StageRecord(sid, verdict, 2, ("run-%s-1" % sid, "run-%s-2" % sid))


# ---------------------------------------------------------------------------
# The ladder grammar
# ---------------------------------------------------------------------------


class LadderParseTests(unittest.TestCase):

    def test_a_valid_ladder(self):
        ladder = C.parse_ladder(_ladder(
            ("main", [{"id": "A", "label": "L-A"},
                      {"id": "B", "handover": "fixture", "reason": "random offers"},
                      {"id": "C", "carryCraft": ["Ship One"],
                       "harvest": {"keepParsek": False, "expectSituation": "ORBITING"}}]),
            harvest={"keepParsek": True, "expectMode": "career"}), SPECS)
        a, b, c = ladder.stages()
        self.assertEqual((a.handover, a.reason, a.template_leaf, a.profile, a.ref),
                         (C.HANDOVER_FIXTURE, "chain head", "la", "stock-tree", "L-A"))
        self.assertEqual((b.handover, b.reason), (C.HANDOVER_FIXTURE, "random offers"))
        self.assertEqual((c.handover, c.reason, c.carry_craft),
                         (C.HANDOVER_CHAIN, "", ("Ship One",)))
        self.assertEqual(a.harvest.argv(), ["--keep-parsek", "--expect-mode", "CAREER"])
        self.assertEqual(c.harvest.argv(), ["--expect-mode", "CAREER",
                                            "--expect-situation", "ORBITING"])
        self.assertEqual((a.dirname, c.dirname), ("00-A", "02-C"))

    def assertRefused(self, obj, *fragments, specs=SPECS):
        with self.assertRaises(C.LadderError) as cm:
            C.parse_ladder(obj, specs)
        for f in fragments:
            self.assertIn(f, str(cm.exception))

    def test_refusals_name_the_problem(self):
        cases = [
            (_ladder(("m", [{"id": "A"}]), schema=2), "schema must be 1"),
            (_ladder(("m", [{"id": "A"}]), bogus=1), "unknown ladder key 'bogus'"),
            (_ladder(("m", [{"id": "A", "nope": 1}])), "unknown key 'nope'"),
            (_ladder(("m", [{"id": "Z"}])), "no committed spec"),
            (_ladder(("m", [{"id": "A"}, {"id": "A"}])), "flies once per campaign"),
            (_ladder(("m", [{"id": "A", "handover": "chain"}])), "declares no handover"),
            (_ladder(("m", [{"id": "A"}, {"id": "B", "handover": "fixture"}])),
             "names the reason"),
            (_ladder(("m", [{"id": "A"}, {"id": "B", "reason": "x"}])), "takes no reason"),
            (_ladder(("m", [{"id": "A"}, {"id": "B", "handover": "teleport"}])),
             "handover 'teleport'"),
            (_ladder(("m", [{"id": "A"}, {"id": "B", "handover": "fixture", "reason": "r",
                                          "carryCraft": ["S"]}])),
             "carryCraft applies only to a chain handover"),
            (_ladder(("m", [{"id": "A"}, {"id": "B", "carryCraft": ["../evil"]}])),
             "not filename-safe"),
            (_ladder(("m", [{"id": "A"}, {"id": "B", "after": ["A"]}])), "of its own chain"),
            (_ladder(("m", [{"id": "A", "after": ["Q"]}])), "after names 'Q'"),
            (_ladder(("m", [{"id": "A", "label": "B"}])), "label 'B' is not unique"),
            (_ladder(("m", [{"id": "A", "harvest": {"expectMode": "MOON"}}])),
             "expectMode must be one of"),
            (_ladder(("m", [{"id": "A"}]), ("m", [{"id": "B"}])), "declared twice"),
            ({"schema": 1, "name": "t"}, "at least one [[chain]]"),
        ]
        for obj, frag in cases:
            with self.subTest(frag=frag):
                self.assertRefused(obj, frag)

    def test_a_template_outside_the_committed_fixtures_is_refused(self):
        specs = dict(SPECS, L=_spec("L", "x"))
        specs["L"]["fixture"]["saveTemplate"] = "fixtures/local-saves/x"
        self.assertRefused(_ladder(("m", [{"id": "L"}])), "not a committed fixture",
                           specs=specs)

    def test_an_after_cycle_across_chains_is_refused(self):
        self.assertRefused(_ladder(("a", [{"id": "A"}, {"id": "B", "after": ["Y"]}]),
                                   ("x", [{"id": "X", "after": ["B"]}, {"id": "Y"}])),
                           "form a cycle")

    def test_every_problem_is_named_at_once(self):
        self.assertRefused(_ladder(("m", [{"id": "Z"}, {"id": "A", "nope": 1}]), schema=3),
                           "schema must be 1", "no committed spec", "unknown key 'nope'")


class CraftReachTests(unittest.TestCase):

    def setUp(self):
        specs = dict(SPECS, B=_spec("B", "lb", craft=["Lander"]),
                     C=_spec("C", "lc", craft=["Flea", "Orbiter"]))
        self.ladder = C.parse_ladder(_ladder(("m", [
            {"id": "A"}, {"id": "B", "carryCraft": ["Lander"]}, {"id": "C"}])), specs)
        self.crafts = {s: C.spec_craft_names(specs[s]) for s in ("A", "B", "C")}

    def test_spec_craft_names_walk_the_driver(self):
        self.assertEqual(self.crafts["C"], {"Flea", "Orbiter"})
        mission = {"driver": {"kind": "mission", "missionParams": {"craft": "Kerbal X"}}}
        self.assertEqual(C.spec_craft_names(mission), {"Kerbal X"})

    def test_carried_overlaid_and_head_craft_reach(self):
        committed = {"la": {"Orbiter"}, "lb": {"Lander"}, "lc": set()}
        self.assertEqual([], C.craft_reach_problems(self.ladder, self.crafts,
                                                    {"lc": ("Flea",)}, committed))

    def test_a_craft_no_save_carries_is_named(self):
        committed = {"la": set(), "lb": {"Lander"}, "lc": {"Orbiter"}}
        problems = C.craft_reach_problems(self.ladder, self.crafts, {}, committed)
        self.assertEqual(2, len(problems), problems)
        self.assertTrue(any("C: launches 'Flea'" in p and "chained" in p for p in problems))
        self.assertTrue(any("C: launches 'Orbiter'" in p for p in problems), problems)

    def test_a_carry_craft_missing_from_the_fixture_is_named(self):
        problems = C.craft_reach_problems(self.ladder, self.crafts, {"lc": ("Flea",)},
                                          {"la": {"Orbiter"}, "lb": set()})
        self.assertTrue(any("carryCraft 'Lander' is not in fixtures/saves/lb" in p
                            for p in problems), problems)


# ---------------------------------------------------------------------------
# Range, readiness, verdicts
# ---------------------------------------------------------------------------


class SelectRangeTests(unittest.TestCase):

    def test_no_range_is_the_whole_ladder(self):
        ladder = _two()
        self.assertIs(C.select_range(ladder, None, None), ladder)

    def test_a_range_head_stages_its_committed_fixture(self):
        ladder = C.parse_ladder(_ladder(("main", [
            {"id": "A"}, {"id": "B", "carryCraft": ["S"]}, {"id": "C"}])), SPECS)
        picked = C.select_range(ladder, "B", "C")
        b, c = picked.stages()
        self.assertEqual((b.handover, b.reason, b.carry_craft),
                         (C.HANDOVER_FIXTURE, "range head", ()))
        self.assertEqual(c.handover, C.HANDOVER_CHAIN)
        self.assertEqual([s.spec_id for s in C.select_range(ladder, None, "B").stages()],
                         ["A", "B"])

    def test_labels_resolve(self):
        self.assertEqual([s.spec_id for s in C.select_range(_line(), "L-A", "B").stages()],
                         ["A", "B"])

    def test_an_after_outside_the_range_is_dropped(self):
        picked = C.select_range(_two(), "Y", "Y")
        self.assertEqual(picked.stages()[0].after, ())

    def test_bad_ranges_refused(self):
        for a, b, frag in (("C", "A", "comes after"), ("A", "Y", "different chains"),
                           ("Q", None, "named Q")):
            with self.subTest(frag=frag), self.assertRaises(C.LadderError) as cm:
                C.select_range(_two(), a, b)
            self.assertIn(frag, str(cm.exception))


class ReadinessTests(unittest.TestCase):

    def ids(self, ladder, records, running=(), failed=()):
        return [s.spec_id for s in C.ready_stages(ladder, records, running, failed)]

    def test_chain_heads_start_and_after_waits(self):
        self.assertEqual(self.ids(_two(), {}), ["A", "X"])
        self.assertEqual(self.ids(_two(), {"X": _green("X")}), ["A"])  # Y waits on A
        self.assertEqual(self.ids(_two(), {"X": _green("X"), "A": _green("A")}), ["B", "Y"])

    def test_a_running_stage_holds_its_chain(self):
        self.assertEqual(self.ids(_line(), {"A": _green("A")}, running=["B"]), [])

    def test_a_stage_red_in_this_invocation_holds_its_chain(self):
        recs = {"A": _green("A"), "B": _red("B")}
        self.assertEqual(self.ids(_line(), recs, failed=["B"]), [])

    def test_resume_point_is_the_stage_that_broke(self):
        recs = {"A": _green("A"), "B": _red("B")}
        self.assertEqual(self.ids(_line(), recs), ["B"])
        recs["C"] = _green("C")  # a stale later record never lets C run before B
        self.assertEqual(self.ids(_line(), recs), ["B"])

    def test_a_green_verdict_with_a_failed_handover_is_not_green(self):
        rec = C.StageRecord("A", "PASS", 1, ("r",), C.HANDOVER_FAILED, "no snapshot")
        self.assertFalse(rec.green)
        self.assertEqual(self.ids(_line(), {"A": rec}), ["A"])

    def test_stage_source(self):
        ladder = _line()
        self.assertEqual(C.stage_source(ladder, ladder.stage("A"), {}), ("fixture", None))
        self.assertEqual(C.stage_source(ladder, ladder.stage("B"), {}), ("chain", "A"))

    def test_harvest_leaf_is_the_next_stage_template(self):
        ladder = _line()
        self.assertEqual(C.harvest_leaf(ladder, ladder.stage("A")), "lb")
        self.assertEqual(C.harvest_leaf(ladder, ladder.stage("C")), "campaign-C")

    def test_record_round_trip(self):
        rec = C.StageRecord("B", "PASS", 2, ("r1", "r2"), C.HANDOVER_OK, "", "A", "lc")
        self.assertEqual(C.StageRecord.from_json(json.loads(json.dumps(rec.to_json()))), rec)
        self.assertIsNone(C.StageRecord.from_json({"schema": "other"}))


class OutcomeTests(unittest.TestCase):

    def test_a_middle_stage_failure_names_the_stage_and_what_did_not_fly(self):
        recs = {"A": _green("A"), "B": _red("B")}
        outcome, code, detail = C.campaign_outcome(_line(), recs, stopped=False)
        self.assertEqual((outcome, code), (C.OUTCOME_RED, C.EXIT_RED))
        self.assertIn("chain main BROKEN at B: PARSEK-FAIL attempts=2 "
                      "runIds=run-B-1,run-B-2; not flown: C", detail)
        self.assertIn("1 of 3 stage(s) green", detail)

    def test_a_failed_handover_breaks_the_chain_with_its_reason(self):
        rec = C.StageRecord("A", "PASS", 1, ("r",), C.HANDOVER_FAILED, "no snapshot")
        (o,) = C.chain_outcomes(_line(), {"A": rec})
        self.assertEqual((o.status, o.stage, o.not_flown), (C.CHAIN_BROKEN, "A", ("B", "C")))
        self.assertIn("handover failed: no snapshot", o.line())

    def test_stopped_and_unflown(self):
        recs = {"A": _green("A")}
        self.assertEqual(C.campaign_outcome(_line(), recs, True)[:2],
                         (C.OUTCOME_STOPPED, C.EXIT_STOPPED))
        out, code, detail = C.campaign_outcome(_line(), recs, False)
        self.assertEqual((out, code), (C.OUTCOME_RED, C.EXIT_RED))
        self.assertIn("PENDING at B: not flown; not flown: B, C", detail)

    def test_all_green(self):
        recs = {s: _green(s) for s in "ABC"}
        self.assertEqual(C.campaign_outcome(_line(), recs, False)[:2],
                         (C.OUTCOME_GREEN, C.EXIT_GREEN))

    def test_a_broken_chain_beats_a_stop(self):
        recs = {"A": _red("A")}
        self.assertEqual(C.campaign_outcome(_line(), recs, True)[0], C.OUTCOME_RED)


class ScheduleTests(unittest.TestCase):

    def test_two_chains_pipeline_on_two_slots(self):
        p50 = {"A": 100, "B": 100, "C": 100, "X": 50, "Y": 50}
        sched = {s.spec_id: s for s in C.simulate_schedule(_two(), p50.get, 2)}
        self.assertEqual((sched["A"].start, sched["X"].start), (0, 0))
        self.assertNotEqual(sched["A"].slot, sched["X"].slot)
        self.assertEqual(sched["Y"].start, 100)  # after A, not after X
        self.assertEqual(sched["B"].start, 100)
        self.assertEqual(max(s.end for s in sched.values()), 300)

    def test_one_slot_is_serial(self):
        p50 = {"A": 100, "B": 100, "C": 100, "X": 50, "Y": 50}
        sched = C.simulate_schedule(_two(), p50.get, 1)
        self.assertEqual(max(s.end for s in sched), 400)
        ends = sorted((s.start, s.end) for s in sched)
        for (s0, e0), (s1, _e1) in zip(ends, ends[1:]):
            self.assertGreaterEqual(s1, e0)


class HandoverRefusalTests(unittest.TestCase):
    ROOT = "/h/results/campaign"
    IN = "/h/results/campaign/c1/main/01-B/in"
    MARKER = {"schema": C.HANDOVER_SCHEMA, "forStage": "B", "token": "tok"}

    def refusal(self, **kw):
        args = dict(stage_from=self.IN, campaign_root=self.ROOT, selected_ids=["B"],
                    slot_mode=True, env_token="tok", marker=self.MARKER,
                    has_persistent_sfs=True)
        args.update(kw)
        return C.stage_from_refusal(**args)

    def test_accepted(self):
        self.assertIsNone(self.refusal())

    def test_each_refusal(self):
        for kw, frag in (
                ({"slot_mode": False}, "only in a campaign runner's --slot child"),
                ({"selected_ids": ["B", "C"]}, "exactly one selected lane"),
                ({"stage_from": "/h/fixtures/saves/x/in"}, "is not inside"),
                ({"stage_from": "/h/results/campaign-evil/in"}, "is not inside"),
                ({"stage_from": "/h/results/campaign/c1/main/01-B/out"}, "folder"),
                ({"marker": None}, "no readable handover.json"),
                ({"marker": dict(HandoverRefusalTests.MARKER, forStage="C")},
                 "not the selected lane"),
                ({"env_token": None}, "not this runner's token"),
                ({"env_token": "other"}, "not this runner's token"),
                ({"has_persistent_sfs": False}, "holds no persistent.sfs")):
            with self.subTest(frag=frag):
                self.assertIn(frag, self.refusal(**kw) or "")

    def test_marker(self):
        ladder = _line()
        m = C.handover_marker("c1", ladder.stage("B"), "A", ["run-A"], "tok")
        self.assertEqual((m["schema"], m["forStage"], m["fromStage"], m["token"]),
                         (C.HANDOVER_SCHEMA, "B", "A", "tok"))


class PromotionTests(unittest.TestCase):

    def test_a_green_stage_promotes_into_its_chained_successor(self):
        ladder = _line()
        self.assertEqual(C.promotion_target(ladder, ladder.stage("A"), _green("A")), ("lb", ""))

    def test_refusals(self):
        ladder = C.parse_ladder(_ladder(("m", [
            {"id": "A"}, {"id": "B", "handover": "fixture", "reason": "random offers"},
            {"id": "C"}])), SPECS)
        self.assertIn("not green", C.promotion_target(ladder, ladder.stage("A"), _red("A"))[1])
        self.assertIn("random offers", C.promotion_target(ladder, ladder.stage("A"),
                                                          _green("A"))[1])
        self.assertIn("the last of chain", C.promotion_target(ladder, ladder.stage("C"),
                                                              _green("C"))[1])


# ---------------------------------------------------------------------------
# The committed ladders
# ---------------------------------------------------------------------------


class CommittedLadderTests(unittest.TestCase):

    def setUp(self):
        self.ctx = pr.Context(console=lambda _l: None)
        self.specs = {str(s.get("id")): s for s in pr.load_specs(self.ctx)}
        ldir = os.path.join(HARNESS_ROOT, C.LADDERS_DIRNAME)
        self.ladders = {}
        for name in sorted(os.listdir(ldir)):
            if name.endswith(".toml"):
                with open(os.path.join(ldir, name), "rb") as fh:
                    self.ladders[name[:-5]] = tomllib.load(fh)

    def test_every_committed_ladder_parses_and_its_craft_reach(self):
        self.assertIn("cs-career", self.ladders)
        for name, table in self.ladders.items():
            with self.subTest(ladder=name):
                self.assertEqual(table.get("name"), name, "the file is named after the ladder")
                ladder = C.parse_ladder(table, self.specs)
                self.assertEqual([], cr.check_ladder(self.ctx, ladder, self.specs))
                for st in ladder.stages():
                    self.assertTrue(os.path.isdir(os.path.join(
                        HARNESS_ROOT, "fixtures", "saves", st.template_leaf)), st.spec_id)

    def test_the_cs_3_to_cs_4b_replay(self):
        full = C.parse_ladder(self.ladders["cs-career"], self.specs)
        picked = C.select_range(full, "CS-3", "CS-4b")
        got = [(s.label, C.stage_source(picked, s, {})) for s in picked.stages()]
        self.assertEqual(got, [
            ("CS-3", ("fixture", None)),
            ("CS-3b", ("fixture", None)),
            ("CS-4", ("chain", "CS-3b-career-hire-upgrade")),
            ("CS-4b", ("chain", "CS-4-career-orbit-return"))])
        self.assertEqual(picked.stage("CS-4-career-orbit-return").carry_craft, ("CS Orbiter",))
        # Each chained stage's harvested save lands under its successor's leaf.
        self.assertEqual(C.harvest_leaf(full, full.stage("CS-4-career-orbit-return")),
                         full.stage("CS-4b-career-tracking-station").template_leaf)

    def test_the_dry_run_names_each_stage_source(self):
        lines = []
        code = cr.main(["run", "--ladder", "cs-career", "--name", "dry", "--from", "CS-3",
                        "--to", "CS-4b", "--slots", "1", "--dry-run"],
                       ctx=pr.Context(console=lines.append))
        self.assertEqual(0, code, "\n".join(lines))
        text = "\n".join(lines)
        self.assertIn("CS-4-career-orbit-return (CS-4) p50=", text)
        self.assertIn("stages CS-3b-career-hire-upgrade's save + carryCraft CS Orbiter", text)
        self.assertIn("stages fixtures/saves/", text)
        self.assertFalse(os.path.isdir(cr.campaign_dir(pr.Context(), "dry")))


# ---------------------------------------------------------------------------
# run.py --stage-from
# ---------------------------------------------------------------------------


class RunStageFromTests(unittest.TestCase):
    SLOT = "stock-tree-2"

    def setUp(self):
        self.box = tempfile.mkdtemp(prefix="parsek-campaign-run-")
        self._orig_results, self._orig_ctx = run.RESULTS_DIR, run._CTX
        run.RESULTS_DIR = os.path.join(self.box, "results")
        self.stage = os.path.join(run.RESULTS_DIR, "campaign", "c1", "main", "01-B")
        self.in_dir = os.path.join(self.stage, "in")
        os.makedirs(self.in_dir)
        with open(os.path.join(self.in_dir, "persistent.sfs"), "w") as fh:
            fh.write("GAME\n{\n\tMode = CAREER\n}\nSTAGE = A\n")
        self.write_marker("B", "tok")
        run._CTX = run.SelectionContext(slot=self.SLOT)

    def tearDown(self):
        run.RESULTS_DIR, run._CTX = self._orig_results, self._orig_ctx
        shutil.rmtree(self.box, ignore_errors=True)

    def write_marker(self, for_stage, token):
        with open(os.path.join(self.stage, C.HANDOVER_FILE), "w") as fh:
            json.dump({"schema": C.HANDOVER_SCHEMA, "forStage": for_stage, "token": token}, fh)

    def refusal(self, ids=("B",), token="tok", path=None):
        env = {slotrun.RUNNER_TOKEN_ENV: token} if token else {}
        return run._stage_from_refusal(path or self.in_dir, list(ids), env)

    def test_the_runner_handover_is_accepted(self):
        refusal, real = self.refusal()
        self.assertIsNone(refusal)
        self.assertEqual(real, os.path.realpath(self.in_dir))

    def test_refused_without_the_runner_token_or_for_another_lane(self):
        self.assertIn("token", self.refusal(token="other")[0])
        self.assertIn("token", self.refusal(token=None)[0])
        self.assertIn("not the selected lane", self.refusal(ids=("C",))[0])
        run._CTX = run.SelectionContext()
        self.assertIn("--slot child", self.refusal()[0])

    def test_refused_outside_the_campaign_folder(self):
        other = os.path.join(self.box, "elsewhere", "in")
        shutil.copytree(self.in_dir, other)
        shutil.copy2(os.path.join(self.stage, C.HANDOVER_FILE),
                     os.path.join(os.path.dirname(other), C.HANDOVER_FILE))
        self.assertIn("is not inside", self.refusal(path=other)[0])

    def test_the_flag_without_slot_is_a_usage_error(self):
        with contextlib.redirect_stderr(io.StringIO()) as err, \
                self.assertRaises(SystemExit) as cm:
            run.main(["--id", "X", "--stage-from", self.in_dir])
        self.assertEqual(2, cm.exception.code)
        self.assertIn("--stage-from is accepted only with --slot", err.getvalue())

    def test_stage_fixture_stages_the_chain_save_under_the_spec_leaf_with_its_overlay(self):
        # cs-career-mc1 has a shared-ships row (Jumping Flea): the overlay keys on the
        # spec's own leaf, so a chained save gets the same library craft its committed
        # fixture would have.
        instance = os.path.join(self.box, "instance")
        run._CTX.stage_from = os.path.realpath(self.in_dir)
        spec = {"fixture": {"saveTemplate": "fixtures/saves/cs-career-mc1",
                            "injectedRecordings": "none"}}
        logger = run.HarnessLogger(os.path.join(self.box, "h.log"))
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                ok, name, subkind = run.stage_fixture(spec, instance, run.Runtime(), logger)
        finally:
            logger.close()
        self.assertEqual((ok, name, subkind), (True, "cs-career-mc1", ""))
        staged = os.path.join(instance, "saves", "cs-career-mc1")
        with open(os.path.join(staged, "persistent.sfs")) as fh:
            self.assertIn("STAGE = A", fh.read())
        self.assertIn("Jumping Flea.craft", os.listdir(os.path.join(staged, "Ships", "VAB")))
        with open(os.path.join(self.box, "h.log")) as fh:
            self.assertIn("campaign handover: staging", fh.read())


# ---------------------------------------------------------------------------
# The runner end to end
# ---------------------------------------------------------------------------


SPEC_TEMPLATE = '''id = "%s"
tier = "daily"
instanceProfile = "stock-tree"
[fixture]
saveTemplate = "fixtures/saves/%s"
injectedRecordings = "none"
[driver]
kind = "seam"
steps = []
'''

# Real leaves with a shared-ships row ("Jumping Flea"), so the child's real
# stage_fixture overlays the library craft and the real harvest drops it again.
LEAVES = {"S-A": "cs-career-mc1", "S-B": "cs-career-orbit", "S-C": "cs-career-ts1",
          "S-X": "cs-career-mun1", "S-Y": "cs-career-mun2"}

LINE = '''schema = 1
name = "line"
[harvest]
keepParsek = true
allowNoVessel = true
expectMode = "CAREER"
[[chain]]
name = "main"
[[chain.stage]]
id = "S-A"
[[chain.stage]]
id = "S-B"
carryCraft = ["Carry Craft"]
[[chain.stage]]
id = "S-C"
'''

TWO = LINE.replace('name = "line"', 'name = "two"') + '''
[[chain]]
name = "side"
[[chain.stage]]
id = "S-X"
[[chain.stage]]
id = "S-Y"
after = ["S-A"]
'''


class RunnerSmokeTests(unittest.TestCase):

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="campaign-smoke-")
        self.umbrella = os.path.join(self.tmp, "umbrella")
        self.harness = os.path.join(self.umbrella, "wt", "harness")
        for d in ("scenarios", "results", "coverage", "tools", "campaigns",
                  os.path.join("fixtures", "ships"), os.path.join("fixtures", "saves")):
            os.makedirs(os.path.join(self.harness, d))
        shutil.copy2(os.path.join(TOOLS, "harvest_bdock_station.py"),
                     os.path.join(self.harness, "tools"))
        shutil.copy2(os.path.join(HARNESS_ROOT, "fixtures", "ships", "Jumping Flea.craft"),
                     os.path.join(self.harness, "fixtures", "ships"))
        for sid, leaf in LEAVES.items():
            with open(os.path.join(self.harness, "scenarios", sid + ".toml"), "w") as fh:
                fh.write(SPEC_TEMPLATE % (sid, leaf))
            save = os.path.join(self.harness, "fixtures", "saves", leaf)
            os.makedirs(os.path.join(save, "Ships", "VAB"))
            with open(os.path.join(save, "persistent.sfs"), "w") as fh:
                fh.write("GAME\n{\n\tTitle = %s (CAREER)\n\tMode = CAREER\n\tFLIGHTSTATE\n"
                         "\t{\n\t\tUT = 0\n\t}\n}\nFIXTURE = %s\n" % (leaf, leaf))
        with open(os.path.join(self.harness, "fixtures", "saves", LEAVES["S-B"], "Ships",
                               "VAB", "Carry Craft.craft"), "w") as fh:
            fh.write("ship = Carry Craft\n")
        for name, text in (("line", LINE), ("two", TWO)):
            with open(os.path.join(self.harness, "campaigns", name + ".toml"), "w") as fh:
                fh.write(text)
        for slot in ("stock-tree", "stock-tree-2"):
            d = os.path.join(self.umbrella, "automation", slot)
            os.makedirs(os.path.join(d, "GameData", "Parsek", "Plugins"))
            open(os.path.join(d, "KSP_x64.exe"), "wb").close()
            with open(os.path.join(d, "GameData", "Parsek", "Plugins", "Parsek.dll"),
                      "wb") as fh:
                fh.write(b"same-dll")
        self.state = os.path.join(self.tmp, "child-state")
        os.makedirs(self.state)
        self.console = []
        env = {k: v for k, v in os.environ.items() if not k.startswith("FAKE_CHILD_")}
        env.update(FAKE_CHILD_STATE=self.state, FAKE_CHILD_UMBRELLA=self.umbrella,
                   FAKE_CHILD_HARNESS=self.harness, FAKE_CHILD_SLEEP="0.2")
        self.env = mock.patch.dict(os.environ, env, clear=True)
        self.env.start()

    def tearDown(self):
        self.env.stop()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def ctx(self):
        return pr.Context(harness_root=self.harness, umbrella_root=self.umbrella,
                          run_py=FAKE_CHILD, poll_seconds=0.05, heartbeat_seconds=0.2,
                          dispatch_gap_seconds=0.0, bridge=FakeBridge(),
                          unity_prefs=FakePrefs(),
                          memory_fn=lambda: parallel.MemoryReading(32.0, 12.0),
                          desktops_fn=lambda: ([str(uuid.UUID(int=1))], str(uuid.UUID(int=1))),
                          stdin_text=lambda: "", console=self.console.append)

    def main(self, *argv):
        return cr.main(list(argv), ctx=self.ctx())

    def run_dir(self, name):
        return os.path.join(self.harness, "results", "campaign", name)

    def verdict(self, name):
        with open(os.path.join(self.run_dir(name), C.VERDICT_FILE)) as fh:
            return fh.read()

    def records(self):
        out = []
        for f in os.listdir(self.state):
            if f.startswith("stage-"):
                with open(os.path.join(self.state, f)) as fh:
                    out.append(json.load(fh))
        return out

    def flown(self, lane):
        return [r for r in self.records() if r["lane"] == lane]

    def events(self, name):
        with open(os.path.join(self.run_dir(name), "events.jsonl")) as fh:
            return [json.loads(l) for l in fh if l.strip()]

    def test_the_produced_save_is_handed_down_the_chain(self):
        code = self.main("run", "--ladder", "line", "--name", "c1", "--slots", "1")
        self.assertEqual(0, code, "\n".join(self.console))
        (a,), (b,), (c,) = self.flown("S-A"), self.flown("S-B"), self.flown("S-C")
        for r in (a, b, c):
            self.assertTrue(r["tokenMatches"], r)
            self.assertIsNone(r["refusal"], r)
            self.assertEqual("campaign-runner run=c1", r["lockSelection"])
        self.assertNotIn("--stage-from", a["argv"])
        self.assertIn("FIXTURE = cs-career-mc1", a["staged"])
        # S-B stages S-A's save (harvested under S-B's leaf), never its own fixture.
        from_b = b["argv"][b["argv"].index("--stage-from") + 1]
        self.assertTrue(from_b.endswith(os.path.join("main", "01-S-B", "in")), from_b)
        self.assertIn("STAGE = S-A", b["staged"])
        self.assertNotIn("FIXTURE = cs-career-orbit", b["staged"])
        self.assertIn("Title = cs-career-orbit (CAREER)", b["staged"])
        # Its carryCraft rides along; the library craft the harvest dropped comes
        # back once through the overlay keyed by S-B's leaf.
        self.assertEqual(["Carry Craft.craft", "Jumping Flea.craft"], b["stagedVab"])
        self.assertIn("STAGE = S-A\n", c["staged"])
        self.assertIn("STAGE = S-B\n", c["staged"])
        self.assertIn("Carry Craft.craft", c["stagedVab"], "a carried craft stays carried")
        text = self.verdict("c1")
        self.assertIn("outcome=GREEN exit=0", text.splitlines()[0])
        self.assertIn("chain main GREEN", text)
        with open(os.path.join(self.run_dir("c1"), "main", "01-S-B",
                               C.STAGE_RECORD_FILE)) as fh:
            rec_b = C.StageRecord.from_json(json.load(fh))
        self.assertEqual((rec_b.source, rec_b.handover, rec_b.out_leaf),
                         ("S-A", C.HANDOVER_OK, "cs-career-ts1"))
        out_sfs = os.path.join(self.run_dir("c1"), "main", "01-S-B", "out", "cs-career-ts1",
                               "persistent.sfs")
        self.assertTrue(os.path.isfile(out_sfs))
        # Promotion: S-A's save becomes S-B's committed fixture, keeping the carry craft.
        code = self.main("promote", "--name", "c1", "--stage", "S-A")
        self.assertEqual(0, code, "\n".join(self.console))
        fixture = os.path.join(self.harness, "fixtures", "saves", "cs-career-orbit")
        with open(os.path.join(fixture, "persistent.sfs")) as fh:
            self.assertIn("STAGE = S-A", fh.read())
        self.assertEqual(["Carry Craft.craft"], sorted(os.listdir(os.path.join(
            fixture, "Ships", "VAB"))))
        self.assertEqual(2, self.main("promote", "--name", "c1", "--stage", "S-C"))
        self.assertEqual(2, self.main("run", "--ladder", "line", "--name", "c1"))

    def test_a_middle_stage_failure_stops_the_chain_and_a_resume_flies_from_it(self):
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"S-B": "PARSEK-FAIL"})
        code = self.main("run", "--ladder", "line", "--name", "c2", "--slots", "1")
        self.assertEqual(1, code)
        self.assertEqual([], self.flown("S-C"), "a later stage must not fly on a stale save")
        text = self.verdict("c2")
        self.assertIn("outcome=RED exit=1", text)
        self.assertIn("chain main BROKEN at S-B: PARSEK-FAIL", text)
        self.assertIn("not flown: S-C", text)
        self.assertIn("stage main S-C NOT-FLOWN", text)

        os.environ["FAKE_CHILD_VERDICTS"] = "{}"
        status = []
        cr.main(["status", "--name", "c2"], ctx=pr.Context(
            harness_root=self.harness, umbrella_root=self.umbrella, console=status.append))
        self.assertIn("chain main BROKEN at S-B", "\n".join(status))
        code = self.main("resume", "--name", "c2")
        self.assertEqual(0, code, "\n".join(self.console))
        self.assertEqual([1, 2, 1], [len(self.flown(s)) for s in ("S-A", "S-B", "S-C")])
        for b in self.flown("S-B"):
            self.assertIn("STAGE = S-A", b["staged"])
            self.assertNotIn("STAGE = S-B", b["staged"])
        (c,) = self.flown("S-C")
        self.assertIn("STAGE = S-B", c["staged"])
        self.assertIn("outcome=GREEN", self.verdict("c2"))

    def test_a_green_stage_without_a_snapshot_breaks_the_chain(self):
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"S-A": "NOSNAP"})
        code = self.main("run", "--ladder", "line", "--name", "c3", "--slots", "1")
        self.assertEqual(1, code)
        self.assertEqual([], self.flown("S-B"))
        text = self.verdict("c3")
        self.assertIn("chain main BROKEN at S-A: PASS", text)
        self.assertIn("handover failed: run run-S-A-", text)
        self.assertIn("instance save is never used", text)

    def test_the_chain_does_not_read_a_save_a_sibling_can_overwrite(self):
        # Every child first overwrites every snapshot and every instance save, as a
        # sibling run re-staging a shared template would.
        os.environ["FAKE_CHILD_CLOBBER"] = "1"
        code = self.main("run", "--ladder", "line", "--name", "c4", "--slots", "2")
        self.assertEqual(0, code, "\n".join(self.console))
        (a,), (b,), (c,) = self.flown("S-A"), self.flown("S-B"), self.flown("S-C")
        self.assertIn("STAGE = S-A", b["staged"])
        self.assertIn("STAGE = S-B", c["staged"])
        for r in (b, c):
            self.assertNotIn("CLOBBERED", r["staged"])
        snaps = [n for n in os.listdir(os.path.join(self.harness, "results"))
                 if n.startswith("run-S-A-") and n.endswith("_save")]
        self.assertEqual(1, len(snaps))
        with open(os.path.join(self.harness, "results", snaps[0], "persistent.sfs")) as fh:
            self.assertIn("CLOBBERED", fh.read(), "the clobber ran; the chain did not need it")

    def test_two_chains_pipeline_on_two_slots(self):
        os.environ["FAKE_CHILD_SLEEP"] = "0.8"
        code = self.main("run", "--ladder", "two", "--name", "c5", "--slots", "2")
        self.assertEqual(0, code, "\n".join(self.console))
        recs = self.records()
        self.assertEqual(sorted(r["lane"] for r in recs), sorted(LEAVES))
        self.assertEqual(2, max(r["concurrent"] for r in recs))
        self.assertEqual({"stock-tree", "stock-tree-2"}, {r["slot"] for r in recs})
        events = self.events("c5")
        order = [(e["kind"], e.get("lane")) for e in events if e["kind"] in
                 ("dispatch", "settle")]
        self.assertLess(order.index(("settle", "S-A")), order.index(("dispatch", "S-Y")),
                        "S-Y waits for its cross-chain dependency S-A")
        self.assertLess(order.index(("dispatch", "S-X")), order.index(("settle", "S-A")),
                        "the second chain starts beside the first")
        (y,) = self.flown("S-Y")
        self.assertIn("STAGE = S-X", y["staged"])
        self.assertNotIn("STAGE = S-A", y["staged"])


if __name__ == "__main__":
    unittest.main()
