"""Tests for the parallel runner: the pure decisions (``lib/parallel.py``) and
the thin shell (``tools/parallel_runner.py``) driven end to end with a FAKE
``run.py`` child (``_fake_parallel_child.py``), temp slot dirs and a temp lock.

No KSP, no registry, no network. Runnable with the stdlib runner::

    cd harness && python -m unittest discover -s lib -q
"""

import io
import json
import os
import re
import shutil
import sys
import tempfile
import time
import tomllib
import unittest
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
WORKTREE_ROOT = os.path.dirname(HARNESS_ROOT)
TOOLS = os.path.join(HARNESS_ROOT, "tools")
PROVISION = os.path.join(HARNESS_ROOT, "provision")
for _p in (HERE, TOOLS, PROVISION):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import machinelock  # noqa: E402
import parallel  # noqa: E402
import parallel_runner as pr  # noqa: E402
import slots as slotslib  # noqa: E402
import tier_runner  # noqa: E402

P = parallel


def _read(path):
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read()


def lane(i, p50=100.0, profile="stock-minimal", excl=None):
    return P.Lane(i, profile, "daily", p50, P.P50_SOURCE_DURATION, excl)


def spec(sid, tier="daily", profile="stock-minimal", steps=None, runtime=None, template=None):
    s = {"id": sid, "tier": tier, "instanceProfile": profile,
         "driver": {"kind": "seam", "steps": steps or []}}
    if runtime is not None:
        s["runtime"] = runtime
    if template is not None:
        s["fixture"] = {"saveTemplate": template}
    return s


class SelectionTests(unittest.TestCase):
    def test_id_lines_and_ids_arg(self):
        self.assertEqual(P.parse_id_lines("# c\n\nA\n B \nA\n"), ["A", "B"])
        self.assertEqual(P.split_ids_arg(" a, b,,c "), ["a", "b", "c"])

    def test_ids_first_then_tiers_in_spec_order_once(self):
        specs = [spec("X", "nightly"), spec("A"), spec("B"), spec("C", "nightly")]
        got = [s["id"] for s in P.resolve_selection(specs, ["C", "B"], ["nightly"])]
        self.assertEqual(got, ["C", "B", "X"])

    def test_unknown_ids_and_tiers_refused_together(self):
        with self.assertRaises(P.SelectionError) as cm:
            P.resolve_selection([spec("A")], ["A", "nope", "zz"], ["dialy"])
        msg = str(cm.exception)
        self.assertIn("nope, zz", msg)
        self.assertIn("dialy", msg)

    def test_run_name(self):
        self.assertTrue(P.valid_run_name("nightly-1.b_2"))
        for bad in ["", "../x", "a/b", "-x", "a" * 65]:
            self.assertFalse(P.valid_run_name(bad), bad)


class ExclusiveTests(unittest.TestCase):
    def test_spec_key_wins(self):
        self.assertIn("spec key", P.exclusive_reason(spec("A", runtime={"exclusive": True})))
        self.assertIsNone(P.exclusive_reason(spec("A", runtime={"exclusive": False})))

    def test_pointer_focus_hover_and_category_steps(self):
        cases = [
            ({"cmd": "UiAction", "args": {"op": "pointer", "park": "true"}}, "op=pointer"),
            ({"cmd": "UiAction", "args": {"op": "click", "focus": "true"}}, "focus=true"),
            ({"cmd": "StockScreen", "args": {"screen": "rnd", "act": "hover"}}, "act=hover"),
            ({"cmd": "RunTests", "args": {"category": "Flight,DisabledHoverEcho"}},
             "DisabledHoverEcho"),
        ]
        for step, needle in cases:
            reason = P.exclusive_reason(spec("A", steps=[{"cmd": "LoadGame"}, step]))
            self.assertIsNotNone(reason, step)
            self.assertIn(needle, reason)
            self.assertIn("step 1", reason)

    def test_ordinary_steps_are_not_exclusive(self):
        steps = [{"cmd": "UiAction", "args": {"op": "open", "focus": "false"}},
                 {"cmd": "StockScreen", "args": {"act": "open"}},
                 {"cmd": "RunTests", "args": {"category": "DisabledHoverEchoNot"}}]
        self.assertIsNone(P.exclusive_reason(spec("A", steps=steps)))

    def test_hlib_accepts_the_key_and_refuses_a_non_bool(self):
        self.assertEqual(hlib.validate_screen_resolution({"exclusive": True}), [])
        errs = hlib.validate_screen_resolution({"exclusive": "yes"})
        self.assertTrue(any("runtime.exclusive" in e for e in errs), errs)

    def test_committed_lt1_and_gui7_carry_the_key(self):
        for name in ("LT-1-long-tail-flight.toml", "GUI-7-census-flight-recording.toml"):
            with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
                s = tomllib.load(fh)
            self.assertIs(s["runtime"]["exclusive"], True, name)
            self.assertIn("spec key", P.exclusive_reason(s))

    def test_cursor_moving_test_categories_match_the_csharp(self):
        """Every in-game test file that calls SetCursorPos declares a category in
        POINTER_TEST_CATEGORIES, so a RunTests lane naming it reads exclusive."""
        root = os.path.join(WORKTREE_ROOT, "Source", "Parsek", "InGameTests")
        found = set()
        for dirpath, _d, files in os.walk(root):
            for f in files:
                if not f.endswith(".cs"):
                    continue
                text = _read(os.path.join(dirpath, f))
                if "SetCursorPos" in text:
                    found.update(re.findall(r'InGameTest\(\s*Category\s*=\s*"([^"]+)"', text))
        self.assertTrue(found)
        self.assertEqual(found - set(P.POINTER_TEST_CATEGORIES), set())

    def test_stockscreen_hover_is_a_csharp_act_token(self):
        path = os.path.join(WORKTREE_ROOT, "Source", "Parsek", "TestCommands",
                            "TestCommandStockScreen.cs")
        text = _read(path)
        m = re.search(r"ActTokens\s*=\s*\{([^}]*)\}", text)
        self.assertIsNotNone(m)
        self.assertIn('"hover"', m.group(1))


