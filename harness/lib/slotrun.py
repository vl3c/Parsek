"""Pure decisions for run.py's install slots and parallel-run plumbing.

``slots`` owns the shared contract (slot names, ports, lock / stamp paths);
this module owns the decisions run.py makes ON TOP of it, so every one of them
is unit-tested without a KSP, a registry, a socket or a second process:

- which kRPC ports an instance dir flies with (a non-slot dir keeps the
  pre-slot 50000 / 50001),
- whether a ``--slot`` child may run under the runner that spawned it
  (the runner token in the machine lock and the runner pid),
- which live KSP process blocks an instance (any KSP for a serial run, only a
  KSP whose exe lives in THIS instance for a slot child),
- the Unity command line every launch passes (per-instance Player.log, windowed
  at the size settings.cfg carries),
- what run.py does to the child's window once it booted
  (``--window desktop|minimize|normal``),
- the ``--report-json`` document a caller reads back,
- the harness log name (it carries the slot, so two children that start in the
  same second never share one log).

Plan: project file harness/parallel-runs-plan-2026-11-14.md (v2 section).

ASCII only; stdlib only.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Tuple

import slots

# The ports every instance carried before slots existed. An instance dir whose
# basename is not a slot name (a test's --instance-dir) keeps them.
DEFAULT_KRPC_PORTS: Tuple[int, int] = (50000, 50001)

# The relative path of the kRPC server settings run.py stamps before a launch.
KRPC_SETTINGS_RELPATH: Tuple[str, ...] = ("GameData", "kRPC", "PluginData", "settings.cfg")

# Env the parallel runner sets for its ``run.py --slot`` children.
RUNNER_TOKEN_ENV = "PARSEK_HARNESS_RUNNER_TOKEN"
RUNNER_PID_ENV = "PARSEK_HARNESS_RUNNER_PID"
# Env run.py sets for the KSP child (read by Parsek's armed test-commands addon).
DESKTOP_ID_ENV = "PARSEK_AUTOMATION_DESKTOP_ID"
RETURN_FOREGROUND_ENV = "PARSEK_AUTOMATION_RETURN_FOREGROUND"

# The machine-lock payload key the runner stamps its token under.
LOCK_TOKEN_KEY = "token"

KSP_IMAGE_NAME = "KSP_x64.exe"
PLAYER_LOG_NAME = "Player.log"

WINDOW_DESKTOP = "desktop"
WINDOW_MINIMIZE = "minimize"
WINDOW_NORMAL = "normal"
WINDOW_MODES: Tuple[str, ...] = (WINDOW_DESKTOP, WINDOW_MINIMIZE, WINDOW_NORMAL)

WINDOW_ACTION_NONE = "none"
WINDOW_ACTION_MINIMIZE = "minimize"

# The line Parsek's armed test-commands addon logs once at startup (W1):
# ``[TestCommands] window placement desktop=<moved|failed hr=..|skipped|no-window>
# foreground=<...>``. Only the first word after ``desktop=`` is the outcome.
WINDOW_PLACEMENT_MARKER = "window placement desktop="
_PLACEMENT_RE = re.compile(r"\[TestCommands\]\s+window placement desktop=(\S+)"
                           r"(?:.*?\bforeground=(\S+))?")
PLACEMENT_MOVED = "moved"

REPORT_SCHEMA = "parsek-run-report/1"
REPORT_NOT_RUN = "NOT-RUN"
# Verdicts a lane can end on without a failure class.
_GREEN_VERDICTS = ("PASS", "EXPECTED-FAIL")


# ---------------------------------------------------------------------------
# Instance -> slot / ports.
# ---------------------------------------------------------------------------


def instance_slot_name(instance_dir: Optional[str]) -> Optional[str]:
    """The slot an instance dir is, by its basename, or None (not a slot name)."""
    if not instance_dir:
        return None
    name = os.path.basename(os.path.normpath(instance_dir))
    try:
        slots.parse_slot_name(name)
    except slots.SlotError:
        return None
    return name


def instance_copy_number(instance_dir: Optional[str]) -> int:
    """The copy number of a slot instance (1 for copy 1 and for a non-slot dir)."""
    slot = instance_slot_name(instance_dir)
    if slot is None:
        return 1
    return slots.parse_slot_name(slot)[1]


def krpc_ports_for_instance(instance_dir: Optional[str]) -> Tuple[int, int]:
    """(rpc, stream) ports an instance's kRPC server is stamped with before a launch."""
    slot = instance_slot_name(instance_dir)
    if slot is None:
        return DEFAULT_KRPC_PORTS
    return slots.krpc_ports_for_slot(slot)


