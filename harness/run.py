"""M-A5 automated-testing harness orchestrator (the thin I/O shell).

This is the imperative half of module M-A5: run.py owns everything OUTSIDE KSP
(process launch, channel-file tail, process-tree kill, fixture copy, subprocess
the ps1/py verifiers, result + coverage writes) and delegates EVERY decision to
the pure ``hlib`` library (the M-A5 analogue of provlib.py). It never links kRPC,
never parses a .sfs for a verdict, never reaches into KSP memory: its only
channels into a running game are the launch env vars, the M-A2 command/response
files, and the files KSP leaves on disk (KSP.log, the save, the results file, the
analyzer report).

Design authority: docs/dev/design-autotest-harness-core.md (Module M-A5). The
KSP-process functions and the external verifier subprocesses sit behind a small
injectable ``Runtime`` seam so a fake-KSP stub (test_run_smoke.py) can drive the
whole loop with no real game.

Two invariants shape the shell (design Mental Model):
  - The seam + hooks own everything inside KSP; run.py owns everything outside.
  - A run never hangs and never lies: every wait has a budget, expiry kills the
    process tree -> KILLED, and every verdict is derived from an explicit signal
    the verifier chain read and logged.

ASCII only; stdlib only (plus hlib + provlib, the pure siblings).
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import tomllib
from datetime import datetime, timezone
from typing import Dict, List, Optional, Sequence, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
LIB_DIR = os.path.join(HERE, "lib")
PROVISION_DIR = os.path.join(HERE, "provision")
for _p in (LIB_DIR, PROVISION_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import oracle  # noqa: E402
import provlib  # noqa: E402

# ---------------------------------------------------------------------------
# Path layout (mirrors provision.py so the harness and the provisioner agree on
# the umbrella / instance geometry).
# ---------------------------------------------------------------------------

HARNESS_ROOT = HERE
WORKTREE_ROOT = os.path.abspath(os.path.join(HARNESS_ROOT, ".."))
DEFAULT_UMBRELLA_ROOT = os.path.abspath(os.path.join(WORKTREE_ROOT, ".."))
SCRIPTS_DIR = os.path.join(WORKTREE_ROOT, "scripts")
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")
REGISTRY_PATH = os.path.join(HARNESS_ROOT, "coverage", "registry.toml")
RESULTS_DIR = os.path.join(HARNESS_ROOT, "results")
COVERAGE_DIR = os.path.join(HARNESS_ROOT, "coverage")
FIXTURES_DIR = os.path.join(HARNESS_ROOT, "fixtures")
PROFILES_DIR = os.path.join(PROVISION_DIR, "profiles")

# M-B1 mission library (design "Mission Library"). The autopilot missions, the
# pure mlib decision core, the pinned requirements, and the vendored venv all live
# under harness/missions/. run.py NEVER imports anything from the venv: it resolves
# schemas (shell-side spec admission), admits the venv stamp (pre-launch ADMIT),
# and spawns the mission as an isolated SUBPROCESS with the venv python.
MISSIONS_DIR = os.path.join(HARNESS_ROOT, "missions")
VENV_DIR = os.path.join(MISSIONS_DIR, ".venv")
# venv python per the design mental model: .venv/Scripts/python on win32,
# .venv/bin/python elsewhere.
VENV_PYTHON = (os.path.join(VENV_DIR, "Scripts", "python.exe")
               if sys.platform == "win32" else os.path.join(VENV_DIR, "bin", "python"))
VENV_STAMP_PATH = os.path.join(VENV_DIR, ".venv-stamp.json")
REQUIREMENTS_PATH = os.path.join(MISSIONS_DIR, "requirements.txt")

# The mission-result JSON schema run.py accepts (design Data Model "Mission
# result": schema = 1). run.py must NOT import mlib (it stays stdlib + hlib/provlib
# only, never links the mission package), so this is an INLINE mirror of
# mlib.MISSION_RESULT_SCHEMA; a result carrying a different schema is treated as
# unreadable (fail-closed), never mis-parsed.
MISSION_RESULT_SCHEMA = 1

# Default kRPC endpoint (design "Connection lifecycle" item 1): the stamped kRPC
# settings bind 127.0.0.1:50000 (RPC) / 50001 (stream). v1 uses these defaults;
# a future multi-instance layout overrides them per instance (deferred).
DEFAULT_RPC_HOST = "127.0.0.1"
DEFAULT_RPC_PORT = 50000
DEFAULT_STREAM_PORT = 50001

# Per-verifier wall-clock timeouts (design S14): distinct from the KSP run budget
# (already spent by the time the chain starts). A verifier subprocess that exceeds
# its timeout is killed and its result is INVALID(tooling), never a silent PASS.
ANALYZER_TIMEOUT_SECONDS = 900
LOGVALIDATE_TIMEOUT_SECONDS = 600
COLLECT_LOGS_TIMEOUT_SECONDS = 600
INJECT_TIMEOUT_SECONDS = 600

# Per-step wait default when a step names no budget (a non-deferred verb resolves
# fast; the run budget is the real ceiling).
DEFAULT_STEP_BUDGET_SECONDS = 60
DEFAULT_BOOT_BUDGET_SECONDS = 300

POLL_INTERVAL_SECONDS = 0.25


# ---------------------------------------------------------------------------
# Value encoding (matches the M-A2 percent codec, TestCommandProtocol.Encode):
# a value with whitespace / '=' / '%' / a control or non-ASCII byte is
# percent-encoded so the addon's parser round-trips it exactly.
# ---------------------------------------------------------------------------


def encode_value(value: str) -> str:
    out = []
    for b in str(value).encode("utf-8"):
        if b <= 0x20 or b == 0x25 or b == 0x3D or b >= 0x7F:
            out.append("%%%02X" % b)
        else:
            out.append(chr(b))
    return "".join(out)


def format_command_line(step_id: str, verb: str, args: Dict) -> str:
    parts = ["id=%s" % step_id, "cmd=%s" % verb]
    for k, v in (args or {}).items():
        parts.append("%s=%s" % (k, encode_value(v)))
    return " ".join(parts)


def utcnow_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def default_harness_log_path() -> str:
    """Per-INVOCATION harness log path ``harness/results/<ts>_harness.log`` (S6).
    One run.py invocation runs a whole selection, so the log is keyed by the launch
    timestamp, not a per-scenario runId; every stdout line is also appended here so
    a scheduled unattended run is reconstructable from the file alone."""
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d_%H%M%S")
    return os.path.join(RESULTS_DIR, "%s_harness.log" % ts)


# ---------------------------------------------------------------------------
# Harness logger: stdout + append-only per-run log (design Diagnostic Logging).
# ---------------------------------------------------------------------------


class HarnessLogger:
    def __init__(self, log_path: Optional[str] = None):
        self.log_path = log_path
        self._fh = None
        if log_path:
            os.makedirs(os.path.dirname(log_path), exist_ok=True)
            self._fh = open(log_path, "a", encoding="utf-8")

    def log(self, level: str, step: str, message: str) -> None:
        line = hlib.format_log_line(level, step, message)
        print(line)
        if self._fh:
            self._fh.write(line + "\n")
            self._fh.flush()

    def info(self, step, msg):
        self.log("Info", step, msg)

    def warn(self, step, msg):
        self.log("Warn", step, msg)

    def verbose(self, step, msg):
        self.log("Verbose", step, msg)

    def error(self, step, msg):
        self.log("Error", step, msg)

    def close(self):
        if self._fh:
            self._fh.close()
            self._fh = None


# ---------------------------------------------------------------------------
# ToolResult: the outcome of one external verifier subprocess.
# ---------------------------------------------------------------------------


class ToolResult:
    def __init__(self, exit_code: int, timed_out: bool, stdout: str = "", stderr: str = ""):
        self.exit_code = exit_code
        self.timed_out = timed_out
        self.stdout = stdout
        self.stderr = stderr

    @property
    def ok(self) -> bool:
        return (not self.timed_out) and self.exit_code == 0


# ---------------------------------------------------------------------------
# Runtime seam: everything the shell does to the OS (launch KSP, poll/kill it,
# shell out to the ps1/py verifiers). The DEFAULT drives a real KSP + real
# scripts; the fake-KSP test injects a stub for all of it.
# ---------------------------------------------------------------------------


class Runtime:
    """Default runtime: real KSP process + real verifier subprocesses (Windows)."""

    def now(self) -> float:
        return time.time()

    def sleep(self, seconds: float) -> None:
        time.sleep(seconds)

    def pid_alive(self, pid: int) -> bool:
        try:
            out = subprocess.run(["tasklist", "/FI", "PID eq %d" % pid],
                                 capture_output=True, text=True)
            return str(pid) in (out.stdout or "")
        except OSError:
            return False

    def ksp_running(self, instance_dir: str) -> Optional[int]:
        """Return the pid of a KSP_x64.exe holding the instance (zombie preflight,
        reusing the provisioner's EC-1 approach), else None. The stock tasklist
        probe cannot bind a pid to a directory, so this is the coarse 'any KSP
        alive' signal the provisioner uses; the harness refuses on it."""
        try:
            out = subprocess.run(
                ["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe", "/FO", "CSV", "/NH"],
                capture_output=True, text=True)
            text = out.stdout or ""
            if "KSP_x64.exe" not in text:
                return None
            for row in text.splitlines():
                cells = [c.strip('"') for c in row.split('","')]
                if cells and cells[0].startswith("KSP_x64"):
                    try:
                        return int(cells[1])
                    except (IndexError, ValueError):
                        return -1
            return -1
        except OSError:
            return None

    def resolve_exe(self, instance_dir: str) -> str:
        return os.path.join(instance_dir, "KSP_x64.exe")

    def launch(self, exe: str, args: Sequence[str], env: Dict[str, str], cwd: str):
        return subprocess.Popen([exe] + list(args), env=env, cwd=cwd,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def poll_exit(self, proc) -> Optional[int]:
        return proc.poll()

    def kill_tree(self, proc) -> List[int]:
        pid = proc.pid
        try:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)],
                           capture_output=True, text=True)
        except OSError:
            try:
                proc.kill()
            except OSError:
                pass
        return [pid]

    # ---- verifier subprocesses -------------------------------------------

    def run_analyzer(self, save_dir: str, fresh_gate: bool, timeout: float) -> ToolResult:
        args = ["pwsh", "-NoProfile", "-File",
                os.path.join(SCRIPTS_DIR, "analyze-recordings.ps1"),
                "-SaveDir", save_dir, "-FailOnRed"]
        if fresh_gate:
            args.append("-FreshSaveGate")
        return self._run(args, timeout, cwd=WORKTREE_ROOT)

    def run_seed_analyzer(self, save_dir: str, out_dir: str, timeout: float) -> ToolResult:
        """Pre-launch seed baseline (M-B2, design Terminology ~92): run the OFFLINE
        analyzer over the STAGED template save and REDIRECT its report OUT of the
        save tree (``-ResultsDir out_dir``, NEVER inside the save KSP will boot) so
        no analyzer artifact rides into the launched save. No ``-FailOnRed`` /
        ``-FreshSaveGate``: this run exists ONLY to parse the template's ``careerSave``
        block for the seed (ONE parser produces both the seed and the produced-save
        totals, so a parser drift can never desync the legs)."""
        args = ["pwsh", "-NoProfile", "-File",
                os.path.join(SCRIPTS_DIR, "analyze-recordings.ps1"),
                "-SaveDir", save_dir, "-ResultsDir", out_dir]
        return self._run(args, timeout, cwd=WORKTREE_ROOT)

    def run_log_validate(self, log_path: str, killed: bool, no_recording: bool,
                         timeout: float) -> ToolResult:
        args = ["pwsh", "-NoProfile", "-File",
                os.path.join(SCRIPTS_DIR, "validate-ksp-log.ps1"),
                "-LogPath", log_path]
        if killed:
            args.append("-KilledRun")
        if no_recording:
            args.append("-NoRecordingRun")
        return self._run(args, timeout, cwd=WORKTREE_ROOT)

    def run_collect_logs(self, label: str, save_name: str, instance_dir: str,
                         timeout: float) -> ToolResult:
        args = [sys.executable, os.path.join(SCRIPTS_DIR, "collect-logs.py"),
                label, "--save", save_name, "--ksp-dir", instance_dir]
        return self._run(args, timeout, cwd=WORKTREE_ROOT)

    def run_inject(self, instance_dir: str, save_name: str, timeout: float,
                   preset: str = "all-synthetic") -> ToolResult:
        env = dict(os.environ)
        env["KSPDIR"] = instance_dir
        args = ["pwsh", "-NoProfile", "-File",
                os.path.join(SCRIPTS_DIR, "inject-recordings.ps1"),
                "-SaveName", save_name, "-Preset", preset]
        return self._run(args, timeout, cwd=WORKTREE_ROOT, env=env)

    # ---- mission subprocess + venv I/O (M-B1) ----------------------------

    def read_venv_stamp(self, stamp_path: str) -> Optional[Dict]:
        """Read the mission venv stamp JSON (design "Dependency manifest"). Returns
        None when the stamp is absent (never bootstrapped) or unparseable -- both
        read as a refusal by hlib.venv_admission (fail-closed)."""
        if not os.path.isfile(stamp_path):
            return None
        try:
            with open(stamp_path, "r", encoding="utf-8") as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return None

    def read_requirements_text(self, requirements_path: str) -> str:
        """Read the committed requirements.txt verbatim (parsed shell-side by
        _parse_requirements). Missing file reads as empty (no pins to enforce)."""
        try:
            with open(requirements_path, "r", encoding="utf-8", errors="replace") as fh:
                return fh.read()
        except OSError:
            return ""

    def spawn_mission(self, venv_python: str, mission_py: str, args: Sequence[str],
                      cwd: str, stdout_path: str):
        """Spawn the mission SUBPROCESS with the venv python (design handoff step 3).
        stdout+stderr are captured to stdout_path so run.py can fold the mission's
        [Mission] lines into the per-invocation harness log after it exits.
        -u (unbuffered): without it python fully buffers stdout to the file, so a
        budget-expired kill loses EVERY line the mission printed and the hang site
        is undiagnosable (first live B1 run, 2026-07-19)."""
        out = open(stdout_path, "w", encoding="utf-8")
        try:
            return subprocess.Popen([venv_python, "-u", mission_py] + list(args), cwd=cwd,
                                    stdout=out, stderr=subprocess.STDOUT)
        finally:
            out.close()

    def _run(self, args, timeout, cwd, env=None) -> ToolResult:
        try:
            out = subprocess.run(args, capture_output=True, text=True,
                                 timeout=timeout, cwd=cwd, env=env)
            return ToolResult(out.returncode, False, out.stdout or "", out.stderr or "")
        except subprocess.TimeoutExpired as exc:
            return ToolResult(-1, True, exc.stdout or "", exc.stderr or "")
        except OSError as exc:
            return ToolResult(-1, False, "", str(exc))


# ---------------------------------------------------------------------------
# Spec + registry loading.
# ---------------------------------------------------------------------------


def load_toml(path: str) -> Dict:
    with open(path, "rb") as fh:
        return tomllib.load(fh)


