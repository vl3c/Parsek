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
import oracle  # noqa: E402
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

    def __init__(self, mode, mission_mode="ok", venv_ok=True, seed_mode="ok",
                 career_funds=25000.0, career_science=0.0, career_rep=0.0):
        self.mode = mode
        self.mission_mode = mission_mode
        self.venv_ok = venv_ok
        # M-B2 seed baseline seam. seed_mode scripts the pre-launch analyzer over the
        # STAGED template: "ok" (parsed career pools), "sandbox" (parsed, no pools ->
        # fixture-authoring INVALID), "unparsed" (parsed:false -> tooling INVALID),
        # "toolfail" (analyzer subprocess nonzero -> tooling INVALID). The produced-save
        # careerSave block (leg B) uses the same career_* pools.
        self.seed_mode = seed_mode
        self.career_funds = career_funds
        self.career_science = career_science
        self.career_rep = career_rep
        self.seed_analyzer_count = 0
        self.launch_count = 0
        self.mission_spawn_count = 0

    def _career_block_json(self):
        return {"parsed": True,
                "hasFunds": True, "funds": self.career_funds,
                "hasScience": True, "sciencePool": self.career_science,
                "hasRep": True, "reputation": self.career_rep,
                "subjectScience": {}, "facilityLevelFrac": {},
                "activeContractGuids": [], "completedMilestoneIds": [], "vessels": []}

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
        self.mission_spawn_count += 1
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
            # Additive careerSave block (leg B) so an active ledger-oracle slot 8 has a
            # produced-save careerSave to read; inert for non-ledger scenarios.
            json.dump({"counts": {"failNonBaselined": 0, "staleNonBaselined": 0},
                       "findings": [], "careerSave": self._career_block_json()}, fh)
        return run.ToolResult(0, False)

    def run_seed_analyzer(self, save_dir, out_dir, timeout):
        # M-B2 pre-launch seed baseline over the STAGED template. Writes the redirected
        # <leaf>.analysis.json that _capture_seed_baseline reads, scripted by seed_mode.
        self.seed_analyzer_count += 1
        if self.seed_mode == "toolfail":
            return run.ToolResult(1, False)
        os.makedirs(out_dir, exist_ok=True)
        leaf = os.path.basename(save_dir.rstrip("/\\"))
        if self.seed_mode == "unparsed":
            block = {"parsed": False}
        elif self.seed_mode == "sandbox":
            block = {"parsed": True, "hasFunds": False, "hasScience": False, "hasRep": False}
        else:  # "ok"
            block = self._career_block_json()
        with open(os.path.join(out_dir, "%s.analysis.json" % leaf), "w", encoding="utf-8") as fh:
            json.dump({"careerSave": block}, fh)
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


def _make_ledger_spec(save_template, run_tests_budget=30, run_budget=600, manifest=None):
    """A B10-shape seam scenario that ACTIVATES the M-B2 ledger oracle (slot 8) via a
    real [expectations.ledger] block, so run.py's seed-baseline capture + slot-8
    dispatch are exercised end to end over the fake Runtime seam."""
    spec = _make_spec(save_template, run_tests_budget, run_budget)
    spec["id"] = "SMOKE-ledger"
    spec["expectations"]["ledger"] = {
        "seedFrom": "template", "tolerances": "default", "rec3CarveOut": False,
        "manifest": manifest or [],
    }
    return spec


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


