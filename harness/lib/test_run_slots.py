"""Fake-KSP smoke tests for run.py's install-slot mode (parallel runs).

A ``run.py --slot <name>`` child of the parallel runner: runner-token check
(ok / mismatch / dead runner / lost mid-selection), its slot lock, the kRPC
port stamp and the port-busy refusal, the per-instance Player.log and screen
launch args, the read-only stop request, the skipped results prune and index
append, the --report-json document, plus the serial path's new duties (slot
lock per instance, Unity prefs begin / end) and the launch-site helpers
(spacing, window step, process scoping). Same fake KSP as test_run_smoke.
"""

import contextlib
import io
import json
import os
import shutil
import socket
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
for _p in (HARNESS_ROOT, HERE, os.path.join(HARNESS_ROOT, "provision")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import run  # noqa: E402
import slotrun  # noqa: E402
import slots  # noqa: E402
from test_run_smoke import FakeRuntime, _make_spec, _write_manifest  # noqa: E402

SLOT = "stock-minimal-2"
TOKEN = "runner-token-123"


class _Valid:
    errors = []
    warnings = []


@contextlib.contextmanager
def _context(**kw):
    prev = run._CTX
    run._CTX = run.SelectionContext(**kw)
    try:
        yield run._CTX
    finally:
        run.release_slot_locks(_NullLogger())
        run._CTX = prev


class _NullLogger:
    def __getattr__(self, name):
        return lambda *a, **k: None


class _SlotBox(unittest.TestCase):
    """A temp umbrella with automation/stock-minimal-2 provisioned (manifest +
    kRPC dir + settings.cfg), a template save, and the runner's machine lock."""

    def setUp(self):
        self.box = tempfile.mkdtemp(prefix="parsek-slots-")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.box, "results")
        os.makedirs(run.RESULTS_DIR)
        self.instance = slots.instance_dir(self.box, SLOT)
        _write_manifest(self.instance, "stock-minimal")
        os.makedirs(os.path.join(self.instance, "GameData", "kRPC", "PluginData"))
        with open(os.path.join(self.instance, "settings.cfg"), "w", encoding="utf-8") as fh:
            fh.write("SCREEN_RESOLUTION_WIDTH = 1280\nSCREEN_RESOLUTION_HEIGHT = 720\n"
                     "FULLSCREEN = False\n")
        self.template = os.path.join(self.box, "fresh-career")
        os.makedirs(self.template)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self.write_machine_lock(TOKEN, os.getpid())
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "slot_harness.log"))
        self.env = {k: v for k, v in os.environ.items()
                    if k not in (hlib.SERIAL_VERIFY_ENV, hlib.RESULTS_ARCHIVE_OPT_OUT_ENV)}
        self.env[slotrun.RUNNER_TOKEN_ENV] = TOKEN
        self.env[slotrun.RUNNER_PID_ENV] = str(os.getpid())

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.box, ignore_errors=True)

    def write_machine_lock(self, token, pid):
        path = run.run_lock_path(self.box)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump({"pid": pid, "token": token, "timestamp": 0, "startedIso": "x"}, fh)

    def specs(self, *ids, profile="stock-minimal"):
        out = []
        for sid in ids:
            spec = _make_spec(self.template, 30, 600)
            spec["id"] = sid
            spec["instanceProfile"] = profile
            out.append(spec)
        return out

    def log_text(self):
        with open(self.logger.log_path, encoding="utf-8") as fh:
            return fh.read()

    def results(self):
        rows = {}
        for name in os.listdir(run.RESULTS_DIR):
            if name.endswith(".json"):
                with open(os.path.join(run.RESULTS_DIR, name), encoding="utf-8") as fh:
                    obj = json.load(fh)
                if isinstance(obj, dict) and "runId" in obj:
                    rows.setdefault(obj["scenarioId"], []).append(obj)
        return rows

    def fly_slot(self, specs, rt, args_extra=None, stop_request=False):
        args = mock.Mock(instance_dir=None, no_coverage=False, serial_verify=False,
                         keep_stop_request=False, tests_prebuilt=True)
        for k, v in (args_extra or {}).items():
            setattr(args, k, v)
        if stop_request:
            with open(run.stop_request_path(), "w") as fh:
                fh.write("stop\n")
        with _context(slot=SLOT, umbrella_root=self.box) as ctx, \
                mock.patch.dict(os.environ, self.env, clear=True), \
                mock.patch.object(hlib, "validate_spec", lambda *a, **k: _Valid()), \
                mock.patch.object(run, "resolve_mission_schemas",
                                  lambda s, lg=None: (None, [])), \
                mock.patch.object(run, "refresh_coverage_and_flake",
                                  side_effect=AssertionError("slot mode refreshes nothing")), \
                mock.patch.object(run, "_index_archived_runs",
                                  side_effect=AssertionError("slot mode appends no index")), \
                contextlib.redirect_stdout(io.StringIO()):
            rt.scope_ksp_to_instance = True
            code = run._run_slot_child(specs, specs, {}, self.box, args, rt, self.logger,
                                       "id=SLOT-*")
            report = slotrun.build_run_report(SLOT, code, [s["id"] for s in specs],
                                              ctx.report_results)
        return code, report


