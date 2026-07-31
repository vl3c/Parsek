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
                 career_funds=25000.0, career_science=0.0, career_rep=0.0,
                 analyzer_fail_calls=0, produced_parsed=True):
        self.mode = mode
        # Item 10: when False the PRODUCED-save careerSave block is {parsed:false}, so an
        # active ledger-oracle slot 8 must classify tooling INVALID (the analyzer could
        # not parse the produced save), never PARSEK-FAIL missing-facet.
        self.produced_parsed = produced_parsed
        self.mission_mode = mission_mode
        self.venv_ok = venv_ok
        # M-A5.1 subprocess-scoped retry seam: the FIRST `analyzer_fail_calls`
        # run_analyzer invocations simulate a WEDGED analyzer (timed_out tooling fault,
        # no report written); subsequent calls produce the clean RED=0 report. Counts
        # across BOTH the in-attempt subprocess retry and any whole-attempt retry.
        self.analyzer_fail_calls = analyzer_fail_calls
        self.analyzer_call_count = 0
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

    def _career_block_json(self, produced=False):
        # produced=True is the PRODUCED-save block (run_analyzer). Only it honors
        # produced_parsed; the SEED block (run_seed_analyzer, produced=False) always
        # parses so seed_mode alone scripts the seed lane.
        if produced and not self.produced_parsed:
            return {"parsed": False}
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

    def run_inject(self, instance_dir, save_name, timeout, preset="all-synthetic"):
        rec = os.path.join(instance_dir, "saves", save_name, "Parsek", "Recordings")
        os.makedirs(rec, exist_ok=True)
        for i in range(8):
            open(os.path.join(rec, "rec%02d.prec" % i), "w").close()
        return run.ToolResult(0, False)

    def run_analyzer(self, save_dir, fresh_gate, timeout):
        self.analyzer_call_count += 1
        if self.analyzer_call_count <= self.analyzer_fail_calls:
            # Wedged analyzer: a per-subprocess timeout tooling fault, no report written.
            return run.ToolResult(-1, True)
        analysis = os.path.join(save_dir, "analysis")
        os.makedirs(analysis, exist_ok=True)
        leaf = os.path.basename(save_dir.rstrip("/\\"))
        with open(os.path.join(analysis, "%s.analysis.txt" % leaf), "w", encoding="utf-8") as fh:
            fh.write("[Analyzer] save=%s findings=0 FAIL=0 STALE=0 RED=0\n" % leaf)
        with open(os.path.join(analysis, "%s.analysis.json" % leaf), "w", encoding="utf-8") as fh:
            # Additive careerSave block (leg B) so an active ledger-oracle slot 8 has a
            # produced-save careerSave to read; inert for non-ledger scenarios.
            json.dump({"counts": {"failNonBaselined": 0, "staleNonBaselined": 0},
                       "findings": [], "careerSave": self._career_block_json(produced=True)}, fh)
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
        result_path = os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"])
        self.assertTrue(os.path.isfile(result_path))
        # ... and it carries the MEASURED recordings count, not just the verdict.
        # This is the ONLY place a green run's count survives: a PASS runs no
        # collect-logs and the produced save is transient, so without this the
        # number needed to pin a provisional count window is gone forever.
        with open(result_path, "r", encoding="utf-8") as fh:
            persisted = json.load(fh)
        self.assertEqual({"recordings": {"count": 0}},
                         persisted["verifiers"]["expectations"]["observed"],
                         "the measured count must round-trip into results/<runId>.json")
        # S6: the per-invocation harness log file exists and carries the verdict line.
        self.assertTrue(os.path.isfile(self.logger.log_path))
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            log_body = fh.read()
        self.assertIn("verdict=%s" % hlib.VERDICT_PASS, log_body,
                      "the harness log file must carry the Classify verdict line")

    def test_report_only_rows_land_on_a_clean_pass(self):
        """The two REPORT-ONLY channels must be MEASURED on a green run, not only on
        a red one - that is the whole point of a non-gating row.

        `anomalySweep.hitCounts` and the `unityExceptions` row are the calibration
        data an operator needs before arming either ceiling, and a PASS runs no
        collect-logs, so if the numbers are not in results/<runId>.json on a green run
        they do not exist anywhere. This cell also pins that neither row moves a clean
        verdict."""
        result, _ = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        v = result["verifiers"]

        sweep = v["anomalySweep"]
        self.assertEqual("PASS", sweep["status"])
        self.assertEqual({}, sweep["hitCounts"], "a clean log raises no gated anomaly")

        ue = v["unityExceptions"]
        self.assertEqual(hlib.UNITY_EXCEPTIONS_STATUS_REPORT, ue["status"],
                         "the scan must be REPORT-ONLY with no [expectations."
                         "unityExceptions] block declared")
        self.assertFalse(ue["gating"])
        self.assertEqual(0, ue["total"])
        self.assertIsNone(ue["maxTotal"])
        # Every pattern reports a number, so "we looked and saw none" is on the record.
        self.assertEqual(sorted(n for n, _ in hlib.UNITY_EXCEPTION_PATTERNS),
                         sorted(ue["counts"]))
        # ... and both rows round-trip into the durable result.
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"]),
                  "r", encoding="utf-8") as fh:
            persisted = json.load(fh)
        self.assertEqual(0, persisted["verifiers"]["unityExceptions"]["total"])
        self.assertIn("hitCounts", persisted["verifiers"]["anomalySweep"])

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
        # A KILLED run deliberately does not read the torn save at all
        # (recordingCount is None), so it carries no measured facets. The
        # SKIPPED-with-observed contract belongs to the non-killed
        # short-circuit / driver-INVALID branch -- see
        # test_loadgame_error_skips_mission_spawn.
        self.assertNotIn("observed", result["verifiers"]["expectations"])
        # The raw-Unity-exception scan STILL reports on a killed attempt: the kill
        # tears the SAVE, not the log, and an exception storm is a leading suspect for
        # whatever hung the process. Never gating on this path.
        ue = result["verifiers"]["unityExceptions"]
        self.assertEqual(hlib.UNITY_EXCEPTIONS_STATUS_REPORT, ue["status"])
        self.assertFalse(ue["gating"])
        self.assertEqual("killed-triage-only", ue["reason"])
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

    def _run_ledger(self, mode="pass", seed_mode="ok", run_tests_budget=30, run_budget=600,
                    produced_parsed=True):
        spec = _make_ledger_spec(self.template, run_tests_budget, run_budget)
        rt = FakeRuntime(mode, seed_mode=seed_mode, produced_parsed=produced_parsed)
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
        # seed analyzer, and slot 8 records the reserved no-ledger-block-declared SKIP.
        result, rt = self._run_nonledger()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual(0, rt.seed_analyzer_count, "no ledger block -> no seed analyzer pass")
        self.assertEqual("SKIPPED", result["verifiers"]["ledgerOracle"]["status"])
        self.assertEqual("no-ledger-block-declared", result["verifiers"]["ledgerOracle"]["reason"])

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

    def test_produced_careersave_parsed_false_is_tooling_invalid(self):
        # Item 10: the SEED parses (ok) + KSP boots, but the PRODUCED-save careerSave is
        # {parsed:false} (the analyzer could not parse the produced save). An active
        # ledger verifier must classify tooling INVALID (a parse fault), never
        # PARSEK-FAIL missing-facet off the all-absent diff.
        result, rt = self._run_ledger(seed_mode="ok", produced_parsed=False)
        self.assertEqual(1, rt.launch_count, "the seed parsed, so KSP still booted")
        self.assertEqual("INVALID", result["verifiers"]["ledgerOracle"]["status"])
        self.assertEqual("tooling", result["verifiers"]["ledgerOracle"]["subkind"])
        self.assertIn("parsed=false", result["verifiers"]["ledgerOracle"]["reason"])
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])

    def test_sandbox_template_with_declared_entries_is_terminal_fixture_invalid_zero_boot(self):
        # Branch 3 (invalid-fixture): a template that parses but carries NO career
        # pools while the declared manifest expects a DELTA is a fixture-authoring
        # defect (an expected delta needs a pool to land in) -> terminal
        # INVALID(fixture-authoring), booting ZERO KSPs (edge 15).
        spec = _make_ledger_spec(self.template, 30, 600, manifest=[{
            "action": "research-node", "facet": "science", "amount": -5.0,
            "amountKind": "delta", "utWindow": "any",
        }])
        rt = FakeRuntime("pass", seed_mode="sandbox")
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("fixture-authoring", result["subkind"])
        self.assertEqual(1, rt.seed_analyzer_count)
        self.assertEqual(0, rt.launch_count, "edge-15 fixture INVALID must boot ZERO KSPs")
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertFalse(hlib.should_retry(v, attempt=1, retry_policy="once"),
                         "fixture-authoring is TERMINAL, never retried")

    def test_sandbox_template_with_empty_manifest_boots_and_passes(self):
        # SANDBOX carve-out (career-fixtures review resolution (a), 2026-07-23):
        # a pool-less template with an EMPTY declared manifest is the
        # L1-passive-sandbox contract -- the seed is accepted as all-None,
        # compute_expected yields all-None, the diff facet-skips, and the run
        # BOOTS and PASSes with the ledger oracle ACTIVE (proving the
        # facet-skip path + the trusted empty-manifest cross-check over a
        # pool-less save, which dropping [expectations.ledger] would discard).
        result, rt = self._run_ledger(seed_mode="sandbox")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual(1, rt.seed_analyzer_count)
        self.assertEqual(1, rt.launch_count, "the carve-out must BOOT the run")
        self.assertEqual("PASS", result["verifiers"]["ledgerOracle"]["status"])

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
        # The FLOWN shape records its measured count too (the mission dropped one
        # .prec), so an orbit-lane operator can pin a provisional window off a
        # green autopilot run without re-flying to catch the save.
        self.assertEqual({"recordings": {"count": 1}}, v["expectations"]["observed"])
        # The per-attempt mission-result JSON landed under results/.
        mission_json = os.path.join(run.RESULTS_DIR, "%s_mission.json" % result["runId"])
        self.assertTrue(os.path.isfile(mission_json))
        # G6: the mission's own wall span rides the harness result, so the
        # harness-vs-mission residue (KSP boot + verifier chain) is a plain
        # subtraction rather than an investigation. The fake mission reports 1.
        self.assertEqual(1, result["missionWallSeconds"])
        self.assertIsNotNone(result["wallSeconds"])

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
        # The expectations verifier is SKIPPED on a driver-INVALID save -- but
        # it still records the MEASURED facets. `observed` exists so a run's
        # numbers survive into results/<runId>.json instead of dying with the
        # transient save, and a run that came back INVALID is exactly the run
        # whose recording count an operator needs (did the save get ANY
        # recordings before the driver failed?). recording_count is already
        # computed for the comparison; it used to be dropped on this branch.
        exp = result["verifiers"]["expectations"]
        self.assertEqual("SKIPPED", exp["status"])
        self.assertEqual({"recordings": {"count": 0}}, exp["observed"],
                         "the measured count must survive a SKIPPED expectations verifier")