class LedgerSeedBaselineSmokeTests(unittest.TestCase):
    """Review SF8: the M-B2 run.py PLUMBING driven through run.run_attempt over the
    fake Runtime seam (no KSP). Covers _capture_seed_baseline's 4-way branch (skipped
    / ok / invalid-fixture / invalid-tooling), the run_seed_analyzer seam, the produced
    -save _read_career_save_block, and the run_verifiers slot-8 dispatch (active PASS /
    driver-invalid skip / killed skip). The edge-15 pre-launch terminal INVALIDs are
    asserted to boot ZERO KSPs, mirroring test_venv_refusal_is_terminal_and_boots_no_ksp."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-ledger-seed-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "ledger_seed_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run_ledger(self, mode="pass", seed_mode="ok", run_tests_budget=30, run_budget=600):
        spec = _make_ledger_spec(self.template, run_tests_budget, run_budget)
        rt = FakeRuntime(mode, seed_mode=seed_mode)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def _run_nonledger(self):
        spec = _make_spec(self.template, 30, 600)   # no [expectations.ledger]
        rt = FakeRuntime("pass")
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def test_non_ledger_scenario_skips_seed_capture(self):
        # Branch 1 (skipped): a scenario with no [expectations.ledger] never runs the
        # seed analyzer, and slot 8 records the reserved mb2-not-landed SKIP.
        result, rt = self._run_nonledger()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual(0, rt.seed_analyzer_count, "no ledger block -> no seed analyzer pass")
        self.assertEqual("SKIPPED", result["verifiers"]["ledgerOracle"]["status"])
        self.assertEqual("mb2-not-landed", result["verifiers"]["ledgerOracle"]["reason"])

    def test_ok_seed_and_clean_save_active_oracle_passes(self):
        # Branch 2 (ok) + active slot 8: the seed parses (funds/science/rep), the
        # produced save's careerSave equals the seed, the manifest is empty -> the
        # ledger oracle is ACTIVE and PASSes. Proves run_seed_analyzer,
        # _read_career_save_block, and the active dispatch are wired.
        result, rt = self._run_ledger(seed_mode="ok")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "expected PASS, got %s (%s)" % (result["verdict"], result.get("subkind")))
        self.assertEqual(1, rt.seed_analyzer_count)
        self.assertEqual(1, rt.launch_count)
        self.assertEqual("PASS", result["verifiers"]["ledgerOracle"]["status"])
        self.assertEqual(0, result["verifiers"]["ledgerOracle"]["hardDivergences"])
        # The accumulated manifest artifact landed with the careerSave-shape seed key.
        mpath = os.path.join(run.RESULTS_DIR, "%s.manifest.json" % result["runId"])
        self.assertTrue(os.path.isfile(mpath))
        with open(mpath, "r", encoding="utf-8") as fh:
            manifest = json.load(fh)
        self.assertIn("sciencePool", manifest["seed"])
        self.assertEqual(25000.0, manifest["seed"]["funds"])

    def test_sandbox_template_is_terminal_fixture_invalid_zero_boot(self):
        # Branch 3 (invalid-fixture): a template that parses but carries NO career pools
        # while [expectations.ledger] is declared is a fixture-authoring defect ->
        # terminal INVALID(fixture-authoring), booting ZERO KSPs (edge 15).
        result, rt = self._run_ledger(seed_mode="sandbox")
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("fixture-authoring", result["subkind"])
        self.assertEqual(1, rt.seed_analyzer_count)
        self.assertEqual(0, rt.launch_count, "edge-15 fixture INVALID must boot ZERO KSPs")
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertFalse(hlib.should_retry(v, attempt=1, retry_policy="once"),
                         "fixture-authoring is TERMINAL, never retried")

    def test_unparsable_template_is_terminal_tooling_invalid_zero_boot(self):
        # Branch 4 (invalid-tooling): the seed analyzer could not parse the template
        # (parsed:false) -> terminal INVALID(tooling), booting ZERO KSPs (edge 15).
        result, rt = self._run_ledger(seed_mode="unparsed")
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("tooling", result["subkind"])
        self.assertEqual(0, rt.launch_count, "edge-15 tooling INVALID must boot ZERO KSPs")

    def test_seed_analyzer_subprocess_failure_is_tooling_invalid_zero_boot(self):
        # Branch 4 variant: the seed analyzer SUBPROCESS failed (nonzero exit) -> the
        # block never reads -> terminal INVALID(tooling), ZERO boots.
        result, rt = self._run_ledger(seed_mode="toolfail")
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("tooling", result["subkind"])
        self.assertEqual(0, rt.launch_count)

    def test_killed_run_skips_ledger_slot(self):
        # Slot-8 dispatch on a KILLED attempt: a torn save is never ground truth, so the
        # ledger oracle is SKIPPED(killed) even though the block was declared (edge 11).
        result, rt = self._run_ledger(mode="hang", seed_mode="ok",
                                      run_tests_budget=1, run_budget=2)
        self.assertEqual(hlib.VERDICT_KILLED, result["verdict"])
        self.assertEqual("SKIPPED", result["verifiers"]["ledgerOracle"]["status"])
        self.assertEqual("killed", result["verifiers"]["ledgerOracle"]["reason"])

    def test_driver_invalid_skips_ledger_slot(self):
        # Slot-8 dispatch on a driver-INVALID (boot crash): a save from an invalid
        # driver run is not ground truth -> ledger oracle SKIPPED(driver-invalid).
        result, rt = self._run_ledger(mode="bootcrash", seed_mode="ok")
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("boot-crash", result["subkind"])
        self.assertEqual("SKIPPED", result["verifiers"]["ledgerOracle"]["status"])
        self.assertEqual("driver-invalid", result["verifiers"]["ledgerOracle"]["reason"])


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

    def test_loadgame_error_skips_mission_spawn(self):
        """SHOULD-FIX 3 (design handoff step 1): a boot whose LoadGame returns ERROR
        must NOT hand off to the mission -- run.py skips the mission spawn (no
        subprocess) so a dead boot never burns the 600-780s mission budget, and the
        run classifies INVALID with the load-failure attribution. Fails if a failed
        boot still spawns the mission (budget burned) or the mission step is misread
        as met."""
        # KSP mode "autopilot-loadfail" makes the boot LoadGame return ERROR.
        spec = _make_autopilot_spec(self.template, mission_budget=30, run_budget=600)
        rt = FakeRuntime("autopilot-loadfail", mission_mode="ok", venv_ok=True)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)

        # NO mission subprocess was spawned (the whole point: budget preserved).
        self.assertEqual(0, rt.mission_spawn_count,
                         "a failed LoadGame must NOT spawn the mission subprocess")
        # The run is a driver-INVALID attributed to the failed load, not a Parsek
        # defect and not a mission verdict.
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("load-failed", result["subkind"])
        # The mission step row is present, unmet, and carries the skip reason.
        mission_rows = [s for s in result["driver"]["steps"] if s.get("phase") == "mission"]
        self.assertEqual(1, len(mission_rows))
        self.assertFalse(mission_rows[0]["met"])
        self.assertIsNone(mission_rows[0]["missionVerdict"])
        self.assertIn("LoadGame", mission_rows[0].get("reason", ""))


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


class ReadMissionVerdictSchemaGateTests(unittest.TestCase):
    """SHOULD-FIX 6: _read_mission_verdict gates on the top-level `schema` -- a
    result whose schema != the one run.py understands is treated as UNREADABLE
    (None), so a future/legacy mission-result shape fails closed to
    tooling-mission instead of being mis-parsed. run.py does NOT import mlib; the
    schema constant is an inline mirror."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-verdict-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _write(self, obj):
        p = os.path.join(self.tmp, "m.json")
        with open(p, "w", encoding="utf-8") as fh:
            json.dump(obj, fh)
        return p

    def test_correct_schema_returns_verdict(self):
        p = self._write({"schema": run.MISSION_RESULT_SCHEMA, "verdict": "MISSION-OK"})
        self.assertEqual("MISSION-OK", run._read_mission_verdict(p))

    def test_wrong_schema_is_unreadable(self):
        p = self._write({"schema": 2, "verdict": "MISSION-OK"})
        self.assertIsNone(run._read_mission_verdict(p),
                          "a result carrying the wrong schema must read as unreadable")

    def test_missing_schema_is_unreadable(self):
        p = self._write({"verdict": "MISSION-OK"})
        self.assertIsNone(run._read_mission_verdict(p))

    def test_absent_file_is_none(self):
        self.assertIsNone(run._read_mission_verdict(os.path.join(self.tmp, "nope.json")))

    def test_no_verdict_is_none(self):
        p = self._write({"schema": run.MISSION_RESULT_SCHEMA})
        self.assertIsNone(run._read_mission_verdict(p))