def load_registry() -> Dict:
    return load_toml(REGISTRY_PATH)


def load_all_specs() -> List[Dict]:
    specs = []
    if not os.path.isdir(SCENARIOS_DIR):
        return specs
    for name in sorted(os.listdir(SCENARIOS_DIR)):
        if not name.endswith(".toml"):
            continue
        spec = load_toml(os.path.join(SCENARIOS_DIR, name))
        spec["_path"] = os.path.join(SCENARIOS_DIR, name)
        specs.append(spec)
    return specs


def resolve_instance_dir(profile_name: str, umbrella_root: str,
                         override: Optional[str]) -> Optional[str]:
    if override:
        return os.path.abspath(override)
    profile_path = os.path.join(PROFILES_DIR, "%s.toml" % profile_name)
    if not os.path.isfile(profile_path):
        return None
    profile = load_toml(profile_path)
    rel = profile.get("instanceDir")
    if not rel:
        return None
    return os.path.abspath(os.path.join(umbrella_root, rel))


# ---------------------------------------------------------------------------
# M-B1 mission spec admission (design "Spec admission" / "Spec-validation rules
# for kind = autopilot"). SHELL-SIDE resolution: read the mission's declared
# param schema from harness/missions/<mission>.schema.toml and confirm the
# mission .py resolves on disk, then hand the parsed registry to the PURE
# hlib.validate_spec for the param / handoff checks.
# ---------------------------------------------------------------------------


def _canonical_dist(name: str) -> str:
    """Canonical distribution key (lowercase, ``_``/``.`` -> ``-``), MIRRORING
    bootstrap_venv._canonical_dist so both sides of the venv-admission comparison
    agree (NIT 10). The bootstrap canonicalizes when it parses requirements + writes
    the stamp pins; run.py must canonicalize identically so a non-canonical committed
    pin (e.g. ``KRPC==0.5.4`` / ``proto_buf==...``) matches the canonical stamp key
    instead of drifting to a false tooling-venv refusal."""
    return name.strip().lower().replace("_", "-").replace(".", "-")


def _parse_requirements(text: str) -> Dict[str, str]:
    """Parse a committed requirements.txt body into {canonical distribution: pinned
    version} (design "Dependency manifest"; the venv_admission docstring assigns this
    parse to the caller). Only exact ``name==version`` pins are enforced; comment
    lines, blank lines, and the PROVISIONAL (commented) protobuf line are skipped.
    The distribution name is CANONICALIZED (NIT 10) so it matches the venv stamp's
    canonical pin keys regardless of the requirement's spelling / separators."""
    reqs: Dict[str, str] = {}
    for raw in (text or "").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        # strip a trailing inline comment (" # ...") before the pin check.
        hashpos = line.find(" #")
        if hashpos >= 0:
            line = line[:hashpos].strip()
        if "==" not in line:
            continue
        name, _, ver = line.partition("==")
        name = name.strip()
        ver = ver.strip()
        if name and ver:
            reqs[_canonical_dist(name)] = ver
    return reqs


def resolve_mission_schemas(spec: Dict, logger: Optional[HarnessLogger] = None
                            ) -> Tuple[Optional[Dict], List[str]]:
    """Resolve an autopilot spec's mission reference SHELL-SIDE (design "Spec
    admission"). Returns (mission_schemas_registry, shell_errors):

    - Non-autopilot spec: (None, []) -- the pure seam rules apply unchanged.
    - Autopilot spec: reads harness/missions/<mission>.schema.toml (when present)
      into the registry the pure validator consumes, AND checks the mission .py
      resolves on disk. A missing schema file makes the mission ABSENT from the
      registry (the pure validator then rejects it as an unknown mission); a
      missing mission .py adds a shell error here. Either is a spec-invalid INVALID
      per the design (no KSP boot for a mission that cannot run). A non-filename-safe
      mission ref is left to the pure validator (no disk probe on an unsafe name)."""
    driver = spec.get("driver", {}) or {}
    if driver.get("kind") != "autopilot":
        return None, []
    mission = driver.get("mission")
    registry: Dict = {}
    errors: List[str] = []
    if not isinstance(mission, str) or not mission or not hlib._MISSION_RE.match(mission):
        # The pure validator emits the filename-safety error; do not probe disk with
        # an unsafe name (a traversal token must never reach os.path.join).
        return registry, errors
    schema_path = os.path.join(MISSIONS_DIR, "%s.schema.toml" % mission)
    if os.path.isfile(schema_path):
        try:
            registry[mission] = load_toml(schema_path)
        except (OSError, ValueError, tomllib.TOMLDecodeError) as exc:
            errors.append("driver.mission: schema %s failed to parse: %s" % (schema_path, exc))
    # else: absent schema -> mission not in registry -> pure validator rejects it as
    # an unknown mission (design: unknown mission -> reject), so no shell error here.
    py_path = os.path.join(MISSIONS_DIR, "%s.py" % mission)
    if not os.path.isfile(py_path):
        errors.append("driver.mission: no mission script at %s" % py_path)
    if errors and logger is not None:
        for e in errors:
            logger.warn("Admit", "mission-ref: %s" % e)
    return registry, errors


class MissionContext:
    """Per-attempt autopilot handoff context (built once at pre-launch ADMIT after
    the venv is admitted). Carries the resolved venv python + mission script, the
    JSON-serializable missionParams, the instance cwd, and the venv stamp path +
    parsed requirements for the in-flight backstop re-check."""

    def __init__(self, mission_name: str, venv_python: str, mission_py: str,
                 mission_params: Dict, cwd: str, stamp_path: str, requirements: Dict):
        self.mission_name = mission_name
        self.venv_python = venv_python
        self.mission_py = mission_py
        self.mission_params = mission_params
        self.cwd = cwd
        self.stamp_path = stamp_path
        self.requirements = requirements


# ---------------------------------------------------------------------------
# Admission (design Instance admission / edge 6). Reads the on-disk manifest and
# refuses on a missing manifest, a .provision-incomplete marker, or a nonempty
# diff. See the module NOTE on expected-manifest construction in v1.
# ---------------------------------------------------------------------------

PARSEK_DLL_REL = os.path.join("GameData", "Parsek", "Plugins", "Parsek.dll")


