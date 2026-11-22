"""Unit tests for watchlane.py (the attended hold mode and the watch-lane
generator) plus the end-to-end hold smoke over the fake KSP and the source-sync
cell that keeps the tree-handle vocabulary equal to the seam's C# twin."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import tomllib
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(HERE)
REPO = os.path.dirname(HARNESS)
for _p in (HERE, HARNESS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import runindex  # noqa: E402
import watchlane as wl  # noqa: E402


def _spec(steps, watch=None, kind="seam"):
    spec = {"id": "X", "driver": {"kind": kind, "steps": steps}}
    if watch is not None:
        spec["watch"] = watch
    return spec


LOAD = {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"}, "expect": "OK"}
QUIT = {"cmd": "FlushAndQuit", "expect": "OK"}


class HandleOffsetTests(unittest.TestCase):
    """${ref.field+N}: the watch lanes' 'a few seconds after the captured UT'."""

    def test_offset_adds_to_a_numeric_field(self):
        store = {"slp": {"relaunchUt": "5336958314.5075293", "tree": "abc"}}
        out, subs, unresolved = hlib.substitute_handle_refs("${slp.relaunchUt+5}", store)
        self.assertEqual([], unresolved)
        self.assertAlmostEqual(5336958319.5075293, float(out), places=4)
        self.assertEqual("${slp.relaunchUt+5}", subs[0]["ref"])
        out, _s, _u = hlib.substitute_handle_refs("${slp.relaunchUt-0.5}", store)
        self.assertAlmostEqual(5336958314.0075293, float(out), places=4)

    def test_offset_on_a_non_number_is_unresolved(self):
        store = {"slp": {"tree": "abc"}}
        out, _s, unresolved = hlib.substitute_handle_refs("${slp.tree+5}", store)
        self.assertEqual(hlib.UNRESOLVED_HANDLE_NO_SUCH_FIELD, unresolved[0]["kind"])

    def test_the_static_scan_accepts_the_offset_form(self):
        self.assertEqual([], hlib.find_malformed_handle_tokens("${slp.relaunchUt+5}"))
        self.assertEqual([("${slp.relaunchUt+5}", "slp", "relaunchUt")],
                         hlib.find_handle_refs("${slp.relaunchUt+5}"))
        self.assertTrue(hlib.find_malformed_handle_tokens("${slp.relaunchUt+x}"))


class HoldPlanTests(unittest.TestCase):
    def test_default_hold_point_is_the_first_flushandquit(self):
        steps = [LOAD, {"cmd": "MissionConfig", "expect": "OK"}, QUIT]
        plan = wl.plan_hold(_spec(steps))
        self.assertEqual(2, plan.hold_index)
        self.assertEqual("FlushAndQuit", plan.source)
        self.assertEqual(((2, "FlushAndQuit"),), plan.skipped)
        self.assertEqual(2, plan.setup_count)

    def test_hold_before_names_a_label(self):
        steps = [LOAD, {"cmd": "WarpToUT", "label": "late", "expect": "OK"},
                 {"cmd": "CaptureScreenshot", "expect": "OK"}, QUIT]
        plan = wl.plan_hold(_spec(steps, {"holdBefore": "late"}))
        self.assertEqual(1, plan.hold_index)
        self.assertEqual("holdBefore", plan.source)
        self.assertEqual([1, 2, 3], [i for i, _c in plan.skipped])

    def test_no_quit_holds_after_the_last_step(self):
        plan = wl.plan_hold(_spec([LOAD, {"cmd": "EnterMapView", "expect": "OK"}]))
        self.assertEqual(2, plan.hold_index)
        self.assertEqual("end", plan.source)
        self.assertEqual((), plan.skipped)

    def test_refusals(self):
        self.assertIsNone(wl.hold_refusal(_spec([LOAD, QUIT])))
        self.assertIn("seam", wl.hold_refusal(_spec([LOAD], kind="autopilot")))
        self.assertIn("mission", wl.hold_refusal(_spec([LOAD, {"phase": "mission"}])))
        self.assertIn("no steps", wl.hold_refusal(_spec([])))
        self.assertIn("names no step label",
                      wl.hold_refusal(_spec([LOAD, QUIT], {"holdBefore": "nope"})))
        auto = _spec([LOAD])
        auto["driver"]["autorun"] = {"tests": "X"}
        self.assertIn("autorun", wl.hold_refusal(auto))


