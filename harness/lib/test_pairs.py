"""Paired lanes (multiplayer T-54): two specs that share a `[pair] group` fly at
once on two slots of the parallel runner, may share one race folder and meet at
harness-side `barrier` steps.

Four layers, each against the code that owns it:

- the spec grammar and the pure barrier rules (``hlib``);
- the runner's pure decisions (``parallel``): order, both-or-neither dispatch,
  the reservation, the memory guard for two copies, the resume rule and the
  partner-lost backstop;
- ``run.py --slot`` as a pair child over the fake KSP (``test_run_slots``'s box);
- the runner shell end to end with the fake child (``test_parallel``'s box),
  whose barriers run through run.py's own ``wait_at_barrier``.

No KSP, no network::

    cd harness && python -m unittest discover -s lib -p test_pairs.py -q
"""

import contextlib
import io
import json
import ntpath
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import tomllib
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
for _p in (HARNESS_ROOT, HERE, os.path.join(HARNESS_ROOT, "provision"),
           os.path.join(HARNESS_ROOT, "tools")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import parallel  # noqa: E402
import parallel_runner as pr  # noqa: E402
import run  # noqa: E402
import slotrun  # noqa: E402
import test_parallel as tp  # noqa: E402  (module import: its TestCases are not re-collected)
import test_run_slots as trs  # noqa: E402

P = parallel


def barrier(name, timeout=60):
    return {"harness": "barrier", "args": {"name": name, "timeout": timeout}}


def pair_spec(sid, group="G1", role="A", barriers=("one",), profile="stock-minimal",
              tier="operator", shared=True, retry="none", budget=600):
    steps = [{"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"},
              "expect": "OK", "budget": 300}]
    for name in barriers:
        steps.append(barrier(name))
    steps.append({"cmd": "FlushAndQuit", "expect": "OK"})
    spec = {"schema": 1, "id": sid, "tier": tier, "instanceProfile": profile,
            "fixture": {"saveTemplate": "fixtures/saves/fresh-career",
                        "injectedRecordings": "none", "craft": []},
            "driver": {"kind": "seam", "steps": steps},
            "runtime": {"budgetSeconds": budget}, "retry": {"policy": retry},
            "pair": {"group": group, "role": role}}
    if shared:
        spec["fixture"]["race"] = {"sharedFolder": hlib.RACE_SHARED_PAIR}
    return spec


def harness_errors(spec):
    steps = spec["driver"]["steps"]
    out = []
    for i, step in enumerate(steps):
        if hlib.is_harness_step(step):
            out.extend(hlib.validate_harness_step(
                i, steps, hlib.spec_race_fixture(spec) is not None, None,
                hlib.spec_pair(spec) is not None, spec["runtime"]["budgetSeconds"]))
    return out


# ---------------------------------------------------------------------------
# hlib: the spec grammar and the barrier rules
# ---------------------------------------------------------------------------


class PairSpecTests(unittest.TestCase):

    def test_barrier_names_that_differ_only_in_case_are_refused(self):
        # One marker file on Windows.
        errs = harness_errors(pair_spec("S", barriers=("Sync", "sync")))
        self.assertTrue(any("already an earlier barrier" in e and "ignoring case" in e
                            for e in errs), errs)

    def test_groups_that_differ_only_in_case_are_refused(self):
        specs = [pair_spec("a1", group="g1", role="A"), pair_spec("b1", group="g1", role="B"),
                 pair_spec("a2", group="G1", role="A"), pair_spec("b2", group="G1", role="B")]
        problems = hlib.pair_problems(specs)
        self.assertTrue(any("G1 and g1 differ only in case" in p for p in problems), problems)
        # Selecting one of them alone still meets the other in the check.
        with self.assertRaises(P.SelectionError) as cm:
            P.complete_pairs([specs[0]], specs)
        self.assertIn("differ only in case", str(cm.exception))

    def test_a_well_formed_pair_half_is_clean(self):
        spec = pair_spec("PA")
        self.assertEqual([], hlib.validate_pair_block(spec))
        self.assertEqual([], harness_errors(spec))
        self.assertEqual([], hlib.validate_race_fixture(spec["fixture"]))
        self.assertEqual(("G1", "A"), hlib.spec_pair_identity(spec))

    def _block_error(self, spec, fragment):
        errors = hlib.validate_pair_block(spec)
        self.assertTrue(any(fragment in e for e in errors), errors)

    def test_a_pair_half_with_no_barrier_is_refused(self):
        # The rule that keeps a half from reading PASS while its partner never ran.
        self._block_error(pair_spec("PA", barriers=()), "at least one")

    def test_a_pair_half_that_would_retry_is_refused(self):
        self._block_error(pair_spec("PA", retry="once"), "retry.policy")

    def test_an_exclusive_pair_half_is_refused(self):
        spec = pair_spec("PA")
        spec["runtime"]["exclusive"] = True
        self._block_error(spec, "exclusive")

    def test_bad_group_role_and_keys_are_refused(self):
        spec = pair_spec("PA")
        spec["pair"] = {"group": "../x", "role": "C", "extra": 1}
        errors = hlib.validate_pair_block(spec)
        self.assertEqual(3, len(errors), errors)
        self.assertIsNone(hlib.spec_pair_identity(spec))
        spec["pair"] = "G1"
        self.assertTrue(hlib.validate_pair_block(spec)[0].startswith("pair: must be a table"))

    def test_the_pair_folder_needs_a_pair_block(self):
        spec = pair_spec("PA")
        del spec["pair"]
        self._block_error(spec, "needs a [pair]")

    def test_a_spec_without_either_block_is_untouched(self):
        spec = pair_spec("PA", shared=False)
        del spec["pair"]
        spec["driver"]["steps"] = [s for s in spec["driver"]["steps"]
                                   if not hlib.is_harness_step(s)]
        self.assertEqual([], hlib.validate_pair_block(spec))
        self.assertIsNone(hlib.spec_pair(spec))

    def test_barrier_step_refusals(self):
        def errors_for(args, pair=True, budget=600, earlier=()):
            steps = ([{"cmd": "LoadGame"}] + [barrier(n) for n in earlier]
                     + [{"harness": "barrier", "args": args}, {"cmd": "FlushAndQuit"}])
            return hlib.validate_harness_step(len(earlier) + 1, steps, False, None, pair, budget)
        self.assertEqual([], errors_for({"name": "go", "timeout": 30}))
        self.assertIn("needs a [pair]", errors_for({"name": "go", "timeout": 30}, pair=False)[0])
        self.assertIn("plain name", errors_for({"name": "a/b", "timeout": 30})[0])
        self.assertIn("already an earlier barrier",
                      errors_for({"name": "go", "timeout": 30}, earlier=("go",))[0])
        for bad in (0, -1, True, "30", hlib.BARRIER_TIMEOUT_MAX_SECONDS + 1):
            with self.subTest(timeout=bad):
                self.assertIn("seconds in", errors_for({"name": "go", "timeout": bad})[0])
        self.assertIn("under runtime.budgetSeconds",
                      errors_for({"name": "go", "timeout": 600}, budget=600)[0])
        self.assertIn("unknown key", errors_for({"name": "go", "timeout": 3, "x": 1})[0])

    def test_a_barrier_carries_no_seam_keys_and_sits_between_seam_steps(self):
        steps = [{"cmd": "LoadGame"}, {"harness": "barrier", "args": {"name": "x", "timeout": 5},
                                       "expect": "OK"}]
        errors = hlib.validate_harness_step(1, steps, False, None, True, 600)
        self.assertTrue(any("not allowed on a harness step" in e for e in errors), errors)
        self.assertTrue(any("no seam step follows" in e for e in errors), errors)

    def test_the_race_fixture_validator_accepts_the_pair_folder(self):
        self.assertEqual([], hlib.validate_race_fixture({"race": {"sharedFolder": "pair"}}))
        self.assertTrue(hlib.validate_race_fixture({"race": {"sharedFolder": "pairs"}}))

    def test_pair_problems_across_two_specs(self):
        a, b = pair_spec("PA"), pair_spec("PB", role="B")
        self.assertEqual([], hlib.pair_problems([a, b]))
        self.assertIn("exactly one role A and one role B",
                      hlib.pair_problems([a])[0])
        self.assertIn("exactly one role A and one role B",
                      hlib.pair_problems([a, pair_spec("PA2")])[0])
        cases = (
            (pair_spec("PB", role="B", profile="stock-tree"), "instanceProfile differs"),
            (pair_spec("PB", role="B", tier="nightly"), "tier differs"),
            (pair_spec("PB", role="B", barriers=("two",)), "the barriers differ"),
            (pair_spec("PB", role="B", shared=False), "only one half"),
        )
        for other, fragment in cases:
            with self.subTest(fragment=fragment):
                problems = hlib.pair_problems([a, other])
                self.assertEqual(1, len(problems), problems)
                self.assertIn(fragment, problems[0])

    def test_barrier_order_must_match(self):
        a = pair_spec("PA", barriers=("one", "two"))
        b = pair_spec("PB", role="B", barriers=("two", "one"))
        self.assertIn("same order", hlib.pair_problems([a, b])[0])

    def test_unpaired_specs_have_no_pair_problems(self):
        self.assertEqual([], hlib.pair_problems([tp.spec("A"), tp.spec("B")]))


