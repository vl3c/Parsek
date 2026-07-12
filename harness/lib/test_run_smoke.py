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
FAKE_MISSION = os.path.join(HERE, "_fake_mission.py")


class FakeRuntime(run.Runtime):
    """Injectable runtime that launches the fake-KSP stub instead of KSP_x64.exe
    and stubs the external verifier subprocesses. Process launch / poll / kill and
    the wall clock stay REAL, so the tail / budget / kill plumbing is exercised.

    M-B1: also fakes the mission subprocess (``_fake_mission.py``) and the venv
    stamp / requirements reads, so the autopilot handoff drives end to end with no
    real venv and no kRPC. ``mission_mode`` scripts the fake mission's verdict;
    ``venv_ok`` toggles the pre-launch venv admission; ``launch_count`` proves a
    venv refusal boots ZERO KSPs."""

    def __init__(self, mode, mission_mode="ok", venv_ok=True):
        self.mode = mode
        self.mission_mode = mission_mode
        self.venv_ok = venv_ok
        self.launch_count = 0

    def sleep(self, seconds):
        # Keep real time advancing (so budgets elapse) but spin fast.
        time.sleep(min(seconds, 0.05))

    def ksp_running(self, instance_dir):
        return None  # no zombie in the fake environment

    def resolve_exe(self, instance_dir):
        return sys.executable

    def launch(self, exe, args, env, cwd):
        import subprocess
        self.launch_count += 1
        return subprocess.Popen(
            [exe, FAKE_KSP, "--root", cwd, "--mode", self.mode],
            env=env, cwd=cwd,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # ---- M-B1 mission subprocess + venv I/O ------------------------------

    def read_venv_stamp(self, stamp_path):
        # venv_ok -> a stamp whose pins MATCH the requirements below (admit);
        # otherwise None (missing stamp -> venv_admission refuses tooling-venv).
        return {"pins": {"krpc": "0.5.4"}} if self.venv_ok else None

    def read_requirements_text(self, requirements_path):
        return "# committed pins\nkrpc==0.5.4\n"

    def spawn_mission(self, venv_python, mission_py, args, cwd, stdout_path):
        import subprocess
        # Extract the --result path run.py chose and drive the fake mission with the
        # test-injected mode. The venv python / mission_py are ignored (no real venv).
        result_path = list(args)[list(args).index("--result") + 1]
        out = open(stdout_path, "w", encoding="utf-8")
        try:
            return subprocess.Popen(
                [sys.executable, FAKE_MISSION, "--result", result_path,
                 "--mode", self.mission_mode],
                cwd=cwd, stdout=out, stderr=subprocess.STDOUT)
        finally:
            out.close()

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


def _make_autopilot_spec(save_template, mission_budget=30, run_budget=600):
    """A flown (kind=autopilot) scenario: LoadGame -> pin auto-record -> mission
    handoff -> CommitTree -> FlushAndQuit, expecting exactly one recording + the
    REC log lines a flown scenario produces (design B1 spec shape)."""
    return {
        "schema": 1,
        "id": "SMOKE-autopilot",
        "tier": "daily",
        "instanceProfile": "stock-minimal",
        "fixture": {"saveTemplate": save_template, "injectedRecordings": "none", "craft": []},
        "driver": {
            "kind": "autopilot",
            "mission": "fake_mission",
            "missionParams": {"throttle": 1.0,
                              "apoapsisWindowMeters": {"min": 6000, "max": 30000}},
            "steps": [
                {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"},
                 "expect": "OK", "budget": 30},
                {"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "true"},
                 "expect": "OK"},
                {"phase": "mission", "expect": "MISSION-OK", "budget": mission_budget},
                {"cmd": "CommitTree", "expect": "OK"},
                {"cmd": "FlushAndQuit", "expect": "OK"},
            ],
        },
        "expectations": {
            "recordings": {"count": {"min": 1, "max": 1}},
            "logContracts": {"required": ["Recording started", "Recording stopped"],
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
        # S6: a per-invocation harness log file alongside stdout.
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "smoke_harness.log"))

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
        # S6: the per-invocation harness log file exists and carries the verdict line.
        self.assertTrue(os.path.isfile(self.logger.log_path))
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            log_body = fh.read()
        self.assertIn("verdict=%s" % hlib.VERDICT_PASS, log_body,
                      "the harness log file must carry the Classify verdict line")

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


class AutopilotHandoffSmokeTests(unittest.TestCase):
    """M-B1 (design Test Plan "run.py handoff over a fake mission subprocess"): the
    autopilot handoff -- pre-launch venv admit, mission-kind step spawn, bounded
    wait, result read + verdict mapping -- driven end to end over a FAKE mission
    subprocess and a FAKE auto-recording KSP, with no real venv / kRPC / game."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-autopilot-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "b1-pad-craft")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "autopilot_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run(self, mission_mode="ok", venv_ok=True, mission_budget=30, run_budget=600):
        spec = _make_autopilot_spec(self.template, mission_budget, run_budget)
        rt = FakeRuntime("autopilot", mission_mode=mission_mode, venv_ok=venv_ok)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def test_mission_ok_drives_full_chain_to_pass(self):
        """(a) The mission writes MISSION-OK; the full chain LoadGame OK -> mission
        MET -> CommitTree -> FlushAndQuit -> verifiers -> PASS. Fails if the handoff
        mis-maps a MISSION-OK verdict, runs the mission before FLIGHT, or a flown
        recording is not counted."""
        result, rt = self._run("ok")

        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "expected PASS, got %s (%s)" % (result["verdict"], result.get("subkind")))
        # The KSP booted exactly once (no venv refusal).
        self.assertEqual(1, rt.launch_count)
        # The mission step appears inline as a driver.steps row with its verdict.
        steps = result["driver"]["steps"]
        mission_rows = [s for s in steps if s.get("phase") == "mission"]
        self.assertEqual(1, len(mission_rows))
        self.assertEqual("MISSION-OK", mission_rows[0]["missionVerdict"])
        self.assertTrue(mission_rows[0]["met"])
        self.assertIsNone(mission_rows[0]["subkind"])
        self.assertTrue(result["driver"]["allExpectedMet"])
        # The mission-validity gate passed AND the verifier chain judged Parsek's
        # recording (orthogonal): one recording, analyzer green, expectations PASS.
        v = result["verifiers"]
        self.assertEqual("PASS", v["driverValidity"]["status"])
        self.assertEqual("PASS", v["mission"]["status"])
        self.assertEqual("PASS", v["analyzer"]["status"])
        self.assertEqual("PASS", v["expectations"]["status"])
        # The per-attempt mission-result JSON landed under results/.
        mission_json = os.path.join(run.RESULTS_DIR, "%s_mission.json" % result["runId"])
        self.assertTrue(os.path.isfile(mission_json))

    def test_mission_assert_fail_is_invalid_mission_retryable(self):
        """(b) The mission writes MISSION-ASSERT-FAIL -> INVALID(mission),
        retry-once. Fails if an autopilot assertion miss poisons the Parsek-defect
        bucket (misread as PARSEK-FAIL) or is made non-retryable."""
        result, _ = self._run("assertfail")

        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("mission", result["subkind"])
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertTrue(hlib.should_retry(v, attempt=1, retry_policy="once"),
                        "INVALID(mission) must be retry-once")
        mission_rows = [s for s in result["driver"]["steps"] if s.get("phase") == "mission"]
        self.assertEqual("MISSION-ASSERT-FAIL", mission_rows[0]["missionVerdict"])
        self.assertEqual("mission", mission_rows[0]["subkind"])

    def test_venv_refusal_is_terminal_and_boots_no_ksp(self):
        """(c) A venv admission refusal at pre-launch ADMIT -> terminal
        INVALID(tooling-venv) with ZERO KSP boots and no retry. Fails if a
        missing/drifted venv boots KSP anyway or is wrongly made retryable."""
        result, rt = self._run(venv_ok=False)

        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("tooling-venv", result["subkind"])
        self.assertEqual(0, rt.launch_count, "venv refusal must boot ZERO KSPs")
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertFalse(hlib.should_retry(v, attempt=1, retry_policy="once"),
                         "tooling-venv is TERMINAL, never retried")

    def test_missing_result_file_is_tooling_mission(self):
        """(d) The mission exits nonzero without writing a result -> run.py fails
        closed to INVALID(tooling-mission) (edge 12). Fails if a missing result is
        read as a silent met or hangs the handoff."""
        result, _ = self._run("noresult")

        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("tooling-mission", result["subkind"])
        mission_rows = [s for s in result["driver"]["steps"] if s.get("phase") == "mission"]
        self.assertIsNone(mission_rows[0]["missionVerdict"])
        self.assertEqual("tooling-mission", mission_rows[0]["subkind"])


class MissionSpecAdmissionTests(unittest.TestCase):
    """M-B1 deliverable 1 (run.py spec admission): resolve_mission_schemas reads the
    mission's declared schema toml + confirms the mission .py resolves on disk, and
    a missing schema / missing .py is a spec-invalid INVALID (no KSP boot)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-missionadmit-")
        self._orig_missions = run.MISSIONS_DIR
        run.MISSIONS_DIR = os.path.join(self.tmp, "missions")
        os.makedirs(run.MISSIONS_DIR, exist_ok=True)

    def tearDown(self):
        run.MISSIONS_DIR = self._orig_missions
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _write_mission(self, name, schema_body):
        with open(os.path.join(run.MISSIONS_DIR, "%s.py" % name), "w", encoding="utf-8") as fh:
            fh.write("# fake mission shell\n")
        with open(os.path.join(run.MISSIONS_DIR, "%s.schema.toml" % name), "w", encoding="utf-8") as fh:
            fh.write(schema_body)

    def _autopilot_spec(self, mission):
        return {"driver": {"kind": "autopilot", "mission": mission,
                           "missionParams": {}, "steps": []}}

    def test_resolved_mission_yields_registry_no_errors(self):
        self._write_mission("b1_pad_hop", "[params]\n")
        registry, errors = run.resolve_mission_schemas(self._autopilot_spec("b1_pad_hop"))
        self.assertEqual([], errors)
        self.assertIn("b1_pad_hop", registry)

    def test_missing_py_is_spec_invalid_error(self):
        # schema present but no <mission>.py -> shell error (spec-invalid).
        with open(os.path.join(run.MISSIONS_DIR, "b1_pad_hop.schema.toml"), "w", encoding="utf-8") as fh:
            fh.write("[params]\n")
        registry, errors = run.resolve_mission_schemas(self._autopilot_spec("b1_pad_hop"))
        self.assertTrue(any("no mission script" in e for e in errors))

    def test_missing_schema_makes_pure_validator_reject_unknown(self):
        # .py present but no schema -> mission absent from registry; the pure
        # validator then rejects it as an unknown mission (no declared schema).
        with open(os.path.join(run.MISSIONS_DIR, "b1_pad_hop.py"), "w", encoding="utf-8") as fh:
            fh.write("# shell\n")
        registry, errors = run.resolve_mission_schemas(self._autopilot_spec("b1_pad_hop"))
        self.assertEqual([], errors)
        self.assertNotIn("b1_pad_hop", registry)
        spec = {"schema": 1, "id": "B1", "tier": "daily", "instanceProfile": "stock-minimal",
                "fixture": {"saveTemplate": "fixtures/saves/b1", "injectedRecordings": "none",
                            "craft": []},
                "driver": {"kind": "autopilot", "mission": "b1_pad_hop", "missionParams": {},
                           "steps": [
                               {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"},
                                "expect": "OK"},
                               {"phase": "mission", "expect": "MISSION-OK", "budget": 30},
                               {"cmd": "FlushAndQuit", "expect": "OK"}]},
                "runtime": {"budgetSeconds": 900}, "retry": {"policy": "once"},
                "expectedFail": {"bugId": ""}}
        validation = hlib.validate_spec(spec, {}, [], registry)
        self.assertFalse(validation.ok)
        self.assertTrue(any("unknown mission" in e for e in validation.errors))

    def test_non_autopilot_spec_is_noop(self):
        registry, errors = run.resolve_mission_schemas({"driver": {"kind": "seam"}})
        self.assertIsNone(registry)
        self.assertEqual([], errors)


if __name__ == "__main__":
    unittest.main()
