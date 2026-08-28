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

import ast
import copy
import inspect
import json
import os
import re
import shutil
import sys
import tempfile
import textwrap
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
for _p in (HARNESS_ROOT, HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import ghostlife  # noqa: E402
import hlib  # noqa: E402
import oracle  # noqa: E402
import rendercompose  # noqa: E402
import run  # noqa: E402
import status  # noqa: E402

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
                 analyzer_fail_calls=0, produced_parsed=True, inject_noop=False,
                 render_manifest_text=None, ghost_lifecycle_log_text=None):
        self.mode = mode
        # The GhostRenderTrace mesh-lifecycle lines the fake KSP "writes" into its
        # KSP.log, APPENDED at launch for the render-manifest reason below (after
        # staging, which is where a real run's lines land). Append, never write:
        # the fake KSP appends its own session markers to the same file, and
        # clobbering them would break every other row that reads the log.
        self.ghost_lifecycle_log_text = ghost_lifecycle_log_text
        # M-A7: the text the fake KSP "writes" as its render manifest, dropped at
        # LAUNCH time - i.e. AFTER staging, which is where the real recorder's
        # scene-exit / teardown flush lands. Writing it before run_attempt would
        # place it ahead of the stage rotation and would test nothing about the
        # artifact this attempt produced.
        self.render_manifest_text = render_manifest_text
        # HARNESS-INJECT-FAILS-OPEN seam: when True, run_inject exits 0 having
        # written NOTHING - the measured `--no-build`-against-a-never-built-assembly
        # shape (and the KSP.log lock-probe refusal, which exits the same way).
        self.inject_noop = inject_noop
        self.inject_call_count = 0
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
        # M-A7: the env dict run.py handed the launch, so a test can assert the
        # CONDITIONAL PARSEK_RENDER_MANIFEST arming rather than only the log line.
        self.last_launch_env = {}

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
        self.last_launch_env = dict(env)
        if self.render_manifest_text is not None:
            with open(os.path.join(cwd, run.RENDER_MANIFEST_FILENAME),
                      "w", encoding="utf-8") as fh:
                fh.write(self.render_manifest_text)
        if self.ghost_lifecycle_log_text is not None:
            with open(os.path.join(cwd, "KSP.log"), "a", encoding="utf-8") as fh:
                fh.write(self.ghost_lifecycle_log_text)
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
        # Route-1 seam bridge: forward run.py's OWN channel path + reserved id so the
        # `midcommit` mode writes exactly where a real mission would, and the
        # mid-mission-write reader is exercised against the id run.py actually chose
        # (not one the test picked).
        argv = list(args)
        seam = []
        for flag in ("--seam-commands", "--seam-commit-id"):
            if flag in argv:
                seam += [flag, argv[argv.index(flag) + 1]]
        out = open(stdout_path, "w", encoding="utf-8")
        try:
            return subprocess.Popen(
                [sys.executable, FAKE_MISSION, "--result", result_path,
                 "--mode", self.mission_mode] + seam,
                cwd=cwd, stdout=out, stderr=subprocess.STDOUT)
        finally:
            out.close()

    # ---- stubbed verifier subprocesses -----------------------------------

    def run_inject(self, instance_dir, save_name, timeout, preset="all-synthetic"):
        self.inject_call_count += 1
        if self.inject_noop:
            return run.ToolResult(0, False)  # exit 0, fixture never written
        rec = os.path.join(instance_dir, "saves", save_name, "Parsek", "Recordings")
        os.makedirs(rec, exist_ok=True)
        for i in range(8):
            open(os.path.join(rec, "rec%02d.prec" % i), "w").close()
        if preset == "rewind-b9":
            rp_dir = os.path.join(instance_dir, "saves", save_name, "Parsek", "RewindPoints")
            os.makedirs(rp_dir, exist_ok=True)
            open(os.path.join(rp_dir, "rp_b9_root.sfs"), "w").close()
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


# A minimal but PRODUCTION-SHAPED render manifest (M-A7). Authored here as a
# literal rather than imported from test_rendercompose so this file's smoke legs
# stay a statement about run.py's ROW WIRING and never about the parser's rule
# coverage - and so a rule-fixture edit next door cannot red the harness smoke
# suite. It deliberately carries NO CONSTANTS node: the RC-CONST findings that
# omission raises are exactly what a REPORT-only row must record and carry into
# results/<runId>.json without moving the verdict, which is the wiring claim.
_RENDER_MANIFEST_TEXT = """RENDER_MANIFEST
{
\tschemaVersion = 1
\texportUT = 1000
\texportReason = verb
\tscene = FLIGHT
\tsaveName = smoke
\tenvArmed = True
\tforceArmed = False
\tmapRenderTracingOn = False
\tOBSERVED
\t{
\t\tDWELL
\t\t{
\t\t\tpid = 100
\t\t\trecId = smoke-rec-a
\t\t\tcommittedIndex = 0
\t\t\tchainSignature = sig-a
\t\t\tsegmentIndex = 0
\t\t\tphaseKind = ascent
\t\t\ttreatment = TracedPath
\t\t\tvisible = True
\t\t\tcoverage = InSegment
\t\t\tframeBody = Kerbin
\t\t\topenUT = 1000
\t\t\tcloseUT = 1010
\t\t\tframes = 100
\t\t\twarp1x = 100
\t\t\tminHeadUT = 1000
\t\t\tmaxHeadUT = 1010
\t\t\tmaxUtStep = 0.2
\t\t}
\t\tOWNERSHIP_CHANGE
\t\t{
\t\t\trecId = smoke-rec-a
\t\t\tut = 1000
\t\t\tevent = appear
\t\t}
\t\tOWNERSHIP_CHANGE
\t\t{
\t\t\trecId = smoke-rec-a
\t\t\tut = 1010
\t\t\tevent = disappear
\t\t}
\t}
}
"""