class BarrierDecisionTests(unittest.TestCase):

    def test_a_partner_that_gave_the_barrier_up_is_lost_despite_its_arrival(self):
        decision, reason = hlib.barrier_decision(True, False, 0.0, 60, "x", "A", True)
        self.assertEqual(hlib.BARRIER_LOST, decision)
        self.assertIn("partner A gave barrier x up", reason)
        self.assertEqual("x.A.gaveup", hlib.barrier_gave_up_file("x", "A"))

    def test_a_late_partner_never_meets_a_half_that_timed_out(self):
        folder = tempfile.mkdtemp(prefix="parsek-barrier-")
        self.addCleanup(shutil.rmtree, folder, True)
        met, reason, _ = run.wait_at_barrier(folder, "x", "A", 0.2, poll_seconds=0.02)
        self.assertFalse(met)
        self.assertIn("did not reach barrier x", reason)
        self.assertTrue(os.path.isfile(os.path.join(folder, "x.A.arrived")))
        self.assertTrue(os.path.isfile(os.path.join(folder, "x.A.gaveup")))
        # B boots late: A's arrival file is still there, but A left.
        met, reason, waited = run.wait_at_barrier(folder, "x", "B", 5, poll_seconds=0.02)
        self.assertFalse(met)
        self.assertIn("partner A gave barrier x up", reason)
        self.assertLess(waited, 1.0)

    def test_arrival_wins_over_an_ended_marker(self):
        # A partner that passed this barrier and then finished is not lost.
        self.assertEqual(hlib.BARRIER_MET, hlib.barrier_decision(True, True, 99, 10)[0])

    def test_an_ended_partner_is_lost_before_the_timeout(self):
        decision, reason = hlib.barrier_decision(False, True, 0.1, 60, "one", "B")
        self.assertEqual(hlib.BARRIER_LOST, decision)
        self.assertIn("partner B ended without reaching barrier one", reason)

    def test_the_timeout_loses_the_partner(self):
        self.assertEqual(hlib.BARRIER_WAIT, hlib.barrier_decision(False, False, 9.9, 10)[0])
        decision, reason = hlib.barrier_decision(False, False, 10, 10, "one", "A")
        self.assertEqual(hlib.BARRIER_LOST, decision)
        self.assertIn("within 10s", reason)

    def test_file_names(self):
        self.assertEqual("one.A.arrived", hlib.barrier_arrival_file("one", "A"))
        self.assertEqual("B.ended", hlib.pair_ended_file("B"))
        self.assertEqual("B", hlib.pair_partner_role("A"))
        self.assertEqual("A", hlib.pair_partner_role("B"))


class PairChildRefusalTests(unittest.TestCase):
    PAR, RS = "/u/wt/harness/results/parallel", "/u/automation/rs"

    def refusal(self, slot_mode=True, selected=None, pair_dir=PAR + "/r1/pairs/G1",
                shared=RS + "/r1/G1"):
        return hlib.pair_child_refusal(slot_mode, [pair_spec("PA")] if selected is None
                                       else selected, pair_dir, shared, self.PAR, self.RS)

    def test_the_runner_shape_is_accepted(self):
        self.assertIsNone(self.refusal())

    def test_each_refusal(self):
        self.assertIn("only with --slot", self.refusal(slot_mode=False))
        self.assertIn("exactly one lane", self.refusal(selected=[pair_spec("PA")] * 2))
        self.assertIn("no valid [pair]", self.refusal(selected=[tp.spec("A")]))
        self.assertIn("--pair-dir", self.refusal(pair_dir=self.PAR + "/r1/pairs/G2"))
        self.assertIn("--pair-dir", self.refusal(pair_dir="/elsewhere/r1/pairs/G1"))
        self.assertIn("--pair-shared", self.refusal(shared=self.RS + "/r2/G1"))
        self.assertIn("--pair-shared", self.refusal(shared=self.RS + "/r1/G1/x"))

    def test_windows_folded_paths_match_an_upper_case_group_and_run(self):
        # The shape run.py hands over on Windows: ntpath.normcase lower-cases every
        # path, so the group id must be folded by the same function (the bug a PC
        # run found: "pair-1" != "PAIR-1" refused every real pair child).
        def fold(text):
            return ntpath.normcase(text).replace("\\", "/")
        par = fold(r"C:\U\wt\harness\results\parallel")
        rs = fold(r"C:\U\automation\rs")
        spec = pair_spec("PA", group="PAIR-1")

        def refusal(pair_dir, shared, with_fold=True):
            return hlib.pair_child_refusal(True, [spec], fold(pair_dir), fold(shared), par, rs,
                                           fold=fold if with_fold else None)
        good_dir = r"C:\U\wt\harness\results\parallel\Run-UP\pairs\PAIR-1"
        good_shared = r"C:\U\automation\rs\Run-UP\PAIR-1"
        self.assertIsNone(refusal(good_dir, good_shared))
        self.assertIn("--pair-dir", refusal(good_dir, good_shared, with_fold=False))
        self.assertIn("--pair-dir", refusal(good_dir.replace("PAIR-1", "PAIR-2"), good_shared))
        self.assertIn("--pair-shared", refusal(good_dir, good_shared.replace("Run-UP", "Run-2")))
        self.assertIn("--pair-shared", refusal(good_dir, good_shared.replace("PAIR-1", "PAIR-2")))


