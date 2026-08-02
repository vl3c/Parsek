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
from typing import Dict, List, Optional, Sequence, Set, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
LIB_DIR = os.path.join(HERE, "lib")
PROVISION_DIR = os.path.join(HERE, "provision")
for _p in (LIB_DIR, PROVISION_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import oracle  # noqa: E402
import provlib  # noqa: E402
import saveparse  # noqa: E402

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
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")

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


def settings_sidecar_path(instance_dir: str) -> str:
    """The instance-wide Parsek settings sidecar SetSetting writes through."""
    return os.path.join(instance_dir, *hlib.SETTINGS_SIDECAR_RELPATH)


def reset_settings_sidecar(instance_dir: str, logger: HarnessLogger, phase: str) -> bool:
    """Write the deterministic tracers-OFF baseline into the instance's Parsek
    settings sidecar. Returns True when the file is left holding the baseline.

    WHY (see hlib's section comment for the full contract): `SetSetting` on any of
    the eight sidecar-tracked settings persists INSTANCE-WIDE, and Parsek applies
    the sidecar OVER each loaded save, so without this one scenario's
    `mapRenderTracing=true` silently pins the per-frame render tracer on for every
    later run on the instance. Called TWICE per attempt - at STAGE (so the run
    starts from a declared state) and at TEARDOWN in the per-attempt finally (so
    the instance is left clean even when the budget watchdog killed KSP). Both
    calls write the SAME baseline rather than restoring a captured prior state:
    restoring the prior state would faithfully preserve a previous run's
    contamination, which is the bug.

    Never raises: a sidecar the harness could not write is a WARNED degradation,
    not a reason to fail a scenario that has nothing to do with it. A run whose
    teardown never executes at all (the harness process itself killed) is healed
    by the next run's stage call.
    """
    path = settings_sidecar_path(instance_dir)
    prior_text = ""
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                prior_text = fh.read()
        except OSError:
            prior_text = ""
    was_on = hlib.settings_sidecar_tracers_on(prior_text)
    baseline = hlib.render_settings_sidecar_baseline()
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        tmp = path + ".harness-tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(baseline)
        os.replace(tmp, path)
    except Exception as exc:  # noqa: BLE001 - see below
        # Deliberately BROAD: the teardown call runs inside run_attempt's finally,
        # where ANY escaping exception would replace the attempt's real verdict
        # with a settings-file error. Housekeeping must never be the reason a
        # result is lost, so every failure degrades to a warning.
        logger.warn("Settings", "settings-sidecar reset FAILED phase=%s path=%s (%s: %s); "
                                "a stale tracer flag may leak into this or a later run"
                    % (phase, path, type(exc).__name__, exc))
        return False
    logger.info("Settings", "settings-sidecar baseline written phase=%s tracers=off%s"
                % (phase,
                   (" (cleared leaked: %s)" % ",".join(was_on)) if was_on else ""))
    return True


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


def _inject_postcondition_missing(save_dir: str, preset: str) -> List[str]:
    """The artifacts a fixture-injection preset MUST have written into the staged
    save, as a list of what is MISSING (empty = postcondition holds).

    This is the fail-closed half of HARNESS-INJECT-FAILS-OPEN: the injector's exit
    code proves only that a process ran, and `dotnet test --no-build` against a
    never-built assembly exits 0 having injected nothing (measured, S1.5 attempt 1
    + both V1-map-dwell flights). The check is mechanism-independent - whatever the
    injection subprocess returned, either the fixture exists on disk or it does not.

    - ``all-synthetic`` -> a non-empty ``Parsek/Recordings/``.
    - ``rewind-b9``     -> the same, plus ``Parsek/RewindPoints/rp_b9_root.sfs``
      (the RP every rewind-b9 consumer's ``InvokeRewind rp=rp_b9_root`` needs)."""
    missing: List[str] = []
    rec_dir = os.path.join(save_dir, "Parsek", "Recordings")
    try:
        has_recordings = os.path.isdir(rec_dir) and bool(os.listdir(rec_dir))
    except OSError:
        has_recordings = False
    if not has_recordings:
        missing.append("non-empty Parsek/Recordings/")
    if preset == "rewind-b9":
        rp = os.path.join(save_dir, "Parsek", "RewindPoints", "rp_b9_root.sfs")
        if not os.path.isfile(rp):
            missing.append("Parsek/RewindPoints/rp_b9_root.sfs")
    return missing


def stage_fixture(spec: Dict, instance_dir: str, runtime: Runtime,
                  logger: HarnessLogger) -> Tuple[bool, str, str]:
    """Stage the scenario's fixture. Returns (ok, run_save_name, subkind); subkind
    is "" on success, "spec-invalid" on a containment violation (a runSaveName that
    escapes saves/), "staging" on a missing template, or "stage-inject-noop" when a
    requested fixture injection left no fixture on disk (fail-closed postcondition;
    the run never boots KSP)."""
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
    # (1b) reap prior PreParsekBackup siblings (career-fixtures review F2):
    # a fixture with NO Parsek footprint (the fresh-* ledger saves) triggers
    # Parsek's pre-Parsek backup on every run's cold OnLoad, and the backup
    # lands as a saves-root SIBLING "<name> (pre-Parsek <ts>)" outside the
    # run-save leaf -- unbounded Load-menu clutter without this reap. Never
    # pre-seed the backup marker instead: that would create the very Parsek
    # footprint the fresh-contact scenarios exclude.
    if os.path.isdir(saves_dir):
        backup_prefix = run_save_name + " (pre-Parsek"
        for entry in os.listdir(saves_dir):
            if entry.startswith(backup_prefix):
                backup_dir = os.path.join(saves_dir, entry)
                if os.path.isdir(backup_dir) and _is_strictly_inside(backup_dir, saves_dir):
                    shutil.rmtree(backup_dir, ignore_errors=True)
                    logger.verbose("Stage", "reaped prior pre-Parsek backup: %s" % entry)
    os.makedirs(saves_dir, exist_ok=True)
    shutil.copytree(template_abs, target_save)

    # (3) inject synthetic recordings when requested (recording OFF by construction).
    #
    # FAIL CLOSED ON THE POSTCONDITION, not the exit code (HARNESS-INJECT-FAILS-OPEN,
    # found by S1.5 attempt 1 and reconfirmed by two full V1-map-dwell flights). The
    # injector's `dotnet test --filter Inject* --no-build` against a never-built test
    # assembly exits 0 having run NOTHING (measured), and its KSP.log lock probe
    # refuses through the same reports-success path - so `res.ok` proved the process
    # exited, never that a fixture exists. The miss then surfaced minutes later as an
    # unrelated seam rejection (`invokerewind refused: unknown-rp`), classified
    # driver-INVALID against a correct spec, after a full KSP boot (or a full flight).
    # Asserting what the preset MUST have written is mechanism-independent: it catches
    # every way an injection can no-op, pre-boot, with the cause named. The subkind is
    # NOT in RETRYABLE_INVALID_SUBKINDS on purpose - the miss is deterministic (same
    # worktree, same result), so a retry burns the `once` budget to learn nothing.
    inj = fixture.get("injectedRecordings", "none")
    if inj in ("all-synthetic", "rewind-b9"):
        res = runtime.run_inject(instance_dir, run_save_name, INJECT_TIMEOUT_SECONDS, preset=inj)
        if not res.ok:
            logger.warn("Stage", "inject-recordings failed preset=%s exit=%s"
                        % (inj, res.exit_code))
        missing = _inject_postcondition_missing(target_save, inj)
        if missing:
            assembly = os.path.join(WORKTREE_ROOT, "Source", "Parsek.Tests",
                                    "bin", "Debug", "net472", "Parsek.Tests.dll")
            cause = ("Parsek.Tests assembly missing (never built in this worktree; the "
                     "injector's deliberate --no-build runs nothing) - remedy: "
                     "dotnet build Source/Parsek.Tests"
                     if not os.path.isfile(assembly)
                     else "injector exited without writing the fixture (assembly present; "
                          "check the KSP.log lock probe and the injector output)")
            logger.error("Stage", "inject postcondition failed preset=%s exit=%s missing=[%s]; "
                                  "aborting pre-boot (INVALID stage-inject-noop). likely cause: %s"
                         % (inj, res.exit_code, ", ".join(missing), cause))
            return False, run_save_name, "stage-inject-noop"

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

    # (6) pin the instance-wide Parsek settings sidecar to the tracers-OFF
    # baseline so this run starts from a DECLARED tracer state instead of
    # inheriting whichever SetSetting a previous scenario persisted. The matching
    # teardown write lives in run_attempt's finally.
    reset_settings_sidecar(instance_dir, logger, "stage")

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
        # The mission's OWN wallSeconds, read out of the mission-result JSON the
        # mission step already parses (audit finding G6). None when the mission
        # never ran / wrote no readable result.
        self.mission_wall_seconds: Optional[float] = None
        # The tail steps NOT driven because the mission step came back UNMET (design
        # "The unmet-mission tail"). Each row: id / cmd / role / reason. Empty on every
        # other path, including a MISSION-OK run and a seam-only driver, so a normal
        # result record is byte-identical to what it was before this seam existed.
        self.skipped_tail_steps: List[Dict] = []
        # True IFF a mission came back UNMET while the spec's skipTailOnUnmetMission was
        # false, i.e. the full tail was driven BY POLICY. Without this the durable record
        # of such a run is indistinguishable from one where the policy never applied
        # (both have an empty skipped list), and the opt-out would live only in the
        # harness log.
        self.tail_skip_opted_out = False
        # HARNESS-MIDMISSION-COMMIT-BYPASS: what the mission subprocess wrote into the
        # seam channel on its own account (route 1). REPORT-ONLY -- see hlib's section
        # comment. None on every seam-only driver and on any run whose channel could not
        # be read, so a normal result record is unchanged.
        self.mid_mission_seam_writes: Optional[hlib.MidMissionSeamWrites] = None
        # Set instead when the channel could not be read/parsed, so an unreadable channel
        # records a GAP rather than being indistinguishable from "the mission wrote nothing".
        self.mid_mission_seam_read_error: Optional[str] = None


def _read_mission_result(result_path: str) -> Optional[Dict]:
    """The mission-result JSON as a dict, or None when the file is absent /
    unparseable / carries the WRONG schema. The ``schema`` gate (design Backward
    Compatibility: "a schema bump makes the harness refuse an old artifact ...
    rather than mis-parse") makes run.py refuse a result whose top-level
    ``schema`` is not the one it understands, so a future/legacy mission-result
    shape is treated as unreadable rather than silently mis-read."""
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
    return obj


def _read_mission_verdict(result_path: str) -> Optional[str]:
    """The mission result's ``verdict`` string, or None when the result is
    absent / unreadable / carries no string verdict (design edge 12: a missing
    or unreadable result fails closed via hlib.classify_mission_step(None))."""
    obj = _read_mission_result(result_path)
    if obj is None:
        return None
    v = obj.get("verdict")
    return v if isinstance(v, str) else None


def _read_mission_wall_seconds(result_path: str) -> Optional[float]:
    """The mission's OWN measured wall span, for the harness-vs-mission residue
    (audit finding G6).

    ``harness wallSeconds - missionWallSeconds`` is the whole non-flight cost of
    a run in one subtraction: KSP boot, LoadGame, the SetSetting steps, the
    verifier chain, FlushAndQuit, collect-logs. The audit MEASURED that residue
    at a stable 40-67 s across 16 archived runs (KSP boot ~35 s + verifier chain
    ~10 s), which is why the seven individual call sites are deliberately NOT
    instrumented: the subtraction already bounds them, and every extra timer is
    a permanent maintenance cost for a quantity that does not move. If the
    residue ever climbs past ~120 s, THAT is the signal to go instrument the
    call sites -- not before."""
    obj = _read_mission_result(result_path)
    if obj is None:
        return None
    wall = obj.get("wallSeconds")
    if isinstance(wall, (int, float)) and not isinstance(wall, bool):
        return float(wall)
    return None


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
    load_id = hlib.step_id_for_index(load_index)
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
    # Mid-mission command-seam bridge (route 1, section 3.2 of the B-DOCK design):
    # hand the mission the two channel file paths (at the instance root =
    # mission_ctx.cwd) + a RESERVED command-id. The reserved id is the mission
    # step's OWN step_id -- the mission step consumes an index but writes NOTHING
    # to the channel, so its id is free and collision-safe against the surrounding
    # driver.steps ids (which use their own indices), and monotonic (the
    # mid-mission commit fires chronologically between the pre- and post-mission
    # seam steps). Missions that never emit ACTION_PARSEK_COMMIT_TREE (B1/B2/B4/
    # B5/B7/forge) ignore these entirely.
    args += ["--seam-commands", os.path.join(mission_ctx.cwd, "parsek-test-commands.txt"),
             "--seam-responses", os.path.join(mission_ctx.cwd, "parsek-test-responses.txt"),
             "--seam-commit-id", step_id]
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
            # Read the channel BEFORE the early return -- this path used to skip the
            # instrument entirely, losing its highest-value case (a world-mutating write
            # with no verdict yet), and the mission's own stdout is block-buffered and
            # routinely lost when the process is killed.
            #
            # DO NOT hardcode mission_met=False here, however obvious "a killed mission has
            # no verdict" sounds. `poll_exit` is checked BEFORE the budget, so the live
            # window is "result already written, process not yet reaped" - the ordinary
            # shape for a long mission near its wall. The mission-budget branch immediately
            # below exists for exactly that reason and does this same final read.
            # Hardcoding False made the record claim `exposedAfterUnmetMission` and the log
            # say "returned UNMET" over a mission that had already written MISSION-OK - and
            # this path never sets result.mission_step, so nothing else in the record
            # contradicts it.
            killed_verdict = _read_mission_verdict(result_path)
            killed_met, _ = hlib.classify_mission_step(killed_verdict)
            _record_mid_mission_seam_writes(result, mission_ctx, step_id, killed_met, logger)
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
    # G6: carry the mission's own wall span up to the harness result so the
    # harness-vs-mission residue is a subtraction, not an investigation.
    result.mission_wall_seconds = _read_mission_wall_seconds(result_path)
    if result.mission_wall_seconds is not None:
        logger.info("Mission", "mission wall=%.0fs (harness residue = run "
                               "wallSeconds - this: KSP boot + verifier chain)"
                    % result.mission_wall_seconds)
    _record_mid_mission_seam_writes(result, mission_ctx, step_id, met, logger)
    _log_handoff_return(logger, step_index, verdict, met, subkind or "")
    return False


def _record_mid_mission_seam_writes(result: "DriveResult", mission_ctx, reserved_id: str,
                                    mission_met: bool, logger: HarnessLogger) -> None:
    """HARNESS-MIDMISSION-COMMIT-BYPASS (report-only): read the route-1 writes.

    The mission subprocess writes into the SAME channel run.py drives, under the
    reserved id handed to it at spawn -- so the channel file IS driver-side evidence
    and no mission-side change is needed to read it.

    Failure-isolated: an unreadable channel must never move a verdict. But it must
    not be SILENT either. This instrument exists precisely because a mid-mission
    write was invisible, and a bare `except OSError: pass` reproduced that
    invisibility -- an unreadable channel used to leave a record byte-identical to
    "the mission wrote nothing", intermittently and without a trace. So the failure
    is warned AND recorded, and `Exception` is caught rather than `OSError` alone
    (the read and the parse both live under it now; an instrument may not cost an
    attempt its result).

    Called from the normal exit path AND from the run-budget kill path: a mission
    killed mid-flight is BY CONSTRUCTION a mission with no verdict at all, which is
    the very shape this measures, and the early `return True` used to skip it.
    """
    try:
        with open(os.path.join(mission_ctx.cwd, "parsek-test-commands.txt"),
                  "r", encoding="utf-8", errors="replace") as fh:
            channel_text = fh.read()
        writes = hlib.parse_mid_mission_seam_writes(channel_text, reserved_id, mission_met)
    except Exception as exc:                                   # noqa: BLE001 - instrument
        result.mid_mission_seam_read_error = "%s: %s" % (type(exc).__name__, exc)
        logger.warn("Mission", "mid-mission seam writes UNREADABLE (%s); this run's "
                               "midMissionSeamWrites is a GAP, not a zero"
                    % result.mid_mission_seam_read_error)
        return
    result.mid_mission_seam_writes = writes
    if writes.exposed:
        logger.warn("Mission", "mid-mission seam writes: %s" % writes.summary)
    elif writes.total:
        logger.info("Mission", "mid-mission seam writes: %s" % writes.summary)


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
    skip_tail = hlib.spec_skips_tail_on_unmet_mission(spec)
    tail_plan: Optional[hlib.UnmetTailPlan] = None

    for i, step in enumerate(steps):
        step_id = hlib.step_id_for_index(i)
        # M-B1 handoff (design "The handoff"): a phase=mission step is NOT written to
        # the channel; run.py spawns the mission subprocess and bounded-waits it, then
        # drives the remaining seam steps. On a MET mission the whole tail runs; on an
        # UNMET one only the CLEANUP tail runs (design "The unmet-mission tail").
        if step.get("phase") == "mission":
            load_ok = _preceding_loadgame_ok(steps, i, responses_path)
            killed = _drive_mission_step(result, step, step_id, i, proc, runtime,
                                         logger, run_budget, run_start, mission_ctx, run_id,
                                         preceding_load_ok=load_ok)
            if killed:
                return result
            if result.mission_step is not None and not result.mission_step.get("met"):
                tail_plan = hlib.plan_unmet_mission_tail(steps, i, skip_tail=skip_tail)
                result.tail_skip_opted_out = not tail_plan.skip_enabled
                log = logger.info if not tail_plan.skipped_indices else logger.warn
                log("Drive", "mission UNMET verdict=%s subkind=%s: %s"
                    % (result.mission_step.get("missionVerdict") or "<no-result>",
                       result.mission_step.get("subkind") or "-", tail_plan.summary))
            continue
        if tail_plan is not None and i in tail_plan.skipped_indices:
            role = hlib.seam_verb_tail_role(step.get("cmd", ""))
            row = {"id": step_id, "cmd": step.get("cmd", ""), "role": role,
                   "reason": "mission-unmet"}
            result.skipped_tail_steps.append(row)
            # Bounded (a driver has well under 20 steps), so log per skipped step: the
            # KSP.log / harness log is the only place the omission is auditable.
            logger.info("Drive", "drive step=%d id=%s cmd=%s SKIPPED role=%s reason=mission-unmet "
                                 "(no channel line written)"
                        % (i, step_id, row["cmd"], role))
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


def read_save_structure(save_dir: str) -> Optional["saveparse.ParsekSaveSnapshot"]:
    """Read + parse the produced save's persistent.sfs for the M-C2 save-parse
    verifier. Thin I/O only - every parse decision is saveparse.py. Returns None
    when the file is missing/unreadable (a DEFINED fault the evaluator names;
    never read a missing save as zero rows)."""
    sfs_path = os.path.join(save_dir, "persistent.sfs")
    if not os.path.isfile(sfs_path):
        return None
    try:
        with open(sfs_path, "r", encoding="utf-8", errors="replace") as fh:
            return saveparse.parse_parsek_scenario(fh.read())
    except OSError:
        return None


def grep_anomaly_tokens(log_text: str) -> List[str]:
    # Thin delegate: the matching is a DECISION and lives in hlib (anchored on the
    # tracers' `phase=Anomaly ... reason=<token>` raise shape, not a bare substring
    # search over the whole log).
    return hlib.grep_anomaly_tokens(log_text)


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
        # SANDBOX carve-out (career-fixtures review, resolution (a)): a
        # template with NO career pools is a VALID seed IFF the declared
        # manifest is EMPTY -- L1-passive-sandbox's whole point is proving
        # the facet-skip path + the trusted empty-manifest cross-check over
        # a pool-less save (compute_expected yields all-None, the diff
        # facet-skips; test_passive_sandbox_empty_manifest_expected_equals_
        # absent_seed proves the math). Any scenario with a NONZERO manifest
        # still INVALIDs here: an expected delta needs a pool to land in.
        declared = ledger_block.get("manifest") or []
        if not declared:
            logger.info("Seed", "ledger-seed: pool-less template accepted (empty declared manifest; sandbox facet-skip contract)")
            return SeedCapture(oracle.parse_seed_baseline(block), "ok", block)
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
            "provenance": e.provenance, "rec3Row": e.rec3_row,
            "utWindow": (list(e.ut_window) if e.ut_window is not None else None),
            "stockReason": list(e.stock_reasons)}


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
        # explained by a seam entry is an unexpected stock award.
        #
        # HARD ONLY WHEN THE SCENARIO ARMS IT (`captureCrossCheck = "gate"`, declared
        # by exactly ONE committed spec since 2026-07-31: `CL-2-pod-impact-ledger`,
        # armed over three flights against the real game - see known-gate 3; every
        # other spec is still report-only).
        #
        # Until the 2026-07-29 pattern rewrite this loop had an
        # always-empty input - STOCK_AWARD_PATTERNS matched shapes no KSP build emits -
        # so the hard drift it wrote was unreachable. Turning a working capture and a
        # live gate on in one step would red scenarios against an award baseline nobody
        # has measured, so the mode knob decides and the default reports.
        #
        # WHAT AN ARMING OPERATOR IS ACTUALLY UP AGAINST (corrected 2026-07-29): the
        # capture sees REPUTATION ONLY. KSP logs no funds and no science award line, so
        # the three milestone FUNDS awards a career pad hop trips are invisible here -
        # they move the produced save (where the seam-declared-vs-save diff catches
        # them) but can never surface as unexpected captured awards. The only
        # undeclared awards a career flight can produce on this path are the stock
        # `Progression` rep awards. That makes the baseline far smaller than the
        # original deferral assumed, and a scenario that declares its rep effects can
        # realistically arm the gate.
        cross_check_hard = hlib.capture_cross_check_gates(ledger_block)
        # The corroboration key is (seqKey, facet, amount-within-tolerance), NOT kind:
        # a captured kind is generic and a seam kind is a scenario semantic, so joining
        # on kind made every award - including the scenario's own declared one - read
        # unexpected. Pass the RUN's tolerances so the reputation window is the same
        # one the diff uses (a seam entry is NOMINAL, a stock rep line is post-curve).
        capture_tol = {"funds": tol.funds, "science": tol.science,
                       "reputation": tol.reputation}
        for c in hlib.unmatched_captured_awards(seam_entries, captured, capture_tol):
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
                identity=(c.contract_guid or c.subject_id or c.reason or ""),
                expected=None, parsed=c.amount, ut_window=(aw, aw),
                hard=cross_check_hard,
                detail="unexpected stock award kind=%s facet=%s reason=%s amount=%r ut=%s "
                       "seqKey=%r crossCheck=%s line=%r"
                       % (c.kind, c.facet, c.reason or "(none)", c.amount, c.ut, c.seq_key,
                          "gate" if cross_check_hard else "report", c.raw_line)))
            logger.warn("Verify", "manifest-capture: unexpected stock award ut=%s kind=%s "
                                  "reason=%s hard=%s line='%s'"
                        % (c.ut, c.kind, c.reason or "(none)", cross_check_hard, c.raw_line))

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
    # `crossCheck=` is UNCONDITIONAL on purpose. It used to be emitted only inside
    # the per-unmatched-award loop, so a run with zero unmatched awards left NO
    # archived trace of whether the check was armed - the armed CL-2 flight
    # `2026-07-31_1645` produced a verifier block byte-identical to the report-mode
    # run before it, and "armed and flown green" rested on the spec's state at the
    # time rather than on evidence. A positive, grep-stable token beats an absence
    # proof; the next arming session can cite the log instead of the narrative.
    cross_mode = ((ledger_block or {}).get(hlib.LEDGER_CAPTURE_CROSS_CHECK_KEY, "report")
                  if ledger_block is not None else "n/a")
    logger.info("Verify", "verify ledgerOracle status=%s hardDivergences=%d reportOnly=%d crossCheck=%s"
                % (result["status"], result["hardDivergences"], result["reportOnly"], cross_mode))
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
    mission_outcome_unmet = False
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
        # The EVA-4 fail-open closure (2026-07-25). The carve-out above keeps every
        # post-mission RECORDING step non-gating on driver validity; what it must NOT
        # do is drop a post-mission OUTCOME step's verdict on the floor. That verdict
        # is the run's only channel onto the world state the mission handed off, and
        # on EVA-4 flight 3 it was the ONLY thing that saw the kerbal die. Recorded as
        # its own verifier row and classified PARSEK-FAIL(mission-outcome) so it reds
        # structurally, with no dependence on the spec author's log-token regexes.
        outcome_unmet = hlib.first_unmet_post_mission_outcome(ev.steps, mission_id)
        gating_verbs = [o.cmd for o in ev.steps
                        if str(o.step_id) > str(mission_id)
                        and hlib.post_mission_step_gates(o.cmd)]
        # Only a MET mission reaches the classifier: an unmet mission is already
        # driver-INVALID with its own subkind, and re-reporting its skipped/failed tail
        # as an outcome miss would mask the mission's own reason.
        is_flight_outcome, outcome_driver_subkind = (
            hlib.classify_post_mission_outcome_miss(outcome_unmet)
            if (outcome_unmet is not None and mission["met"]) else (False, ""))
        mission_outcome_unmet = is_flight_outcome
        # A refusal / tooling / never-answered miss is a DRIVER fault, classified exactly
        # as the same fault would be pre-mission, rather than being blamed on the mod.
        if outcome_driver_subkind:
            driver_valid = False
            stage_subkind = outcome_driver_subkind
        if not mission["met"]:
            outcome_status = "SKIPPED"
        elif outcome_unmet is not None:
            outcome_status = "FAIL"
        elif not gating_verbs:
            # NOT "PASS": this row checked nothing. Every autopilot scenario but EVA-4
            # lands here, and reading a blank check as a pass is how a future edit that
            # DROPS the gating step (disarming the gate entirely) goes unnoticed.
            outcome_status = "SKIPPED"
        else:
            outcome_status = "PASS"
        detail["missionOutcome"] = {
            "status": outcome_status,
            "reason": "" if gating_verbs else "no-gating-verbs",
            "gatingVerbs": gating_verbs,
            "firstUnmet": (None if outcome_unmet is None else {
                "id": outcome_unmet.step_id, "cmd": outcome_unmet.cmd,
                "expect": outcome_unmet.expect, "verdict": outcome_unmet.verdict,
                "msg": outcome_unmet.msg,
                "flightOutcome": is_flight_outcome,
                "driverSubkind": outcome_driver_subkind}),
        }
        logger.info("Verify", "verify missionOutcome status=%s gating=%d firstUnmet=%s"
                    % (outcome_status, len(gating_verbs),
                       "-" if outcome_unmet is None
                       else "%s(%s) verdict=%s msg=%s -> %s"
                            % (outcome_unmet.cmd, outcome_unmet.step_id,
                               outcome_unmet.verdict, outcome_unmet.msg or "-",
                               "mission-outcome" if is_flight_outcome
                               else "driver:%s" % outcome_driver_subkind)))
    detail["driverValidity"] = {
        "status": "PASS" if driver_valid else ("SKIPPED" if killed else "FAIL"),
        "allExpectedMet": ev.all_expected_met, "subkind": stage_subkind,
    }
    # allMet covers the steps actually DRIVEN, so on a tail-skipped run it can read true
    # next to status=FAIL (the mission, not a seam step, is what failed). Name the skipped
    # count inline so that pairing reads as designed rather than as a contradiction.
    logger.info("Verify", "verify driverValidity status=%s allMet=%s%s subkind=%s%s"
                % (detail["driverValidity"]["status"], ev.all_expected_met,
                   (" (over the %d driven step(s); %d tail step(s) skipped)"
                    % (len(ev.steps), len(drive.skipped_tail_steps)))
                   if drive.skipped_tail_steps else "",
                   stage_subkind or "-",
                   (" missionVerdict=%s" % mission["missionVerdict"]) if mission else ""))

    # The unmet-mission tail skip, recorded next to the verifier rows it EXPLAINS: on
    # this path the analyzer is triage-only and logValidate / testResults /
    # anomalySweep / expectations / ledgerOracle are all SKIPPED on `not driver_valid`,
    # so the produced save is deliberately incomplete (no committed tree) and no
    # verifier reads it as ground truth. Without this row a reader of the result JSON
    # would have to reconstruct WHY the save has no committed recording.
    if drive.skipped_tail_steps:
        detail["unmetMissionTail"] = {
            "status": "SKIPPED", "reason": "mission-unmet",
            "skippedSteps": [dict(r) for r in drive.skipped_tail_steps],
        }
        logger.info("Verify", "verify unmetMissionTail skipped=%d [%s]; the produced save is "
                               "deliberately incomplete and every save-reading verifier is "
                               "triage-only/SKIPPED on driver-invalid"
                    % (len(drive.skipped_tail_steps),
                       ", ".join("%s:%s" % (r["id"], r["cmd"]) for r in drive.skipped_tail_steps)))

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
        # False on every seam-only driver (no mission step -> every step already gates
        # through all_expected_met) and on every autopilot run whose post-mission
        # outcome steps all answered.
        "mission_outcome_unmet": mission_outcome_unmet,
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
        # The save-parse verifier is SKIPPED on any KILLED attempt: a torn save is
        # never ground truth (design edge 5), and a half-written persistent.sfs is
        # exactly the file this row must not read counts off. Full key set on
        # every branch so a consumer never KeyErrors on the row shape.
        detail["saveParse"] = {
            "status": "SKIPPED", "reason": "killed", "gating": False,
            "blocks": [], "armedBlocks": [], "mismatches": [], "observed": {},
            "parsed": None, "parseError": "", "scenarioFound": None}
        # The raw-Unity-exception scan DOES run on a killed attempt, triage-only. What
        # a watchdog kill tears is the SAVE, not the log - and an exception storm is a
        # leading suspect for the hang that got the process killed, so this is the run
        # whose count an operator most wants. Never gating here regardless of a
        # declared block (KILLED precedes every verifier flag in classify_verdict, and
        # a torn attempt must not be judged on a ceiling).
        killed_ue = hlib.evaluate_unity_exceptions(hlib.scan_unity_exceptions(log_text), None)
        detail["unityExceptions"] = {"status": hlib.UNITY_EXCEPTIONS_STATUS_REPORT,
                                     "gating": False, "total": killed_ue.total,
                                     "counts": dict(killed_ue.counts),
                                     "maxTotal": None, "mismatches": [],
                                     "reason": "killed-triage-only"}
        logger.info("Verify", "verify unityExceptions status=REPORT (killed-triage-only) "
                              "total=%d counts=%s" % (killed_ue.total, dict(killed_ue.counts)))
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
        # Per-token RAISE COUNTS: the input the `{ token, maxCount }` budget form
        # needs. Recorded unconditionally (even with no budget declared) so an
        # operator sizing a future ceiling reads the number off a green run instead
        # of guessing it.
        hit_counts = hlib.count_anomaly_tokens(log_text)
        unallowed = hlib.evaluate_anomaly_sweep(hits, allowed, hit_counts)
        # REPORT-ONLY: anomaly reasons the mod raised that the harness token set
        # does not carry, so the known ANOMALY_TOKENS drift is visible per-run
        # rather than a silent fail-open. Never affects the verdict.
        unlisted = hlib.unlisted_anomaly_reasons(log_text)
        verifiers["anomaly_hit"] = bool(unallowed)
        detail["anomalySweep"] = {"status": "FAIL" if unallowed else "PASS",
                                  "hits": unallowed, "allowed": list(allowed),
                                  "hitCounts": hit_counts,
                                  "unlistedReasons": unlisted}
        if unallowed:
            short_circuited = True
        logger.info("Verify", "verify anomalySweep status=%s hits=%s counts=%s"
                    % (detail["anomalySweep"]["status"], unallowed, hit_counts))
        if unlisted:
            logger.warn("Verify", "anomalySweep saw %d raise(s) with reason(s) NOT in the "
                                  "harness token set (REPORT-ONLY, not gating): %s"
                        % (len(unlisted), unlisted))
    else:
        detail.setdefault("anomalySweep", {"status": "SKIPPED", "reason": "short-circuit"})

    # 6b. Raw Unity exception scan. REPORT-ONLY unless the scenario declares
    # [expectations.unityExceptions] (none do), mirroring how
    # anomalySweep.unlistedReasons surfaces a blind spot without moving a verdict.
    # It exists because NOTHING else in the chain reads a line Parsek did not write:
    # the forbidden tokens are all `[Parsek]`-shaped and validate-ksp-log parses only
    # `[Parsek]` lines, so a NullReferenceException storm has always passed silently.
    if driver_valid and not short_circuited:
        ue = hlib.evaluate_unity_exceptions(
            hlib.scan_unity_exceptions(log_text),
            expectations.get(hlib.UNITY_EXCEPTIONS_BLOCK))
        verifiers["unity_exceptions_over_budget"] = (ue.status == "FAIL")
        detail["unityExceptions"] = {"status": ue.status, "gating": ue.gating,
                                     "total": ue.total, "counts": dict(ue.counts),
                                     "maxTotal": ue.max_total,
                                     "mismatches": list(ue.mismatches)}
        if ue.status == "FAIL":
            short_circuited = True
        logger.info("Verify", "verify unityExceptions status=%s gating=%s total=%d counts=%s"
                    % (ue.status, ue.gating, ue.total, dict(ue.counts)))
        if ue.total and not ue.gating:
            logger.warn("Verify", "unityExceptions saw %d raw Unity exception line(s) "
                                  "(REPORT-ONLY, not gating; arm with "
                                  "[expectations.unityExceptions] maxTotal = N): %s"
                        % (ue.total, dict(ue.counts)))
    else:
        detail.setdefault("unityExceptions",
                          {"status": "SKIPPED", "reason": "short-circuit"})

    # 7. Expectations manifest.
    recording_count = count_recordings(save_dir)
    if driver_valid and not short_circuited:
        exp = hlib.evaluate_expectations(expectations, recording_count, log_text)
        verifiers["expectation_mismatch"] = (exp.status != "PASS")
        detail["expectations"] = {"status": exp.status, "mismatches": list(exp.mismatches),
                                  "reserved": list(exp.reserved),
                                  "observed": dict(exp.observed)}
        logger.info("Verify", "verify expectations status=%s mismatches=%d"
                    % (exp.status, len(exp.mismatches)))
    else:
        # The MEASURED facets ride the SKIPPED branch TOO. The whole point of
        # `observed` is that a green run's numbers survive into
        # results/<runId>.json instead of dying with the transient save -- and a
        # run that short-circuited or came back driver-INVALID is exactly the
        # run whose recording count an operator most wants (did the save have
        # ANY recordings? did a killed run get partway?). recording_count is
        # already computed above; recording it costs nothing and omitting it
        # made the number absent precisely where it was needed.
        detail.setdefault("expectations",
                          {"status": "SKIPPED", "reason": "short-circuit",
                           "observed": hlib.observed_expectation_facets(
                               recording_count)})

    # 7b. Save-parse structural verifier (M-C2 / R9). Parses the produced save's
    # ParsekScenario SCENARIO surfaces (RECORDING_TREE topology, supersede rows,
    # tombstones, rewind points) and evaluates [expectations.rewind] +
    # [expectations.recordings.structure] + [expectations.recordings.points].
    # REPORT-ONLY by default (VERDICT
    # NEUTRALITY: S4.1 already declares a rewind block, so a gating default would
    # move a committed nightly's verdict with no live run to prove the readings);
    # a block opts in with gating = true - declared by exactly ONE committed
    # spec, S4.1-rewind-merge, armed 2026-07-31 after its report-only reading run
    # (guarded by an ALLOWLIST test-suite sweep, so a second declarer still reds).
    # This branch CAN move that scenario's verdict; it moves no other spec's.
    # Like the ledger oracle it runs independent
    # of the later-verifier short-circuit (a structural read of the save is its
    # own triage signal), but only over a driver-VALID save (a driver-INVALID
    # save is deliberately incomplete, not ground truth - the facets are still
    # recorded for triage, the row stays SKIPPED).
    snapshot = read_save_structure(save_dir)
    sp_parsed = None if snapshot is None else bool(snapshot.parsed)
    sp_error = "missing persistent.sfs" if snapshot is None else snapshot.error
    sp_found = None if (snapshot is None or not snapshot.parsed) else snapshot.scenario_found
    if not driver_valid:
        detail["saveParse"] = {
            "status": "SKIPPED", "reason": "driver-invalid", "gating": False,
            "blocks": [], "armedBlocks": [], "mismatches": [],
            "observed": saveparse.observed_structure_facets(snapshot),
            "parsed": sp_parsed, "parseError": sp_error, "scenarioFound": sp_found}
        logger.info("Verify", "verify saveParse status=SKIPPED reason=driver-invalid")
    else:
        sp = saveparse.evaluate_save_structure(expectations, snapshot)
        if sp.gating:
            verifiers["save_structure_mismatch"] = (sp.status == saveparse.STATUS_FAIL)
        detail["saveParse"] = {
            "status": sp.status, "reason": "", "gating": sp.gating,
            "blocks": list(sp.blocks), "armedBlocks": list(sp.armed_blocks),
            "mismatches": list(sp.mismatches), "observed": dict(sp.observed),
            "parsed": sp_parsed, "parseError": sp_error,
            "scenarioFound": sp.scenario_found,
        }
        rewind_obs = (sp.observed.get("rewind") or {})
        # Gate 12: the recorded-POINTS distribution rides the SAME line. It is
        # recorded unconditionally (no block needed), so an operator sizing a
        # first window can read it straight off a green run's console output
        # instead of digging the number out of results/<runId>.json.
        points_obs = ((sp.observed.get("recordings") or {}).get("points") or {})
        logger.info("Verify", "verify saveParse status=%s gating=%s blocks=%s armed=%s "
                              "scenarioFound=%s supersedeRows=%s tombstones=%s "
                              "rewindPoints=%s pointsTotal=%s pointsLargest=%s "
                              "pointsSmallest=%s pointsUnparsed=%s mismatches=%d"
                    % (sp.status, sp.gating, list(sp.blocks) or "-",
                       list(sp.armed_blocks) or "-", sp.scenario_found,
                       rewind_obs.get("supersedeRows", "-"),
                       rewind_obs.get("tombstones", "-"),
                       rewind_obs.get("rewindPoints", "-"),
                       points_obs.get("total", "-"),
                       points_obs.get("largest", "-"),
                       points_obs.get("smallest", "-"),
                       points_obs.get("unparsed", "-"), len(sp.mismatches)))
        report_only = [m for m in sp.mismatches if m not in sp.armed_mismatches]
        if report_only:
            logger.warn("Verify", "saveParse recorded %d report-only mismatch(es) "
                                  "(not gating; arm with gating = true inside the "
                                  "declared block after reading report-only runs): %s"
                        % (len(report_only), report_only))

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