class EstimateAndPlanTests(unittest.TestCase):
    def test_index_medians_pass_only(self):
        rows = [{"lane": "A", "verdict": "PASS", "wall": 10}, {"lane": "A", "verdict": "PASS",
                                                                 "wall": 30},
                {"lane": "A", "verdict": "INVALID", "wall": 999}, {"lane": "B", "verdict": "PASS",
                                                                     "wall": True}]
        self.assertEqual(P.index_pass_medians(rows), {"A": 20.0})

    def test_p50_sources_in_order(self):
        self.assertEqual(P.lane_p50("A", {"A": {"p50": 50}}, {"A": 9}), (50.0, "duration.json"))
        self.assertEqual(P.lane_p50("A", {"A": {"p50": None}}, {"A": 9}), (9.0, "run-index"))
        self.assertEqual(P.lane_p50("A", {}, {}), (120.0, "default"))

    def test_build_lanes_skips_unstaged_local_fixture(self):
        specs = [spec("A"), spec("G", template="fixtures/local-saves/x")]
        lanes, skipped = P.build_lanes(specs, {}, {}, lambda t: False)
        self.assertEqual([l.lane_id for l in lanes], ["A"])
        self.assertEqual(skipped[0][0], "G")
        lanes, skipped = P.build_lanes(specs, {}, {}, lambda t: True)
        self.assertEqual(len(lanes), 2)

    def test_longest_first_exclusive_last(self):
        lanes = [lane("a", 10), lane("x", 500, excl="r"), lane("b", 90), lane("c", 90)]
        self.assertEqual([l.lane_id for l in P.order_lanes(lanes)], ["b", "c", "a", "x"])
        fly, left = P.partition_exclusive(P.order_lanes(lanes), False)
        self.assertEqual([l.lane_id for l in left], ["x"])
        self.assertEqual(len(P.partition_exclusive(lanes, True)[0]), 4)

    def test_lane_json_round_trip(self):
        for l in (lane("a"), lane("b", excl="why")):
            self.assertEqual(P.Lane.from_json(json.loads(json.dumps(l.to_json()))), l)

    def test_slots_by_profile_and_missing(self):
        by = P.slots_by_profile(["stock-minimal", "stock-minimal-2", "stock-tree", "junk"])
        self.assertEqual(by, {"stock-minimal": ["stock-minimal", "stock-minimal-2"],
                              "stock-tree": ["stock-tree"]})
        self.assertEqual(P.missing_profiles([lane("a", profile="modded-compat")], by),
                         ["modded-compat"])

    def test_uniform_dll(self):
        self.assertIsNone(P.check_uniform_dll([("stock-minimal", "aa"), ("stock-minimal-2", "aa")]))
        msg = P.check_uniform_dll([("stock-minimal-2", "bb"), ("stock-minimal", "aa"),
                                   ("stock-minimal-3", "aa")])
        self.assertIn("stock-minimal-2", msg)
        self.assertNotIn("stock-minimal-3", msg)
        self.assertIn("without a deployed", P.check_uniform_dll([("stock-minimal", None)]))