class PartnerLostVerdictTests(unittest.TestCase):

    def test_a_lost_partner_is_a_terminal_flake_exempt_invalid(self):
        v = hlib.classify_verdict({"valid": False, "stage_subkind": hlib.PARTNER_LOST_SUBKIND},
                                  {}, {}, 1, "once")
        self.assertEqual((hlib.VERDICT_INVALID, hlib.PARTNER_LOST_SUBKIND), (v.verdict, v.subkind))
        self.assertFalse(v.retryable)
        self.assertFalse(hlib.should_retry(v, 1, "once"))
        self.assertIn(hlib.PARTNER_LOST_SUBKIND, hlib.FLAKE_EXEMPT_INVALID_SUBKINDS)
        self.assertTrue(hlib.flake_entry_is_exempt({"outcome": "INVALID",
                                                    "subkind": hlib.PARTNER_LOST_SUBKIND}))
        # Listed apart from the machine refusals.
        self.assertNotIn(hlib.PARTNER_LOST_SUBKIND, hlib.PARALLEL_RUN_INVALID_SUBKINDS)
        self.assertNotIn(hlib.PARTNER_LOST_SUBKIND, hlib.PARSEK_FAIL_SUBKINDS)


class CommittedPairSpecTests(unittest.TestCase):
    """Every committed paired spec validates, has its partner, and pairs cleanly."""

    @classmethod
    def setUpClass(cls):
        cls.specs = []
        scen = os.path.join(HARNESS_ROOT, "scenarios")
        for name in sorted(os.listdir(scen)):
            if name.endswith(".toml"):
                with open(os.path.join(scen, name), "rb") as fh:
                    cls.specs.append(tomllib.load(fh))
        cls.paired = [s for s in cls.specs if hlib.spec_pair(s) is not None]

    def test_committed_pairs_are_whole_and_consistent(self):
        self.assertEqual([], hlib.pair_problems(self.specs))
        for spec in self.paired:
            with self.subTest(spec=spec.get("id")):
                self.assertEqual([], hlib.validate_pair_block(spec))
                self.assertIsNotNone(hlib.spec_pair_identity(spec))
        selected, added = P.complete_pairs(self.paired[:1], self.specs) if self.paired \
            else ([], [])
        self.assertEqual(len(added), 1 if self.paired else 0)

    def test_the_pair_handshake_lanes_are_committed(self):
        ids = {s.get("id") for s in self.paired}
        self.assertEqual({"PAIR-1a-shared-folder-handshake",
                          "PAIR-1b-shared-folder-handshake"}, ids)

    def test_the_handshake_fixture_has_the_entry_both_halves_name(self):
        by_id = {s["id"]: s for s in self.paired}
        a = by_id["PAIR-1a-shared-folder-handshake"]
        steps = [s for s in a["driver"]["steps"] if hlib.harness_step_kind(s) == "raceFolder"]
        self.assertTrue(steps)
        for step in steps:
            src = step["args"].get("from")
            if src:
                path = os.path.join(HARNESS_ROOT, src, *step["args"]["path"].split("/"))
                self.assertTrue(os.path.isfile(path), path)


# ---------------------------------------------------------------------------
# parallel: the runner's pure decisions
# ---------------------------------------------------------------------------


def plane(i, group=None, role=None, p50=100.0, profile="stock-minimal"):
    return P.Lane(i, profile, "operator", p50, P.P50_SOURCE_DURATION, None, group, role)


BY_PROFILE = {"stock-minimal": ["stock-minimal", "stock-minimal-2", "stock-minimal-3"]}


class PairOrderTests(unittest.TestCase):

    def test_a_pair_is_one_unit_ranked_by_its_longer_half_a_first(self):
        lanes = [tp.lane("S1", 150), plane("PB", "G", "B", 400), tp.lane("S2", 300),
                 plane("PA", "G", "A", 50)]
        self.assertEqual(["PA", "PB", "S2", "S1"],
                         [l.lane_id for l in P.order_lanes(lanes)])

    def test_unpaired_order_is_unchanged(self):
        lanes = [tp.lane("a", 10), tp.lane("b", 30), tp.lane("c", 20, excl="x")]
        self.assertEqual(["b", "a", "c"], [l.lane_id for l in P.order_lanes(lanes)])

    def test_lane_json_round_trip(self):
        half = plane("PA", "G", "A")
        self.assertEqual(half, P.Lane.from_json(half.to_json()))
        self.assertNotIn("pairGroup", tp.lane("S").to_json())
        self.assertIsNone(P.Lane.from_json({"id": "x", "pairGroup": "G", "pairRole": "Z"}).pair_group)