def _run_id_stamp() -> str:
    """The runId's UTC minute stamp. A seam: the smoke test pins it to drive two
    runs into ONE minute and prove both keep their results."""
    return datetime.now(timezone.utc).strftime(hlib.RUN_ID_TIMESTAMP_FORMAT)


def _claimed_run_ids() -> Set[str]:
    """Run ids results/ already holds an artifact for (I/O half of
    ``hlib.claimed_run_ids``). Reads the ``.pending`` fallback dir too -- a
    verdict that degraded to .pending is still a run's evidence."""
    claimed: Set[str] = set()
    for directory in (RESULTS_DIR, os.path.join(RESULTS_DIR, ".pending")):
        try:
            claimed |= hlib.claimed_run_ids(os.listdir(directory))
        except OSError:
            continue
    return claimed


def _resolve_run_id(scenario_id: str, attempt: int, run_ordinal: int,
                    logger: Optional[HarnessLogger]) -> hlib.RunIdResolution:
    """Resolve this attempt's runId against what results/ already holds, and SAY
    SO when the base id was taken.

    Two runs of one scenario started inside the same minute used to share an id,
    and the second overwrote the first's result JSON, _shots dir (KSP.log
    included) and contact sheet with nothing warning. The resolution never
    overwrites; the Warn is what makes the collision impossible to miss in the
    per-invocation harness log."""
    res = hlib.resolve_run_id(_run_id_stamp(), scenario_id, _claimed_run_ids(),
                              attempt=attempt, min_ordinal=run_ordinal)
    if res.collided and logger is not None:
        logger.warn("Result", "runId collision: %s is already claimed in results/; "
                              "this run writes %s instead (%d id%s stepped over; the "
                              "earlier run's result JSON, _shots artifacts and contact "
                              "sheet are kept)"
                    % (res.collided_with[0], res.run_id, len(res.collided_with),
                       "" if len(res.collided_with) == 1 else "s"))
    return res


