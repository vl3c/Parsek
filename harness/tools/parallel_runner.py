"""Fly harness lanes on several provisioned KSP copies at once (the thin shell).

    cd harness && python tools/parallel_runner.py run --name nightly-1 --tier nightly --slots 2
    cd harness && python tools/parallel_runner.py status --name nightly-1
    cd harness && python tools/parallel_runner.py set-slots --name nightly-1 3
    cd harness && python tools/parallel_runner.py stop --name nightly-1
    cd harness && python tools/parallel_runner.py resume --name nightly-1

Every decision lives in ``lib/parallel.py`` (pure, unit-tested); this file does
the I/O: spec and duration reads, the machine lock and its heartbeat, the slot
and DLL checks, the one Parsek.Tests build, one ``run.py --slot <s>`` child per
lane, the run folder under ``results/parallel/<run>/`` and the end-of-run
bookkeeping (run index append, coverage / duration refresh, results sweep).

Contract: harness/README.md, "Parallel runs". The children are ``run.py``
itself in slot mode, so verdicts, retries and verifiers are run.py's own.

EXIT CODES (tier_runner's vocabulary, run.py's selection rule for 0 / 1):

  0  GREEN            every planned lane ended PASS or EXPECTED-FAIL
  1  RED              a PARSEK-FAIL / INVALID / KILLED / XPASS, a lane that left
                      no report, lanes left unflown, or the tests build failed
  2  NEEDS-PROVISION  no slot for a lane's profile, slots on different DLLs;
                      also a refused selection (unknown id or tier, bad name)
  3  SKIPPED/STOPPED  the machine lock is held, or a stop left lanes unflown
  4  ERROR            the runner itself broke

Stdlib only. Windows-only pieces (job object, memory probe, the virtual-desktop
registry read) import ctypes / winreg lazily and are no-ops elsewhere.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import secrets
import shutil
import subprocess
import sys
import threading
import time
import traceback
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.abspath(os.path.join(HERE, ".."))
WORKTREE_ROOT = os.path.abspath(os.path.join(HARNESS_ROOT, ".."))
UMBRELLA_ROOT = os.path.abspath(os.path.join(WORKTREE_ROOT, ".."))
LIB_DIR = os.path.join(HARNESS_ROOT, "lib")
PROVISION_DIR = os.path.join(HARNESS_ROOT, "provision")
for _p in (LIB_DIR, PROVISION_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import machinelock  # noqa: E402
import parallel  # noqa: E402
import runindex  # noqa: E402
import slots  # noqa: E402

KSP_EXE = "KSP_x64.exe"
PARSEK_DLL_RELPATH = ("GameData", "Parsek", "Plugins", "Parsek.dll")
HEARTBEAT_SECONDS = 60.0
POLL_SECONDS = 2.0
MEMORY_CACHE_SECONDS = 10.0
CHILD_KILL_WAIT_SECONDS = 30.0


def utcnow_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------


class RunnerLog:
    """Runner lines to the console and, once a run folder exists, to its
    ``runner.log``. The file is the record; a dead console latches off."""

    def __init__(self, console: Optional[Callable[[str], None]] = None):
        self._console = console or (lambda t: (print(t), sys.stdout.flush()))
        self._console_dead = False
        self._fh = None
        self._lock = threading.Lock()

    def attach(self, path: str) -> None:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self._fh = open(path, "a", encoding="utf-8")

    def raw(self, text: str) -> None:
        with self._lock:
            if not self._console_dead:
                try:
                    self._console(text)
                except (OSError, ValueError):
                    self._console_dead = True
            if self._fh is not None:
                try:
                    self._fh.write(text + "\n")
                    self._fh.flush()
                except (OSError, ValueError):
                    self._fh = None

    def log(self, level: str, step: str, message: str) -> None:
        self.raw("[Parallel][%s][%s] %s" % (level, step, message))

    def info(self, step, msg):
        self.log("Info", step, msg)

    def warn(self, step, msg):
        self.log("Warn", step, msg)

    def error(self, step, msg):
        self.log("Error", step, msg)

    def verbose(self, step, msg):
        self.log("Verbose", step, msg)

    def close(self) -> None:
        with self._lock:
            if self._fh is not None:
                self._fh.close()
                self._fh = None


class _HarnessLoggerAdapter:
    """The info / warn / error / verbose surface run.py's helpers log through,
    forwarded into the runner log under the helper's own step name."""

    log_path = None

    def __init__(self, log: RunnerLog):
        self._log = log

    def info(self, step, msg):
        self._log.info(step, msg)

    def warn(self, step, msg):
        self._log.warn(step, msg)

    def error(self, step, msg):
        self._log.error(step, msg)

    def verbose(self, step, msg):
        self._log.verbose(step, msg)


# ---------------------------------------------------------------------------
# File helpers
# ---------------------------------------------------------------------------


