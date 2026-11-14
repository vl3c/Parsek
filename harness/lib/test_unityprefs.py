"""Unit tests for the Unity prefs hygiene: the pure decisions (unityprefs) and
the shell (tools/unity_prefs.py) over a fake registry backend."""

import importlib.util
import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import slots  # noqa: E402
import unityprefs as up  # noqa: E402

_TOOL = os.path.join(os.path.dirname(HERE), "tools", "unity_prefs.py")
_spec = importlib.util.spec_from_file_location("parsek_unity_prefs_under_test", _TOOL)
tool = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(tool)

FULL = ("Screenmanager Fullscreen mode_h3630240806", 1, up.REG_DWORD)
WIDTH = ("Screenmanager Resolution Width_h182942802", 2560, up.REG_DWORD)
MONITOR = ("UnitySelectMonitor_h17969598", 0, up.REG_DWORD)
OTHER = ("SomeOtherPref_h1", b"keep\x00", up.REG_BINARY)


class FakeRegistry:
    def __init__(self, values):
        self.values = {v[0]: v for v in values}
        self.fail_write = False

    def read_values(self):
        return list(self.values.values())

    def write(self, name, data, reg_type):
        if self.fail_write:
            raise OSError("access denied")
        self.values[name] = (name, data, reg_type)

    def delete(self, name):
        del self.values[name]


class PureTests(unittest.TestCase):
    def test_only_the_screen_values_are_tracked(self):
        self.assertEqual([FULL, WIDTH, MONITOR],
                         up.select_tracked([OTHER, MONITOR, WIDTH, FULL]))

    def test_snapshot_round_trips_every_type(self):
        values = [FULL, ("Screenmanager Blob", b"\x01\x02", up.REG_BINARY),
                  ("Screenmanager Str", "x", up.REG_SZ),
                  ("Screenmanager Q", 2 ** 40, up.REG_QWORD),
                  ("Screenmanager M", ["a", "b"], up.REG_MULTI_SZ), OTHER]
        text = up.encode_snapshot(values, "2026-11-14T00:00:00Z", 7)
        self.assertNotIn("SomeOtherPref", text)
        self.assertEqual(up.select_tracked(values[:-1]), up.decode_snapshot(text))

    def test_a_damaged_snapshot_never_decodes(self):
        good = up.encode_snapshot([FULL], "t", 1)
        for bad in ("", "{", "[]", good.replace(up.SNAPSHOT_SCHEMA, "other/1"),
                    good.replace("Screenmanager", "Other"),
                    '{"schema": "%s", "values": [{"name": "Screenmanager X", "type": 3, '
                    '"data": "zz"}]}' % up.SNAPSHOT_SCHEMA,
                    '{"schema": "%s", "values": [{"name": "Screenmanager X", "type": 99, '
                    '"data": 1}]}' % up.SNAPSHOT_SCHEMA):
            with self.subTest(bad=bad[:40]):
                with self.assertRaises(up.SnapshotError):
                    up.decode_snapshot(bad)

    def test_plan_writes_differences_and_deletes_additions_only(self):
        snapshot = [FULL, WIDTH]
        current = [(FULL[0], 3, up.REG_DWORD), WIDTH, MONITOR, OTHER]
        writes, deletes = up.plan_restore(snapshot, current)
        self.assertEqual([FULL], writes)
        self.assertEqual([MONITOR[0]], deletes)
        self.assertEqual(([], []), up.plan_restore(snapshot, snapshot))
        # A type change alone is a difference.
        self.assertEqual([FULL], up.plan_restore([FULL], [(FULL[0], 1, up.REG_QWORD)])[0])

    def test_start_action(self):
        self.assertEqual(up.START_SNAPSHOT, up.start_action(False, None))
        self.assertEqual(up.START_RECOVER, up.start_action(True, [FULL]))
        self.assertEqual(up.START_RECOVER, up.start_action(True, []))
        self.assertEqual(up.START_DISCARD, up.start_action(True, None))


class ShellTests(unittest.TestCase):
    def setUp(self):
        self.box = tempfile.mkdtemp(prefix="parsek-unity-prefs-")
        self.lines = []
        self.path = slots.unity_prefs_snapshot_path(self.box)

    def tearDown(self):
        shutil.rmtree(self.box, ignore_errors=True)

    def log(self, level, msg):
        self.lines.append((level, msg))

    def test_begin_then_end_restores_what_ksp_changed(self):
        reg = FakeRegistry([FULL, WIDTH, OTHER])
        self.assertEqual(tool.OUT_SNAPSHOT, tool.begin(self.box, self.log, reg))
        self.assertTrue(os.path.isfile(self.path))
        # A harness KSP quits and writes its own window mode.
        reg.values[FULL[0]] = (FULL[0], 3, up.REG_DWORD)
        reg.values[MONITOR[0]] = MONITOR
        reg.values[OTHER[0]] = (OTHER[0], b"changed", up.REG_BINARY)
        self.assertEqual(tool.OUT_RESTORED, tool.end(self.box, self.log, reg))
        self.assertEqual({FULL[0]: FULL, WIDTH[0]: WIDTH,
                          OTHER[0]: (OTHER[0], b"changed", up.REG_BINARY)}, reg.values)
        self.assertFalse(os.path.exists(self.path))

    def test_a_marker_from_a_dead_run_is_restored_first(self):
        reg = FakeRegistry([FULL, WIDTH])
        tool.begin(self.box, self.log, reg)
        reg.values[FULL[0]] = (FULL[0], 3, up.REG_DWORD)   # the dead run's KSP wrote this
        self.assertEqual(tool.OUT_RECOVERED, tool.begin(self.box, self.log, reg))
        self.assertEqual(FULL, reg.values[FULL[0]])
        self.assertTrue(any(level == "Warn" and "did not finish" in msg
                            for level, msg in self.lines))
        # The new snapshot holds the recovered value.
        with open(self.path, encoding="utf-8") as fh:
            self.assertIn(FULL, up.decode_snapshot(fh.read()))

    def test_an_unreadable_marker_is_set_aside_not_applied(self):
        os.makedirs(os.path.dirname(self.path))
        with open(self.path, "w") as fh:
            fh.write("{broken")
        reg = FakeRegistry([FULL])
        self.assertEqual(tool.OUT_SNAPSHOT, tool.begin(self.box, self.log, reg))
        aside = [n for n in os.listdir(os.path.dirname(self.path)) if ".unreadable-" in n]
        self.assertEqual(1, len(aside))

    def test_a_failed_restore_keeps_the_marker(self):
        reg = FakeRegistry([FULL])
        tool.begin(self.box, self.log, reg)
        reg.values[FULL[0]] = (FULL[0], 3, up.REG_DWORD)
        reg.fail_write = True
        self.assertEqual(tool.OUT_FAILED, tool.end(self.box, self.log, reg))
        self.assertTrue(os.path.isfile(self.path))

    def test_end_without_snapshot_and_hosts_without_a_registry(self):
        self.assertEqual(tool.OUT_NO_SNAPSHOT, tool.end(self.box, self.log, FakeRegistry([])))
        if os.name != "nt":
            self.assertEqual(tool.OUT_UNSUPPORTED, tool.begin(self.box, self.log))
            self.assertFalse(os.path.exists(self.path))


if __name__ == "__main__":
    unittest.main()