# Hang guard on the resolve-stake-re-resolve loop (see _make_run_id). Set far
# above any real contention: staking loses a pass only to a genuinely concurrent
# invocation, and the scan sees that id on the very next pass.
_RUN_ID_CLAIM_ATTEMPTS = 100


def _try_claim_run_id(run_id: str) -> bool:
    """Stake ``results/<runId>.claim`` atomically. ``open(..., "x")`` is
    O_CREAT|O_EXCL on both NTFS and POSIX -- the one primitive that closes the
    window between RESOLVING an id and writing the first artifact under it.

    That window is real: two concurrent run.py invocations of one scenario both
    resolve before either writes anything, and the per-instance run lock does not
    serialize them (the refused one writes its INVALID(instance-locked) record
    immediately, while the winner is still flying).

    The stake is never reaped. A claim left by a run that died is CORRECT: that
    id was issued, and re-issuing it is exactly the overwrite this guards. The
    files are empty-ish and results/ already keeps every result JSON forever.

    Returns False ONLY when the id itself was already staked. An I/O failure
    returns True -- a results dir that cannot be written to must not block a run
    (design edge 10: never lose a verdict); the run then falls back to scan-only
    resolution, which is still strictly better than the unguarded id.

    The two failures are kept in SEPARATE try blocks deliberately: makedirs over
    a path that exists as a FILE raises FileExistsError too, and folding that in
    with the stake's would report "already staked" forever and spin the caller's
    re-resolve loop."""
    try:
        os.makedirs(RESULTS_DIR, exist_ok=True)
    except OSError:
        return True
    path = os.path.join(RESULTS_DIR, "%s%s" % (run_id, hlib.RUN_ID_CLAIM_SUFFIX))
    try:
        with open(path, "x", encoding="utf-8", newline="\n") as fh:
            fh.write(utcnow_iso() + "\n")
        return True
    except FileExistsError:
        return False
    except OSError:
        return True