def write_text_atomic(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = "%s.tmp-%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def write_json_atomic(path: str, obj: Any) -> None:
    write_text_atomic(path, json.dumps(obj, indent=2, sort_keys=True) + "\n")


def read_json(path: str) -> Any:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def read_text(path: str) -> str:
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        return ""


def sha256_file(path: str) -> Optional[str]:
    import hashlib
    try:
        h = hashlib.sha256()
        with open(path, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


# ---------------------------------------------------------------------------
# Windows-only probes (lazy imports, no-ops elsewhere)
# ---------------------------------------------------------------------------


def pid_alive(pid: int) -> bool:
    """Fails closed (reports ALIVE) when the probe cannot run, like run.py."""
    if pid is None or int(pid) <= 0:
        return False
    if sys.platform == "win32":
        try:
            out = subprocess.run(["tasklist", "/FI", "PID eq %d" % int(pid), "/FO", "CSV", "/NH"],
                                 capture_output=True, text=True)
        except OSError:
            return True
        for row in (out.stdout or "").splitlines():
            cells = [c.strip('"') for c in row.split('","')]
            if len(cells) >= 2 and cells[1].strip() == str(int(pid)):
                return True
        return False
    try:
        os.kill(int(pid), 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return True
    return True


def free_physical_ram_gb() -> Optional[float]:
    """GlobalMemoryStatusEx's ullAvailPhys in GB; None off Windows or on failure."""
    if sys.platform != "win32":
        return None
    try:
        import ctypes

        class MEMORYSTATUSEX(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong),
                        ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong),
                        ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

        status = MEMORYSTATUSEX()
        status.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return None
        return status.ullAvailPhys / float(1024 ** 3)
    except Exception:  # noqa: BLE001 - a probe failure skips the check
        return None


def free_vram_gb() -> Optional[float]:
    exe = shutil.which("nvidia-smi")
    if not exe:
        return None
    try:
        out = subprocess.run([exe, "--query-gpu=memory.total,memory.used",
                              "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.SubprocessError):
        return None
    if out.returncode != 0:
        return None
    return parallel.parse_nvidia_smi_free_gb(out.stdout or "")


def memory_reading() -> parallel.MemoryReading:
    return parallel.MemoryReading(free_physical_ram_gb(), free_vram_gb())


VIRTUAL_DESKTOPS_KEY = r"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops"
SESSION_DESKTOPS_KEY = (r"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo"
                        r"\%d\VirtualDesktops")


def read_virtual_desktops() -> Tuple[List[str], Optional[str]]:
    """(desktop ids in order, current desktop id). The current id lives in the
    VirtualDesktops key on Windows 11 and in the per-session SessionInfo key on
    Windows 10; both are read. ([], None) off Windows or when unreadable."""
    if sys.platform != "win32":
        return [], None
    try:
        import winreg
    except ImportError:
        return [], None

    def _value(path: str, name: str) -> Optional[bytes]:
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
                data, _kind = winreg.QueryValueEx(key, name)
                return bytes(data) if isinstance(data, (bytes, bytearray)) else None
        except OSError:
            return None

    ids = parallel.parse_virtual_desktop_ids(_value(VIRTUAL_DESKTOPS_KEY, "VirtualDesktopIDs"))
    current = parallel.guid_from_bytes(_value(VIRTUAL_DESKTOPS_KEY, "CurrentVirtualDesktop"))
    if current is None:
        try:
            import ctypes
            session = ctypes.c_ulong(0)
            if ctypes.windll.kernel32.ProcessIdToSessionId(os.getpid(), ctypes.byref(session)):
                current = parallel.guid_from_bytes(
                    _value(SESSION_DESKTOPS_KEY % session.value, "CurrentVirtualDesktop"))
        except Exception:  # noqa: BLE001 - unknown current desktop is handled
            current = None
    return ids, current


class WindowsJob:
    """A Job Object with KILL_ON_JOB_CLOSE: when the runner dies, Windows closes
    the handle and kills every child run.py and the KSP it started. A no-op
    off Windows or when the job cannot be made (one Warn)."""

    JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000

    def __init__(self, log: RunnerLog):
        self._log = log
        self._job = None
        if sys.platform != "win32":
            return
        try:
            import ctypes
            from ctypes import wintypes

            class IO_COUNTERS(ctypes.Structure):
                _fields_ = [(n, ctypes.c_ulonglong) for n in (
                    "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                    "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

            class BASIC(ctypes.Structure):
                _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong),
                            ("PerJobUserTimeLimit", ctypes.c_longlong),
                            ("LimitFlags", wintypes.DWORD),
                            ("MinimumWorkingSetSize", ctypes.c_size_t),
                            ("MaximumWorkingSetSize", ctypes.c_size_t),
                            ("ActiveProcessLimit", wintypes.DWORD),
                            ("Affinity", ctypes.c_size_t),
                            ("PriorityClass", wintypes.DWORD),
                            ("SchedulingClass", wintypes.DWORD)]

            class EXTENDED(ctypes.Structure):
                _fields_ = [("BasicLimitInformation", BASIC), ("IoInfo", IO_COUNTERS),
                            ("ProcessMemoryLimit", ctypes.c_size_t),
                            ("JobMemoryLimit", ctypes.c_size_t),
                            ("PeakProcessMemoryUsed", ctypes.c_size_t),
                            ("PeakJobMemoryUsed", ctypes.c_size_t)]

            k32 = ctypes.windll.kernel32
            k32.CreateJobObjectW.restype = wintypes.HANDLE
            job = k32.CreateJobObjectW(None, None)
            if not job:
                raise OSError("CreateJobObjectW failed")
            info = EXTENDED()
            info.BasicLimitInformation.LimitFlags = self.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if not k32.SetInformationJobObject(wintypes.HANDLE(job),
                                               self.JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
                                               ctypes.byref(info), ctypes.sizeof(info)):
                raise OSError("SetInformationJobObject failed")
            self._job = job
            log.info("Job", "child job object armed (KILL_ON_JOB_CLOSE)")
        except Exception as exc:  # noqa: BLE001
            log.warn("Job", "could not create the child job object (%s); a dead runner "
                            "would leave its children running" % exc)

    def assign(self, proc) -> None:
        if self._job is None:
            return
        try:
            import ctypes
            from ctypes import wintypes
            handle = getattr(proc, "_handle", None)
            if handle is None or not ctypes.windll.kernel32.AssignProcessToJobObject(
                    wintypes.HANDLE(self._job), wintypes.HANDLE(int(handle))):
                self._log.warn("Job", "child pid=%s not assigned to the job object" % proc.pid)
        except Exception as exc:  # noqa: BLE001
            self._log.warn("Job", "job assignment failed pid=%s: %s" % (proc.pid, exc))


def kill_tree(proc) -> None:
    if proc.poll() is not None:
        return
    if sys.platform == "win32":
        try:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)],
                           capture_output=True, text=True)
            return
        except OSError:
            pass
    try:
        proc.kill()
    except OSError:
        pass


# ---------------------------------------------------------------------------
# run.py bridge: the helpers the runner calls once per run
# ---------------------------------------------------------------------------


def _load_by_path(module_name: str, path: str):
    """Load a harness module by file path once (no sys.path change)."""
    existing = sys.modules.get(module_name)
    if existing is not None and os.path.abspath(getattr(existing, "__file__", "") or "") == \
            os.path.abspath(path):
        return existing
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise ImportError("cannot load %s" % path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop(module_name, None)
        raise
    return module


class RunPyBridge:
    """What must stay byte-identical with a serial run, done once per parallel
    run: the Parsek.Tests build (run.py's own selection build), the coverage /
    duration refresh and the results sweeps (run.py's ``--refresh-coverage`` /
    ``--sweep-results`` maintenance modes, run under this runner's token), and
    the run index append (``tools/run_index.py``'s ``append_runs``)."""

    def __init__(self, harness_root: str = HARNESS_ROOT, python: str = sys.executable):
        self.harness_root = harness_root
        self.python = python

    def run_module(self):
        return _load_by_path("run", os.path.join(self.harness_root, "run.py"))

    def build_tests(self, log: RunnerLog) -> bool:
        run = self.run_module()
        return bool(run._prebuild_tests_for_selection(run.Runtime(), _HarnessLoggerAdapter(log)))

    def _maintenance(self, flag: str, token: str, log: RunnerLog) -> None:
        argv = [self.python, os.path.join(self.harness_root, "run.py"), flag]
        env = parallel.child_env(os.environ, token, os.getpid())
        out = subprocess.run(argv, cwd=self.harness_root, env=env, capture_output=True,
                             text=True, encoding="utf-8", errors="replace")
        for line in (out.stdout or "").splitlines()[-20:]:
            log.raw(line)
        if out.returncode != 0:
            raise RuntimeError("run.py %s exited %d: %s"
                               % (flag, out.returncode, (out.stderr or "").strip()[-400:]))

    def refresh_coverage(self, token: str, log: RunnerLog) -> None:
        self._maintenance("--refresh-coverage", token, log)

    def sweep_results(self, token: str, log: RunnerLog) -> None:
        self._maintenance("--sweep-results", token, log)

    def append_run_index(self, umbrella_root: str, run_ids: Sequence[str], log: RunnerLog) -> None:
        if hlib.results_archive_disabled(os.environ):
            log.info("Archive", "run index append skipped (%s)" % hlib.RESULTS_ARCHIVE_OPT_OUT_ENV)
            return
        module = _load_by_path("run_index", os.path.join(self.harness_root, "tools",
                                                         "run_index.py"))
        archive = os.path.join(umbrella_root, *hlib.RESULTS_ARCHIVE_RELPATH)
        counts, skipped, path = module.append_runs(archive, list(run_ids))
        log.info("Archive", "run index %s: %s%s" % (
            path, module.format_append_counts(counts),
            "" if not skipped else " (%s)" % "; ".join("%s: %s" % s for s in skipped)))


class UnityPrefsBridge:
    """Snapshot / restore of the Unity Screenmanager registry values every KSP
    on the PC shares (plan v2 item 10), through run.py's helper
    ``tools/unity_prefs.py`` (``begin`` / ``end``). The machine-lock holder owns
    them, so the runner calls them and slot children never do. A missing helper
    is one Warn, never a refusal."""

    def __init__(self, harness_root: str = HARNESS_ROOT):
        self.path = os.path.join(harness_root, "tools", "unity_prefs.py")

    def _call(self, name: str, umbrella_root: str, log: RunnerLog) -> None:
        if not os.path.isfile(self.path):
            log.warn("UnityPrefs", "%s not found; unity prefs %s skipped" % (self.path, name))
            return
        module = _load_by_path("parsek_unity_prefs", self.path)
        outcome = getattr(module, name)(umbrella_root,
                                        lambda level, msg: log.log(level, "UnityPrefs", msg))
        log.info("UnityPrefs", "%s: %s" % (name, outcome))

    def snapshot(self, umbrella_root: str, log: RunnerLog) -> None:
        self._call("begin", umbrella_root, log)

    def restore(self, umbrella_root: str, log: RunnerLog) -> None:
        self._call("end", umbrella_root, log)


# ---------------------------------------------------------------------------
# Context (every seam the smoke test swaps)
# ---------------------------------------------------------------------------


@dataclass
class Context:
    harness_root: str = HARNESS_ROOT
    umbrella_root: str = UMBRELLA_ROOT
    python: str = sys.executable
    run_py: Optional[str] = None
    poll_seconds: float = POLL_SECONDS
    heartbeat_seconds: float = HEARTBEAT_SECONDS
    settle_seconds: float = parallel.DISPATCH_SETTLE_SECONDS
    bridge: Any = None
    unity_prefs: Any = None
    memory_fn: Callable[[], parallel.MemoryReading] = memory_reading
    desktops_fn: Callable[[], Tuple[List[str], Optional[str]]] = read_virtual_desktops
    pid_alive_fn: Callable[[int], bool] = pid_alive
    stdin_text: Optional[Callable[[], str]] = None
    console: Optional[Callable[[str], None]] = None

    def __post_init__(self):
        if self.run_py is None:
            self.run_py = os.path.join(self.harness_root, "run.py")
        if self.bridge is None:
            self.bridge = RunPyBridge(self.harness_root, self.python)
        if self.unity_prefs is None:
            self.unity_prefs = UnityPrefsBridge(self.harness_root)
        if self.stdin_text is None:
            self.stdin_text = lambda: sys.stdin.read()

    @property
    def results_dir(self) -> str:
        return os.path.join(self.harness_root, "results")

    @property
    def stop_request_path(self) -> str:
        return os.path.join(self.results_dir, hlib.STOP_REQUEST_FILENAME)

    def run_dir(self, name: str) -> str:
        return os.path.join(self.results_dir, parallel.PARALLEL_RESULTS_DIRNAME, name)

    def automation_dir(self) -> str:
        return os.path.join(self.umbrella_root, slots.AUTOMATION_DIR)


# ---------------------------------------------------------------------------
# Spec, duration and slot reads
# ---------------------------------------------------------------------------


def load_specs(ctx: Context) -> List[Dict]:
    import tomllib
    out = []
    scen = os.path.join(ctx.harness_root, "scenarios")
    for name in sorted(os.listdir(scen)):
        if not name.endswith(".toml"):
            continue
        with open(os.path.join(scen, name), "rb") as fh:
            spec = tomllib.load(fh)
        spec["_path"] = os.path.join(scen, name)
        out.append(spec)
    return out


def load_durations(ctx: Context) -> Dict[str, Dict]:
    data = read_json(os.path.join(ctx.harness_root, "coverage", "duration.json"))
    scenarios = data.get("scenarios") if isinstance(data, dict) else None
    return scenarios if isinstance(scenarios, dict) else {}


def load_index_medians(ctx: Context) -> Dict[str, float]:
    path = os.path.join(ctx.umbrella_root, *hlib.RESULTS_ARCHIVE_RELPATH, runindex.INDEX_FILE_NAME)
    text = read_text(path)
    if not text:
        return {}
    try:
        rows = runindex.parse_index(text).rows
    except Exception:  # noqa: BLE001 - an unreadable index only loses estimates
        return {}
    return parallel.index_pass_medians(rows)


def discover_slot_names(ctx: Context) -> List[str]:
    auto = ctx.automation_dir()
    try:
        names = [n for n in os.listdir(auto)
                 if os.path.isfile(os.path.join(auto, n, KSP_EXE))]
    except OSError:
        names = []
    return slots.discover_slots(names)


def slot_dll_shas(ctx: Context, slot_names: Sequence[str]) -> List[Tuple[str, Optional[str]]]:
    return [(s, sha256_file(os.path.join(ctx.automation_dir(), s, *PARSEK_DLL_RELPATH)))
            for s in slot_names]


def git_head(ctx: Context) -> str:
    try:
        out = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ctx.harness_root,
                             capture_output=True, text=True)
        return (out.stdout or "").strip() or "unknown"
    except OSError:
        return "unknown"


# ---------------------------------------------------------------------------
# The run folder
# ---------------------------------------------------------------------------


class RunFolder:
    def __init__(self, path: str):
        self.path = path
        self._events_lock = threading.Lock()

    def file(self, name: str) -> str:
        return os.path.join(self.path, name)

    def lanes_dir(self) -> str:
        return os.path.join(self.path, parallel.LANES_DIRNAME)

    def read_plan(self) -> Optional[Dict]:
        plan = read_json(self.file(parallel.PLAN_FILE))
        if not isinstance(plan, dict) or plan.get("schema") != parallel.PLAN_SCHEMA:
            return None
        return plan

    def read_control(self, fallback_slots: int) -> Tuple[int, bool]:
        return parallel.read_control_obj(read_json(self.file(parallel.CONTROL_FILE)),
                                         fallback_slots)

    def write_control(self, slots_value: int, stop: bool) -> None:
        obj = parallel.default_control(slots_value)
        obj["stop"] = bool(stop)
        write_json_atomic(self.file(parallel.CONTROL_FILE), obj)

    def event(self, kind: str, **fields) -> None:
        ev = {"kind": kind, "utc": utcnow_iso(), "epoch": time.time()}
        ev.update(fields)
        with self._events_lock:
            os.makedirs(self.path, exist_ok=True)
            with open(self.file(parallel.EVENTS_FILE), "a", encoding="utf-8") as fh:
                fh.write(parallel.event_line(ev) + "\n")

    def events(self) -> List[Dict]:
        return parallel.parse_events(read_text(self.file(parallel.EVENTS_FILE)))

    def summary_lines(self) -> List[str]:
        return read_text(self.file(parallel.SUMMARY_FILE)).splitlines()

    def write_summary(self, lane_lines: Sequence[str], plan_ids: Sequence[str],
                      not_flown: Sequence[str]) -> None:
        write_text_atomic(self.file(parallel.SUMMARY_FILE),
                          parallel.render_summary(lane_lines, plan_ids, not_flown))


def flying_lanes(plan: Dict, include_exclusive: bool) -> Tuple[List[parallel.Lane], List[str]]:
    """(lanes this invocation may fly, ids not flown with their reason text)."""
    lanes = [parallel.Lane.from_json(l) for l in plan.get("lanes") or []]
    flown, left = parallel.partition_exclusive(lanes, include_exclusive)
    not_flown = ["%s" % l.lane_id for l in left]
    not_flown += [str(s.get("id")) for s in plan.get("skipped") or []]
    return flown, not_flown


# ---------------------------------------------------------------------------
# Lock
# ---------------------------------------------------------------------------


class LockKeeper:
    """Holds the machine lock for the whole run and heartbeats it from a daemon
    thread. ``lost`` is set when the lock stops being ours."""

    def __init__(self, ctx: Context, name: str, log: RunnerLog):
        self.ctx = ctx
        self.name = name
        self.log = log
        self.token = secrets.token_hex(16)
        self.path: Optional[str] = None
        self.lost = threading.Event()
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._started_iso = utcnow_iso()

    def payload(self) -> Dict[str, Any]:
        return {"pid": os.getpid(), "timestamp": time.time(), "worktree": WORKTREE_ROOT,
                "selection": "parallel-runner run=%s" % self.name,
                "startedIso": self._started_iso, "token": self.token,
                "kind": parallel.RUNNER_LOCK_KIND, "runName": self.name}

    def _log_fn(self, level: str, msg: str) -> None:
        self.log.log(level if level in ("Info", "Warn", "Error") else "Info", "Lock", msg)

    def acquire(self) -> Tuple[bool, str]:
        lock_path = machinelock.lock_path_for(self.ctx.umbrella_root)
        path, reason, holder = machinelock.acquire(
            lock_path, payload_fn=self.payload, now_fn=time.time,
            pid_alive_fn=self.ctx.pid_alive_fn, log_fn=self._log_fn)
        if path is None:
            return False, "%s: %s" % (reason, machinelock.describe_holder(holder))
        self.path = path
        self._thread = threading.Thread(target=self._beat, name="parallel-lock-heartbeat",
                                        daemon=True)
        self._thread.start()
        return True, reason

    def _beat(self) -> None:
        while not self._stop.wait(self.ctx.heartbeat_seconds):
            if not machinelock.heartbeat(self.path, payload_fn=self.payload, log_fn=self._log_fn):
                self.log.error("Lock", "lost the machine lock; no new lane starts")
                self.lost.set()
                return

    def release(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)
        if self.path:
            machinelock.release(self.path, log_fn=self._log_fn)


# ---------------------------------------------------------------------------
# Dispatch loop
# ---------------------------------------------------------------------------


@dataclass
class Child:
    seq: int
    lane: parallel.Lane
    slot: str
    proc: Any
    started: float
    report_path: str
    log_path: str
    log_fh: Any


def _spawn_child(ctx: Context, folder: RunFolder, job: WindowsJob, seq: int,
                 lane: parallel.Lane, slot: str, window: str, desktop_id: Optional[str],
                 token: str, log: RunnerLog) -> Child:
    lanes_dir = folder.lanes_dir()
    os.makedirs(lanes_dir, exist_ok=True)
    ids_file = os.path.join(lanes_dir, "%s.ids" % lane.lane_id)
    write_text_atomic(ids_file, "# parallel run lane\n%s\n" % lane.lane_id)
    report = os.path.join(lanes_dir, "%s.%d.report.json" % (lane.lane_id, seq))
    log_path = os.path.join(lanes_dir, "%s.%d.log" % (lane.lane_id, seq))
    argv = parallel.child_argv(ctx.python, ctx.run_py, slot, ids_file, report, window, desktop_id)
    env = parallel.child_env(os.environ, token, os.getpid())
    fh = open(log_path, "a", encoding="utf-8")
    flags = 0
    if sys.platform == "win32":
        flags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    proc = subprocess.Popen(argv, cwd=ctx.harness_root, env=env, stdout=fh,
                            stderr=subprocess.STDOUT, creationflags=flags)
    job.assign(proc)
    log.info("Dispatch", "lane=%s slot=%s seq=%d pid=%d window=%s p50=%.0fs%s log=%s"
             % (lane.lane_id, slot, seq, proc.pid, window, lane.p50,
                " exclusive" if lane.exclusive else "", log_path))
    return Child(seq, lane, slot, proc, time.time(), report, log_path, fh)


def _finish_child(child: Child, folder: RunFolder, lane_lines: List[str],
                  log: RunnerLog) -> Tuple[Optional[parallel.LaneOutcome], bool]:
    """(outcome or None, child honoured a stop request instead of flying)."""
    try:
        child.log_fh.close()
    except OSError:
        pass
    code = child.proc.returncode
    wall = time.time() - child.started
    report = parallel.parse_child_report(read_json(child.report_path), child.lane.lane_id)
    not_run = report is None or report.verdict == parallel.VERDICT_NOT_RUN
    if not_run and hlib.STOP_HONOURED_MARKER in read_text(child.log_path):
        log.warn("Finish", "lane=%s slot=%s: run.py honoured a stop request before the lane "
                           "flew; it stays pending" % (child.lane.lane_id, child.slot))
        folder.event("finish", seq=child.seq, lane=child.lane.lane_id, slot=child.slot,
                     verdict=None, stopped=True, exit=code)
        return None, True
    if report is None:
        outcome = parallel.LaneOutcome(utcnow_iso(), parallel.VERDICT_NO_REPORT,
                                       child.lane.lane_id, 1, int(wall), child.slot, (),
                                       "exit=%s" % code)
    else:
        outcome = parallel.LaneOutcome(utcnow_iso(), report.verdict, child.lane.lane_id,
                                       report.attempts, int(round(wall)), child.slot,
                                       report.run_ids)
    lane_lines.append(parallel.format_summary_line(outcome))
    level = log.info if outcome.verdict in parallel.GREEN_VERDICTS else log.warn
    level("Finish", "lane=%s slot=%s verdict=%s attempts=%d wall=%ds exit=%s runIds=%s"
          % (outcome.lane_id, outcome.slot, outcome.verdict, outcome.attempts,
             outcome.wall_seconds, code, ",".join(outcome.run_ids) or "-"))
    folder.event("finish", seq=child.seq, lane=child.lane.lane_id, slot=child.slot,
                 verdict=outcome.verdict, attempts=outcome.attempts, wall=outcome.wall_seconds,
                 runIds=list(outcome.run_ids), exit=code)
    return outcome, False


def _consume_stop_request(ctx: Context) -> bool:
    path = ctx.stop_request_path
    if not os.path.exists(path):
        return False
    try:
        os.remove(path)
    except OSError:
        pass
    return True


@dataclass
class LoopResult:
    stopped: bool = False
    lock_lost: bool = False
    run_ids: List[str] = field(default_factory=list)
    ran: int = 0


def dispatch_loop(ctx: Context, folder: RunFolder, plan: Dict, lanes: List[parallel.Lane],
                  not_flown: List[str], by_profile: Dict[str, List[str]],
                  resolution: parallel.WindowResolution, keeper: LockKeeper,
                  job: WindowsJob, log: RunnerLog) -> LoopResult:
    """Dispatch until every lane started and finished (or a stop drained the
    running ones). Anything raised (Ctrl-C included) kills the running children
    first, so the Unity prefs restore and the lock release that follow never
    run beside a live KSP."""
    running: Dict[int, Child] = {}
    try:
        return _dispatch(ctx, folder, plan, lanes, not_flown, by_profile, resolution,
                         keeper, job, log, running)
    except BaseException:
        _kill_running(running, log)
        for child in running.values():
            try:
                child.proc.wait(timeout=CHILD_KILL_WAIT_SECONDS)
            except Exception:  # noqa: BLE001
                pass
            folder.event("abandon", seq=child.seq, lane=child.lane.lane_id, slot=child.slot)
        raise


def _dispatch(ctx: Context, folder: RunFolder, plan: Dict, lanes: List[parallel.Lane],
              not_flown: List[str], by_profile: Dict[str, List[str]],
              resolution: parallel.WindowResolution, keeper: LockKeeper,
              job: WindowsJob, log: RunnerLog, running: Dict[int, "Child"]) -> LoopResult:
    plan_ids = [l.lane_id for l in lanes]
    lane_lines = [l for l in folder.summary_lines() if parallel.parse_summary_line(l)]
    remaining = set(parallel.remaining_lane_ids(plan_ids, lane_lines))
    pending = [l for l in lanes if l.lane_id in remaining]
    log.info("Plan", "%d lane(s) to fly, %d already settled, %d not flown"
             % (len(pending), len(lanes) - len(pending), len(not_flown)))
    fallback_slots = int(plan.get("slotsAtStart") or parallel.DEFAULT_SLOTS)
    seq = max([int(e.get("seq") or 0) for e in folder.events()] + [0])
    result = LoopResult()
    last_start: Optional[float] = None
    last_wait: Optional[Tuple[str, str]] = None
    mem_cache: Tuple[float, Optional[parallel.MemoryReading]] = (0.0, None)
    while True:
        for s, child in list(running.items()):
            if child.proc.poll() is None:
                continue
            del running[s]
            outcome, child_stopped = _finish_child(child, folder, lane_lines, log)
            if child_stopped:
                result.stopped = True
            if outcome is not None:
                result.ran += 1
                result.run_ids.extend(outcome.run_ids)
                folder.write_summary(lane_lines, plan_ids, not_flown)

        slots_value, stop = folder.read_control(fallback_slots)
        if _consume_stop_request(ctx):
            log.warn("Stop", "%s (results/%s): running lanes finish, nothing new starts"
                     % (hlib.STOP_HONOURED_MARKER, hlib.STOP_REQUEST_FILENAME))
            folder.write_control(slots_value, True)
            folder.event("stop", source="stop-request")
            stop = True
        if stop and not result.stopped:
            log.warn("Stop", "stop requested; %d running lane(s) finish, %d pending dropped "
                             "for this invocation" % (len(running), len(pending)))
            result.stopped = True
        if keeper.lost.is_set() and not result.lock_lost:
            result.lock_lost = True
        halt = result.stopped or result.lock_lost

        memory = None
        if running and pending and not halt:
            now = time.time()
            if now - mem_cache[0] >= MEMORY_CACHE_SECONDS or mem_cache[1] is None:
                mem_cache = (now, ctx.memory_fn())
            memory = mem_cache[1]
        running_view = [parallel.Running(c.lane.lane_id, c.slot, c.lane.exclusive, c.started)
                        for c in running.values()]
        decision = parallel.decide_next(pending, running_view, by_profile, slots_value, halt,
                                        memory, time.time(), last_start, ctx.settle_seconds)
        if decision.action == parallel.ACTION_DONE:
            break
        if decision.action == parallel.ACTION_START:
            lane = next(l for l in pending if l.lane_id == decision.lane_id)
            pending.remove(lane)
            seq += 1
            window = decision.window or resolution.mode
            desktop = resolution.desktop_id if window == parallel.WINDOW_DESKTOP else None
            child = _spawn_child(ctx, folder, job, seq, lane, decision.slot, window, desktop,
                                 keeper.token, log)
            running[seq] = child
            last_start = child.started
            last_wait = None
            folder.event("dispatch", seq=seq, lane=lane.lane_id, slot=decision.slot,
                         window=window, pid=child.proc.pid, reason=decision.reason)
            continue
        key = (decision.wait_kind or "", decision.reason
               if decision.wait_kind in (parallel.WAIT_MEMORY, parallel.WAIT_STOP) else "")
        if key != last_wait:
            last_wait = key
            if decision.wait_kind in (parallel.WAIT_MEMORY, parallel.WAIT_SLOTS,
                                      parallel.WAIT_EXCLUSIVE, parallel.WAIT_SLOT_BUSY):
                log.info("Wait", "%s: %s" % (decision.wait_kind, decision.reason))
                folder.event("wait", waitKind=decision.wait_kind, reason=decision.reason)
        time.sleep(ctx.poll_seconds)
    folder.write_summary(lane_lines, plan_ids, not_flown)
    return result


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------


def _refuse(log: RunnerLog, code: int, step: str, message: str) -> int:
    log.error(step, message)
    return code


def _read_selection(ctx: Context, args) -> Tuple[List[str], List[str]]:
    ids: List[str] = []
    for path in args.ids_file or []:
        if path != "-" and not os.path.isfile(path):
            raise parallel.SelectionError("cannot read --ids-file %s" % path)
        text = ctx.stdin_text() if path == "-" else read_text(path)
        ids.extend(parallel.parse_id_lines(text))
    for value in args.ids or []:
        ids.extend(parallel.split_ids_arg(value))
    return ids, list(args.tier or [])


def cmd_run(ctx: Context, args, log: RunnerLog) -> int:
    if not parallel.valid_run_name(args.name):
        return _refuse(log, parallel.EXIT_USAGE, "Select", "bad run name %r" % args.name)
    folder = RunFolder(ctx.run_dir(args.name))
    if folder.read_plan() is not None and not args.dry_run:
        return _refuse(log, parallel.EXIT_USAGE, "Select",
                       "run %s already has a plan (%s); use `resume --name %s` or another name"
                       % (args.name, folder.file(parallel.PLAN_FILE), args.name))
    n_slots = parallel.clamp_slots(args.slots)
    if n_slots is None:
        return _refuse(log, parallel.EXIT_USAGE, "Select", "--slots must be %d..%d"
                       % (parallel.MIN_SLOTS, parallel.MAX_SLOTS))
    specs = load_specs(ctx)
    try:
        ids, tiers = _read_selection(ctx, args)
        selected = parallel.resolve_selection(specs, ids, tiers)
    except parallel.SelectionError as exc:
        return _refuse(log, parallel.EXIT_USAGE, "Select", "refused: %s" % exc)
    if not selected:
        return _refuse(log, parallel.EXIT_USAGE, "Select",
                       "no lane selected (use --ids-file / --ids / --tier)")
    lanes, skipped = parallel.build_lanes(
        selected, load_durations(ctx), load_index_medians(ctx),
        lambda t: os.path.isdir(os.path.join(ctx.harness_root, t.replace("/", os.sep))))
    lanes = parallel.order_lanes(lanes)
    plan = {"schema": parallel.PLAN_SCHEMA, "name": args.name, "createdUtc": utcnow_iso(),
            "gitHead": git_head(ctx), "windowMode": args.window,
            "includeExclusive": bool(args.include_exclusive), "slotsAtStart": n_slots,
            "lanes": [l.to_json() for l in lanes],
            "skipped": [{"id": sid, "reason": reason} for sid, reason in skipped]}
    flown, not_flown = flying_lanes(plan, plan["includeExclusive"])
    plan["notFlown"] = ([{"id": l.lane_id, "reason": "exclusive (%s); --include-exclusive "
                          "flies it" % l.exclusive_reason}
                         for l in lanes if l.exclusive and not plan["includeExclusive"]]
                        + plan["skipped"])
    for line in format_plan(plan, flown):
        log.info("Plan", line)
    if args.dry_run:
        slot_names = discover_slot_names(ctx)
        by_profile = parallel.slots_by_profile(slot_names)
        log.info("Plan", "slots discovered: %s" % (", ".join(slot_names) or "none"))
        missing = parallel.missing_profiles(flown, by_profile)
        if missing:
            log.warn("Plan", "no provisioned slot for profile(s): %s" % ", ".join(missing))
        res = parallel.resolve_window_mode(args.window, *ctx.desktops_fn())
        log.info("Plan", "window=%s %s%s" % (res.mode, res.detail,
                                              (" desktop=%s" % res.desktop_id)
                                              if res.desktop_id else ""))
        log.info("Done", "dry-run: nothing written, nothing flown")
        return parallel.EXIT_GREEN
    os.makedirs(folder.path, exist_ok=True)
    log.attach(folder.file(parallel.RUNNER_LOG_FILE))
    write_json_atomic(folder.file(parallel.PLAN_FILE), plan)
    folder.write_control(n_slots, False)
    log.info("Plan", "plan written %s" % folder.file(parallel.PLAN_FILE))
    return execute(ctx, folder, plan, plan["includeExclusive"], log)


def format_plan(plan: Dict, flown: Sequence[parallel.Lane]) -> List[str]:
    total = sum(l.p50 for l in flown)
    by_source: Dict[str, int] = {}
    for l in flown:
        by_source[l.p50_source] = by_source.get(l.p50_source, 0) + 1
    out = ["run %s: %d lane(s), p50 sum %.1f h (serial), slots=%d, window=%s, estimates: %s"
           % (plan["name"], len(flown), total / 3600.0, plan["slotsAtStart"], plan["windowMode"],
              " ".join("%s=%d" % kv for kv in sorted(by_source.items())) or "-")]
    for l in flown:
        out.append("lane %s profile=%s p50=%.0fs (%s)%s" % (
            l.lane_id, l.profile, l.p50, l.p50_source,
            " EXCLUSIVE last, alone: %s" % l.exclusive_reason if l.exclusive else ""))
    for entry in plan.get("notFlown") or []:
        out.append("not flown %s: %s" % (entry.get("id"), entry.get("reason")))
    return out


def cmd_resume(ctx: Context, args, log: RunnerLog) -> int:
    folder = RunFolder(ctx.run_dir(args.name))
    plan = folder.read_plan() if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Resume", "no plan for run %r under %s"
                       % (args.name, ctx.run_dir(args.name)))
    log.attach(folder.file(parallel.RUNNER_LOG_FILE))
    include = bool(args.include_exclusive or plan.get("includeExclusive"))
    slots_value, stop = folder.read_control(int(plan.get("slotsAtStart") or 2))
    if stop:
        log.info("Resume", "clearing the stop flag left by the previous invocation")
    folder.write_control(slots_value, False)
    if plan.get("gitHead") != git_head(ctx):
        log.warn("Resume", "this worktree is at %s, the plan was made at %s"
                 % (git_head(ctx), plan.get("gitHead")))
    return execute(ctx, folder, plan, include, log)