class PairDispatchTests(unittest.TestCase):
    NOW = 1000.0

    def decide(self, pending, running=(), max_running=3, by_profile=None, memory=None):
        return P.decide_next(pending, list(running), by_profile or BY_PROFILE, max_running,
                             False, memory, self.NOW, None)

    def pair(self):
        return [plane("PA", "G", "A"), plane("PB", "G", "B")]

    def test_both_halves_start_in_one_decision_a_first(self):
        d = self.decide(self.pair())
        self.assertEqual(P.ACTION_START_PAIR, d.action)
        self.assertEqual(("PA", "stock-minimal", "PB", "stock-minimal-2"),
                         (d.lane_id, d.slot, d.partner_lane_id, d.partner_slot))

    def test_one_free_slot_reserves_the_dispatch_for_the_pair(self):
        # Neither half starts, and the single lane behind the pair does not jump it.
        running = [P.Running("X", "stock-minimal", False, 0.0),
                   P.Running("Y", "stock-minimal-2", False, 0.0)]
        d = self.decide(self.pair() + [tp.lane("S")], running)
        self.assertEqual((P.ACTION_WAIT, P.WAIT_PAIR), (d.action, d.wait_kind))
        self.assertIn("waits for two free stock-minimal slots", d.reason)

    def test_the_slot_cap_counts_both_halves(self):
        running = [P.Running("X", "stock-minimal-3", False, 0.0)]
        d = self.decide(self.pair(), running, max_running=2)
        self.assertEqual((P.ACTION_WAIT, P.WAIT_PAIR), (d.action, d.wait_kind))

    def test_at_one_slot_the_pair_is_passed_over_and_the_run_ends(self):
        d = self.decide(self.pair() + [tp.lane("S")], max_running=1)
        self.assertEqual((P.ACTION_START, "S"), (d.action, d.lane_id))
        d = self.decide(self.pair(), max_running=1)
        self.assertEqual(P.ACTION_DONE, d.action)
        self.assertIn("a pair needs two slots", d.reason)

    def test_a_profile_with_one_slot_never_starts_a_pair(self):
        d = self.decide(self.pair(), by_profile={"stock-minimal": ["stock-minimal"]})
        self.assertEqual(P.ACTION_DONE, d.action)

    def test_a_lone_half_never_starts(self):
        for lone in ([plane("PB", "G", "B")], [plane("PA", "G", "A")]):
            with self.subTest(lone=lone[0].lane_id):
                self.assertEqual(P.ACTION_DONE, self.decide(lone).action)

    def test_the_memory_guard_charges_two_copies(self):
        # Nothing running: the first half is the free first lane, the second is
        # charged; nothing would free memory, so the run ends with the pair pending.
        tight = P.MemoryReading(free_ram_gb=5.0)
        d = self.decide(self.pair(), memory=tight)
        self.assertEqual(P.ACTION_DONE, d.action)
        self.assertIn("pair G, two copies", d.reason)
        self.assertEqual(P.ACTION_START, self.decide([tp.lane("S")], memory=tight).action)
        self.assertEqual(P.ACTION_START_PAIR,
                         self.decide(self.pair(), memory=P.MemoryReading(6.1)).action)
        # One running: both halves charged.
        running = [P.Running("X", "stock-minimal-3", False, 0.0)]
        holds = P.memory_holds(P.MemoryReading(10.0), 1, 0.0, new_copies=2)
        self.assertIn("new copies 2 x 4.5 GB", holds[0][1])
        self.assertEqual(P.WAIT_MEMORY,
                         self.decide(self.pair(), running, memory=P.MemoryReading(10.0)).wait_kind)
        self.assertEqual([], P.memory_holds(P.MemoryReading(10.6), 1, 0.0, new_copies=2))

    def test_a_pair_held_by_memory_from_idle_does_not_block_a_later_lane(self):
        # (a) Nothing runs, so nothing frees memory: the pair does not reserve the
        # dispatch, and the unpaired lane behind it starts (first lane free).
        d = self.decide(self.pair() + [tp.lane("S")], max_running=2,
                        memory=P.MemoryReading(5.5, 12.0))
        self.assertEqual((P.ACTION_START, "S"), (d.action, d.lane_id))

    def test_a_pair_held_by_memory_from_idle_ends_the_run_pending(self):
        # (b) Only the memory-held pair is left and nothing runs: DONE, not a wait.
        d = self.decide(self.pair(), max_running=2, memory=P.MemoryReading(5.5, 12.0))
        self.assertEqual(P.ACTION_DONE, d.action)
        self.assertIn("paired lane(s) PA, PB cannot start: nothing runs to free memory", d.reason)
        self.assertIn("free RAM 5.5 GB", d.reason)
        self.assertIn("free memory, then resume", d.reason)

    def test_a_pair_held_by_memory_while_a_lane_runs_still_reserves(self):
        # (c) Memory can still free while something runs: the pair waits and the
        # lane behind it does not jump it.
        running = [P.Running("X", "stock-minimal-3", False, 0.0)]
        d = self.decide(self.pair() + [tp.lane("S")], running, memory=P.MemoryReading(10.0))
        self.assertEqual((P.ACTION_WAIT, P.WAIT_MEMORY), (d.action, d.wait_kind))
        self.assertIn("pair G, two copies", d.reason)

    def test_an_unpaired_lane_from_idle_is_still_free(self):
        d = self.decide([tp.lane("S")], memory=P.MemoryReading(0.5, 0.5))
        self.assertEqual((P.ACTION_START, "S"), (d.action, d.lane_id))

    def test_single_lane_memory_text_is_unchanged(self):
        holds = P.memory_holds(P.MemoryReading(5.0), 1)
        self.assertEqual("free RAM 5.0 GB - new copy 4.5 GB = 0.5 GB < 1.5 GB", holds[0][1])
        self.assertEqual([], P.memory_holds(P.MemoryReading(0.1), 0))


class PairSelectionTests(unittest.TestCase):

    def test_a_selected_half_pulls_in_its_partner(self):
        specs = [pair_spec("PA"), tp.spec("S"), pair_spec("PB", role="B")]
        selected, added = P.complete_pairs([specs[2], specs[1]], specs)
        self.assertEqual(["PB", "PA", "S"], [s["id"] for s in selected])
        self.assertEqual(["PA"], added)

    def test_a_broken_pair_refuses_the_selection(self):
        specs = [pair_spec("PA"), tp.spec("S")]
        with self.assertRaises(P.SelectionError) as cm:
            P.complete_pairs([specs[0]], specs)
        self.assertIn("exactly one role A and one role B", str(cm.exception))
        excl = pair_spec("PB", role="B")
        excl["driver"]["steps"].insert(1, {"cmd": "UiAction", "args": {"op": "pointer"}})
        with self.assertRaises(P.SelectionError) as cm:
            P.complete_pairs([specs[0]], [specs[0], excl])
        self.assertIn("is exclusive", str(cm.exception))

    def test_unpaired_selection_is_untouched(self):
        specs = [tp.spec("A"), tp.spec("B")]
        self.assertEqual((specs, []), P.complete_pairs(specs, specs))

    def test_a_half_that_cannot_fly_here_takes_its_partner_out(self):
        a, b = pair_spec("PA"), pair_spec("PB", role="B")
        a["fixture"]["saveTemplate"] = "fixtures/local-saves/x"
        lanes, skipped = P.build_lanes([a, b, tp.spec("S")], {}, {}, lambda t: False)
        self.assertEqual(["S"], [l.lane_id for l in lanes])
        self.assertEqual(["PA", "PB"], [sid for sid, _ in skipped])
        self.assertIn("its pair G1", skipped[1][1])

    def test_slot_shortfall_and_partners(self):
        lanes = [plane("PA", "G", "A", profile="stock-tree"),
                 plane("PB", "G", "B", profile="stock-tree"), tp.lane("S")]
        self.assertEqual(["stock-tree"], P.pair_slot_shortfall(
            lanes, {"stock-tree": ["stock-tree"], "stock-minimal": ["stock-minimal"]}))
        self.assertEqual([], P.pair_slot_shortfall(lanes, {"stock-tree": ["a", "b"]}))
        self.assertEqual({"PA": "PB", "PB": "PA"}, P.pair_partners(lanes))


def oline(sid, verdict, attempts=1):
    return tp.outcome_line(sid, verdict, attempts)


