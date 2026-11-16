"""scripts/collect-logs.py and a race save's Parsek/Race/ folder (plan R1-07, design
addition A7): the folder holds other players' files, so the bundle never copies it and
lists it instead (relative path, size, modified time; no contents). A save with no race
folder, which is every single-player save, gets exactly the bundle it got before."""

import contextlib
import importlib.util
import io
import os
import shutil
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.dirname(os.path.dirname(_HERE))


def _load_collect_logs():
    path = os.path.join(_REPO, "scripts", "collect-logs.py")
    spec = importlib.util.spec_from_file_location("collect_logs_under_test", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


cl = _load_collect_logs()


def _write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text)


class CollectLogsRaceFolderTests(unittest.TestCase):
    def setUp(self):
        self.box = tempfile.mkdtemp(prefix="parsek-collect-race-")
        self.parsek = os.path.join(self.box, "save", "Parsek")
        self.out = os.path.join(self.box, "out")
        os.makedirs(self.out)
        _write(os.path.join(self.parsek, "Recordings", "a.prec"), "rec")
        _write(os.path.join(self.parsek, "GameState", "events.pgse"), "ledger")

    def tearDown(self):
        shutil.rmtree(self.box, ignore_errors=True)

    def _add_race(self):
        _write(os.path.join(self.parsek, "Race", "local.cfg"), "local")
        _write(os.path.join(self.parsek, "Race", "own", "player.cfg"), "player-secret")
        os.utime(os.path.join(self.parsek, "Race", "local.cfg"), (1700000000, 1700000000))

    def _quiet(self, fn, *args):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            result = fn(*args)
        return result, buf.getvalue()

    def test_the_race_folder_is_never_selected_for_copy(self):
        self._add_race()
        self.assertEqual(["GameState", "Recordings"],
                         cl.parsek_sidecar_dirs_to_copy(self.parsek, False))
        self.assertEqual(["GameState"], cl.parsek_sidecar_dirs_to_copy(self.parsek, True))

    def test_copy_leaves_the_race_folder_out(self):
        self._add_race()
        dst = os.path.join(self.out, "Parsek")
        self._quiet(cl.copy_parsek_sidecar_dirs, self.parsek, dst, False)
        self.assertTrue(os.path.isfile(os.path.join(dst, "Recordings", "a.prec")))
        self.assertFalse(os.path.exists(os.path.join(dst, "Race")))

    def test_the_listing_names_path_size_and_time_but_no_contents(self):
        self._add_race()
        wrote, printed = self._quiet(cl.write_race_listing, self.parsek, self.out)
        self.assertTrue(wrote)
        self.assertIn("Parsek/Race/  (not copied: 2 files listed in race-listing.txt)", printed)
        with open(os.path.join(self.out, cl.RACE_LISTING_NAME), encoding="utf-8") as fh:
            text = fh.read()
        rows = [l for l in text.splitlines() if not l.startswith("#")]
        self.assertEqual(["local.cfg\t5\t2023-11-14T22:13:20Z",
                          "own/player.cfg"], [rows[0], rows[1].split("\t")[0]])
        self.assertEqual("13", rows[1].split("\t")[1])
        self.assertNotIn("player-secret", text)

    def test_a_single_player_save_gets_no_listing_and_no_line(self):
        wrote, printed = self._quiet(cl.write_race_listing, self.parsek, self.out)
        self.assertFalse(wrote)
        self.assertEqual("", printed)
        self.assertEqual([], os.listdir(self.out))
        self.assertEqual(["GameState", "Recordings"],
                         cl.parsek_sidecar_dirs_to_copy(self.parsek, False))

    def test_the_folder_name_is_the_csharp_constant(self):
        with open(os.path.join(_REPO, "Source", "Parsek", "Recording", "RaceDirectoryKeep.cs"),
                  encoding="utf-8") as fh:
            src = fh.read()
        self.assertIn('internal const string DirectoryName = "%s";' % cl.RACE_DIR_NAME, src)


if __name__ == "__main__":
    unittest.main()