class HoldEndTests(unittest.TestCase):
    def test_exit_outranks_a_stop_request(self):
        self.assertEqual(wl.END_OWNER_CLOSED, wl.decide_hold_end(5, 60, True, True))

    def test_stop_request_and_ceiling(self):
        self.assertEqual(wl.END_STOP_REQUEST, wl.decide_hold_end(5, 60, False, True))
        self.assertEqual(wl.END_CEILING, wl.decide_hold_end(60, 60, False, False))
        self.assertIsNone(wl.decide_hold_end(59.9, 60, False, False))

    def test_zero_ceiling_holds_forever(self):
        self.assertIsNone(wl.decide_hold_end(10 ** 7, 0, False, False))


class HoldClassifyTests(unittest.TestCase):
    def test_owner_closed_complete_is_observed(self):
        v, sub, note = wl.classify_hold(wl.END_OWNER_CLOSED, True)
        self.assertEqual("OBSERVED", v)
        self.assertEqual("", sub)
        self.assertIn("owner closed", note)

    def test_never_pass(self):
        for reason in wl.HOLD_END_REASONS:
            for complete in (True, False):
                v, _s, _n = wl.classify_hold(reason, complete)
                self.assertNotEqual(hlib.VERDICT_PASS, v)
                self.assertIn(v, ("OBSERVED", "INVALID"))

    def test_incomplete_setup_is_named(self):
        v, sub, note = wl.classify_hold(wl.END_OWNER_CLOSED, False)
        self.assertEqual(("OBSERVED", wl.SUBKIND_SETUP_INCOMPLETE), (v, sub))
        self.assertIn("setup incomplete", note)

    def test_boot_failures_are_invalid(self):
        self.assertEqual(("INVALID", "boot-crash"), wl.classify_hold(wl.END_BOOT_CRASH, False)[:2])
        self.assertEqual(("INVALID", "boot-hang"), wl.classify_hold(wl.END_BOOT_HANG, False)[:2])

    def test_observed_literal_is_hlibs(self):
        self.assertEqual(hlib.HOLD_VERDICT_OBSERVED, wl.VERDICT_OBSERVED)
        self.assertNotIn(wl.VERDICT_OBSERVED, hlib.VERDICTS)

    def test_hold_block_shape(self):
        plan = wl.plan_hold(_spec([LOAD, QUIT]))
        block = wl.build_hold_block(plan, wl.END_OWNER_CLOSED, True, 12.4, None, 240)
        self.assertEqual(1, block["holdIndex"])
        self.assertEqual(12, block["heldSeconds"])
        self.assertTrue(block["setupComplete"])
        self.assertEqual([{"index": 1, "cmd": "FlushAndQuit"}], block["skippedSteps"])


class HoldResultExclusionTests(unittest.TestCase):
    def test_is_hold_result(self):
        self.assertTrue(hlib.is_hold_result({"mode": "hold"}))
        self.assertFalse(hlib.is_hold_result({"mode": "other"}))
        self.assertFalse(hlib.is_hold_result({}))
        self.assertFalse(hlib.is_hold_result(None))

    def test_run_index_skips_a_hold_result(self):
        row, reason = runindex.build_row("WL-1_2026-11-22_1200", {
            "mode": "hold", "verdict": "OBSERVED", "scenarioId": "WL-1"}, None, None)
        self.assertIsNone(row)
        self.assertIn("hold", reason)


SFS = """GAME
{
	FLIGHTSTATE
	{
	}
	SCENARIO
	{
		name = ParsekScenario
		RECORDING_TREE
		{
			id = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
			treeName = Kerbal X
			autoGeneratedRootGroupName = Kerbal X
			RECORDING
			{
				recordingId = r1
				vesselName = Kerbal X
				explicitStartUT = 10
				explicitEndUT = 200
			}
		}
		RECORDING_TREE
		{
			id = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
			treeName = Kerbal X
			autoGeneratedRootGroupName = Kerbal X #2
		}
		RECORDING_TREE
		{
			id = cccccccccccccccccccccccccccccccc
			treeName = Duna Rocket
			autoGeneratedRootGroupName = Duna Rocket
		}
		RECORDING_TREE
		{
			id = dddddddddddddddddddddddddddddddd
			treeName = Live
			isActive = True
		}
		MISSION
		{
			treeId = cccccccccccccccccccccccccccccccc
			name = Duna Express
		}
		MISSION
		{
			treeId = cccccccccccccccccccccccccccccccc
			name = Duna Express (clone)
		}
	}
}
"""