class RequirementsCanonicalizationTests(unittest.TestCase):
    """NIT 10: run.py._parse_requirements canonicalizes the distribution name the
    same way bootstrap_venv does, so a NON-canonical committed pin round-trips
    bootstrap -> admission instead of drifting to a false tooling-venv refusal."""

    def test_non_canonical_pin_matches_canonical_stamp(self):
        # A non-canonically spelled committed pin (mixed case + underscore).
        reqs = run._parse_requirements("KRPC==0.5.4\nProto_Buf==4.21.0\n")
        # run.py must canonicalize to the same keys the stamp carries.
        self.assertEqual({"krpc": "0.5.4", "proto-buf": "4.21.0"}, reqs)
        # A stamp written with canonical pins admits the venv (no false drift).
        stamp = {"pins": {"krpc": "0.5.4", "proto-buf": "4.21.0"}}
        ok, subkind = hlib.venv_admission(stamp, reqs)
        self.assertTrue(ok, "canonical stamp must admit a non-canonical committed pin (subkind=%s)" % subkind)

    def test_matches_bootstrap_parse(self):
        # Both sides agree on the canonical key set for the same requirements body.
        import importlib.util
        boot_path = os.path.join(HARNESS_ROOT, "missions", "bootstrap_venv.py")
        spec = importlib.util.spec_from_file_location("bootstrap_venv", boot_path)
        boot = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(boot)
        body = "kRPC==0.5.4\n# comment\nprotobuf==4.21.0\n"
        self.assertEqual(boot.parse_requirements(body), run._parse_requirements(body))