class PostMissionOutcomeSmokeTests(unittest.TestCase):
    """EVA-4-atmo-chute flight 3 (2026-07-25) driven end to end over the fake KSP:
    the mission returns MISSION-OK and the post-mission EvaChuteDeploy answers
    ERROR msg=eva-chute-kerbal-lost.

    The defect these cells guard is the FAIL-OPEN, not the death. On the real run
    driverValidity reported PASS beside `allExpectedMet: false` and the only thing
    that red'd the run was the scenario author's own `\\[Parsek\\]\\[ERROR\\]`
    forbidden pattern. `test_a_blind_spec_still_reds` is the load-bearing cell: it
    strips every expectation that could notice, so the ONLY thing left that can red
    the run is the outcome step's own verdict."""

    TAIL = [
        {"cmd": "EvaExit", "args": {"release": "true"}, "expect": "OK", "budget": 120},
        {"cmd": "EvaChuteDeploy", "args": {"awaitDown": "true"}, "expect": "OK", "budget": 420},
        {"cmd": "StopRecording", "expect": "OK"},
        {"cmd": "CommitTree", "expect": "OK"},
        {"cmd": "FlushAndQuit", "expect": "OK"},
    ]

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-outcome-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "b1-pad-craft")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "outcome_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run(self, ksp_mode, blind=False):
        spec = _make_autopilot_spec(self.template)
        spec["driver"]["steps"] = spec["driver"]["steps"][:3] + [dict(s) for s in self.TAIL]
        if blind:
            # Every expectation that could independently notice a dead kerbal,
            # removed: no forbidden ERROR pattern, no completion-token requirement.
            spec["expectations"]["logContracts"] = {"required": [], "forbidden": []}
        rt = FakeRuntime(ksp_mode, mission_mode="ok", venv_ok=True)
        return run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                               prior_boot_crashed=False, logger=self.logger)

    def test_a_blind_spec_still_reds(self):
        """THE fail-open cell. With the expectations verifier deliberately blinded,
        a run whose kerbal died still reds - on the outcome step's own verdict."""
        result = self._run("autopilot-kerballost", blind=True)
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        self.assertEqual("mission-outcome", result["subkind"])
        # Prove the blinding worked, i.e. the red did NOT come from expectations.
        self.assertEqual("PASS", result["verifiers"]["expectations"]["status"])
        # ... and never retried into a green.
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertFalse(hlib.should_retry(v, attempt=1, retry_policy="once"))

    def test_the_real_spec_reds_naming_the_cause_not_the_symptom(self):
        result = self._run("autopilot-kerballost")
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        # The expectations verifier DOES also catch it (the forbidden [Parsek][ERROR]),
        # exactly as it did on 2026-07-25 - but the subkind reported is the cause.
        self.assertEqual("FAIL", result["verifiers"]["expectations"]["status"])
        self.assertEqual("mission-outcome", result["subkind"])

    def test_the_result_record_names_the_step_and_its_terminal(self):
        result = self._run("autopilot-kerballost")
        row = result["verifiers"]["missionOutcome"]
        self.assertEqual("FAIL", row["status"])
        self.assertEqual(["EvaExit", "EvaChuteDeploy"], row["gatingVerbs"])
        self.assertEqual("EvaChuteDeploy", row["firstUnmet"]["cmd"])
        self.assertEqual("ERROR", row["firstUnmet"]["verdict"])
        self.assertEqual("eva-chute-kerbal-lost", row["firstUnmet"]["msg"])
        # The mission itself is still reported MET - it flew the craft into the
        # envelope and handed off, which is all it ever claimed to do.
        self.assertEqual("PASS", result["verifiers"]["mission"]["status"])
        self.assertEqual("MISSION-OK", result["verifiers"]["mission"]["missionVerdict"])

    def test_a_healthy_run_with_the_same_tail_still_passes(self):
        """Non-regression: the SAME EVA tail, every step OK, is untouched."""
        result = self._run("autopilot")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        row = result["verifiers"]["missionOutcome"]
        self.assertEqual("PASS", row["status"])
        self.assertIsNone(row["firstUnmet"])

    def test_a_refusal_is_a_retryable_driver_fault_not_a_parsek_defect(self):
        """A post-mission SPEC fault (here a typo'd kerbal name -> EvaExit answers
        REJECTED msg=kerbal-not-aboard) must classify exactly as the same refusal
        would pre-mission: retryable driver-stage INVALID, never PARSEK-FAIL filed
        against the mod. Fails if the gate collapses every outcome-verb terminal into
        one cause."""
        result = self._run("autopilot-evarefused")
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertNotEqual("mission-outcome", result["subkind"])
        self.assertIn(result["subkind"], hlib.RETRYABLE_INVALID_SUBKINDS)
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertTrue(hlib.should_retry(v, attempt=1, retry_policy="once"))
        # The row still NAMES it, with the classification it took.
        row = result["verifiers"]["missionOutcome"]
        self.assertEqual("EvaExit", row["firstUnmet"]["cmd"])
        self.assertFalse(row["firstUnmet"]["flightOutcome"])
        self.assertTrue(row["firstUnmet"]["driverSubkind"])