class TreeHandleTests(unittest.TestCase):
    def setUp(self):
        self.trees, err = wl.read_save_trees(SFS)
        self.assertEqual("", err)

    def test_reads_committed_trees_in_order_with_display_names(self):
        self.assertEqual(["a" * 32, "b" * 32, "c" * 32], [t.tree_id for t in self.trees])
        self.assertEqual(["Kerbal X", "Kerbal X #2", "Duna Express"],
                         [t.display_name for t in self.trees])
        self.assertEqual((10.0, 200.0), (self.trees[0].start_ut, self.trees[0].end_ut))

    def test_first_latest_id(self):
        self.assertEqual(("a" * 32, None), wl.resolve_tree_handle("first", self.trees))
        self.assertEqual(("c" * 32, None), wl.resolve_tree_handle("LATEST", self.trees))
        self.assertEqual(("b" * 32, None), wl.resolve_tree_handle("b" * 32, self.trees))

    def test_name_matches_the_original_mission_case_insensitively(self):
        self.assertEqual(("c" * 32, None), wl.resolve_tree_handle("name:duna express", self.trees))
        self.assertEqual(("b" * 32, None), wl.resolve_tree_handle("name:Kerbal X #2", self.trees))

    def test_refusals(self):
        self.assertEqual((None, wl.REASON_NO_MATCH), wl.resolve_tree_handle("name:Mun", self.trees))
        dup = list(self.trees) + [wl.TreeEntry("e" * 32, "Kerbal X")]
        self.assertEqual((None, wl.REASON_AMBIGUOUS), wl.resolve_tree_handle("name:kerbal x", dup))
        self.assertEqual((None, wl.REASON_NO_TREES), wl.resolve_tree_handle("first", []))
        self.assertEqual((None, wl.REASON_EMPTY), wl.resolve_tree_handle("  ", self.trees))
        self.assertEqual((None, wl.REASON_UNKNOWN_ID), wl.resolve_tree_handle("f" * 32, self.trees))

    def test_is_tree_handle(self):
        self.assertTrue(wl.is_tree_handle("Latest"))
        self.assertTrue(wl.is_tree_handle("name:x"))
        self.assertFalse(wl.is_tree_handle("a" * 32))


class TreeHandleSourceSyncTests(unittest.TestCase):
    """The seam resolves the same vocabulary live; its keyword and reason
    literals must stay byte-equal to this module's."""

    CS = os.path.join(REPO, "Source", "Parsek", "TestCommands", "TestCommandTreeHandle.cs")

    def test_the_csharp_twin_carries_the_same_literals(self):
        with open(self.CS, "r", encoding="utf-8") as fh:
            text = "\n".join(line.split("//", 1)[0] for line in fh.read().splitlines())
        consts = dict(re.findall(r'internal const string (\w+) = "([^"]*)";', text))
        self.assertEqual(wl.TREE_HANDLE_FIRST, consts.get("First"))
        self.assertEqual(wl.TREE_HANDLE_LATEST, consts.get("Latest"))
        self.assertEqual(wl.TREE_HANDLE_NAME_PREFIX, consts.get("NamePrefix"))
        self.assertEqual(wl.REASON_NO_TREES, consts.get("NoTreesReason"))
        self.assertEqual(wl.REASON_NO_MATCH, consts.get("NoMatchReason"))
        self.assertEqual(wl.REASON_AMBIGUOUS, consts.get("AmbiguousReason"))


def _params(**kw):
    base = dict(lane_id="WL-T", scene="landing",
                save_template="fixtures/saves/mun-landing-recorded",
                tree_id="0da22482a8a648c6835b7bcd6b0f200d")
    base.update(kw)
    return wl.WatchLaneParams(**base)