class DispatchTests(unittest.TestCase):
    BY = {"stock-minimal": ["stock-minimal", "stock-minimal-2", "stock-minimal-3"],
          "stock-tree": ["stock-tree"]}
    PLENTY = P.MemoryReading(20.0, 12.0)

    def decide(self, pending, running=(), slots=2, stop=False, mem=None, now=1000.0, last=None):
        return P.decide_next(pending, list(running), self.BY, slots, stop,
                             mem if mem is not None else self.PLENTY, now, last)

    def run_(self, i, slot, excl=False):
        return P.Running(i, slot, excl, 0.0)

    def test_first_lane_takes_copy_one(self):
        d = self.decide([lane("a"), lane("b")])
        self.assertEqual((d.action, d.lane_id, d.slot, d.window),
                         ("start", "a", "stock-minimal", None))

    def test_next_lane_takes_a_free_copy(self):
        d = self.decide([lane("b")], [self.run_("a", "stock-minimal")])
        self.assertEqual((d.action, d.slot), ("start", "stock-minimal-2"))

    def test_slot_cap_and_lowering_never_kills(self):
        running = [self.run_("a", "stock-minimal"), self.run_("b", "stock-minimal-2")]
        d = self.decide([lane("c")], running, slots=2)
        self.assertEqual((d.action, d.wait_kind), ("wait", "slots"))
        d = self.decide([lane("c")], running, slots=1)
        self.assertEqual(d.action, "wait")
        self.assertEqual(self.decide([lane("c")], running, slots=3).action, "start")

    def test_profile_slot_busy_skips_to_a_lane_that_fits(self):
        running = [self.run_("t", "stock-tree")]
        d = self.decide([lane("t2", profile="stock-tree"), lane("m")], running, slots=3)
        self.assertEqual((d.action, d.lane_id), ("start", "m"))
        d = self.decide([lane("t2", profile="stock-tree")], running, slots=3)
        self.assertEqual(d.wait_kind, "slot-busy")

    def test_dispatch_gap_after_a_start_is_the_launch_spacing(self):
        self.assertEqual(P.DISPATCH_GAP_SECONDS, slotslib.DEFAULT_LAUNCH_SPACING_SECONDS)
        running = [self.run_("a", "stock-minimal")]
        d = self.decide([lane("b")], running, now=1010.0, last=1000.0)
        self.assertEqual(d.wait_kind, "dispatch-gap")
        self.assertIn("dispatch gap", d.reason)
        self.assertEqual(self.decide([lane("b")], running, now=1016.0, last=1000.0).action,
                         "start")

    def test_lanes_shorter_than_a_minute_fill_every_slot(self):
        # PC run 2026-11-14: daily lanes took 46-90 s, so a 45 s settle after every
        # start capped dispatch at one start per ~46 s and slot 3 never got a lane.
        # 40 s after the second start, with plenty of memory, the third starts.
        running = [P.Running("a", "stock-minimal", False, 900.0),
                   P.Running("b", "stock-minimal-2", False, 960.0)]
        d = self.decide([lane("c")], running, slots=3, now=1000.0, last=960.0,
                        mem=P.MemoryReading(20.0, 12.0))
        self.assertEqual((d.action, d.slot), ("start", "stock-minimal-3"))

    def test_boot_ramp(self):
        self.assertEqual(P.BOOT_WINDOW_SECONDS, 60.0)
        self.assertAlmostEqual(P.boot_remaining_share(1000.0, 1000.0), 1.0)
        self.assertAlmostEqual(P.boot_remaining_share(970.0, 1000.0), 0.5)
        self.assertEqual(P.boot_remaining_share(940.0, 1000.0), 0.0)
        self.assertEqual(P.boot_remaining_share(100.0, 1000.0), 0.0)
        # A start stamp ahead of the clock counts as one whole copy, never more.
        self.assertEqual(P.boot_remaining_share(1010.0, 1000.0), 1.0)
        self.assertEqual(P.boot_remaining_share(990.0, 1000.0, boot_window_seconds=0), 0.0)
        running = [P.Running("a", "s", False, 950.0), P.Running("b", "s2", False, 980.0)]
        self.assertAlmostEqual(P.booting_share(running, 1000.0), 1.0 / 6 + 2.0 / 3)
        self.assertEqual(P.booting_count(running, 1000.0), 2)

    # Cases from the owner's PC (2026-11-14, daily-par-3, 3 slots, 1280x720
    # windows): one copy peaks at 4.25 GB RAM and about 1 GB VRAM.

    def test_pc_a_two_booted_copies_at_the_lowest_free_ram_start_a_third(self):
        # 6.07 - 4.5 = 1.57 >= 1.5.
        running = [P.Running("a", "stock-minimal", False, 800.0),
                   P.Running("b", "stock-minimal-2", False, 900.0)]
        d = self.decide([lane("c")], running, slots=3, now=1000.0, last=900.0,
                        mem=P.MemoryReading(6.07, 12.0))
        self.assertEqual((d.action, d.slot), ("start", "stock-minimal-3"), d.reason)

    def test_pc_b_the_daily_par_3_hold_now_starts(self):
        # The old rule held this reading ("10.4 - 2 x 4.5 = 1.4 < 6.0"). Started
        # 50 s and 20 s ago, the copies still owe 1/6 + 2/3 = 0.83 of a footprint:
        # RAM 10.4 - 3.75 - 4.5 = 2.15 >= 1.5, VRAM 13.7 - 1.67 - 2.0 = 10.03 >= 1.5.
        running = [P.Running("a", "stock-minimal", False, 950.0),
                   P.Running("b", "stock-minimal-2", False, 980.0)]
        d = self.decide([lane("c")], running, slots=3, now=1000.0, last=980.0,
                        mem=P.MemoryReading(10.4, 13.7))
        self.assertEqual((d.action, d.slot), ("start", "stock-minimal-3"), d.reason)
        self.assertEqual(P.memory_holds(P.MemoryReading(10.4, 13.7), 2,
                                        P.booting_share(running, 1000.0)), [])
        # The same reading with both copies just launched still holds on RAM.
        fresh = [P.Running("a", "stock-minimal", False, 1000.0),
                 P.Running("b", "stock-minimal-2", False, 1000.0)]
        d = self.decide([lane("c")], fresh, slots=3, now=1000.0, last=None,
                        mem=P.MemoryReading(10.4, 13.7))
        self.assertEqual(d.wait_kind, "memory")
        self.assertIn("free RAM 10.4 GB - booting 9.0 GB - new copy 4.5 GB = -3.1 GB < 1.5 GB",
                      d.reason)
        self.assertIn("2 running, 2 booting", d.reason)
        self.assertNotIn("VRAM", d.reason)

    def test_pc_c_low_free_ram_holds_naming_ram(self):
        running = [P.Running("a", "stock-minimal", False, 800.0),
                   P.Running("b", "stock-minimal-2", False, 900.0)]
        d = self.decide([lane("c")], running, slots=3, now=1000.0, last=900.0,
                        mem=P.MemoryReading(5.5, 12.0))
        self.assertEqual(d.wait_kind, "memory")
        self.assertIn("free RAM 5.5 GB - new copy 4.5 GB = 1.0 GB < 1.5 GB", d.reason)
        self.assertNotIn("booting 0", d.reason)
        self.assertNotIn("VRAM", d.reason)

    def test_pc_d_low_free_vram_holds_naming_vram(self):
        running = [P.Running("a", "stock-minimal", False, 800.0),
                   P.Running("b", "stock-minimal-2", False, 900.0)]
        d = self.decide([lane("c")], running, slots=3, now=1000.0, last=900.0,
                        mem=P.MemoryReading(20.0, 3.0))
        self.assertEqual(d.wait_kind, "memory")
        self.assertIn("free VRAM 3.0 GB - new copy 2.0 GB = 1.0 GB < 1.5 GB", d.reason)
        self.assertNotIn("free RAM", d.reason)

    def test_memory_wait_log_key_ignores_the_boot_ramp(self):
        # The ramp moves the reason's numbers every poll; the log key moves only
        # with the reading, which readings hold, and the running / booting counts.
        running = [P.Running("a", "stock-minimal", False, 990.0),
                   P.Running("b", "stock-minimal-2", False, 995.0)]
        mem = P.MemoryReading(9.0, 13.0)
        d1 = self.decide([lane("c")], running, slots=3, now=1000.0, mem=mem)
        d2 = self.decide([lane("c")], running, slots=3, now=1002.0, mem=mem)
        self.assertEqual((d1.wait_kind, d2.wait_kind), ("memory", "memory"))
        self.assertNotEqual(d1.reason, d2.reason)
        self.assertEqual(d1.log_key, d2.log_key)
        d3 = self.decide([lane("c")], running, slots=3, now=1002.0,
                         mem=P.MemoryReading(8.9, 13.0))
        self.assertNotEqual(d1.log_key, d3.log_key)
        self.assertEqual(self.decide([lane("c")], [], mem=mem).log_key, "")

    def test_memory_deferral_rules(self):
        self.assertEqual((P.MIN_FREE_RAM_GB, P.MIN_FREE_VRAM_GB), (1.5, 1.5))
        self.assertEqual((P.COPY_FOOTPRINT_RAM_GB, P.COPY_FOOTPRINT_VRAM_GB), (4.5, 2.0))
        # The first lane is never held; an unavailable reading never holds.
        self.assertIsNone(P.memory_deferral(P.MemoryReading(1.0, 1.0), 0, booting_copies=3))
        self.assertIsNone(P.memory_deferral(None, 2, booting_copies=2))
        self.assertIsNone(P.memory_deferral(P.MemoryReading(None, None), 2, booting_copies=2))
        # Floor met exactly: starts. One booting copy's full footprint on top: held.
        self.assertIsNone(P.memory_deferral(P.MemoryReading(6.0, 3.5), 1))
        held = P.memory_deferral(P.MemoryReading(6.0, 3.5), 1, booting_copies=1.0)
        self.assertIn("free RAM 6.0 GB - booting 4.5 GB - new copy 4.5 GB = -3.0 GB < 1.5 GB",
                      held)
        self.assertIn("free VRAM 3.5 GB - booting 2.0 GB - new copy 2.0 GB = -0.5 GB < 1.5 GB",
                      held)
        self.assertEqual(P.memory_deferral(P.MemoryReading(6.0, 3.5), 1, booting_copies=-1),
                         None)
        self.assertIsNone(P.memory_deferral(P.MemoryReading(5.0, 5.0), 1, booting_copies=0.5,
                                            min_free_ram_gb=0.0, min_free_vram_gb=0.0,
                                            footprint_ram_gb=3.0, footprint_vram_gb=3.0))

    def test_memory_guard_holds_extra_lanes_only(self):
        low = P.MemoryReading(4.0, None)
        self.assertEqual(self.decide([lane("a")], mem=low).action, "start")
        d = self.decide([lane("b")], [self.run_("a", "stock-minimal")], mem=low)
        self.assertEqual(d.wait_kind, "memory")
        self.assertIn("free RAM 4.0 GB", d.reason)
        d = self.decide([lane("b")], [self.run_("a", "stock-minimal")],
                        mem=P.MemoryReading(10.0, 3.0))
        self.assertIn("VRAM", d.reason)
        d = self.decide([lane("b")], [self.run_("a", "stock-minimal")],
                        mem=P.MemoryReading(None, None))
        self.assertEqual(d.action, "start")

    def test_stop_drains(self):
        self.assertEqual(self.decide([lane("b")], [self.run_("a", "stock-minimal")],
                                     stop=True).wait_kind, "stop")
        self.assertEqual(self.decide([lane("b")], stop=True).action, "done")
        self.assertEqual(self.decide([]).action, "done")
        self.assertEqual(self.decide([], [self.run_("a", "stock-minimal")]).wait_kind, "drain")

    def test_exclusive_waits_then_flies_alone_normal_window(self):
        x = lane("x", excl="op=pointer")
        d = self.decide([x], [self.run_("a", "stock-minimal")], slots=3)
        self.assertEqual(d.wait_kind, "exclusive")
        d = self.decide([x])
        self.assertEqual((d.action, d.lane_id, d.slot, d.window),
                         ("start", "x", "stock-minimal", "normal"))
        d = self.decide([lane("b")], [self.run_("x", "stock-minimal", True)], slots=3)
        self.assertEqual(d.wait_kind, "exclusive")

    def test_normal_lanes_go_before_exclusive_even_if_listed_after(self):
        d = self.decide([lane("x", excl="r"), lane("a")])
        self.assertEqual(d.lane_id, "a")

    def test_nvidia_smi_parse(self):
        self.assertAlmostEqual(P.parse_nvidia_smi_free_gb("16376, 10232\n8000, 1\n"), 6.0)
        self.assertIsNone(P.parse_nvidia_smi_free_gb("N/A, N/A"))
        self.assertIsNone(P.parse_nvidia_smi_free_gb(""))