class UnmetMissionTailSmokeTests(unittest.TestCase):
    """The unmet-mission tail, driven end to end over the fake KSP + fake mission
    (design "The unmet-mission tail"). The regression it guards is the EVA-4-atmo-
    chute flight-1 incident (2026-07-24): the mission ASSERT-FAILed with
    eva-window-missed and the harness drove the tail anyway, writing an EvaExit at
    terminal velocity to the channel. These cells assert on the CHANNEL FILE -- the
    only place that proves a command was never sent."""

    #  The REAL EVA-4 tail shape, verb for verb: the two irreversible in-world actions
    #  flight 1 fired at terminal velocity, then teardown, the commit, the quit.
    TAIL = [
        {"cmd": "EvaExit", "args": {"release": "true"}, "expect": "OK", "budget": 120},
        {"cmd": "EvaChuteDeploy", "args": {"awaitDown": "true"}, "expect": "OK", "budget": 420},
        {"cmd": "StopRecording", "expect": "OK"},
        {"cmd": "CommitTree", "expect": "OK"},
        {"cmd": "FlushAndQuit", "expect": "OK"},
    ]

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-unmettail-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "b1-pad-craft")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "unmettail_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run(self, mission_mode, skip_tail=None):
        spec = _make_autopilot_spec(self.template)
        # Replace the stock CommitTree/FlushAndQuit tail with the EVA-shaped one.
        spec["driver"]["steps"] = spec["driver"]["steps"][:3] + [dict(s) for s in self.TAIL]
        if skip_tail is not None:
            spec["driver"]["skipTailOnUnmetMission"] = skip_tail
        rt = FakeRuntime("autopilot", mission_mode=mission_mode, venv_ok=True)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, self._commands()

    def _commands(self):
        path = os.path.join(self.instance, "parsek-test-commands.txt")
        if not os.path.isfile(path):
            return []
        with open(path, "r", encoding="utf-8") as fh:
            return [l.strip() for l in fh if l.strip()]

    def _verbs(self, lines):
        out = []
        for line in lines:
            for tok in line.split():
                if tok.startswith("cmd="):
                    out.append(tok[4:])
        return out

    def test_unmet_mission_never_writes_the_in_world_verb_to_the_channel(self):
        """The incident cell: MISSION-ASSERT-FAIL -> EvaExit and CommitTree are never
        written to the command channel, while StopRecording and FlushAndQuit still
        are. Fails the moment the harness goes back to driving a world-mutating tail
        over a mission that never reached its envelope."""
        result, lines = self._run("assertfail")
        verbs = self._verbs(lines)

        self.assertNotIn("EvaExit", verbs, "an UNMET mission must not EVA a kerbal")
        self.assertNotIn("EvaChuteDeploy", verbs,
                         "an UNMET mission must not deploy a kerbal chute at terminal velocity")
        self.assertNotIn("CommitTree", verbs, "an UNMET mission must not commit a junk tree")
        self.assertIn("StopRecording", verbs, "the recorder teardown must still run")
        self.assertIn("FlushAndQuit", verbs, "KSP must still be brought down cleanly")

        # The verdict is unchanged by the skip: the mission subkind still drives it,
        # and it is still retryable-once.
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("mission", result["subkind"])
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertTrue(hlib.should_retry(v, attempt=1, retry_policy="once"))

        # The skip is AUDITABLE in the durable record, outside driver.steps (a step
        # never sent is not an unmet step).
        skipped = result["driver"]["skippedTailSteps"]
        self.assertEqual(["EvaExit", "EvaChuteDeploy", "CommitTree"],
                         [r["cmd"] for r in skipped])
        self.assertEqual(["world-mutating"] * 3, [r["role"] for r in skipped])
        self.assertEqual(["mission-unmet"], sorted({r["reason"] for r in skipped}))
        self.assertNotIn("EvaExit", [s.get("cmd") for s in result["driver"]["steps"]])

        # ... and in the verifiers block, explaining why the produced save is thin.
        self.assertEqual("SKIPPED", result["verifiers"]["unmetMissionTail"]["status"])
        self.assertEqual("mission-unmet", result["verifiers"]["unmetMissionTail"]["reason"])

    def test_met_mission_still_drives_the_whole_tail(self):
        """The non-regression cell for the MISSION-OK path every autopilot scenario
        takes (B1/B2/B4/B5/B6/B7, BDOCK-1, EVA-4, the three FORGE specs): a met mission
        drives the FULL tail exactly as before, and the result record carries no skip
        rows at all."""
        result, lines = self._run("ok")
        verbs = self._verbs(lines)

        for verb in ("EvaExit", "EvaChuteDeploy", "StopRecording", "CommitTree",
                     "FlushAndQuit"):
            self.assertIn(verb, verbs, verb)
        self.assertNotIn("skippedTailSteps", result["driver"])
        self.assertNotIn("unmetMissionTail", result["verifiers"])

    def test_spec_opt_out_restores_the_legacy_full_tail(self):
        """skipTailOnUnmetMission=false drives everything, unmet mission or not --
        the escape hatch a scenario can take deliberately."""
        result, lines = self._run("assertfail", skip_tail=False)
        verbs = self._verbs(lines)

        self.assertIn("EvaExit", verbs)
        self.assertIn("CommitTree", verbs)
        self.assertNotIn("skippedTailSteps", result["driver"])
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("mission", result["subkind"])
        # The opt-out is VISIBLE in the durable record. Without this the JSON of an
        # opted-out unmet run is indistinguishable from one where the policy never
        # applied (both carry an empty skip list) and the opt-out would live only in
        # the harness log.
        self.assertFalse(result["driver"]["skipTailOnUnmetMission"])

    def test_default_policy_run_does_not_carry_the_opt_out_marker(self):
        """The mirror of the cell above: the marker is emitted ONLY when the opt-out was
        actually in force, so a default-policy record is unchanged."""
        result, _ = self._run("assertfail")
        self.assertNotIn("skipTailOnUnmetMission", result["driver"])
        ok_result, _ = self._run("ok")
        self.assertNotIn("skipTailOnUnmetMission", ok_result["driver"])

    def test_tooling_mission_no_result_also_skips_the_tail(self):
        """Every UNMET path skips, not just ASSERT-FAIL: a mission that wrote no
        readable result (edge 12, tooling-mission) is equally no evidence that the
        flight reached the state the tail assumes."""
        result, lines = self._run("noresult")
        verbs = self._verbs(lines)

        self.assertNotIn("EvaExit", verbs)
        self.assertNotIn("EvaChuteDeploy", verbs)
        # A POSITIVE assertion too: _commands() returns [] for a missing file, so a
        # negative-only cell would pass vacuously if the channel path ever drifted.
        self.assertIn("FlushAndQuit", verbs)
        self.assertEqual("tooling-mission", result["subkind"])
        self.assertEqual(["EvaExit", "EvaChuteDeploy", "CommitTree"],
                         [r["cmd"] for r in result["driver"]["skippedTailSteps"]])

    def test_load_failure_before_the_mission_also_skips_the_tail(self):
        """The UNMET paths that never spawn a mission at all (here: the boot LoadGame
        returned ERROR, so run.py skips the handoff) take the same skip. Pins that the
        skip keys off `met`, not off "a mission subprocess ran"."""
        spec = _make_autopilot_spec(self.template)
        spec["driver"]["steps"] = spec["driver"]["steps"][:3] + [dict(s) for s in self.TAIL]
        rt = FakeRuntime("autopilot-loadfail", mission_mode="ok", venv_ok=True)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)

        self.assertEqual(0, rt.mission_spawn_count, "a failed boot must not spawn a mission")
        self.assertEqual("load-failed", result["subkind"])
        verbs = self._verbs(self._commands())
        self.assertNotIn("EvaExit", verbs)
        self.assertNotIn("EvaChuteDeploy", verbs)
        self.assertIn("FlushAndQuit", verbs)
        self.assertEqual(["EvaExit", "EvaChuteDeploy", "CommitTree"],
                         [r["cmd"] for r in result["driver"]["skippedTailSteps"]])


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

    def test_mission_wall_seconds_shares_the_schema_gate(self):
        """G6: the wall read goes through the SAME schema gate as the verdict
        read, so a future/legacy result cannot leak a mis-scaled duration."""
        ok = self._write({"schema": run.MISSION_RESULT_SCHEMA,
                          "verdict": "MISSION-OK", "wallSeconds": 580.826})
        self.assertAlmostEqual(580.826, run._read_mission_wall_seconds(ok))
        bad = self._write({"schema": 2, "wallSeconds": 580.826})
        self.assertIsNone(run._read_mission_wall_seconds(bad))
        nowall = self._write({"schema": run.MISSION_RESULT_SCHEMA})
        self.assertIsNone(run._read_mission_wall_seconds(nowall))
        self.assertIsNone(run._read_mission_wall_seconds(
            os.path.join(self.tmp, "nope.json")))


