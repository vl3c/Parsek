"""Unit tests for slotrun (run.py's install-slot decisions; pure)."""

import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import slotrun  # noqa: E402
import slots  # noqa: E402


class PortsTests(unittest.TestCase):
    def test_a_slot_dir_flies_its_slot_ports(self):
        self.assertEqual((50000, 50001),
                         slotrun.krpc_ports_for_instance("/u/automation/stock-minimal"))
        self.assertEqual((50002, 50003),
                         slotrun.krpc_ports_for_instance("/u/automation/stock-minimal-2/"))
        self.assertEqual((50020, 50021),
                         slotrun.krpc_ports_for_instance("/u/automation/stock-tree"))

    def test_a_non_slot_dir_keeps_the_pre_slot_ports(self):
        for d in ("/tmp/x/instance", "", None, "/u/automation/stock-minimal-0"):
            with self.subTest(d=d):
                self.assertEqual(slotrun.DEFAULT_KRPC_PORTS, slotrun.krpc_ports_for_instance(d))

    def test_copy_number(self):
        self.assertEqual(1, slotrun.instance_copy_number("/u/automation/stock-minimal"))
        self.assertEqual(3, slotrun.instance_copy_number("/u/automation/stock-minimal-3"))
        self.assertEqual(1, slotrun.instance_copy_number("/tmp/instance"))
        self.assertIsNone(slotrun.instance_slot_name("/tmp/instance"))


class RunnerTokenTests(unittest.TestCase):
    LOCK = {"pid": 4242, "token": "tok-1", "worktree": "w"}

    def decide(self, token="tok-1", pid="4242", lock=None, alive=True):
        return slotrun.decide_runner_token(token, pid, self.LOCK if lock is None else lock,
                                           lambda p: alive)

    def test_ok_when_the_lock_carries_the_token_and_the_runner_lives(self):
        d = self.decide()
        self.assertTrue(d.ok, d.reason)

    def test_every_refusal(self):
        cases = {
            "no token": dict(token=""),
            "pid not int": dict(pid="abc"),
            "pid zero": dict(pid="0"),
            "no lock": dict(lock={"_": 1}),
            "token mismatch": dict(token="other"),
            "lock pid differs": dict(pid="77"),
            "runner dead": dict(alive=False),
        }
        for name, kw in cases.items():
            with self.subTest(name):
                d = self.decide(**kw)
                self.assertFalse(d.ok)
                self.assertTrue(d.reason)
        self.assertFalse(slotrun.decide_runner_token("tok-1", "4242", None,
                                                     lambda p: True).ok)

    def test_a_lock_without_a_pid_still_needs_the_runner_alive(self):
        self.assertTrue(self.decide(lock={"token": "tok-1"}).ok)
        self.assertFalse(self.decide(lock={"token": "tok-1"}, alive=False).ok)


class BlockingPidTests(unittest.TestCase):
    MINE = os.path.join(os.sep, "u", "automation", "stock-minimal-2")
    OTHER = os.path.join(os.sep, "u", "automation", "stock-minimal")

    def test_serial_is_blocked_by_any_ksp(self):
        procs = [(10, os.path.join(self.OTHER, "KSP_x64.exe"))]
        self.assertEqual(10, slotrun.blocking_ksp_pid(procs, self.MINE, scoped=False))
        self.assertIsNone(slotrun.blocking_ksp_pid([], self.MINE, scoped=False))

    def test_a_slot_ignores_a_sibling_slot_ksp(self):
        procs = [(10, os.path.join(self.OTHER, "KSP_x64.exe")),
                 (11, os.path.join(self.MINE, "KSP_x64.exe"))]
        self.assertEqual(11, slotrun.blocking_ksp_pid(procs, self.MINE, scoped=True))
        self.assertIsNone(slotrun.blocking_ksp_pid(procs[:1], self.MINE, scoped=True))

    def test_an_unreadable_path_blocks_a_slot(self):
        self.assertEqual(12, slotrun.blocking_ksp_pid([(12, None)], self.MINE, scoped=True))

    def test_is_ksp_image(self):
        self.assertTrue(slotrun.is_ksp_image(r"C:\x\KSP_x64.exe"))
        self.assertTrue(slotrun.is_ksp_image("ksp_x64.EXE"))
        self.assertFalse(slotrun.is_ksp_image(r"C:\x\explorer.exe"))
        self.assertFalse(slotrun.is_ksp_image(None))