class _PrebuiltRuntime(FakeRuntime):
    def tests_assembly_present(self):
        return True


class SlotChildTests(_SlotBox):

    def test_a_token_ok_child_flies_with_its_slot_ports_and_own_player_log(self):
        rt = _PrebuiltRuntime("pass")
        code, report = self.fly_slot(self.specs("SLOT-A"), rt)
        log = self.log_text()
        self.assertEqual(0, code, log)
        self.assertEqual("PASS", self.results()["SLOT-A"][0]["verdict"])
        player_log = os.path.join(self.instance, "Player.log")
        self.assertEqual(["-logFile", player_log, "-screen-fullscreen", "0",
                          "-screen-width", "1280", "-screen-height", "720"],
                         rt.last_launch_args)
        with open(os.path.join(self.instance, *slotrun.KRPC_SETTINGS_RELPATH)) as fh:
            krpc = fh.read()
        self.assertIn("value = 50002\n", krpc)
        self.assertIn("value = 50003\n", krpc)
        self.assertIn("port-check rpc=50002 stream=50003 result=CLEAR", log)
        self.assertIn("runner check ok", log)
        self.assertIn("slot-lock acquired slot=%s" % SLOT, log)
        self.assertIn("tests prebuild skipped (--tests-prebuilt", log)
        # Serial verify, slot lock released, launch stamped, no prune.
        self.assertNotIn("post-quit verification pipelined", log)
        self.assertFalse(os.path.exists(slots.slot_lock_path(self.box, SLOT)))
        self.assertTrue(os.path.isfile(slots.launch_stamp_path(self.box)))
        self.assertIn("retention skipped (slot mode", log)
        # The report document.
        lane = report["lanes"][0]
        self.assertEqual(("SLOT-A", "PASS", 1), (lane["id"], lane["verdict"], lane["attempts"]))
        self.assertTrue(all(os.path.isfile(p) for p in lane["resultPaths"]))

    def _refused(self, rt, specs, subkind, **kw):
        code, report = self.fly_slot(specs, rt, **kw)
        self.assertEqual(1, code)
        self.assertEqual(0, rt.launch_count, "a refusal boots nothing")
        rows = self.results()
        for spec in specs:
            with self.subTest(lane=spec["id"]):
                self.assertEqual(("INVALID", subkind),
                                 (rows[spec["id"]][-1]["verdict"], rows[spec["id"]][-1]["subkind"]))
        self.assertEqual([subkind] * len(specs), [l["failureClass"] for l in report["lanes"]])
        return self.log_text()

    def test_a_token_mismatch_refuses_every_lane(self):
        self.write_machine_lock("someone-else", os.getpid())
        log = self._refused(_PrebuiltRuntime("pass"), self.specs("SLOT-A", "SLOT-B"),
                            hlib.RUNNER_TOKEN_SUBKIND)
        self.assertIn("does not match", log)

    def test_a_dead_runner_refuses(self):
        class Dead(_PrebuiltRuntime):
            def pid_alive(self, pid):
                return False
        log = self._refused(Dead("pass"), self.specs("SLOT-A"), hlib.RUNNER_TOKEN_SUBKIND)
        self.assertIn("is not alive", log)

    def test_a_busy_kRPC_port_refuses_the_attempt(self):
        rt = _PrebuiltRuntime("pass")
        rt.busy_ports = (50003,)
        log = self._refused(rt, self.specs("SLOT-A"), hlib.PORT_BUSY_SUBKIND)
        self.assertIn("port-busy: kRPC port(s) [50003]", log)

    def test_a_lane_of_another_profile_is_refused(self):
        self._refused(_PrebuiltRuntime("pass"), self.specs("SLOT-T", profile="stock-tree"),
                      hlib.SLOT_PROFILE_MISMATCH_SUBKIND)

    def test_a_slot_lock_held_by_a_live_process_refuses(self):
        path = slots.slot_lock_path(self.box, SLOT)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump({"pid": os.getppid(), "timestamp": 10 ** 12, "slot": SLOT}, fh)

        class Alive(_PrebuiltRuntime):
            def pid_alive(self, pid):
                return True
        self._refused(Alive("pass"), self.specs("SLOT-A"), hlib.SLOT_LOCKED_SUBKIND)
        self.assertTrue(os.path.isfile(path), "a refused child never removes the holder's lock")

    def test_losing_the_runner_mid_selection_stops_the_rest(self):
        box = self

        class LoseAfterFirst(_PrebuiltRuntime):
            def launch(self, exe, args, env, cwd):
                box.write_machine_lock("a-new-holder", os.getpid())
                return super().launch(exe, args, env, cwd)
        rt = LoseAfterFirst("pass")
        code, report = self.fly_slot(self.specs("SLOT-A", "SLOT-B"), rt)
        self.assertEqual(1, code)
        self.assertEqual(1, rt.launch_count)
        rows = self.results()
        self.assertEqual("PASS", rows["SLOT-A"][0]["verdict"])
        self.assertEqual(("INVALID", hlib.RUNNER_TOKEN_SUBKIND),
                         (rows["SLOT-B"][0]["verdict"], rows["SLOT-B"][0]["subkind"]))

    def test_the_stop_request_is_honoured_read_only(self):
        rt = _PrebuiltRuntime("pass")
        code, report = self.fly_slot(self.specs("SLOT-A"), rt, stop_request=True)
        self.assertEqual(0, rt.launch_count)
        self.assertTrue(os.path.exists(run.stop_request_path()),
                        "the runner and the sibling children must still see it")
        self.assertEqual(slotrun.REPORT_NOT_RUN, report["lanes"][0]["verdict"])

    def test_tests_prebuilt_without_the_assembly_refuses_tooling_build(self):
        class NoAssembly(FakeRuntime):
            def tests_assembly_present(self):
                return False
        self._refused(NoAssembly("pass"), self.specs("SLOT-A"),
                      hlib.TESTS_BUILD_INVALID_SUBKIND)