class ScenarioCostAccountingTests(unittest.TestCase):
    """G6: each attempt wrote its own result and NOTHING summed them, so
    B7-duna burning 794 + 776 = 1,570 wall seconds across two INVALID attempts
    and producing nothing was traceable only as two unrelated summary lines."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-cost-")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        os.makedirs(run.RESULTS_DIR, exist_ok=True)
        self.lines = []
        self.logger = run.HarnessLogger(
            os.path.join(run.RESULTS_DIR, "cost_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _drive(self, verdicts, walls):
        """Run _run_scenario_with_retry over a stubbed run_attempt."""
        calls = {"n": 0}
        orig = run.run_attempt

        def fake_attempt(spec, instance_dir, umbrella_root, runtime, attempt,
                         prior_boot_crashed, logger):
            i = calls["n"]
            calls["n"] += 1
            return {"schema": hlib.SCHEMA_VERSION,
                    "runId": "2026-07-25_0100_S1%s" % ("_a2" if attempt == 2 else ""),
                    "scenarioId": "S1", "endedUtc": "2026-07-25T01:00:00Z",
                    "verdict": verdicts[i],
                    # A RETRYABLE subkind so attempt 1 actually retries (the
                    # B7-duna case was INVALID(mission)).
                    "subkind": "mission" if verdicts[i] == "INVALID" else "",
                    "note": "",
                    "wallSeconds": walls[i], "attempt": attempt,
                    "expectedFail": {"bugId": "", "matched": False}}

        run.run_attempt = fake_attempt
        try:
            return run._run_scenario_with_retry(
                {"id": "S1", "retry": {"policy": "once"}}, self.tmp, self.tmp,
                None, self.logger), calls["n"]
        finally:
            run.run_attempt = orig

    def _summary_lines(self):
        path = os.path.join(run.RESULTS_DIR, "summary.txt")
        if not os.path.isfile(path):
            return []
        with open(path, "r", encoding="utf-8") as fh:
            return [l for l in fh.read().splitlines() if l.strip()]

    def test_two_invalid_attempts_sum_into_one_number(self):
        # The measured B7-duna case: 794 + 776 = 1,570 s for nothing.
        result, attempts = self._drive(["INVALID", "INVALID"], [794, 776])
        self.assertEqual(2, attempts)
        self.assertEqual(1570, result["attemptsWallSeconds"])

    def test_single_attempt_records_its_own_cost(self):
        result, attempts = self._drive(["PASS", "PASS"], [627, 627])
        self.assertEqual(1, attempts)
        self.assertEqual(627, result["attemptsWallSeconds"])

    def test_plain_path_does_not_double_the_rolling_summary(self):
        """The enrichment re-write must not append a second summary.txt line
        for every run (write_result(append_summary=False))."""
        self._drive(["PASS", "PASS"], [627, 627])
        self.assertEqual(0, len(self._summary_lines()),
                         "the stubbed run_attempt writes no summary; the cost "
                         "re-write must not add one either")

    def test_flaked_then_passed_still_records_its_note_line(self):
        result, attempts = self._drive(["INVALID", "PASS"], [300, 620])
        self.assertEqual(2, attempts)
        self.assertEqual("flakedThenPassed", result["note"])
        self.assertEqual(920, result["attemptsWallSeconds"])
        summary = self._summary_lines()
        self.assertEqual(1, len(summary), summary)
        self.assertIn("note=flakedThenPassed", summary[0])


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

    def test_in_flight_venv_backstop_is_unmet_and_spawns_nothing(self):
        """The THIRD never-spawned UNMET path (after load-failed and no-result): the
        in-flight venv backstop, reachable only by a venv mutated AFTER pre-launch
        ADMIT. It must record an unmet mission step - which is what makes the tail skip
        fire - with NO subprocess spawned. Pins that the skip keys off `met`, not off
        "a mission subprocess ran"; the pre-launch refusal is a different path that
        never launches KSP at all."""
        class _StampGoodThenGone(_MiniMissionRuntime):
            """Admits at ADMIT, then the stamp vanishes before the backstop re-reads.
            Counts spawns locally rather than touching the shared fake."""
            def __init__(self):
                super().__init__(write_result_verdict=None)
                self.reads = 0
                self.spawns = 0

            def read_venv_stamp(self, stamp_path):
                self.reads += 1
                return {"pins": {"krpc": "0.5.4"}} if self.reads == 1 else None

            def spawn_mission(self, venv_python, mission_py, args, cwd, stdout_path):
                self.spawns += 1
                return super().spawn_mission(venv_python, mission_py, args, cwd, stdout_path)

        rt = _StampGoodThenGone()
        rt.reads = 1  # pretend ADMIT already consumed its read
        result = run.DriveResult()
        ctx = run.MissionContext("m", "vpy", "m.py", {}, self.tmp,
                                 "stamp.json", {"krpc": "0.5.4"})
        step = {"phase": "mission", "expect": "MISSION-OK", "budget": 60}
        proc = type("P", (), {"pid": 12345})()
        killed = run._drive_mission_step(result, step, "0003", 2, proc, rt, self.logger,
                                         run_budget=10_000, run_start=0.0,
                                         mission_ctx=ctx, run_id="testrun-venv",
                                         preceding_load_ok=True)

        self.assertFalse(killed)
        self.assertEqual(0, rt.spawns, "the backstop must spawn no subprocess")
        self.assertFalse(result.mission_step["met"], "an unmet step is what triggers the skip")
        self.assertEqual("tooling-venv", result.mission_step["subkind"])

    def test_run_budget_kill_drives_no_tail_at_all_not_even_cleanup(self):
        """The RUN-budget expiry is NOT an unmet-tail case and must not be documented as
        one: it kills the mission subprocess AND the KSP tree, so `drive_seam` returns
        immediately and NOTHING is driven, cleanup included. There is no process left to
        send a command to. Pins the distinction from a MISSION-STEP-budget expiry, which
        IS `met=False` and does take the cleanup-only tail."""
        rt = _MiniMissionRuntime(write_result_verdict=None)
        result = run.DriveResult()
        ctx = run.MissionContext("m", "vpy", "m.py", {}, self.tmp,
                                 "stamp.json", {"krpc": "0.5.4"})
        step = {"phase": "mission", "expect": "MISSION-OK", "budget": 10_000}
        proc = type("P", (), {"pid": 12345})()
        # run_budget already exhausted at entry -> the run-budget branch, not the
        # mission-step branch.
        killed = run._drive_mission_step(result, step, "0003", 2, proc, rt, self.logger,
                                         run_budget=0.0, run_start=0.0,
                                         mission_ctx=ctx, run_id="testrun-killed",
                                         preceding_load_ok=True)

        self.assertTrue(killed, "a run-budget expiry must report killed")
        self.assertTrue(result.killed)
        self.assertEqual("run", result.kill_scope)
        # No mission row, so drive_seam's unmet check cannot fire; and it returns before
        # any tail step, so nothing is skipped OR driven.
        self.assertIsNone(result.mission_step)
        self.assertEqual([], result.skipped_tail_steps)
        self.assertFalse(result.tail_skip_opted_out)


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

    def _ledger_block(self, manifest=None, capture_cross_check=None):
        block = {"seedFrom": "template", "tolerances": "default", "rec3CarveOut": False,
                 "manifest": manifest or []}
        if capture_cross_check is not None:
            block[hlib.LEDGER_CAPTURE_CROSS_CHECK_KEY] = capture_cross_check
        return block

    # A MEASURED stock award line (CL-1 flight 2, logs/2026-07-28_1913_CL-1-pod-impact
    # /KSP.log:10361) - the shape STOCK_AWARD_PATTERNS actually matches. Two earlier
    # generations of this fixture were INVENTED (`ContractSystem ... funds=1000`, then
    # `Added 1000 funds: 'RecordsSpeed'`), and both let these cells pass green while
    # the capture was a structural no-op in the field. REPUTATION IS THE ONLY FACET
    # KSP LOGS: `Funding.AddFunds` and `ResearchAndDevelopment.AddScience` carry no
    # Debug.Log at all, so a funds- or science-shaped fixture line here is by
    # definition a fiction. Keep this constant pinned to a line lifted verbatim from a
    # collected log.
    STOCK_REP_LINE = "[LOG] Added 0.9999995 (1) reputation: 'Progression'."

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

    def test_unexpected_award_reds_with_named_ut_window_when_armed(self):
        # Empty manifest but a stock award line fired at ut=500 -> unexpected award
        # (economy-drift signal) -> hard drift with the UT window NAMED (edge 4). The
        # save itself is clean; the capture cross-check is what reds.
        # ARMED explicitly (`captureCrossCheck = "gate"`): since the 2026-07-29 pattern
        # rewrite made the capture actually fire, the HARD path is opt-in so a live
        # capture cannot flip a committed scenario's verdict before calibration.
        log = ("[LOG] [Parsek][INFO][Recorder] tick ut=500.0\n"
               + self.STOCK_REP_LINE + "\n")
        result, drift, tooling = self._run(
            self._ledger_block(capture_cross_check=hlib.LEDGER_CAPTURE_CROSS_CHECK_GATE),
            self._career_block(), log_text=log)
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertEqual([500.0, 500.0], result["utWindow"])
        # capturedRaw records the fired award for audit.
        with open(os.path.join(run.RESULTS_DIR, "e2e-run.manifest.json"), "r", encoding="utf-8") as fh:
            manifest = json.load(fh)
        self.assertEqual(1, len(manifest["capturedRaw"]))
        self.assertEqual("Progression", manifest["capturedRaw"][0]["stockReason"])

    def test_unexpected_award_is_report_only_by_default(self):
        # THE FAIL-OPEN-SAFETY CELL for the pattern rewrite. The SAME log that reds
        # when armed must NOT red a scenario that declares no mode: the capture is
        # live now, and the L1 career scenarios trip stock milestone awards their seam
        # manifests never declared, so a default-on gate would red live-proven
        # nightlies on the strength of a regex. The award is still RECORDED
        # (report-only divergence + capturedRaw), so the calibration data an operator
        # needs to arm the gate is produced by the very runs that stay green.
        log = ("[LOG] [Parsek][INFO][Recorder] tick ut=500.0\n"
               + self.STOCK_REP_LINE + "\n")
        result, drift, tooling = self._run(self._ledger_block(), self._career_block(),
                                           log_text=log)
        self.assertEqual("PASS", result["status"])
        self.assertFalse(drift)
        self.assertFalse(tooling)
        self.assertGreaterEqual(result["reportOnly"], 1)
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

    def test_funds_fill_from_capture_is_unreachable_in_the_field(self):
        # Review SF6b wired the deduped capture pool into the seam parse so a funds
        # fill-from-capture entry could resolve from a matching stock award. That
        # mechanism is now provably UNREACHABLE against a real KSP.log, and this cell
        # pins the consequence rather than manufacturing an input to hide it.
        #
        # Two facts compose: (a) fill-from-capture is legal ONLY on the funds facet
        # (science and reputation fills are rejected outright - filling a
        # state-dependent facet from the capture would destroy M-B2 leg independence,
        # see oracle.parse_manifest_entries), and (b) KSP writes no funds award line
        # at all, so the funds capture pool is always empty. A funds `null` amount
        # therefore ALWAYS fails ambiguous -> hard drift, which is the correct
        # fail-closed outcome: an un-fillable expected effect must never be silently
        # dropped. Here a genuine rep award IS captured, and it still cannot fill the
        # funds entry.
        #
        # The fill mechanism is retained, not deleted - but BE HONEST ABOUT WHAT STILL
        # GUARDS IT. The pure mechanism keeps its coverage in test_oracle.py
        # (`test_fill_from_capture_state_independent_single_match` and siblings, which
        # feed synthetic captured entries directly). What this cell no longer guards is
        # the SF6b WIRING at run.py's `captured=captured_entries`: the FAIL asserted
        # here is satisfied identically by the pre-SF6b regression (captured never
        # passed -> the same ambiguous FAIL), so dropping that kwarg would go unnoticed
        # by the whole suite. That is accepted rather than fixed, because the only way
        # to restore an end-to-end positive is to feed a funds award line KSP does not
        # emit - the exact fiction this retirement removed.
        log = ("[LOG] [Parsek][INFO][Recorder] tick ut=500.0\n"
               + self.STOCK_REP_LINE + "\n")
        ledger = self._ledger_block(manifest=[
            {"ut": 500.0, "kind": "stock-funds-award", "funds": None}])
        result, drift, tooling = self._run(ledger, self._career_block(funds=26000.0), log_text=log)
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertFalse(tooling)
        # The rep award was captured and recorded for audit - the failure is the
        # un-fillable funds entry, NOT an empty capture.
        with open(os.path.join(run.RESULTS_DIR, "e2e-run.manifest.json"), "r", encoding="utf-8") as fh:
            manifest = json.load(fh)
        self.assertEqual(1, len(manifest["capturedRaw"]))
        self.assertEqual("Progression", manifest["capturedRaw"][0]["stockReason"])

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


class SubprocessScopedRetrySmokeTests(unittest.TestCase):
    """M-A5.1 item 1 over the REAL run loop (fake runtime): a wedged analyzer
    subprocess is re-run once over the SAME produced save before the whole-attempt
    retry. Fails-once -> PASS in ONE boot with a logged subprocess-retry; fails-twice
    -> the whole-attempt path (a SECOND boot). Regressions guarded: a subprocess flake
    burning a fresh ~10-min boot when a cheap re-run would recover; a subprocess retry
    masking nondeterminism (both attempts' outcomes must be logged)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-subproc-retry-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "subproc_retry_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _log_body(self):
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            return fh.read()

    def test_analyzer_flake_once_then_recovers_in_one_boot(self):
        spec = _make_spec(self.template, 30, 600)
        rt = FakeRuntime("pass", analyzer_fail_calls=1)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        # Recovered on the SAME attempt: PASS, ONE KSP boot, analyzer invoked TWICE.
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "expected PASS, got %s (%s)" % (result["verdict"], result.get("subkind")))
        self.assertEqual(1, rt.launch_count, "a subprocess flake must NOT burn a fresh boot")
        self.assertEqual(2, rt.analyzer_call_count, "the analyzer subprocess re-ran once")
        self.assertEqual("PASS", result["verifiers"]["analyzer"]["status"])
        # BOTH attempts' outcomes logged; the recovery is called out (no masked flake).
        body = self._log_body()
        self.assertIn("analyzer subprocess-retry: attempt 1 tooling fault", body)
        self.assertIn("subprocess-retry outcomes: attempt1=INVALID/tooling attempt2=PASS", body)
        self.assertIn("RECOVERED on attempt 2", body)

    def test_analyzer_flake_twice_falls_back_to_whole_attempt(self):
        # The subprocess re-run ALSO faults -> the unchanged whole-attempt retry fires a
        # SECOND boot; attempt 2's analyzer (3rd call) is clean -> PASS(flakedThenPassed).
        spec = _make_spec(self.template, 30, 600)
        rt = FakeRuntime("pass", analyzer_fail_calls=2)
        terminal = run._run_scenario_with_retry(spec, self.instance, self.tmp, rt, self.logger)
        self.assertEqual(hlib.VERDICT_PASS, terminal["verdict"])
        self.assertEqual("flakedThenPassed", terminal.get("note"))
        self.assertEqual(2, rt.launch_count, "two subprocess faults must fall back to a whole-attempt boot")
        self.assertEqual(3, rt.analyzer_call_count,
                         "2 in-attempt analyzer calls (attempt1) + 1 (attempt2)")
        body = self._log_body()
        self.assertIn("attempt 2 ALSO tooling", body)
        self.assertIn("retry scenario=SMOKE-fake attempt=2", body)

    def test_recovered_flake_is_auditable_and_accrues_in_the_flake_ledger(self):
        """SF1: a subprocess-recovered flake writes ONE PASS result JSON. That JSON must
        (a) carry the self-contained verifiers.subprocessRetry detail so the recovery is
        durably auditable (NIT 1), and (b) accrue toward the scenario's flake numerator
        so a chronically-wedging tool reaches quarantine -- exactly like a whole-attempt
        flakedThenPassed. Fails if the recovery is silently dropped from either."""
        spec = _make_spec(self.template, 30, 600)
        rt = FakeRuntime("pass", analyzer_fail_calls=1)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        # (a) Durably auditable: the subprocessRetry detail is in the result JSON.
        retries = result["verifiers"]["subprocessRetry"]
        self.assertEqual(1, len(retries))
        self.assertEqual("analyzer", retries[0]["stage"])
        self.assertTrue(retries[0]["retried"])
        self.assertTrue(retries[0]["recovered"])
        self.assertEqual("INVALID/tooling", retries[0]["attempt1"])
        self.assertEqual("PASS", retries[0]["attempt2"])
        # (b) The flake ledger accrues it: refresh over the produced result JSON gives
        # the scenario a nonzero numerator (PASS + synthetic INVALID).
        orig_cov = run.COVERAGE_DIR
        run.COVERAGE_DIR = os.path.join(self.tmp, "coverage")
        try:
            run.refresh_coverage_and_flake([spec], {"schema": 1}, self.logger)
            with open(os.path.join(run.COVERAGE_DIR, "flake.json"), "r", encoding="utf-8") as fh:
                flake = json.load(fh)
        finally:
            run.COVERAGE_DIR = orig_cov
        sc = flake["scenarios"]["SMOKE-fake"]
        self.assertEqual(2, sc["total"], "PASS + synthetic INVALID from the recovered flake")
        self.assertEqual(1, sc["numerator"], "the recovered subprocess flake accrued")

    def test_triage_only_analyzer_does_not_subprocess_retry(self):
        """NIT 3: on a driver-INVALID run the analyzer runs ONCE triage-only (non-verdict).
        A wedged analyzer there must NOT trigger a subprocess re-run -- re-running over an
        already-INVALID save is pure waste. autopilot-loadfail forces the driver-INVALID;
        analyzer_fail_calls=1 would flake. Fails if the triage analyzer re-runs (call
        count > 1) or a subprocessRetry entry is recorded for a non-verdict run."""
        spec = _make_autopilot_spec(self.template, mission_budget=30, run_budget=600)
        rt = FakeRuntime("autopilot-loadfail", mission_mode="ok", venv_ok=True,
                         analyzer_fail_calls=1)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("load-failed", result["subkind"])
        # The triage analyzer ran EXACTLY ONCE (no subprocess retry burned).
        self.assertEqual(1, rt.analyzer_call_count,
                         "a triage-only analyzer over an INVALID save must not re-run")
        # No subprocessRetry entry recorded (the triage path never enters the retry seam).
        self.assertEqual([], result["verifiers"].get("subprocessRetry", []))