def execute(ctx: Context, folder: RunFolder, plan: Dict, include_exclusive: bool,
            log: RunnerLog) -> int:
    lanes, not_flown = flying_lanes(plan, include_exclusive)
    plan_ids = [l.lane_id for l in lanes]
    remaining = parallel.remaining_lane_ids(plan_ids, folder.summary_lines())
    if not remaining:
        outcome, code, detail = parallel.selection_outcome(plan_ids, folder.summary_lines(), False)
        log.info("Done", "nothing left to fly; outcome=%s exit=%d %s" % (outcome, code, detail))
        return code
    slot_names = discover_slot_names(ctx)
    by_profile = parallel.slots_by_profile(slot_names)
    todo = [l for l in lanes if l.lane_id in set(remaining)]
    missing = parallel.missing_profiles(todo, by_profile)
    if missing:
        return _refuse(log, parallel.EXIT_NEEDS_PROVISION, "Slots",
                       "no provisioned slot for profile(s) %s (slots found: %s); provision "
                       "them (`python provision/provision.py --profile <p>`) or leave those "
                       "lanes out" % (", ".join(missing), ", ".join(slot_names) or "none"))
    used = [s for p in sorted({l.profile for l in todo}) for s in by_profile.get(p, [])]
    log.info("Slots", "slots in use: %s" % ", ".join(used))

    keeper = LockKeeper(ctx, str(plan.get("name")), log)
    ok, reason = keeper.acquire()
    if not ok:
        return _refuse(log, parallel.EXIT_SKIPPED, "Lock",
                       "machine lock held, nothing started (%s)" % reason)
    log.info("Lock", "machine lock acquired (%s) kind=%s pid=%d"
             % (reason, parallel.RUNNER_LOCK_KIND, os.getpid()))
    folder.event("start", pid=os.getpid(), lanes=len(remaining))
    job = WindowsJob(log)
    prefs_taken = False
    result = LoopResult()
    try:
        odd = parallel.check_uniform_dll(slot_dll_shas(ctx, used))
        if odd:
            folder.event("end", outcome="NEEDS-PROVISION", exit=parallel.EXIT_NEEDS_PROVISION)
            return _refuse(log, parallel.EXIT_NEEDS_PROVISION, "Slots", odd)
        desktops, current = ctx.desktops_fn()
        resolution = parallel.resolve_window_mode(str(plan.get("windowMode")), desktops, current)
        log.info("Window", "window=%s %s%s" % (resolution.mode, resolution.detail,
                                                (" desktop=%s" % resolution.desktop_id)
                                                if resolution.desktop_id else ""))
        if resolution.hint:
            log.info("Window", resolution.hint)
        ctx.unity_prefs.snapshot(ctx.umbrella_root, log)
        prefs_taken = True
        if not ctx.bridge.build_tests(log):
            folder.event("end", outcome="RED", exit=parallel.EXIT_RED, reason="tests-build")
            return _refuse(log, parallel.EXIT_RED, "Build",
                           "Parsek.Tests build failed; no lane started")
        result = dispatch_loop(ctx, folder, plan, lanes, not_flown, by_profile, resolution,
                               keeper, job, log)
        if result.ran:
            _bookkeeping(ctx, keeper, result, log)
    finally:
        if prefs_taken:
            try:
                ctx.unity_prefs.restore(ctx.umbrella_root, log)
            except Exception as exc:  # noqa: BLE001
                log.warn("UnityPrefs", "restore failed: %s" % exc)
        keeper.release()

    outcome, code, detail = parallel.selection_outcome(plan_ids, folder.summary_lines(),
                                                       result.stopped)
    if result.lock_lost and code == parallel.EXIT_GREEN:
        outcome, code = parallel.OUTCOME_RED, parallel.EXIT_RED
    folder.event("end", outcome=outcome, exit=code, detail=detail)
    (log.info if code == parallel.EXIT_GREEN else log.warn)(
        "Done", "run %s outcome=%s exit=%d %s; summary %s"
        % (plan.get("name"), outcome, code, detail, folder.file(parallel.SUMMARY_FILE)))
    return code