class SlotCliTests(_SlotBox):

    def _real_id(self):
        for spec in run.load_all_specs():
            if spec.get("instanceProfile") == "stock-minimal":
                return spec["id"]
        self.skipTest("no stock-minimal spec")

    def test_a_slot_child_without_a_runner_token_writes_refusals_and_the_report(self):
        sid = self._real_id()
        report_path = os.path.join(self.box, "out", "report.json")
        env = dict(self.env)
        env.pop(slotrun.RUNNER_TOKEN_ENV)
        with mock.patch.dict(os.environ, env, clear=True), \
                contextlib.redirect_stdout(io.StringIO()):
            code = run.main(["--slot", SLOT, "--id", sid, "--umbrella-root", self.box,
                             "--tests-prebuilt", "--report-json", report_path,
                             "--window", "minimize"])
        self.assertEqual(1, code)
        with open(report_path, encoding="utf-8") as fh:
            report = json.load(fh)
        self.assertEqual((slotrun.REPORT_SCHEMA, SLOT, 1),
                         (report["schema"], report["slot"], report["exitCode"]))
        self.assertEqual([(sid, "INVALID", hlib.RUNNER_TOKEN_SUBKIND, 1)],
                         [(l["id"], l["verdict"], l["failureClass"], l["attempts"])
                          for l in report["lanes"]])
        logs = [n for n in os.listdir(run.RESULTS_DIR) if n.endswith("_harness.log")]
        self.assertTrue(any(n.endswith("_%s_harness.log" % SLOT) for n in logs), logs)
        self.assertIsNone(run._CTX.slot, "run() restores the default context")

    def test_bad_flag_combinations_exit_2(self):
        for argv in (["--id", "X", "--tests-prebuilt"],
                     ["--id", "X", "--slot", SLOT, "--instance-dir", self.box],
                     ["--id", "X", "--slot", "not-a-slot"],
                     ["--id", "X", "--window", "sideways"]):
            with self.subTest(argv=argv):
                with contextlib.redirect_stderr(io.StringIO()), \
                        self.assertRaises(SystemExit) as cm:
                    run.main(argv)
                self.assertEqual(2, cm.exception.code)

    def test_serial_run_snapshots_and_restores_unity_prefs_and_slot_mode_does_not(self):
        sid = self._real_id()
        for slot_argv, want in (([], ["begin", "end"]), (["--slot", SLOT], [])):
            with self.subTest(slot=slot_argv):
                # The serial run takes and releases the machine lock itself; the slot
                # child needs the runner's lock back in place.
                self.write_machine_lock(TOKEN, os.getpid())
                rt = FakeRuntime("pass")
                with mock.patch.dict(os.environ, self.env, clear=True), \
                        mock.patch.object(run, "_run_selection", return_value=0), \
                        contextlib.redirect_stdout(io.StringIO()):
                    code = run.run(["--id", sid, "--umbrella-root", self.box] + slot_argv,
                                   runtime=rt)
                self.assertEqual(0, code)
                self.assertEqual(want, getattr(rt, "prefs_calls", []))
                self.assertFalse(os.path.exists(slots.slot_lock_path(self.box, SLOT)))

    def test_sweep_results_under_a_runner_token(self):
        shots = os.path.join(run.RESULTS_DIR, ".verify-private", "x")
        os.makedirs(shots)
        with mock.patch.dict(os.environ, self.env, clear=True), \
                contextlib.redirect_stdout(io.StringIO()):
            code = run.main(["--sweep-results", "--umbrella-root", self.box])
        self.assertEqual(0, code)
        self.assertFalse(os.path.exists(os.path.dirname(shots)))