class DurationLedgerIoTests(unittest.TestCase):
    """The COMMITTED duration ledger's I/O contract (2026-07-26 review,
    BLOCKER-1 / MAJOR-2 / MAJOR-4).

    ``coverage/duration.json`` is the ONLY committed artifact this refresh
    writes, and ``results/`` is gitignored and per-checkout -- so a recompute
    over the local results dir replaces the whole-suite record with whatever
    this worktree happened to fly. Two things must hold on every run: the merge
    preserves scenarios this checkout never measured, and an unreadable ledger
    is never REPLACED by a partial one."""

    SCENARIO = "SMOKE-fake"

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-duration-")
        self._orig_results = run.RESULTS_DIR
        self._orig_cov = run.COVERAGE_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        run.COVERAGE_DIR = os.path.join(self.tmp, "coverage")
        os.makedirs(run.RESULTS_DIR, exist_ok=True)
        os.makedirs(run.COVERAGE_DIR, exist_ok=True)
        self.logger = run.HarnessLogger(
            os.path.join(run.RESULTS_DIR, "duration_harness.log"))
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        self.spec = _make_spec(self.template, 30, 600)

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        run.COVERAGE_DIR = self._orig_cov
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    @property
    def _path(self):
        return os.path.join(run.COVERAGE_DIR, "duration.json")

    def _write_ledger(self, text):
        with open(self._path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)

    def _seed_pass_result(self, wall, ended, run_id):
        result = {"schema": hlib.SCHEMA_VERSION, "runId": run_id,
                  "scenarioId": self.SCENARIO, "verdict": hlib.VERDICT_PASS,
                  "attempt": 1, "wallSeconds": wall, "startedUtc": ended,
                  "endedUtc": ended, "note": ""}
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % run_id), "w",
                  encoding="utf-8", newline="\n") as fh:
            fh.write(hlib.serialize_result(result))

    def _read_ledger(self):
        with open(self._path, "r", encoding="utf-8") as fh:
            return json.load(fh)

    def _log_body(self):
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            return fh.read()

    def test_a_one_scenario_checkout_does_not_wipe_the_committed_record(self):
        """BLOCKER-1, the observed live failure: a fresh worktree that flew ONE
        scenario overwrote the 24-entry committed ledger with 1 entry."""
        self._write_ledger(json.dumps({
            "schema": hlib.SCHEMA_VERSION,
            "scenarios": {
                "B11-mun-orbit": {"n": 5, "p50": 1317.0, "p95": 1319.0,
                                  "last": 1318.0, "lastVsP50": 1.001},
                "B12-minmus-orbit": {"n": 4, "p50": 627.0, "p95": 627.0,
                                     "last": 626.0, "lastVsP50": 0.998},
            }}, sort_keys=True, indent=2) + "\n")
        self._seed_pass_result(1400, "2026-07-26T10:00:00Z", "2026-07-26_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        scenarios = self._read_ledger()["scenarios"]
        self.assertEqual(sorted(scenarios),
                         ["B11-mun-orbit", "B12-minmus-orbit", self.SCENARIO])
        self.assertEqual(scenarios["B11-mun-orbit"]["n"], 5)
        self.assertEqual(scenarios["B12-minmus-orbit"]["p50"], 627.0)
        self.assertEqual(scenarios[self.SCENARIO]["n"], 1)

    def test_a_measured_scenario_accrues_instead_of_being_replaced(self):
        """MAJOR-2: the summary-only committed entry keeps its n instead of
        being recomputed down to this checkout's 1, so the regression warn
        stays armed. Then the NEXT run accrues on top of it."""
        self._write_ledger(json.dumps({
            "schema": hlib.SCHEMA_VERSION,
            "scenarios": {self.SCENARIO: {"n": 5, "p50": 1317.0, "p95": 1319.0,
                                          "last": 1318.0, "lastVsP50": 1.001}},
        }, sort_keys=True, indent=2) + "\n")
        self._seed_pass_result(1400, "2026-07-26T10:00:00Z", "2026-07-26_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        entry = self._read_ledger()["scenarios"][self.SCENARIO]
        self.assertEqual(entry["n"], 5)
        self.assertGreaterEqual(entry["n"], hlib.DURATION_MIN_SAMPLES)
        self.assertEqual(entry["last"], 1400.0)
        # A second run over the SAME accumulated results dir plus one new PASS.
        self._seed_pass_result(1290, "2026-07-27T10:00:00Z", "2026-07-27_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        entry = self._read_ledger()["scenarios"][self.SCENARIO]
        self.assertEqual(entry["n"], 6)
        self.assertEqual(sorted(entry["samples"].values()), [1290.0, 1400.0])

    def test_an_unparseable_ledger_is_never_replaced_and_reds_loudly(self):
        """MAJOR-4: the recovery path must not reopen the bug it closes. A
        partial file (truncate-then-die) must leave the file UNTOUCHED and log
        an Error, not silently replace 24 scenarios with this run's one."""
        partial = '{\n  "schema": 1,\n  "scenarios": {\n    "B11-mun-orb'
        self._write_ledger(partial)
        self._seed_pass_result(1400, "2026-07-26T10:00:00Z", "2026-07-26_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        with open(self._path, "r", encoding="utf-8") as fh:
            self.assertEqual(fh.read(), partial)
        body = self._log_body()
        self.assertIn("[Error][Duration]", body)
        self.assertIn("SKIPPING the duration write", body)

    def test_a_future_schema_ledger_is_refused_not_overwritten(self):
        self._write_ledger(json.dumps(
            {"schema": hlib.SCHEMA_VERSION + 1, "scenarios": {}}) + "\n")
        self._seed_pass_result(1400, "2026-07-26T10:00:00Z", "2026-07-26_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        self.assertEqual(self._read_ledger()["schema"], hlib.SCHEMA_VERSION + 1)
        self.assertIn("failed the schema gate", self._log_body())

    def test_a_missing_ledger_is_a_legitimate_first_write(self):
        self.assertFalse(os.path.exists(self._path))
        self._seed_pass_result(1400, "2026-07-26T10:00:00Z", "2026-07-26_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        self.assertEqual(self._read_ledger()["scenarios"][self.SCENARIO]["n"], 1)
        self.assertNotIn("[Error][Duration]", self._log_body())

    def test_the_write_leaves_no_tmp_behind(self):
        """The write is tmp + os.replace (MAJOR-4a); a leftover .tmp would mean
        the rename never happened."""
        self._seed_pass_result(1400, "2026-07-26T10:00:00Z", "2026-07-26_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        self.assertFalse(os.path.exists(self._path + ".tmp"))
        self.assertTrue(os.path.isfile(self._path))

    def test_a_malformed_entry_cannot_crash_the_end_of_a_flown_suite(self):
        """MINOR-8: the ledger is committed and hand-editable, and the warn line
        formats last/p50/p95. Before the fix this raised KeyError out of
        refresh_coverage_and_flake AFTER the whole suite had flown, so
        logger.close() never ran."""
        self._write_ledger(json.dumps({
            "schema": hlib.SCHEMA_VERSION,
            "scenarios": {"X": {"n": 5, "lastVsP50": 2.0}}},
            sort_keys=True, indent=2) + "\n")
        self._seed_pass_result(1400, "2026-07-26T10:00:00Z", "2026-07-26_1000_x")
        run.refresh_coverage_and_flake([self.spec], {"schema": 1}, self.logger)
        self.assertNotIn("X", self._read_ledger()["scenarios"])


class MultiCategoryBatchSmokeTests(unittest.TestCase):
    """M-A5.1 item 2 over the REAL run loop (fake runtime): a multi-category RunTests
    emits per-category BATCH_COMPLETE lines + a category=multi:<count> aggregate. With
    the aggregate -> PASS (failed=0 means ALL categories passed); without it (per-category
    lines only) -> PARSEK-FAIL(batch-crashed), never a silent pass off a per-category
    line."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-multibatch-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "multibatch_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _multi_spec(self):
        spec = _make_spec(self.template, 30, 600)
        spec["id"] = "SMOKE-multi"
        # Drive TWO categories: the fake KSP emits a per-category line for each + aggregate.
        for step in spec["driver"]["steps"]:
            if step.get("cmd") == "RunTests":
                step["args"]["category"] = "A,B"
        return spec

    def test_multi_category_with_aggregate_passes(self):
        rt = FakeRuntime("multipass")
        result = run.run_attempt(self._multi_spec(), self.instance, self.tmp, rt,
                                 attempt=1, prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "expected PASS, got %s (%s)" % (result["verdict"], result.get("subkind")))
        bc = result["verifiers"]["batchComplete"]
        self.assertEqual("PASS", bc["status"])
        self.assertTrue(bc["multi"])
        self.assertFalse(bc["aggregateMissing"])
        self.assertEqual(0, bc["failed"])
        self.assertEqual(2, bc["perCategoryCount"])

    def test_multi_category_missing_aggregate_reds_batch_crashed(self):
        rt = FakeRuntime("multinoagg")
        result = run.run_attempt(self._multi_spec(), self.instance, self.tmp, rt,
                                 attempt=1, prior_boot_crashed=False, logger=self.logger)
        # A defined fault, not a silent pass off a per-category line.
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        self.assertEqual("batch-crashed", result["subkind"])
        bc = result["verifiers"]["batchComplete"]
        self.assertEqual("FAIL", bc["status"])
        self.assertTrue(bc["aggregateMissing"])
        self.assertEqual(2, bc["perCategoryCount"])

    def test_multi_category_count_mismatch_reds_batch_crashed(self):
        # SF2: the aggregate declares multi:3 but only 2 per-category lines are present
        # (a category batch cut off). Same treatment as a missing aggregate: reds
        # batch-incomplete, never a silent pass off the mis-counted aggregate.
        rt = FakeRuntime("multimismatch")
        result = run.run_attempt(self._multi_spec(), self.instance, self.tmp, rt,
                                 attempt=1, prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        self.assertEqual("batch-crashed", result["subkind"])
        bc = result["verifiers"]["batchComplete"]
        self.assertEqual("FAIL", bc["status"])
        self.assertTrue(bc["categoryCountMismatch"])
        self.assertFalse(bc["aggregateMissing"])
        self.assertEqual(3, bc["expectedCategoryCount"])
        self.assertEqual(2, bc["perCategoryCount"])


class SceneRoutedBootSmokeTests(unittest.TestCase):
    """R12 over the REAL run loop (fake runtime). Two things are proven here.

    THE HAPPY PATH: a spec may now drive `LoadGame scene=trackstation`,
    `SimulateStockSwitchClick` and `ExitToSpaceCenter` end to end - which before this
    wave was impossible twice over (the switch verb failed validation as RESERVED, and
    no verb could leave FLIGHT at all).

    THE NEGATIVE CONTROL, which is the load-bearing half. `scene-route-wrong` answers
    the boot OK but lands at SPACECENTER anyway, so the batch scene-skips every member:
    `total=5 passed=0 failed=0 skipped=5 ... scene=SPACECENTER`. That is B10 verbatim -
    the run that shipped at daily tier reading GREEN while executing ZERO tests. Note
    what still passes on that run: exit 0, a clean analyzer, log-validate, and the
    batchComplete verifier itself (`failed=0` is TRUE of an all-skipped batch). The ONLY
    thing standing between a silently-wrong-scene boot and a green run is the
    anti-vacuity WHOLE-tally pin naming the scene. The A/B is the same spec against two
    fake modes, so a PASS under `pass` and a PARSEK-FAIL under `scene-route-wrong` prove
    the pin is what did it, not some unrelated redness."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-sceneroute-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "sceneroute_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _ts_spec(self):
        """A TRACKSTATION-routed batch spec pinned the anti-vacuity way: the WHOLE
        tally, scene token included."""
        spec = _make_spec(self.template, 30, 600)
        spec["id"] = "SMOKE-scene-ts"
        for step in spec["driver"]["steps"]:
            if step.get("cmd") == "LoadGame":
                step["args"]["scene"] = "trackstation"
            elif step.get("cmd") == "RunTests":
                step["args"]["category"] = "TrackingStation"
        spec["expectations"]["logContracts"]["required"] = [
            "BATCH_COMPLETE v1 total=5 passed=5 failed=0 skipped=0 "
            "category=TrackingStation scene=TRACKSTATION"]
        return spec

    def _switch_and_exit_spec(self):
        """Both R12 verbs as ordinary steps, no batch: record, drive a stock-click
        switch, then exit to the Space Center (the v1 CL-1 shape)."""
        spec = _make_spec(self.template, 30, 600)
        spec["id"] = "SMOKE-switch-exit"
        spec["driver"]["steps"] = [
            {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"},
             "expect": "OK", "budget": 30},
            {"cmd": "SetSetting", "args": {"name": "autoMerge", "value": "true"},
             "expect": "OK"},
            {"cmd": "StartRecording", "expect": "OK"},
            # NOTE the RAW vessel name. run.py's encode_value percent-encodes every arg
            # on the way out, so a spec that pre-encodes gets DOUBLE-encoded
            # (`Test%2520Craft`) and the seam resolves a vessel literally named
            # "Test%20Craft", which does not exist -> target-not-found. The M-A2 doc's
            # "(percent-encoded)" describes the WIRE token, not the TOML value.
            {"cmd": "SimulateStockSwitchClick",
             "args": {"site": "map", "vessel": "Test Craft"}, "expect": "OK"},
            {"cmd": "ExitToSpaceCenter", "expect": "OK", "budget": 120},
            {"cmd": "FlushAndQuit", "expect": "OK"},
        ]
        # No RunTests step, so the required pin must not name BATCH_COMPLETE (the
        # batch-owner rule rejects a spec that demands one with nobody to own it).
        spec["expectations"]["logContracts"]["required"] = ["armed session=fake"]
        return spec

    def _run(self, spec, mode):
        rt = FakeRuntime(mode)
        return run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                               prior_boot_crashed=False, logger=self.logger)

    def _commands_written(self):
        path = os.path.join(self.instance, "parsek-test-commands.txt")
        with open(path, "r", encoding="utf-8") as fh:
            return [l.strip() for l in fh if l.strip()]

    # ---- happy paths -----------------------------------------------------

    def test_scene_routed_boot_passes_when_the_route_takes(self):
        result = self._run(self._ts_spec(), "pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "expected PASS, got %s (%s)" % (result["verdict"],
                                                         result.get("subkind")))
        self.assertEqual("PASS", result["verifiers"]["expectations"]["status"])
        # The arg actually reached the wire in the seam's lowercase spelling. A
        # `scene=Trackstation` would be a typed REJECTED, and validate_spec now refuses
        # it pre-launch, but this pins that run.py forwards the arg at all.
        load = next(l for l in self._commands_written() if "cmd=LoadGame" in l)
        self.assertIn("scene=trackstation", load)

    def test_both_new_verbs_drive_end_to_end(self):
        result = self._run(self._switch_and_exit_spec(), "pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "expected PASS, got %s (%s)" % (result["verdict"],
                                                         result.get("subkind")))
        self.assertTrue(result["driver"]["allExpectedMet"])
        self.assertEqual(["LoadGame", "SetSetting", "StartRecording",
                          "SimulateStockSwitchClick", "ExitToSpaceCenter", "FlushAndQuit"],
                         [s["cmd"] for s in result["driver"]["steps"]])
        self.assertTrue(all(s["met"] for s in result["driver"]["steps"]))
        lines = self._commands_written()
        click = next(l for l in lines if "cmd=SimulateStockSwitchClick" in l)
        self.assertIn("site=map", click)
        # THE SPEC-AUTHORING TRAP, pinned. `vessel=` is the arg that exists because live
        # pids are not spec-addressable, so it is the one R12 arg a TOML routinely gives
        # a value with a SPACE in it. run.py encodes on the way out, so the raw name
        # above becomes exactly one whitespace-delimited wire token - and a spec that
        # pre-encodes would ship `Test%2520Craft` and be refused target-not-found for a
        # vessel it named correctly.
        self.assertIn("vessel=Test%20Craft", click)
        self.assertNotIn("%25", click)
        self.assertTrue(any("cmd=ExitToSpaceCenter" in l for l in lines))

    # ---- the negative control -------------------------------------------

    def test_wrong_scene_boot_reds_instead_of_reading_green(self):
        result = self._run(self._ts_spec(), "scene-route-wrong")
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"],
                         "a silently-wrong-scene boot must RED; got %s (%s)"
                         % (result["verdict"], result.get("subkind")))
        self.assertEqual("expectation", result["subkind"])
        self.assertEqual("FAIL", result["verifiers"]["expectations"]["status"])

    def test_the_wrong_scene_run_is_green_on_every_channel_but_the_tally_pin(self):
        """Why the WHOLE-tally pin is not optional ceremony. On the same run above,
        every cheap signal reads clean: the driver met every step (the seam answered OK
        - it BELIEVES it booted), batchComplete PASSes because `failed=0` is true of a
        batch that ran nothing, the analyzer is RED=0, log-validate passes and KSP
        exited 0. Delete the scene token from the pin and this run reports PASS."""
        result = self._run(self._ts_spec(), "scene-route-wrong")
        v = result["verifiers"]
        self.assertTrue(result["driver"]["allExpectedMet"])
        self.assertEqual("PASS", v["driverValidity"]["status"])
        self.assertEqual("PASS", v["batchComplete"]["status"])
        self.assertEqual(0, v["batchComplete"]["failed"])
        self.assertEqual("PASS", v["analyzer"]["status"])
        self.assertEqual("PASS", v["logValidate"]["status"])
        self.assertEqual(0, result["kspExit"]["code"])
        # ... and the one that did the work.
        self.assertEqual("FAIL", v["expectations"]["status"])

    def test_a_failed_zero_only_pin_would_have_passed_the_wrong_scene_boot(self):
        """The B10 regression itself, reproduced. The contract this design doc's own
        example once recommended - `BATCH_COMPLETE v1 .* failed=0\\b` - matches
        `total=5 passed=0 failed=0 skipped=5` exactly as well as five passes. Kept as an
        executable statement of WHY the anti-vacuity rule exists: this cell asserts the
        weak pin reads GREEN on a run that executed zero tests."""
        spec = self._ts_spec()
        spec["id"] = "SMOKE-scene-ts-weakpin"
        spec["expectations"]["logContracts"]["required"] = [
            "BATCH_COMPLETE v1 .* failed=0\\b"]
        result = self._run(spec, "scene-route-wrong")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "the weak pin is expected to MISS this fault -- if it now "
                         "catches it, the anti-vacuity rationale has changed and this "
                         "cell should be rewritten, not deleted")


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


class SettingsSidecarResetSmokeTests(unittest.TestCase):
    """The tracer-leak fix (shell half), driven through the REAL run.run_attempt
    over the fake-KSP seam.

    THE INCIDENT. `SetSetting mapRenderTracing=true` does not only touch the live
    per-save GameParameters: it is one of the eight settings Parsek ALSO persists
    to the instance-wide GameData/Parsek/PluginData/settings.cfg, and Parsek
    applies that sidecar OVER every save it loads. So after S1.4 ran once, the
    automation instance's sidecar held `mapRenderTracing = True` and EVERY later
    run - including two 2,000+ second landing flights - paid the per-frame
    map/TS render tracer cost and was gated by an anomaly sweep it never
    declared. These cells assert on the FILE, the only place that proves the
    instance was left clean.
    """

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-sidecar-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "gloops-airshow")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "sidecar_harness.log"))
        self.sidecar = run.settings_sidecar_path(self.instance)

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _contaminate(self, body="mapRenderTracing = True\n"):
        os.makedirs(os.path.dirname(self.sidecar), exist_ok=True)
        with open(self.sidecar, "w", encoding="utf-8") as fh:
            fh.write(body)

    def _read(self):
        if not os.path.isfile(self.sidecar):
            return None
        with open(self.sidecar, "r", encoding="utf-8") as fh:
            return fh.read()

    def _run(self, mode="pass", run_tests_budget=30, run_budget=600):
        spec = _make_spec(self.template, run_tests_budget, run_budget)
        rt = FakeRuntime(mode)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def test_stage_clears_a_leaked_tracer_before_the_game_boots(self):
        """The exact live shape: the instance carries S1.4's leaked
        `mapRenderTracing = True`. Staging must overwrite it with the baseline
        BEFORE launch, so this run does not silently inherit another scenario's
        tracer."""
        self._contaminate()
        ok, _, subkind = run.stage_fixture(
            _make_spec(self.template, 30, 600), self.instance, run.Runtime(), self.logger)
        self.assertTrue(ok, subkind)
        self.assertEqual([], hlib.settings_sidecar_tracers_on(self._read()))
        self.assertEqual(hlib.render_settings_sidecar_baseline(), self._read())

    def test_pass_run_leaves_the_instance_at_the_baseline(self):
        self._contaminate()
        result, _ = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual(hlib.render_settings_sidecar_baseline(), self._read())

    def test_killed_run_still_leaves_the_instance_at_the_baseline(self):
        """The robustness claim: a scenario whose KSP is process-tree-killed by the
        budget watchdog must not leave the instance contaminated. The teardown
        write lives in run_attempt's finally, which the kill path still runs."""
        self._contaminate()
        result, _ = self._run("hang", run_tests_budget=1, run_budget=2)
        self.assertEqual(hlib.VERDICT_KILLED, result["verdict"])
        self.assertEqual([], hlib.settings_sidecar_tracers_on(self._read()))

    def test_a_mid_run_setsetting_does_not_survive_the_attempt(self):
        """Simulates what the seam does during a tracer-on scenario: the sidecar is
        rewritten to True while KSP is alive. Teardown must put it back, so the
        NEXT scenario starts from the declared baseline rather than this one's."""
        spec = _make_spec(self.template, 30, 600)
        rt = FakeRuntime("pass")
        real_launch = rt.launch

        def launch_then_contaminate(exe, args, env, cwd):
            proc = real_launch(exe, args, env, cwd)
            self._contaminate("mapRenderTracing = True\nledgerTracing = True\n")
            return proc

        rt.launch = launch_then_contaminate
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual([], hlib.settings_sidecar_tracers_on(self._read()),
                         "a scenario's own SetSetting must not outlive its attempt")

    def test_the_clearing_is_named_in_the_harness_log(self):
        """A silent clobber would hide the leak instead of surfacing it; the stage
        line must name which tracer it found switched on."""
        self._contaminate()
        self._run("pass")
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("settings-sidecar baseline written phase=stage", body)
        self.assertIn("cleared leaked: mapRenderTracing", body)
        self.assertIn("settings-sidecar baseline written phase=teardown", body)

    def test_baseline_leaves_the_other_tracked_settings_unset(self):
        """Only the three tracers are pinned. Writing any of the other five
        sidecar-tracked settings would override the fixture's own GameParameters
        for every save on the instance - the same bug in a different key."""
        self._run("pass")
        values = hlib.parse_settings_sidecar(self._read())
        self.assertEqual(sorted(hlib.TRACER_SETTING_KEYS), sorted(values))