def _make_run_id(scenario_id: str, attempt: int, run_ordinal: int = 1,
                 logger: Optional[HarnessLogger] = None) -> str:
    """The per-attempt runId (design "Mission result": ATTEMPT-SUFFIXED so each
    retry writes its own <runId>_mission.json). Computed ONCE per attempt in
    run_attempt so the mission-result filename the handoff writes/reads matches the
    runId the durable result JSON records, even if the minute rolls over during a
    long flight.

    Resolves against results/ and then STAKES the id, re-resolving from the next
    ordinal if a concurrent invocation staked it first. Each pass starts strictly
    above an ordinal now taken on disk, and the bounded loop is a HANG GUARD, not
    a collision cap: a stake that reports "taken" for a reason unrelated to the
    id (the makedirs-over-a-file shape `_try_claim_run_id` calls out) would
    otherwise spin an unattended nightly forever. Exhausting it degrades to the
    scan-only id -- loudly."""
    ordinal = run_ordinal
    res = None
    for _ in range(_RUN_ID_CLAIM_ATTEMPTS):
        res = _resolve_run_id(scenario_id, attempt, ordinal, logger)
        if _try_claim_run_id(res.run_id):
            return res.run_id
        if logger is not None:
            logger.warn("Result", "runId %s was claimed by a concurrent run between "
                                  "resolving and staking it; re-resolving above "
                                  "ordinal %d" % (res.run_id, res.ordinal))
        ordinal = res.ordinal + 1
    if logger is not None:
        logger.error("Result", "runId staking failed %d times for scenario=%s; "
                               "falling back to the scan-only id %s (results/ may be "
                               "unwritable -- an artifact overwrite is possible)"
                     % (_RUN_ID_CLAIM_ATTEMPTS, scenario_id, res.run_id))
    return res.run_id