class LaunchArgsTests(unittest.TestCase):
    def test_full_args_with_a_size(self):
        args = slotrun.ksp_launch_args("/i/Player.log", {"SCREEN_RESOLUTION_WIDTH": "1280",
                                                         "SCREEN_RESOLUTION_HEIGHT": " 720 ",
                                                         "FULLSCREEN": "False"})
        self.assertEqual(["-logFile", "/i/Player.log", "-screen-fullscreen", "0",
                          "-screen-width", "1280", "-screen-height", "720"], args)

    def test_no_size_args_without_two_positive_ints(self):
        for values in ({}, {"SCREEN_RESOLUTION_WIDTH": "1280"},
                       {"SCREEN_RESOLUTION_WIDTH": "x", "SCREEN_RESOLUTION_HEIGHT": "720"},
                       {"SCREEN_RESOLUTION_WIDTH": "0", "SCREEN_RESOLUTION_HEIGHT": "720"}):
            with self.subTest(values=values):
                self.assertEqual(["-logFile", "p", "-screen-fullscreen", "0"],
                                 slotrun.ksp_launch_args("p", values))

    def test_player_log_is_inside_the_instance(self):
        self.assertEqual(os.path.join("i", "Player.log"), slotrun.instance_player_log("i"))


class WindowTests(unittest.TestCase):
    LOG = ("[LOG 1] noise\n"
           "[LOG 2] [Parsek][INFO][TestCommands] window placement desktop=failed hr=0x80004005 "
           "foreground=returned\n")

    def test_parse_takes_the_last_line_and_the_first_word(self):
        self.assertEqual({"desktop": "failed", "foreground": "returned"},
                         slotrun.parse_window_placement(self.LOG))
        two = self.LOG + ("[LOG 3] [Parsek][INFO][TestCommands] window placement desktop=moved "
                          "foreground=not-foreground\n")
        self.assertEqual("moved", slotrun.parse_window_placement(two)["desktop"])
        self.assertIsNone(slotrun.parse_window_placement("nothing"))
        self.assertIsNone(slotrun.parse_window_placement(None))

    def test_decisions(self):
        moved = {"desktop": "moved", "foreground": "returned"}
        failed = {"desktop": "failed", "foreground": ""}
        cases = [
            (slotrun.WINDOW_NORMAL, moved, slotrun.WINDOW_ACTION_NONE),
            (slotrun.WINDOW_NORMAL, None, slotrun.WINDOW_ACTION_NONE),
            (slotrun.WINDOW_MINIMIZE, moved, slotrun.WINDOW_ACTION_MINIMIZE),
            (slotrun.WINDOW_DESKTOP, moved, slotrun.WINDOW_ACTION_NONE),
            (slotrun.WINDOW_DESKTOP, failed, slotrun.WINDOW_ACTION_MINIMIZE),
            (slotrun.WINDOW_DESKTOP, {"desktop": "skipped"}, slotrun.WINDOW_ACTION_MINIMIZE),
            (slotrun.WINDOW_DESKTOP, None, slotrun.WINDOW_ACTION_MINIMIZE),
            ("bogus", None, slotrun.WINDOW_ACTION_NONE),
        ]
        for mode, placement, want in cases:
            with self.subTest(mode=mode, placement=placement):
                action, reason = slotrun.decide_window_action(mode, placement)
                self.assertEqual(want, action)
                self.assertTrue(reason)

    def test_ksp_processes_from_listing_keeps_unreadable_paths(self):
        listing = [(0, "KSP_x64.exe"), (5, "svchost.exe"), (6, "KSP_x64.exe"),
                   (7, "KSP_x64.exe"), (8, "KSP_x64.exe")]

        def image_path(pid):
            if pid == 7:
                raise PermissionError("denied")
            return {6: r"C:\a\KSP_x64.exe", 8: ""}.get(pid)
        self.assertEqual([(6, r"C:\a\KSP_x64.exe"), (7, None), (8, None)],
                         slotrun.ksp_processes_from_listing(listing, image_path))
        self.assertEqual([], slotrun.ksp_processes_from_listing([], image_path))

    def test_return_foreground_only_off_normal_and_never_to_a_harness_ksp(self):
        umb = r"C:\umb"
        self.assertFalse(slotrun.should_return_foreground(slotrun.WINDOW_NORMAL, r"C:\e.exe", umb))
        self.assertTrue(slotrun.should_return_foreground(slotrun.WINDOW_DESKTOP, r"C:\e.exe", umb))
        self.assertTrue(slotrun.should_return_foreground(slotrun.WINDOW_MINIMIZE, None, umb))
        # A harness install under <umbrella>/automation/ keeps the foreground.
        self.assertFalse(slotrun.should_return_foreground(
            slotrun.WINDOW_DESKTOP, r"C:\umb\automation\stock-minimal-2\KSP_x64.exe", umb))
        # Case-insensitive, as Windows paths are.
        self.assertFalse(slotrun.should_return_foreground(
            slotrun.WINDOW_MINIMIZE, r"c:\UMB\Automation\stock-minimal\ksp_x64.EXE", umb))
        # The owner's own KSP (Steam) gets its foreground back.
        self.assertTrue(slotrun.should_return_foreground(
            slotrun.WINDOW_DESKTOP,
            r"C:\Steam\steamapps\common\Kerbal Space Program\KSP_x64.exe", umb))
        # So does the umbrella's dev instance, and a sibling dir that merely shares
        # the "automation" string prefix.
        self.assertTrue(slotrun.should_return_foreground(
            slotrun.WINDOW_DESKTOP, r"C:\umb\Kerbal Space Program\KSP_x64.exe", umb))
        self.assertTrue(slotrun.should_return_foreground(
            slotrun.WINDOW_DESKTOP, r"C:\umb\automation-old\stock-minimal\KSP_x64.exe", umb))
        # A non-KSP exe under automation/ is not a harness KSP.
        self.assertTrue(slotrun.should_return_foreground(
            slotrun.WINDOW_DESKTOP, r"C:\umb\automation\tool.exe", umb))

    def test_harness_ksp_compares_real_paths(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            umb = os.path.join(tmp, "umb")
            inst = os.path.join(umb, "automation", "stock-minimal-2")
            os.makedirs(inst)
            exe = os.path.join(inst, "KSP_x64.exe")
            self.assertTrue(slotrun.is_harness_ksp_exe(exe, umb))
            self.assertFalse(slotrun.should_return_foreground(slotrun.WINDOW_DESKTOP, exe, umb))
            # A dotted spelling resolves to the same install.
            dotted = os.path.join(umb, "x", "..", "automation", "stock-minimal-2", "KSP_x64.exe")
            self.assertTrue(slotrun.is_harness_ksp_exe(dotted, umb))
            self.assertFalse(slotrun.is_harness_ksp_exe(None, umb))
            self.assertFalse(slotrun.is_harness_ksp_exe(exe, ""))
            if hasattr(os, "symlink"):
                link = os.path.join(tmp, "elsewhere")
                try:
                    os.symlink(inst, link)
                except OSError:
                    return
                # Reached through a link outside the umbrella, still a harness install.
                self.assertTrue(slotrun.is_harness_ksp_exe(os.path.join(link, "KSP_x64.exe"), umb))


class ReportTests(unittest.TestCase):
    def _r(self, rid, sid, attempt, verdict, subkind="", wall=10):
        return {"runId": rid, "scenarioId": sid, "attempt": attempt, "verdict": verdict,
                "subkind": subkind, "wallSeconds": wall}

    def test_lanes_group_attempts_and_take_the_last_verdict(self):
        results = [
            (self._r("r1", "A", 1, "INVALID", "boot-crash", 30), "p/r1.json"),
            (self._r("r2", "B", 1, "PARSEK-FAIL", "results", 50), "p/r2.json"),
            (self._r("r3", "A", 2, "PASS", "", 40), "p/r3.json"),
            # The flakedThenPassed enrichment re-writes r3; one run id, last copy wins.
            (dict(self._r("r3", "A", 2, "PASS", "", 40), note="flakedThenPassed"), "p/r3.json"),
        ]
        rep = slotrun.build_run_report("stock-minimal-2", 1, ["A", "B", "C"], results)
        self.assertEqual(slotrun.REPORT_SCHEMA, rep["schema"])
        self.assertEqual(("stock-minimal-2", 1), (rep["slot"], rep["exitCode"]))
        lanes = {l["id"]: l for l in rep["lanes"]}
        self.assertEqual(["A", "B", "C"], [l["id"] for l in rep["lanes"]])
        a = lanes["A"]
        self.assertEqual(("PASS", "", 2, ["r1", "r3"], 70, ["p/r1.json", "p/r3.json"]),
                         (a["verdict"], a["failureClass"], a["attempts"], a["runIds"],
                          a["wallSeconds"], a["resultPaths"]))
        self.assertEqual(("PARSEK-FAIL", "results", "results"),
                         (lanes["B"]["verdict"], lanes["B"]["subkind"],
                          lanes["B"]["failureClass"]))
        self.assertEqual((slotrun.REPORT_NOT_RUN, 0, []),
                         (lanes["C"]["verdict"], lanes["C"]["attempts"], lanes["C"]["runIds"]))

    def test_an_empty_report(self):
        rep = slotrun.build_run_report(None, 2, [], [])
        self.assertEqual({"schema": slotrun.REPORT_SCHEMA, "slot": None, "exitCode": 2,
                          "lanes": []}, rep)

    def test_a_verdict_without_subkind_is_its_own_failure_class(self):
        rep = slotrun.build_run_report(None, 1, ["K"], [(self._r("k", "K", 1, "KILLED"), "k")])
        self.assertEqual("KILLED", rep["lanes"][0]["failureClass"])


class HarnessLogNameTests(unittest.TestCase):
    def test_names(self):
        self.assertEqual("2026-11-14_120000_harness.log",
                         slotrun.harness_log_name("2026-11-14_120000"))
        self.assertEqual("2026-11-14_120000_stock-minimal-2_harness.log",
                         slotrun.harness_log_name("2026-11-14_120000", "stock-minimal-2"))
        # Every slot name stays inside the archive / efficiency tools' name pattern.
        for slot in ("stock-minimal", "stock-tree-3", slots.slot_name("modded-compat", 10)):
            self.assertTrue(slotrun.harness_log_name("s", slot).endswith("_harness.log"))

    def test_the_archive_and_efficiency_tools_read_slot_named_logs(self):
        import importlib.util
        tools = os.path.join(os.path.dirname(HERE), "tools")
        for fname in ("archive_results.py", "flight_efficiency.py"):
            spec = importlib.util.spec_from_file_location(
                "slotrun_test_" + fname[:-3], os.path.join(tools, fname))
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)
            for slot in (None, "stock-minimal", "stock-tree-3"):
                name = slotrun.harness_log_name("2026-11-14_120000", slot)
                with self.subTest(tool=fname, name=name):
                    self.assertTrue(mod._HARNESS_LOG_RE.match(name))
                    self.assertTrue(mod._HARNESS_LOG_RE.match(name + ".gz"))


if __name__ == "__main__":
    unittest.main()