class AlwaysCollectAndContactSheetSmokeTests(unittest.TestCase):
    """V3 (design-testing-unified section 6): the light UNCONDITIONAL artifact
    step + the contact sheet, driven through run.run_attempt over the fake KSP.

    The whole point of V3 is that a GREEN run leaves something a human can look
    at, so the load-bearing cells here run mode="pass": before this change a
    PASS ran no collect-logs and its KSP.log died with the next boot."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-v3-smoke-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "smoke_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run(self, mode="pass", run_tests_budget=30, run_budget=600):
        spec = _make_spec(self.template, run_tests_budget, run_budget)
        rt = FakeRuntime(mode)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def test_pass_run_collects_artifacts_and_writes_the_sheet(self):
        """A PASS must leave: results/<runId>_shots/KSP.log, the per-run contact
        HTML, and results/index.html - while collect-logs stays non-PASS-only."""
        result, _ = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        # collect-logs untouched: still not run on a PASS.
        self.assertFalse(result["collectLogs"]["ran"])
        # the always-collect step ran and copied the log.
        art = result["artifacts"]
        self.assertTrue(art["ran"])
        self.assertTrue(art["kspLog"])
        self.assertFalse(art["kspLogTruncated"])
        shots = os.path.join(run.RESULTS_DIR, "%s_shots" % result["runId"])
        self.assertTrue(os.path.isfile(os.path.join(shots, "KSP.log")))
        # the contact sheet + index landed.
        sheet = os.path.join(run.RESULTS_DIR, "%s_contact.html" % result["runId"])
        self.assertTrue(os.path.isfile(sheet))
        self.assertTrue(os.path.isfile(os.path.join(run.RESULTS_DIR, "index.html")))
        with open(sheet, "r", encoding="utf-8") as fh:
            page = fh.read()
        # numbers next to pictures: the batch tally + the verifier rows are ON the page.
        self.assertIn("BATCH_COMPLETE v1 total=5", page)
        self.assertIn("analyzer", page)
        # ... and the artifacts block round-trips into the durable result JSON
        # (additive key; schema unchanged).
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"]),
                  "r", encoding="utf-8") as fh:
            persisted = json.load(fh)
        self.assertTrue(persisted["artifacts"]["kspLog"])
        self.assertEqual(hlib.SCHEMA_VERSION, persisted["schema"])

    def test_killed_run_still_collects_artifacts(self):
        """The unconditional step is unconditional: a budget-killed run's shots
        dir must still hold the KSP.log (the heavy collect-logs also ran, as
        before)."""
        result, _ = self._run("hang", run_tests_budget=1, run_budget=2)
        self.assertEqual(hlib.VERDICT_KILLED, result["verdict"])
        self.assertTrue(result["collectLogs"]["ran"])
        self.assertTrue(result["artifacts"]["kspLog"])
        shots = os.path.join(run.RESULTS_DIR, "%s_shots" % result["runId"])
        self.assertTrue(os.path.isfile(os.path.join(shots, "KSP.log")))
        self.assertTrue(os.path.isfile(
            os.path.join(run.RESULTS_DIR, "%s_contact.html" % result["runId"])))

    def test_run_window_screenshot_is_copied_and_prior_run_one_is_not(self):
        """The Screenshots dir accumulates across runs; only files stamped inside
        this run's wall-clock window ride into the shots dir."""
        shots_src = os.path.join(self.instance, "Screenshots")
        os.makedirs(shots_src, exist_ok=True)
        now = time.time()
        fresh = os.path.join(shots_src, "fresh.png")
        stale = os.path.join(shots_src, "stale.png")
        for p in (fresh, stale):
            with open(p, "wb") as fh:
                fh.write(b"\x89PNG fake")
        # fresh: mtime in the future (inside the run window); stale: an hour ago.
        os.utime(fresh, (now + 3600, now + 3600))
        os.utime(stale, (now - 3600, now - 3600))

        result, _ = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual(1, result["artifacts"]["screenshots"])
        shots_dst = os.path.join(run.RESULTS_DIR, "%s_shots" % result["runId"])
        self.assertTrue(os.path.isfile(os.path.join(shots_dst, "fresh.png")))
        self.assertFalse(os.path.isfile(os.path.join(shots_dst, "stale.png")))
        # the copied capture is referenced from the sheet's image grid.
        with open(os.path.join(run.RESULTS_DIR, "%s_contact.html" % result["runId"]),
                  "r", encoding="utf-8") as fh:
            self.assertIn("fresh.png", fh.read())

    def test_verdict_is_durable_before_the_artifact_copy_and_summary_not_doubled(self):
        """Review MINOR 4: the result JSON is written (with a placeholder
        artifacts block) BEFORE the possibly-slow artifact copy, then enriched
        and re-written with append_summary=False -- so a kill inside the copy
        cannot cost the verdict, and the rolling summary gets exactly ONE line."""
        result, _ = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        with open(os.path.join(run.RESULTS_DIR, "summary.txt"), "r", encoding="utf-8") as fh:
            lines = [l for l in fh.read().splitlines() if "SMOKE-fake" in l]
        self.assertEqual(1, len(lines), "the enrichment re-write must not double the summary")

    def test_truncated_log_copy_is_bounded_and_marked(self):
        """Review MINOR 3 + 5 (CONFIRMED by the reviewer's repro): the streaming
        head+tail copy itself, driven with call-time-rebound caps. The tail leg
        must be BYTE-BOUNDED (the source can grow between the size snapshot and
        the copy) and the marker must sit between the exact head and tail."""
        body = bytes(range(256)) * 4  # 1024 distinct-ish bytes
        with open(os.path.join(self.instance, "KSP.log"), "wb") as fh:
            fh.write(body)
        orig = (hlib.ARTIFACT_LOG_CAP_BYTES, hlib.ARTIFACT_LOG_HEAD_BYTES,
                hlib.ARTIFACT_LOG_TAIL_BYTES)
        try:
            hlib.ARTIFACT_LOG_CAP_BYTES = 200
            hlib.ARTIFACT_LOG_HEAD_BYTES = 50
            hlib.ARTIFACT_LOG_TAIL_BYTES = 150
            art = run._collect_run_artifacts("trunc-run", self.instance,
                                             time.time(), self.logger)
        finally:
            (hlib.ARTIFACT_LOG_CAP_BYTES, hlib.ARTIFACT_LOG_HEAD_BYTES,
             hlib.ARTIFACT_LOG_TAIL_BYTES) = orig
        self.assertTrue(art["kspLog"])
        self.assertTrue(art["kspLogTruncated"])
        dst = os.path.join(run.RESULTS_DIR, "trunc-run_shots", "KSP.log")
        with open(dst, "rb") as fh:
            data = fh.read()
        marker_at = data.find(b"[harness-artifact] KSP.log TRUNCATED")
        self.assertGreater(marker_at, 0)
        self.assertTrue(data.startswith(body[:50]), "head must be the first 50 source bytes")
        self.assertTrue(data.endswith(body[-150:]), "tail must be the last 150 source bytes")
        # bounded: head + tail + one marker line, nothing more.
        self.assertLess(len(data), 200 + 200, "payload must stay ~cap + marker")

    def test_tail_copy_stays_bounded_when_the_log_grows_mid_copy(self):
        """The reviewer's growth repro: getsize snapshots 500, the file is
        really 4000 (a not-fully-reaped KSP child kept appending). The tail leg
        must copy plan.tail_bytes and STOP, not stream to EOF."""
        with open(os.path.join(self.instance, "KSP.log"), "wb") as fh:
            fh.write(b"x" * 4000)
        orig = (hlib.ARTIFACT_LOG_CAP_BYTES, hlib.ARTIFACT_LOG_HEAD_BYTES,
                hlib.ARTIFACT_LOG_TAIL_BYTES)
        orig_getsize = os.path.getsize
        try:
            hlib.ARTIFACT_LOG_CAP_BYTES = 200
            hlib.ARTIFACT_LOG_HEAD_BYTES = 50
            hlib.ARTIFACT_LOG_TAIL_BYTES = 150
            os.path.getsize = lambda p, _o=orig_getsize: (
                500 if os.path.basename(str(p)) == "KSP.log" else _o(p))
            art = run._collect_run_artifacts("grow-run", self.instance,
                                             time.time(), self.logger)
        finally:
            os.path.getsize = orig_getsize
            (hlib.ARTIFACT_LOG_CAP_BYTES, hlib.ARTIFACT_LOG_HEAD_BYTES,
             hlib.ARTIFACT_LOG_TAIL_BYTES) = orig
        self.assertTrue(art["kspLogTruncated"])
        dst = os.path.join(run.RESULTS_DIR, "grow-run_shots", "KSP.log")
        self.assertLess(os.path.getsize(dst), 200 + 200,
                        "a mid-copy grown source must not bust the cap")

    def test_artifact_failure_never_changes_the_verdict(self):
        """The OTHER half of the verdict-neutrality contract (review MINOR 5):
        a crash inside _collect_run_artifacts is a Warn, never a verdict
        change."""
        orig = hlib.plan_artifact_log_copy
        hlib.plan_artifact_log_copy = _raise_artifact_boom
        try:
            result, _ = self._run("pass")
        finally:
            hlib.plan_artifact_log_copy = orig
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertFalse(result["artifacts"]["kspLog"])
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("artifact collection FAILED", body)
        self.assertIn("verdict unaffected", body)

    def test_retention_prunes_old_shots_dirs_but_never_the_current_run(self):
        """Review MAJOR 1: results/ is gitignored and nothing else prunes it;
        the retention pass must bound *_shots growth, oldest first, protecting
        the run that just collected."""
        os.makedirs(run.RESULTS_DIR, exist_ok=True)
        now = time.time()
        for i, name in enumerate(["old1_shots", "old2_shots", "old3_shots"]):
            d = os.path.join(run.RESULTS_DIR, name)
            os.makedirs(d, exist_ok=True)
            with open(os.path.join(d, "KSP.log"), "w") as fh:
                fh.write("x" * 10)
            os.utime(d, (now - 3600 + i, now - 3600 + i))
        orig_keep = hlib.ARTIFACT_SHOTS_KEEP_DIRS
        try:
            hlib.ARTIFACT_SHOTS_KEEP_DIRS = 2
            result, _ = self._run("pass")
        finally:
            hlib.ARTIFACT_SHOTS_KEEP_DIRS = orig_keep
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        current = os.path.join(run.RESULTS_DIR, "%s_shots" % result["runId"])
        self.assertTrue(os.path.isdir(current), "the current run's dir must survive")
        survivors = sorted(n for n in os.listdir(run.RESULTS_DIR) if n.endswith("_shots"))
        self.assertEqual(2, len(survivors), "keep=2: current + the newest old dir")
        self.assertNotIn("old1_shots", survivors)
        self.assertNotIn("old2_shots", survivors)
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            self.assertIn("retention pruned", fh.read())

    def test_contact_sheet_module_loads_by_path_without_syspath(self):
        """Review NIT 11: the tool loads by file path, no sys.path mutation, and
        an already-imported module (what the monkeypatch cells rely on) is
        reused so the patch and run.py see the SAME object."""
        saved_module = sys.modules.pop("contact_sheet", None)
        saved_cache = run._contact_sheet_module
        run._contact_sheet_module = None
        path_before = list(sys.path)
        try:
            mod = run._load_contact_sheet_module()
            self.assertTrue(callable(mod.generate_run_sheet))
            self.assertEqual(path_before, sys.path, "loading must not touch sys.path")
            self.assertIs(mod, run._load_contact_sheet_module(), "cached on repeat")
        finally:
            run._contact_sheet_module = saved_cache
            if saved_module is not None:
                sys.modules["contact_sheet"] = saved_module
            else:
                sys.modules.pop("contact_sheet", None)

    def test_sheet_failure_never_changes_the_verdict(self):
        """The V3 verdict-neutrality contract: a contact-sheet crash is a Warn in
        the harness log and NOTHING else - the attempt's verdict, result JSON,
        and collect behavior are untouched."""
        tools_dir = os.path.join(HARNESS_ROOT, "tools")
        if tools_dir not in sys.path:
            sys.path.insert(0, tools_dir)
        import contact_sheet
        orig = contact_sheet.generate_run_sheet
        contact_sheet.generate_run_sheet = _raise_sheet_boom
        try:
            result, _ = self._run("pass")
        finally:
            contact_sheet.generate_run_sheet = orig
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertTrue(os.path.isfile(
            os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"])))
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("contact-sheet generation FAILED", body)
        self.assertIn("verdict unaffected", body)


def _raise_sheet_boom(results_dir, run_id):
    raise RuntimeError("boom (injected sheet failure)")


def _raise_artifact_boom(*args, **kwargs):
    raise RuntimeError("boom (injected artifact failure)")


if __name__ == "__main__":
    unittest.main()