def _bookkeeping(ctx: Context, keeper: LockKeeper, result: LoopResult, log: RunnerLog) -> None:
    """Once per run, after the last child exited and while the machine lock is
    still held (run.py's maintenance modes check this runner's token): the run
    index append, the coverage refresh, the results sweep. A failure is one
    Warn and never touches a verdict or the exit code."""
    steps = (("run index append",
              lambda: ctx.bridge.append_run_index(ctx.umbrella_root, result.run_ids, log)),
             ("coverage refresh", lambda: ctx.bridge.refresh_coverage(keeper.token, log)),
             ("results sweep", lambda: ctx.bridge.sweep_results(keeper.token, log)))
    for label, fn in steps:
        try:
            fn()
        except Exception as exc:  # noqa: BLE001 - bookkeeping never touches a verdict
            log.warn("Finish", "%s failed (%s: %s); verdicts unaffected"
                     % (label, type(exc).__name__, exc))


def _kill_running(running: Dict[int, Child], log: RunnerLog) -> None:
    for child in running.values():
        log.warn("Abort", "killing lane=%s slot=%s pid=%d" % (child.lane.lane_id, child.slot,
                                                            child.proc.pid))
        kill_tree(child.proc)


def cmd_set_slots(ctx: Context, args, log: RunnerLog) -> int:
    folder = RunFolder(ctx.run_dir(args.name))
    plan = folder.read_plan() if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Control", "no plan for run %r" % args.name)
    n = parallel.clamp_slots(args.count)
    if n is None:
        return _refuse(log, parallel.EXIT_USAGE, "Control", "slots must be %d..%d"
                       % (parallel.MIN_SLOTS, parallel.MAX_SLOTS))
    old, stop = folder.read_control(int(plan.get("slotsAtStart") or 2))
    folder.write_control(n, stop)
    log.info("Control", "run %s slots %d -> %d (takes effect at the next lane start; a "
                        "running lane is never stopped)" % (args.name, old, n))
    return 0


