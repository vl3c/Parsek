"""HARNESS-RESULTS-ARCHIVE: the machine-wide copy of every run's durable artifacts.

Pure cells over hlib's plan / manifest / opt-out; shell cells over
run.archive_run_results in a temp archive root; a fake-KSP smoke run through
the production retry loop that archives into a TEMP umbrella (never the real
one) and keeps its verdict when archiving fails; and the back-fill tool.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import contextlib
import gzip
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")
for _p in (HARNESS_ROOT, HERE, TOOLS_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import run  # noqa: E402
import archive_results  # noqa: E402
from test_run_smoke import FakeRuntime, _make_spec, _write_manifest  # noqa: E402

RID = "2026-10-11_0900_B11-mun-orbit"


class _MemLogger:
    def __init__(self):
        self.lines = []
        self.log_path = None

    def _add(self, level, step, msg):
        self.lines.append("[%s][%s] %s" % (level, step, msg))

    def info(self, step, msg):
        self._add("Info", step, msg)

    def warn(self, step, msg):
        self._add("Warn", step, msg)

    def verbose(self, step, msg):
        self._add("Verbose", step, msg)

    def error(self, step, msg):
        self._add("Error", step, msg)


class OptOutTests(unittest.TestCase):
    def test_unset_and_falsy_values_keep_archiving_on(self):
        self.assertFalse(hlib.results_archive_disabled({}))
        self.assertFalse(hlib.results_archive_disabled(None))
        for value in ("", "0", "false", "No", " off "):
            self.assertFalse(hlib.results_archive_disabled(
                {hlib.RESULTS_ARCHIVE_OPT_OUT_ENV: value}), value)

    def test_truthy_values_disable(self):
        for value in ("1", "true", "yes", "anything"):
            self.assertTrue(hlib.results_archive_disabled(
                {hlib.RESULTS_ARCHIVE_OPT_OUT_ENV: value}), value)

    def test_env_name_and_paths(self):
        self.assertEqual("PARSEK_HARNESS_NO_ARCHIVE", hlib.RESULTS_ARCHIVE_OPT_OUT_ENV)
        self.assertEqual(("automation", "results-archive", RID),
                         hlib.results_archive_relpath(RID))
        # Same automation/ dir as the machine lock, so every worktree agrees.
        import provlib
        self.assertEqual(provlib.MACHINE_LOCK_RELPATH[0], hlib.RESULTS_ARCHIVE_RELPATH[0])
        umb = os.path.join("C:", os.sep, "umb")
        self.assertEqual(os.path.join(umb, "automation", "results-archive"),
                         run.results_archive_root(umb))
        # Temp / swap names are dot-prefixed so no run-id listing can see them.
        self.assertEqual("." + RID + ".archive-tmp", hlib.results_archive_tmp_name(RID))
        self.assertEqual("." + RID + ".archive-old", hlib.results_archive_old_name(RID))


class PlanTests(unittest.TestCase):
    ALL = [RID + ".json", RID + "_status.json", RID + "_mission.json",
           RID + "_mission.stdout.log", RID + "_contact.html", RID + "_shots",
           RID + "_save", RID + ".claim", RID + ".manifest.json", "summary.txt",
           "2026-10-11_090001_harness.log", "2026-10-11_0800_OTHER.json"]

    def test_full_run_copies_the_small_files_and_gzips_the_log(self):
        plan = hlib.plan_results_archive(RID, self.ALL, True, "2026-10-11_090001_harness.log")
        self.assertIsNone(plan.skip_reason)
        self.assertEqual((), plan.missing)
        self.assertEqual(
            [RID + ".json", RID + "_status.json", RID + "_mission.json",
             RID + "_mission.stdout.log", "2026-10-11_090001_harness.log.gz", "KSP.log.gz"],
            [e.dest_name for e in plan.entries])
        gz = [e for e in plan.entries if e.gzip]
        self.assertEqual(2, len(gz))
        self.assertEqual((RID + "_shots", "KSP.log"), gz[1].source_rel)
        self.assertEqual(hlib.RESULTS_ARCHIVE_KSP_LOG_NAME, gz[1].dest_name)
        # The harness log's path is the shell's; the plan only names it.
        self.assertIsNone(plan.entries[4].source_rel)
        # An already-gzipped source log keeps its name (back-fill from an archive).
        again = hlib.plan_results_archive(RID, [RID + ".json"], False,
                                          "2026-10-11_090001_harness.log.gz")
        self.assertEqual("2026-10-11_090001_harness.log.gz", again.entries[1].dest_name)

    def test_bulky_and_foreign_artifacts_never_ride_along(self):
        plan = hlib.plan_results_archive(RID, self.ALL, True, None)
        names = [e.dest_name for e in plan.entries]
        for bulky in (RID + "_save", RID + "_contact.html", RID + ".claim",
                      "summary.txt", "2026-10-11_0800_OTHER.json"):
            self.assertNotIn(bulky, names)

    def test_missing_optional_files_are_reported_not_fatal(self):
        plan = hlib.plan_results_archive(RID, [RID + ".json"], False, None)
        self.assertIsNone(plan.skip_reason)
        self.assertEqual([RID + ".json"], [e.dest_name for e in plan.entries])
        self.assertEqual((RID + "_status.json", RID + "_mission.json",
                          RID + "_mission.stdout.log", "harness log",
                          RID + "_shots/KSP.log"), plan.missing)

    def test_no_result_json_archives_nothing(self):
        plan = hlib.plan_results_archive(RID, [RID + "_mission.json"], True, "x_harness.log")
        self.assertEqual("no result json", plan.skip_reason)
        self.assertEqual((), plan.entries)

    def test_an_unsafe_run_id_or_log_name_is_refused(self):
        self.assertEqual("invalid run id",
                         hlib.plan_results_archive("../x", ["../x.json"], False, None).skip_reason)
        self.assertEqual("invalid run id", hlib.plan_results_archive("", [], False, None).skip_reason)
        plan = hlib.plan_results_archive(RID, [RID + ".json"], False, "..\\evil.log")
        self.assertIn("harness log", plan.missing)


class ManifestTests(unittest.TestCase):
    def test_manifest_shape(self):
        m = hlib.build_results_archive_manifest(
            RID, "run", "2026-10-11T09:30:00Z", "C:/w", "abc123", "f00d",
            [(RID + ".json", 100, 100), ("KSP.log.gz", 50, 900)], ["harness log"])
        self.assertEqual(1, m["schema"])
        self.assertEqual(RID, m["runId"])
        self.assertEqual("run", m["origin"])
        self.assertEqual("C:/w", m["worktree"])
        self.assertEqual("abc123", m["gitHead"])
        self.assertEqual("f00d", m["parsekDllSha256"])
        self.assertEqual(150, m["totalBytes"])
        self.assertEqual({"name": "KSP.log.gz", "bytes": 50, "sourceBytes": 900},
                         m["files"][1])
        self.assertEqual(["harness log"], m["missing"])
        text = hlib.serialize_results_archive_manifest(m)
        self.assertTrue(text.endswith("\n"))
        self.assertEqual(m, json.loads(text))
        self.assertTrue(all(ord(ch) < 128 for ch in text))


class HarnessLogMappingTests(unittest.TestCase):
    def test_maps_each_run_to_the_log_that_wrote_its_result(self):
        logs = {
            "2026-10-11_080000_harness.log":
                "[Harness][Info][Result] result written C:\\r\\%s.json\n" % RID,
            "2026-10-11_100000_harness.log":
                "[Harness][Info][Result] result written C:\\r\\%s_a2.json\n" % RID,
        }
        got = hlib.harness_log_names_for_runs(logs, [RID, RID + "_a2", "2026-10-11_0000_NONE"])
        self.assertEqual({RID: "2026-10-11_080000_harness.log",
                          RID + "_a2": "2026-10-11_100000_harness.log"}, got)

    def test_newest_log_wins_and_a_last_line_without_newline_counts(self):
        line = "result written /r/%s.json" % RID
        logs = {"2026-10-11_080000_harness.log": line + "\n",
                "2026-10-11_100000_harness.log": line}
        self.assertEqual({RID: "2026-10-11_100000_harness.log"},
                         hlib.harness_log_names_for_runs(logs, [RID]))


class HarnessLogSliceTests(unittest.TestCase):
    """An N-scenario selection writes ONE log; archiving it whole per run would
    store N growing copies, so each run keeps only its scenario block."""

    LOG = [
        "[Harness][Info][Select] select expr='tier=nightly' -> 3 scenarios\n",
        "[Harness][Info][Lock] run-lock acquired (acquired-free) path=x pid=1\n",
        "[Harness][Info][Admit] admit scenario A\n",
        "[Harness][Info][Result] result written C:\\r\\2026-10-11_0900_A.json\n",
        "[Harness][Info][Cost] scenario cost attempts=1 wallTotal=10s terminal=PASS\n",
        "[Harness][Info][Admit] admit scenario B\n",
        "[Harness][Info][Result] result written C:\\r\\2026-10-11_0910_B.json\n",
        "[Harness][Info][Retry] retry scenario=B attempt=2 reason=INVALID\n",
        "[Harness][Info][Result] result written C:\\r\\2026-10-11_0920_B_a2.json\n",
        "[Harness][Info][Cost] scenario cost attempts=2 wallTotal=20s terminal=PASS\n",
        "[Harness][Info][Admit] admit scenario C\n",
        "[Harness][Info][Result] result written C:\\r\\2026-10-11_0930_C.json\n",
    ]

    def test_a_slice_holds_only_its_own_scenario(self):
        b = hlib.slice_harness_log_for_run(self.LOG, "2026-10-11_0910_B")
        self.assertEqual([self.LOG[1]] + self.LOG[5:10], b)
        joined = "".join(b)
        self.assertNotIn("scenario A", joined)
        self.assertNotIn("scenario C", joined)
        self.assertIn("[Retry]", joined)
        self.assertIn("[Cost]", joined)
        # Both attempts of the retried scenario get the same block.
        self.assertEqual(b, hlib.slice_harness_log_for_run(self.LOG, "2026-10-11_0920_B_a2"))

    def test_first_and_still_open_blocks(self):
        self.assertEqual(self.LOG[:5],
                         hlib.slice_harness_log_for_run(self.LOG, "2026-10-11_0900_A"))
        # The last block runs to the end of the log.
        self.assertEqual([self.LOG[1]] + self.LOG[10:],
                         hlib.slice_harness_log_for_run(self.LOG, "2026-10-11_0930_C"))

    def test_an_unknown_run_or_a_suffixed_lookalike_is_not_matched(self):
        self.assertIsNone(hlib.slice_harness_log_for_run(self.LOG, "2026-10-11_0900_Z"))
        # "..._B.json" must not match the "..._B_a2.json" line.
        self.assertIsNone(hlib.slice_harness_log_for_run([self.LOG[8]], "2026-10-11_0910_B"))

    def test_the_flighteff_facts_survive_the_slice(self):
        import flighteff
        facts = flighteff.parse_harness_log(
            hlib.slice_harness_log_for_run(self.LOG, "2026-10-11_0910_B"))
        row = facts["2026-10-11_0920_B_a2"]
        self.assertEqual(20, row["cost"]["wallTotal"])
        self.assertEqual(1, len(row["retries"]))
        self.assertIsNotNone(row["lock"])

    def test_shell_keeps_the_full_log_when_the_run_is_absent(self):
        tmp = tempfile.mkdtemp(prefix="parsek-slice-")
        try:
            results = os.path.join(tmp, "results")
            log = _populate_results(results)
            with open(log, "w") as fh:
                fh.write("unrelated line\n")
            final = run.archive_run_results(RID, results, os.path.join(tmp, "archive"),
                                            log, _MemLogger(), worktree=None)
            with open(os.path.join(final, "manifest.json")) as fh:
                self.assertEqual("full", json.load(fh)["harnessLog"])
            self.assertEqual("unrelated line\n",
                             _read_gz(os.path.join(final, "2026-10-11_090001_harness.log.gz")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class PipelinedHarnessLogSliceTests(unittest.TestCase):
    """Pipelined verification writes B's flight while A's post-quit block is still
    held, and B's retry after C's flight. The lane markers let the slice put each
    scenario back together; parse_harness_log needs nothing new because the held
    blocks keep Result / Retry / Cost lines grouped per scenario."""

    @staticmethod
    def _lane(sid, attempt, rid, body):
        return (["[Harness][Info][Lane] %s\n" % hlib.format_lane_begin_message(sid, attempt)]
                + ["[Harness][Info][Drive] %s\n" % b for b in body]
                + ["[Harness][Info][Lane] %s\n"
                   % hlib.format_lane_end_message(rid, sid, attempt)])

    def _log(self):
        r = "C:\\r\\"
        return (["[Harness][Info][Lock] run-lock acquired (acquired-free) path=x pid=1\n"]
                + self._lane("A", 1, "T_A", ["fly A"])
                + self._lane("B", 1, "T_B", ["fly B1"])
                + ["[Harness][Info][Verify] verify A\n",
                   "[Harness][Info][Result] result written %sT_A.json\n" % r,
                   "[Harness][Info][Cost] scenario cost attempts=1 wallTotal=10s terminal=PASS\n"]
                + self._lane("C", 1, "T_C", ["fly C"])
                + self._lane("B", 2, "T_B_a2", ["fly B2"])
                + ["[Harness][Info][Verify] verify C\n",
                   "[Harness][Info][Result] result written %sT_C.json\n" % r,
                   "[Harness][Info][Cost] scenario cost attempts=1 wallTotal=9s terminal=PASS\n",
                   "[Harness][Info][Verify] verify B1\n",
                   "[Harness][Info][Result] result written %sT_B.json\n" % r,
                   "[Harness][Info][Retry] retry scenario=B attempt=2 reason=INVALID\n",
                   "[Harness][Info][Verify] verify B2\n",
                   "[Harness][Info][Result] result written %sT_B_a2.json\n" % r,
                   "[Harness][Info][Cost] scenario cost attempts=2 wallTotal=20s terminal=PASS\n"])

    def test_each_slice_holds_its_own_flights_and_block_only(self):
        log = self._log()
        cases = (("T_A", ["fly A", "verify A"], ["fly B", "fly C", "verify C"]),
                 ("T_C", ["fly C", "verify C"], ["fly A", "fly B", "verify B"]),
                 ("T_B", ["fly B1", "fly B2", "verify B1", "[Retry]", "verify B2"],
                  ["fly A", "fly C", "verify A", "verify C"]))
        for rid, mine, theirs in cases:
            with self.subTest(runId=rid):
                text = "".join(hlib.slice_harness_log_for_run(log, rid))
                for s in mine:
                    self.assertIn(s, text)
                for s in theirs:
                    self.assertNotIn(s, text)
                self.assertEqual(1, text.count("[Cost]"))
                self.assertIn("[Lock]", text)
        self.assertEqual(hlib.slice_harness_log_for_run(log, "T_B"),
                         hlib.slice_harness_log_for_run(log, "T_B_a2"))

    def test_flights_come_first_then_the_post_quit_block(self):
        keys = ("fly B1", "fly B2", "verify B1", "verify B2")
        b = hlib.slice_harness_log_for_run(self._log(), "T_B")
        order = [k for ln in b for k in keys if k in ln]
        self.assertEqual(list(keys), order)

    def test_the_flighteff_facts_read_the_same(self):
        import flighteff
        facts = flighteff.parse_harness_log(self._log())
        self.assertEqual(2, facts["T_B_a2"]["cost"]["attempts"])
        self.assertEqual(1, len(facts["T_B"]["retries"]))
        self.assertEqual(0, len(facts["T_C"]["retries"]))
        self.assertEqual(10, facts["T_A"]["cost"]["wallTotal"])

    def test_an_unterminated_flight_is_nobodys(self):
        cut = self._log() + [
            "[Harness][Info][Lane] %s\n" % hlib.format_lane_begin_message("D", 1),
            "[Harness][Info][Drive] fly D\n"]
        for rid in ("T_A", "T_B", "T_C"):
            self.assertNotIn("fly D", "".join(hlib.slice_harness_log_for_run(cut, rid)))

    def test_a_serial_log_slices_exactly_as_before(self):
        log = HarnessLogSliceTests.LOG
        self.assertEqual(log[:5], hlib.slice_harness_log_for_run(log, "2026-10-11_0900_A"))
        self.assertEqual([log[1]] + log[5:10],
                         hlib.slice_harness_log_for_run(log, "2026-10-11_0910_B"))


def _populate_results(results_dir, run_id=RID, ksp_text="[LOG 09:00:00.000] hello\n" * 500,
                      log_name="2026-10-11_090001_harness.log"):
    os.makedirs(os.path.join(results_dir, run_id + "_shots"), exist_ok=True)
    with open(os.path.join(results_dir, run_id + ".json"), "w") as fh:
        json.dump({"runId": run_id, "verdict": "PASS"}, fh)
    with open(os.path.join(results_dir, run_id + "_mission.json"), "w") as fh:
        fh.write("{}")
    with open(os.path.join(results_dir, run_id + "_shots", "KSP.log"), "w") as fh:
        fh.write(ksp_text)
    with open(os.path.join(results_dir, run_id + "_shots", "shot.png"), "wb") as fh:
        fh.write(b"\x89PNG")
    os.makedirs(os.path.join(results_dir, run_id + "_save"), exist_ok=True)
    log = os.path.join(results_dir, log_name)
    with open(log, "w") as fh:
        fh.write("[Harness][Info][Result] result written %s\n"
                 % os.path.join(results_dir, run_id + ".json"))
        fh.write("[Harness][Info][Cost] scenario cost attempts=1 wallTotal=5s terminal=PASS\n")
    return log


def _read_gz(path):
    with gzip.open(path, "rt", encoding="utf-8") as fh:
        return fh.read()


class ArchiveShellTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-archive-")
        self.results = os.path.join(self.tmp, "results")
        self.root = os.path.join(self.tmp, "automation", "results-archive")
        self.log = _populate_results(self.results)
        self.logger = _MemLogger()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _archive(self, **kw):
        return run.archive_run_results(RID, self.results, self.root, self.log,
                                       self.logger, worktree=None, **kw)

    def test_archives_small_files_gzips_the_log_and_writes_a_manifest(self):
        final = self._archive()
        self.assertEqual(os.path.join(self.root, RID), final)
        self.assertEqual(sorted([RID + ".json", RID + "_mission.json", "KSP.log.gz",
                                 "2026-10-11_090001_harness.log.gz", "manifest.json"]),
                         sorted(os.listdir(final)))
        with gzip.open(os.path.join(final, "KSP.log.gz"), "rt") as fh:
            with open(os.path.join(self.results, RID + "_shots", "KSP.log")) as src:
                self.assertEqual(src.read(), fh.read())
        with open(os.path.join(final, "manifest.json")) as fh:
            manifest = json.load(fh)
        self.assertEqual(RID, manifest["runId"])
        self.assertIsNone(manifest["parsekDllSha256"])
        sizes = {f["name"]: f for f in manifest["files"]}
        self.assertLess(sizes["KSP.log.gz"]["bytes"], sizes["KSP.log.gz"]["sourceBytes"])
        self.assertIn(RID + "_status.json", manifest["missing"])
        self.assertEqual([], [n for n in os.listdir(self.root) if n.startswith(".")])
        self.assertTrue(any("results archived run=%s" % RID in l for l in self.logger.lines))

    def test_rearchive_replaces_the_dir_whole(self):
        self._archive()
        stale = os.path.join(self.root, RID, "stale.txt")
        with open(stale, "w") as fh:
            fh.write("x")
        os.remove(os.path.join(self.results, RID + "_mission.json"))
        self.assertIsNotNone(self._archive())
        names = os.listdir(os.path.join(self.root, RID))
        self.assertNotIn("stale.txt", names)
        self.assertNotIn(RID + "_mission.json", names)
        self.assertEqual([RID], os.listdir(self.root))

    def test_a_leftover_tmp_dir_from_a_killed_archive_is_replaced(self):
        leftover = os.path.join(self.root, hlib.results_archive_tmp_name(RID))
        os.makedirs(leftover)
        with open(os.path.join(leftover, "junk"), "w") as fh:
            fh.write("x")
        self._archive()
        self.assertEqual([RID], os.listdir(self.root))
        self.assertNotIn("junk", os.listdir(os.path.join(self.root, RID)))

    def test_failure_is_a_warn_and_returns_none(self):
        os.makedirs(os.path.dirname(self.root), exist_ok=True)
        with open(self.root, "w") as fh:  # a FILE where the archive dir must go
            fh.write("x")
        self.assertIsNone(self._archive())
        self.assertTrue(any(l.startswith("[Warn][Archive] results archive FAILED")
                            for l in self.logger.lines))

    def test_no_result_json_is_a_warned_skip(self):
        os.remove(os.path.join(self.results, RID + ".json"))
        self.assertIsNone(self._archive())
        self.assertFalse(os.path.isdir(os.path.join(self.root, RID)))
        self.assertTrue(any("reason=no result json" in l for l in self.logger.lines))

    def test_dll_sha_is_recorded_when_the_instance_has_one(self):
        instance = os.path.join(self.tmp, "instance")
        dll = os.path.join(instance, run.PARSEK_DLL_REL)
        os.makedirs(os.path.dirname(dll))
        with open(dll, "wb") as fh:
            fh.write(b"MZ-fake")
        final = self._archive(instance_dir=instance)
        with open(os.path.join(final, "manifest.json")) as fh:
            self.assertEqual(run._sha256_file(dll), json.load(fh)["parsekDllSha256"])


class FakeRunArchiveSmokeTests(unittest.TestCase):
    """A fake-KSP run through the production retry loop archives into the TEMP
    umbrella it was handed. The real umbrella is never touched because every
    smoke entry point passes its own temp dir as umbrella_root."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-archive-smoke-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(
            os.path.join(run.RESULTS_DIR, "2026-10-11_090000_harness.log"))
        self.archive = run.results_archive_root(self.tmp)
        env = dict(os.environ)
        env.pop(hlib.RESULTS_ARCHIVE_OPT_OUT_ENV, None)
        self._env = mock.patch.dict(os.environ, env, clear=True)
        self._env.start()

    def tearDown(self):
        self._env.stop()
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _fly(self):
        spec = _make_spec(self.template, 30, 600)
        with contextlib.redirect_stdout(io.StringIO()):
            return run._run_scenario_with_retry(spec, self.instance, self.tmp,
                                                FakeRuntime("pass"), self.logger)

    def _log(self):
        with open(self.logger.log_path, encoding="utf-8") as fh:
            return fh.read()

    def test_a_fake_run_archives_into_the_temp_umbrella(self):
        result = self._fly()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        run_dir = os.path.join(self.archive, result["runId"])
        names = set(os.listdir(run_dir))
        self.assertIn(result["runId"] + ".json", names)
        self.assertIn("KSP.log.gz", names)
        self.assertIn("2026-10-11_090000_harness.log.gz", names)
        self.assertIn("manifest.json", names)
        self.assertFalse(any(n.endswith(".png") or n.endswith("_save") for n in names))
        with open(os.path.join(run_dir, "manifest.json")) as fh:
            manifest = json.load(fh)
        self.assertEqual("run", manifest["origin"])
        self.assertEqual(run.WORKTREE_ROOT, manifest["worktree"])
        # The copied invocation log already holds the scenario's Cost line.
        self.assertEqual("slice", manifest["harnessLog"])
        self.assertIn("[Cost] scenario cost",
                      _read_gz(os.path.join(run_dir, "2026-10-11_090000_harness.log.gz")))
        with open(os.path.join(run_dir, result["runId"] + ".json")) as fh:
            self.assertEqual(hlib.VERDICT_PASS, json.load(fh)["verdict"])

    def test_an_archive_failure_does_not_change_the_verdict(self):
        os.makedirs(os.path.dirname(self.archive), exist_ok=True)
        with open(self.archive, "w") as fh:
            fh.write("not a dir")
        result = self._fly()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        with open(os.path.join(run.RESULTS_DIR, result["runId"] + ".json")) as fh:
            self.assertEqual(hlib.VERDICT_PASS, json.load(fh)["verdict"])
        self.assertIn("[Warn][Archive] results archive FAILED", self._log())

    def test_the_opt_out_env_skips_archiving(self):
        os.environ[hlib.RESULTS_ARCHIVE_OPT_OUT_ENV] = "1"
        result = self._fly()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertFalse(os.path.exists(self.archive))
        self.assertIn("results archive disabled by PARSEK_HARNESS_NO_ARCHIVE", self._log())


class BackfillToolTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-backfill-")
        self.results = os.path.join(self.tmp, "Parsek-x", "harness", "results")
        _populate_results(self.results)
        _populate_results(self.results, run_id="2026-09-01_0900_OLD",
                          log_name="2026-09-01_090001_harness.log")
        self.root = os.path.join(self.tmp, "archive")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _main(self, *extra):
        with contextlib.redirect_stdout(io.StringIO()):
            return archive_results.main(["--from", self.results, "--archive-dir", self.root]
                                        + list(extra))

    def test_backfill_imports_every_run_with_its_harness_log(self):
        self.assertEqual(0, self._main())
        self.assertEqual(sorted([RID, "2026-09-01_0900_OLD"]), sorted(os.listdir(self.root)))
        with open(os.path.join(self.root, RID, "manifest.json")) as fh:
            manifest = json.load(fh)
        self.assertEqual("backfill", manifest["origin"])
        self.assertIsNone(manifest["gitHead"])
        self.assertEqual(os.path.join(self.tmp, "Parsek-x"), manifest["worktree"])
        # An old PLAIN log is archived as this run's gzipped slice.
        self.assertEqual("slice", manifest["harnessLog"])
        text = _read_gz(os.path.join(self.root, RID, "2026-10-11_090001_harness.log.gz"))
        self.assertIn(RID + ".json", text)
        self.assertIn("[Cost] scenario cost", text)
        self.assertIn("2026-09-01_090001_harness.log.gz",
                      os.listdir(os.path.join(self.root, "2026-09-01_0900_OLD")))

    def test_backfill_reads_an_already_gzipped_harness_log(self):
        old = os.path.join(self.results, "2026-10-11_090001_harness.log")
        with open(old, "rb") as fin, gzip.open(old + ".gz", "wb") as fout:
            shutil.copyfileobj(fin, fout)
        os.remove(old)
        self.assertEqual(0, self._main("--since", "2026-10-01"))
        text = _read_gz(os.path.join(self.root, RID, "2026-10-11_090001_harness.log.gz"))
        self.assertIn(RID + ".json", text)

    def test_since_dry_run_and_existing_runs(self):
        self.assertEqual(0, self._main("--dry-run"))
        self.assertFalse(os.path.exists(self.root))
        self.assertEqual(0, self._main("--since", "2026-10-01"))
        self.assertEqual([RID], os.listdir(self.root))
        marker = os.path.join(self.root, RID, "marker")
        with open(marker, "w") as fh:
            fh.write("x")
        self._main()  # existing run left alone without --overwrite
        self.assertTrue(os.path.isfile(marker))
        self._main("--overwrite")
        self.assertFalse(os.path.isfile(marker))

    def test_result_run_ids_ignore_non_result_files(self):
        self.assertEqual([RID], archive_results.result_run_ids(
            [RID + ".json", RID + "_mission.json", "2026-10-11_0800_X_mission.json",
             "summary.txt", "2026-10-11_090001_harness.log"]))


if __name__ == "__main__":
    unittest.main()