# ---------------------------------------------------------------------------
# The runner token (slot mode only).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class RunnerTokenDecision:
    ok: bool
    reason: str


def decide_runner_token(env_token: Optional[str], env_pid: Optional[str],
                        lock_payload: Optional[Dict],
                        pid_alive_fn: Callable[[int], bool]) -> RunnerTokenDecision:
    """May a ``--slot`` child run (or keep running) under its runner?

    The runner holds the machine lock for its whole run with its token in the
    payload; the child's env carries the same token and the runner's pid. The
    child runs only while the lock still carries that token, the env pid parses
    and is alive, and (when the payload names a pid) the payload pid is the env
    pid. Any other state means the runner died or another holder owns the
    machine, and a child that kept flying would run beside it unlocked."""
    token = (env_token or "").strip()
    if not token:
        return RunnerTokenDecision(False, "no %s in the environment" % RUNNER_TOKEN_ENV)
    try:
        pid = int(str(env_pid or "").strip())
    except ValueError:
        return RunnerTokenDecision(False, "%s is not a pid: %r" % (RUNNER_PID_ENV, env_pid))
    if pid <= 0:
        return RunnerTokenDecision(False, "%s is not a pid: %r" % (RUNNER_PID_ENV, env_pid))
    if not isinstance(lock_payload, dict):
        return RunnerTokenDecision(False, "no machine lock is held (the runner must hold it)")
    held = lock_payload.get(LOCK_TOKEN_KEY)
    if not isinstance(held, str) or held != token:
        return RunnerTokenDecision(False, "the machine lock holder token does not match %s"
                                   % RUNNER_TOKEN_ENV)
    lock_pid = lock_payload.get("pid")
    if lock_pid is not None and lock_pid != pid:
        return RunnerTokenDecision(False, "the machine lock is held by pid=%s, not the "
                                          "runner pid=%d" % (lock_pid, pid))
    if not pid_alive_fn(pid):
        return RunnerTokenDecision(False, "the runner pid=%d is not alive" % pid)
    return RunnerTokenDecision(True, "runner pid=%d holds the machine lock" % pid)


# ---------------------------------------------------------------------------
# Process scoping.
# ---------------------------------------------------------------------------


def is_ksp_image(path_or_name: Optional[str]) -> bool:
    if not path_or_name:
        return False
    return os.path.basename(path_or_name.replace("\\", "/")).lower() == KSP_IMAGE_NAME.lower()


def blocking_ksp_pid(procs: Iterable[Tuple[int, Optional[str]]], instance_dir: str,
                     scoped: bool) -> Optional[int]:
    """The pid of the first KSP that blocks a launch into ``instance_dir``, or None.

    ``procs`` is every live KSP as (pid, exe path or None when unreadable).
    Serial runs (``scoped`` False) are blocked by ANY KSP on the machine, the
    pre-slot rule. A slot child is blocked only by a KSP whose exe lives in its
    own instance; an unreadable path counts as its own (busy, never somebody
    else's), per ``slots.exe_belongs_to_instance``."""
    for pid, exe in procs:
        if not scoped or slots.exe_belongs_to_instance(exe, instance_dir):
            return pid
    return None


# ---------------------------------------------------------------------------
# Launch command line.
# ---------------------------------------------------------------------------


def _positive_int(value: Optional[str]) -> Optional[int]:
    try:
        n = int(str(value).strip())
    except (TypeError, ValueError):
        return None
    return n if n > 0 else None


def ksp_launch_args(player_log: str, settings_values: Dict[str, str]) -> List[str]:
    """Unity player args for a launch: the per-instance Player.log, windowed, and
    the window size settings.cfg carries (read back after run.py's own screen
    patch). Unity's args cover the boot frames before KSP reads settings.cfg, so
    a fullscreen value in the machine-wide Unity prefs registry key can no longer
    win. A size settings.cfg does not carry as two positive ints is left out."""
    args = ["-logFile", player_log, "-screen-fullscreen", "0"]
    width = _positive_int((settings_values or {}).get("SCREEN_RESOLUTION_WIDTH"))
    height = _positive_int((settings_values or {}).get("SCREEN_RESOLUTION_HEIGHT"))
    if width is not None and height is not None:
        args += ["-screen-width", str(width), "-screen-height", str(height)]
    return args


def instance_player_log(instance_dir: str) -> str:
    return os.path.join(instance_dir, PLAYER_LOG_NAME)


# ---------------------------------------------------------------------------
# Window placement after boot.
# ---------------------------------------------------------------------------