class _MiniMissionRuntime(run.Runtime):
    """A minimal runtime that drives _drive_mission_step's mission-budget-expiry
    path deterministically: the spawned mission NEVER exits (poll_exit -> None) so
    the mission-step budget expires; ``write_result`` controls whether a real
    MISSION-OK result is present at expiry (NIT 7). now() advances a virtual clock
    so the budget elapses in a fixed number of polls without real waiting."""

    def __init__(self, write_result_verdict=None):
        self._t = 0.0
        self._write_verdict = write_result_verdict
        self._result_path = None

    def now(self):
        self._t += 0.5
        return self._t

    def sleep(self, seconds):
        pass

    def read_venv_stamp(self, stamp_path):
        return {"pins": {"krpc": "0.5.4"}}

    def spawn_mission(self, venv_python, mission_py, args, cwd, stdout_path):
        self._result_path = list(args)[list(args).index("--result") + 1]
        if self._write_verdict is not None:
            with open(self._result_path, "w", encoding="utf-8") as fh:
                json.dump({"schema": run.MISSION_RESULT_SCHEMA,
                           "verdict": self._write_verdict}, fh)
        return object()  # a dummy proc; poll_exit never reports it exits

    def poll_exit(self, proc):
        return None

    def kill_tree(self, proc):
        return []