class WindowTests(unittest.TestCase):
    G = [uuid.UUID(int=i + 1) for i in range(3)]

    def blob(self, guids):
        return b"".join(g.bytes_le for g in guids)

    def test_parse_ids_bytes_le(self):
        g = uuid.UUID("01020304-0506-0708-090a-0b0c0d0e0f10")
        raw = bytes([4, 3, 2, 1, 6, 5, 8, 7, 9, 10, 11, 12, 13, 14, 15, 16])
        self.assertEqual(P.parse_virtual_desktop_ids(raw), [str(g)])
        self.assertEqual(P.guid_from_bytes(raw), str(g))
        self.assertEqual(P.parse_virtual_desktop_ids(raw + b"\x00" * 5), [str(g)])
        self.assertEqual(P.parse_virtual_desktop_ids(None), [])
        self.assertIsNone(P.guid_from_bytes(raw[:15]))

    def test_desktop_targets_last_non_current(self):
        ids = P.parse_virtual_desktop_ids(self.blob(self.G))
        r = P.resolve_window_mode("desktop", ids, str(self.G[0]))
        self.assertEqual((r.mode, r.desktop_id), ("desktop", str(self.G[2])))
        r = P.resolve_window_mode("desktop", ids, str(self.G[2]).upper())
        self.assertEqual(r.desktop_id, str(self.G[1]))
        r = P.resolve_window_mode("desktop", ids, None)
        self.assertEqual(r.desktop_id, str(self.G[2]))

    def test_one_desktop_falls_back_to_minimize_with_hint(self):
        r = P.resolve_window_mode("desktop", [str(self.G[0])], str(self.G[0]))
        self.assertEqual((r.mode, r.desktop_id), ("minimize", None))
        self.assertIn("Win+Ctrl+D", r.hint)
        self.assertEqual(P.resolve_window_mode("desktop", [], None).mode, "minimize")

    def test_explicit_modes_pass_through(self):
        for m in ("minimize", "normal"):
            self.assertEqual(P.resolve_window_mode(m, [str(g) for g in self.G], None).mode, m)