class GeneratorTests(unittest.TestCase):
    def _parse(self, p):
        text = wl.build_watch_spec(p)
        return text, tomllib.loads(text)

    def test_flight_scene_is_a_valid_holdable_spec(self):
        text, spec = self._parse(_params(at_ut=1272933))
        self.assertEqual("operator", spec["tier"])
        cmds = [s["cmd"] for s in spec["driver"]["steps"]]
        self.assertEqual(["LoadGame", "StopRecording", "DiscardTree", "SetSetting", "SetSetting",
                          "SetSetting", "SetSetting", "MissionConfig", "StartLoopPlayback",
                          "TimeJump", "WarpToUT", "EnterWatchMode", "CaptureScreenshot",
                          "FlushAndQuit"], cmds)
        steps = spec["driver"]["steps"]
        self.assertEqual("1272933", steps[9]["args"]["ut"])
        self.assertEqual("1272938", steps[10]["args"]["ut"])
        self.assertIsNone(wl.hold_refusal(spec))
        plan = wl.plan_hold(spec)
        self.assertEqual(len(cmds) - 1, plan.hold_index)
        registry = tomllib.load(open(os.path.join(HARNESS, "coverage", "registry.toml"), "rb"))
        self.assertEqual([], list(hlib.validate_spec(spec, registry, [], None).errors))
        self.assertTrue(text.isascii())

    def test_liftoff_rides_to_the_launch(self):
        _text, spec = self._parse(_params(scene="liftoff", focus_pid=12))
        steps = spec["driver"]["steps"]
        self.assertEqual("12", steps[0]["args"]["focusPid"])
        warp = [s for s in steps if s["cmd"] == "WarpToUT"]
        self.assertEqual(["${slp.relaunchUt+5}"], [s["args"]["ut"] for s in warp])
        registry = tomllib.load(open(os.path.join(HARNESS, "coverage", "registry.toml"), "rb"))
        self.assertEqual([], list(hlib.validate_spec(spec, registry, [], None).errors))

    def test_map_scene_frames_and_dumps(self):
        _text, spec = self._parse(_params(scene="transfer", map_focus="Sun",
                                          map_distance=5e10, at_ut=5355451927))
        cmds = [s["cmd"] for s in spec["driver"]["steps"]]
        self.assertEqual(["EnterMapView", "MapCamera", "CaptureScreenshot", "DumpMapGeometry",
                          "FlushAndQuit"], cmds[-5:])
        cam = spec["driver"]["steps"][-4]["args"]
        self.assertEqual(("Sun", "50000000000"), (cam["focus"], cam["distance"]))
        req = spec["expectations"]["logContracts"]["required"]
        self.assertIn("dumpmapgeometry ok label=wl-t-transfer scene=FLIGHT ", req)
        registry = tomllib.load(open(os.path.join(HARNESS, "coverage", "registry.toml"), "rb"))
        self.assertEqual([], list(hlib.validate_spec(spec, registry, [], None).errors))

    def test_bad_params_refuse(self):
        with self.assertRaises(ValueError):
            wl.build_watch_spec(_params(scene="orbit"))
        with self.assertRaises(ValueError):
            wl.build_watch_spec(_params(tree_id="latest"))
        with self.assertRaises(ValueError):
            wl.build_watch_spec(_params(scene="transfer"))   # no map distance

    def test_deterministic(self):
        self.assertEqual(wl.build_watch_spec(_params()), wl.build_watch_spec(_params()))


class CommittedWatchLaneSyncTests(unittest.TestCase):
    """Every committed spec with a [watch] table is the generator's output for
    the arguments its header records (``harness/tools/make_watch_lane.py``
    regenerates it); a hand edit reds here."""

    def test_committed_watch_lanes_regenerate_byte_for_byte(self):
        sys.path.insert(0, os.path.join(HARNESS, "tools"))
        import make_watch_lane  # noqa: E402
        scen = os.path.join(HARNESS, "scenarios")
        found = 0
        for name in sorted(os.listdir(scen)):
            if not name.endswith(".toml"):
                continue
            path = os.path.join(scen, name)
            with open(path, "rb") as fh:
                # Line endings are the checkout's (autocrlf), not the generator's.
                raw = fh.read().replace(b"\r\n", b"\n")
            spec = tomllib.loads(raw.decode("utf-8"))
            if "watch" not in spec:
                continue
            found += 1
            argv = make_watch_lane.recorded_argv(raw.decode("utf-8"))
            self.assertIsNotNone(argv, "%s has no recorded generator command" % name)
            regenerated = make_watch_lane.generate_text(argv, HARNESS)
            self.assertEqual(raw.decode("utf-8"), regenerated,
                             "%s differs from its generator output; re-run "
                             "python harness/tools/make_watch_lane.py %s"
                             % (name, " ".join(argv)))
        self.assertGreaterEqual(found, 4, "the four committed watch lanes")