class SerialSlotLockTests(_SlotBox):

    def test_a_serial_run_holds_the_slot_lock_of_each_instance_it_flies(self):
        instance = slots.instance_dir(self.box, "stock-minimal")
        _write_manifest(instance, "stock-minimal")
        lock = slots.slot_lock_path(self.box, "stock-minimal")
        seen = []

        class Watch(FakeRuntime):
            def launch(self, exe, args, env, cwd):
                seen.append(os.path.isfile(lock))
                return super().launch(exe, args, env, cwd)

            def build_tests(self, timeout):
                return run.ToolResult(0, False)

            def tests_assembly_present(self):
                return True
        rt = Watch("pass")
        args = mock.Mock(instance_dir=None, no_coverage=True, serial_verify=True,
                         keep_stop_request=True)
        with _context(umbrella_root=self.box), \
                mock.patch.dict(os.environ, self.env, clear=True), \
                mock.patch.object(hlib, "validate_spec", lambda *a, **k: _Valid()), \
                mock.patch.object(run, "resolve_mission_schemas",
                                  lambda s, lg=None: (None, [])), \
                contextlib.redirect_stdout(io.StringIO()):
            code = run._run_selection(self.specs("SER-A", "SER-B"), [], {}, self.box, args,
                                      rt, self.logger, None, "id=SER-*")
            self.assertTrue(os.path.isfile(lock), "held to the end of the invocation")
        self.assertEqual(0, code, self.log_text())
        self.assertEqual([True, True], seen)
        self.assertFalse(os.path.isfile(lock), "released with the invocation")
        self.assertEqual(1, self.log_text().count("slot-lock acquired slot=stock-minimal"))
        # A serial run keeps the pre-slot ports and launch args shape.
        self.assertIn("port-check rpc=50000 stream=50001 result=CLEAR", self.log_text())
        self.assertEqual("-logFile", rt.last_launch_args[0])