def run_attempt(spec: Dict, instance_dir: str, umbrella_root: str, runtime: Runtime,
                attempt: int, prior_boot_crashed: bool, logger: HarnessLogger,
                run_ordinal: int = 1) -> Dict:
    scenario_id = spec.get("id")
    profile = spec.get("instanceProfile")
    started = utcnow_iso()
    start_wall = runtime.now()
    run_id = _make_run_id(scenario_id, attempt, run_ordinal, logger)

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
            reason = {
                "spec-invalid": "staged target escaped saves/ (containment guard)",
                "stage-inject-noop": "fixture injection wrote no fixture "
                                     "(fail-closed staging postcondition)",
            }.get(stage_subkind, "fixture staging failed")
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
            # R5: the isolated arm is a separate var, never a selector prefix -- the
            # selector string is consumed verbatim as the `category=` token both the
            # runner stamps and hlib's anti-vacuity probe synthesizes, so a prefix
            # would desynchronize the two and silently weaken that gate. Set ONLY on
            # the autorun path; a RunTests-step spec carries the flag as a wire arg
            # on the step itself (validate_spec forbids declaring both).
            if autorun.get(hlib.BATCH_ISOLATED_KEY):
                env["PARSEK_AUTORUN_ISOLATED"] = "1"
        env.pop("PARSEK_ANALYZER_BASELINE_MODE", None)  # never set at KSP launch
        run_budget = float((spec.get("runtime", {}) or {}).get("budgetSeconds", 600))
        exe = runtime.resolve_exe(instance_dir)
        proc = runtime.launch(exe, [], env, instance_dir)
        logger.info("Launch", "launch exe=%s pid=%s env=[TEST_COMMANDS=1 AUTORUN=%s EXIT=%s ISOLATED=%s] batchIsolated=%s budget=%ds"
                    % (exe, proc.pid, env.get("PARSEK_AUTORUN_TESTS", "unset"),
                       env.get("PARSEK_AUTORUN_EXIT", "0"),
                       env.get("PARSEK_AUTORUN_ISOLATED", "0"),
                       hlib.spec_batch_isolated(spec), int(run_budget)))

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
        # Leave the instance's tracer settings in the declared baseline state no
        # matter HOW this attempt ended - PASS, verifier red, budget-watchdog
        # process-tree kill, or an exception on any path after the lock. Without
        # this a scenario's `SetSetting mapRenderTracing=true` stays persisted
        # instance-wide and every later flight silently pays the per-frame tracer
        # cost and gets gated by an anomaly sweep it never declared.
        reset_settings_sidecar(instance_dir, logger, "teardown")
        release_run_lock(lock_path)