def _make_render_compose_spec(save_template, block=None, run_tests_budget=30,
                              run_budget=600):
    """A seam scenario declaring [expectations.renderComposition] - the surface
    that arms PARSEK_RENDER_MANIFEST at launch and activates row 7c. ``block``
    defaults to the bare reading-run declaration (declared, unarmed)."""
    spec = _make_spec(save_template, run_tests_budget, run_budget)
    spec["id"] = "SMOKE-rendercompose"
    spec["expectations"][rendercompose.RENDER_COMPOSITION_BLOCK] = (
        {} if block is None else copy.deepcopy(block))
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

    def test_save_parse_row_reports_on_a_clean_pass(self):
        """M-C2/R9: the saveParse verifier row must land REPORT-ONLY on a green
        run with no M-C2 block declared, carrying the measured structural
        facets, and must not move the verdict. The staged smoke fixture is a
        minimal `GAME { }` save (no ParsekScenario node), so every count is a
        genuine zero - parsed=True, scenario absent."""
        result, _ = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        sp = result["verifiers"]["saveParse"]
        self.assertEqual("REPORT", sp["status"])
        self.assertFalse(sp["gating"])
        self.assertEqual([], sp["blocks"])
        self.assertEqual([], sp["armedBlocks"])
        self.assertEqual([], sp["mismatches"])
        self.assertTrue(sp["parsed"])
        # The smoke template is `GAME { }` - Parsek absent. With no block
        # declared that is a REPORT row, and the state is READABLE on the row.
        self.assertIs(False, sp["scenarioFound"])
        self.assertEqual({"supersedeRows": 0, "tombstones": 0, "rewindPoints": 0,
                          "rewindRetirements": 0},
                         sp["observed"]["rewind"])
        self.assertEqual(0, sp["observed"]["recordings"]["structure"]["trees"])
        # ... and the row round-trips into the durable result (a PASS runs no
        # collect-logs, so results/<runId>.json is the only place the measured
        # facets survive - the promotion path reads them there).
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"]),
                  "r", encoding="utf-8") as fh:
            persisted = json.load(fh)
        self.assertEqual(sp, persisted["verifiers"]["saveParse"])

    def _run_with_b9_template(self, extra_expectations):
        """Drive a full fake-KSP run over a Parsek-BEARING staged save (the
        production-shaped merged-B9 text from test_saveparse), with the given
        M-C2 expectation blocks merged into the spec."""
        import test_saveparse as ts
        template = os.path.join(self.tmp, "b9-template")
        os.makedirs(template, exist_ok=True)
        with open(os.path.join(template, "persistent.sfs"), "w", encoding="utf-8") as fh:
            fh.write(ts.B9_MERGED_SFS)
        spec = _make_spec(template, 30, 600)
        spec["expectations"].update(copy.deepcopy(extra_expectations))
        rt = FakeRuntime("pass")
        return run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                               prior_boot_crashed=False, logger=self.logger)

    def test_declared_rewind_block_reports_mismatches_without_gating(self):
        """The S4.1 shape end-to-end (adversarial-review finding 6): a DECLARED
        [expectations.rewind] whose windows mismatch the produced save must
        land status=REPORT with the mismatches recorded and the verdict
        untouched - the exact verdict-neutrality contract this verifier
        shipped around."""
        result = self._run_with_b9_template(
            {"rewind": {"supersedeRows": {"max": 0}, "tombstones": {"max": 0}}})
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "a report-only mismatch must not move the verdict")
        sp = result["verifiers"]["saveParse"]
        self.assertEqual("REPORT", sp["status"])
        self.assertEqual(["rewind"], sp["blocks"])
        self.assertEqual([], sp["armedBlocks"])
        self.assertEqual(2, len(sp["mismatches"]))
        self.assertIs(True, sp["scenarioFound"])
        self.assertEqual(1, sp["observed"]["rewind"]["supersedeRows"])

    def test_armed_block_mismatch_reds_save_structure(self):
        """gating = true + a mismatching window -> PARSEK-FAIL(save-structure),
        across the real run.py -> hlib boundary (the flag name is exercised
        end-to-end, not injected)."""
        result = self._run_with_b9_template(
            {"rewind": {"gating": True, "supersedeRows": {"max": 0}}})
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        self.assertEqual("save-structure", result["subkind"])
        sp = result["verifiers"]["saveParse"]
        self.assertEqual("FAIL", sp["status"])
        self.assertEqual(["rewind"], sp["armedBlocks"])

    def test_armed_block_match_stays_pass(self):
        result = self._run_with_b9_template(
            {"rewind": {"gating": True, "supersedeRows": 1,
                        "tombstones": {"min": 1, "max": 1}}})
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual("PASS", result["verifiers"]["saveParse"]["status"])

    # ---- M-A7 renderCompose (row 7c) ------------------------------------

    def _drop_render_manifest(self, text=None):
        """Place a parsek-render-manifest.txt in the fake KSP root RIGHT NOW.

        Used only to simulate a STALE artifact left behind by a previous run -
        the instance is reused, so this is what run B finds when it stages. A
        manifest this attempt is supposed to have PRODUCED goes through
        ``FakeRuntime.render_manifest_text`` instead, which drops it at launch,
        after staging."""
        path = os.path.join(self.instance, run.RENDER_MANIFEST_FILENAME)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(_RENDER_MANIFEST_TEXT if text is None else text)
        return path

    def _run_render_compose(self, block=None, drop_manifest=True, manifest_text=None):
        spec = _make_render_compose_spec(self.template, block)
        produced = None
        if drop_manifest:
            produced = _RENDER_MANIFEST_TEXT if manifest_text is None else manifest_text
        rt = FakeRuntime("pass", render_manifest_text=produced)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def test_render_compose_row_reports_on_a_clean_pass_with_no_block(self):
        """The default state of every committed spec today: no block declared, so
        the recorder is never armed, no manifest exists, and the row lands
        REPORT-ONLY with the absence NAMED in parseError rather than silently read
        as zero records. It must not move the verdict."""
        result, rt = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        rc = result["verifiers"]["renderCompose"]
        self.assertEqual("REPORT", rc["status"])
        self.assertFalse(rc["gating"])
        self.assertEqual([], rc["blocks"])
        self.assertEqual([], rc["armedBlocks"])
        # No block declared => an absent manifest is NOT a mismatch (the saveparse
        # degrade rule: the fault stays visible in parsed/parseError instead).
        self.assertEqual([], rc["mismatches"])
        self.assertIsNone(rc["parsed"])
        self.assertIn("missing parsek-render-manifest.txt", rc["parseError"])
        self.assertEqual({}, rc["observed"])
        self.assertEqual([], rc["findings"])
        self.assertEqual({}, rc["unevaluable"])
        # ... and the env var is NOT set: arming is by DECLARATION.
        self.assertNotIn("PARSEK_RENDER_MANIFEST", rt.last_launch_env)

    def test_declared_block_arms_the_env_var_and_reports_the_facets(self):
        """Leg (a): a DECLARED block arms PARSEK_RENDER_MANIFEST=1 at launch, and
        a produced manifest lands as a REPORT row carrying the measured facets +
        the structured findings, with the verdict untouched. This is the reading
        run the arming workflow mandates, end to end."""
        result, rt = self._run_render_compose()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertEqual("1", rt.last_launch_env.get("PARSEK_RENDER_MANIFEST"),
                         "a declared block must arm the recorder at launch")
        rc = result["verifiers"]["renderCompose"]
        self.assertEqual("REPORT", rc["status"])
        self.assertFalse(rc["gating"])
        self.assertEqual(["renderComposition"], rc["blocks"])
        self.assertEqual([], rc["armedBlocks"])
        self.assertIs(True, rc["parsed"])
        self.assertEqual("", rc["parseError"])
        facets = rc["observed"]["renderComposition"]
        self.assertEqual(1, facets["dwells"])
        self.assertEqual(1, facets["schemaVersion"])
        self.assertEqual("verb", facets["exportReason"])
        self.assertEqual(100, facets["warpBuckets"]["warp1x"])
        self.assertEqual({"TracedPath": 1}, facets["treatments"])
        # The structured M-A1-model findings ride the row, every one citing a
        # contract (the discipline the rule module enforces at construction).
        self.assertTrue(rc["findings"], "the fixture omits CONSTANTS; RC-CONST must raise")
        self.assertTrue(all(f["citedContract"] for f in rc["findings"]))
        self.assertIn("RC-CONST", {f["ruleId"] for f in rc["findings"]})
        # FAIL-level findings become mismatches - RECORDED, not gating.
        self.assertTrue(rc["mismatches"])
        # ... and the whole row round-trips into results/<runId>.json, which is the
        # ONLY durable home of a green run's composition numbers.
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"]),
                  "r", encoding="utf-8") as fh:
            persisted = json.load(fh)
        self.assertEqual(rc, persisted["verifiers"]["renderCompose"])

    def test_the_row_records_which_block_it_evaluated(self):
        """The run JSON must say WHAT was asserted, not only that something was.

        Provenance: run `2026-08-25_1811` armed a negative control by substring-
        replacing the V24W spec's `warpBuckets` line; the spec quotes that literal
        in two rationale COMMENTS ahead of the real key, the replace hit a
        comment, and the flight evaluated the UNINVERTED block. The PASS was
        correct - but the run's own artifacts recorded only the block's NAME, so
        "the control was refuted" and "the control never loaded" looked
        identical on disk, and settling it took an offline replay across every
        committed version of the rule module. A control whose result cannot say
        what it asserted is not a control."""
        block = {"gating": True, "warpBuckets": ["warpHigh"], "dwells": {"min": 1}}
        result, _rt = self._run_render_compose(block=block)
        rc = result["verifiers"]["renderCompose"]
        self.assertEqual(block, rc["declared"])
        self.assertEqual(["renderComposition"], rc["armedBlocks"])
        # It rides into results/<runId>.json - the durable home - and is
        # JSON-round-trip stable (no tuples, no NaN).
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"]),
                  "r", encoding="utf-8") as fh:
            persisted = json.load(fh)
        self.assertEqual(block, persisted["verifiers"]["renderCompose"]["declared"])
        # A spec declaring nothing records None, never {}: absent is not "declared
        # nothing", the same rule the facets follow.
        plain, _rt = self._run("pass")
        self.assertIsNone(plain["verifiers"]["renderCompose"]["declared"])

    def test_declared_block_with_no_manifest_is_a_defined_mismatch_not_a_pass(self):
        """Leg (b): the absent-artifact-is-a-defined-mismatch rule. A spec that
        DECLARED the block booted with the recorder armed, so no manifest means the
        recorder never armed, never flushed, or the export never ran - never a
        silent pass. Still REPORT (unarmed), so the verdict is untouched."""
        result, rt = self._run_render_compose(drop_manifest=False)
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "a report-only mismatch must not move the verdict")
        self.assertEqual("1", rt.last_launch_env.get("PARSEK_RENDER_MANIFEST"))
        rc = result["verifiers"]["renderCompose"]
        self.assertEqual("REPORT", rc["status"])
        self.assertEqual(["renderComposition"], rc["blocks"])
        self.assertEqual(1, len(rc["mismatches"]))
        self.assertIn("manifest absent", rc["mismatches"][0])
        self.assertIsNone(rc["parsed"])
        self.assertEqual({}, rc["observed"])

    def test_a_stale_manifest_from_a_previous_run_is_rotated_at_stage(self):
        """The instance is REUSED across runs, so run A's manifest is sitting in
        the KSP root when run B stages. Staging rotates it exactly as it rotates
        parsek-test-results.txt: without that, a run B that produced NO manifest
        reads run A's file and reports a composition belonging to a different
        flight - the absent-manifest mismatch silently replaced by a green
        reading of stale evidence."""
        stale = self._drop_render_manifest()
        self.assertTrue(os.path.isfile(stale))
        # Run B declares the block (so the recorder arms) but produces nothing.
        result, rt = self._run_render_compose(drop_manifest=False)
        self.assertEqual("1", rt.last_launch_env.get("PARSEK_RENDER_MANIFEST"))
        rc = result["verifiers"]["renderCompose"]
        self.assertIsNone(rc["parsed"], "run A's manifest was still readable")
        self.assertEqual(1, len(rc["mismatches"]))
        self.assertIn("manifest absent", rc["mismatches"][0])
        self.assertEqual({}, rc["observed"])
        self.assertFalse(os.path.isfile(stale))

    def test_torn_manifest_is_a_defined_mismatch_never_zero_records(self):
        """The other structural fault: a zero-byte manifest is trivially
        brace-balanced and WOULD read as a clean all-zero composition. It must fail
        loud with parsed=False instead."""
        result, _ = self._run_render_compose(manifest_text="")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        rc = result["verifiers"]["renderCompose"]
        self.assertIs(False, rc["parsed"])
        self.assertIn("RENDER_MANIFEST", rc["parseError"])
        self.assertEqual(1, len(rc["mismatches"]))
        self.assertIn("unreadable", rc["mismatches"][0])
        self.assertEqual({}, rc["observed"], "an unparsed manifest measures NOTHING")

    def test_armed_block_mismatch_reds_render_composition(self):
        """gating = true + an unsatisfiable floor -> PARSEK-FAIL(render-composition)
        across the real run.py -> hlib boundary (the flag name is exercised end to
        end, not injected). No committed spec does this; the shape must work before
        the first lane is promoted."""
        result, _ = self._run_render_compose(
            {"gating": True, "dwells": {"min": 5}})
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        self.assertEqual("render-composition", result["subkind"])
        rc = result["verifiers"]["renderCompose"]
        self.assertEqual("FAIL", rc["status"])
        self.assertTrue(rc["gating"])
        self.assertEqual(["renderComposition"], rc["armedBlocks"])
        self.assertTrue(any("dwells" in m for m in rc["mismatches"]))

    # ---- ghostLifecycle (row 7d) ----------------------------------------

    #: Production-shaped GhostRenderTrace mesh-lifecycle lines, appended to the
    #: fake KSP.log at launch. One balanced recording plus one that spawns and
    #: never destroys, so a single corpus exercises both the count facet and the
    #: balance ledger. The vessel name and the destroy reason both carry SPACES -
    #: the property a whitespace-split parser would silently truncate.
    GHOST_LIFECYCLE_LOG = "".join(
        "[LOG] [Parsek][INFO][GhostRenderTrace] phase=%s rec=%s recId=%s "
        "ghostIndex=%d frame=%d currentUT=100.000 playbackUT=50.000 "
        "vessel=%s reason=%s\n" % row
        for row in (
            ("MeshSpawned", "smoke001", "smoke001aaaa", 0, 10,
             "Kerbal X Mk2", "ghost-created"),
            ("MeshSpawned", "smoke002", "smoke002bbbb", 1, 11,
             "Munar Lander", "ghost-created"),
            ("MeshDestroyed", "smoke001", "smoke001aaaa", 0, 90,
             "Kerbal X Mk2", "playback completed"),
        ))

    def _run_ghost_lifecycle(self, block=None, drop_lines=True):
        spec = _make_spec(self.template, 30, 600)
        spec["id"] = "SMOKE-ghostlifecycle"
        if block is not None:
            spec["expectations"][ghostlife.GHOST_LIFECYCLE_BLOCK] = copy.deepcopy(block)
        rt = FakeRuntime("pass", ghost_lifecycle_log_text=(
            self.GHOST_LIFECYCLE_LOG if drop_lines else None))
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        return result, rt

    def test_ghost_lifecycle_row_reports_facets_with_no_block(self):
        """The default state of every committed spec today: no block declared, so
        the row lands REPORT-ONLY carrying the measured facets and moving no
        verdict. The facets are recorded UNCONDITIONALLY - that is how a lane
        earns its first honest window off a green report-only run."""
        result, _ = self._run_ghost_lifecycle()
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        gl = result["verifiers"]["ghostLifecycle"]
        self.assertEqual("REPORT", gl["status"])
        self.assertFalse(gl["gating"])
        self.assertEqual([], gl["blocks"])
        self.assertEqual([], gl["armedBlocks"])
        self.assertEqual([], gl["mismatches"])
        self.assertIsNone(gl["declared"])
        facets = gl["observed"][ghostlife.GHOST_LIFECYCLE_BLOCK]
        self.assertEqual(2, facets["spawned"])
        self.assertEqual(1, facets["destroyLines"])
        self.assertEqual({"playback completed": 1}, facets["destroyedReasons"])
        # The spaces survived the whole pipeline - log write, harness read, parse.
        self.assertEqual(["Kerbal X Mk2", "Munar Lander"], facets["vessels"])
        self.assertEqual([{"recId": "smoke002bbbb", "rec": "smoke002",
                           "vessel": "Munar Lander"}], facets["unbalanced"])

    def test_declared_block_reports_mismatches_without_moving_the_verdict(self):
        """The reading run the arming workflow mandates: mismatches RECORDED,
        verdict untouched. `requireBalanced` defaults ON, so the leaked recording
        is reported even though the block declares nothing but a window."""
        result, _ = self._run_ghost_lifecycle({"spawned": {"min": 1, "max": 8}})
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        gl = result["verifiers"]["ghostLifecycle"]
        self.assertEqual("REPORT", gl["status"])
        self.assertEqual(["ghostLifecycle"], gl["blocks"])
        self.assertEqual([], gl["armedBlocks"])
        self.assertEqual({"spawned": {"min": 1, "max": 8}}, gl["declared"])
        self.assertTrue(any("requireBalanced" in m for m in gl["mismatches"]),
                        gl["mismatches"])

    def test_an_armed_block_moves_the_verdict(self):
        """The whole point of arming, and the only path that can red a run:
        PARSEK-FAIL(ghost-lifecycle). Unreachable for every committed spec today
        (none arms the block) and pinned so it works the day the first lane is
        promoted."""
        result, _ = self._run_ghost_lifecycle(
            {"gating": True, "spawned": {"min": 1, "max": 8}})
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        self.assertEqual("ghost-lifecycle", result["subkind"])
        gl = result["verifiers"]["ghostLifecycle"]
        self.assertEqual("FAIL", gl["status"])
        self.assertTrue(gl["gating"])
        self.assertEqual(["ghostLifecycle"], gl["armedBlocks"])

    def test_an_armed_block_passes_on_a_healthy_run(self):
        result, _ = self._run_ghost_lifecycle(
            {"gating": True, "spawned": {"min": 1, "max": 8},
             "requireBalanced": False})
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        gl = result["verifiers"]["ghostLifecycle"]
        self.assertEqual("PASS", gl["status"])
        self.assertEqual([], gl["mismatches"])

    def test_the_vacuity_floor_reds_a_run_that_rendered_nothing(self):
        """THE ROW'S REASON TO EXIST: a declared block over a log with no
        MeshSpawned lines - the tracer never armed, or the playback never spawned
        a mesh - must MISMATCH, not pass vacuously. The window here is one the
        zero measurement would otherwise SATISFY, so only the floor can red it."""
        result, _ = self._run_ghost_lifecycle(
            {"gating": True, "spawned": {"min": 0, "max": 4}}, drop_lines=False)
        self.assertEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])
        self.assertEqual("ghost-lifecycle", result["subkind"])
        gl = result["verifiers"]["ghostLifecycle"]
        self.assertTrue(any("no MeshSpawned lines" in m for m in gl["mismatches"]),
                        gl["mismatches"])

    def test_no_block_over_an_empty_log_is_silent(self):
        result, _ = self._run_ghost_lifecycle(drop_lines=False)
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        gl = result["verifiers"]["ghostLifecycle"]
        self.assertEqual("REPORT", gl["status"])
        self.assertEqual([], gl["mismatches"])
        self.assertEqual(0, gl["observed"][ghostlife.GHOST_LIFECYCLE_BLOCK]["spawned"])

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
        # M-C2: the save-parse row is SKIPPED on a torn (killed) save too - a
        # half-written persistent.sfs must never be read for structural counts.
        # Full key set on every branch so consumers never KeyError on shape.
        sp = result["verifiers"]["saveParse"]
        self.assertEqual("SKIPPED", sp["status"])
        self.assertEqual("killed", sp["reason"])
        self.assertFalse(sp["gating"])
        self.assertIsNone(sp["parsed"])
        self.assertEqual({}, sp["observed"])
        # M-A7: the render-composition row is SKIPPED on a torn (killed) run for
        # the same reason - the export flushes at scene exit / teardown, so a
        # watchdog-killed process leaves no manifest or a half-written one. FULL
        # KEY SET on this branch too, so a consumer never KeyErrors on the shape.
        rc = result["verifiers"]["renderCompose"]
        self.assertEqual({"status", "reason", "gating", "blocks", "armedBlocks",
                          "mismatches", "observed", "parsed", "parseError",
                          "findings", "unevaluable"}, set(rc))
        self.assertEqual("SKIPPED", rc["status"])
        self.assertEqual("killed", rc["reason"])
        self.assertFalse(rc["gating"])
        self.assertIsNone(rc["parsed"])
        self.assertEqual({}, rc["observed"])
        self.assertEqual([], rc["findings"])
        self.assertEqual({}, rc["unevaluable"])
        # The ghost-lifecycle row goes the OTHER way on a killed attempt, and the
        # divergence is about the SOURCE: the two rows above read the produced
        # save and the teardown-flushed manifest, which a kill tears; this one
        # reads the KSP.log, which the kill does not. So it REPORTS triage-only
        # (the unityExceptions precedent) instead of skipping. Never gating.
        gl = result["verifiers"]["ghostLifecycle"]
        self.assertEqual(ghostlife.STATUS_REPORT, gl["status"])
        self.assertEqual("killed-triage-only", gl["reason"])
        self.assertFalse(gl["gating"])
        self.assertEqual([], gl["mismatches"])
        self.assertIs(True, gl["parsed"])
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