class LaunchSiteHelperTests(unittest.TestCase):

    def setUp(self):
        self.box = tempfile.mkdtemp(prefix="parsek-launchsite-")
        self.lines = []

    def tearDown(self):
        shutil.rmtree(self.box, ignore_errors=True)

    class _Log:
        def __init__(self, sink):
            self.sink = sink

        def __getattr__(self, level):
            return lambda step, msg: self.sink.append((level, step, msg))

    def test_spacing_waits_after_a_recent_start_in_slot_mode_only(self):
        rt = FakeRuntime("pass")
        rt.launch_spacing_seconds = 0.3
        log = self._Log(self.lines)
        with _context(umbrella_root=self.box):
            self.assertEqual("p0", run.spaced_launch(self.box, rt, log, lambda: "p0"))
        self.assertFalse(os.path.exists(slots.launch_stamp_path(self.box)),
                         "a serial run neither waits nor stamps")
        with _context(slot=SLOT, umbrella_root=self.box):
            run.spaced_launch(self.box, rt, log, lambda: "p1")
            t0 = rt.now()
            run.spaced_launch(self.box, rt, log, lambda: "p2")
            self.assertGreaterEqual(rt.now() - t0, 0.25)
        waits = [m for _l, _s, m in self.lines if "launch spacing: waiting" in m]
        self.assertEqual(1, len(waits))
        self.assertFalse(os.path.exists(slots.launch_stamp_path(self.box) + ".lock"))

    def test_a_stale_launch_lock_is_removed(self):
        rt = FakeRuntime("pass")
        rt.launch_spacing_seconds = 0.0
        lock = slots.launch_stamp_path(self.box) + ".lock"
        os.makedirs(os.path.dirname(lock))
        open(lock, "w").close()
        old = rt.now() - 10 * run.LAUNCH_LOCK_STALE_SECONDS
        os.utime(lock, (old, old))
        with _context(slot=SLOT, umbrella_root=self.box):
            self.assertEqual("p", run.spaced_launch(self.box, rt, self._Log(self.lines),
                                                    lambda: "p"))
        self.assertTrue(any("old; removing it" in m for _l, _s, m in self.lines))

    def test_window_step(self):
        log_path = os.path.join(self.box, "KSP.log")
        calls = []

        class Rt(FakeRuntime):
            def minimize_process_windows(self, pid):
                calls.append(pid)
                return 1
        proc = mock.Mock(pid=321)
        cases = (
            (slotrun.WINDOW_DESKTOP, "[TestCommands] window placement desktop=moved "
                                     "foreground=returned\n", slotrun.WINDOW_ACTION_NONE),
            (slotrun.WINDOW_DESKTOP, "[TestCommands] window placement desktop=skipped "
                                     "foreground=not-foreground\n",
             slotrun.WINDOW_ACTION_MINIMIZE),
            (slotrun.WINDOW_DESKTOP, "", slotrun.WINDOW_ACTION_MINIMIZE),
            (slotrun.WINDOW_MINIMIZE, "", slotrun.WINDOW_ACTION_MINIMIZE),
        )
        for mode, text, want in cases:
            with self.subTest(mode=mode, text=text):
                with open(log_path, "w") as fh:
                    fh.write(text)
                calls.clear()
                with _context(window_mode=mode):
                    got = run.apply_window_mode(proc, log_path, Rt("pass"),
                                                self._Log(self.lines))
                self.assertEqual(want, got)
                self.assertEqual([321] if want == slotrun.WINDOW_ACTION_MINIMIZE else [], calls)
        self.assertTrue(any(s == "Window" and "action=minimize windows=1" in m
                            for _l, s, m in self.lines))

    def test_boot_watch_calls_on_ready_once_and_survives_its_failure(self):
        hits = []
        bw = run.BootWatch(os.path.join(self.box, "KSP.log"), None, 0.0,
                           hlib.BootWatchdogLimits(), on_ready=lambda: hits.append(1))
        log = self._Log(self.lines)
        bw.mark_ready("seam-response", 1.0, log)
        bw.mark_ready("seam-response", 2.0, log)
        self.assertEqual([1], hits)

        def boom():
            raise RuntimeError("no window")
        bw2 = run.BootWatch(os.path.join(self.box, "KSP.log"), None, 0.0,
                            hlib.BootWatchdogLimits(), on_ready=boom)
        bw2.mark_ready("seam-receipt", 1.0, log)
        self.assertTrue(any("window step FAILED" in m for _l, _s, m in self.lines))

    def test_ksp_running_is_scoped_only_for_a_slot_child(self):
        mine = os.path.join(self.box, "automation", SLOT)
        other = os.path.join(self.box, "automation", "stock-minimal")

        class Rt(run.Runtime):
            def list_ksp_processes(self):
                return [(7, os.path.join(other, "KSP_x64.exe"))]
        rt = Rt()
        self.assertEqual(7, rt.ksp_running(mine))
        rt.scope_ksp_to_instance = True
        self.assertIsNone(rt.ksp_running(mine))
        self.assertEqual(7, rt.ksp_running(other))

    def test_windows_ksp_lister_keeps_a_ksp_whose_path_is_unreadable(self):
        mine = os.path.join(self.box, "automation", SLOT)
        other = os.path.join(self.box, "automation", "stock-minimal")
        listing = [(4, "System"), (10, "KSP_x64.exe"), (11, "ksp_x64.EXE"),
                   (12, "explorer.exe"), (13, "KSP_x64.exe")]
        paths = {10: os.path.join(other, "KSP_x64.exe")}

        def image_path(pid):
            if pid == 13:
                raise OSError("access denied")
            return paths.get(pid)
        procs = run._windows_ksp_processes(lister=lambda: listing, image_path_fn=image_path)
        self.assertEqual([(10, paths[10]), (11, None), (13, None)], procs)

        class Rt(run.Runtime):
            def list_ksp_processes(self):
                return procs
        rt = Rt()
        rt.scope_ksp_to_instance = True
        # The unreadable KSP blocks the slot child: busy, never somebody else's.
        self.assertEqual(11, rt.ksp_running(mine))

    def test_port_accepts_reads_a_real_listener(self):
        srv = socket.socket()
        srv.bind(("127.0.0.1", 0))
        srv.listen(1)
        port = srv.getsockname()[1]
        try:
            self.assertTrue(run.Runtime().port_accepts("127.0.0.1", port))
        finally:
            srv.close()
        self.assertFalse(run.Runtime().port_accepts("127.0.0.1", port))

    def test_stamp_krpc_ports_writes_only_on_change_and_skips_without_krpc(self):
        inst = os.path.join(self.box, "automation", SLOT)
        log = self._Log(self.lines)
        self.assertFalse(run.stamp_krpc_ports(inst, 50002, 50003, log))
        os.makedirs(os.path.join(inst, "GameData", "kRPC"))
        self.assertTrue(run.stamp_krpc_ports(inst, 50002, 50003, log))
        path = run.krpc_settings_path(inst)
        mtime = os.path.getmtime(path)
        os.utime(path, (mtime - 100, mtime - 100))
        self.assertTrue(run.stamp_krpc_ports(inst, 50002, 50003, log))
        self.assertEqual(mtime - 100, os.path.getmtime(path), "unchanged bytes not rewritten")
        with open(path, "rb") as fh:
            self.assertNotIn(b"\r\n", fh.read())

    def test_the_mission_gets_the_instance_ports(self):
        ctx = run.MissionContext("m", "py", "m.py", {}, "cwd", "stamp", {})
        self.assertEqual((50000, 50001), (ctx.rpc_port, ctx.stream_port))


if __name__ == "__main__":
    unittest.main()