def parse_window_placement(log_text: Optional[str]) -> Optional[Dict[str, str]]:
    """The LAST window-placement line in a KSP.log text, as
    {"desktop": outcome, "foreground": outcome or ""}, or None when absent."""
    found = None
    for m in _PLACEMENT_RE.finditer(log_text or ""):
        found = {"desktop": m.group(1), "foreground": m.group(2) or ""}
    return found


def decide_window_action(mode: str, placement: Optional[Dict[str, str]]) -> Tuple[str, str]:
    """(action, reason) for the booted child's window.

    normal: leave it. minimize: minimise without activating. desktop: Parsek
    moved its own window to the target virtual desktop; when its line is
    missing or says anything but ``moved``, fall back to minimising (measured at
    full speed with non-black screenshots)."""
    if mode == WINDOW_NORMAL:
        return WINDOW_ACTION_NONE, "window mode normal"
    if mode == WINDOW_MINIMIZE:
        return WINDOW_ACTION_MINIMIZE, "window mode minimize"
    if mode == WINDOW_DESKTOP:
        if placement is None:
            return WINDOW_ACTION_MINIMIZE, "no window placement line in KSP.log"
        outcome = placement.get("desktop") or ""
        if outcome == PLACEMENT_MOVED:
            return WINDOW_ACTION_NONE, "Parsek moved its window to the target desktop"
        return WINDOW_ACTION_MINIMIZE, "window placement desktop=%s" % (outcome or "?")
    return WINDOW_ACTION_NONE, "unknown window mode %r" % (mode,)


def should_return_foreground(mode: str, foreground_exe: Optional[str]) -> bool:
    """Hand the foreground back after launch only when the child is not meant to
    be the visible window (``normal`` lanes may need focus: pointer / focus
    lanes run that way) and the foreground window is not itself a KSP."""
    if mode == WINDOW_NORMAL:
        return False
    return not is_ksp_image(foreground_exe)


# ---------------------------------------------------------------------------
# Harness log name and the report document.
# ---------------------------------------------------------------------------


def harness_log_name(stamp: str, slot: Optional[str] = None) -> str:
    """``<stamp>_harness.log`` for a serial run, ``<stamp>_<slot>_harness.log``
    for a slot child."""
    if slot:
        return "%s_%s_harness.log" % (stamp, slot)
    return "%s_harness.log" % stamp


def _num(value) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return 0.0
    return float(value)


def build_run_report(slot: Optional[str], exit_code: int, selected_ids: Sequence[str],
                     results: Sequence[Tuple[Dict, str]]) -> Dict:
    """The ``--report-json`` document.

    ``results`` is every result record the invocation wrote, in write order,
    with its path (a record re-written later, e.g. the flakedThenPassed
    enrichment, replaces its earlier copy). A lane's attempts are its distinct
    run ids, its verdict / subkind are the last attempt's (the terminal), its
    wall is the sum of its attempts' walls. A selected lane with no record
    reads ``NOT-RUN`` (a stop request, an unresolvable instance)."""
    latest: Dict[str, Tuple[Dict, str]] = {}
    order: List[str] = []
    for result, path in results:
        rid = (result or {}).get("runId")
        if not rid:
            continue
        if rid not in latest:
            order.append(rid)
        latest[rid] = (result, path)
    by_lane: Dict[str, List[str]] = {}
    lane_order: List[str] = []
    for rid in order:
        sid = latest[rid][0].get("scenarioId") or "?"
        if sid not in by_lane:
            by_lane[sid] = []
            lane_order.append(sid)
        by_lane[sid].append(rid)
    for sid in selected_ids:
        if sid not in by_lane:
            lane_order.append(sid)
    lanes = []
    for sid in lane_order:
        rids = by_lane.get(sid, [])
        if not rids:
            lanes.append({"id": sid, "verdict": REPORT_NOT_RUN, "subkind": "",
                          "failureClass": REPORT_NOT_RUN, "attempts": 0, "runIds": [],
                          "wallSeconds": 0, "resultPaths": []})
            continue
        rids = sorted(rids, key=lambda r: (int(latest[r][0].get("attempt") or 0),
                                           order.index(r)))
        last = latest[rids[-1]][0]
        verdict = last.get("verdict") or "?"
        subkind = last.get("subkind") or ""
        failure = "" if verdict in _GREEN_VERDICTS else (subkind or verdict)
        lanes.append({
            "id": sid,
            "verdict": verdict,
            "subkind": subkind,
            "failureClass": failure,
            "attempts": len(rids),
            "runIds": list(rids),
            "wallSeconds": int(round(sum(_num(latest[r][0].get("wallSeconds"))
                                         for r in rids))),
            "resultPaths": [latest[r][1] for r in rids],
        })
    return {"schema": REPORT_SCHEMA, "slot": slot, "exitCode": int(exit_code),
            "lanes": lanes}