def cmd_stop(ctx: Context, args, log: RunnerLog) -> int:
    folder = RunFolder(ctx.run_dir(args.name))
    plan = folder.read_plan() if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Control", "no plan for run %r" % args.name)
    n, _stop = folder.read_control(int(plan.get("slotsAtStart") or 2))
    folder.write_control(n, True)
    log.info("Control", "run %s stop requested: running lanes finish, nothing new starts; "
                        "`resume --name %s` continues" % (args.name, args.name))
    return 0


def runner_state(ctx: Context, name: str, events: Sequence[Dict]) -> str:
    holder = machinelock.read_lock(machinelock.lock_path_for(ctx.umbrella_root))
    if isinstance(holder, dict) and holder.get("kind") == parallel.RUNNER_LOCK_KIND \
            and holder.get("runName") == name and ctx.pid_alive_fn(int(holder.get("pid") or 0)):
        return "running (pid %s)" % holder.get("pid")
    last = next((e for e in reversed(events) if e.get("kind") in ("start", "end")), None)
    if last is None:
        return "not started"
    if last.get("kind") == "end":
        return "finished %s (outcome=%s exit=%s)" % (last.get("utc"), last.get("outcome"),
                                                     last.get("exit"))
    return "not running (the last invocation ended without an end event)"