class InjectPostconditionTests(unittest.TestCase):
    """HARNESS-INJECT-FAILS-OPEN, fail-closed half (found by S1.5 attempt 1; cost
    two more full V1-map-dwell flights before this fix). A fixture injection that
    exits 0 having written NOTHING must fail the STAGE, pre-boot, with the
    terminal INVALID(stage-inject-noop) classification - never report staging
    success and let the miss surface minutes later as an unrelated seam rejection
    (`invokerewind refused: unknown-rp`) classified against a correct spec."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-inject-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "gloops-airshow")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "inject_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _injected_spec(self, preset):
        spec = _make_spec(self.template, 30, 600)
        spec["fixture"]["injectedRecordings"] = preset
        return spec

    def test_noop_injection_is_terminal_invalid_pre_boot(self):
        # The full-run shape of the S1.5 / V1-map-dwell burn, driven through the
        # fake-KSP harness: the injector runs, exits 0, writes nothing. The run must
        # terminate INVALID(stage-inject-noop) WITHOUT booting KSP - the whole point
        # is that the miss costs seconds at stage time, not a 21-minute flight plus a
        # misdirecting `driver-arg` classification.
        spec = self._injected_spec("all-synthetic")
        rt = FakeRuntime("pass", inject_noop=True)
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("stage-inject-noop", result["subkind"])
        self.assertEqual(1, rt.inject_call_count, "the injector WAS invoked")
        self.assertEqual(0, rt.launch_count,
                         "a failed postcondition must never boot KSP")
        # Deterministic miss (same worktree -> same result): NOT retryable, so the
        # `once` budget is preserved for genuine flakes instead of burning a second
        # identical attempt (the V1-map-dwell double-flight shape).
        v = hlib.Verdict(result["verdict"], result["subkind"], False, "")
        self.assertFalse(hlib.should_retry(v, attempt=1, retry_policy="once"))
        # The harness log names the fail-closed classification at stage time.
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            log_body = fh.read()
        self.assertIn("stage-inject-noop", log_body)
        self.assertIn("inject postcondition failed", log_body)

    def test_successful_all_synthetic_injection_stages(self):
        spec = self._injected_spec("all-synthetic")
        ok, name, subkind = run.stage_fixture(spec, self.instance, FakeRuntime("pass"),
                                              self.logger)
        self.assertTrue(ok)
        self.assertEqual("", subkind)

    def test_successful_rewind_b9_injection_stages(self):
        spec = self._injected_spec("rewind-b9")
        ok, name, subkind = run.stage_fixture(spec, self.instance, FakeRuntime("pass"),
                                              self.logger)
        self.assertTrue(ok)
        self.assertEqual("", subkind)
        self.assertTrue(os.path.isfile(os.path.join(
            self.instance, "saves", name, "Parsek", "RewindPoints", "rp_b9_root.sfs")))

    def test_rewind_b9_without_its_rp_fails_closed(self):
        # The second silent trigger from the V1-map-dwell attempt 2 (KSP.log lock
        # probe refusing mid-way): recordings landed but the preset's RP did not. The
        # rewind-b9 postcondition requires BOTH, because the consumer's
        # `InvokeRewind rp=rp_b9_root` needs the RP specifically.
        class RpLessRuntime(FakeRuntime):
            def run_inject(self, instance_dir, save_name, timeout, preset="all-synthetic"):
                rec = os.path.join(instance_dir, "saves", save_name,
                                   "Parsek", "Recordings")
                os.makedirs(rec, exist_ok=True)
                open(os.path.join(rec, "b9-root.prec"), "w").close()
                return run.ToolResult(0, False)

        spec = self._injected_spec("rewind-b9")
        ok, _name, subkind = run.stage_fixture(spec, self.instance,
                                               RpLessRuntime("pass"), self.logger)
        self.assertFalse(ok)
        self.assertEqual("stage-inject-noop", subkind)

    def test_postcondition_predicate_shapes(self):
        # The predicate itself, so a refactor cannot silently invert a branch.
        save = os.path.join(self.tmp, "postcond-save")
        os.makedirs(save, exist_ok=True)
        self.assertEqual(["non-empty Parsek/Recordings/"],
                         run._inject_postcondition_missing(save, "all-synthetic"))
        self.assertEqual(["non-empty Parsek/Recordings/",
                          "Parsek/RewindPoints/rp_b9_root.sfs"],
                         run._inject_postcondition_missing(save, "rewind-b9"))
        # An EMPTY Recordings dir is still a miss (the dir alone proves nothing).
        rec = os.path.join(save, "Parsek", "Recordings")
        os.makedirs(rec, exist_ok=True)
        self.assertEqual(["non-empty Parsek/Recordings/"],
                         run._inject_postcondition_missing(save, "all-synthetic"))
        open(os.path.join(rec, "a.prec"), "w").close()
        self.assertEqual([], run._inject_postcondition_missing(save, "all-synthetic"))
        self.assertEqual(["Parsek/RewindPoints/rp_b9_root.sfs"],
                         run._inject_postcondition_missing(save, "rewind-b9"))
        rp_dir = os.path.join(save, "Parsek", "RewindPoints")
        os.makedirs(rp_dir, exist_ok=True)
        open(os.path.join(rp_dir, "rp_b9_root.sfs"), "w").close()
        self.assertEqual([], run._inject_postcondition_missing(save, "rewind-b9"))


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

    # ---- HARNESS-MIDMISSION-COMMIT-BYPASS, end to end ------------------------
    # The gap the tail gate above does NOT close, driven over the fake KSP + fake
    # mission: a route-1 mid-mission CommitTree that lands BEFORE the verdict
    # exists. REPORT-ONLY, so these assert on the RECORD, never on the verdict.

    def test_mid_mission_commit_before_unmet_is_recorded_but_moves_no_verdict(self):
        result, lines = self._run("midcommit")

        # (1) The bypass is real: the mission's CommitTree IS in the channel, while the
        #     DRIVER's own tail CommitTree was correctly skipped by the tail gate. Both
        #     facts at once are the whole point of the entry.
        # id 0003 = the mission step's own index-derived id, which run.py donates to
        # the mission as its reserved id (the mission step writes nothing itself).
        self.assertIn("id=0003 cmd=CommitTree", lines)
        self.assertIn("CommitTree", [r["cmd"] for r in result["driver"]["skippedTailSteps"]])

        # (2) It is now VISIBLE in the durable record, which is what was missing.
        mm = result["driver"]["midMissionSeamWrites"]
        self.assertEqual(1, mm["total"])
        self.assertEqual(1, mm["worldMutating"])
        self.assertEqual(["CommitTree"], mm["verbs"])
        self.assertTrue(mm["exposedAfterUnmetMission"])

        # (3) REPORT-ONLY: the verdict is the same INVALID(mission) the unmet mission
        #     already earned. If this ever changes, the instrument has become a gate.
        self.assertEqual("INVALID", result["verdict"])
        self.assertEqual("mission", result["subkind"])

    def test_a_mission_that_writes_nothing_leaves_the_record_unchanged(self):
        """Every committed non-B-DOCK mission ignores the seam args entirely. Their
        result record must be byte-identical to what it was before this instrument."""
        result, _ = self._run("assertfail")
        self.assertNotIn("midMissionSeamWrites", result["driver"])
        self.assertEqual("INVALID", result["verdict"])
        self.assertEqual("mission", result["subkind"])

    def test_an_unreadable_channel_records_a_GAP_not_a_silent_zero(self):
        """Failure-isolated must not mean SILENT.

        The read was `except OSError: pass`, which left a run whose channel could not be
        read byte-identical to one where the mission wrote nothing - reproducing exactly
        the invisibility this instrument exists to end, intermittently and with no trace
        (a Windows PermissionError while the KSP addon holds the file is the live route).
        It must say so, record a GAP rather than a zero, and still move no verdict."""
        import builtins
        real_open = builtins.open
        target = os.path.abspath(
            os.path.join(self.instance, "parsek-test-commands.txt"))
        fired = []

        def exploding_open(path, mode="r", *a, **kw):
            # ONE-SHOT, and only for a READ of the command channel: run.py's own append of
            # driver commands must still work, and so must this class's `_commands()`
            # helper, which reads the very same file once the run has returned.
            if ("r" in mode and not fired
                    and os.path.abspath(str(path)) == target):
                fired.append(True)
                raise PermissionError("channel held by another process")
            return real_open(path, mode, *a, **kw)

        builtins.open = exploding_open
        try:
            result, _ = self._run("midcommit")
        finally:
            builtins.open = real_open
        self.assertTrue(fired, "the instrument never read the channel; gate is vacuous")

        mm = result["driver"]["midMissionSeamWrites"]
        self.assertIn("PermissionError", mm["readError"])
        # A GAP is not a zero: the count keys are ABSENT rather than reading 0, so nobody
        # can mistake "we could not look" for "we looked and saw none".
        self.assertNotIn("total", mm)
        self.assertNotIn("exposedAfterUnmetMission", mm)
        # Still report-only: the verdict is the one the unmet mission already earned.
        self.assertEqual("INVALID", result["verdict"])
        self.assertEqual("mission", result["subkind"])



def _make_science_bench_spec(save_template, run_budget=900):
    """A flown scenario naming the REAL committed `science_bench_recover` mission,
    with the params its REAL committed schema declares. Shaped for the wave-2
    career forge: fly the career-pad craft, collect + transmit science, recover it,
    commit, and let the analyzer read the produced save.

    Deliberately NOT committed under harness/scenarios: this wave ships the
    CAPABILITY, and a spec is only honest once its fixture is pinned and its
    windows are measured off a reading run. What this in-memory spec proves is the
    plumbing - that run.py resolves the mission on disk, the pure validator admits
    these params against the committed schema, and the whole attempt drives to PASS
    with no game."""
    return {
        "schema": 1,
        "id": "SMOKE-science-bench-recover",
        "tier": "daily",
        "instanceProfile": "stock-minimal",
        "fixture": {"saveTemplate": save_template, "injectedRecordings": "none", "craft": []},
        "driver": {
            "kind": "autopilot",
            "mission": "science_bench_recover",
            "missionParams": {
                "throttle": 1.0,
                "apoapsisWindowMeters": {"min": 6000, "max": 30000},
                "chuteArmMaxRateMps": 30,
                "chuteFullDeployAltMeters": 2500,
                "landedSituations": ["LANDED", "SPLASHED"],
                "ascentTimeoutSeconds": 90,
                "coastTimeoutSeconds": 180,
                "descentTimeoutSeconds": 360,
                "collectMinExperiments": 1,
                "collectTimeoutSeconds": 120,
                "transmitMinScienceGain": 0.5,
                "transmitTimeoutSeconds": 120,
                # A SMALL POSITIVE FLOOR, deliberately, even though the schema
                # legally allows 0.0. Section 4d tells wave 2 to promote this
                # spec VERBATIM, so it has to start strong: at 0.0 the recovery
                # terminal certifies "the funds pool was READABLE across the
                # recovery" and nothing about the pool having MOVED, which is
                # weaker than a career forge wants from the row it exists to
                # produce. 1.0 is defense in depth on top of the structural
                # guards (craft observed gone, credit measured across the event),
                # not a prediction: any real recovery refunds orders of magnitude
                # more, so it cannot false-fail a good flight, and it does catch
                # a recovery that credited literally nothing.
                "recoverMinFundsGain": 1.0,
                "recoverTimeoutSeconds": 180,
            },
            "steps": [
                {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"},
                 "expect": "OK", "budget": 30},
                {"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "true"},
                 "expect": "OK"},
                {"phase": "mission", "expect": "MISSION-OK", "budget": 300},
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


class ScienceBenchRecoverAdmissionTests(unittest.TestCase):
    """The career-earning capability, admitted and driven with NO real game.

    Two halves, and both are needed. The mission-machine half (does the pad hop
    hand over to collect / transmit / recover, do the three verbs fire in order,
    does a recovered craft certify) is proven against a scripted telemetry seam in
    `harness/missions/lib/test_science_bench_recover.py`. THIS half is the other
    end of the same rope: run.py resolving the mission on disk, the PURE validator
    admitting its params against the COMMITTED schema, and the whole attempt
    driving LoadGame -> mission -> CommitTree -> FlushAndQuit -> verifiers to PASS
    over the fake KSP.

    Unlike the sibling autopilot smoke tests this one names the REAL mission
    rather than `fake_mission`, so it reads the committed schema file: a schema
    that stops admitting the params a spec would carry reds HERE, before a spec is
    ever written against it."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-sbr-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "career-pad-craft")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "sbr_harness.log"))

    def tearDown(self):
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    # The VALIDATION cells declare a relative fixture path (validate_spec rejects
    # an absolute one by design); the DRIVEN cells use the temp template, which
    # the stage step joins whole. Two shapes because the two halves are checked by
    # two different rules, not because either is a workaround.
    def _validating_spec(self):
        spec = _make_science_bench_spec("fixtures/saves/career-pad-craft")
        return spec

    def test_the_committed_mission_resolves_on_disk(self):
        """Both files exist under the REAL harness/missions and parse: a mission
        with a schema and no shell (or the reverse) is a spec-invalid INVALID that
        never boots KSP, and it is cheaper to catch here than at admission."""
        spec = _make_science_bench_spec(self.template)
        registry, errors = run.resolve_mission_schemas(spec)
        self.assertEqual([], errors)
        self.assertIn("science_bench_recover", registry)
        self.assertIn("collectMinExperiments", registry["science_bench_recover"]["params"])

    def test_the_pure_validator_admits_a_spec_built_on_it(self):
        spec = self._validating_spec()
        registry, _errors = run.resolve_mission_schemas(spec)
        validation = hlib.validate_spec(spec, {}, [], registry)
        self.assertTrue(validation.ok, validation.errors)

    def test_a_career_param_outside_its_declared_range_is_rejected(self):
        """The negative control for the arming that matters most: a
        transmitMinScienceGain of 0.0 makes the credit gate satisfiable by any two
        finite readings, so a transmit that credited NOTHING would report
        MISSION-OK. The schema's exclusive floor is what forbids it, and this is
        the cell that proves the floor is doing work."""
        spec = self._validating_spec()
        spec["driver"]["missionParams"]["transmitMinScienceGain"] = 0.0
        registry, _errors = run.resolve_mission_schemas(spec)
        validation = hlib.validate_spec(spec, {}, [], registry)
        self.assertFalse(validation.ok)
        self.assertTrue(any("transmitMinScienceGain" in e for e in validation.errors),
                        validation.errors)

    def test_a_missing_career_param_is_rejected(self):
        spec = self._validating_spec()
        del spec["driver"]["missionParams"]["collectMinExperiments"]
        registry, _errors = run.resolve_mission_schemas(spec)
        validation = hlib.validate_spec(spec, {}, [], registry)
        self.assertFalse(validation.ok)
        self.assertTrue(any("collectMinExperiments" in e for e in validation.errors),
                        validation.errors)

    def test_the_full_attempt_drives_to_pass_with_no_game(self):
        """End to end over the fake KSP + a fake mission subprocess: the flown
        chain, the mission-validity gate, and the verifier chain over the produced
        save. Fails if adding this mission perturbs the autopilot handoff."""
        spec = _make_science_bench_spec(self.template)
        rt = FakeRuntime("autopilot", mission_mode="ok")
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"],
                         "%s (%s)" % (result["verdict"], result.get("subkind")))
        mission_rows = [s for s in result["driver"]["steps"] if s.get("phase") == "mission"]
        self.assertEqual(1, len(mission_rows))
        self.assertEqual("MISSION-OK", mission_rows[0]["missionVerdict"])
        self.assertEqual("PASS", result["verifiers"]["mission"]["status"])
        self.assertEqual("PASS", result["verifiers"]["expectations"]["status"])

    def test_a_mission_that_did_not_fly_is_driver_invalid_never_parsek_fail(self):
        """THE ORTHOGONALITY RULE on this lane, end to end. Every one of the
        mission's named non-success terminals - a craft with no science part, an
        uncredited transmit, an unrecoverable vessel - reports MISSION-ASSERT-FAIL,
        and the harness must classify that as a retryable driver-INVALID rather
        than as a defect in the mod. A forge that failed to forge is not a Parsek
        bug."""
        spec = _make_science_bench_spec(self.template)
        rt = FakeRuntime("autopilot", mission_mode="assertfail")
        result = run.run_attempt(spec, self.instance, self.tmp, rt, attempt=1,
                                 prior_boot_crashed=False, logger=self.logger)
        self.assertEqual(hlib.VERDICT_INVALID, result["verdict"])
        self.assertEqual("mission", result["subkind"])
        self.assertNotEqual(hlib.VERDICT_PARSEK_FAIL, result["verdict"])


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
                         prior_boot_crashed, logger, run_ordinal=1):
            i = calls["n"]
            calls["n"] += 1
            return {"schema": hlib.SCHEMA_VERSION,
                    # Built the way production does, so the stub keeps mirroring
                    # run_attempt's id (ordinal before the terminal attempt suffix).
                    "runId": hlib.format_run_id("2026-07-25_0100", "S1", attempt,
                                                run_ordinal),
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

    def _drive_run_budget_kill(self, rt):
        """Drive the RUN-budget branch (not the mission-budget one) with a
        world-mutating mid-mission write already in the channel."""
        with open(os.path.join(self.tmp, "parsek-test-commands.txt"),
                  "w", encoding="utf-8") as fh:
            fh.write("id=0003 cmd=CommitTree\n")
        result = run.DriveResult()
        ctx = run.MissionContext("m", "vpy", "m.py", {}, self.tmp,
                                 "stamp.json", {"krpc": "0.5.4"})
        step = {"phase": "mission", "expect": "MISSION-OK", "budget": 10_000}
        proc = type("P", (), {"pid": 12345})()
        killed = run._drive_mission_step(result, step, "0003", 2, proc, rt, self.logger,
                                         run_budget=1.0, run_start=0.0,
                                         mission_ctx=ctx, run_id="testrun",
                                         preceding_load_ok=True)
        return result, killed

    def test_run_budget_kill_reads_the_channel_and_judges_the_REAL_verdict(self):
        """The run-budget kill must record mid-mission seam writes, and must judge them
        against the verdict actually on disk - NOT against a hardcoded "killed means
        unmet".

        NOT HYPOTHETICAL: `poll_exit` is checked BEFORE the budget, so the live window is
        "result already written, process not yet reaped" - the ordinary shape for a long
        mission near its wall, and the same window the NIT 7 cells above exist for. A
        first version of this fix hardcoded `mission_met=False`; over a mission that had
        written MISSION-OK it recorded `exposedAfterUnmetMission: true` and logged
        "returned UNMET", and since this path never sets `result.mission_step`, nothing
        in the record contradicted it.

        Replaces a source-grep gate that only asserted a call EXISTED - it could not see
        wrong arguments, which is exactly why it missed that defect."""
        result, killed = self._drive_run_budget_kill(
            _MiniMissionRuntime(write_result_verdict="MISSION-OK"))

        self.assertTrue(killed, "expected the RUN-budget branch, not the mission-budget one")
        self.assertEqual("run", result.kill_scope)
        # (a) it read the channel at all - the regression the fix was for
        mm = result.mid_mission_seam_writes
        self.assertIsNotNone(mm)
        self.assertEqual(1, mm.total)
        self.assertEqual(1, mm.world_mutating)
        # (b) and judged it against the REAL verdict: the mission MET, so this is not the
        #     exposed shape and the summary must not claim it returned UNMET
        self.assertFalse(mm.exposed)
        self.assertNotIn("UNMET", mm.summary)
        self.assertIn("; mission met", mm.summary)

    def test_run_budget_kill_with_no_verdict_IS_the_exposed_shape(self):
        """The other half: killed with nothing written is genuinely unmet, and a
        world-mutating write there is precisely what this instrument exists to surface.
        Without this cell, reading the real verdict could regress to always-met."""
        result, _ = self._drive_run_budget_kill(
            _MiniMissionRuntime(write_result_verdict=None))

        mm = result.mid_mission_seam_writes
        self.assertIsNotNone(mm)
        self.assertTrue(mm.exposed)
        self.assertIn("UNMET", mm.summary)

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

    # ---- world ROSTER sub-facet (the L1 dismiss claim), through the REAL verifier ----

    @staticmethod
    def _kerbal(name, state="Available"):
        return {"name": name, "gender": "Male", "type": "Crew",
                "trait": "Pilot", "state": state}

    def _career_with_roster(self, roster, has_roster=True):
        block = self._career_block()
        block["hasRoster"] = has_roster
        block["roster"] = list(roster)
        return block

    def test_world_roster_dismissed_kerbal_still_present_reds(self):
        # The L1 shape: the spec declares the dismissed kerbal absent, the produced
        # save still carries him -> hard drift -> PARSEK-FAIL(ledger).
        career = self._career_with_roster([
            self._kerbal("Jebediah Kerman"), self._kerbal("Bill Kerman")])
        world = {"roster": {"absent": ["Bill Kerman"],
                            "present": ["Jebediah Kerman"]}}
        result, drift, tooling = run._run_ledger_oracle(
            None, world, career, None, "", "e2e-roster-red", self.logger)
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertFalse(tooling)

    def test_world_roster_dismissal_applied_passes(self):
        career = self._career_with_roster([
            self._kerbal("Jebediah Kerman"), self._kerbal("Bob Kerman"),
            self._kerbal("Valentina Kerman")])
        world = {"roster": {"absent": ["Bill Kerman"],
                            "present": ["Jebediah Kerman", "Bob Kerman",
                                        "Valentina Kerman"]}}
        result, drift, tooling = run._run_ledger_oracle(
            None, world, career, None, "", "e2e-roster-ok", self.logger)
        self.assertEqual("PASS", result["status"])
        self.assertFalse(drift)

    def test_world_roster_unexported_roster_reds_rather_than_greens(self):
        # An analyzer that never exported the roster (hasRoster false) must not let a
        # declared roster claim pass unverified. The run still reds - fail-closed is the
        # property under test - but it reds as INVALID(tooling), because a missing
        # analyzer export is a tooling fault, not a Parsek ledger defect.
        career = self._career_with_roster([], has_roster=False)
        world = {"roster": {"absent": ["Bill Kerman"]}}
        result, drift, tooling = run._run_ledger_oracle(
            None, world, career, None, "", "e2e-roster-unexported", self.logger)
        self.assertEqual("INVALID", result["status"])
        self.assertEqual("tooling", result["subkind"])
        self.assertNotEqual("PASS", result["status"], "must never green on a missing input")
        self.assertFalse(drift, "a missing analyzer export is not ledger drift")
        self.assertTrue(tooling)

    def test_world_roster_declared_against_empty_but_exported_roster_still_diffs(self):
        # hasRoster TRUE with an empty roster is a REAL state (a wiped roster), not a
        # tooling fault: the tooling route must not swallow it. `present` claims red.
        career = self._career_with_roster([], has_roster=True)
        world = {"roster": {"present": ["Jebediah Kerman"]}}
        result, drift, tooling = run._run_ledger_oracle(
            None, world, career, None, "", "e2e-roster-empty-exported", self.logger)
        self.assertEqual("FAIL", result["status"])
        self.assertTrue(drift)
        self.assertFalse(tooling)

    def test_world_block_without_a_roster_declaration_is_unaffected(self):
        # Every existing world declarer must be byte-unaffected by the new sub-facet.
        career = self._career_with_roster([self._kerbal("Bill Kerman")])
        world = {"vessels": {"entry": []}}
        result, drift, tooling = run._run_ledger_oracle(
            None, world, career, None, "", "e2e-roster-undeclared", self.logger)
        self.assertEqual("PASS", result["status"])
        self.assertEqual(0, result["hardDivergences"])
        self.assertEqual(0, result["reportOnly"])
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

    def _run(self, mode="pass", run_tests_budget=30, run_budget=600,
             render_manifest_text=None):
        spec = _make_spec(self.template, run_tests_budget, run_budget)
        rt = FakeRuntime(mode, render_manifest_text=render_manifest_text)
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

    def test_pass_run_copies_the_render_manifest_into_the_shots_dir(self):
        """M-A7: a PASS runs no collect-logs and the next boot overwrites the KSP
        root, so without this leg a green run's manifest - the ONE artifact a
        report-only composition row exists to produce - dies with that boot. The
        key is ADDITIVE and False when no manifest was produced."""
        # Baseline: no manifest produced -> the flag reads False, not absent.
        result, _ = self._run("pass")
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertFalse(result["artifacts"]["renderManifest"])

        # With one produced, it rides into results/<runId>_shots/ verbatim. It is
        # dropped at LAUNCH, after the stage rotation, because that is where a
        # real export lands - a pre-stage drop would be rotated away.
        result, _ = self._run("pass", render_manifest_text=_RENDER_MANIFEST_TEXT)
        self.assertEqual(hlib.VERDICT_PASS, result["verdict"])
        self.assertTrue(result["artifacts"]["renderManifest"])
        copied = os.path.join(run.RESULTS_DIR, "%s_shots" % result["runId"],
                              run.RENDER_MANIFEST_FILENAME)
        self.assertTrue(os.path.isfile(copied))
        with open(copied, "r", encoding="utf-8") as fh:
            self.assertEqual(_RENDER_MANIFEST_TEXT, fh.read())
        # No tmp file left behind by the tmp+rename write.
        self.assertFalse(os.path.isfile(copied + ".harness-tmp"))
        # ... and the flag round-trips into the durable result JSON.
        with open(os.path.join(run.RESULTS_DIR, "%s.json" % result["runId"]),
                  "r", encoding="utf-8") as fh:
            self.assertTrue(json.load(fh)["artifacts"]["renderManifest"])

    def test_render_manifest_copy_is_bounded(self):
        """The copy is byte-bounded like the KSP.log leg: an oversize export keeps
        a HEAD slice with an explicit marker rather than busting the cap."""
        orig = run.ARTIFACT_RENDER_MANIFEST_CAP_BYTES
        run.ARTIFACT_RENDER_MANIFEST_CAP_BYTES = 128
        try:
            result, _ = self._run("pass",
                                  render_manifest_text=_RENDER_MANIFEST_TEXT)
            copied = os.path.join(run.RESULTS_DIR, "%s_shots" % result["runId"],
                                  run.RENDER_MANIFEST_FILENAME)
            with open(copied, "r", encoding="utf-8") as fh:
                body = fh.read()
        finally:
            run.ARTIFACT_RENDER_MANIFEST_CAP_BYTES = orig
        self.assertIn("TRUNCATED at the 128-byte cap", body)
        self.assertTrue(body.startswith("RENDER_MANIFEST"))
        self.assertLess(len(body), len(_RENDER_MANIFEST_TEXT) + 200)

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
        self.assertLess(os.path.getsize(dst), 200 + 400,
                        "a mid-copy grown source must not bust the cap")
        # Review NEW-2: the kept tail slice was computed from the STALE size
        # snapshot, so it is NOT the real end of the log -- the artifact must
        # say so instead of letting a reader trust mid-log spam as the
        # teardown/BATCH_COMPLETE tail.
        with open(dst, "rb") as fh:
            self.assertIn(b"[harness-artifact] KSP.log GREW during the copy", fh.read())

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

    def test_retention_still_runs_when_the_artifact_copy_fails(self):
        """Review NEW-5: a full disk FAILS the copy, and that is exactly when
        pruning matters most -- the retention pass lives in its own try so a
        copy exception cannot skip it."""
        os.makedirs(run.RESULTS_DIR, exist_ok=True)
        now = time.time()
        for i, name in enumerate(["stale1_shots", "stale2_shots", "stale3_shots"]):
            d = os.path.join(run.RESULTS_DIR, name)
            os.makedirs(d, exist_ok=True)
            with open(os.path.join(d, "KSP.log"), "w") as fh:
                fh.write("x" * 10)
            os.utime(d, (now - 3600 + i, now - 3600 + i))
        with open(os.path.join(self.instance, "KSP.log"), "w") as fh:
            fh.write("boot\n")
        orig_plan = hlib.plan_artifact_log_copy
        orig_keep = hlib.ARTIFACT_SHOTS_KEEP_DIRS
        hlib.plan_artifact_log_copy = _raise_artifact_boom
        try:
            hlib.ARTIFACT_SHOTS_KEEP_DIRS = 2
            art = run._collect_run_artifacts("diskfull-run", self.instance,
                                             time.time(), self.logger)
        finally:
            hlib.plan_artifact_log_copy = orig_plan
            hlib.ARTIFACT_SHOTS_KEEP_DIRS = orig_keep
        self.assertFalse(art["kspLog"], "the copy did fail")
        survivors = sorted(n for n in os.listdir(run.RESULTS_DIR) if n.endswith("_shots"))
        self.assertEqual(["diskfull-run_shots", "stale3_shots"], survivors,
                         "retention must prune despite the failed copy, protecting the current run")

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

class DryRunPlanVerifierEnumerationTests(unittest.TestCase):
    """--dry-run's [VERIFY] line must name the verifiers that can move the verdict.

    It was a hand-maintained string literal and it went stale: the M-C2 `saveParse`
    row shipped without being added, so on 2026-07-31 - the day S4.1 ARMED
    `gating = true` - the plan for the one gating scenario advertised a chain that
    did not include the gate. A plan that under-reports is worse than no plan: an
    operator reads it as "report-only" and mis-attributes the resulting red.

    `print_dry_run_plan` renders THREE states - armed, declared-but-report-only,
    and no-block-declared. Only two of them are reachable from the committed
    corpus: S4.1 is the sole declarer and it is now ARMED, so the middle state
    has no committed spec to pin it against. It is pinned here with a SYNTHETIC
    spec instead, and that is not a formality - declared-but-unarmed is the state
    EVERY future declarer passes through on the mandated read-report-only-then-arm
    workflow, so shipping it broken would be found by an operator rather than by
    the suite. Pinning it also makes `armed` and `declared` distinguishable: with
    committed specs alone, `if sp_armed:` and `if sp_declared:` are the same
    predicate over the corpus, and a regression advertising a merely-declared
    block as an armed gate passes every cell."""

    _SPECS = None

    @classmethod
    def _all_specs(cls):
        # Cached: this class renders a plan per committed spec, and reloading all
        # 61 TOMLs per call made the sweep parse ~3,800 files for no reason.
        if cls._SPECS is None:
            cls._SPECS = run.load_all_specs()
        return cls._SPECS

    def _render(self, spec):
        import io
        import contextlib
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            run.print_dry_run_plan([spec], lambda _p: "C:/instance",
                                   run.HarnessLogger(None))
        return next(l for l in buf.getvalue().splitlines() if "[VERIFY " in l)

    def _plan(self, scenario_id):
        spec = next((s for s in self._all_specs() if s.get("id") == scenario_id), None)
        self.assertIsNotNone(spec, "no committed spec with id %r" % scenario_id)
        return self._render(spec)

    def test_an_armed_spec_names_the_gate_and_its_failure_subkind(self):
        line = self._plan("S4.1-rewind-merge")
        self.assertIn("saveParse(armed: rewind", line)
        self.assertIn("PARSEK-FAIL(save-structure)", line)

    def test_a_declared_but_unarmed_block_renders_report_only(self):
        """SYNTHETIC. No committed spec can reach this branch (see class docstring),
        so a real-spec fixture would silently stop covering it."""
        line = self._render({"id": "SYNTH-declared-unarmed", "driver": {"steps": []},
                             "expectations": {"rewind": {"supersedeRows": {"max": 0}}}})
        self.assertIn("saveParse(report-only: rewind)", line)
        # The whole point: a declared-but-unarmed block must NOT advertise a gate.
        self.assertNotIn("armed", line)
        self.assertNotIn("save-structure", line)

    def test_an_armed_block_is_rendered_differently_from_a_declared_one(self):
        """Pins that `armed` and `declared` are distinct predicates. Over the
        committed corpus alone they are indistinguishable, so this comparison is
        the only thing standing between us and `if sp_declared:` at run.py:2882."""
        declared_only = {"id": "SYNTH-a", "driver": {"steps": []},
                         "expectations": {"rewind": {"supersedeRows": {"max": 0}}}}
        armed = {"id": "SYNTH-b", "driver": {"steps": []},
                 "expectations": {"rewind": {"gating": True,
                                             "supersedeRows": {"max": 0}}}}
        self.assertNotEqual(self._render(declared_only), self._render(armed))
        self.assertIn("saveParse(armed: rewind", self._render(armed))

    def test_a_spec_declaring_no_block_says_facets_only(self):
        # B1-pad-hop is used deliberately rather than CL-2: CL-2 is the spec this
        # work names as the NEXT declarer (stage B), so pinning the
        # no-block-declared rendering to it would red for a reason unrelated to
        # what this cell guards the moment stage B is authored.
        line = self._plan("B1-pad-hop")
        self.assertIn("saveParse(facets only, no block declared)", line)
        self.assertNotIn("save-structure", line)

    def test_every_scenario_plan_names_saveparse(self):
        """The row runs on every driver-valid run, so no spec's plan may omit it."""
        missing = [s["id"] for s in self._all_specs()
                   if "saveParse(" not in self._plan(s["id"])]
        self.assertEqual([], missing, "dry-run plan omitted the saveParse row")

    # -- renderCompose (row 7c, M-A7): the SAME three states and the SAME staleness
    # class as the saveParse block above, pinned from the day the row landed rather
    # than on the day the first lane armed.
    #
    # THE SYNTHETIC/COMMITTED SPLIT MOVED ON 2026-08-25, the same way saveParse's
    # did. The ARMED state now has committed subjects - V14M and V8 were armed off
    # their own report-only reading runs - so that cell is stated against a REAL
    # spec, which is the strictly stronger fixture: it also proves a committed block
    # actually reaches the armed branch of the renderer. The DECLARED-BUT-UNARMED
    # state has NO committed subject any more (every declarer is armed), so it stays
    # SYNTHETIC for saveParse's stated reason - a real-spec fixture there would have
    # silently stopped covering the branch on the day the lane armed.

    def test_an_armed_render_composition_spec_names_the_gate_and_subkind(self):
        """COMMITTED subject (V14M, armed 2026-08-25)."""
        line = self._plan("V14M-ike-player-loop")
        self.assertIn("renderCompose(armed: renderComposition", line)
        self.assertIn("PARSEK-FAIL(render-composition)", line)

    def test_a_declared_but_unarmed_render_composition_block_renders_report_only(self):
        """SYNTHETIC, and must stay so.

        Its original note read "every committed declarer is now ARMED", which was true
        of the four-declarer corpus it was written against and stopped being true on
        2026-08-26, when Phase 3C/Wave B took the declarer roster to 24 against 6 armed
        lanes. Committed declared-but-unarmed subjects are therefore the NORM now, not
        the exception - but which lane is bare moves with every arming decision, and a
        cell that named one would be re-pointed by unrelated work. The synthetic input
        states the property unconditionally and does not participate in that churn.

        (Corrected 2026-08-28 while the watch-entry change briefly de-armed V14M. The
        de-arm was reverted in the same change; this note is not about it.)"""
        line = self._render({"id": "SYNTH-rc-declared", "driver": {"steps": []},
                             "expectations": {"renderComposition": {"dwells": {"min": 1}}}})
        self.assertIn("renderCompose(report-only: renderComposition", line)
        self.assertNotIn("render-composition", line)

    def test_the_plan_prints_the_declared_assertions_not_just_the_block_name(self):
        # The pre-flight read that settles "is my negative control actually
        # loaded?" BEFORE the machine lock. Run `2026-08-25_1811` armed a control
        # by substring-replacing the spec's `warpBuckets` line, hit a rationale
        # COMMENT quoting the same literal ahead of the real key, and flew the
        # UNINVERTED block for 48 minutes; the block's NAME reads identically
        # either way, so only the key/values can tell the two apart.
        armed = self._render(
            {"id": "SYNTH-rc-values", "driver": {"steps": []},
             "expectations": {"renderComposition": {
                 "gating": True, "warpBuckets": ["warpHigh"]}}})
        self.assertIn("declared:", armed)
        self.assertIn("warpHigh", armed)
        # The inversion is VISIBLE: two armed blocks naming the same block render
        # differently, which is the whole point of printing the values.
        uninverted = self._render(
            {"id": "SYNTH-rc-values", "driver": {"steps": []},
             "expectations": {"renderComposition": {
                 "gating": True, "warpBuckets": ["warp100", "warp1000"]}}})
        self.assertNotEqual(armed, uninverted)
        self.assertNotIn("warpHigh", uninverted)
        # Report-only blocks print theirs too; an undeclared spec prints none.
        self.assertIn("declared:", self._render(
            {"id": "SYNTH-rc-values2", "driver": {"steps": []},
             "expectations": {"renderComposition": {"dwells": {"min": 1}}}}))
        self.assertNotIn("declared:", self._plan("B1-pad-hop"))

    def test_an_armed_render_composition_block_renders_differently_from_declared(self):
        declared_only = {"id": "SYNTH-rc-a", "driver": {"steps": []},
                         "expectations": {"renderComposition": {"dwells": {"min": 1}}}}
        armed = {"id": "SYNTH-rc-b", "driver": {"steps": []},
                 "expectations": {"renderComposition": {"gating": True,
                                                        "dwells": {"min": 1}}}}
        self.assertNotEqual(self._render(declared_only), self._render(armed))

    def test_a_spec_declaring_no_render_composition_block_says_facets_only(self):
        line = self._plan("B1-pad-hop")
        self.assertIn("renderCompose(facets only, no block declared)", line)
        self.assertNotIn("render-composition", line)

    def test_every_scenario_plan_names_rendercompose(self):
        """The row runs on every driver-valid run, so no spec's plan may omit it."""
        missing = [s["id"] for s in self._all_specs()
                   if "renderCompose(" not in self._plan(s["id"])]
        self.assertEqual([], missing, "dry-run plan omitted the renderCompose row")

    def test_the_launch_line_names_the_render_manifest_env_var_only_when_declared(self):
        """The LAUNCH line, not the VERIFY line: the recorder is armed by the
        DECLARATION, and a lane whose manifest never gets written reads as a
        product defect unless the plan says the env var was never set."""
        def _launch(spec):
            import io
            import contextlib
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                run.print_dry_run_plan([spec], lambda _p: "C:/instance",
                                       run.HarnessLogger(None))
            return next(l for l in buf.getvalue().splitlines() if "[LAUNCH " in l)

        declared = _launch({"id": "SYNTH-rc-launch", "driver": {"steps": []},
                            "expectations": {"renderComposition": {}}})
        self.assertIn("PARSEK_RENDER_MANIFEST=1", declared)
        bare = _launch({"id": "SYNTH-no-rc", "driver": {"steps": []},
                        "expectations": {}})
        self.assertNotIn("PARSEK_RENDER_MANIFEST", bare)

    # -- unityExceptions (row 6b): the SAME staleness class as the saveParse row
    # above, found the day it mattered. The [VERIFY] line hard-coded
    # "unityExceptions(report-only)" as part of its hand-maintained literal, so on
    # 2026-08-04 - the day 14 specs armed maxTotal - the plan advertised every one
    # of them as unarmed. Same fix, same pinning.

    def test_an_armed_unity_budget_names_the_gate_and_its_subkind(self):
        line = self._plan("H23-tracking-station")
        self.assertIn("unityExceptions(armed: maxTotal=6", line)
        self.assertIn("PARSEK-FAIL(unity-exception)", line)

    def test_an_unarmed_spec_renders_unity_report_only(self):
        # B1-pad-hop declares no unityExceptions block (it is in the noisy
        # warp-family remainder the calibration sweep deliberately left unarmed).
        line = self._plan("B1-pad-hop")
        self.assertIn("unityExceptions(report-only)", line)
        self.assertNotIn("PARSEK-FAIL(unity-exception)", line)

    def test_every_scenario_plan_names_unity_exceptions(self):
        """The scan runs on every driver-valid run, so no spec's plan may omit it."""
        missing = [s["id"] for s in self._all_specs()
                   if "unityExceptions(" not in self._plan(s["id"])]
        self.assertEqual([], missing, "dry-run plan omitted the unityExceptions row")


class _LockRuntime(run.Runtime):
    """Clock + liveness under test control. Nothing else is stubbed: the lockfile
    I/O under test is the REAL os.open/os.remove path."""

    def __init__(self, now=1000.0, alive=True):
        self._now = now
        self._alive = alive
        self.alive_queries = []

    def now(self):
        return self._now

    def pid_alive(self, pid):
        self.alive_queries.append(pid)
        return self._alive(pid) if callable(self._alive) else self._alive


class MachineLockWiringTests(unittest.TestCase):
    """The lock WIRING (as opposed to provlib's pure decision) had ZERO test
    coverage before this: no cell ever wrote a lockfile and drove run.py against
    it, which is how the per-attempt scope, the non-atomic acquire, and the
    unowned release all survived. These drive the real file operations."""

    def setUp(self):
        self.umbrella = tempfile.mkdtemp(prefix="parsek-lock-")
        self.addCleanup(shutil.rmtree, self.umbrella, ignore_errors=True)
        self.logger = run.HarnessLogger(os.path.join(self.umbrella, "harness.log"))
        self.addCleanup(self.logger.close)

    def _lock(self):
        return run.run_lock_path(self.umbrella)

    def _seed(self, payload):
        path = self._lock()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(payload, fh)
        return path

    def _read(self):
        with open(self._lock(), "r", encoding="utf-8") as fh:
            return json.load(fh)

    # ---- acquire ---------------------------------------------------------

    def test_acquire_on_clean_machine_writes_our_identity(self):
        rt = _LockRuntime()
        path = run.acquire_run_lock(self.umbrella, rt, self.logger, selection="tier=daily")
        self.assertEqual(path, self._lock())
        body = self._read()
        self.assertEqual(body["pid"], os.getpid())
        # Rich identity: a refusal must be able to name WHO holds the lock.
        self.assertEqual(body["selection"], "tier=daily")
        self.assertIn("worktree", body)
        self.assertIn("startedIso", body)

    def test_acquire_creates_the_parent_directory(self):
        # A machine that has never provisioned has no automation/ dir yet.
        self.assertFalse(os.path.isdir(os.path.dirname(self._lock())))
        self.assertIsNotNone(run.acquire_run_lock(self.umbrella, _LockRuntime(), self.logger))

    def test_live_sibling_refuses_and_leaves_their_lock_intact(self):
        self._seed({"pid": os.getpid() + 1, "timestamp": 1000.0,
                    "worktree": "C:/other", "selection": "tier=nightly"})
        rt = _LockRuntime(now=1000.0, alive=True)
        self.assertIsNone(run.acquire_run_lock(self.umbrella, rt, self.logger))
        self.assertEqual(self._read()["pid"], os.getpid() + 1,
                         "a refused acquire must not touch the holder's lock")

    def test_dead_holder_is_reclaimed(self):
        self._seed({"pid": os.getpid() + 1, "timestamp": 1000.0})
        rt = _LockRuntime(now=1000.0, alive=False)
        self.assertIsNotNone(run.acquire_run_lock(self.umbrella, rt, self.logger))
        self.assertEqual(self._read()["pid"], os.getpid())

    def test_expired_lease_is_reclaimed_even_though_the_pid_looks_alive(self):
        """The pid-reuse wedge (L4): without expiry this instance is refused
        forever once a recycled pid occupies the lockfile."""
        self._seed({"pid": os.getpid() + 1, "timestamp": 0.0})
        rt = _LockRuntime(now=provlib_lease() + 1.0, alive=True)
        self.assertIsNotNone(run.acquire_run_lock(self.umbrella, rt, self.logger))
        self.assertEqual(self._read()["pid"], os.getpid())

    def test_torn_lockfile_does_not_crash_the_acquire(self):
        path = self._lock()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write("{not json")
        self.assertIsNotNone(run.acquire_run_lock(self.umbrella, _LockRuntime(), self.logger))

    def test_acquire_is_atomic_not_read_decide_write(self):
        """Two racers must not both win. The old read-decide-write left a window
        (both shell out to `tasklist` between the read and the write) in which
        both reclaimed the same stale lock and both launched.

        Proven behaviorally: with the lockfile ALREADY present and its holder
        live, an acquire must refuse without ever overwriting it -- the failure
        mode of a read-decide-write is that it writes anyway."""
        holder = {"pid": os.getpid() + 1, "timestamp": 1000.0, "selection": "theirs"}
        self._seed(holder)
        for _ in range(20):
            self.assertIsNone(run.acquire_run_lock(
                self.umbrella, _LockRuntime(now=1000.0, alive=True), self.logger))
            self.assertEqual(self._read(), holder)

    def test_a_racer_never_deletes_the_winners_fresh_lock(self):
        """THE reclaim race (found in review, reproduced with a PoC before the
        fix). Two processes both read the same STALE lock and both decide to
        reclaim. The first removes it and creates its own. Under a bare
        `os.remove` the second then deleted the WINNER's fresh lock and created
        its own -- both believed they held it, and both would launch KSP.

        The fix quarantines by atomic rename and then VERIFIES the moved bytes
        are the stale lock it judged. Here the file mutates to a fresh live
        holder between our read and our reclaim, exactly as the loser sees it."""
        stale = {"pid": os.getpid() + 1, "timestamp": 0.0, "selection": "stale"}
        self._seed(stale)
        winner = {"pid": os.getpid() + 2, "timestamp": 5000.0, "selection": "winner"}
        swapped = []

        class Loser(_LockRuntime):
            def pid_alive(inner, pid):
                # We judged the stale lock dead; the winner reclaims and creates
                # its own lock in the window before our reclaim lands.
                if not swapped:
                    swapped.append(True)
                    self._seed(winner)
                    return False
                return True

        self.assertIsNone(run.acquire_run_lock(
            self.umbrella, Loser(now=5000.0), self.logger))
        self.assertEqual(self._read(), winner,
                         "the winner's live lock must be restored untouched")

    @unittest.skipUnless(os.name == "nt",
                         "Windows-only by construction: the driver is an open handle blocking the rename, which POSIX does not do - see the todo entry TWO-MACHINE-LOCK-CELLS-ARE-UNPASSABLE-ON-THE-LINUX-CLOUD-PATH; the reclaim-refusal contract is verified on Windows runs only")
    def test_a_reclaim_that_cannot_complete_refuses_instead_of_looping(self):
        """The reclaim path is bounded to ONE retry and then refuses. Two racers
        that both loop on reclaim livelock instead of serializing.

        Driven by making the reclaim genuinely fail: an open handle blocks the
        rename on Windows, so acquire takes the reclaim branch, cannot move the
        stale lock aside, exhausts its bounded retries, and must then refuse
        rather than spin."""
        path = self._seed({"pid": os.getpid() + 1, "timestamp": 1000.0})
        blocker = open(path, "r", encoding="utf-8")
        self.addCleanup(blocker.close)
        rt = _LockRuntime(now=1000.0, alive=False)   # holder looks dead -> reclaim
        self.assertIsNone(run.acquire_run_lock(self.umbrella, rt, self.logger))
        self.assertTrue(os.path.isfile(path), "the undeletable lock must survive")

    # ---- release ---------------------------------------------------------

    def test_release_removes_our_own_lock(self):
        run.acquire_run_lock(self.umbrella, _LockRuntime(), self.logger)
        run.release_run_lock(self._lock(), self.logger)
        self.assertFalse(os.path.isfile(self._lock()))

    def test_release_refuses_to_delete_a_lock_that_is_no_longer_ours(self):
        """Leak L2. If we were reclaimed as stale mid-run, the lock now belongs to
        a LIVE sibling; deleting it would turn one refusal into two concurrent KSPs."""
        run.acquire_run_lock(self.umbrella, _LockRuntime(), self.logger)
        self._seed({"pid": os.getpid() + 1, "timestamp": 1000.0})   # sibling took over
        run.release_run_lock(self._lock(), self.logger)
        self.assertTrue(os.path.isfile(self._lock()))
        self.assertEqual(self._read()["pid"], os.getpid() + 1)

    def test_release_of_absent_lock_is_a_noop(self):
        run.release_run_lock(self._lock(), self.logger)      # never acquired
        run.release_run_lock(None, self.logger)

    # ---- heartbeat -------------------------------------------------------

    def test_heartbeat_refreshes_our_timestamp_so_a_long_run_never_expires(self):
        rt = _LockRuntime(now=1000.0)
        run.acquire_run_lock(self.umbrella, rt, self.logger, selection="tier=nightly")
        rt._now = 1000.0 + 9999
        self.assertTrue(
            run.heartbeat_run_lock(self._lock(), rt, self.logger, selection="tier=nightly"))
        self.assertEqual(self._read()["timestamp"], 1000.0 + 9999)
        self.assertEqual(self._read()["pid"], os.getpid())

    def test_heartbeat_preserves_when_the_hold_began(self):
        """The refusal message promises 'since=' is when the holder ACQUIRED. A
        heartbeat that restamped startedIso made it mean 'last refreshed', so a
        blocked operator could not tell a 10-second hold from an 8-hour one."""
        rt = _LockRuntime(now=1000.0)
        run.acquire_run_lock(self.umbrella, rt, self.logger, selection="tier=nightly")
        began = self._read()["startedIso"]
        rt._now = 1000.0 + 9999
        run.heartbeat_run_lock(self._lock(), rt, self.logger, selection="tier=nightly")
        self.assertEqual(self._read()["startedIso"], began)

    def test_heartbeat_reports_loss_so_the_caller_can_stop(self):
        """Losing the lock mid-selection must be FATAL to the caller: a sibling
        reclaimed us and is flying the same instance."""
        self._seed({"pid": os.getpid() + 1, "timestamp": 1000.0})
        self.assertFalse(run.heartbeat_run_lock(self._lock(), _LockRuntime(), self.logger))
        self.assertEqual(self._read()["pid"], os.getpid() + 1,
                         "must not resurrect a lock we lost")

    def test_heartbeat_on_absent_lock_is_a_noop(self):
        self.assertTrue(run.heartbeat_run_lock(None, _LockRuntime(), self.logger))
        run.heartbeat_run_lock(self._lock(), _LockRuntime(), self.logger)
        self.assertFalse(os.path.isfile(self._lock()))


def _calls(fn):
    """The set of function names CALLED by fn, via AST rather than substring
    search: matching raw source text also matches the rationale comments that
    name these very functions."""
    tree = ast.parse(textwrap.dedent(inspect.getsource(fn)))
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            f = node.func
            if isinstance(f, ast.Name):
                names.add(f.id)
            elif isinstance(f, ast.Attribute):
                names.add(f.attr)
    return names


class MachineLockScopeTests(unittest.TestCase):
    """The lock is taken ONCE per invocation, around the whole selection. Taking
    it per ATTEMPT left it free in the gaps between a selection's scenarios,
    which is how a sibling could shred a nightly scenario by scenario."""

    def test_run_attempt_no_longer_acquires_or_releases(self):
        called = _calls(run.run_attempt)
        self.assertNotIn("acquire_run_lock", called)
        self.assertNotIn("release_run_lock", called)

    def test_selection_loop_heartbeats_each_scenario_boundary(self):
        self.assertIn("heartbeat_run_lock", _calls(run._run_selection))

    def test_run_acquires_once_and_releases_it(self):
        called = _calls(run.run)
        self.assertIn("acquire_run_lock", called)
        self.assertIn("release_run_lock", called)

    def test_release_is_in_a_finally_so_a_failing_selection_frees_the_machine(self):
        tree = ast.parse(textwrap.dedent(inspect.getsource(run.run)))
        released_in_finally = any(
            "release_run_lock" in {
                n.func.id for n in ast.walk(handler)
                if isinstance(n, ast.Call) and isinstance(n.func, ast.Name)}
            for node in ast.walk(tree) if isinstance(node, ast.Try)
            for handler in [ast.Module(body=node.finalbody, type_ignores=[])])
        self.assertTrue(released_in_finally,
                        "release_run_lock must run in a finally, or a crashing "
                        "selection leaves the machine locked until the lease expires")

    def test_admit_runs_under_the_lock(self):
        """D8: the DLL-hash ADMIT check used to run BEFORE the lock was taken, so
        a concurrent provision could rewrite both the DLL and the manifest it is
        compared against in the window between the check and the launch. With the
        lock held by run() for the whole invocation, ADMIT is inside it."""
        called = _calls(run.run_attempt)
        self.assertIn("admit_instance", called)
        self.assertNotIn("acquire_run_lock", called)


class LeaseCoversWorstCaseScenarioTests(unittest.TestCase):
    """The lease must exceed ONE scenario's worst-case wall time, since the
    heartbeat refreshes at each scenario boundary. If a spec ever declares a
    budget that outgrows the lease, a sibling can LEGITIMATELY reclaim the lock
    mid-flight and put two KSPs on one kRPC port -- so this reds here rather
    than in a night flight."""

    def test_lease_exceeds_the_worst_committed_spec_including_retries(self):
        import provlib
        specs = run.load_all_specs()
        # load_all_specs() returns [] when SCENARIOS_DIR is missing, which would
        # make this invariant pass having proven nothing.
        self.assertTrue(specs, "no committed specs loaded; the invariant is vacuous")
        worst_id, worst_wall = None, 0.0
        for spec in specs:
            budget = float((spec.get("runtime", {}) or {}).get("budgetSeconds", 600))
            policy = (spec.get("retry", {}) or {}).get("policy", "once")
            attempts = 2 if policy == "once" else 1
            wall = budget * attempts
            if wall > worst_wall:
                worst_id, worst_wall = spec.get("id"), wall
        # Per-attempt overhead the budget does NOT cover: staging + boot +
        # verifier chain + collect-logs, generously bounded.
        overhead = 900.0 * 2
        needed = worst_wall + overhead
        self.assertGreater(
            provlib.DEFAULT_LEASE_SECONDS, needed,
            "lease %.0fs must exceed the worst scenario's %.0fs (%s: %.0fs budget"
            " x retries + %.0fs overhead). Raise DEFAULT_LEASE_SECONDS or lower"
            " the budget." % (provlib.DEFAULT_LEASE_SECONDS, needed, worst_id,
                              worst_wall, overhead))


class MachineLockLossStopsTheSelectionTests(unittest.TestCase):
    """Losing the lock mid-selection must STOP the run, not warn and continue."""

    def test_selection_loop_returns_on_a_failed_heartbeat(self):
        source = textwrap.dedent(inspect.getsource(run._run_selection))
        tree = ast.parse(source)
        # Find `if not heartbeat_run_lock(...)` and prove its body returns.
        guarded = []
        for node in ast.walk(tree):
            if not isinstance(node, ast.If) or not isinstance(node.test, ast.UnaryOp):
                continue
            called = {n.func.id for n in ast.walk(node.test)
                      if isinstance(n, ast.Call) and isinstance(n.func, ast.Name)}
            if "heartbeat_run_lock" in called:
                guarded.append(any(isinstance(n, ast.Return) for n in ast.walk(node)))
        self.assertTrue(guarded, "no `if not heartbeat_run_lock(...)` guard found")
        self.assertTrue(all(guarded),
                        "a failed heartbeat must return, never fall through and "
                        "keep flying without the lock")


class MachineLockSharedProtocolTests(unittest.TestCase):
    """Both shells must take the SAME acquire protocol. The first cut had two
    implementations and the provisioner's weaker one could clobber a live
    holder's lock; sharing the module is the structural fix."""

    def test_both_shells_call_the_shared_acquire(self):
        import provision
        self.assertIn("acquire", _calls(run.acquire_run_lock))
        self.assertIn("acquire", _calls(provision.phase_preflight))

    def test_neither_shell_reimplements_an_unguarded_replace_of_the_lock(self):
        import provision
        for fn in (run.acquire_run_lock, provision.phase_preflight):
            src = textwrap.dedent(inspect.getsource(fn))
            self.assertNotIn("os.replace(tmp, lock_path)", src)

    def test_the_shared_acquire_verifies_before_it_reclaims(self):
        import machinelock
        src = inspect.getsource(machinelock.acquire)
        # The reclaim must compare what it moved against what it judged.
        self.assertIn("moved != raw", src)
        self.assertNotIn("os.remove(path)", src)


class MachineLockPathAgreementTests(unittest.TestCase):
    """Both shells must resolve the SAME file or the exclusivity is fictional."""

    def test_run_py_resolves_the_shared_relpath(self):
        import provlib
        umbrella = os.path.join("C:", os.sep, "umb")
        self.assertEqual(run.run_lock_path(umbrella),
                         os.path.join(umbrella, *provlib.MACHINE_LOCK_RELPATH))

    def test_the_lock_is_not_keyed_on_an_instance_directory(self):
        # Two different profiles must map to ONE lock: the resources actually
        # monopolised (kRPC 50000/50001, the single GPU) are machine-global.
        umbrella = os.path.join("C:", os.sep, "umb")
        self.assertEqual(run.run_lock_path(umbrella), run.run_lock_path(umbrella))
        self.assertNotIn("stock-minimal", run.run_lock_path(umbrella))


def provlib_lease():
    import provlib
    return provlib.DEFAULT_LEASE_SECONDS
class RunIdCollisionSmokeTests(unittest.TestCase):
    """Two SEPARATE runs of one scenario started inside the SAME minute must both
    keep their evidence, driven end to end over the fake KSP.

    The run id stamps only the minute, and results/<runId>.json,
    <runId>_shots/ (the collected KSP.log) and <runId>_contact.html are keyed by
    it alone -- so the second run used to write straight over the first's, with
    nothing warning. Observed live on 2026-08-01: two H23-tracking-station
    flights 52 seconds apart left TWO per-invocation harness logs but ONE result
    JSON and ONE _shots dir; the first flight's clean verdict was gone.

    The minute stamp is PINNED here rather than raced. The collision path is the
    subject; an unpinned pair of runs would exercise it only when they happened
    not to straddle a minute boundary.
    """

    STAMP = "2026-08-01_1628"
    SCENARIO = "H23-tracking-station"
    BASE = "2026-08-01_1628_H23-tracking-station"

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-runid-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        self.template = os.path.join(self.tmp, "fresh-career")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self._orig_results = run.RESULTS_DIR
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "runid_harness.log"))
        # Both runs land in ONE minute, deterministically.
        self._orig_stamp = run._run_id_stamp
        run._run_id_stamp = lambda: self.STAMP

    def tearDown(self):
        run._run_id_stamp = self._orig_stamp
        run.RESULTS_DIR = self._orig_results
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _spec(self):
        spec = _make_spec(self.template, 30, 600)
        spec["id"] = self.SCENARIO
        return spec

    def _fly(self, mode="pass"):
        return run.run_attempt(self._spec(), self.instance, self.tmp,
                               FakeRuntime(mode), attempt=1,
                               prior_boot_crashed=False, logger=self.logger)

    def _results_path(self, *parts):
        return os.path.join(run.RESULTS_DIR, *parts)

    def test_two_runs_in_one_minute_both_keep_their_results(self):
        first = self._fly()
        self.assertEqual(self.BASE, first["runId"],
                         "the FIRST run must keep the plain, unsuffixed id")
        first_shots = self._results_path(self.BASE + "_shots")
        self.assertTrue(os.path.isfile(self._results_path(self.BASE + ".json")))
        self.assertTrue(os.path.isfile(os.path.join(first_shots, "KSP.log")),
                        "the collected KSP.log is the evidence at stake")
        # A sentinel INSIDE the first run's artifact dir: if the second run reuses
        # the dir this is what silently disappears.
        sentinel = os.path.join(first_shots, "first-run-evidence.txt")
        with open(sentinel, "w", encoding="utf-8") as fh:
            fh.write("19:28:02\n")

        second = self._fly()
        self.assertEqual(self.BASE + "_run2", second["runId"],
                         "the second run must take a fresh id, not the first's")

        # Both runs' evidence survives, in full.
        self.assertTrue(os.path.isfile(sentinel),
                        "the first run's _shots dir was clobbered by the second")
        self.assertTrue(os.path.isdir(self._results_path(self.BASE + "_run2_shots")))
        for run_id in (first["runId"], second["runId"]):
            with open(self._results_path("%s.json" % run_id), "r", encoding="utf-8") as fh:
                persisted = json.load(fh)
            self.assertEqual(run_id, persisted["runId"])
            self.assertEqual(hlib.VERDICT_PASS, persisted["verdict"])
            self.assertTrue(os.path.isfile(self._results_path("%s_contact.html" % run_id)),
                            "%s lost its contact sheet" % run_id)

    def test_two_resolutions_before_either_writes_do_not_share_an_id(self):
        """The TOCTOU half. An id is resolved at the TOP of run_attempt, long
        before anything is written under it, so two concurrent run.py invocations
        of one scenario both see an empty results/ and would both take the base
        id. The per-instance run lock does not serialize them: the refused one
        writes its INVALID(instance-locked) record straight away, while the
        winner is still flying and has written nothing.

        Scan-only resolution cannot see that - only the exclusive-create stake
        can. Two resolutions with no run in between is exactly that shape."""
        first = run._make_run_id(self.SCENARIO, 1, logger=self.logger)
        second = run._make_run_id(self.SCENARIO, 1, logger=self.logger)
        self.assertEqual(self.BASE, first)
        self.assertEqual(self.BASE + "_run2", second,
                         "an unstaked id lets a concurrent invocation reuse it")
        for run_id in (first, second):
            self.assertTrue(os.path.isfile(self._results_path(run_id + ".claim")),
                            "%s was issued without being staked" % run_id)

    def test_a_stake_that_cannot_be_written_never_blocks_the_run(self):
        """Design edge 10: never lose a verdict. A results dir that refuses the
        stake degrades to scan-only resolution -- still better than the unguarded
        id -- rather than failing the run."""
        orig = run.RESULTS_DIR
        try:
            # An existing FILE where the results dir should be stands in for any
            # unwritable results dir. This shape is also the one that caught the
            # first cut: makedirs over a file raises FileExistsError, which a
            # single combined handler read as "already staked" -- and the caller
            # re-resolved forever, hanging the whole suite.
            blocker = os.path.join(self.tmp, "not-a-dir")
            with open(blocker, "w", encoding="utf-8") as fh:
                fh.write("x")
            run.RESULTS_DIR = blocker
            self.assertTrue(run._try_claim_run_id(self.BASE),
                            "an unstakeable dir must degrade open, not report "
                            "the id taken (that spins the re-resolve loop)")
            self.assertEqual(self.BASE, run._make_run_id(self.SCENARIO, 1,
                                                         logger=self.logger))
        finally:
            run.RESULTS_DIR = orig

    def test_the_collision_is_announced_not_swallowed(self):
        """A silently-renamed run is only half the fix: nothing in results/ told a
        reader two runs had happened, so the Warn is the other half."""
        self._fly()
        self._fly()
        with open(self.logger.log_path, "r", encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("[Harness][Warn][Result] runId collision", body)
        self.assertIn(self.BASE + "_run2", body)

    def test_the_run_index_lists_both_runs(self):
        """The reader-facing half: results/index.html is where an operator sees
        what ran. Two flights used to produce ONE row."""
        self._fly()
        self._fly()
        with open(self._results_path("index.html"), "r", encoding="utf-8") as fh:
            index = fh.read()
        for run_id in (self.BASE, self.BASE + "_run2"):
            self.assertIn('"%s_contact.html"' % run_id, index,
                          "%s is missing from the run index" % run_id)

    def test_a_retried_second_run_keeps_its_attempts_on_one_stem(self):
        """The `[retry] policy = "once"` path through the production entry point.
        Attempt 2 belongs to ONE run and is distinguished by `_a2`; the SECOND
        run's two attempts must both carry its `_run2` stem rather than one of
        them drifting back onto a first-run id."""
        first = run._run_scenario_with_retry(self._spec(), self.instance, self.tmp,
                                             FakeRuntime("bootcrash"), self.logger)
        second = run._run_scenario_with_retry(self._spec(), self.instance, self.tmp,
                                              FakeRuntime("bootcrash"), self.logger)
        # Both runs retried (boot-crash is retryable), so four attempt records exist.
        self.assertEqual(self.BASE + "_a2", first["runId"])
        self.assertEqual(self.BASE + "_run2_a2", second["runId"])
        for run_id in (self.BASE, self.BASE + "_a2",
                       self.BASE + "_run2", self.BASE + "_run2_a2"):
            self.assertTrue(os.path.isfile(self._results_path("%s.json" % run_id)),
                            "attempt record %s.json is missing" % run_id)
        # ... and the attempt suffix stays terminal, so status.py still reads it.
        self.assertEqual(2, status.split_run_id(second["runId"])["attempt"])
        self.assertEqual(2, status.split_run_id(second["runId"])["run"])
        self.assertEqual(self.SCENARIO, status.split_run_id(second["runId"])["scenario"])

    def test_only_the_second_run_retrying_still_keeps_it_off_run_1s_stem(self):
        """The shape that DISCRIMINATES the ordinal threading, and the reason the
        cell above is not enough on its own: when both runs retry, run 1 leaves
        `..._a2` on disk and the plain results/ scan produces the same four ids
        whether or not `_run_scenario_with_retry` threads its ordinal.

        Here run 1 flies clean and never retries, so `..._a2` is FREE. Without
        the threading, run 2's attempt 2 resolves straight back onto run 1's stem
        and its record is filed - and rendered in the status panel and the run
        index - as run 1's attempt 2. Nothing is overwritten (that id really was
        free), so only an attribution assertion catches it."""
        first = run._run_scenario_with_retry(self._spec(), self.instance, self.tmp,
                                             FakeRuntime("pass"), self.logger)
        self.assertEqual(self.BASE, first["runId"], "run 1 must not have retried")

        second = run._run_scenario_with_retry(self._spec(), self.instance, self.tmp,
                                              FakeRuntime("bootcrash"), self.logger)
        self.assertEqual(self.BASE + "_run2_a2", second["runId"])
        parts = status.split_run_id(second["runId"])
        self.assertEqual(2, parts["run"], "run 2's retry must read as run 2")
        self.assertEqual(2, parts["attempt"])
        # The negative is the load-bearing half: these are the paths run 2's
        # attempt 2 lands on when the ordinal is NOT carried forward.
        for stray in (self.BASE + "_a2.json", self.BASE + "_a2_shots",
                      self.BASE + "_a2.claim"):
            self.assertFalse(os.path.exists(self._results_path(stray)),
                             "run 2's retry was filed on run 1's stem as %s" % stray)


if __name__ == "__main__":
    unittest.main()


class SharedShipOverlayStagingTests(unittest.TestCase):
    """The shell half of the fixture dedup: stage_fixture must lay the shared craft
    into the staged save, and must fail CLOSED, pre-boot, when it cannot.

    A save fixture no longer commits its own copy of a craft that several fixtures
    share; the copy arrives here instead. If it silently did not, the save would
    boot and the mission's `launch_vessel` would fail to resolve
    `<save>/Ships/VAB/<name>.craft` minutes later - classified driver-INVALID
    against a perfectly good spec, which is the exact misdirection the injection
    postcondition above exists to avoid."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-harness-ships-")
        self.instance = os.path.join(self.tmp, "instance")
        os.makedirs(self.instance, exist_ok=True)
        _write_manifest(self.instance, "stock-minimal")
        # A template named for a save the real manifest does NOT list, so the test
        # controls the whole overlay decision through its own manifest below.
        self.template = os.path.join(self.tmp, "ships-fixture")
        os.makedirs(self.template, exist_ok=True)
        with open(os.path.join(self.template, "persistent.sfs"), "w") as fh:
            fh.write("GAME { }\n")
        self.ships = os.path.join(self.tmp, "ships")
        os.makedirs(self.ships, exist_ok=True)
        self._write_ship("Kerbal X", "ship = KerbalX\n")
        self.manifest_path = os.path.join(self.tmp, "shared-ships.toml")
        self._write_shared_manifest('"ships-fixture" = ["Kerbal X"]')
        self._orig = (run.SHIPS_DIR, run.SHARED_SHIPS_PATH, run.RESULTS_DIR)
        run.SHIPS_DIR = self.ships
        run.SHARED_SHIPS_PATH = self.manifest_path
        run.RESULTS_DIR = os.path.join(self.tmp, "results")
        self.logger = run.HarnessLogger(os.path.join(run.RESULTS_DIR, "ships_harness.log"))

    def tearDown(self):
        run.SHIPS_DIR, run.SHARED_SHIPS_PATH, run.RESULTS_DIR = self._orig
        self.logger.close()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _write_ship(self, name, body):
        with open(os.path.join(self.ships, name + ".craft"), "w", encoding="utf-8") as fh:
            fh.write(body)

    def _write_shared_manifest(self, rows):
        with open(self.manifest_path, "w", encoding="utf-8") as fh:
            fh.write("[ships]\n%s\n" % rows)

    def _stage(self):
        return run.stage_fixture(_make_spec(self.template, 30, 600), self.instance,
                                 FakeRuntime("pass"), self.logger)

    def test_a_declared_shared_craft_lands_in_the_staged_saves_vab(self):
        ok, name, subkind = self._stage()
        self.assertTrue(ok)
        self.assertEqual("", subkind)
        staged = os.path.join(self.instance, "saves", name, "Ships", "VAB", "Kerbal X.craft")
        self.assertTrue(os.path.isfile(staged), "the shared craft must be staged")
        with open(staged, encoding="utf-8") as fh:
            self.assertEqual("ship = KerbalX\n", fh.read())

    def test_the_vab_directory_is_created_when_the_template_has_none(self):
        # Post-dedup this is the COMMON shape: every fixture whose only craft were
        # shared now commits no Ships/ directory at all (git stores no empty dirs).
        self.assertFalse(os.path.isdir(os.path.join(self.template, "Ships")))
        ok, name, _ = self._stage()
        self.assertTrue(ok)
        self.assertTrue(os.path.isdir(os.path.join(self.instance, "saves", name, "Ships", "VAB")))

    def test_a_missing_library_craft_fails_closed_before_boot(self):
        self._write_shared_manifest('"ships-fixture" = ["Kerbal X", "Duna Rocket"]')
        ok, name, subkind = self._stage()
        self.assertFalse(ok)
        self.assertEqual("staging", subkind)
        with open(self.logger.log_path, encoding="utf-8") as fh:
            body = fh.read()
        self.assertIn("Duna Rocket", body)
        self.assertIn("not in the ship library", body)

    def test_a_craft_both_shared_and_committed_fails_closed(self):
        # The dedup-regressed shape. Overlaying silently would overwrite a committed
        # copy that may have diverged from the library - hiding the exact drift the
        # shared library exists to prevent.
        vab = os.path.join(self.template, "Ships", "VAB")
        os.makedirs(vab, exist_ok=True)
        with open(os.path.join(vab, "Kerbal X.craft"), "w", encoding="utf-8") as fh:
            fh.write("ship = DivergedCopy\n")
        ok, _name, subkind = self._stage()
        self.assertFalse(ok)
        self.assertEqual("staging", subkind)
        with open(self.logger.log_path, encoding="utf-8") as fh:
            self.assertIn("declared shared AND committed", fh.read())

    def test_an_unlisted_save_stages_verbatim_with_no_overlay(self):
        self._write_shared_manifest('"some-other-save" = ["Kerbal X"]')
        ok, name, subkind = self._stage()
        self.assertTrue(ok)
        self.assertEqual("", subkind)
        self.assertFalse(os.path.isdir(os.path.join(self.instance, "saves", name, "Ships")),
                         "an unlisted save must stage exactly as a verbatim copytree")

    def test_a_missing_manifest_fails_closed(self):
        # This used to assert the opposite - that a missing manifest "degrades to
        # the pre-library behavior". That reasoning died with the dedup: before it,
        # a verbatim copytree CARRIED the craft; now it carries nothing, so a
        # missing manifest silently stages twelve fixtures craftless and reports
        # success. It also left the two failure modes inconsistent - a missing
        # fixtures/ships/ failed closed pre-boot while a missing manifest booted.
        os.remove(self.manifest_path)
        ok, _name, subkind = self._stage()
        self.assertFalse(ok)
        self.assertEqual("staging", subkind)
        with open(self.logger.log_path, encoding="utf-8") as fh:
            self.assertIn("shared-ships manifest missing", fh.read())

    def test_an_unreadable_manifest_fails_closed_without_a_traceback(self):
        # _run_selection has no per-scenario except, so raising out of the parse
        # would take down the remaining scenarios of the whole selection.
        with open(self.manifest_path, "w", encoding="utf-8") as fh:
            fh.write("[ships\nthis is not toml = = =\n")
        ok, _name, subkind = self._stage()
        self.assertFalse(ok)
        self.assertEqual("staging", subkind)
        with open(self.logger.log_path, encoding="utf-8") as fh:
            self.assertIn("shared-ships manifest unreadable", fh.read())

    def test_the_overlay_precedes_injection_so_an_injected_save_sees_the_craft(self):
        # Asserting the craft exists AFTER staging cannot see the ordering - it is
        # true whichever side of the injector the overlay runs on (verified: moving
        # the overlay below the inject block leaves such a cell green). The only
        # way to test the claim is to have the injector LOOK, so this runtime
        # records what Ships/VAB held at the moment it was called.
        observed = {}

        class ObservingRuntime(FakeRuntime):
            def run_inject(self, instance_dir, save_name, timeout, preset="all-synthetic"):
                vab = os.path.join(instance_dir, "saves", save_name, "Ships", "VAB")
                observed["at_inject"] = sorted(os.listdir(vab)) if os.path.isdir(vab) else []
                return super().run_inject(instance_dir, save_name, timeout, preset=preset)

        spec = _make_spec(self.template, 30, 600)
        spec["fixture"]["injectedRecordings"] = "all-synthetic"
        ok, name, subkind = run.stage_fixture(spec, self.instance, ObservingRuntime("pass"),
                                              self.logger)
        self.assertTrue(ok, subkind)
        self.assertEqual(["Kerbal X.craft"], observed.get("at_inject"),
                         "the injector must see the overlaid craft; the overlay has "
                         "to run BEFORE injection so an injected fixture gets the "
                         "same Ships/VAB a verbatim template copy would have had")

    def test_a_save_with_no_manifest_row_is_logged_rather_than_silent(self):
        # The craftless case is legitimate but must leave a fingerprint: without one
        # the harness log cannot distinguish "overlay ran" from "no row", which is
        # exactly the state a dedup slip leaves behind.
        self._write_shared_manifest('"some-other-save" = ["Kerbal X"]')
        ok, _name, _subkind = self._stage()
        self.assertTrue(ok)
        with open(self.logger.log_path, encoding="utf-8") as fh:
            self.assertIn("shared-ship overlay: no rows for save=ships-fixture", fh.read())


# ---------------------------------------------------------------------------
# Cross-language filename pin.
# ---------------------------------------------------------------------------


class RenderManifestFilenamePinTests(unittest.TestCase):
    """The manifest filename is a CONTRACT spelled in three languages.

    The C# recorder WRITES it, run.py READS and ROTATES it, and collect-logs.py
    COPIES it. Nothing links the three: a rename on the C# side leaves the two
    Python readers looking for a file nobody writes, and the harness's only
    symptom is "manifest absent" - the same reading a genuinely unarmed recorder
    produces. So the drift is caught here instead, the
    `test_the_c_sharp_writer_still_emits_pointcount` precedent (hlib cells that
    read OUTSIDE harness/ to keep a cross-language pin honest).
    """

    def setUp(self):
        lib_dir = os.path.dirname(os.path.abspath(__file__))
        self.repo_root = os.path.dirname(os.path.dirname(lib_dir))

    def _read(self, *parts):
        path = os.path.join(self.repo_root, *parts)
        self.assertTrue(os.path.isfile(path), "missing %s" % path)
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read()

    def test_the_csharp_recorder_declares_the_filename_run_py_reads(self):
        source = self._read("Source", "Parsek", "MapRender",
                            "RenderCompositionRecorder.cs")
        declared = re.findall(
            r'ManifestFileName\s*=\s*"([^"]+)"\s*;', source)
        self.assertEqual(1, len(declared),
                         "expected exactly one ManifestFileName declaration, "
                         "found %r" % (declared,))
        self.assertEqual(run.RENDER_MANIFEST_FILENAME, declared[0])

    def test_collect_logs_copies_the_same_filename(self):
        source = self._read("scripts", "collect-logs.py")
        self.assertIn('"%s"' % run.RENDER_MANIFEST_FILENAME, source,
                      "collect-logs.py no longer names the manifest run.py reads")
