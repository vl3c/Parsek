"""Fake-KSP smoke test for the M-A5 run.py I/O shell.

Per the design Test Plan (and MEMORY: an agent cannot pilot KSP), the thin
run.py shell -- subprocess launch, channel-file tail, process-tree kill, verifier
dispatch -- is exercised by a FAKE KSP: a real child process (``_fake_ksp.py``)
that reads the command file and writes scripted responses / journal /
BATCH_COMPLETE-bearing KSP.log lines. This drives run.py's full per-attempt loop
(admit -> lock -> preflight -> stage -> launch -> drive -> budget -> verify ->
classify -> result) with NO real game.

Two required end-to-end runs (design Fake-KSP happy path + hang -> KILLED), plus
a boot-crash retry case for coverage:
  - PASS:   the stub responds OK to every step and emits a clean batch; run.py
            drives it to a PASS result with every verifier PASS/SKIPPED.
  - KILLED: the stub wedges on RunTests; the run-budget watchdog kills the process
            tree within budget and classifies KILLED with the killed-run
            log-validation mode selected. Fails if the harness hangs (no budget
            enforcement) or reds a killed run on marker-pairing.
  - INVALID(boot-crash): the stub exits during boot-wait with no response;
            classified INVALID(boot-crash), retryable.

The verifier SUBPROCESSES (analyzer / log-validate / collect-logs / inject) are
stubbed by the FakeRuntime so CI needs neither dotnet nor pwsh; the file-reading +
hlib-parsing half of the chain stays REAL (the stub writes real report files that
run.py reads and hlib parses).

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import copy
import json
import os
import shutil
import sys
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
for _p in (HARNESS_ROOT, HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import run  # noqa: E402

FAKE_KSP = os.path.join(HERE, "_fake_ksp.py")


class FakeRuntime(run.Runtime):
    """Injectable runtime that launches the fake-KSP stub instead of KSP_x64.exe
    and stubs the external verifier subprocesses. Process launch / poll / kill and
    the wall clock stay REAL, so the tail / budget / kill plumbing is exercised."""

    def __init__(self, mode):
        self.mode = mode

    def sleep(self, seconds):
        # Keep real time advancing (so budgets elapse) but spin fast.
        time.sleep(min(seconds, 0.05))

    def ksp_running(self, instance_dir):
        return None  # no zombie in the fake environment

    def resolve_exe(self, instance_dir):
        return sys.executable

    def launch(self, exe, args, env, cwd):
        import subprocess
        return subprocess.Popen(
            [exe, FAKE_KSP, "--root", cwd, "--mode", self.mode],
            env=env, cwd=cwd,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # ---- stubbed verifier subprocesses -----------------------------------

    def run_inject(self, instance_dir, save_name, timeout):
        rec = os.path.join(instance_dir, "saves", save_name, "Parsek", "Recordings")
        os.makedirs(rec, exist_ok=True)
        for i in range(8):
            open(os.path.join(rec, "rec%02d.prec" % i), "w").close()
        return run.ToolResult(0, False)

    def run_analyzer(self, save_dir, fresh_gate, timeout):
        analysis = os.path.join(save_dir, "analysis")
        os.makedirs(analysis, exist_ok=True)
        leaf = os.path.basename(save_dir.rstrip("/\\"))
        with open(os.path.join(analysis, "%s.analysis.txt" % leaf), "w", encoding="utf-8") as fh:
            fh.write("[Analyzer] save=%s findings=0 FAIL=0 STALE=0 RED=0\n" % leaf)
        with open(os.path.join(analysis, "%s.analysis.json" % leaf), "w", encoding="utf-8") as fh:
            json.dump({"counts": {"failNonBaselined": 0, "staleNonBaselined": 0},
                       "findings": []}, fh)
        return run.ToolResult(0, False)

    def run_log_validate(self, log_path, killed, no_recording, timeout):
        # Record the profile the harness selected so the test can assert on it.
        self.last_log_validate = {"killed": killed, "no_recording": no_recording}
        return run.ToolResult(0, False)

    def run_collect_logs(self, label, save_name, instance_dir, timeout):
        return run.ToolResult(0, False, stdout="../logs/2026-07-12_0000_%s\n" % label)


def _write_manifest(instance_dir, profile):
    parsek_gd = os.path.join(instance_dir, "GameData", "Parsek")
    os.makedirs(parsek_gd, exist_ok=True)
    manifest = {
        "schema": 1,
        "profile": profile,
        "kspVersion": "1.12.5",
        "components": {"parsek": {"kind": "dll"}},
        "settingsDeltasApplied": {},
        "devSourcedMods": {},
    }
    with open(os.path.join(parsek_gd, "provision-manifest.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh)


def _make_spec(save_template, run_tests_budget, run_budget):
    return {
        "schema": 1,
        "id": "SMOKE-fake",
        "tier": "daily",
        "instanceProfile": "stock-minimal",
        "fixture": {"saveTemplate": save_template, "injectedRecordings": "none", "craft": []},
        "driver": {"kind": "seam", "steps": [
            {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"},
             "expect": "OK", "budget": 30},
            {"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "false"},
             "expect": "OK"},
            {"cmd": "RunTests", "args": {"category": "RecordingInvariants"},
             "expect": "OK", "budget": run_tests_budget},
            {"cmd": "FlushAndQuit", "expect": "OK"},
        ]},
        "expectations": {
            "recordings": {"count": {"min": 0, "max": 0}},
            "logContracts": {"required": ["BATCH_COMPLETE v1 .* failed=0\\b"],
                             "forbidden": ["\\[Parsek\\]\\[ERROR\\]"]},
            "allowedAnomalies": [],
        },
        "runtime": {"budgetSeconds": run_budget},
        "retry": {"policy": "once"},
        "expectedFail": {"bugId": ""},
    }


class FakeKspSmokeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-smoke-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        # Fixture template (absolute path -> os.path.join in stage keeps it whole).
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        # Redirect the durable result store into the temp dir.
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger()

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run(self, mode, run_tests_budget=30, run_budget=600):
        spec = _make_spec(self.template, run_tests_budget, run_budget)
        rt = FakeRuntime(mode)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def test_happy_path_drives_to_pass(self):
        """PASS: the stub responds OK to every step + emits a clean batch; run.py's
        tail/dedupe/verify wiring must terminate PASS. Fails if a well-formed run
        is misclassified or a verifier reds a clean save."""
        result, _ = self._run("pass")

        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "expected PASS, got %s (%s)" % (result["verdict"], result.get("subkind")))
        self.assertTrue(result["driver"]["allExpectedMet"])
        self.assertEqual(4, len(result["driver"]["steps"]))
        self.assertTrue(all(s["met"] for s in result["driver"]["steps"]))
        # Every run-verifier PASS/SKIPPED; the batch was found with failed=0.
        v = result["verifiers"]
        self.assertEqual("PASS", v["driverValidity"]["status"])
        self.assertEqual("PASS", v["batchComplete"]["status"])
        self.assertEqual(0, v["batchComplete"]["failed"])
        self.assertEqual("PASS", v["analyzer"]["status"])
        self.assertEqual(0, v["analyzer"]["red"])
        self.assertEqual("PASS", v["logValidate"]["status"])
        self.assertEqual("PASS", v["expectations"]["status"])
        self.assertEqual(0, result["kspExit"]["code"])
        self.assertFalse(result["kspExit"]["killed"])
        # A PASS does not snapshot heavy diagnostics.
        self.assertFalse(result["collectLogs"]["ran"])
        # The durable result landed.
        self.assertTrue(os.path.isfile(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"])))

    def test_hang_is_killed_within_budget(self):
        """KILLED: the stub wedges on RunTests; the run-budget watchdog must kill
        the process tree within budget and classify KILLED with the killed-run
        log-validation mode selected. Fails if the harness hangs (no budget
        enforcement) or reds a killed run on marker-pairing."""
        start = time.time()
        result, rt = self._run("hang", run_tests_budget=1, run_budget=2)
        elapsed = time.time() - start

        self.assertEqual(hlib.VERDICT_KILLED, result["verdict"],
                         "expected KILLED, got %s" % result["verdict"])
        self.assertTrue(result["kspExit"]["killed"])
        self.assertLess(elapsed, 60, "watchdog did not kill within a bounded window")
        # Killed-run log validation was selected (marker-pairing suppressed); the
        # recording-free scenario (count.max==0) also carries the no-recording flag.
        self.assertTrue(rt.last_log_validate["killed"])
        self.assertTrue(rt.last_log_validate["no_recording"])
        self.assertTrue(result["verifiers"]["logValidate"]["killedRunMode"])
        # Save-reading verifiers are skipped on a torn (killed) save.
        self.assertEqual("SKIPPED", result["verifiers"]["analyzer"]["status"])
        self.assertEqual("SKIPPED", result["verifiers"]["expectations"]["status"])
        # Non-PASS snapshots diagnostics.
        self.assertTrue(result["collectLogs"]["ran"])

    def test_boot_crash_is_invalid_and_retryable(self):
        """INVALID(boot-crash): the stub exits during boot-wait with no response;
        run.py must classify INVALID(boot-crash), which should_retry marks
        retryable. Fails if a boot crash wedges the run or is not retried."""
        result, _ = self._run("bootcrash")

        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("boot-crash", result["subkind"])
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertTrue(hlib.should_retry(v, attempt=1, retry_policy="once"))


class StageFixtureContainmentTests(unittest.TestCase):
    """S1: stage_fixture must refuse a runSaveName that resolves outside saves/
    BEFORE any destructive rmtree/copytree, aborting INVALID(spec-invalid). A bug
    here is a saves/.. rmtree that wipes the whole instance."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-contain-")
        self.instance = os.path.join(self.tmp, "instance")
        self.saves = os.path.join(self.instance, "saves")
        os.makedirs(self.saves, exist_ok=True)
        # A sibling directory INSIDE the instance that a saves/.. escape would reach.
        self.sentinel = os.path.join(self.instance, "GameData")
        os.makedirs(self.sentinel, exist_ok=True)
        open(os.path.join(self.sentinel, "keep.txt"), "w").close()
        self.logger = run.HarnessLogger()

    def tearDown(self):
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_escape_leaf_aborts_spec_invalid_without_touching_disk(self):
        # saveTemplate leaf ".." -> target = saves/.. == the instance dir (escape).
        spec = {"fixture": {"saveTemplate": "fixtures/saves/..",
                            "injectedRecordings": "none", "craft": []}}
        ok, name, subkind = run.stage_fixture(spec, self.instance, run.Runtime(), self.logger)
        self.assertFalse(ok)
        self.assertEqual("spec-invalid", subkind)
        # Nothing was removed: the sibling sentinel (and its file) survive.
        self.assertTrue(os.path.isfile(os.path.join(self.sentinel, "keep.txt")),
                        "containment guard must abort BEFORE any rmtree")

    def test_strictly_inside_predicate(self):
        # A well-formed leaf is strictly inside; the saves dir itself and its
        # parent are not (equal / escape).
        self.assertTrue(run._is_strictly_inside(os.path.join(self.saves, "fresh-career"), self.saves))
        self.assertFalse(run._is_strictly_inside(self.saves, self.saves))
        self.assertFalse(run._is_strictly_inside(self.instance, self.saves))


if __name__ == "__main__":
    unittest.main()