class MissionBudgetExpiryFinalReadTests(unittest.TestCase):
    """NIT 7: on a mission-step-budget expiry run.py attempts ONE final result read
    (the mission may have finished writing a real verdict inside the last poll
    interval); a valid result is used, else the fabricated FLAKE row is tagged
    distinguishably so it never reads as the mission itself reporting FLAKE."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-nit7-")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        os.makedirs(run.RESULTS_DIR, exist_ok=True)
        self.logger = run.HarnessLogger()

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _drive(self, rt):
        result = run.DriveResult()
        ctx = run.MissionContext("m", "vpy", "m.py", {}, self.tmp,
                                 "stamp.json", {"krpc": "0.5.4"})
        step = {"phase": "mission", "expect": "MISSION-OK", "budget": 1}
        proc = type("P", (), {"pid": 12345})()
        killed = run._drive_mission_step(result, step, "0003", 2, proc, rt, self.logger,
                                         run_budget=10_000, run_start=0.0,
                                         mission_ctx=ctx, run_id="testrun",
                                         preceding_load_ok=True)
        return result, killed

    def test_result_written_before_expiry_is_used(self):
        # The mission wrote MISSION-OK just before the budget expiry kill; use it.
        rt = _MiniMissionRuntime(write_result_verdict="MISSION-OK")
        result, killed = self._drive(rt)
        self.assertFalse(killed)
        self.assertEqual("MISSION-OK", result.mission_step["missionVerdict"])
        self.assertTrue(result.mission_step["met"])
        self.assertNotIn("reason", result.mission_step)  # not the fabricated row

    def test_no_result_at_expiry_is_distinguishable_flake(self):
        # No result was written; the fabricated FLAKE row is tagged so it never
        # reads as the mission itself reporting FLAKE.
        rt = _MiniMissionRuntime(write_result_verdict=None)
        result, killed = self._drive(rt)
        self.assertFalse(killed)
        self.assertEqual(hlib.MISSION_VERDICT_FLAKE, result.mission_step["missionVerdict"])
        self.assertEqual("autopilot-flake", result.mission_step["subkind"])
        self.assertIn("no result", result.mission_step.get("reason", ""))


class LedgerOracleEndToEndTests(unittest.TestCase):
    """M-B2 end-to-end (design Test Plan "End-to-end (fake save JSON, no KSP)" ~830):
    the REAL ledger-oracle verifier path (run._run_ledger_oracle -> oracle
    compute/diff/build) driven over a FABRICATED careerSave block + manifest, with
    NO KSP. Covers the zero-drift PASS, the hard-facet drift -> PARSEK-FAIL(ledger),
    the report-only drift (logged not red), the absent-block tooling failure, and
    the empty-manifest cross-check catching an unenumerated award."""

    SEED = {"funds": 25000.0, "science": 0.0, "reputation": 0.0}

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-ledger-e2e-")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        os.makedirs(run.RESULTS_DIR, exist_ok=True)
        self.logger = run.HarnessLogger()

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _seed_capture(self, **overrides):
        vals = dict(self.SEED)
        vals.update(overrides)
        seed = oracle.SeedBaseline(funds=vals["funds"], science=vals["science"],
                                   reputation=vals["reputation"])
        block = {"parsed": True, "hasFunds": True, "hasScience": True, "hasRep": True}
        block.update({"funds": vals["funds"], "sciencePool": vals["science"],
                      "reputation": vals["reputation"]})
        return run.SeedCapture(seed, "ok", block)

    def _career_block(self, funds=25000.0, science=0.0, reputation=0.0,
                      subject_science=None, vessels=None):
        return {"parsed": True,
                "hasFunds": True, "funds": funds,
                "hasScience": True, "sciencePool": science,
                "hasRep": True, "reputation": reputation,
                "subjectScience": subject_science or {},
                "activeContractGuids": [],
                "vessels": vessels or []}

    def _ledger_block(self, manifest=None):
        return {"seedFrom": "template", "tolerances": "default", "rec3CarveOut": False,
                "manifest": manifest or []}

    def _run(self, ledger_block, career_block, log_text="", world_block=None, seed_capture=None):
        return run._run_ledger_oracle(
            ledger_block, world_block, career_block,
            seed_capture if seed_capture is not None else self._seed_capture(),
            log_text, "e2e-run", self.logger)

    def test_zero_drift_empty_manifest_passes(self):
        # Empty manifest + a careerSave block equal to the seed -> PASS, no hard drift.
        result, drift, tooling = self._run(self._ledger_block(), self._career_block())
        self.assertEqual("PASS", result["status"])
        self.assertEqual(0, result["hardDivergences"])
        self.assertFalse(drift)
        self.assertFalse(tooling)
        # The accumulated manifest artifact landed (deterministic, empty entries).
        mpath = os.path.join(run.RESULTS_DIR, "e2e-run.manifest.json")
        self.assertTrue(os.path.isfile(mpath))
        with open(mpath, "r", encoding="utf-8") as fh:
            manifest = json.load(fh)
        self.assertEqual([], manifest["entries"])
        self.assertEqual([], manifest["capturedRaw"])
        self.assertEqual(25000.0, manifest["seed"]["funds"])

    def test_hard_funds_drift_reds_ledger(self):
        # The cold-load wipe (BUG-F) / economy drift (BUG-A): the produced save's
        # funds moved beyond tolerance -> hard drift -> PARSEK-FAIL(ledger). This is
        # the most dangerous silent pass this module exists to prevent.
        result, drift, tooling = self._run(self._ledger_block(), self._career_block(funds=0.0))
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertFalse(tooling)
        self.assertGreaterEqual(result["hardDivergences"], 1)
        # classify_verdict maps ledger_drift -> PARSEK-FAIL(ledger).
        d, v = _clean_ledger_facts()
        v["ledger_drift"] = True
        verdict = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(("PARSEK-FAIL", "ledger"), (verdict.verdict, verdict.subkind))

    def test_unexpected_award_reds_with_named_ut_window(self):
        # Empty manifest but a stock award line fired at ut=500 -> unexpected award
        # (economy-drift signal) -> hard drift with the UT window NAMED (edge 4). The
        # save itself is clean; the capture cross-check is what reds.
        log = ("[LOG] [Parsek][INFO][Recorder] tick ut=500.0\n"
               "[LOG] ContractSystem: contract Foo completed guid=g-9 funds=1000\n")
        result, drift, tooling = self._run(self._ledger_block(), self._career_block(), log_text=log)
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertEqual([500.0, 500.0], result["utWindow"])
        # capturedRaw records the fired award for audit.
        with open(os.path.join(run.RESULTS_DIR, "e2e-run.manifest.json"), "r", encoding="utf-8") as fh:
            manifest = json.load(fh)
        self.assertEqual(1, len(manifest["capturedRaw"]))

    def test_report_only_drift_logged_not_red(self):
        # A per-subject science difference is REPORT-ONLY (the false positive
        # LedgerGroundTruthDiff avoids): logged, counted, never red. The hard pools
        # match the seed.
        career = self._career_block(subject_science={"crewReport@KerbinSrfLandedLaunchPad": 5.0})
        result, drift, tooling = self._run(self._ledger_block(), career)
        self.assertEqual("PASS", result["status"])   # no HARD drift
        self.assertFalse(drift)
        self.assertGreaterEqual(result["reportOnly"], 1)

    def test_malformed_seam_entry_reds_ledger(self):
        # Review SF6a / design edge 18: a seam entry with an unknown kind is a DROPPED
        # expected effect. It must RED PARSEK-FAIL(ledger), not be warn-logged and
        # dropped (a dropped expected effect can false-PASS). The save itself is clean.
        ledger = self._ledger_block(manifest=[{"kind": "not-a-real-kind", "funds": 5.0}])
        result, drift, tooling = self._run(ledger, self._career_block())
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertFalse(tooling)
        self.assertGreaterEqual(result["hardDivergences"], 1)

    def test_unfillable_funds_seam_entry_reds_ledger(self):
        # Review SF6a: a funds fill-from-capture seam entry with NO matching captured
        # award is un-fillable -> ambiguous rejection -> hard drift (never silently
        # dropped). Empty log = nothing to fill from.
        ledger = self._ledger_block(manifest=[
            {"ut": 500.0, "kind": "contract-complete", "funds": None, "contractGuid": "g"}])
        result, drift, tooling = self._run(ledger, self._career_block(), log_text="")
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)

    def test_funds_fill_from_capture_is_wired(self):
        # Review SF6b: the deduped captured award pool is now passed to the seam parse,
        # so a funds fill-from-capture seam entry resolves from the matching stock award
        # (before the fix captured was never passed and this ALWAYS failed ambiguous).
        # seam declares the contract-complete with a null funds amount; the stock line
        # supplies 1000; expected funds = seed 25000 + 1000, matched by the save -> PASS.
        log = ("[LOG] [Parsek][INFO][Recorder] tick ut=500.0\n"
               "[LOG] ContractSystem: contract Foo completed guid=g funds=1000\n")
        ledger = self._ledger_block(manifest=[
            {"ut": 500.0, "kind": "contract-complete", "funds": None, "contractGuid": "g"}])
        result, drift, tooling = self._run(ledger, self._career_block(funds=26000.0), log_text=log)
        self.assertEqual("PASS", result["status"])
        self.assertFalse(drift)
        self.assertFalse(tooling)
        # The manifest records the FILLED seam entry (funds resolved from capture).
        with open(os.path.join(run.RESULTS_DIR, "e2e-run.manifest.json"), "r", encoding="utf-8") as fh:
            manifest = json.load(fh)
        self.assertEqual(1, len(manifest["entries"]))
        self.assertEqual(1000.0, manifest["entries"][0]["funds"])

    def test_absent_career_block_is_tooling_invalid(self):
        # An ACTIVE ledger verifier with an ABSENT careerSave block (old/broken
        # analyzer) is INVALID(tooling), NEVER a silent pass (edge 13).
        result, drift, tooling = self._run(self._ledger_block(), None)
        self.assertEqual("INVALID", result["status"])
        self.assertEqual("tooling", result["subkind"])
        self.assertTrue(tooling)
        self.assertFalse(drift)

    def test_world_only_vessel_resource_drift_reds(self):
        # A world-only activation (no ledger block, no seed): a guid-correlated vessel
        # resource outside tolerance is a hard world mismatch -> PARSEK-FAIL(ledger).
        career = self._career_block(vessels=[
            {"pid": "v-guid-1", "persistentId": 100000, "name": "X", "type": "Ship",
             "resourceTotals": {"LiquidFuel": 40.0}}])
        world = {"vessels": {"entry": [
            {"guid": "v-guid-1", "resources": {"LiquidFuel": {"expected": 90.0, "tol": 0.1}}}]}}
        result, drift, tooling = run._run_ledger_oracle(
            None, world, career, None, "", "e2e-world", self.logger)
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertFalse(tooling)

    def test_world_only_vessel_resource_within_tolerance_passes(self):
        career = self._career_block(vessels=[
            {"pid": "v-guid-1", "persistentId": 100000, "name": "X", "type": "Ship",
             "resourceTotals": {"LiquidFuel": 90.05}}])
        world = {"vessels": {"entry": [
            {"guid": "v-guid-1", "resources": {"LiquidFuel": {"expected": 90.0, "tol": 0.1}}}]}}
        result, drift, tooling = run._run_ledger_oracle(
            None, world, career, None, "", "e2e-world-ok", self.logger)
        self.assertEqual("PASS", result["status"])
        self.assertFalse(drift)


def _clean_ledger_facts():
    """A clean driver-valid facts pair (mirrors test_hlib's _clean_pass_facts) with
    every verifier PASS, so a single toggled verifier flag drives the verdict."""
    driver = {"spec_valid": True, "admission_ok": True, "instance_lock_ok": True,
              "instance_busy": False, "boot_crashed": False, "boot_crash_repeated": False,
              "batch_crashed": False, "valid": True, "stage_subkind": ""}
    verifiers = {"killed": False, "batch_expected": False, "batch_present": True,
                 "analyzer": hlib.AnalyzerVerdict("PASS", "", None),
                 "log_validate_failed": False, "results_failed": False,
                 "results_mismatch": False, "anomaly_hit": False,
                 "expectation_mismatch": False, "ledger_drift": False}
    return driver, verifiers


if __name__ == "__main__":
    unittest.main()