class ChildTests(unittest.TestCase):
    def test_argv_names_exactly_the_run_py_flags(self):
        argv = P.child_argv("py", "run.py", "stock-minimal-2", "ids", "rep", "desktop", "g")
        self.assertEqual(argv, ["py", "run.py", "--slot", "stock-minimal-2", "--ids-file", "ids",
                                "--tests-prebuilt", "--no-coverage", "--keep-stop-request",
                                "--report-json", "rep", "--window", "desktop",
                                "--desktop-id", "g"])
        self.assertNotIn("--desktop-id", P.child_argv("py", "r", "s", "i", "o", "normal", "g"))

    def test_env(self):
        env = P.child_env({"A": "1"}, "tok", 42)
        self.assertEqual((env["A"], env[P.RUNNER_TOKEN_ENV], env[P.RUNNER_PID_ENV]),
                         ("1", "tok", "42"))

    def test_report_parse_run_py_document(self):
        doc = {"schema": "parsek-run-report/1", "slot": "stock-minimal-2", "exitCode": 1,
               "lanes": [{"id": "B", "verdict": "PASS", "attempts": 1, "runIds": ["x"]},
                         {"id": "A", "verdict": "INVALID", "subkind": "boot-crash",
                          "attempts": 2, "runIds": ["r1", "r1_a2"], "wallSeconds": 90}]}
        self.assertEqual(P.parse_child_report(doc, "A"),
                         P.ChildReport("INVALID", 2, ("r1", "r1_a2"), 90.0))
        self.assertIsNone(P.parse_child_report(doc, "C"))
        nr = {"lanes": [{"id": "A", "verdict": "NOT-RUN", "attempts": 0, "runIds": []}]}
        self.assertEqual(P.parse_child_report(nr, "A").verdict, "NOT-RUN")
        self.assertFalse(P.lane_settled("NOT-RUN", 1))

    def test_report_parse(self):
        obj = {"A": {"verdict": "PASS", "attempts": 2, "runIds": ["r1", "r2"]}}
        self.assertEqual(P.parse_child_report(obj, "A"), P.ChildReport("PASS", 2, ("r1", "r2")))
        self.assertIsNone(P.parse_child_report(obj, "B"))
        self.assertIsNone(P.parse_child_report([], "A"))
        self.assertEqual(P.parse_child_report({"A": {"verdict": "INVALID", "attempts": "x"}},
                                              "A").attempts, 1)


def outcome_line(sid, verdict, attempts=1, ended="2026-11-14T10:00:00Z"):
    return P.format_summary_line(P.LaneOutcome(ended, verdict, sid, attempts, 61, "stock-minimal",
                                               ("r1",)))


class SummaryTests(unittest.TestCase):
    def test_round_trip_and_tier_runner_tally_reads_it(self):
        o = P.LaneOutcome("2026-11-14T10:00:00Z", "PASS", "A", 2, 61, "stock-minimal-2",
                          ("r1", "r2"), "flaked then")
        line = P.format_summary_line(o)
        self.assertEqual(line, "2026-11-14T10:00:00Z PASS A attempts=2 wall=61s "
                               "slot=stock-minimal-2 runIds=r1,r2 note=flaked_then")
        back = P.parse_summary_line(line)
        self.assertEqual((back.verdict, back.lane_id, back.attempts, back.wall_seconds, back.slot,
                          back.run_ids), ("PASS", "A", 2, 61, "stock-minimal-2", ("r1", "r2")))
        text = P.render_summary([line, outcome_line("B", "KILLED")], ["A", "B", "C"])
        self.assertEqual(tier_runner.tally_verdicts(text.splitlines()[:-1]),
                         {"PASS": 1, "KILLED": 1})
        self.assertTrue(text.splitlines()[-1].startswith("# totals"))
        self.assertIsNone(P.parse_summary_line(text.splitlines()[-1]))

    def test_settled_matches_tier_runner(self):
        for verdict in ("PASS", "EXPECTED-FAIL", "PARSEK-FAIL", "INVALID", "KILLED", "XPASS"):
            for attempts in (1, 2):
                self.assertEqual(P.lane_settled(verdict, attempts),
                                 tier_runner.full_run_done(verdict, attempts), (verdict, attempts))
        self.assertFalse(P.lane_settled("NO-REPORT", 2))

    def test_remaining_uses_latest_line(self):
        lines = [outcome_line("A", "INVALID"), outcome_line("B", "PASS"),
                 outcome_line("A", "PASS"), outcome_line("C", "NO-REPORT")]
        self.assertEqual(P.remaining_lane_ids(["A", "B", "C", "D"], lines), ["C", "D"])

    def test_exit_rule_mirrors_run_py(self):
        ids = ["A", "B"]
        green = [outcome_line("A", "PASS"), outcome_line("B", "EXPECTED-FAIL")]
        self.assertEqual(P.selection_outcome(ids, green, False)[:2], ("GREEN", 0))
        for red in ("PARSEK-FAIL", "KILLED", "XPASS"):
            self.assertEqual(P.selection_outcome(ids, [green[0], outcome_line("B", red)],
                                                 False)[1], 1, red)
        self.assertEqual(P.selection_outcome(ids, [green[0], outcome_line("B", "INVALID", 2)],
                                             False)[1], 1)
        self.assertEqual(P.selection_outcome(ids, [green[0]], True)[:2], ("STOPPED", 3))
        self.assertEqual(P.selection_outcome(ids, [green[0]], False)[:2], ("RED", 1))
        self.assertEqual(P.selection_outcome(ids, [green[0], outcome_line("B", "PARSEK-FAIL")],
                                             True)[1], 1)
        self.assertEqual(P.selection_outcome([], [], False)[1], 2)

    def test_totals_line(self):
        line = P.format_totals_line(["A", "B"], [outcome_line("A", "PASS")], ["X"])
        self.assertEqual(line, "# totals PASS=1 pending=1 of 2 notFlown=X")