def _terminal_result(spec, profile, attempt, started, start_wall, runtime, verdict,
                     admission, logger, run_id, drive=None, facts=None):
    """Build + write a result for an early-refusal path (no launch / no verify)."""
    return _finish_result(spec, profile, attempt, started, start_wall, runtime, verdict,
                          admission, drive, facts, None, None, logger, run_id=run_id)


# ---------------------------------------------------------------------------
# V3 always-collect artifacts + contact sheet (design-testing-unified section 6,
# V3). The heavy non-PASS collect-logs snapshot is UNCHANGED; this is the light
# UNCONDITIONAL step -- a green run is exactly the run a human wants to scan, and
# before this nothing durable survived a PASS but the result JSON. Both steps are
# failure-isolated by construction: artifact or sheet trouble degrades to a Warn
# and NEVER touches the verdict (verdict-neutrality is the V3 contract).
# ---------------------------------------------------------------------------


_EMPTY_ARTIFACTS: Dict = {"ran": False, "shotsDir": None, "kspLog": False,
                          "kspLogTruncated": False, "screenshots": 0,
                          "screenshotsSkipped": 0}


def _copy_bounded(src, out, limit: int) -> int:
    """Stream at most ``limit`` bytes from ``src`` to ``out``; returns bytes
    copied. The BOUND is the point (review MINOR 3): the source KSP.log can
    GROW between the size snapshot and the copy (a not-fully-reaped KSP child
    still appending at high warp), and an unbounded copyfileobj would then
    bust the cap and falsify the truncation marker."""
    copied = 0
    while copied < limit:
        chunk = src.read(min(1024 * 1024, limit - copied))
        if not chunk:
            break
        out.write(chunk)
        copied += len(chunk)
    return copied


def _collect_run_artifacts(run_id: str, instance_dir: Optional[str],
                           run_start_epoch: float, logger: HarnessLogger) -> Dict:
    """Copy the run's KSP.log (bounded, hlib.plan_artifact_log_copy) and any
    run-window Screenshots/ images (hlib.select_run_screenshots) into
    ``results/<runId>_shots/``, then run the shots-dir retention pass
    (hlib.select_shots_dirs_to_prune). Returns the additive ``artifacts``
    result block."""
    artifacts: Dict = dict(_EMPTY_ARTIFACTS)
    if not instance_dir:
        return artifacts
    try:
        shots_dir = os.path.join(RESULTS_DIR, "%s_shots" % run_id)
        os.makedirs(shots_dir, exist_ok=True)
        artifacts["ran"] = True
        artifacts["shotsDir"] = os.path.basename(shots_dir)

        # (1) KSP.log, bounded. Oversize keeps head + tail with an explicit
        # marker line (rationale on the hlib constants). Written tmp+rename
        # (review NIT 9) so a mid-copy death never leaves a TORN KSP.log that
        # the sheet would then render as the run's authoritative key lines.
        # EVERY copy leg is byte-bounded (review MINOR 3): the source can grow
        # under us, and the bound is what keeps the artifact <= cap + marker.
        log_src = os.path.join(instance_dir, "KSP.log")
        if os.path.isfile(log_src):
            size = os.path.getsize(log_src)
            plan = hlib.plan_artifact_log_copy(size)
            dst = os.path.join(shots_dir, "KSP.log")
            tmp = dst + ".harness-tmp"
            with open(log_src, "rb") as src, open(tmp, "wb") as out:
                if plan.copy_all:
                    copied = _copy_bounded(src, out, hlib.ARTIFACT_LOG_CAP_BYTES)
                    if src.read(1):
                        # The file outgrew its copy-all snapshot past the cap
                        # mid-copy; say so instead of silently cutting.
                        out.write(("\n[harness-artifact] KSP.log grew past the "
                                   "%d-byte cap during the copy; remainder dropped\n"
                                   % hlib.ARTIFACT_LOG_CAP_BYTES).encode("utf-8"))
                else:
                    _copy_bounded(src, out, plan.head_bytes)
                    out.write(("\n[harness-artifact] KSP.log TRUNCATED for the "
                               "contact-sheet copy: original %d bytes, kept first "
                               "%d + last %d (hlib.ARTIFACT_LOG_CAP_BYTES)\n"
                               % (size, plan.head_bytes, plan.tail_bytes)).encode("utf-8"))
                    src.seek(max(plan.head_bytes, size - plan.tail_bytes))
                    _copy_bounded(src, out, plan.tail_bytes)
                    if src.read(1):
                        # Review NEW-2: the tail region was computed from the
                        # size SNAPSHOT; a source that grew mid-copy means the
                        # kept slice is NOT the real end of the log. Say so --
                        # a reader must not trust a mid-log slice as the
                        # teardown/BATCH_COMPLETE tail.
                        out.write(("\n[harness-artifact] KSP.log GREW during the "
                                   "copy (snapshot %d bytes); the kept tail region "
                                   "is from the snapshot, NOT the real end of the "
                                   "log\n" % size).encode("utf-8"))
                    artifacts["kspLogTruncated"] = True
            os.replace(tmp, dst)
            artifacts["kspLog"] = True

        # (2) Screenshots stamped inside this run's wall-clock window (the
        # instance dir accumulates across runs; V4's capture verbs will feed
        # this -- today it is usually empty and that is fine).
        screenshots_dir = os.path.join(instance_dir, "Screenshots")
        if os.path.isdir(screenshots_dir):
            candidates = []
            for name in sorted(os.listdir(screenshots_dir)):
                path = os.path.join(screenshots_dir, name)
                if os.path.isfile(path):
                    try:
                        st = os.stat(path)
                        candidates.append((name, st.st_mtime, st.st_size))
                    except OSError:
                        continue
            selected, prior, over_cap = hlib.select_run_screenshots(
                candidates, run_start_epoch)
            copy_missed = 0
            for name in selected:
                try:
                    shutil.copy2(os.path.join(screenshots_dir, name), shots_dir)
                    artifacts["screenshots"] += 1
                except OSError:
                    copy_missed += 1
            # skipped = this run's captures that did NOT land in the snapshot
            # (cap-dropped or copy-failed); prior-run files are not "skipped",
            # they belong to older runs and are only logged.
            artifacts["screenshotsSkipped"] = over_cap + copy_missed
            if prior or over_cap or copy_missed:
                logger.verbose("Artifacts", "screenshots skipped: %d prior-run, %d over-cap, %d copy-failed"
                               % (prior, over_cap, copy_missed))

        logger.info("Artifacts", "artifacts collected run=%s kspLog=%s truncated=%s screenshots=%d -> %s"
                    % (run_id, artifacts["kspLog"], artifacts["kspLogTruncated"],
                       artifacts["screenshots"], shots_dir))
    except Exception as exc:  # noqa: BLE001 - failure isolation by design (V3)
        logger.warn("Artifacts", "artifact collection FAILED run=%s (%s: %s); "
                                 "snapshot degraded, verdict unaffected"
                    % (run_id, type(exc).__name__, exc))
    # (3) Retention (review MAJOR 1): results/ is gitignored and nothing else
    # ever prunes it, so the heavy *_shots dirs are bounded here -- newest-first
    # keep window, this run's own dir always protected. The KB-scale history
    # (result JSONs, summary, contact HTML -- which already embeds the extracted
    # key lines as text) is never touched. In its OWN try (review NEW-5): a
    # FAILED copy is exactly when pruning matters most (disk full fails the
    # copy; skipping the prune would then keep the disk full run after run).
    try:
        _prune_shots_dirs("%s_shots" % run_id, logger)
    except Exception as exc:  # noqa: BLE001 - failure isolation by design (V3)
        logger.warn("Artifacts", "shots-dir retention FAILED run=%s (%s: %s); "
                                 "verdict unaffected" % (run_id, type(exc).__name__, exc))
    return artifacts


def _dir_size_bytes(path: str) -> int:
    total = 0
    for root, _dirs, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                continue
    return total


def _prune_shots_dirs(protect_name: str, logger: HarnessLogger) -> None:
    """Remove the oldest ``results/*_shots`` dirs past the retention budget
    (hlib.select_shots_dirs_to_prune; the current run's dir is always kept)."""
    entries = []
    for name in os.listdir(RESULTS_DIR):
        if not name.endswith("_shots"):
            continue
        path = os.path.join(RESULTS_DIR, name)
        if not os.path.isdir(path):
            continue
        try:
            mtime = os.path.getmtime(path)
        except OSError:
            continue
        entries.append((name, mtime, _dir_size_bytes(path)))
    prune = hlib.select_shots_dirs_to_prune(entries, protect_name=protect_name)
    for name in prune:
        shutil.rmtree(os.path.join(RESULTS_DIR, name), ignore_errors=True)
    if prune:
        logger.info("Artifacts", "retention pruned %d old shots dir(s) "
                                 "(keep newest %d dirs / %d MiB total): %s"
                    % (len(prune), hlib.ARTIFACT_SHOTS_KEEP_DIRS,
                       hlib.ARTIFACT_SHOTS_MAX_TOTAL_BYTES // (1024 * 1024),
                       ", ".join(prune[:5]) + (" ..." if len(prune) > 5 else "")))