class PairResumeTests(unittest.TestCase):
    PARTNERS = {"PA": "PB", "PB": "PA"}
    PLAN = ["PA", "PB", "S"]

    def remaining(self, *lines):
        return P.remaining_lane_ids(self.PLAN, list(lines), self.PARTNERS)

    def test_a_pair_re_flies_whole_when_one_half_is_unsettled(self):
        self.assertEqual(["PA", "PB"], self.remaining(oline("PA", "PASS"),
                                                      oline("PB", "INVALID"),
                                                      oline("S", "PASS")))
        self.assertEqual(["PA", "PB", "S"], self.remaining(oline("PA", "NO-REPORT")))

    def test_a_settled_finding_keeps_the_pair(self):
        # The finding is never flown away; its partner's loss is its consequence.
        self.assertEqual([], self.remaining(oline("PA", "PARSEK-FAIL"), oline("PB", "INVALID"),
                                            oline("S", "PASS")))

    def test_a_green_pair_is_settled(self):
        self.assertEqual(["S"], self.remaining(oline("PA", "PASS"), oline("PB", "PASS")))

    def test_a_dispatch_marker_keeps_a_stale_pass_from_settling_the_pair(self):
        # Flight 1: A INVALID at attempt 1, B PASS. The resume dispatches the pair
        # (one NOT-RUN marker per half, written before either child starts), A
        # reports PASS, and the runner dies before B reports. B's latest line is its
        # marker, not flight 1's PASS: the pair is not green and flies again.
        def marker(sid):
            return P.format_summary_line(P.pair_dispatch_outcome(sid, "s", "2026-11-17T10:00:00Z"))
        lines = [oline("PA", "INVALID"), oline("PB", "PASS"), oline("S", "PASS"),
                 marker("PA"), marker("PB"), oline("PA", "PASS")]
        self.assertEqual(["PA", "PB"], self.remaining(*lines))
        outcome, code, detail = P.selection_outcome(self.PLAN, lines, False)
        self.assertEqual((P.OUTCOME_RED, P.EXIT_RED), (outcome, code))
        self.assertIn("PASS=2 pending=1 of 3 (lanes left unflown)", detail)
        self.assertEqual("# totals PASS=2 pending=1 of 3", P.format_totals_line(self.PLAN, lines))
        # After a stop the same lines read STOPPED, as an unpaired stopped lane does.
        self.assertEqual((P.OUTCOME_STOPPED, P.EXIT_SKIPPED),
                         P.selection_outcome(self.PLAN, lines, True)[:2])
        self.assertIn("note=pair-dispatched", marker("PA"))

    def test_without_partners_the_rule_is_per_lane(self):
        lines = [oline("PA", "PASS"), oline("PB", "INVALID")]
        self.assertEqual(["PB", "S"], P.remaining_lane_ids(self.PLAN, lines))


class PairGuardTests(unittest.TestCase):

    def outcome(self, sid, verdict):
        return P.LaneOutcome("2026-11-17T10:00:00Z", verdict, sid, 1, 30, "stock-minimal",
                             ("run-" + sid,))

    def test_a_green_half_whose_partner_left_no_verdict_is_partner_lost(self):
        for partner in (self.outcome("PB", P.VERDICT_NO_REPORT),
                        self.outcome("PB", P.VERDICT_NOT_RUN), None):
            with self.subTest(partner=partner and partner.verdict):
                rewritten = P.pair_guard(self.outcome("PA", "PASS"), partner, "PB")
                self.assertIsNotNone(rewritten)
                self.assertEqual(("INVALID", 1), (rewritten.verdict, rewritten.attempts))
                self.assertIn("partner-lost: partner PB left", rewritten.note)
                self.assertFalse(P.lane_settled(rewritten.verdict, rewritten.attempts))

    def test_a_partner_that_ran_leaves_the_verdict_alone(self):
        for verdict in ("PASS", "INVALID", "PARSEK-FAIL", "KILLED"):
            with self.subTest(partner=verdict):
                self.assertIsNone(P.pair_guard(self.outcome("PA", "PASS"),
                                               self.outcome("PB", verdict), "PB"))
        self.assertIsNone(P.pair_guard(self.outcome("PA", "PARSEK-FAIL"),
                                       self.outcome("PB", "NO-REPORT"), "PB"))


# ---------------------------------------------------------------------------
# run.py --slot as a pair child, over the fake KSP
# ---------------------------------------------------------------------------