class ControlAndStatusTests(unittest.TestCase):
    def test_clamp_slots(self):
        self.assertEqual([P.clamp_slots(v) for v in (2, "3", " 1 ", 0, 9, "x", True, 2.5, None)],
                         [2, 3, 1, None, None, None, None, None, None])

    def test_read_control(self):
        self.assertEqual(P.read_control_obj({"slots": 3, "stop": True}, 2), (3, True))
        self.assertEqual(P.read_control_obj({"slots": 99, "stop": "yes"}, 2), (2, False))
        self.assertEqual(P.read_control_obj(None, 2), (2, False))

    def test_running_from_events_and_status(self):
        events = [{"kind": "start"},
                  {"kind": "dispatch", "seq": 1, "lane": "A", "slot": "stock-minimal",
                   "epoch": 100.0},
                  {"kind": "dispatch", "seq": 2, "lane": "B", "slot": "stock-minimal-2",
                   "epoch": 150.0},
                  {"kind": "finish", "seq": 1, "lane": "A"},
                  {"kind": "wait", "waitKind": "memory", "reason": "free RAM 4.0 GB - new copy 4.5 GB = -0.5 GB < 1.5 GB",
                   "epoch": 160.0}]
        self.assertEqual([e["lane"] for e in P.running_from_events(events)], ["B"])
        plan = {"name": "n", "windowMode": "desktop",
                "lanes": [lane(i).to_json() for i in "ABCD"],
                "notFlown": [{"id": "X", "reason": "exclusive"}]}
        out = P.format_status(plan, 3, False, events, [outcome_line("A", "PASS")], 250.0,
                              "running (pid 7)")
        self.assertIn("runner running (pid 7), slots=3", out[0])
        self.assertIn("running slot=stock-minimal-2 lane=B elapsed=1m40s", out)
        self.assertIn("queued=2 running=1 done: PASS=1", out)
        self.assertTrue(any(l.startswith("memory deferral: free RAM") for l in out))
        self.assertTrue(any("not flown: X" in l for l in out))
        self.assertEqual(P.running_from_events(events + [{"kind": "start"}]), [])


class RunPyBridgeNamesTests(unittest.TestCase):
    def test_the_helpers_the_bridge_calls_exist_in_run_py(self):
        run = pr.RunPyBridge(HARNESS_ROOT).run_module()
        for name in ("_prebuild_tests_for_selection", "Runtime"):
            self.assertTrue(hasattr(run, name), name)
        ri = pr._load_by_path("run_index", os.path.join(TOOLS, "run_index.py"))
        for name in ("append_runs", "format_append_counts"):
            self.assertTrue(hasattr(ri, name), name)


# ---------------------------------------------------------------------------
# Shell smoke: the real runner, a fake run.py child.
# ---------------------------------------------------------------------------

FAKE_CHILD = os.path.join(HERE, "_fake_parallel_child.py")


class FakeBridge:
    def __init__(self, build_ok=True):
        self.calls = []
        self.build_ok = build_ok

    def build_tests(self, log):
        self.calls.append(("build",))
        return self.build_ok

    def refresh_coverage(self, token, log):
        self.calls.append(("coverage", bool(token)))

    def sweep_results(self, token, log):
        self.calls.append(("sweep", bool(token)))

    def append_run_index(self, umbrella, run_ids, log):
        self.calls.append(("index", tuple(sorted(run_ids))))


class FakePrefs:
    def __init__(self):
        self.calls = []

    def snapshot(self, umbrella, log):
        self.calls.append("snapshot")

    def restore(self, umbrella, log):
        self.calls.append("restore")


SPEC_TEMPLATE = '''id = "%s"
tier = "%s"
instanceProfile = "%s"
[driver]
kind = "seam"
steps = [%s]
'''