_contact_sheet_module = None


def _load_contact_sheet_module():
    """Load harness/tools/contact_sheet.py by FILE PATH -- no sys.path mutation
    (review NIT 11: an insert(0) of tools/ would let a future
    ``harness/tools/<stdlib-name>.py`` silently shadow the stdlib for the rest
    of the process). An already-imported ``contact_sheet`` (the smoke tests
    import it from tools/ to monkeypatch) is reused, so a test's patch and
    run.py always see the SAME module object."""
    global _contact_sheet_module
    if _contact_sheet_module is not None:
        return _contact_sheet_module
    existing = sys.modules.get("contact_sheet")
    if existing is not None:
        _contact_sheet_module = existing
        return existing
    import importlib.util
    path = os.path.join(TOOLS_DIR, "contact_sheet.py")
    spec = importlib.util.spec_from_file_location("contact_sheet", path)
    if spec is None or spec.loader is None:
        raise ImportError("cannot load contact_sheet from %s" % path)
    module = importlib.util.module_from_spec(spec)
    sys.modules["contact_sheet"] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop("contact_sheet", None)
        raise
    _contact_sheet_module = module
    return module


def _generate_contact_sheet(run_id: str, logger: HarnessLogger) -> None:
    """Emit ``results/<runId>_contact.html`` + refresh ``results/index.html``
    via harness/tools/contact_sheet.py. Failure-isolated: any trouble (module
    missing, disk full, malformed inputs) is a Warn, never a verdict change."""
    try:
        contact_sheet = _load_contact_sheet_module()
        sheet_path = contact_sheet.generate_run_sheet(RESULTS_DIR, run_id)
        contact_sheet.generate_index(RESULTS_DIR)
        logger.info("Sheet", "contact sheet written %s (+ index.html)" % sheet_path)
    except Exception as exc:  # noqa: BLE001 - failure isolation by design (V3)
        logger.warn("Sheet", "contact-sheet generation FAILED run=%s (%s: %s); "
                             "sheet degraded, verdict unaffected"
                    % (run_id, type(exc).__name__, exc))


def _finish_result(spec, profile, attempt, started, start_wall, runtime, verdict,
                   admission, drive, facts, run_save_name, instance_dir, logger,
                   run_id: Optional[str] = None) -> Dict:
    scenario_id = spec.get("id")
    ended = utcnow_iso()
    wall = int(runtime.now() - start_wall)
    if run_id is None:
        run_id = _make_run_id(scenario_id, attempt, logger=logger)

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
    # The unmet-mission tail steps that were deliberately NOT driven. Recorded OUTSIDE
    # driver.steps on purpose: a step the harness chose not to send is not an unmet
    # step, so it must not drag allExpectedMet down or read as a seam verdict miss.
    # Emitted only when non-empty, so every other run's record is unchanged.
    if drive is not None and drive.skipped_tail_steps:
        driver_rec["skippedTailSteps"] = list(drive.skipped_tail_steps)
    # The mirror case: an UNMET mission whose spec opted OUT, so the full tail was driven
    # by policy. Named after the spec key a reader would go looking for. Emitted only in
    # that case, so a default-policy run's record is unchanged.
    if drive is not None and drive.tail_skip_opted_out:
        driver_rec[hlib.SKIP_TAIL_ON_UNMET_MISSION_KEY] = False
    # HARNESS-MIDMISSION-COMMIT-BYPASS (report-only): the route-1 writes the tail gate
    # never sees. Emitted only when the mission actually wrote some, so every existing
    # run's record -- including every seam-only driver's -- is byte-identical.
    if drive is not None and drive.mid_mission_seam_writes is not None \
            and drive.mid_mission_seam_writes.total:
        mm = drive.mid_mission_seam_writes
        driver_rec["midMissionSeamWrites"] = {
            # Counts WIRE WRITES, not executed commands: the seam skips a duplicate id, so a
            # mission that retries its commit writes twice and executes once. Read these as
            # "what the mission put on the wire", which is what the tail gate would have been
            # judging too.
            "total": mm.total,
            "worldMutating": mm.world_mutating,
            "verbs": list(mm.verbs),
            # True = a world-mutating write was made and the mission did NOT come back met
            # (unmet, or killed with no verdict at all). REPORT-ONLY: nothing reads this to
            # move a verdict.
            "exposedAfterUnmetMission": mm.exposed,
        }
    elif drive is not None and drive.mid_mission_seam_read_error:
        # A GAP is not a zero. Recorded so a reader can tell "we looked and saw none" from
        # "we could not look" -- the distinction this whole instrument exists to make.
        driver_rec["midMissionSeamWrites"] = {"readError": drive.mid_mission_seam_read_error}

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
        # G6: the mission subprocess's own wall span (None on a seam-only run /
        # a mission that wrote no readable result). wallSeconds minus this IS
        # the harness overhead; see _read_mission_wall_seconds.
        "missionWallSeconds": (int(round(drive.mission_wall_seconds))
                               if drive is not None
                               and drive.mission_wall_seconds is not None
                               else None),
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
        # V3 additive key: what the always-collect step snapshotted. Written
        # FIRST as the empty placeholder so the VERDICT is durable before the
        # (possibly slow) artifact copy runs (review MINOR 4: a Ctrl-C or a
        # process-tree kill landing inside a multi-MB copy must not cost the
        # attempt's result), then enriched and re-written below.
        "artifacts": dict(_EMPTY_ARTIFACTS),
    }
    write_result(result, logger)
    # V3 always-collect: the light UNCONDITIONAL artifact snapshot (KSP.log +
    # run-window screenshots into results/<runId>_shots/), verdict-neutral.
    result["artifacts"] = _collect_run_artifacts(run_id, instance_dir, start_wall, logger)
    write_result(result, logger, append_summary=False)
    # V3 contact sheet: AFTER the durable result lands so the sheet reads the
    # written record. Failure-isolated; never moves the verdict.
    _generate_contact_sheet(run_id, logger)
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


def write_result(result: Dict, logger: HarnessLogger,
                 append_summary: bool = True) -> None:
    """Persist one result record. ``append_summary=False`` re-writes the JSON
    WITHOUT a second summary.txt line -- used by the per-scenario cost pass,
    which enriches an already-written result and must not double the rolling
    summary for every run."""
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

    if not append_summary:
        return
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


def write_text_atomic(path: str, text: str) -> None:
    """tmp + ``os.replace`` write for a generated/committed artifact, mirroring
    ``write_result``.

    A truncate-in-place write leaves a PARTIAL file when the process dies
    between the truncate and the write (Ctrl-C, the run-budget process-tree
    kill, power loss). For ``coverage/duration.json`` -- the one COMMITTED
    artifact here -- that partial file is then unparseable to the next run,
    which is exactly the state the duration recovery path must never resolve by
    replacing the record (2026-07-26 review, MAJOR-4).
    """
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def read_duration_ledger(path: str, logger: HarnessLogger) -> Tuple[Dict, bool]:
    """The committed duration ledger's ``scenarios`` block, plus whether the
    read is TRUSTWORTHY.

    Fail LOUD, never fail quiet: a missing file is a legitimate first write
    (``{}``, ok=True), but a file that EXISTS and does not parse / fails the
    schema gate / has no scenarios map returns ok=False, and the caller then
    SKIPS the duration write entirely for this run. Silently treating an
    unreadable ledger as empty would replace a 24-scenario committed record with
    this checkout's handful -- reopening the very bug the merge exists to close,
    and doing it in a form ``git status`` shows as an ordinary modification.
    """
    if not os.path.isfile(path):
        return {}, True
    try:
        with open(path, "r", encoding="utf-8") as fh:
            obj = json.load(fh)
    except (OSError, ValueError) as exc:
        logger.error("Duration", "duration ledger %s exists but did not parse "
                                 "(%s); SKIPPING the duration write this run so a "
                                 "partial file cannot replace the committed record"
                     % (path, exc))
        return {}, False
    ok, message = hlib.check_schema(obj)
    if not ok:
        logger.error("Duration", "duration ledger %s failed the schema gate (%s); "
                                 "SKIPPING the duration write this run" % (path, message))
        return {}, False
    scenarios = (obj or {}).get("scenarios")
    if not isinstance(scenarios, dict):
        logger.error("Duration", "duration ledger %s has no scenarios map "
                                 "(got %r); SKIPPING the duration write this run"
                     % (path, type(scenarios).__name__))
        return {}, False
    return scenarios, True