class PairChildSmokeTests(trs._SlotBox):
    RUN, GROUP = "pr1", "G1"

    def setUp(self):
        super().setUp()
        self.pair_dir = os.path.join(run.RESULTS_DIR, "parallel", self.RUN, "pairs", self.GROUP)
        self.shared = os.path.join(self.box, "automation", "rs", self.RUN, self.GROUP)
        os.makedirs(self.pair_dir)
        os.makedirs(self.shared)
        with open(os.path.join(self.shared, "from-partner.cfg"), "w") as fh:
            fh.write("x = 1\n")

    def spec(self, role="A", timeout=30, steps_after=()):
        spec = trs._make_spec(self.template, 30, 600)
        spec["id"] = "PAIR-" + role
        spec["pair"] = {"group": self.GROUP, "role": role}
        spec["retry"] = {"policy": "none"}
        spec["fixture"]["race"] = {"sharedFolder": hlib.RACE_SHARED_PAIR}
        steps = spec["driver"]["steps"]
        steps.insert(1, {"cmd": "SetSetting", "args": {"name": "x", "value": hlib.RACE_SHARED_TOKEN},
                         "expect": "OK"})
        steps.insert(1, {"harness": "barrier", "args": {"name": "one", "timeout": timeout}})
        return spec

    def fly(self, specs, rt, pair=True):
        args = mock.Mock(instance_dir=None, no_coverage=False, serial_verify=False,
                         keep_stop_request=False, tests_prebuilt=True)
        with trs._context(slot=trs.SLOT, umbrella_root=self.box) as ctx, \
                mock.patch.dict(os.environ, self.env, clear=True), \
                mock.patch.object(hlib, "validate_spec", lambda *a, **k: trs._Valid()), \
                mock.patch.object(run, "resolve_mission_schemas", lambda s, lg=None: (None, [])), \
                contextlib.redirect_stdout(io.StringIO()):
            if pair:
                ctx.pair_dir, ctx.pair_shared = self.pair_dir, self.shared
            rt.scope_ksp_to_instance = True
            code = run._run_slot_child(specs, specs, {}, self.box, args, rt, self.logger, "x")
        return code

    def arrive(self, role, name="one"):
        with open(os.path.join(self.pair_dir, hlib.barrier_arrival_file(name, role)), "w") as fh:
            fh.write("x\n")

    def commands(self):
        with open(os.path.join(self.instance, "parsek-test-commands.txt"), encoding="utf-8") as fh:
            return fh.read()

    def test_a_met_barrier_flies_on_and_the_lane_uses_the_pair_folder(self):
        self.arrive("B")
        rt = trs._PrebuiltRuntime("pass", launch_files={})
        code = self.fly([self.spec("A")], rt)
        log = self.log_text()
        self.assertEqual(0, code, log)
        row = self.results()["PAIR-A"][0]
        self.assertEqual("PASS", row["verdict"], row.get("note"))
        self.assertTrue(os.path.isfile(os.path.join(self.pair_dir, "one.A.arrived")))
        self.assertTrue(os.path.isfile(os.path.join(self.pair_dir, "A.ended")),
                        "the half marks itself ended once its drive is over")
        self.assertIn("value=%s" % run.encode_value(os.path.abspath(self.shared)), self.commands())
        self.assertIn("race shared folder is the pair's path=", log)
        self.assertIn("barrier name=one timeout=30 result=met", log)
        harness_rows = row["driver"]["harnessSteps"]
        self.assertEqual([("barrier", "one", True)],
                         [(r["harness"], r["name"], r["met"]) for r in harness_rows])
        snap = row["raceSnapshot"]
        self.assertTrue(snap["ran"], snap)
        self.assertTrue(os.path.isfile(os.path.join(run.RESULTS_DIR, snap["path"],
                                                    "from-partner.cfg")))
        self.assertTrue(os.path.isfile(os.path.join(self.shared, "from-partner.cfg")),
                        "a pair child never wipes the shared folder")

    def test_an_ended_partner_is_partner_lost_at_once_and_not_retried(self):
        with open(os.path.join(self.pair_dir, "B.ended"), "w") as fh:
            fh.write("gone\n")
        rt = trs._PrebuiltRuntime("pass")
        t0 = time.monotonic()
        code = self.fly([self.spec("A", timeout=30)], rt)
        self.assertLess(time.monotonic() - t0, 25, "the ended marker releases the barrier")
        self.assertEqual(1, code)
        rows = self.results()["PAIR-A"]
        self.assertEqual(1, len(rows), "never retried")
        self.assertEqual(("INVALID", hlib.PARTNER_LOST_SUBKIND),
                         (rows[0]["verdict"], rows[0]["subkind"]))
        self.assertEqual(1, rt.launch_count)
        log = self.log_text()
        self.assertIn("partner B ended without reaching barrier one", log)
        self.assertNotIn("value=%s" % run.encode_value(os.path.abspath(self.shared)),
                         self.commands(), "the drive stopped at the barrier")

    def test_a_partner_that_never_arrives_times_out(self):
        rt = trs._PrebuiltRuntime("pass")
        spec = self.spec("B", timeout=0.3)
        code = self.fly([spec], rt)
        self.assertEqual(1, code)
        row = self.results()["PAIR-B"][0]
        self.assertEqual(hlib.PARTNER_LOST_SUBKIND, row["subkind"])
        self.assertIn("did not reach barrier one within 0.3s", self.log_text())
        self.assertTrue(os.path.isfile(os.path.join(self.pair_dir, "B.ended")))

    def test_a_paired_lane_without_its_partner_is_refused_before_boot(self):
        rt = trs._PrebuiltRuntime("pass")
        code = self.fly([self.spec("A")], rt, pair=False)
        self.assertEqual(1, code)
        self.assertEqual(0, rt.launch_count)
        row = self.results()["PAIR-A"][0]
        self.assertEqual(("INVALID", hlib.PARTNER_LOST_SUBKIND), (row["verdict"], row["subkind"]))
        self.assertIn("flies only beside its partner", self.log_text())

    def test_an_unpaired_lane_ignores_the_pair_machinery(self):
        rt = trs._PrebuiltRuntime("pass")
        code = self.fly(self.specs("SLOT-A"), rt, pair=False)
        self.assertEqual(0, code)
        self.assertEqual([], os.listdir(self.pair_dir))

    def test_pair_args_refusal_reads_the_disk(self):
        with trs._context(slot=trs.SLOT, umbrella_root=self.box):
            self.assertIsNone(run._pair_args_refusal(self.pair_dir, self.shared,
                                                     [self.spec("A")], self.box))
            self.assertIn("is not a folder", run._pair_args_refusal(
                self.pair_dir + "x", self.shared, [self.spec("A")], self.box))
            self.assertIn("--pair-shared", run._pair_args_refusal(
                self.pair_dir, os.path.join(self.box, "automation"), [self.spec("A")], self.box))

    def test_the_cli_flags_go_together_and_need_a_slot(self):
        for argv in (["--id", "X", "--pair-dir", "a"],
                     ["--id", "X", "--pair-dir", "a", "--pair-shared", "b"]):
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()), \
                    self.assertRaises(SystemExit):
                run.run(argv)

    def test_pair_args_refusal_folds_the_spec_names_like_the_paths(self):
        # Windows folds every path through os.path.normcase (lower case, back
        # slashes) before the parts are compared; the group id and the pairs folder
        # name must be folded the same way, or every group with an upper-case
        # letter is refused on the PC. Simulated here so a Linux host reds too.
        run_name, group = "PR1-Upper", "PAIR-1"
        pair_dir = os.path.join(run.RESULTS_DIR, "parallel", run_name, "pairs", group)
        shared = os.path.join(self.box, "automation", "rs", run_name, group)
        other_dir = os.path.join(run.RESULTS_DIR, "parallel", run_name, "pairs", "PAIR-2")
        other_shared = os.path.join(self.box, "automation", "rs", "PR2-Upper", group)
        for path in (pair_dir, shared, other_dir, other_shared):
            os.makedirs(path)
        spec = self.spec("A")
        spec["pair"]["group"] = group

        def windows_normcase(s):
            return s.replace("/", "\\").lower()

        with trs._context(slot=trs.SLOT, umbrella_root=self.box), \
                mock.patch.object(run.os.path, "normcase", windows_normcase), \
                mock.patch.object(run.os, "sep", "\\"):
            self.assertIsNone(run._pair_args_refusal(pair_dir, shared, [spec], self.box))
            self.assertIn("--pair-dir", run._pair_args_refusal(other_dir, shared, [spec],
                                                               self.box))
            self.assertIn("--pair-shared", run._pair_args_refusal(pair_dir, other_shared,
                                                                  [spec], self.box))

    @unittest.skipIf(os.name == "nt", "symlink creation needs privileges on Windows")
    def test_a_linked_shared_folder_is_refused_at_staging(self):
        target = os.path.join(self.box, "elsewhere")
        os.makedirs(target)
        shutil.rmtree(self.shared)
        os.symlink(target, self.shared)
        self.arrive("B")
        rt = trs._PrebuiltRuntime("pass")
        code = self.fly([self.spec("A")], rt)
        self.assertEqual(1, code)
        self.assertEqual("staging", self.results()["PAIR-A"][0]["subkind"])
        self.assertEqual(0, rt.launch_count)

    @unittest.skipUnless(os.name == "nt", "a junction is a Windows folder link")
    def test_a_junction_shared_folder_is_refused_at_staging(self):
        # The Windows twin of the symlink cell: a junction needs no privilege.
        target = os.path.join(self.box, "elsewhere")
        os.makedirs(target)
        shutil.rmtree(self.shared)
        subprocess.run(["cmd", "/c", "mklink", "/J", self.shared, target], check=True,
                       capture_output=True)
        self.arrive("B")
        rt = trs._PrebuiltRuntime("pass")
        try:
            code = self.fly([self.spec("A")], rt)
        finally:
            # rmdir drops the junction itself, before tearDown's rmtree of the box.
            os.rmdir(self.shared)
        self.assertEqual(1, code)
        self.assertEqual("staging", self.results()["PAIR-A"][0]["subkind"])
        self.assertEqual(0, rt.launch_count)
        self.assertEqual([], os.listdir(target), "nothing written through the junction")


# ---------------------------------------------------------------------------
# The runner shell end to end: the real runner, the fake child, real barriers
# ---------------------------------------------------------------------------

PAIR_TOML = '''schema = 1
id = "%s"
tier = "operator"
instanceProfile = "%s"
[fixture]
saveTemplate = "fixtures/saves/fresh-career"
[fixture.race]
sharedFolder = "pair"
[pair]
group = "%s"
role = "%s"
[driver]
kind = "seam"
steps = [
  { cmd = "LoadGame", args = { save = "${runSave}", name = "persistent" } },
  { harness = "barrier", args = { name = "one", timeout = 60 } },
  { harness = "barrier", args = { name = "two", timeout = 60 } },
  { cmd = "FlushAndQuit" },
]
[retry]
policy = "none"
'''