class HoldSmokeTests(unittest.TestCase):
    """run.py --hold driven end to end over the fake KSP: the drive stops before
    FlushAndQuit, the fake game stays up until it 'closes' (its --max-seconds),
    and the OBSERVED record lands with mode=hold, outside load_all_results."""

    def setUp(self):
        import run  # noqa: E402
        import test_run_smoke as smoke  # noqa: E402
        self.run, self.smoke = run, smoke
        self.tmp = tempfile.mkdtemp(prefix="parsek-hold-smoke-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance)
        smoke._write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "hold_harness.log"))

    def tearDown(self):
        self.run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _runtime(self, close_after, mode="pass"):
        smoke = self.smoke

        class ClosingRuntime(smoke.FakeRuntime):
            def launch(self, exe, args, env, cwd):
                self.launch_count += 1
                return subprocess.Popen(
                    [exe, smoke.FAKE_KSP, "--root", cwd, "--mode", mode,
                     "--max-seconds", str(close_after)],
                    env=env, cwd=cwd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return ClosingRuntime(mode)

    def _spec(self):
        spec = self.smoke._make_spec(self.template, 30, 2)   # run budget 2 s: must NOT kill
        spec["driver"]["steps"] = [s for s in spec["driver"]["steps"] if s["cmd"] != "RunTests"]
        return spec

    def test_holds_until_the_game_closes_and_writes_observed(self):
        run = self.run
        spec = self._spec()
        plan = wl.plan_hold(spec)
        hold = run.HoldContext(plan, 1.0, None, "--id SMOKE-fake")
        start = time.time()
        result = run.run_attempt(spec, self.instance, self.tmp, self._runtime(4.0), 1, False,
                                 self.logger, hold=hold)
        elapsed = time.time() - start
        self.assertEqual("OBSERVED", result["verdict"], result.get("note"))
        self.assertEqual("hold", result["mode"])
        self.assertEqual(wl.END_OWNER_CLOSED, result["hold"]["endReason"])
        self.assertTrue(result["hold"]["setupComplete"])
        self.assertEqual([{"index": 2, "cmd": "FlushAndQuit"}], result["hold"]["skippedSteps"])
        self.assertEqual(["LoadGame", "SetSetting"], [s["cmd"] for s in result["driver"]["steps"]])
        self.assertGreater(elapsed, 2.5, "the run budget (2 s) must not end a hold")
        self.assertTrue(result["collectLogs"]["ran"])
        self.assertEqual([], run.load_all_results(run.RESULTS_DIR),
                         "a hold result must not reach coverage / flake readers")
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"]), encoding="utf-8") as fh:
            self.assertEqual("OBSERVED", json.load(fh)["verdict"])

    def test_the_ceiling_closes_a_forgotten_hold(self):
        run = self.run
        spec = self._spec()
        hold = run.HoldContext(wl.plan_hold(spec), 0.02, None, "--id SMOKE-fake")  # 1.2 s
        result = run.run_attempt(spec, self.instance, self.tmp, self._runtime(60.0), 1, False,
                                 self.logger, hold=hold)
        self.assertEqual("OBSERVED", result["verdict"])
        self.assertEqual(wl.END_CEILING, result["hold"]["endReason"])

    def test_a_refusal_before_launch_is_tagged_and_not_counted(self):
        run = self.run
        spec = self._spec()
        spec["fixture"]["saveTemplate"] = os.path.join(self.tmp, "no-such-template")
        hold = run.HoldContext(wl.plan_hold(spec), 1.0, None, "--id SMOKE-fake")
        result = run.run_attempt(spec, self.instance, self.tmp, self._runtime(4.0), 1, False,
                                 self.logger, hold=hold)
        self.assertEqual("INVALID", result["verdict"])
        self.assertEqual("hold", result["mode"])
        self.assertEqual(wl.END_REFUSED_BEFORE_LAUNCH, result["hold"]["endReason"])
        self.assertEqual([], run.load_all_results(run.RESULTS_DIR))

    def test_an_unresolved_handle_holds_instead_of_killing(self):
        run = self.run
        spec = self._spec()
        spec["driver"]["steps"].insert(1, {"cmd": "SetSetting", "args": {
            "name": "x", "value": "${nope.field+5}"}, "expect": "OK"})
        hold = run.HoldContext(wl.plan_hold(spec), 1.0, None, "--id SMOKE-fake")
        result = run.run_attempt(spec, self.instance, self.tmp, self._runtime(4.0), 1, False,
                                 self.logger, hold=hold)
        self.assertEqual(("OBSERVED", wl.SUBKIND_SETUP_INCOMPLETE),
                         (result["verdict"], result["subkind"]))
        self.assertEqual(wl.END_OWNER_CLOSED, result["hold"]["endReason"])
        self.assertEqual("UNRESOLVED-HANDLE", result["hold"]["unmetStep"]["verdict"])
        self.assertFalse(result["kspExit"]["killed"])

    def test_boot_crash_is_invalid(self):
        run = self.run
        spec = self._spec()
        hold = run.HoldContext(wl.plan_hold(spec), 1.0, None, "--id SMOKE-fake")
        result = run.run_attempt(spec, self.instance, self.tmp,
                                 self._runtime(4.0, mode="bootcrash"), 1, False,
                                 self.logger, hold=hold)
        self.assertEqual(("INVALID", "boot-crash"), (result["verdict"], result["subkind"]))
        self.assertEqual("hold", result["mode"])


if __name__ == "__main__":
    unittest.main()