def refresh_coverage_and_flake(specs: Sequence[Dict], registry: Dict,
                               logger: HarnessLogger) -> None:
    results = load_all_results()
    report = hlib.compute_coverage(specs, results, registry)
    os.makedirs(COVERAGE_DIR, exist_ok=True)
    write_text_atomic(os.path.join(COVERAGE_DIR, "coverage.json"),
                      json.dumps(hlib.coverage_to_json_obj(report),
                                 sort_keys=True, indent=2) + "\n")
    write_text_atomic(os.path.join(COVERAGE_DIR, "coverage.txt"),
                      hlib.coverage_to_txt(report))

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
    write_text_atomic(flake_path, json.dumps(flake_out, sort_keys=True, indent=2) + "\n")

    # Cross-run DURATION record (audit finding G5). Committed, unlike
    # coverage.json / flake.json: it is a few hundred bytes and it IS the
    # history -- results/*.json, results/summary.txt and every *.log are
    # gitignored, so without this nothing durable knows how long a scenario
    # takes and a backwards ordering claim can sit in a spec unnoticed across
    # four measured runs (it did).
    #
    # MERGE, never recompute (2026-07-26 review, BLOCKER-1 / MAJOR-2). This
    # checkout's results/ dir holds only the scenarios THIS worktree flew, so a
    # recompute-and-overwrite replaces the committed record with a one-entry
    # file, and even a missing-scenarios-only merge downgrades a measured
    # scenario's n=5 record to a per-checkout n=1 -- which disarms the very
    # regression warn the ledger exists for. hlib.merge_durations unions the
    # committed SAMPLES with this run's new PASS values.
    duration_path = os.path.join(COVERAGE_DIR, "duration.json")
    prior_durations, ledger_ok = read_duration_ledger(duration_path, logger)
    if ledger_ok:
        fresh_samples = hlib.duration_samples(results)
        durations = hlib.merge_durations(prior_durations, fresh_samples)
        write_text_atomic(duration_path,
                          json.dumps({"schema": hlib.SCHEMA_VERSION,
                                      "scenarios": durations},
                                     sort_keys=True, indent=2) + "\n")
        for sid in hlib.duration_regressions(durations):
            entry = durations[sid]
            logger.warn("Duration", "duration regression scenario=%s last=%.0fs "
                                    "p50=%.0fs p95=%.0fs n=%d lastVsP50=%.2fx"
                        % (sid, entry.get("last", 0), entry.get("p50", 0),
                           entry.get("p95", 0), entry.get("n", 0),
                           entry.get("lastVsP50", 0) or 0))
        logger.info("Duration", "duration record scenarios=%d measured-this-run=%d "
                                "(PASS results only) -> %s"
                    % (len(durations), len(fresh_samples), duration_path))
    # else: the reader already logged an Error and NOTHING is written. An
    # unreadable ledger must be repaired (git checkout), never replaced by this
    # checkout's partial view.

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
                       "logValidate, results, anomalySweep, unityExceptions(report-only), "
                       "expectations")
        if is_autopilot:
            # POSITION matters: only steps AFTER the mission handoff gate through this row
            # (a pre-mission outcome verb already gates through driverValidity), so the plan
            # must not advertise one that does not.
            mission_idx = next((i for i, s in enumerate(steps) if s.get("phase") == "mission"),
                               len(steps))
            gating = [s.get("cmd") for s in steps[mission_idx + 1:]
                      if s.get("cmd") and hlib.post_mission_step_gates(s.get("cmd"))]
            verify_line += (", missionOutcome(%s -> PARSEK-FAIL(mission-outcome) on an unmet "
                            "post-mission outcome step)"
                            % (", ".join(gating) if gating else "no gating verbs"))
        # saveParse (row 7b) runs on every driver-valid run, but the plan must say
        # whether it can MOVE THE VERDICT for this spec: gating is per-block and
        # opt-in, so "declared" and "armed" are different facts and the enumeration
        # was silently omitting both. S4.1 arms `rewind` (2026-07-31), and a plan
        # that does not name the one gate an operator is about to fly is worse than
        # no plan - it reads as report-only when it is not.
        sp_declared = saveparse.declared_structure_blocks(exp)
        sp_armed = saveparse.armed_structure_blocks(exp)
        if sp_armed:
            verify_line += (", saveParse(armed: %s -> PARSEK-FAIL(save-structure) on a "
                            "mismatch; report-only: %s)"
                            % (", ".join(sp_armed),
                               ", ".join(b for b in sp_declared if b not in sp_armed) or "none"))
        elif sp_declared:
            verify_line += ", saveParse(report-only: %s)" % ", ".join(sp_declared)
        else:
            verify_line += ", saveParse(facets only, no block declared)"
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
    parser.add_argument("--dry-run", action="store_true",
                        help="print the action plan and validate every selected "
                             "spec; launch nothing. Exits 1 if any spec is invalid.")
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
        # VALIDATE ON --dry-run TOO (2026-07-27). This used to `return 0` straight
        # after printing the plan, so --dry-run rendered a clean plan for a spec
        # validate_spec would REJECT - and the natural read of a green dry-run is
        # "this spec is fine to schedule". A reviewer proved the gap by breaking a
        # logContracts pattern: clean plan, exit 0, and the error surfaced only
        # after the flight. The real run path already validates (below); this makes
        # the FREE check agree with it. Deliberately AFTER the plan print, so the
        # author still gets the plan they asked for, and non-zero so a script can
        # gate on it.
        # Warnings are surfaced too, and bug_ids hoisted out of the loop, so this
        # reports EXACTLY what the real path reports rather than a subset - a spec
        # that looks cleaner on dry-run than on the real run is the same class of
        # gap this block was added to close.
        dry_bug_ids = _load_bug_ids()
        dry_errors = 0
        for spec in selected:
            schemas, schema_errors = resolve_mission_schemas(spec, logger)
            validation = hlib.validate_spec(spec, registry, dry_bug_ids, schemas)
            for w in validation.warnings:
                logger.warn("Select", "spec warning id=%s: %s" % (spec.get("id"), w))
            problems = list(validation.errors) + schema_errors
            for problem in problems:
                logger.error("Select", "spec invalid id=%s: %s"
                             % (spec.get("id"), problem))
            dry_errors += len(problems)
        if dry_errors:
            logger.error("Select",
                         "dry-run found %d spec validation error(s)" % dry_errors)
            return 1
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
    """Run a scenario with the spec's retry policy and return the LAST attempt's
    result, enriched with ``attemptsWallSeconds`` (audit finding G6).

    Before this, each attempt wrote its own result and nothing summed them, so
    B7-duna burning 794 + 776 = 1,570 wall seconds across two INVALID attempts
    and producing nothing was traceable only as two unrelated summary lines.
    """
    retry_policy = (spec.get("retry", {}) or {}).get("policy", "once")
    attempts: List[hlib.Verdict] = []
    last_result = None
    prior_boot_crashed = False
    attempts_wall = 0.0
    attempts_run = 0
    # ONE run-instance ordinal for the whole scenario run, resolved (and warned
    # about) exactly once here, then carried into both attempts -- `..._run2` and
    # `..._run2_a2` -- instead of each attempt disambiguating on its own and
    # drifting onto different stems. Each attempt still re-stamps its own minute
    # (a long flight rolls the clock over), so the ordinal is what is carried,
    # not the whole id; `_a<N>` stays the terminal suffix either way.
    run_ordinal = _resolve_run_id(spec.get("id"), 1, 1, logger).ordinal
    for attempt in (1, 2):
        result = run_attempt(spec, instance_dir, umbrella_root, runtime, attempt,
                             prior_boot_crashed, logger, run_ordinal=run_ordinal)
        last_result = result
        attempts_run += 1
        wall = result.get("wallSeconds")
        if isinstance(wall, (int, float)) and not isinstance(wall, bool):
            attempts_wall += float(wall)
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
    flaked_then_passed = (terminal.note == "flakedThenPassed"
                          and last_result is not None)
    if flaked_then_passed:
        last_result["note"] = "flakedThenPassed"
        logger.info("Classify", "verdict=PASS scenario=%s reason=flakedThenPassed (attempt-1 INVALID)"
                    % spec.get("id"))
    if last_result is not None:
        last_result["attemptsWallSeconds"] = int(round(attempts_wall))
        # Re-write the enriched record. The summary line is appended ONLY on
        # the flakedThenPassed path (which is what it always did); the plain
        # path already wrote its summary line inside run_attempt and must not
        # write a second one.
        write_result(last_result, logger, append_summary=flaked_then_passed)
        # Re-render the sheet + index over the enriched record (idempotent;
        # failure-isolated exactly like the in-attempt generation).
        _generate_contact_sheet(last_result["runId"], logger)
    logger.info("Cost", "scenario cost attempts=%d wallTotal=%ds terminal=%s"
                % (attempts_run, int(round(attempts_wall)), terminal.verdict))
    return last_result


def _write_invalid_spec_result(spec, errors, runtime, logger) -> None:
    scenario_id = spec.get("id") or os.path.basename(spec.get("_path", "unknown")).replace(".toml", "")
    # This id belongs to a spec that FAILED validation -- possibly on the
    # filename-safety rule itself -- and the runId becomes a results/ path
    # component (the .json and now the contact sheet's .html). Sanitize the
    # runId only; scenarioId in the record keeps the raw value (review NIT 10).
    safe_id = "".join(c if (c.isalnum() or c in "._-") else "_" for c in scenario_id)
    started = utcnow_iso()
    result = {
        "schema": hlib.SCHEMA_VERSION,
        "runId": _make_run_id(safe_id, 1, logger=logger),
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
        "artifacts": dict(_EMPTY_ARTIFACTS),
    }
    write_result(result, logger)
    _generate_contact_sheet(result["runId"], logger)


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