def cmd_status(ctx: Context, args, log: RunnerLog) -> int:
    folder = RunFolder(ctx.run_dir(args.name))
    plan = folder.read_plan() if parallel.valid_run_name(args.name) else None
    if plan is None:
        return _refuse(log, parallel.EXIT_USAGE, "Status", "no plan for run %r" % args.name)
    events = folder.events()
    n, stop = folder.read_control(int(plan.get("slotsAtStart") or 2))
    state = runner_state(ctx, args.name, events)
    include = bool(plan.get("includeExclusive"))
    lanes, _nf = flying_lanes(plan, include)
    view = dict(plan)
    view["lanes"] = [l.to_json() for l in lanes]
    if not state.startswith("running"):
        # A dispatch with no finish is only a live lane while the runner lives.
        events = [e for e in events if e.get("kind") != "dispatch"]
    for line in parallel.format_status(view, n, stop, events, folder.summary_lines(),
                                       time.time(), state):
        log.raw(line)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Fly harness lanes on several provisioned KSP copies at once "
                    "(harness/README.md, \"Parallel runs\")",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="exit codes: 0 GREEN, 1 RED, 2 NEEDS-PROVISION / refused selection, "
               "3 lock held / STOPPED, 4 runner error")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("run", help="plan and fly a selection")
    p.add_argument("--name", required=True, help="run name (folder under results/parallel/)")
    p.add_argument("--ids-file", action="append", metavar="FILE",
                   help="lane ids, one per line, # comments; '-' reads stdin (repeatable)")
    p.add_argument("--ids", action="append", metavar="A,B,C", help="comma-separated lane ids")
    p.add_argument("--tier", action="append", metavar="T", help="every lane of a tier (repeatable)")
    p.add_argument("--slots", default=str(parallel.DEFAULT_SLOTS),
                   help="lanes at once (default %d; change mid-run with set-slots)"
                   % parallel.DEFAULT_SLOTS)
    p.add_argument("--window", choices=list(parallel.WINDOW_MODES), default=parallel.WINDOW_DESKTOP,
                   help="where the test windows go (default desktop: the last virtual desktop "
                        "that is not the current one; minimize when there is only one)")
    p.add_argument("--include-exclusive", action="store_true",
                   help="also fly the exclusive lanes (cursor / foreground), last and alone")
    p.add_argument("--dry-run", action="store_true", help="print the plan; write and fly nothing")

    p = sub.add_parser("resume", help="fly the lanes of a run not settled yet")
    p.add_argument("--name", required=True)
    p.add_argument("--include-exclusive", action="store_true")

    p = sub.add_parser("set-slots", help="change how many lanes run at once")
    p.add_argument("--name", required=True)
    p.add_argument("count")

    p = sub.add_parser("stop", help="finish running lanes, start nothing new")
    p.add_argument("--name", required=True)

    p = sub.add_parser("status", help="running lanes, queue, verdict counts")
    p.add_argument("--name", required=True)
    return parser


COMMANDS = {"run": cmd_run, "resume": cmd_resume, "set-slots": cmd_set_slots,
            "stop": cmd_stop, "status": cmd_status}


def main(argv: Optional[Sequence[str]] = None, ctx: Optional[Context] = None) -> int:
    args = build_parser().parse_args(argv)
    ctx = ctx or Context()
    log = RunnerLog(ctx.console)
    try:
        return COMMANDS[args.command](ctx, args, log)
    except KeyboardInterrupt:
        log.error("Abort", "interrupted; children die with the runner's job object")
        return parallel.EXIT_ERROR
    except Exception:  # noqa: BLE001
        log.error("Error", "runner internal error: %s"
                  % traceback.format_exc(limit=5).replace("\n", " | "))
        return parallel.EXIT_ERROR
    finally:
        log.close()


if __name__ == "__main__":
    sys.exit(main())