class PairRunnerSmokeTests(unittest.TestCase):
    setUp_base = tp.ShellSmokeTests.setUp
    tearDown = tp.ShellSmokeTests.tearDown
    make_slot = tp.ShellSmokeTests.make_slot
    ctx = tp.ShellSmokeTests.ctx
    main = tp.ShellSmokeTests.main
    run_dir = tp.ShellSmokeTests.run_dir
    summary = tp.ShellSmokeTests.summary
    child_records = tp.ShellSmokeTests.child_records
    D1, D2 = tp.ShellSmokeTests.D1, tp.ShellSmokeTests.D2

    def setUp(self):
        self.setUp_base()
        self.write_pair("PA", "PB", "G1")
        os.environ["FAKE_CHILD_SLEEP"] = "0.1"
        self.script({})

    def write_pair(self, a, b, group, profile="stock-minimal"):
        for sid, role in ((a, "A"), (b, "B")):
            with open(os.path.join(self.harness, "scenarios", sid + ".toml"), "w") as fh:
                fh.write(PAIR_TOML % (sid, profile, group, role))

    def script(self, overrides, timeout=20):
        scripts = {"PA": {"role": "A", "barriers": ["one", "two"], "timeout": timeout},
                   "PB": {"role": "B", "barriers": ["one", "two"], "timeout": timeout}}
        scripts.update(overrides)
        os.environ["FAKE_CHILD_PAIRS"] = json.dumps(scripts)

    def events(self, name):
        with open(os.path.join(self.run_dir(name), "events.jsonl")) as fh:
            return [json.loads(l) for l in fh if l.strip()]

    def records(self, lane_id):
        return [r for r in self.child_records() if r["lane"] == lane_id]

    def log(self, name):
        return tp._read(os.path.join(self.run_dir(name), "runner.log"))

    def test_a_pair_starts_together_and_shares_its_folder(self):
        shared = os.path.join(self.umbrella, "automation", "rs", "p1", "G1")
        os.makedirs(shared)
        with open(os.path.join(shared, "stale.txt"), "w") as fh:
            fh.write("left by an earlier run\n")
        barrier_dir = os.path.join(self.run_dir("p1"), "pairs", "G1")
        os.makedirs(barrier_dir)
        with open(os.path.join(barrier_dir, "one.B.arrived"), "w") as fh:
            fh.write("stale\n")
        code = self.main("run", "--name", "p1", "--ids", "C,PB", "--slots", "2")
        self.assertEqual(0, code, "\n".join(self.console))
        self.assertEqual("# totals PASS=3 pending=0 of 3", self.summary("p1")[-1])
        dispatch = [e for e in self.events("p1") if e["kind"] == "dispatch"]
        # The pair (selected through B) is one unit, A first, both starts back to back.
        paired = [e for e in dispatch if e.get("pair")]
        self.assertEqual(["PA", "PB"], [e["lane"] for e in paired])
        self.assertEqual(["G1/A", "G1/B"], [e.get("pair") for e in paired])
        self.assertEqual({"stock-minimal", "stock-minimal-2"}, {e["slot"] for e in paired})
        self.assertEqual(paired[0]["seq"] + 1, paired[1]["seq"])
        self.assertEqual({"C", "PA", "PB"}, {e["lane"] for e in dispatch})
        a, b = self.records("PA")[0], self.records("PB")[0]
        for rec in (a, b):
            argv = rec["argv"]
            self.assertEqual(os.path.join(self.umbrella, "automation", "rs", "p1", "G1"),
                             argv[argv.index("--pair-shared") + 1])
            self.assertEqual(barrier_dir, argv[argv.index("--pair-dir") + 1])
            self.assertNotIn("stale.txt", rec["pair"]["sharedAtStart"], "wiped at dispatch")
            self.assertEqual([True, True], [x["met"] for x in rec["pair"]["barriers"]])
        self.assertEqual([True, True], b["pair"]["sawToken"],
                         "B sees what A wrote before the first barrier")
        self.assertTrue(os.path.isfile(os.path.join(barrier_dir, "A.ended")))
        self.assertTrue(os.path.isfile(os.path.join(barrier_dir, "B.ended")))
        self.assertIn("added PA: the partner of a selected paired lane", "\n".join(self.console))
        self.assertIn("PAIR G1 role A", "\n".join(self.console))

    def test_a_crashed_partner_leaves_the_survivor_partner_lost_then_resume_re_flies_both(self):
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"PB": "-"})
        t0 = time.monotonic()
        code = self.main("run", "--name", "p2", "--ids", "PA", "--slots", "2")
        self.assertLess(time.monotonic() - t0, 15, "the ended marker, not the timeout")
        self.assertEqual(1, code)
        lines = self.summary("p2")
        self.assertTrue(any(" INVALID PA " in l for l in lines), lines)
        self.assertTrue(any(" NO-REPORT PB " in l for l in lines), lines)
        a = self.records("PA")[0]["pair"]["barriers"][0]
        self.assertFalse(a["met"])
        self.assertIn("partner B ended without reaching barrier one", a["reason"])
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({})
        code = self.main("resume", "--name", "p2")
        self.assertEqual(0, code, "\n".join(self.console))
        self.assertEqual((2, 2), (len(self.records("PA")), len(self.records("PB"))))
        self.assertEqual("# totals PASS=2 pending=0 of 2", self.summary("p2")[-1])

    def test_the_memory_guard_charges_the_second_copy_of_a_pair_from_idle(self):
        # Memory is read with nothing running, and a reading that cannot hold the
        # second copy ends the run with the pair pending; a resume with memory free
        # flies it.
        ctx = self.ctx()
        ctx.memory_fn = lambda: P.MemoryReading(5.0, 12.0)
        code = pr.main(["run", "--name", "p10", "--ids", "PA", "--slots", "2"], ctx=ctx)
        self.assertEqual(P.EXIT_RED, code, "\n".join(self.console))
        self.assertIn("pair G1, two copies", self.log("p10"))
        self.assertEqual([], self.child_records())
        code = self.main("resume", "--name", "p10")
        self.assertEqual(0, code, "\n".join(self.console))
        self.assertEqual("# totals PASS=2 pending=0 of 2", self.summary("p10")[-1])

    def test_an_interrupted_re_flight_never_reads_green_from_a_stale_pass(self):
        # Flight 1: A INVALID (after meeting both barriers), B PASS.
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"PA": "INVALID"})
        self.assertEqual(1, self.main("run", "--name", "p9", "--ids", "PA", "--slots", "2"))
        # Flight 2 (resume re-flies both): the runner dies right after A's new PASS
        # line, while B (slowed) is still flying; B is killed with the runner.
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({})
        os.environ["FAKE_CHILD_SLEEPS"] = json.dumps({"PB": 3})
        real = pr.RunFolder.write_summary
        armed = {"on": True}

        def write_summary(folder, lines, *a, **k):
            real(folder, lines, *a, **k)
            latest = P.latest_outcomes(lines)
            if armed["on"] and "PA" in latest and latest["PA"].verdict == "PASS":
                armed["on"] = False
                raise KeyboardInterrupt()
        with mock.patch.object(pr.RunFolder, "write_summary", write_summary):
            try:
                code = self.main("resume", "--name", "p9")
            except KeyboardInterrupt:
                code = None
        self.assertNotEqual(0, code)
        lines = self.summary("p9")
        self.assertEqual("NOT-RUN", P.latest_outcomes(lines)["PB"].verdict, lines)
        self.assertEqual("# totals PASS=1 pending=1 of 2", lines[-1])
        # Flight 3: the pair is not settled, so both halves fly again.
        os.environ.pop("FAKE_CHILD_SLEEPS")
        before = (len(self.records("PA")), len(self.records("PB")))
        code = self.main("resume", "--name", "p9")
        self.assertEqual(0, code, "\n".join(self.console))
        self.assertEqual((before[0] + 1, before[1] + 1),
                         (len(self.records("PA")), len(self.records("PB"))))
        self.assertEqual("# totals PASS=2 pending=0 of 2", self.summary("p9")[-1])

    def test_a_green_half_whose_partner_never_ran_is_not_reported_pass(self):
        # Backstop: a half whose (fake) script meets no barrier reports PASS by
        # itself; its partner left no report, so the runner rewrites it.
        self.script({"PA": {"role": "A", "barriers": [], "timeout": 20}})
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"PB": "-"})
        code = self.main("run", "--name", "p3", "--ids", "PA", "--slots", "2")
        self.assertEqual(1, code)
        lines = self.summary("p3")
        latest = P.latest_outcomes(lines)
        self.assertEqual("INVALID", latest["PA"].verdict, lines)
        self.assertIn("partner-lost", latest["PA"].note)
        self.assertNotIn("PASS=", lines[-1])

    def test_one_slot_leaves_the_pair_unflown_then_set_slots_and_resume(self):
        code = self.main("run", "--name", "p4", "--ids", "PA", "--slots", "1")
        self.assertEqual(1, code)
        self.assertEqual([], self.child_records())
        self.assertIn("a pair needs two slots at once", self.log("p4"))
        self.assertEqual(0, self.main("set-slots", "--name", "p4", "2"))
        code = self.main("resume", "--name", "p4")
        self.assertEqual(0, code, "\n".join(self.console))
        self.assertEqual("# totals PASS=2 pending=0 of 2", self.summary("p4")[-1])

    def test_a_profile_with_one_copy_cannot_fly_a_pair(self):
        self.write_pair("TA", "TB", "T1", profile="stock-tree")
        self.make_slot("stock-tree", b"same-dll")
        code = self.main("run", "--name", "p5", "--ids", "TA", "--slots", "2")
        self.assertEqual(P.EXIT_NEEDS_PROVISION, code)
        self.assertIn("--profile <p> --copy 2", "\n".join(self.console))
        self.assertEqual([], self.child_records())

    def test_a_stop_while_the_pair_flies_lets_both_halves_finish(self):
        os.environ["FAKE_CHILD_STOP_AFTER"] = "PA"
        os.environ["FAKE_CHILD_STOP_REQUEST"] = os.path.join(self.harness, "results",
                                                             hlib.STOP_REQUEST_FILENAME)
        self.script({"PB": {"role": "B", "barriers": ["one", "two"], "timeout": 20}})
        os.environ["FAKE_CHILD_SLEEP"] = "0.3"
        code = self.main("run", "--name", "p6", "--ids", "PA,C", "--slots", "2")
        self.assertEqual(P.EXIT_SKIPPED, code, "\n".join(self.console))
        self.assertEqual(["PA", "PB"], sorted(r["lane"] for r in self.child_records()))
        latest = P.latest_outcomes(self.summary("p6"))
        self.assertEqual(("PASS", "PASS"), (latest["PA"].verdict, latest["PB"].verdict))
        os.environ.pop("FAKE_CHILD_STOP_AFTER")
        code = self.main("resume", "--name", "p6")
        self.assertEqual(0, code, "\n".join(self.console))
        self.assertEqual(["C", "PA", "PB"], sorted(r["lane"] for r in self.child_records()))

    def test_a_broken_pair_refuses_the_whole_run(self):
        os.remove(os.path.join(self.harness, "scenarios", "PB.toml"))
        code = self.main("run", "--name", "p7", "--ids", "PA,C")
        self.assertEqual(P.EXIT_USAGE, code)
        self.assertIn("exactly one role A and one role B", "\n".join(self.console))
        self.assertEqual([], self.child_records())

    def test_a_pair_memory_held_from_idle_ends_the_run_instead_of_looping(self):
        # (b) end to end: the runner exits with the pair pending and no dispatch
        # marker; bounded, so a regression fails instead of hanging the suite.
        ctx = self.ctx()
        ctx.memory_fn = lambda: P.MemoryReading(5.5, 12.0)
        box = {}
        worker = threading.Thread(target=lambda: box.setdefault(
            "code", pr.main(["run", "--name", "p11", "--ids", "PA", "--slots", "2"], ctx=ctx)),
            daemon=True)
        worker.start()
        worker.join(30)
        self.assertFalse(worker.is_alive(), "the runner is still waiting on memory")
        self.assertEqual(P.EXIT_RED, box.get("code"))
        self.assertEqual([], self.child_records())
        lines = self.summary("p11")
        self.assertEqual(["# totals no-results pending=2 of 2"], lines)
        self.assertIn("nothing runs to free memory", self.log("p11"))

    def test_a_stopped_pair_reads_stopped_with_its_halves_pending(self):
        # Both halves honour a stop before flying: no verdict line follows their
        # dispatch markers, and the run reads STOPPED (exit 3), not RED.
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"PA": "STOP", "PB": "STOP"})
        code = self.main("run", "--name", "p12", "--ids", "PA", "--slots", "2")
        self.assertEqual(P.EXIT_SKIPPED, code, "\n".join(self.console))
        lines = self.summary("p12")
        self.assertEqual("# totals no-results pending=2 of 2", lines[-1])
        os.environ.pop("FAKE_CHILD_VERDICTS")
        self.assertEqual(0, self.main("resume", "--name", "p12"))
        self.assertEqual("# totals PASS=2 pending=0 of 2", self.summary("p12")[-1])

    def test_status_counts_a_flying_half_as_running_not_done(self):
        events = [{"kind": "start", "epoch": 1},
                  {"kind": "dispatch", "seq": 1, "lane": "PA", "slot": "s1", "epoch": 2,
                   "pair": "G1/A"},
                  {"kind": "dispatch", "seq": 2, "lane": "PB", "slot": "s2", "epoch": 2,
                   "pair": "G1/B"}]
        markers = [P.format_summary_line(P.pair_dispatch_outcome(sid, "s", "2026-11-17T10:00:00Z"))
                   for sid in ("PA", "PB")]
        lines = P.format_status({"name": "x", "lanes": [{"id": "PA"}, {"id": "PB"}]}, 2, False,
                                events, markers, 10, "running")
        text = "\n".join(lines)
        self.assertIn("running=2 done: no-results", text)
        self.assertNotIn("NOT-RUN", text)

    def test_status_names_the_pair_of_a_running_half(self):
        events = [{"kind": "start", "epoch": 1},
                  {"kind": "dispatch", "seq": 1, "lane": "PA", "slot": "s1", "epoch": 2,
                   "pair": "G1/A"}]
        lines = P.format_status({"name": "x", "lanes": [{"id": "PA"}, {"id": "PB"}]}, 2, False,
                                events, [], 10, "running")
        self.assertIn("running slot=s1 lane=PA elapsed=8s pair=G1/A", lines)


if __name__ == "__main__":
    unittest.main()