def _sha256_file(path: str) -> Optional[str]:
    import hashlib
    try:
        h = hashlib.sha256()
        with open(path, "rb") as fh:
            for chunk in iter(lambda: fh.read(65536), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def read_manifest(instance_dir: str) -> Tuple[Optional[Dict], bool]:
    """Return (manifest_or_None, incomplete_marker). A manifest that fails to
    parse reads as missing (never a silent admit)."""
    parsek_gd = os.path.join(instance_dir, "GameData", "Parsek")
    manifest_path = os.path.join(parsek_gd, "provision-manifest.json")
    incomplete = os.path.isfile(os.path.join(parsek_gd, ".provision-incomplete"))
    if not os.path.isfile(manifest_path):
        return None, incomplete
    try:
        with open(manifest_path, "r", encoding="utf-8") as fh:
            return json.load(fh), incomplete
    except (OSError, ValueError):
        return None, incomplete


def build_expected_from_manifest(manifest: Dict, instance_dir: str,
                                 logger: Optional[HarnessLogger] = None) -> Dict:
    """v1 expected-admission construction (design S11 ADAPTATION -- see NOTE).

    The provisioner's live manifest-stamping recipe (phase_deploy content hashes)
    is not yet implemented, so the harness cannot re-derive provision-time content
    hashes / resolved git commits from committed sources alone. v1 therefore
    projects the on-disk manifest as the expected baseline and substitutes the
    ONE substantive drift check the design calls out loudest: the DEPLOYED
    Parsek.dll sha vs the manifest's recorded parsek dll hash.

    NOTE (v1 adaptation, adjudication 1): this detects POST-PROVISION CLOBBER only
    -- the deployed DLL was CHANGED after the provisioner stamped the manifest
    (fresh sha != recorded sha -> drift). It does NOT detect a STALE DEPLOY (Parsek
    rebuilt in source but never redeployed, so the manifest and the deployed DLL
    still agree on the old hash); catching source-newer-than-deployed needs the
    provisioner's live content-hash recipe and is deferred. The remaining fields
    admit as-recorded until that live hashing lands.
    """
    import copy
    expected = copy.deepcopy({k: manifest.get(k) for k in provlib.ADMISSION_KEYS if k in manifest})
    parsek = ((expected.get("components") or {}).get("parsek")) or {}
    recorded_keys = [k for k in ("dllSha256", "sha256", "dllHash") if k in parsek]
    dll_path = os.path.join(instance_dir, PARSEK_DLL_REL)
    if recorded_keys and os.path.isfile(dll_path):
        fresh = _sha256_file(dll_path)
        if fresh is not None:
            for k in recorded_keys:
                parsek[k] = fresh
    elif logger is not None:
        # N2: no recorded parsek dll hash means the ONE substantive drift check is a
        # no-op and admission rubber-stamps on the remaining as-recorded fields.
        logger.warn("Admit", "admission: manifest parsek component carries no dll hash (%s); the DLL clobber check is a no-op, admitting on the remaining fields only (N2)"
                    % ("dll file missing" if recorded_keys else "no dllSha256/sha256/dllHash key"))
    return expected


# ---------------------------------------------------------------------------
# Run lock (design Run lock / edge 7). The harness's OWN run lock, distinct from
# the provisioner lock and the seam's in-KSP lock, acquired pre-stage.
# ---------------------------------------------------------------------------


def acquire_run_lock(instance_dir: str, runtime: Runtime, logger: HarnessLogger):
    parsek_gd = os.path.join(instance_dir, "GameData", "Parsek")
    lock_path = os.path.join(parsek_gd, ".harness-run.lock")
    existing = None
    if os.path.isfile(lock_path):
        try:
            with open(lock_path, "r", encoding="utf-8") as fh:
                existing = json.load(fh)
        except (OSError, ValueError):
            existing = None
    decision = provlib.acquire_lock(existing, os.getpid(), runtime.now(), runtime.pid_alive)
    if decision.reason == "refused-live":
        logger.warn("Lock", "run-lock refused: live holder pid=%s" % decision.holder_pid)
        return None
    if decision.reason == "reclaimed-stale":
        logger.warn("Lock", "run-lock reclaimed stale pid=%s" % decision.holder_pid)
    os.makedirs(parsek_gd, exist_ok=True)
    with open(lock_path, "w", encoding="utf-8") as fh:
        json.dump({"pid": os.getpid(), "timestamp": runtime.now()}, fh)
    logger.info("Lock", "run-lock acquired instance=%s pid=%d" % (instance_dir, os.getpid()))
    return lock_path


def release_run_lock(lock_path: Optional[str]) -> None:
    if lock_path and os.path.isfile(lock_path):
        try:
            os.remove(lock_path)
        except OSError:
            pass


# ---------------------------------------------------------------------------
# Fixture staging (design Fixture staging).
# ---------------------------------------------------------------------------

CHANNEL_FILES = (
    "parsek-test-commands.txt",
    "parsek-test-responses.txt",
    "parsek-test-commands.journal",
    "parsek-test-commands.lock",
)
RESULTS_FILE = "parsek-test-results.txt"


def _is_strictly_inside(child_path: str, parent_path: str) -> bool:
    """True iff realpath(child) is strictly BELOW realpath(parent) (never equal,
    never a sibling/escape). Case-normalized for Windows; a cross-drive pair (which
    makes os.path.commonpath raise) is an escape. The staging rmtree/copytree guard
    (S1) relies on this to refuse a target that resolves outside saves/."""
    parent_real = os.path.normcase(os.path.realpath(parent_path))
    child_real = os.path.normcase(os.path.realpath(child_path))
    if child_real == parent_real:
        return False
    try:
        return os.path.commonpath([parent_real, child_real]) == parent_real
    except ValueError:
        return False


def stage_fixture(spec: Dict, instance_dir: str, runtime: Runtime,
                  logger: HarnessLogger) -> Tuple[bool, str, str]:
    """Stage the scenario's fixture. Returns (ok, run_save_name, subkind); subkind
    is "" on success, "spec-invalid" on a containment violation (a runSaveName that
    escapes saves/), or "staging" on a missing template."""
    fixture = spec.get("fixture", {}) or {}
    save_template = fixture.get("saveTemplate", "")
    run_save_name = os.path.basename(save_template.replace("\\", "/").rstrip("/"))
    saves_dir = os.path.join(instance_dir, "saves")
    target_save = os.path.join(saves_dir, run_save_name)

    # (0) BELT-AND-BRACES containment assert (S1): hlib.validate_spec already
    # rejects a non-filename-safe runSaveName before launch, but before ANY
    # destructive rmtree/copytree confirm the resolved target is strictly inside
    # saves/. A target that escapes (traversal, symlink, cross-drive) aborts as
    # INVALID(spec-invalid) with NOTHING removed or copied.
    if not _is_strictly_inside(target_save, saves_dir):
        logger.error("Stage", "save containment violation: runSaveName=%r target=%s escapes saves=%s; aborting (INVALID spec-invalid)"
                     % (run_save_name, os.path.realpath(target_save), os.path.realpath(saves_dir)))
        return False, run_save_name, "spec-invalid"

    # (1) remove any prior staged save, (2) copy the template verbatim.
    template_abs = os.path.join(HARNESS_ROOT, save_template)
    if not os.path.isdir(template_abs):
        # Fixtures may not be committed (heavy); a missing template is a staging
        # failure, surfaced by the caller as INVALID(admission-adjacent staging).
        logger.error("Stage", "save template missing: %s" % template_abs)
        return False, run_save_name, "staging"
    if os.path.isdir(target_save):
        shutil.rmtree(target_save, ignore_errors=True)
    os.makedirs(saves_dir, exist_ok=True)
    shutil.copytree(template_abs, target_save)

    # (3) inject synthetic recordings when requested (recording OFF by construction).
    inj = fixture.get("injectedRecordings", "none")
    injected = False
    if inj in ("all-synthetic", "rewind-b9"):
        res = runtime.run_inject(instance_dir, run_save_name, INJECT_TIMEOUT_SECONDS, preset=inj)
        injected = res.ok
        if not injected:
            logger.warn("Stage", "inject-recordings failed preset=%s exit=%s (continuing; verifier will red)"
                        % (inj, res.exit_code))

    # (4) stage craft files.
    craft = fixture.get("craft", []) or []
    ships_dir = os.path.join(instance_dir, "Ships")
    for c in craft:
        src = os.path.join(HARNESS_ROOT, c)
        if os.path.isfile(src):
            os.makedirs(ships_dir, exist_ok=True)
            shutil.copy2(src, ships_dir)

    # (5) truncate the four channel files + rotate any stale results file so this
    # attempt reads only its own rows (M-A2 cross-run reuse: monotonic ids +
    # truncate; design staging step 5).
    for fname in CHANNEL_FILES:
        open(os.path.join(instance_dir, fname), "w", encoding="utf-8").close()
    results_rotated = False
    results_path = os.path.join(instance_dir, RESULTS_FILE)
    if os.path.isfile(results_path):
        try:
            os.remove(results_path)
            results_rotated = True
        except OSError:
            pass

    logger.info("Stage", "stage save=%s template=%s inject=%s craft=%d results-rotated=%s"
                % (run_save_name, save_template, inj, len(craft), results_rotated))
    return True, run_save_name, ""


# ---------------------------------------------------------------------------
# Seam driving (design Driving the seam).
# ---------------------------------------------------------------------------


def _read_response_lines(path: str) -> List[str]:
    if not os.path.isfile(path):
        return []
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return [l.rstrip("\n") for l in fh if l.strip()]
    except OSError:
        return []


def _response_has_terminal(lines: Sequence[str], step_id: str) -> Optional[str]:
    for line in lines:
        parsed = hlib._parse_response_line(line)
        if parsed is not None and parsed.get("id") == step_id:
            return parsed.get("verdict")
    return None


class DriveResult:
    def __init__(self):
        self.steps_with_ids: List[Dict] = []
        self.response_lines: List[str] = []
        self.killed = False
        self.kill_scope = ""
        self.boot_crashed = False
        self.batch_crashed = False
        self.pending_step_id: Optional[str] = None
        self.exit_code: Optional[int] = None
        self.killed_pids: List[int] = []
        # M-B1: the one mission-kind step's outcome row (design "Mission result"
        # driver.steps row), or None for a seam-only driver.
        self.mission_step: Optional[Dict] = None


def _read_mission_verdict(result_path: str) -> Optional[str]:
    """Read the mission-result JSON and return its ``verdict`` string, or None when
    the file is absent / unparseable / carries the WRONG schema / carries no string
    verdict (design edge 12: a missing or unreadable result fails closed via
    hlib.classify_mission_step(None)). The ``schema`` gate (design Backward
    Compatibility: "a schema bump makes the harness refuse an old artifact ...
    rather than mis-parse") makes run.py refuse a result whose top-level ``schema``
    is not the one it understands, so a future/legacy mission-result shape is
    treated as unreadable tooling-mission rather than silently mis-read."""
    if not os.path.isfile(result_path):
        return None
    try:
        with open(result_path, "r", encoding="utf-8", errors="replace") as fh:
            obj = json.load(fh)
    except (OSError, ValueError):
        return None
    if not isinstance(obj, dict):
        return None
    if obj.get("schema") != MISSION_RESULT_SCHEMA:
        return None
    v = obj.get("verdict")
    return v if isinstance(v, str) else None


def _forward_mission_stdout(stdout_path: str, logger: HarnessLogger) -> None:
    """Fold the mission subprocess's captured stdout ([Mission] lines) into the
    per-invocation harness log so a scheduled run is reconstructable from the log
    alone (design Diagnostic Logging)."""
    if not os.path.isfile(stdout_path):
        return
    try:
        with open(stdout_path, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.rstrip("\n")
                if line:
                    logger.verbose("Mission", "mission-stdout: %s" % line)
    except OSError:
        pass


def _log_handoff_return(logger: HarnessLogger, step_index: int,
                        verdict: Optional[str], met: bool, subkind: str) -> None:
    """HANDOFF-RETURN log line (design Diagnostic Logging)."""
    shown = verdict if verdict is not None else "<no-result>"
    if met:
        logger.info("Drive", "mission step=%d verdict=%s -> met" % (step_index, shown))
    else:
        logger.warn("Drive", "mission step failed: %s -> INVALID subkind=%s (retry per policy)"
                    % (shown, subkind))


def _preceding_loadgame_ok(steps: Sequence[Dict], mission_index: int,
                           responses_path: str) -> bool:
    """design "The handoff" step 1: True IFF the nearest preceding ``LoadGame`` step
    returned ``OK`` (evidence of a settled FLIGHT). A boot that did not settle must
    not hand off to the mission (a failed LoadGame would otherwise burn the whole
    600-780s mission budget flying against a game that never reached FLIGHT). If
    there is no preceding LoadGame at all the handoff is invalid (validate_spec
    requires one), so this returns False."""
    load_index = None
    for j in range(mission_index - 1, -1, -1):
        if (steps[j] or {}).get("cmd") == "LoadGame":
            load_index = j
            break
    if load_index is None:
        return False
    load_id = "%04d" % (load_index + 1)
    return _response_has_terminal(_read_response_lines(responses_path), load_id) == "OK"


def _drive_mission_step(result: DriveResult, step: Dict, step_id: str, step_index: int,
                        proc, runtime: Runtime, logger: HarnessLogger,
                        run_budget: float, run_start: float,
                        mission_ctx: Optional[MissionContext], run_id: Optional[str],
                        preceding_load_ok: bool = True) -> bool:
    """Drive the one mission-kind step (design "The handoff" steps 2-4). Records the
    mission step row on ``result.mission_step`` and returns True IFF the RUN budget
    expired mid-mission (mission killed FIRST, then the KSP tree -> KILLED). A
    mission-STEP-budget expiry kills only the mission subprocess -> INVALID
    (autopilot-flake) and returns False so run.py drives the seam teardown."""
    expect = step.get("expect", hlib.MISSION_STEP_EXPECT)

    # Defensive: a mission step under a non-autopilot driver is a spec bug that
    # validation should have caught; fail closed rather than spawn nothing silently.
    if mission_ctx is None or not run_id:
        logger.warn("Drive", "mission step id=%s has no mission context -> INVALID tooling-mission" % step_id)
        result.mission_step = {"id": step_id, "phase": "mission", "expect": expect,
                               "missionVerdict": None, "met": False, "subkind": "tooling-mission"}
        _log_handoff_return(logger, step_index, None, False, "tooling-mission")
        return False

    # (1) design "The handoff" step 1: only hand off to the mission if the preceding
    # LoadGame returned OK (a settled FLIGHT). A failed boot must NOT burn the
    # mission budget flying against a game that never reached FLIGHT -- skip the
    # spawn and record the mission step unmet. (Classification is driven by the
    # pre-step LoadGame failure, so this is INVALID(load-failed) either way; the
    # value here is spending ZERO mission budget on a dead boot.)
    if not preceding_load_ok:
        logger.warn("Drive", "mission step id=%s: preceding LoadGame not OK -> skipping mission spawn (INVALID load-failed)"
                    % step_id)
        result.mission_step = {"id": step_id, "phase": "mission", "expect": expect,
                               "missionVerdict": None, "met": False, "subkind": "load-failed",
                               "reason": "preceding LoadGame not OK"}
        _log_handoff_return(logger, step_index, None, False, "load-failed")
        return False

    # (2) In-flight venv BACKSTOP: re-read the stamp (the load-bearing gate already
    # ran at pre-launch ADMIT). This only catches a venv mutated AFTER admission; a
    # trip fails the mission step tooling-venv with NO subprocess spawned.
    stamp = runtime.read_venv_stamp(mission_ctx.stamp_path)
    venv_ok, venv_subkind = hlib.venv_admission(stamp, mission_ctx.requirements)
    if not venv_ok:
        logger.warn("Mission", "mission venv backstop tripped (post-ADMIT mutation, %s) -> INVALID tooling-venv; no subprocess spawned"
                    % ("stamp missing" if not stamp else "pin drift"))
        result.mission_step = {"id": step_id, "phase": "mission", "expect": expect,
                               "missionVerdict": None, "met": False, "subkind": venv_subkind}
        _log_handoff_return(logger, step_index, None, False, venv_subkind)
        return False

    mission_budget = float(step.get("budget") or run_budget)
    result_path = os.path.join(RESULTS_DIR, "%s_mission.json" % run_id)
    stdout_path = os.path.join(RESULTS_DIR, "%s_mission.stdout.log" % run_id)
    os.makedirs(RESULTS_DIR, exist_ok=True)

    # (3) DELETE any stale result at the target path before spawning (delete-before-
    # spawn + the per-attempt runId close any stale-result read).
    for p in (result_path, stdout_path):
        try:
            if os.path.isfile(p):
                os.remove(p)
        except OSError:
            pass

    params_json = json.dumps(mission_ctx.mission_params, sort_keys=True)
    args = ["--params", params_json,
            "--rpc-host", DEFAULT_RPC_HOST,
            "--rpc-port", str(DEFAULT_RPC_PORT),
            "--stream-port", str(DEFAULT_STREAM_PORT),
            "--result", result_path,
            "--budget", str(int(mission_budget))]
    logger.info("Mission", "mission spawn name=%s venv-python=%s rpc=%s:%d budget=%ds result=%s"
                % (mission_ctx.mission_name, mission_ctx.venv_python, DEFAULT_RPC_HOST,
                   DEFAULT_RPC_PORT, int(mission_budget), result_path))
    mproc = runtime.spawn_mission(mission_ctx.venv_python, mission_ctx.mission_py,
                                  args, mission_ctx.cwd, stdout_path)

    # (4) Bounded, NON-blocking poll: (a) subprocess exit, (b) mission-step budget,
    # (c) run budget. Two distinct kills; the mission never blocks unbounded.
    mission_start = runtime.now()
    polls = 0
    verdict: Optional[str] = None
    expiry_reason: Optional[str] = None
    while True:
        polls += 1
        exit_code = runtime.poll_exit(mproc)
        if exit_code is not None:
            verdict = _read_mission_verdict(result_path)
            met, subkind = hlib.classify_mission_step(verdict)
            logger.info("Mission", "mission subprocess exit=%s verdict=%s met=%s"
                        % (exit_code, verdict if verdict is not None else "<no-result>", met))
            break
        now = runtime.now()
        if now - run_start > run_budget:
            logger.warn("Budget", "budget exceeded scope=run during mission elapsed=%.0f; killing mission then KSP tree root pid=%d"
                        % (now - run_start, proc.pid))
            runtime.kill_tree(mproc)
            result.killed = True
            result.kill_scope = "run"
            result.killed_pids = runtime.kill_tree(proc)
            logger.info("Budget", "kill complete pids=%s" % result.killed_pids)
            _forward_mission_stdout(stdout_path, logger)
            return True
        if now - mission_start > mission_budget:
            logger.warn("Budget", "mission budget exceeded (%ds) elapsed=%.0f; killing mission subprocess"
                        % (int(mission_budget), now - mission_start))
            runtime.kill_tree(mproc)
            # NIT 7: the mission may have finished WRITING a real result inside the
            # last poll interval (before we killed it). Attempt ONE final read; a
            # valid verdict there is authoritative (e.g. a MISSION-OK the mission
            # just wrote), used instead of fabricating a FLAKE the mission never
            # reported. Only when no valid result exists do we fabricate the
            # autopilot-flake row, tagged so it never reads as the mission itself
            # reporting FLAKE.
            final_verdict = _read_mission_verdict(result_path)
            if final_verdict is not None:
                verdict = final_verdict
                met, subkind = hlib.classify_mission_step(verdict)
                logger.info("Mission", "mission budget expired but a valid result was already written verdict=%s met=%s; using it"
                            % (verdict, met))
            else:
                verdict = hlib.MISSION_VERDICT_FLAKE
                met, subkind = False, "autopilot-flake"
                expiry_reason = "mission-budget-expired (no result)"
                logger.warn("Mission", "mission budget expired with no result -> INVALID autopilot-flake (%s)"
                            % expiry_reason)
            break
        if polls % 40 == 0:
            logger.verbose("Mission", "mission poll elapsed=%.0f/%.0f" % (now - mission_start, mission_budget))
        runtime.sleep(POLL_INTERVAL_SECONDS)

    _forward_mission_stdout(stdout_path, logger)
    result.mission_step = {"id": step_id, "phase": "mission", "expect": expect,
                           "missionVerdict": verdict, "met": met, "subkind": subkind or ""}
    if expiry_reason:
        result.mission_step["reason"] = expiry_reason
    _log_handoff_return(logger, step_index, verdict, met, subkind or "")
    return False


def drive_seam(spec: Dict, instance_dir: str, run_save_name: str, proc,
               runtime: Runtime, logger: HarnessLogger, run_budget: float,
               mission_ctx: Optional[MissionContext] = None,
               run_id: Optional[str] = None) -> DriveResult:
    result = DriveResult()
    steps = (spec.get("driver", {}) or {}).get("steps", []) or []
    commands_path = os.path.join(instance_dir, "parsek-test-commands.txt")
    responses_path = os.path.join(instance_dir, "parsek-test-responses.txt")
    run_start = runtime.now()
    any_response_seen = False

    for i, step in enumerate(steps):
        step_id = "%04d" % (i + 1)
        # M-B1 handoff (design "The handoff"): a phase=mission step is NOT written to
        # the channel; run.py spawns the mission subprocess and bounded-waits it, then
        # drives the REMAINING seam steps regardless of the mission outcome.
        if step.get("phase") == "mission":
            load_ok = _preceding_loadgame_ok(steps, i, responses_path)
            killed = _drive_mission_step(result, step, step_id, i, proc, runtime,
                                         logger, run_budget, run_start, mission_ctx, run_id,
                                         preceding_load_ok=load_ok)
            if killed:
                return result
            continue
        verb = step.get("cmd", "")
        args = dict(step.get("args", {}) or {})
        # Substitute ${runSave} before the line hits the channel (design [driver]).
        for k, v in list(args.items()):
            if v == hlib.RUN_SAVE_TOKEN:
                args[k] = run_save_name
        record_step = {"id": step_id, "cmd": verb, "expect": step.get("expect", "OK")}
        result.steps_with_ids.append(record_step)

        line = format_command_line(step_id, verb, args)
        with open(commands_path, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
        logger.info("Drive", "drive step=%d id=%s cmd=%s expect=%s"
                    % (i, step_id, verb, record_step["expect"]))

        # Per-step wait (S5/N1): a deferred verb (RunTests/LoadGame) can park at the
        # seam head up to the seam's OWN 600s fallback deferral ceiling before it
        # self-emits a TIMEOUT verdict, so the harness must out-wait the LARGER of
        # the spec's per-step budget and that 600s ceiling, plus the 60s margin, so a
        # genuine seam TIMEOUT is OBSERVED (a retryable driver-INVALID) instead of
        # being pre-empted by a harness KILL. The wait is capped at the run budget
        # (the hard ceiling); when the cap bites below the seam window the run is too
        # tight to surface a seam TIMEOUT and would KILL instead -- warned once.
        step_budget = step.get("budget")
        if step_budget is None:
            step_budget = (DEFAULT_BOOT_BUDGET_SECONDS if verb == "LoadGame"
                           else DEFAULT_STEP_BUDGET_SECONDS)
        if verb in hlib.DEFERRED_SEAM_VERBS:
            seam_deferral = max(float(step_budget), float(hlib.SEAM_FALLBACK_DEFERRAL_SECONDS))
            step_wait = hlib.required_step_wait(seam_deferral)
            if step_wait > run_budget:
                step_wait = run_budget
                if not hlib.step_wait_ok(step_wait, seam_deferral):
                    logger.warn("Budget", "deferred step %s: step-wait %.0fs capped by run budget is below seam deferral %.0fs + margin; a seam TIMEOUT may KILL instead of surfacing driver-INVALID"
                                % (verb, step_wait, seam_deferral))
        else:
            # A non-two-phase verb still defers at the seam head up to its OWN dispatch
            # deferral budget (AnswerMergeDialog 120s, KscAction 60s, ... default 60s)
            # before the seam self-emits a TIMEOUT. Out-wait that budget + the 60s margin
            # so the seam's own verdict (retryable driver-INVALID) is OBSERVED instead of
            # the harness KILLing a genuinely-deferring verb; a spec-pinned larger step
            # budget still wins. Capped at the run budget (M-A5 integration item 3).
            dispatch_wait = hlib.required_dispatch_step_wait(verb)
            step_wait = max(float(step_budget), dispatch_wait)
            if step_wait > run_budget:
                step_wait = run_budget
                if step_wait < dispatch_wait:
                    logger.warn("Budget", "dispatch-deferring step %s: step-wait %.0fs capped by run budget is below dispatch deferral %.0fs + margin; a seam TIMEOUT may KILL instead of surfacing driver-INVALID"
                                % (verb, step_wait, dispatch_wait))
        step_start = runtime.now()
        polls = 0

        while True:
            polls += 1
            result.response_lines = _read_response_lines(responses_path)
            if result.response_lines:
                any_response_seen = True
            verdict = _response_has_terminal(result.response_lines, step_id)
            if verdict is not None:
                logger.info("Drive", "drive resp id=%s verdict=%s met=%s"
                            % (step_id, verdict, verdict == record_step["expect"]))
                break

            exit_code = runtime.poll_exit(proc)
            if exit_code is not None:
                result.exit_code = exit_code
                if not any_response_seen and i == 0:
                    result.boot_crashed = True
                    logger.warn("Boot-Wait",
                                "boot-crash: process exited (exit=%s) with no response -> INVALID"
                                % exit_code)
                else:
                    result.batch_crashed = True
                    result.pending_step_id = step_id
                    logger.warn("Drive",
                                "batch-crashed: KSP exited (exit=%s) with step id=%s pending -> PARSEK-FAIL"
                                % (exit_code, step_id))
                return result

            now = runtime.now()
            if now - run_start > run_budget:
                logger.warn("Budget", "budget exceeded scope=run elapsed=%.0f; killing process tree root pid=%d"
                            % (now - run_start, proc.pid))
                result.killed = True
                result.kill_scope = "run"
                result.killed_pids = runtime.kill_tree(proc)
                logger.info("Budget", "kill complete pids=%s" % result.killed_pids)
                return result
            if now - step_start > step_wait:
                logger.warn("Budget", "budget exceeded scope=step:%s elapsed=%.0f; killing process tree root pid=%d"
                            % (verb, now - step_start, proc.pid))
                result.killed = True
                result.kill_scope = "step:%s" % verb
                result.killed_pids = runtime.kill_tree(proc)
                logger.info("Budget", "kill complete pids=%s" % result.killed_pids)
                return result
            if polls % 40 == 0:
                logger.verbose("Drive", "poll: pendingId=%s elapsed=%.0f/%.0f"
                               % (step_id, now - step_start, step_wait))
            runtime.sleep(POLL_INTERVAL_SECONDS)

    # All steps got a terminal response; drain a final read and wait briefly for a
    # clean self-exit (the QUIT owner). A clean exit is the normal PASS path.
    result.response_lines = _read_response_lines(responses_path)
    deadline = runtime.now() + 30.0
    while runtime.now() < deadline:
        exit_code = runtime.poll_exit(proc)
        if exit_code is not None:
            result.exit_code = exit_code
            break
        runtime.sleep(POLL_INTERVAL_SECONDS)
    else:
        # Steps all met but the process did not exit within the grace: force it
        # down so the verifier chain can read a stable set of files.
        result.killed_pids = runtime.kill_tree(proc)
    return result


# ---------------------------------------------------------------------------
# Verifier chain (design The verifier chain).
# ---------------------------------------------------------------------------


def count_recordings(save_dir: str) -> int:
    rec_dir = os.path.join(save_dir, "Parsek", "Recordings")
    if not os.path.isdir(rec_dir):
        return 0
    return sum(1 for f in os.listdir(rec_dir) if f.endswith(".prec"))


def grep_anomaly_tokens(log_text: str) -> List[str]:
    hits = []
    for tok in hlib.ANOMALY_TOKENS:
        if tok in (log_text or ""):
            hits.append(tok)
    return hits


# ---------------------------------------------------------------------------
# Ledger oracle glue (M-B2, design "The ledger-oracle verifier" ~444). run.py owns
# the I/O (seed subprocess, careerSave file read, manifest write); every DECISION
# is oracle.py / hlib. The oracle NEVER reads a Parsek-computed number.
# ---------------------------------------------------------------------------

# The captured-award facet -> careerSave/diff pool name (oracle diff facet names).
_AWARD_FACET_TO_DIFF = {"funds": "funds", "science": "sciencePool", "reputation": "reputation"}


class SeedCapture:
    """The pre-launch seed baseline outcome (design Terminology ~92 / edge 15).
    ``status`` is one of: ``ok`` (seed parsed), ``skipped`` (non-ledger or world-only
    scenario -- no seed needed), ``invalid-fixture`` (template parsed but no career
    pools while [expectations.ledger] is declared -> INVALID(fixture-authoring)),
    ``invalid-tooling`` (the analyzer threw / could not parse -> INVALID(tooling))."""

    def __init__(self, seed, status: str, block: Optional[Dict]):
        self.seed = seed          # oracle.SeedBaseline or None
        self.status = status
        self.block = block        # the raw seed careerSave dict or None


def _capture_seed_baseline(spec: Dict, instance_dir: str, run_save_name: str,
                           run_id: str, runtime: Runtime, logger: HarnessLogger) -> SeedCapture:
    """Acquire the fixture seed baseline pre-launch (design ~92). Runs ONLY for a
    scenario declaring ``[expectations.ledger]`` with ``seedFrom = "template"`` (a
    world-only scenario needs no seed). Distinguishes edge-15 failure modes by the
    seed careerSave block's ``parsed`` / ``hasX`` flags."""
    expectations = spec.get("expectations", {}) or {}
    ledger_block = expectations.get("ledger")
    if ledger_block is None or (ledger_block.get("seedFrom", "template") != "template"):
        return SeedCapture(None, "skipped", None)

    save_dir = os.path.join(instance_dir, "saves", run_save_name)
    seed_out = os.path.join(RESULTS_DIR, "%s.seed" % run_id)
    os.makedirs(seed_out, exist_ok=True)
    res = runtime.run_seed_analyzer(save_dir, seed_out, ANALYZER_TIMEOUT_SECONDS)
    block = None
    if res.ok:
        leaf = os.path.basename(save_dir.rstrip("/\\"))
        json_path = os.path.join(seed_out, "%s.analysis.json" % leaf)
        if os.path.isfile(json_path):
            try:
                with open(json_path, "r", encoding="utf-8", errors="replace") as fh:
                    block = hlib.parse_career_save_block(fh.read())
            except OSError:
                block = None
    # Delete the seed artifact before boot (it is outside the save tree already;
    # remove for tidiness so no stale seed report lingers, design ~98).
    shutil.rmtree(seed_out, ignore_errors=True)

    if block is None or not block.get("parsed", False):
        logger.warn("Seed", "ledger-seed: template careerSave missing/parsed=false -> INVALID(tooling)")
        return SeedCapture(None, "invalid-tooling", block)
    has_any = bool(block.get("hasFunds") or block.get("hasScience") or block.get("hasRep"))
    if not has_any:
        logger.warn("Seed", "ledger-seed: template parsed but no career pools + [expectations.ledger] declared -> INVALID(fixture-authoring)")
        return SeedCapture(None, "invalid-fixture", block)
    try:
        seed = oracle.parse_seed_baseline(block)
    except ValueError as exc:
        # A hasX=true facet with a non-numeric value is a malformed careerSave block
        # (writer-contract violation), not a real seed -> tooling INVALID, never a
        # silent facet-absent degrade (item 10).
        logger.warn("Seed", "ledger-seed: malformed careerSave block (%s) -> INVALID(tooling)" % exc)
        return SeedCapture(None, "invalid-tooling", block)
    logger.info("Seed", "ledger-seed template=%s via=analyzer parsed=True funds=%s science=%s rep=%s hasFunds=%s hasScience=%s hasRep=%s resultsRedirect=%s"
                % (run_save_name, seed.funds, seed.science, seed.reputation,
                   block.get("hasFunds"), block.get("hasScience"), block.get("hasRep"), seed_out))
    return SeedCapture(seed, "ok", block)


def _read_career_save_block(save_dir: str) -> Optional[Dict]:
    """Read the produced save's ``careerSave`` block from the analyzer's
    ``.analysis.json`` (verifier 3 already produced it). None => the block is ABSENT
    (old/broken analyzer -> the ledger verifier treats it as INVALID(tooling),
    edge 13). A ``{parsed:false}`` block is returned as-is (facet-absent)."""
    leaf = os.path.basename(save_dir.rstrip("/\\"))
    json_path = os.path.join(save_dir, "analysis", "%s.analysis.json" % leaf)
    if not os.path.isfile(json_path):
        return None
    try:
        with open(json_path, "r", encoding="utf-8", errors="replace") as fh:
            return hlib.parse_career_save_block(fh.read())
    except OSError:
        return None


def _manifest_entry_to_dict(e) -> Dict:
    """Serialize an oracle.ManifestEntry to a stable-keyed dict for the accumulated
    manifest artifact."""
    return {"ut": e.ut, "seq": e.seq, "kind": e.kind, "funds": e.funds,
            "science": e.science, "reputation": e.reputation, "repMode": e.rep_mode,
            "subjectIds": list(e.subject_ids), "contractGuid": e.contract_guid,
            "provenance": e.provenance, "rec3Row": e.rec3_row}


def _write_accumulated_manifest(manifest: Dict, run_id: str, logger: HarnessLogger) -> None:
    """Write ``harness/results/<runId>.manifest.json`` deterministically (design
    ~262: sorted keys, ``\\n`` endings). Never raises: a write failure degrades to
    an Error log (the verdict is still computed from the in-memory manifest)."""
    os.makedirs(RESULTS_DIR, exist_ok=True)
    path = os.path.join(RESULTS_DIR, "%s.manifest.json" % run_id)
    text = json.dumps(manifest, sort_keys=True, indent=2).replace("\r\n", "\n") + "\n"
    try:
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        logger.info("Verify", "manifest written %s" % path)
    except OSError as exc:
        logger.error("Verify", "manifest write failed: %s" % exc)


def _build_and_write_manifest(ledger_block: Dict, log_text: str, seed,
                              run_id: str, logger: HarnessLogger) -> Tuple:
    """Build leg A (design ~366): the seam-declared author-constant entries (the set
    the oracle sums into EXPECTED) + the stock-log-captured awards (cross-checked as
    corroborating / unexpected). Writes the accumulated ``<runId>.manifest.json``
    (``entries`` = the oracle-consumed seam entries, ``capturedRaw`` = every matched
    stock line). Returns the 3-tuple ``(seam_entries, deduped_captured,
    seam_reject_errors)`` (``seam_reject_errors`` is the tuple of per-entry rejection
    reasons the caller reds each as a dropped-expected-effect PARSEK-FAIL, edge 18).

    v1 reconciliation (design ambiguity resolved): the accumulated ``entries`` the
    oracle CONSUMES for EXPECTED are the seam-declared author constants ONLY. The
    Mental Model invariant (~199) is binding -- an empty-manifest B10 must compute
    ``expected == seed`` so the save-diff catches an award the capture MISSED -- so
    captured awards are NOT summed into expected; they are cross-checked
    (corroborate a seam entry, or red as an unexpected award, edge 4). ``capturedRaw``
    records every captured line for audit."""
    raw_seam = ledger_block.get("manifest", []) or []

    # Capture FIRST: the deduped stock-log awards are the fill-from-capture pool a
    # funds-facet seam entry (null funds amount) draws from (design edge 18 fill path).
    # They are parsed into ManifestEntry objects so the seam parse can match on
    # (seqKey, kind, contractGuid, funds-facet). Captured awards are always well-formed
    # deltas (hlib guarantees it), so a captured-entry parse error is a capture-tooling
    # anomaly, warn-logged (never a scenario RED, unlike a seam-declared rejection).
    cap = hlib.parse_stock_award_lines(log_text)
    deduped = hlib.dedupe_captured_awards(cap.captured)
    captured_parse = oracle.parse_manifest_entries([c.to_entry_dict() for c in deduped])
    for err in captured_parse.errors:
        logger.warn("Verify", "manifest captured entry rejected (capture tooling): %s" % err)
    captured_entries = captured_parse.entries

    seam_parse = oracle.parse_manifest_entries(raw_seam, captured=captured_entries)
    for err in seam_parse.errors:
        logger.warn("Verify", "manifest seam entry rejected: %s" % err)
    seam_entries = seam_parse.entries

    logger.info("Verify", "manifest-capture stockLines=%d deduped=%d seamDeclared=%d seamRejected=%d accumulated=%d"
                % (cap.stock_lines, len(deduped), len(seam_entries), len(seam_parse.errors),
                   len(seam_entries) + len(deduped)))

    manifest = {
        "schema": oracle.SCHEMA_VERSION,
        "runId": run_id,
        # Seed audit copy in the SINGLE careerSave-block shape (key `sciencePool`, NOT
        # `science`) so it round-trips through oracle.parse_seed_baseline identically to
        # the analyzer block it was captured from (review BLOCKER 1 / SF3).
        "seed": {"funds": seed.funds, "sciencePool": seed.science, "reputation": seed.reputation,
                 "hasFunds": seed.has_funds, "hasScience": seed.has_science, "hasRep": seed.has_rep},
        "entries": [_manifest_entry_to_dict(e) for e in seam_entries],
        "capturedRaw": [dict(c.to_entry_dict(), rawLine=c.raw_line) for c in cap.captured],
    }
    _write_accumulated_manifest(manifest, run_id, logger)
    return seam_entries, deduped, tuple(seam_parse.errors)


def _world_declared_vessels(world_block: Dict) -> List[Dict]:
    """The declared vessel entries under ``[[expectations.world.vessels.entry]]``
    (design ~502)."""
    vessels = (world_block or {}).get("vessels", {}) or {}
    return vessels.get("entry", []) or []


def _run_ledger_oracle(ledger_block: Optional[Dict], world_block: Optional[Dict],
                       career_block: Optional[Dict], seed_capture: Optional[SeedCapture],
                       log_text: str, run_id: str, logger: HarnessLogger) -> Tuple[Dict, bool, bool]:
    """Run the ledger-oracle verifier (design ~444). Returns ``(ledgerOracle result
    row, ledger_drift, tooling_invalid)``. Pure over its inputs apart from the leg-A
    manifest write; the diff DECISIONS are all oracle.py.

    Edge 13: an ABSENT careerSave block on an ACTIVE ledger verifier is
    INVALID(tooling) (an active ledger check must never green on a missing input).
    """
    if career_block is None:
        logger.warn("Verify", "verify ledgerOracle status=INVALID subkind=tooling: careerSave block absent from analysis.json")
        return ({"status": oracle.ORACLE_STATUS_INVALID, "subkind": "tooling",
                 "reason": "careerSave block absent from analysis.json",
                 "hardDivergences": 0, "reportOnly": 0, "utWindow": [None, None]},
                False, True)
    # A produced-save careerSave that the analyzer could not parse (parsed=false) on an
    # ACTIVE ledger verifier is the same tooling condition as an ABSENT block (edge 13
    # symmetry / edge 15): the diff would net all-facets-absent into PARSEK-FAIL
    # missing-facet drift, which is the WRONG signal (it is an analyzer/config parse
    # fault, not a Parsek defect). Route to INVALID(tooling), never a false PARSEK-FAIL
    # (item 10). Facet-absence (Sandbox/Science) is signalled by hasX flags with
    # parsed=TRUE, not by parsed=false, so this only fires on a genuine parse failure.
    if career_block.get("parsed") is False:
        logger.warn("Verify", "verify ledgerOracle status=INVALID subkind=tooling: produced careerSave parsed=false (analyzer could not parse the produced save)")
        return ({"status": oracle.ORACLE_STATUS_INVALID, "subkind": "tooling",
                 "reason": "produced careerSave parsed=false (analyzer could not parse the produced save)",
                 "hardDivergences": 0, "reportOnly": 0, "utWindow": [None, None]},
                False, True)

    tol = oracle.default_tolerances()
    divergences: List = []

    if ledger_block is not None:
        seed = seed_capture.seed if seed_capture else None
        if seed is None:
            # Defensive: an active ledger verifier with no seed should have been a
            # pre-launch terminal INVALID; fail closed rather than green.
            logger.warn("Verify", "verify ledgerOracle status=INVALID subkind=tooling: no seed baseline for an active ledger verifier")
            return ({"status": oracle.ORACLE_STATUS_INVALID, "subkind": "tooling",
                     "reason": "no seed baseline", "hardDivergences": 0,
                     "reportOnly": 0, "utWindow": [None, None]}, False, True)
        rec3 = bool(ledger_block.get("rec3CarveOut", False))
        rec3_whitelist = (ledger_block.get("rec3Whitelist", []) or []) if rec3 else []
        seam_entries, captured, seam_errors = _build_and_write_manifest(
            ledger_block, log_text, seed, run_id, logger)
        # Design edge 18: a rejected seam entry (unknown kind / balance amount /
        # state-dependent null / un-fillable funds) is a DROPPED expected effect that
        # would false-PASS if silently dropped; each rejection reds PARSEK-FAIL(ledger).
        for err in seam_errors:
            divergences.append(oracle.OracleDivergence(
                facet="ledger", kind="manifest-parse-error", identity="",
                expected=None, parsed=None, ut_window=(None, None), hard=True,
                detail="manifest entry rejected (a dropped expected effect can false-PASS): %s" % err))
            logger.warn("Verify", "ledger manifest-parse-error (hard): %s" % err)
        expected = oracle.compute_expected(seed, seam_entries, tol, rec3_whitelist)
        logger.info("Verify", "oracle-expected funds=%s science=%s rep=%s subjects=%d activeContracts=%d rec3CarveOut=%s"
                    % (expected.funds, expected.science, expected.reputation,
                       len(expected.subject_science), len(expected.active_contract_guids), rec3))
        for row in expected.rec3_residual_rows:
            logger.info("Verify", "oracle: rec3 residual retained row=%s expecting [Rec-3 residual]" % row)
        divergences += oracle.diff_expected_vs_parsed(expected, career_block, tol, rec3_whitelist)
        # Zero-delta cross-check (design ~482 / edge 4): a captured award not
        # explained by a seam entry is an unexpected stock award -> hard drift.
        for c in hlib.unmatched_captured_awards(seam_entries, captured):
            facet = _AWARD_FACET_TO_DIFF.get(c.facet, c.facet)
            # Edge 4 (~582): the UT window is the captured line's UT, or the ORDINAL
            # seq when the award had no UT-stamped [Parsek] neighbor (never [None, None],
            # which would strip the drift's only positional anchor). This is the RAW
            # NUMERIC anchor, not the type-tagged seq_key (the window bounds must stay
            # comparable for _aggregate_ut_window's min/max; the tag lives only in the
            # matcher keys).
            aw = c.ut if c.ut is not None else c.seq
            divergences.append(oracle.OracleDivergence(
                facet=facet, kind="unexpected-award",
                identity=(c.contract_guid or c.subject_id or ""),
                expected=None, parsed=c.amount, ut_window=(aw, aw), hard=True,
                detail="unexpected stock award kind=%s facet=%s amount=%r ut=%s seqKey=%r line=%r"
                       % (c.kind, c.facet, c.amount, c.ut, c.seq_key, c.raw_line)))
            logger.warn("Verify", "manifest-capture: unexpected stock award ut=%s kind=%s line='%s'"
                        % (c.ut, c.kind, c.raw_line))

    if world_block is not None:
        declared = _world_declared_vessels(world_block)
        parsed_vessels = career_block.get("vessels", []) if isinstance(career_block, dict) else []
        # report_phantoms stays FALSE (the default) DELIBERATELY (review N2): the
        # [expectations.world] block is a resource WHITELIST, not an exhaustive census,
        # so an undeclared parsed vessel (stray debris, other craft) is expected and
        # emitting a report-only phantom per save vessel would be pure noise. Phantoms
        # are report-only and can never red (design ~516), so suppressing them changes
        # no verdict; the classification remains available for a future census facet.
        world_divs = oracle.diff_world_vessels(declared, parsed_vessels, tol)
        for d in world_divs:
            logger.info("Verify", "world-vessel corr=%s kind=%s expected=%s parsed=%s hard=%s detail=%s"
                        % (d.identity, d.kind, d.expected, d.parsed, d.hard, d.detail))
        divergences += world_divs
        logger.verbose("Verify", "world: roster sub-facet deferred (no CareerSaveSnapshot roster)")

    for d in divergences:
        if d.hard:
            logger.warn("Verify", "ledger-drift facet=%s id=%s expected=%s parsed=%s utWindow=[%s,%s]"
                        % (d.facet, d.identity, d.expected, d.parsed, d.ut_window[0], d.ut_window[1]))
        else:
            logger.info("Verify", "ledger-diff facet=%s id=%s expected=%s parsed=%s hard=False"
                        % (d.facet, d.identity, d.expected, d.parsed))

    result = oracle.build_oracle_result(divergences)
    ledger_drift = oracle.has_hard_drift(divergences)
    logger.info("Verify", "verify ledgerOracle status=%s hardDivergences=%d reportOnly=%d"
                % (result["status"], result["hardDivergences"], result["reportOnly"]))
    return result, ledger_drift, False


def run_verifiers(spec: Dict, instance_dir: str, run_save_name: str,
                  drive: DriveResult, runtime: Runtime, logger: HarnessLogger,
                  seed_capture: Optional["SeedCapture"] = None,
                  run_id: Optional[str] = None) -> Dict:
    """Run the ordered verifier chain and return the (driver, verifiers) fact dicts
    for hlib.classify_verdict plus a per-verifier detail record."""
    expectations = spec.get("expectations", {}) or {}
    recordings = expectations.get("recordings", {}) or {}
    count_spec = recordings.get("count", {}) or {}
    count_max = count_spec.get("max")
    log_contracts = expectations.get("logContracts", {}) or {}
    required = log_contracts.get("required", []) or []
    requires_batch = any("BATCH_COMPLETE" in str(p) for p in required)

    save_dir = os.path.join(instance_dir, "saves", run_save_name)
    log_path = os.path.join(instance_dir, "KSP.log")
    log_text = ""
    if os.path.isfile(log_path):
        try:
            with open(log_path, "r", encoding="utf-8", errors="replace") as fh:
                log_text = fh.read()
        except OSError:
            log_text = ""

    killed = drive.killed
    detail: Dict[str, Dict] = {}
    # SF1: every subprocess-scoped verifier retry that fired this attempt, recorded in
    # the durable result JSON so a recovered flake is auditable AND the flake ledger can
    # accrue it (hlib.flake_attempt_entries reads verifiers.subprocessRetry).
    subprocess_retries: List[Dict] = []

    # 1. Driver validity (from the response stream + the mission step, M-B1).
    ev = hlib.evaluate_response_stream(drive.response_lines, drive.steps_with_ids)
    mission = drive.mission_step
    if mission is None:
        # Seam-only driver: every seam step gates validity (unchanged M-A5).
        driver_valid = ev.all_expected_met and not drive.boot_crashed and not drive.batch_crashed
        stage_subkind = _stage_subkind_for(ev.first_unmet)
    else:
        # Autopilot driver (design classification carve-out): validity is gated by
        # the steps UP TO AND INCLUDING the mission handoff -- LoadGame/SetSetting
        # (pre-mission seam steps) plus the mission verdict. Post-mission seam steps
        # (CommitTree/FlushAndQuit) are RECORDED but NON-gating on a MISSION-OK run:
        # a good flight Parsek then failed to record is a PARSEK-FAIL(expectation),
        # NOT a driver-INVALID a retry would paper over. When the mission itself did
        # NOT return MISSION-OK, its subkind drives the driver-INVALID.
        mission_id = mission["id"]
        pre_steps = [s for s in ev.steps if s.step_id < mission_id]
        pre_unmet = next((s for s in pre_steps if not s.met), None)
        pre_met = pre_unmet is None
        driver_valid = (pre_met and mission["met"]
                        and not drive.boot_crashed and not drive.batch_crashed)
        if not pre_met:
            stage_subkind = _stage_subkind_for(pre_unmet)
        elif not mission["met"]:
            stage_subkind = mission["subkind"]
        else:
            stage_subkind = ""
        detail["mission"] = {
            "status": "PASS" if mission["met"] else "FAIL",
            "missionVerdict": mission["missionVerdict"], "subkind": mission["subkind"],
        }
    detail["driverValidity"] = {
        "status": "PASS" if driver_valid else ("SKIPPED" if killed else "FAIL"),
        "allExpectedMet": ev.all_expected_met, "subkind": stage_subkind,
    }
    logger.info("Verify", "verify driverValidity status=%s allMet=%s subkind=%s%s"
                % (detail["driverValidity"]["status"], ev.all_expected_met, stage_subkind or "-",
                   (" missionVerdict=%s" % mission["missionVerdict"]) if mission else ""))

    # 2. BATCH_COMPLETE presence (parsed even on short-circuit; cheap triage).
    # M-A5.1 (N3): a multi-category selector ("all" / "A,B") emits per-category lines
    # plus a category=multi:<count> AGGREGATE; resolve_batch_complete gates on the
    # aggregate union (failed==0 means EVERY category passed) and flags a missing
    # aggregate with per-category lines present as a defined fault (never a silent pass).
    driven_category = _driven_category(spec)
    batches = hlib.find_batch_complete_lines(log_text)
    sel = hlib.resolve_batch_complete(batches, driven_category)
    batch_present = sel.present
    batch_failed = sel.failed
    detail["batchComplete"] = {
        "status": "PASS" if batch_present else ("SKIPPED" if not requires_batch else "FAIL"),
        "found": batch_present, "failed": batch_failed,
        "category": sel.category if sel.category is not None else driven_category,
        "multi": sel.multi, "aggregateMissing": sel.aggregate_missing,
        "categoryCountMismatch": sel.category_count_mismatch,
        "duplicateAggregate": sel.duplicate_aggregate,
        "expectedCategoryCount": sel.expected_category_count,
        "perCategoryCount": sel.per_category_count,
    }
    if sel.duplicate_aggregate:
        logger.warn("Verify", "verify batchComplete: multi-category selector '%s' emitted MORE THAN ONE category=multi:<n> aggregate line -> duplicate_aggregate defined fault (the summary emitted twice); reds batch-incomplete, never a silent first-wins (M-A5 integration item 10)"
                    % (driven_category,))
    if sel.aggregate_missing:
        logger.warn("Verify", "verify batchComplete: multi-category selector '%s' emitted %d per-category line(s) but NO category=multi:<n> aggregate -> defined fault (batch cut off before H1 summary); reds batch-incomplete, never a silent pass (M-A5.1 N3)"
                    % (driven_category, sel.per_category_count))
    if sel.category_count_mismatch:
        logger.warn("Verify", "verify batchComplete: multi-category aggregate category=%s declares %s categor(ies) but %d per-category BATCH_COMPLETE line(s) present -> category_count_mismatch defined fault (a category batch cut off, or an unexpected extra batch); reds batch-incomplete, never a silent pass (M-A5.1 SF2)"
                    % (sel.category, sel.expected_category_count, sel.per_category_count))
    logger.info("Verify", "verify batchComplete status=%s found=%s failed=%s multi=%s perCategory=%d"
                % (detail["batchComplete"]["status"], batch_present, batch_failed,
                   sel.multi, sel.per_category_count))

    verifiers: Dict = {
        "killed": killed,
        "batch_expected": requires_batch,
        "batch_present": batch_present,
    }
    driver_facts: Dict = {
        "spec_valid": True,
        "admission_ok": True,
        "instance_lock_ok": True,
        "instance_busy": False,
        "boot_crashed": drive.boot_crashed,
        "boot_crash_repeated": False,  # set by the caller across attempts
        "batch_crashed": drive.batch_crashed,
        "valid": driver_valid,
        "stage_subkind": stage_subkind or "driver-stage",
    }

    # KILLED short-circuits the SAVE-reading verifiers (design edge 5): a torn save
    # is never ground truth. Only killed-run log validation + batch lines apply.
    if killed:
        prof = hlib.select_logvalidate_profile(hlib.spec_expects_live_recording(spec), True)
        no_rec = prof.suppress_recording_rules
        lv = runtime.run_log_validate(log_path, killed=True, no_recording=no_rec,
                                      timeout=LOGVALIDATE_TIMEOUT_SECONDS)
        detail["logValidate"] = {
            "status": "INVALID" if lv.timed_out else ("PASS" if lv.ok else "FAIL"),
            "recRulesSuppressed": prof.suppress_recording_rules, "killedRunMode": True,
        }
        logger.info("Verify", "verify logValidate status=%s recRulesSuppressed=%s killedRunMode=True"
                    % (detail["logValidate"]["status"], prof.suppress_recording_rules))
        # Results parse is triage-only on a killed run (design S9): recorded, never
        # verdict-driving.
        results_failures = _parse_results(instance_dir)
        detail["testResults"] = {"status": "SKIPPED", "failures": results_failures,
                                 "reason": "killed-triage-only"}
        detail["analyzer"] = {"status": "SKIPPED", "reason": "killed-torn-save"}
        detail["anomalySweep"] = {"status": "SKIPPED", "reason": "killed"}
        detail["expectations"] = {"status": "SKIPPED", "reason": "killed"}
        # The ledger-oracle verifier is SKIPPED on any KILLED attempt: a torn save is
        # never ground truth (design edge 11), regardless of whether it was declared.
        ledger_active_killed = (expectations.get("ledger") is not None
                                or expectations.get("world") is not None)
        detail["ledgerOracle"] = {"status": "SKIPPED",
                                  "reason": "killed" if ledger_active_killed else "no-ledger-block-declared"}
        detail["subprocessRetry"] = subprocess_retries
        return {"driver": driver_facts, "verifiers": verifiers, "detail": detail,
                "recordingCount": None}

    # Non-killed: run the full chain, short-circuiting after the first hard fail
    # (but always keeping batch + results as triage context).
    short_circuited = False

    # 3. Offline analyzer over the produced save, Forbid (fresh-save gate).
    analyzer_verdict = None
    if driver_valid:
        analyzer_verdict, analyzer_detail, analyzer_retry = _run_analyzer_retrying(
            save_dir, runtime, logger)
        detail["analyzer"] = analyzer_detail
        if analyzer_retry is not None:
            subprocess_retries.append(analyzer_retry)
        if analyzer_verdict is not None and analyzer_verdict.status != "PASS":
            short_circuited = True
    else:
        # N6: even on a terminal driver-INVALID, run the analyzer ONCE triage-only
        # (non-verdict) for the record, then let the driver flags drive the verdict.
        # NIT 3: NO subprocess retry here -- a triage-only analyzer over an already-
        # INVALID driver save is non-verdict, so re-running a wedged subprocess is
        # pure waste (call _run_analyzer directly, not the retrying wrapper).
        _, analyzer_detail = _run_analyzer(save_dir, runtime, logger, triage_only=True)
        detail["analyzer"] = analyzer_detail
    verifiers["analyzer"] = analyzer_verdict if driver_valid else None

    # 4. Log validation + LogContract.
    if driver_valid and not short_circuited:
        prof = hlib.select_logvalidate_profile(hlib.spec_expects_live_recording(spec), False)
        no_rec = prof.suppress_recording_rules
        lv, lv_retry = _run_log_validate_retrying(runtime, log_path, no_rec, logger)
        if lv_retry is not None:
            subprocess_retries.append(lv_retry)
        if lv.timed_out:
            verifiers["tooling_invalid"] = True
            verifiers["tooling_subkind"] = "tooling"
            detail["logValidate"] = {"status": "INVALID", "subkind": "tooling",
                                     "reason": "subprocess timed out"}
        else:
            failed = not lv.ok
            verifiers["log_validate_failed"] = failed
            detail["logValidate"] = {"status": "FAIL" if failed else "PASS",
                                     "recRulesSuppressed": prof.suppress_recording_rules,
                                     "killedRunMode": False}
            if failed:
                short_circuited = True
        logger.info("Verify", "verify logValidate status=%s recRulesSuppressed=%s killedRunMode=False"
                    % (detail["logValidate"]["status"], prof.suppress_recording_rules))
    else:
        detail.setdefault("logValidate", {"status": "SKIPPED", "reason": "short-circuit"})

    # 5. Results parse (always parsed for triage; drives verdict when reachable).
    results_failures = _parse_results(instance_dir)
    results_mismatch = (batch_present and batch_failed is not None
                        and results_failures != batch_failed)
    if driver_valid and not short_circuited:
        results_failed = results_failures > 0
        verifiers["results_failed"] = results_failed
        verifiers["results_mismatch"] = results_mismatch
        detail["testResults"] = {"status": "FAIL" if (results_failed or results_mismatch) else "PASS",
                                 "failures": results_failures, "batchFailed": batch_failed}
        if results_failed or results_mismatch:
            short_circuited = True
    else:
        detail["testResults"] = {"status": "SKIPPED", "failures": results_failures,
                                 "reason": "short-circuit-or-invalid-driver"}

    # 6. Anomaly sweep.
    if driver_valid and not short_circuited:
        allowed = expectations.get("allowedAnomalies", []) or []
        hits = grep_anomaly_tokens(log_text)
        unallowed = hlib.evaluate_anomaly_sweep(hits, allowed)
        verifiers["anomaly_hit"] = bool(unallowed)
        detail["anomalySweep"] = {"status": "FAIL" if unallowed else "PASS",
                                  "hits": unallowed, "allowed": list(allowed)}
        if unallowed:
            short_circuited = True
        logger.info("Verify", "verify anomalySweep status=%s hits=%s"
                    % (detail["anomalySweep"]["status"], unallowed))
    else:
        detail.setdefault("anomalySweep", {"status": "SKIPPED", "reason": "short-circuit"})

    # 7. Expectations manifest.
    recording_count = count_recordings(save_dir)
    if driver_valid and not short_circuited:
        exp = hlib.evaluate_expectations(expectations, recording_count, log_text)
        verifiers["expectation_mismatch"] = (exp.status != "PASS")
        detail["expectations"] = {"status": exp.status, "mismatches": list(exp.mismatches),
                                  "reserved": list(exp.reserved)}
        logger.info("Verify", "verify expectations status=%s mismatches=%d"
                    % (exp.status, len(exp.mismatches)))
    else:
        detail.setdefault("expectations", {"status": "SKIPPED", "reason": "short-circuit"})

    # 8. Ledger oracle (M-B2). Active iff the scenario declares [expectations.ledger]
    # OR [expectations.world]; else SKIPPED(no-ledger-block-declared), the reserved
    # contract. Runs after the analyzer (verifier 3) produced the .analysis.json
    # careerSave block; independent of the later-verifier short-circuit (a ledger drift
    # is its own signal). Gated on driver_valid: a driver-INVALID save is not ground truth.
    ledger_block = expectations.get("ledger")
    world_block = expectations.get("world")
    if ledger_block is None and world_block is None:
        detail["ledgerOracle"] = {"status": "SKIPPED", "reason": "no-ledger-block-declared"}
    elif not driver_valid:
        detail["ledgerOracle"] = {"status": "SKIPPED", "reason": "driver-invalid"}
        logger.info("Verify", "verify ledgerOracle status=SKIPPED reason=driver-invalid")
    else:
        career_block = _read_career_save_block(save_dir)
        led_detail, ledger_drift, ledger_tooling = _run_ledger_oracle(
            ledger_block, world_block, career_block, seed_capture,
            log_text, run_id or "", logger)
        detail["ledgerOracle"] = led_detail
        if ledger_tooling:
            verifiers["tooling_invalid"] = True
            verifiers["tooling_subkind"] = led_detail.get("subkind", "tooling")
        if ledger_drift:
            verifiers["ledger_drift"] = True

    detail["subprocessRetry"] = subprocess_retries
    return {"driver": driver_facts, "verifiers": verifiers, "detail": detail,
            "recordingCount": recording_count}


def _stage_subkind_for(fu) -> str:
    """Map the first unmet seam-step outcome to a driver-stage subkind (design
    driver-validity taxonomy). None (no unmet step) -> "" (met). An M-C1 verb refusal
    carrying a recognized `msg=` reason maps to the finer driver-* subkind (item 6);
    an unrecognized reason falls back to driver-verdict-mismatch."""
    if fu is None:
        return ""
    if not fu.found:
        return "driver-stage"
    if fu.verdict == "TIMEOUT":
        return "seam-timeout"
    if fu.cmd == "LoadGame" and fu.verdict == "ERROR":
        return "load-failed"
    refusal = hlib.classify_seam_refusal_subkind(getattr(fu, "msg", ""))
    if refusal:
        return refusal
    return "driver-verdict-mismatch"


def _driven_category(spec: Dict) -> Optional[str]:
    driver = spec.get("driver", {}) or {}
    for step in driver.get("steps", []) or []:
        if step.get("cmd") == "RunTests":
            return (step.get("args", {}) or {}).get("category")
    autorun = driver.get("autorun")
    if autorun and autorun.get("tests"):
        return autorun.get("tests")
    return None


def _parse_results(instance_dir: str) -> int:
    path = os.path.join(instance_dir, RESULTS_FILE)
    if not os.path.isfile(path):
        return 0
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return hlib.parse_results_failures(fh.read())
    except OSError:
        return 0


def _run_analyzer(save_dir: str, runtime: Runtime, logger: HarnessLogger,
                  triage_only: bool = False):
    res = runtime.run_analyzer(save_dir, fresh_gate=True, timeout=ANALYZER_TIMEOUT_SECONDS)
    if res.timed_out:
        detail = {"status": "INVALID", "subkind": "tooling", "reason": "analyzer subprocess timed out"}
        logger.warn("Verify", "verify analyzer status=INVALID subkind=tooling: subprocess timed out")
        return (hlib.AnalyzerVerdict("INVALID", "tooling", None) if not triage_only else None), detail
    leaf = os.path.basename(save_dir.rstrip("/\\"))
    analysis_dir = os.path.join(save_dir, "analysis")
    txt_path = os.path.join(analysis_dir, "%s.analysis.txt" % leaf)
    json_path = os.path.join(analysis_dir, "%s.analysis.json" % leaf)
    red = None
    aj = None
    if os.path.isfile(txt_path):
        with open(txt_path, "r", encoding="utf-8", errors="replace") as fh:
            red = hlib.parse_analysis_red_token(fh.read())
    if os.path.isfile(json_path):
        with open(json_path, "r", encoding="utf-8", errors="replace") as fh:
            aj = hlib.parse_analysis_json(fh.read())
    verdict = hlib.classify_analyzer(red, aj)
    detail = {
        "status": verdict.status, "red": red, "subkind": verdict.subkind,
        "failNonBaselined": aj.fail_non_baselined if aj else None,
        "staleNonBaselined": aj.stale_non_baselined if aj else None,
        "topRule": verdict.top_rule,
        "reportTxt": txt_path if os.path.isfile(txt_path) else None,
        "reportJson": json_path if os.path.isfile(json_path) else None,
        "triageOnly": triage_only,
    }
    logger.info("Verify", "verify analyzer status=%s red=%s subkind=%s topRule=%s%s"
                % (verdict.status, red, verdict.subkind, verdict.top_rule,
                   " (triage-only)" if triage_only else ""))
    return verdict, detail


# ---------------------------------------------------------------------------
# Subprocess-scoped retry (M-A5.1). When a verifier that shells out over the
# already-produced run artifacts flakes on a TOOLING fault (a wedged pwsh analyzer,
# a transient log-validate timeout) -- not a Parsek verdict -- re-invoke JUST that
# subprocess ONCE over the SAME artifacts before the whole-attempt retry burns a
# fresh ~10-min KSP boot. The SCOPE decision is pure (hlib.classify_retry_scope);
# the re-invocation is behind the Runtime seam. BOTH attempts' outcomes are logged
# so a subprocess retry never masks nondeterminism.
# ---------------------------------------------------------------------------


def _verifier_with_subprocess_retry(stage, invoke, classify, logger):
    """Run a verifier subprocess (``invoke()``) and, on a subprocess-retryable tooling
    fault, re-run it ONCE over the same artifacts. ``classify(raw)`` -> ``(is_tooling_
    fault, subkind, label)``. Returns ``(raw_result, retry_info)`` where ``retry_info``
    is None when no subprocess retry fired, else a self-contained detail dict
    ``{"stage","retried":True,"attempt1","attempt2","recovered"}`` (M-A5.1 SF1 / NIT 1):
    the durable result JSON records it so a recovered flake is auditable AND the flake
    ledger can accrue it. On a recovery the second (good) result is returned so no
    whole-attempt retry is needed; on a repeat fault the second (still-faulted) result
    is returned and flows through the unchanged INVALID(tooling) -> whole-attempt retry
    taxonomy."""
    raw = invoke()
    tooling, subkind, label = classify(raw)
    scope = hlib.classify_retry_scope(stage, tooling, subkind)
    if scope != hlib.RETRY_SCOPE_SUBPROCESS:
        return raw, None
    logger.warn("Verify", "verify %s subprocess-retry: attempt 1 tooling fault subkind=%s (%s); re-running the SAME subprocess over the same run artifacts, no fresh boot (M-A5.1)"
                % (stage, subkind, label))
    raw2 = invoke()
    tooling2, subkind2, label2 = classify(raw2)
    # Log BOTH attempts' outcomes: a subprocess retry must never mask nondeterminism.
    logger.info("Verify", "verify %s subprocess-retry outcomes: attempt1=%s attempt2=%s"
                % (stage, label, label2))
    recovered = not tooling2
    if recovered:
        logger.info("Verify", "verify %s subprocess-retry RECOVERED on attempt 2 (attempt1 tooling subkind=%s -> attempt2 %s); no whole-attempt retry needed"
                    % (stage, subkind, label2))
    else:
        logger.warn("Verify", "verify %s subprocess-retry: attempt 2 ALSO tooling subkind=%s; deferring to the unchanged whole-attempt retry policy"
                    % (stage, subkind2))
    retry_info = {"stage": stage, "retried": True, "attempt1": label,
                  "attempt2": label2, "recovered": recovered}
    return raw2, retry_info


def _classify_analyzer_outcome(vd_detail):
    """(is_tooling_fault, subkind, label) for an ``_run_analyzer`` return. A tooling
    fault is an analyzer INVALID whose subkind a subprocess retry can address
    (``tooling`` subprocess timeout / ``analyzer-error`` no-gate-token crash). A
    PARSEK-FAIL (RED=1) or a fixture-* INVALID is NOT a tooling fault -> not re-run."""
    _verdict, detail = vd_detail
    status = detail.get("status")
    subkind = detail.get("subkind", "") or ""
    tooling = (status == "INVALID" and subkind in hlib.SUBPROCESS_RETRYABLE_SUBKINDS)
    label = "%s%s" % (status, ("/%s" % subkind if subkind else ""))
    return tooling, subkind, label


def _run_analyzer_retrying(save_dir, runtime, logger):
    """`_run_analyzer` with the M-A5.1 subprocess-scoped retry wrapped around it.
    Returns ``(verdict, detail, retry_info)``; ``retry_info`` is None when no subprocess
    retry fired, else the self-contained subprocessRetry dict the result JSON records.
    Used ONLY on the verdict-driving path -- the triage-only analyzer run (a driver-
    INVALID save is non-verdict) calls ``_run_analyzer`` directly, no retry (NIT 3)."""
    def invoke():
        return _run_analyzer(save_dir, runtime, logger, triage_only=False)
    (verdict, detail), retry_info = _verifier_with_subprocess_retry(
        "analyzer", invoke, _classify_analyzer_outcome, logger)
    return verdict, detail, retry_info


def _run_log_validate_retrying(runtime, log_path, no_rec, logger):
    """`runtime.run_log_validate` (non-killed) with the M-A5.1 subprocess-scoped retry.
    A log-validate TIMEOUT is a tooling flake (re-run once); a clean PASS or a genuine
    validation FAIL (a Parsek verdict) is returned as-is, never re-run. Returns
    ``(lv, retry_info)`` (retry_info None when no subprocess retry fired)."""
    def invoke():
        return runtime.run_log_validate(log_path, killed=False, no_recording=no_rec,
                                        timeout=LOGVALIDATE_TIMEOUT_SECONDS)

    def classify(lv):
        tooling = lv.timed_out
        subkind = "tooling" if tooling else ""
        label = "timeout" if tooling else ("PASS" if lv.ok else "FAIL")
        return tooling, subkind, label
    lv, retry_info = _verifier_with_subprocess_retry("logValidate", invoke, classify, logger)
    return lv, retry_info


# ---------------------------------------------------------------------------
# One scenario, one attempt.
# ---------------------------------------------------------------------------


def _make_run_id(scenario_id: str, attempt: int) -> str:
    """The per-attempt runId (design "Mission result": ATTEMPT-SUFFIXED so each
    retry writes its own <runId>_mission.json). Computed ONCE per attempt in
    run_attempt so the mission-result filename the handoff writes/reads matches the
    runId the durable result JSON records, even if the minute rolls over during a
    long flight."""
    run_id = "%s_%s" % (datetime.now(timezone.utc).strftime("%Y-%m-%d_%H%M"), scenario_id)
    if attempt > 1:
        run_id += "_a%d" % attempt
    return run_id


def run_attempt(spec: Dict, instance_dir: str, umbrella_root: str, runtime: Runtime,
                attempt: int, prior_boot_crashed: bool, logger: HarnessLogger) -> Dict:
    scenario_id = spec.get("id")
    profile = spec.get("instanceProfile")
    started = utcnow_iso()
    start_wall = runtime.now()
    run_id = _make_run_id(scenario_id, attempt)

    # ---- ADMIT -----------------------------------------------------------
    manifest, incomplete = read_manifest(instance_dir)
    expected = build_expected_from_manifest(manifest, instance_dir, logger) if manifest else {}
    admission = hlib.admit_instance(expected, manifest, incomplete)
    admit_diff = [
        {"field": d.field, "expected": d.expected, "actual": d.actual, "kind": d.kind}
        for d in admission.diff
    ]
    logger.info("Admit", "admit instance=%s manifest=%s result=%s"
                % (profile, "present" if manifest else "missing",
                   "OK" if admission.admitted else "DRIFT"))
    if not admission.admitted:
        for d in admit_diff:
            logger.warn("Admit", "admit drift field=%s expected=%s actual=%s"
                        % (d["field"], d["expected"], d["actual"]))
        return _terminal_result(spec, profile, attempt, started, start_wall, runtime,
                                hlib.Verdict(hlib.VERDICT_INVALID, admission.subkind, False,
                                             "admission %s" % admission.subkind),
                                admission={"admitted": False, "subkind": admission.subkind,
                                           "diff": admit_diff},
                                logger=logger, run_id=run_id)

    admission_rec = {"admitted": True, "subkind": "", "diff": []}

    # ---- VENV ADMIT (M-B1, autopilot only) -------------------------------
    # The mission venv is admitted at pre-launch ADMIT, alongside instance admission
    # (design edge 4): a missing / drifted venv refuses TERMINAL INVALID(tooling-venv)
    # with NO KSP boot and NO retry (a provisioning fault a retry cannot fix).
    mission_ctx: Optional[MissionContext] = None
    driver = spec.get("driver", {}) or {}
    if driver.get("kind") == "autopilot":
        stamp = runtime.read_venv_stamp(VENV_STAMP_PATH)
        requirements = _parse_requirements(runtime.read_requirements_text(REQUIREMENTS_PATH))
        venv_ok, venv_subkind = hlib.venv_admission(stamp, requirements)
        venv_result = "OK" if venv_ok else ("MISSING" if not stamp else "DRIFT")
        logger.info("Mission", "mission venv-admit stamp=%s result=%s" % (VENV_STAMP_PATH, venv_result))
        if not venv_ok:
            logger.warn("Mission", "mission venv drift/missing: %s vs %s; INVALID %s (terminal, no KSP boot)"
                        % (REQUIREMENTS_PATH, VENV_STAMP_PATH, venv_subkind))
            return _terminal_result(spec, profile, attempt, started, start_wall, runtime,
                                    hlib.Verdict(hlib.VERDICT_INVALID, venv_subkind, False,
                                                 "mission venv %s" % venv_result),
                                    admission=admission_rec, logger=logger, run_id=run_id)
        mission_ctx = MissionContext(
            mission_name=str(driver.get("mission")),
            venv_python=VENV_PYTHON,
            mission_py=os.path.join(MISSIONS_DIR, "%s.py" % driver.get("mission")),
            mission_params=dict(driver.get("missionParams", {}) or {}),
            cwd=instance_dir,
            stamp_path=VENV_STAMP_PATH,
            requirements=requirements)

    # ---- LOCK ------------------------------------------------------------
    lock_path = acquire_run_lock(instance_dir, runtime, logger)
    if lock_path is None:
        return _terminal_result(spec, profile, attempt, started, start_wall, runtime,
                                hlib.Verdict(hlib.VERDICT_INVALID, "instance-locked", False,
                                             "run lock held by a live sibling"),
                                admission=admission_rec, logger=logger, run_id=run_id)

    try:
        # ---- ZOMBIE PREFLIGHT --------------------------------------------
        zombie_pid = runtime.ksp_running(instance_dir)
        if zombie_pid is not None:
            logger.warn("Preflight", "instance-busy: live KSP pid=%s bound to %s; refusing (INVALID instance-busy)"
                        % (zombie_pid, instance_dir))
            return _terminal_result(spec, profile, attempt, started, start_wall, runtime,
                                    hlib.Verdict(hlib.VERDICT_INVALID, "instance-busy", False,
                                                 "a live KSP is bound to the instance"),
                                    admission=admission_rec, logger=logger, run_id=run_id)
        logger.info("Preflight", "zombie-check instance=%s result=CLEAR" % profile)

        # ---- STAGE -------------------------------------------------------
        staged, run_save_name, stage_subkind = stage_fixture(spec, instance_dir, runtime, logger)
        if not staged:
            reason = ("staged target escaped saves/ (containment guard)"
                      if stage_subkind == "spec-invalid" else "fixture staging failed")
            return _terminal_result(spec, profile, attempt, started, start_wall, runtime,
                                    hlib.Verdict(hlib.VERDICT_INVALID, stage_subkind or "staging",
                                                 False, reason),
                                    admission=admission_rec, logger=logger, run_id=run_id)

        # ---- SEED BASELINE (M-B2, ledger scenarios) ----------------------
        # Acquire the fixture seed pre-launch by running the analyzer over the STAGED
        # save (design ~92). Edge 15: a template with no career pools (fixture bug)
        # or an analyzer that could not parse it (tooling) is a TERMINAL INVALID with
        # no boot -- there is nothing to assert against. Non-ledger / world-only
        # scenarios skip this entirely.
        seed_capture = _capture_seed_baseline(spec, instance_dir, run_save_name,
                                              run_id, runtime, logger)
        if seed_capture.status in ("invalid-fixture", "invalid-tooling"):
            subkind = "fixture-authoring" if seed_capture.status == "invalid-fixture" else "tooling"
            return _terminal_result(spec, profile, attempt, started, start_wall, runtime,
                                    hlib.Verdict(hlib.VERDICT_INVALID, subkind, False,
                                                 "ledger seed baseline %s" % seed_capture.status),
                                    admission=admission_rec, logger=logger, run_id=run_id)

        # ---- LAUNCH ------------------------------------------------------
        env = dict(os.environ)
        env["PARSEK_TEST_COMMANDS"] = "1"
        autorun = (spec.get("driver", {}) or {}).get("autorun")
        if autorun and autorun.get("tests"):
            env["PARSEK_AUTORUN_TESTS"] = str(autorun.get("tests"))
            if autorun.get("exit"):
                env["PARSEK_AUTORUN_EXIT"] = "1"
        env.pop("PARSEK_ANALYZER_BASELINE_MODE", None)  # never set at KSP launch
        run_budget = float((spec.get("runtime", {}) or {}).get("budgetSeconds", 600))
        exe = runtime.resolve_exe(instance_dir)
        proc = runtime.launch(exe, [], env, instance_dir)
        logger.info("Launch", "launch exe=%s pid=%s env=[TEST_COMMANDS=1 AUTORUN=%s EXIT=%s] budget=%ds"
                    % (exe, proc.pid, env.get("PARSEK_AUTORUN_TESTS", "unset"),
                       env.get("PARSEK_AUTORUN_EXIT", "0"), int(run_budget)))

        # ---- DRIVE + BUDGET ----------------------------------------------
        drive = drive_seam(spec, instance_dir, run_save_name, proc, runtime, logger,
                           run_budget, mission_ctx=mission_ctx, run_id=run_id)

        # ---- VERIFY ------------------------------------------------------
        facts = run_verifiers(spec, instance_dir, run_save_name, drive, runtime, logger,
                              seed_capture=seed_capture, run_id=run_id)
        driver_facts = facts["driver"]
        # NOTE (N4): v1 flags boot-crash-repeated on ANY second boot-crash. The S7
        # boot-crash SIGNATURE compare (exit code + last KSP.log lines) that would
        # distinguish a deterministic boot crash from two unrelated boot flakes is
        # DEFERRED; here a second consecutive boot-crash attempt is treated as
        # repeated regardless of signature.
        if drive.boot_crashed and prior_boot_crashed:
            driver_facts["boot_crash_repeated"] = True

        # ---- CLASSIFY ----------------------------------------------------
        ef = spec.get("expectedFail", {}) or {}
        bug_id = ef.get("bugId", "") or ""
        ef_subkind = ef.get("subkind", "") or ""
        base = hlib.classify_verdict(driver_facts, facts["verifiers"], {}, attempt,
                                     (spec.get("retry", {}) or {}).get("policy", "once"))
        # Signature match (S2): expectedFail.subkind narrows the demotion to one
        # PARSEK-FAIL class; an empty subkind falls back to bugId-only matching (any
        # PARSEK-FAIL demotes), warned here at demotion time so the bugId-only scope
        # is visible in the log.
        signature_matched = hlib.expected_fail_signature_matched(
            base.verdict, base.subkind, ef_subkind)
        if bug_id and base.verdict == hlib.VERDICT_PARSEK_FAIL and not ef_subkind:
            logger.warn("Classify", "expected-fail bugId=%s has no subkind; matching on bugId only (any PARSEK-FAIL demotes to EXPECTED-FAIL)"
                        % bug_id)
        verdict = hlib.classify_expected_fail(base, bug_id, signature_matched)
        logger.info("Classify", "verdict=%s scenario=%s attempt=%d reason=%s"
                    % (verdict.verdict, scenario_id, attempt, verdict.reason))
        if bug_id:
            logger.info("Classify", "expected-fail bugId=%s matched=%s"
                        % (bug_id, verdict.expected_fail_matched))
        if verdict.verdict == hlib.VERDICT_XPASS:
            logger.warn("Classify", "XPASS bugId=%s scenario=%s: confirm bug closed, remove expectedFail key"
                        % (bug_id, scenario_id))

        return _finish_result(spec, profile, attempt, started, start_wall, runtime,
                              verdict, admission_rec, drive, facts, run_save_name,
                              instance_dir, logger, run_id=run_id)
    finally:
        release_run_lock(lock_path)


def _terminal_result(spec, profile, attempt, started, start_wall, runtime, verdict,
                     admission, logger, run_id, drive=None, facts=None):
    """Build + write a result for an early-refusal path (no launch / no verify)."""
    return _finish_result(spec, profile, attempt, started, start_wall, runtime, verdict,
                          admission, drive, facts, None, None, logger, run_id=run_id)


def _finish_result(spec, profile, attempt, started, start_wall, runtime, verdict,
                   admission, drive, facts, run_save_name, instance_dir, logger,
                   run_id: Optional[str] = None) -> Dict:
    scenario_id = spec.get("id")
    ended = utcnow_iso()
    wall = int(runtime.now() - start_wall)
    if run_id is None:
        run_id = _make_run_id(scenario_id, attempt)

    steps_rec = []
    if drive is not None:
        ev = hlib.evaluate_response_stream(drive.response_lines, drive.steps_with_ids)
        for o in ev.steps:
            steps_rec.append({"cmd": o.cmd, "id": o.step_id, "expect": o.expect,
                              "verdict": o.verdict, "met": o.met})
        # M-B1: fold the mission-kind step in as a driver.steps row (design "Mission
        # result" row shape), inserted in id order so it reads inline with the seam
        # steps. verdict is the mission verdict on a met step, else "INVALID".
        m = drive.mission_step
        if m is not None:
            mrow = {"phase": "mission", "id": m["id"], "expect": m["expect"],
                    "verdict": m["missionVerdict"] if m["met"] else hlib.VERDICT_INVALID,
                    "missionVerdict": m["missionVerdict"], "met": m["met"],
                    "subkind": m["subkind"] or None}
            if m.get("reason"):
                mrow["reason"] = m["reason"]
            steps_rec.append(mrow)
            steps_rec.sort(key=lambda s: s["id"])
    driver_rec = {"steps": steps_rec,
                  "allExpectedMet": all(s["met"] for s in steps_rec) if steps_rec else False}

    verifiers_detail = facts["detail"] if facts else {}
    ef = spec.get("expectedFail", {}) or {}

    killed = bool(drive and drive.killed)
    exit_code = drive.exit_code if drive else None

    # collect-logs on non-PASS (design results layout / edge 18).
    collect = {"ran": False, "path": None}
    if verdict.verdict != hlib.VERDICT_PASS and run_save_name and instance_dir:
        res = runtime.run_collect_logs(scenario_id, run_save_name, instance_dir,
                                       COLLECT_LOGS_TIMEOUT_SECONDS)
        if res.ok:
            collect = {"ran": True, "path": _extract_collect_path(res.stdout)}
            logger.info("Collect", "collect-logs label=%s -> %s" % (scenario_id, collect["path"]))
        else:
            logger.error("Collect", "collect-logs failed: exit=%s; snapshot degraded" % res.exit_code)

    result = {
        "schema": hlib.SCHEMA_VERSION,
        "runId": run_id,
        "scenarioId": scenario_id,
        "tier": spec.get("tier"),
        "instanceProfile": profile,
        "startedUtc": started,
        "endedUtc": ended,
        "wallSeconds": wall,
        "attempt": attempt,
        "verdict": verdict.verdict,
        "subkind": verdict.subkind,
        "note": verdict.note,
        "admission": admission,
        "driver": driver_rec,
        "verifiers": verifiers_detail,
        "expectedFail": {"bugId": ef.get("bugId", "") or "",
                         "matched": verdict.expected_fail_matched},
        "kspExit": {"code": exit_code, "killed": killed},
        "collectLogs": collect,
    }
    write_result(result, logger)
    return result


def _extract_collect_path(stdout: str) -> Optional[str]:
    for line in (stdout or "").splitlines():
        line = line.strip()
        if line and (os.sep in line or "/" in line) and "logs" in line.lower():
            return line
    return None


# ---------------------------------------------------------------------------
# Result + summary persistence (design edge 10: never lose a verdict).
# ---------------------------------------------------------------------------


def write_result(result: Dict, logger: HarnessLogger) -> None:
    os.makedirs(RESULTS_DIR, exist_ok=True)
    text = hlib.serialize_result(result)
    path = os.path.join(RESULTS_DIR, "%s.json" % result["runId"])
    try:
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        os.replace(tmp, path)
        logger.info("Result", "result written %s" % path)
    except OSError as exc:
        # Degrade to a .pending fallback, then to stdout, so a verdict is never lost.
        try:
            pending = os.path.join(RESULTS_DIR, ".pending")
            os.makedirs(pending, exist_ok=True)
            with open(os.path.join(pending, "%s.json" % result["runId"]), "w",
                      encoding="utf-8", newline="\n") as fh:
                fh.write(text)
            logger.error("Result", "result write to %s failed: %s; wrote .pending" % (path, exc))
        except OSError:
            logger.error("Result", "result write failed: %s; emitted to stdout+log" % exc)
            print(text)

    summary = "%s %s %s attempt=%d wall=%ds%s" % (
        result["endedUtc"], result["verdict"], result["scenarioId"], result["attempt"],
        result["wallSeconds"], (" note=%s" % result["note"]) if result.get("note") else "")
    try:
        with open(os.path.join(RESULTS_DIR, "summary.txt"), "a", encoding="utf-8") as fh:
            fh.write(summary + "\n")
    except OSError:
        pass


# ---------------------------------------------------------------------------
# Coverage + flake refresh (design Coverage + flake generation, via hlib).
# ---------------------------------------------------------------------------


def load_all_results() -> List[Dict]:
    out = []
    if not os.path.isdir(RESULTS_DIR):
        return out
    for name in sorted(os.listdir(RESULTS_DIR)):
        if not name.endswith(".json"):
            continue
        try:
            with open(os.path.join(RESULTS_DIR, name), "r", encoding="utf-8") as fh:
                obj = json.load(fh)
        except (OSError, ValueError):
            continue
        ok, _ = hlib.check_schema(obj)
        if ok:
            out.append(obj)
    return out


def refresh_coverage_and_flake(specs: Sequence[Dict], registry: Dict,
                               logger: HarnessLogger) -> None:
    results = load_all_results()
    report = hlib.compute_coverage(specs, results, registry)
    os.makedirs(COVERAGE_DIR, exist_ok=True)
    with open(os.path.join(COVERAGE_DIR, "coverage.json"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(hlib.coverage_to_json_obj(report), sort_keys=True, indent=2) + "\n")
    with open(os.path.join(COVERAGE_DIR, "coverage.txt"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(hlib.coverage_to_txt(report))

    # Flake: per scenario (v1 stage = "run"), a rolling window of attempt outcomes.
    prior = {}
    flake_path = os.path.join(COVERAGE_DIR, "flake.json")
    if os.path.isfile(flake_path):
        try:
            with open(flake_path, "r", encoding="utf-8") as fh:
                prior = {k: v.get("quarantined", False)
                         for k, v in (json.load(fh).get("scenarios", {}) or {}).items()}
        except (OSError, ValueError):
            prior = {}
    by_scenario: Dict[str, List[Dict]] = {}
    for r in results:
        sid = r.get("scenarioId")
        if sid is None:
            continue
        # SF1: one result contributes its own verdict entry PLUS, for a PASS that
        # recovered a subprocess-scoped verifier flake, a synthetic INVALID entry -- so
        # a recovered flake accrues toward quarantine exactly like a whole-attempt
        # flakedThenPassed's attempt-1 INVALID JSON does.
        by_scenario.setdefault(sid, []).extend(hlib.flake_attempt_entries(r))
    now = utcnow_iso()
    flake_out = {"schema": hlib.SCHEMA_VERSION, "scenarios": {}}
    for sid, attempts in sorted(by_scenario.items()):
        fr = hlib.compute_flake(attempts, now=now, prior_quarantined=prior.get(sid, False))
        flake_out["scenarios"][sid] = {"stage": "run", "total": fr.total,
                                       "numerator": fr.numerator, "rate": fr.rate,
                                       "quarantined": fr.quarantined}
        if fr.quarantined:
            logger.warn("Coverage", "flake quarantine scenario=%s stage=run rate=%.2f over 7d"
                        % (sid, fr.rate))
    with open(flake_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(flake_out, sort_keys=True, indent=2) + "\n")

    logger.info("Coverage", "coverage: values=%d covered=%d uncovered=%d expectedFail=%d xpass=%d"
                % (report.rollup["values"], report.rollup["covered"], report.rollup["uncovered"],
                   report.rollup["expectedFailValues"], report.rollup["xpass"]))


# ---------------------------------------------------------------------------
# CLI + orchestration.
# ---------------------------------------------------------------------------


def build_select_expr(args) -> Optional[str]:
    if args.id:
        return "--id %s" % args.id
    if args.tier:
        return "--tier %s" % args.tier
    if args.tag:
        return "--tag %s" % args.tag
    if args.cadence:
        return "--cadence %s" % args.cadence
    return None


def print_dry_run_plan(selected: Sequence[Dict], instance_root_fn, logger: HarnessLogger) -> None:
    print("")
    print("=== DRY-RUN ACTION PLAN: %d scenario(s) ===" % len(selected))
    for spec in selected:
        sid = spec.get("id")
        profile = spec.get("instanceProfile")
        inst = instance_root_fn(profile)
        fixture = spec.get("fixture", {}) or {}
        driver = spec.get("driver", {}) or {}
        steps = driver.get("steps", []) or []
        is_autopilot = driver.get("kind") == "autopilot"
        print("  [SELECT ] %s tier=%s profile=%s kind=%s"
              % (sid, spec.get("tier"), profile, driver.get("kind")))
        print("  [ADMIT  ] read %s/GameData/Parsek/provision-manifest.json" % inst)
        # M-B1: an autopilot spec ALSO admits the mission venv at pre-launch ADMIT
        # (terminal INVALID(tooling-venv) with no KSP boot); surface it in the plan.
        if is_autopilot:
            print("  [VENV-ADMIT] mission=%s stamp=%s vs %s (terminal tooling-venv on drift/missing; no KSP boot)"
                  % (driver.get("mission"), VENV_STAMP_PATH, REQUIREMENTS_PATH))
        print("  [STAGE  ] template=%s inject=%s craft=%d"
              % (fixture.get("saveTemplate"), fixture.get("injectedRecordings"),
                 len(fixture.get("craft", []) or [])))
        # M-B2: a ledger scenario captures the seed baseline pre-launch (analyzer
        # over the staged template, redirected OUT of the save tree).
        exp = spec.get("expectations", {}) or {}
        ledger_block = exp.get("ledger")
        world_block = exp.get("world")
        if ledger_block is not None and (ledger_block.get("seedFrom", "template") == "template"):
            print("  [SEED   ] analyzer over staged template -> seed baseline "
                  "(careerSave block; redirect out of save tree; terminal INVALID on edge-15 fixture/tooling fault)")
        print("  [LAUNCH ] %s/KSP_x64.exe budget=%ss"
              % (inst, (spec.get("runtime", {}) or {}).get("budgetSeconds")))
        for i, step in enumerate(steps):
            if step.get("phase") == "mission":
                # M-B1 handoff: spawn the mission SUBPROCESS with the venv python
                # (no channel traffic); bounded by the mission-step budget.
                print("  [MISSION] step=%d handoff mission=%s expect=%s budget=%s (venv-python subprocess; kRPC autopilot)"
                      % (i, driver.get("mission"), step.get("expect", hlib.MISSION_STEP_EXPECT),
                         step.get("budget", "-")))
            else:
                print("  [DRIVE  ] step=%d cmd=%s expect=%s budget=%s"
                      % (i, step.get("cmd"), step.get("expect", "OK"), step.get("budget", "-")))
        verify_line = ("  [VERIFY ] driverValidity, batchComplete, analyzer(-FreshSaveGate), "
                       "logValidate, results, anomalySweep, expectations")
        if ledger_block is not None or world_block is not None:
            verify_line += (", ledgerOracle(manifest-capture + oracle diff -> PARSEK-FAIL(ledger) on hard drift)")
        print(verify_line)
        print("")
    print("=== end plan ===")
    print("")


def run(argv: Optional[Sequence[str]] = None, runtime: Optional[Runtime] = None) -> int:
    parser = argparse.ArgumentParser(
        description="M-A5 automated-testing harness orchestrator",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "exit-code contract (N8):\n"
            "  0  every selected scenario terminated PASS or EXPECTED-FAIL (green)\n"
            "  1  at least one PARSEK-FAIL, INVALID, KILLED, or XPASS. XPASS exits 1\n"
            "     as SCHEDULER-AMBER: the run passed but an expected-fail guard now\n"
            "     passes and a human must confirm the bug is closed + remove the key,\n"
            "     so it must not read green to a scheduler.\n"
            "  2  no scenario selection given (also argparse's own bad-argument code)\n"))
    sel = parser.add_mutually_exclusive_group()
    sel.add_argument("--id", help="run one scenario by id")
    sel.add_argument("--tier", help="run all specs of a tier (perpr|daily|nightly|weekly)")
    sel.add_argument("--tag", help="run every spec carrying this tag")
    sel.add_argument("--cadence", help="run the tier set a cadence maps to (per-pr|daily|nightly|weekly)")
    parser.add_argument("--dry-run", action="store_true", help="print the action plan; launch nothing")
    parser.add_argument("--umbrella-root", help="override the umbrella root (default: parent of the worktree)")
    parser.add_argument("--instance-dir", help="override the resolved instance dir (single-profile runs / tests)")
    parser.add_argument("--no-coverage", action="store_true", help="skip the coverage/flake refresh")
    args = parser.parse_args(argv)

    runtime = runtime or Runtime()
    umbrella_root = os.path.abspath(args.umbrella_root) if args.umbrella_root else DEFAULT_UMBRELLA_ROOT
    logger = HarnessLogger(default_harness_log_path())

    registry = load_registry()
    specs = load_all_specs()

    expr = build_select_expr(args)
    if expr is None:
        logger.error("Select", "no selection given (use --id / --tier / --tag / --cadence)")
        return 2
    selected = hlib.select_scenarios(specs, expr)
    logger.info("Select", "select expr='%s' -> %d scenarios: %s"
                % (expr, len(selected), [s.get("id") for s in selected]))

    def instance_root_fn(profile):
        return resolve_instance_dir(profile, umbrella_root, args.instance_dir) or "<unresolved>"

    if args.dry_run:
        print_dry_run_plan(selected, instance_root_fn, logger)
        return 0

    # Validate every selected spec; an invalid spec is SKIPPED with an INVALID-SPEC
    # result (never launches KSP), so one broken spec cannot abort the batch.
    bug_ids = _load_bug_ids()
    exit_code = 0
    ran_any = False
    for spec in selected:
        # M-B1 spec admission: resolve the autopilot mission ref SHELL-SIDE (read
        # the mission's declared schema toml + confirm the mission .py exists) and
        # hand the parsed registry to the pure validator. A missing schema / missing
        # mission .py is a spec-invalid INVALID (no KSP boot for a mission that
        # cannot run). Non-autopilot specs get (None, []) -- unchanged seam path.
        mission_schemas, mission_errors = resolve_mission_schemas(spec, logger)
        validation = hlib.validate_spec(spec, registry, bug_ids, mission_schemas)
        for w in validation.warnings:
            logger.warn("Select", "spec warning id=%s: %s" % (spec.get("id"), w))
        all_errors = list(validation.errors) + mission_errors
        if all_errors:
            logger.warn("Select", "spec invalid id=%s reasons=%s"
                        % (spec.get("id"), all_errors))
            _write_invalid_spec_result(spec, all_errors, runtime, logger)
            exit_code = 1
            continue

        profile = spec.get("instanceProfile")
        instance_dir = resolve_instance_dir(profile, umbrella_root, args.instance_dir)
        if instance_dir is None:
            logger.error("Admit", "cannot resolve instance dir for profile=%s" % profile)
            exit_code = 1
            continue

        ran_any = True
        terminal = _run_scenario_with_retry(spec, instance_dir, umbrella_root, runtime, logger)
        if terminal["verdict"] in (hlib.VERDICT_PARSEK_FAIL, hlib.VERDICT_INVALID,
                                   hlib.VERDICT_KILLED, hlib.VERDICT_XPASS):
            exit_code = max(exit_code, 1)

    if ran_any and not args.no_coverage:
        refresh_coverage_and_flake(specs, registry, logger)

    logger.close()
    return exit_code


def _run_scenario_with_retry(spec, instance_dir, umbrella_root, runtime, logger) -> Dict:
    retry_policy = (spec.get("retry", {}) or {}).get("policy", "once")
    attempts: List[hlib.Verdict] = []
    last_result = None
    prior_boot_crashed = False
    for attempt in (1, 2):
        result = run_attempt(spec, instance_dir, umbrella_root, runtime, attempt,
                             prior_boot_crashed, logger)
        last_result = result
        v = hlib.Verdict(result["verdict"], result.get("subkind", ""), False,
                         "", result.get("expectedFail", {}).get("matched", False),
                         result.get("note", ""))
        attempts.append(v)
        prior_boot_crashed = (result.get("subkind") == "boot-crash")
        if not hlib.should_retry(v, attempt, retry_policy):
            break
        logger.info("Retry", "retry scenario=%s attempt=2 reason=%s"
                    % (spec.get("id"), result["verdict"]))

    terminal = hlib.resolve_terminal(attempts)
    if terminal.note == "flakedThenPassed" and last_result is not None:
        last_result["note"] = "flakedThenPassed"
        write_result(last_result, logger)
        logger.info("Classify", "verdict=PASS scenario=%s reason=flakedThenPassed (attempt-1 INVALID)"
                    % spec.get("id"))
    return last_result


def _write_invalid_spec_result(spec, errors, runtime, logger) -> None:
    scenario_id = spec.get("id") or os.path.basename(spec.get("_path", "unknown")).replace(".toml", "")
    started = utcnow_iso()
    result = {
        "schema": hlib.SCHEMA_VERSION,
        "runId": "%s_%s" % (datetime.now(timezone.utc).strftime("%Y-%m-%d_%H%M"), scenario_id),
        "scenarioId": scenario_id,
        "tier": spec.get("tier"),
        "instanceProfile": spec.get("instanceProfile"),
        "startedUtc": started,
        "endedUtc": started,
        "wallSeconds": 0,
        "attempt": 1,
        "verdict": hlib.VERDICT_INVALID,
        "subkind": "spec-invalid",
        "note": "",
        "admission": {"admitted": False, "subkind": "spec-invalid", "diff": []},
        "driver": {"steps": [], "allExpectedMet": False},
        "verifiers": {"specValidation": {"status": "FAIL", "errors": errors}},
        "expectedFail": {"bugId": (spec.get("expectedFail", {}) or {}).get("bugId", "") or "",
                         "matched": False},
        "kspExit": {"code": None, "killed": False},
        "collectLogs": {"ran": False, "path": None},
    }
    write_result(result, logger)


def _load_bug_ids() -> List[str]:
    """Best-effort scrape of resolvable todo-doc bug ids (design: expectedFail.bugId
    is a WARN, not a hard fail, so a missing doc never blocks a run)."""
    doc = os.path.join(WORKTREE_ROOT, "docs", "dev", "todo-and-known-bugs.md")
    if not os.path.isfile(doc):
        return []
    import re
    ids = set()
    try:
        with open(doc, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                for m in re.findall(r"\b([A-Z]\d+[A-Za-z0-9-]*)\b", line):
                    ids.add(m)
    except OSError:
        return []
    return sorted(ids)


def main(argv: Optional[Sequence[str]] = None) -> int:
    return run(argv)


if __name__ == "__main__":
    sys.exit(main())