class ShellSmokeTests(unittest.TestCase):
    D1, D2 = str(uuid.UUID(int=11)), str(uuid.UUID(int=22))

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parallel-smoke-")
        self.harness = os.path.join(self.tmp, "umbrella", "wt", "harness")
        self.umbrella = os.path.join(self.tmp, "umbrella")
        os.makedirs(os.path.join(self.harness, "scenarios"))
        os.makedirs(os.path.join(self.harness, "results"))
        os.makedirs(os.path.join(self.harness, "coverage"))
        with open(os.path.join(self.harness, "coverage", "duration.json"), "w") as fh:
            json.dump({"scenarios": {"A": {"p50": 300}, "B": {"p50": 200}}}, fh)
        for sid, tier, steps in (("A", "daily", ""), ("B", "daily", ""), ("C", "daily", ""),
                                 ("X", "nightly", '{ cmd = "UiAction", args = { op = "pointer", '
                                                  'park = "true" } }')):
            with open(os.path.join(self.harness, "scenarios", sid + ".toml"), "w") as fh:
                fh.write(SPEC_TEMPLATE % (sid, tier, "stock-minimal", steps))
        for slot in ("stock-minimal", "stock-minimal-2"):
            self.make_slot(slot, b"same-dll")
        self.state = os.path.join(self.tmp, "child-state")
        os.makedirs(self.state)
        self.bridge = FakeBridge()
        self.prefs = FakePrefs()
        self.console = []
        self.env_backup = dict(os.environ)
        os.environ["FAKE_CHILD_STATE"] = self.state
        os.environ["FAKE_CHILD_UMBRELLA"] = self.umbrella
        os.environ["FAKE_CHILD_SLEEP"] = "0.4"
        os.environ.pop("FAKE_CHILD_VERDICTS", None)

    def tearDown(self):
        os.environ.clear()
        os.environ.update(self.env_backup)
        shutil.rmtree(self.tmp, ignore_errors=True)

    def make_slot(self, slot, dll):
        d = os.path.join(self.umbrella, "automation", slot)
        os.makedirs(os.path.join(d, "GameData", "Parsek", "Plugins"), exist_ok=True)
        open(os.path.join(d, "KSP_x64.exe"), "wb").close()
        with open(os.path.join(d, "GameData", "Parsek", "Plugins", "Parsek.dll"), "wb") as fh:
            fh.write(dll)

    def ctx(self, stdin="", gap=0.0):
        return pr.Context(harness_root=self.harness, umbrella_root=self.umbrella,
                          run_py=FAKE_CHILD, poll_seconds=0.05, heartbeat_seconds=0.2,
                          dispatch_gap_seconds=gap, bridge=self.bridge, unity_prefs=self.prefs,
                          memory_fn=lambda: P.MemoryReading(32.0, 12.0),
                          desktops_fn=lambda: ([self.D1, self.D2], self.D1),
                          stdin_text=lambda: stdin, console=self.console.append)

    def main(self, *argv, stdin="", gap=0.0):
        return pr.main(list(argv), ctx=self.ctx(stdin, gap))

    def run_dir(self, name):
        return os.path.join(self.harness, "results", "parallel", name)

    def summary(self, name):
        with open(os.path.join(self.run_dir(name), "summary.txt")) as fh:
            return fh.read().splitlines()

    def child_records(self):
        out = []
        for f in sorted(os.listdir(self.state)):
            if f.startswith("argv-"):
                with open(os.path.join(self.state, f)) as fh:
                    out.append(json.load(fh))
        return out

    def test_happy_path_two_slots(self):
        code = self.main("run", "--name", "r1", "--ids", "C,A", "--ids-file", "-",
                         "--slots", "2", stdin="# lanes\nB\n", gap=0.2)
        self.assertEqual(code, 0, "\n".join(self.console))
        lines = self.summary("r1")
        self.assertEqual(len(lines), 4)
        self.assertEqual(lines[-1], "# totals PASS=3 pending=0 of 3")
        recs = self.child_records()
        self.assertEqual(sorted(r["lane"] for r in recs), ["A", "B", "C"])
        # Longest p50 first: A (300) then B (200) then C (default 120).
        with open(os.path.join(self.run_dir("r1"), "events.jsonl")) as fh:
            events = [json.loads(l) for l in fh if l.strip()]
        self.assertEqual([e["lane"] for e in events if e["kind"] == "dispatch"],
                         ["A", "B", "C"])
        self.assertEqual([e["kind"] for e in events][0], "start")
        self.assertEqual(events[-1]["kind"], "end")
        # The dispatch gap is a wait like any other: one event and one log line.
        gaps = [e for e in events if e["kind"] == "wait" and e["waitKind"] == "dispatch-gap"]
        self.assertTrue(gaps, events)
        with open(os.path.join(self.run_dir("r1"), "runner.log")) as fh:
            self.assertIn("dispatch-gap: the last start was", fh.read())
        for r in recs:
            argv = r["argv"]
            for flag in ("--tests-prebuilt", "--no-coverage", "--keep-stop-request"):
                self.assertIn(flag, argv)
            self.assertEqual(argv[argv.index("--window") + 1], "desktop")
            self.assertEqual(argv[argv.index("--desktop-id") + 1], self.D2)
            self.assertTrue(r["tokenMatches"], r)
            self.assertEqual(r["runnerPid"], os.getpid())
        self.assertEqual(max(r["concurrent"] for r in recs), 2)
        self.assertEqual({r["slot"] for r in recs}, {"stock-minimal", "stock-minimal-2"})
        self.assertFalse(os.path.exists(machinelock.lock_path_for(self.umbrella)))
        self.assertEqual(self.bridge.calls[0], ("build",))
        self.assertIn(("index", ("run-A", "run-B", "run-C")), self.bridge.calls)
        self.assertEqual([c for c in self.bridge.calls if c[0] in ("coverage", "sweep")],
                         [("coverage", True), ("sweep", True)])
        self.assertEqual(self.prefs.calls, ["snapshot", "restore"])
        status = []
        pr.main(["status", "--name", "r1"], ctx=pr.Context(
            harness_root=self.harness, umbrella_root=self.umbrella, console=status.append))
        self.assertIn("finished", status[0])
        self.assertIn("queued=0 running=0 done: PASS=3", status)

    def test_red_lane_no_report_and_resume(self):
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"A": "PARSEK-FAIL", "B": "-"})
        code = self.main("run", "--name", "r2", "--ids", "A,B,C", "--slots", "1")
        self.assertEqual(code, 1)
        lines = self.summary("r2")
        self.assertTrue(any(" PARSEK-FAIL A " in l for l in lines))
        self.assertTrue(any(" NO-REPORT B " in l and "note=exit=3" in l for l in lines), lines)
        self.assertEqual(max(r["concurrent"] for r in self.child_records()), 1)
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({})
        code = self.main("resume", "--name", "r2")
        self.assertEqual(code, 1)  # A stays settled red
        recs = self.child_records()
        self.assertEqual(sorted(r["lane"] for r in recs), ["A", "B", "B", "C"])
        self.assertEqual(self.summary("r2")[-1], "# totals PASS=2 PARSEK-FAIL=1 pending=0 of 3")

    def test_exclusive_skipped_then_included_last_alone(self):
        code = self.main("run", "--name", "r3", "--tier", "daily", "--tier", "nightly")
        self.assertEqual(code, 0)
        self.assertNotIn("X", [r["lane"] for r in self.child_records()])
        self.assertIn("notFlown=X", self.summary("r3")[-1])
        code = self.main("resume", "--name", "r3", "--include-exclusive")
        self.assertEqual(code, 0)
        recs = self.child_records()
        x = [r for r in recs if r["lane"] == "X"]
        self.assertEqual(len(x), 1)
        self.assertEqual(x[0]["argv"][x[0]["argv"].index("--window") + 1], "normal")
        self.assertEqual(x[0]["concurrent"], 1)

    def test_stop_request_mid_run_drops_pending_lanes(self):
        os.environ["FAKE_CHILD_STOP_AFTER"] = "A"
        os.environ["FAKE_CHILD_STOP_REQUEST"] = os.path.join(self.harness, "results",
                                                             "stop-request")
        code = self.main("run", "--name", "r4", "--ids", "A,B", "--slots", "1")
        self.assertEqual(code, 3)
        self.assertEqual([r["lane"] for r in self.child_records()], ["A"])
        self.assertFalse(os.path.exists(os.environ["FAKE_CHILD_STOP_REQUEST"]))
        with open(os.path.join(self.run_dir("r4"), "control.json")) as fh:
            self.assertTrue(json.load(fh)["stop"])
        os.environ.pop("FAKE_CHILD_STOP_AFTER")
        self.assertEqual(self.main("resume", "--name", "r4"), 0)
        self.assertEqual(sorted(r["lane"] for r in self.child_records()), ["A", "B"])

    def test_stale_stop_request_is_cleared_at_run_and_resume_start(self):
        path = os.path.join(self.harness, "results", "stop-request")
        with open(path, "w") as fh:
            fh.write("stop\n")
        code = self.main("run", "--name", "r7", "--ids", "A,B", "--slots", "1")
        self.assertEqual(code, 0, "\n".join(self.console))
        self.assertFalse(os.path.exists(path))
        self.assertEqual(sorted(r["lane"] for r in self.child_records()), ["A", "B"])
        self.assertTrue(any("removed a stale stop request" in l for l in self.console),
                        self.console)
        # A resume clears one too (tier_runner's --resume rule).
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"B": "-"})
        self.assertEqual(self.main("run", "--name", "r8", "--ids", "A,B", "--slots", "1"), 1)
        with open(path, "w") as fh:
            fh.write("stop\n")
        os.environ["FAKE_CHILD_VERDICTS"] = "{}"
        self.console.clear()
        self.assertEqual(self.main("resume", "--name", "r8"), 0, "\n".join(self.console))
        self.assertFalse(os.path.exists(path))
        self.assertTrue(any("removed a stale stop request" in l for l in self.console))

    def test_stop_request_of_a_live_lock_holder_is_left_alone(self):
        path = os.path.join(self.harness, "results", "stop-request")
        with open(path, "w") as fh:
            fh.write("stop\n")
        lock = machinelock.lock_path_for(self.umbrella)
        os.makedirs(os.path.dirname(lock), exist_ok=True)
        with open(lock, "w") as fh:
            json.dump({"pid": os.getppid(), "timestamp": time.time(), "selection": "--tier x"}, fh)
        self.assertEqual(self.main("run", "--name", "r9", "--ids", "A"), 3)
        self.assertTrue(os.path.exists(path))

    def test_child_that_honoured_a_stop_leaves_its_lane_pending(self):
        os.environ["FAKE_CHILD_VERDICTS"] = json.dumps({"A": "STOP"})
        code = self.main("run", "--name", "r6", "--ids", "A,B", "--slots", "1")
        self.assertEqual(code, 3)
        self.assertEqual([r["lane"] for r in self.child_records()], ["A"])
        self.assertEqual(self.summary("r6")[-1], "# totals no-results pending=2 of 2")
        os.environ["FAKE_CHILD_VERDICTS"] = "{}"
        self.assertEqual(self.main("resume", "--name", "r6"), 0)

    def test_set_slots_and_stop_commands(self):
        self.main("run", "--name", "r5", "--ids", "A", "--slots", "2")
        self.assertEqual(self.main("set-slots", "--name", "r5", "3"), 0)
        self.assertEqual(self.main("stop", "--name", "r5"), 0)
        with open(os.path.join(self.run_dir("r5"), "control.json")) as fh:
            ctl = json.load(fh)
        self.assertEqual((ctl["slots"], ctl["stop"]), (3, True))
        self.assertEqual(self.main("set-slots", "--name", "r5", "0"), 2)
        self.assertEqual(self.main("stop", "--name", "nope"), 2)

    def test_refusals(self):
        self.assertEqual(self.main("run", "--name", "u", "--ids", "A,ZZ"), 2)
        self.assertTrue(any("ZZ" in l for l in self.console))
        self.assertEqual(self.main("run", "--name", "u", "--tier", "dialy"), 2)
        self.make_slot("stock-minimal-2", b"other-dll")
        self.assertEqual(self.main("run", "--name", "d", "--ids", "A"), 2)
        self.assertTrue(any("stock-minimal-2" in l and "different Parsek.dll" in l
                            for l in self.console))
        self.assertEqual(self.child_records(), [])
        self.assertFalse(os.path.exists(machinelock.lock_path_for(self.umbrella)))

    def test_lock_held_by_a_live_holder_skips(self):
        path = machinelock.lock_path_for(self.umbrella)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w") as fh:
            json.dump({"pid": os.getppid(), "timestamp": time.time(), "selection": "--tier x"}, fh)
        self.assertEqual(self.main("run", "--name", "l", "--ids", "A"), 3)
        self.assertEqual(self.child_records(), [])
        self.assertTrue(os.path.exists(path))

    def test_build_failure_starts_nothing(self):
        self.bridge.build_ok = False
        self.assertEqual(self.main("run", "--name", "b", "--ids", "A"), 1)
        self.assertEqual(self.child_records(), [])
        self.assertEqual(self.prefs.calls, ["snapshot", "restore"])

    def test_dry_run_writes_nothing(self):
        self.assertEqual(self.main("run", "--name", "dr", "--ids", "A", "--dry-run"), 0)
        self.assertFalse(os.path.exists(self.run_dir("dr")))
        self.assertTrue(any("window=desktop" in l for l in self.console))


if __name__ == "__main__":
    unittest.main()
