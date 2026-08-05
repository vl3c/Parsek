"""The one-command tier entry point for unattended-STYLE harness runs.

    cd harness && python tools/cadence_runner.py --tier {daily|nightly|operator}

HOW IT IS USED TODAY: on demand. A human types the line above, walks away, and
comes back to a classified outcome and one skim line. Nothing on this machine
invokes it on a schedule -- ``scripts/register-cadence-tasks.ps1`` can turn that
on reproducibly if the decision is ever taken, and the exit-code contract below
is shaped so a scheduler would be a correct SECOND consumer, but it is not the
primary frame. (Operator decision 2026-08-05, pending the selection-layer
refactor tracked as HARNESS-TIER-TAXONOMY in docs/dev/todo-and-known-bugs.md.)

WHAT THIS IS. ``run.py`` already knows how to select, fly, verify and classify a
tier. What it does NOT know is anything about running for hours with nobody
watching the window: whether the box is about to fall asleep mid-flight, whether
an interactive KSP is already up, what to do when a sibling holds the machine
lock, and where to leave a one-line trace a human can skim afterwards. This
runner owns exactly that shell, and nothing else. It never decides a verdict,
never touches a result JSON, never re-runs a scenario, and never provisions.

INVOCATION. ``--tier`` is an argparse ``choices=`` over the three cadence tiers.
That restriction is load-bearing: run.py's ``--tier`` takes any string, and a
typo'd tier selects ZERO scenarios and exits 0 -- a walk-away run that reads
green having flown nothing. Neither a tired human nor a scheduler should be able
to express that.

EXIT-CODE CONTRACT (also what Task Scheduler would show as "Last Run Result"):

  0  GREEN            -- run.py exited 0 with at least one new result line.
  1  RED              -- something needs a human: a PARSEK-FAIL, a KILLED, an
                         INVALID, an XPASS (amber), or NO-SELECTION (the
                         should-be-unreachable "flew nothing" shape, which must
                         never read green to anyone).
  2  NEEDS-PROVISION  -- the instance is not fit (admission drift / missing
                         manifest / incomplete provision). A HUMAN decides
                         whether to provision; see the policy below.
  3  SKIPPED          -- nothing was attempted: the machine lock is held, a KSP
                         is already running, or we lost the preflight race.
  4  ERROR            -- the runner itself broke. run.py's own outcome, if it
                         got that far, is in the log.

POLICY 1 -- SKIP, DO NOT QUEUE. When the machine lock is held or a KSP_x64.exe
is alive, the runner exits 3 without invoking run.py. Letting run.py refuse
instead would be worse than useless: its refusal stamps one junk
``INVALID(instance-locked)`` result JSON PER SELECTED SPEC (46 of them for the
nightly tier) into ``results/`` and onto the contact-sheet index, burying the
real history under a run that never flew. A cadence that did not run is a
missing history line, which is a cheaper and more honest signal.

POLICY 2 -- NEVER AUTO-PROVISION. ``NEEDS-PROVISION`` reports and stops. The
provisioner DEPLOYs whatever ``Source/Parsek/bin/Debug/Parsek.dll`` the invoking
worktree currently holds, which is as likely to be a half-finished build as a
release candidate -- and the person who started this run walked away. Deciding
which worktree's DLL the automation instance should carry is a human call,
always:

    cd harness && python provision/provision.py --profile stock-minimal

POLICY 3 -- NEVER RE-RUN A FINDING. run.py owns retry policy. A PARSEK-FAIL
that reaches this runner is a finding to investigate, never something to fly
again until it goes away.

ARTIFACTS. Everything the runner itself produces lives under
``harness/results/cadence/`` (gitignored):

  ``<tier>_<YYYY-MM-DD_HHMMSS>.log``  one per invocation, UTC-stamped; the full
                                      tee of the runner's own lines AND run.py's
                                      merged stdout/stderr, written unbuffered so
                                      it is tail-able mid-flight.
  ``history.txt``                     ONE line per invocation -- the after-the-run
                                      skim surface.

That dated log is the RECORD OF TRUTH; console output is best-effort. The runner
survives losing its console mid-run (a broken wrapper pipe, a closed terminal):
console writes latch off with one warn INTO the file, and the file keeps going.

The runner rotates ONLY its own ``*.log`` files (60 days). It never prunes
result JSONs, ``summary.txt``, contact sheets or ``.claim`` stakes: those are
permanent by design (harness/README.md, "Contact sheets").

TEST SEAM. ``PARSEK_CADENCE_RUNPY_ARGS`` appends extra arguments to the run.py
invocation (shlex-split). It exists so the shell can be smoke-tested end to end
with ``--dry-run`` appended, which launches no game and takes no lock. It is
NOT an operating knob: it cannot suppress, rewrite or fake a verdict, and a
dry-run smoke classifies honestly as NO-SELECTION (exit 1) because zero results
were produced.

Stdlib only, mirroring the rest of harness/. Pure decisions live at module level
(``classify_outcome``, ``tally_verdicts``, ``format_history_line``,
``select_stale_cadence_logs``, ``evaluate_lock_preflight``) and are unit-tested
in ``harness/lib/test_cadence_runner.py``; everything below ``main`` is I/O.
"""

from __future__ import annotations

import argparse
import os
import shlex
import subprocess
import sys
import time
import traceback
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Callable, Dict, List, Optional, Sequence, Tuple

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.abspath(os.path.join(HERE, ".."))
WORKTREE_ROOT = os.path.abspath(os.path.join(HARNESS_ROOT, ".."))
UMBRELLA_ROOT = os.path.abspath(os.path.join(WORKTREE_ROOT, ".."))
RESULTS_DIR = os.path.join(HARNESS_ROOT, "results")
CADENCE_DIR = os.path.join(RESULTS_DIR, "cadence")
HISTORY_PATH = os.path.join(CADENCE_DIR, "history.txt")
RUN_PY = os.path.join(HARNESS_ROOT, "run.py")
SUMMARY_PATH = os.path.join(RESULTS_DIR, "summary.txt")

LIB_DIR = os.path.join(HARNESS_ROOT, "lib")
PROVISION_DIR = os.path.join(HARNESS_ROOT, "provision")
for _p in (HERE, LIB_DIR, PROVISION_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import machinelock  # noqa: E402  (lock file I/O; the decision is provlib's)
import provlib  # noqa: E402


# ---------------------------------------------------------------------------
# Vocabulary
# ---------------------------------------------------------------------------

# The tiers this entry point offers. NOT hlib.TIERS: `perpr` is a PR gate,
# `weekly` currently has no committed specs, and `pending-fixture` is an
# EXCLUSION marker. Widening this set is an operating decision, not a typo fix.
TIER_CHOICES: Tuple[str, ...] = ("daily", "nightly", "operator")

OUTCOME_GREEN = "GREEN"
OUTCOME_RED = "RED"
OUTCOME_NEEDS_PROVISION = "NEEDS-PROVISION"
OUTCOME_NO_SELECTION = "NO-SELECTION"
OUTCOME_LOCKED = "LOCKED"
OUTCOME_SKIPPED = "SKIPPED"
OUTCOME_ERROR = "ERROR"

EXIT_GREEN = 0
EXIT_RED = 1
EXIT_NEEDS_PROVISION = 2
EXIT_SKIPPED = 3
EXIT_ERROR = 4

# Outcome -> runner exit code. NO-SELECTION maps to RED's code deliberately: the
# invocation did not fly what it was asked to fly, and that must be as loud as a
# failure rather than as quiet as a pass.
OUTCOME_EXIT_CODES: Dict[str, int] = {
    OUTCOME_GREEN: EXIT_GREEN,
    OUTCOME_RED: EXIT_RED,
    OUTCOME_NO_SELECTION: EXIT_RED,
    OUTCOME_NEEDS_PROVISION: EXIT_NEEDS_PROVISION,
    OUTCOME_LOCKED: EXIT_SKIPPED,
    OUTCOME_SKIPPED: EXIT_SKIPPED,
    OUTCOME_ERROR: EXIT_ERROR,
}

# summary.txt verdict vocabulary, in the order a tally reads best (the good news
# first, then the things that cost a human time).
KNOWN_VERDICTS: Tuple[str, ...] = (
    "PASS", "EXPECTED-FAIL", "PARSEK-FAIL", "KILLED", "INVALID", "XPASS",
)
UNPARSED_VERDICT = "UNPARSED"

# Evidence markers scanned out of run.py's merged stdout. These are the exact
# strings run.py emits (run.py's ADMIT step and its whole-invocation lock
# refusal); a change to either must be mirrored here, which is why they are
# named constants rather than inline literals.
#
# `result=DRIFT` alone is NOT a marker: run.py also emits
# `mission venv-admit stamp=... result=DRIFT` for a mission-VENV drift, which
# provision.py does not fix (missions/bootstrap_venv.py does) -- so a bare
# result=DRIFT match would label a venv problem NEEDS-PROVISION and send the
# operator down the wrong recovery. The INSTANCE admit line is recognised only
# by the `admit instance=` + `result=DRIFT` co-occurrence in
# ``is_admission_drift_line``.
ADMISSION_DRIFT_MARKERS: Tuple[str, ...] = (
    "admit drift field=",     # per-field drift detail
    "manifest-missing",
    "provision-incomplete",
)
INSTANCE_LOCKED_MARKERS: Tuple[str, ...] = (
    "machine lock held by a live sibling run or provision",   # whole-invocation refusal
    "lost the machine lock mid-selection",                    # heartbeat reclaim mid-batch
    "instance-locked",
)

PROVISION_HINT = (
    "instance not fit -- provision is a HUMAN call (a worktree build may be "
    "WIP); run `cd harness && python provision/provision.py --profile "
    "stock-minimal` from the intended worktree")

CADENCE_LOG_KEEP_DAYS = 60
LOG_STAMP_FORMAT = "%Y-%m-%d_%H%M%S"

# How many evidence lines the streaming tee retains for classification. The tee
# writes EVERY line to the log; only lines matching a marker are held in memory,
# so a 50k-line nightly costs a bounded handful of strings.
EVIDENCE_LINE_CAP = 200

# Written INTO THE FILE (never to the console -- the console is what just died)
# the first time a console write fails. See TeeLog.
CONSOLE_LOST_TEMPLATE = "[Cadence][Warn][Log] console lost (%s); continuing file-only"


# ---------------------------------------------------------------------------
# Pure decisions
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class CadenceOutcome:
    outcome: str
    detail: str

    @property
    def exit_code(self) -> int:
        return OUTCOME_EXIT_CODES.get(self.outcome, EXIT_ERROR)


def tally_verdicts(summary_lines: Sequence[str]) -> Dict[str, int]:
    """Count verdicts in ``summary.txt`` lines.

    One line looks like::

        2026-08-05T01:12:33Z PASS S1.6-map-dwell attempt=1 wall=1841s
        2026-08-05T02:44:10Z INVALID B12-foo attempt=2 wall=12s note=flakedThenPassed

    so the verdict is the SECOND whitespace token. A line whose second token is
    not a known verdict counts as ``UNPARSED`` rather than being dropped: a
    format drift must show up in the tally, not silently shrink it.
    """
    counts: Dict[str, int] = {}
    for raw in summary_lines:
        line = (raw or "").strip()
        if not line:
            continue
        parts = line.split()
        verdict = parts[1] if len(parts) >= 2 and parts[1] in KNOWN_VERDICTS else UNPARSED_VERDICT
        counts[verdict] = counts.get(verdict, 0) + 1
    return counts


def format_tally(counts: Dict[str, int]) -> str:
    """Render a tally in a stable order: known verdicts first, then anything
    else sorted. Empty tally renders as ``no-results``."""
    if not counts:
        return "no-results"
    ordered = [v for v in KNOWN_VERDICTS if counts.get(v)]
    ordered += sorted(k for k in counts if k not in KNOWN_VERDICTS and counts[k])
    return " ".join("%s=%d" % (v, counts[v]) for v in ordered)


def is_admission_drift_line(line: str) -> bool:
    """True when a line is genuine INSTANCE-admission drift evidence. The
    instance admit summary (`admit instance=%s manifest=%s result=DRIFT`) is
    matched by co-occurrence so the mission venv-admit line (`mission
    venv-admit stamp=... result=DRIFT`) can never classify NEEDS-PROVISION --
    provisioning does not touch the venv, so that hint would be wrong."""
    text = line or ""
    if "admit instance=" in text and "result=DRIFT" in text:
        return True
    return any(m in text for m in ADMISSION_DRIFT_MARKERS)


def is_evidence_line(line: str) -> bool:
    """True for a run.py stdout line the classifier may need. Used by the
    streaming tee to keep memory bounded without weakening classification."""
    text = line or ""
    return is_admission_drift_line(text) or \
        any(m in text for m in INSTANCE_LOCKED_MARKERS)


def _contains_any(lines: Sequence[str], markers: Sequence[str]) -> bool:
    return any(any(m in (line or "") for m in markers) for line in lines)


def classify_outcome(run_exit_code: int,
                     new_summary_lines: Sequence[str],
                     runner_captured_lines: Sequence[str]) -> CadenceOutcome:
    """Turn (run.py exit code, new summary.txt lines, captured stdout) into the
    outcome a human reads in ``history.txt``.

    PRECEDENCE, and why:

    1. ``NO-SELECTION`` first, on exit 2 (run.py's "no selection given") or on a
       green exit that produced zero result lines. It outranks GREEN because
       "exited 0" and "flew something" are different claims, and only the second
       one is worth a green light. ``choices=`` should make this unreachable;
       it is classified anyway because the failure mode it guards against --
       a walk-away run reading green while flying nothing -- is exactly the one
       nobody would notice.
    2. ``GREEN`` on exit 0 with results.
    3. ``LOCKED`` before NEEDS-PROVISION, but ONLY when nothing actually flew
       (every new summary line is INVALID, or there are none). A refused
       invocation never reached ADMIT, so drift evidence cannot be genuine on
       that path, and ordering this way means a lost preflight race is never
       mislabelled as a broken instance. The all-INVALID guard matters for the
       OTHER lock path -- run.py losing its lease mid-selection after flying
       real scenarios -- where LOCKED's "skipped, nothing attempted" exit code
       would bury whatever those scenarios found. That case falls through to
       RED with the tally.
    4. ``NEEDS-PROVISION`` before generic RED: "the instance is not fit" is a
       different action for a human than "a test failed", and the INVALID rows
       an unfit instance produces are not product findings.
    5. ``RED`` otherwise.

    The verdict TALLY is in the detail of every non-skip outcome, including
    NEEDS-PROVISION, so a KILLED can never be masked by the surrounding label or
    by twenty passes beside it.
    """
    counts = tally_verdicts(new_summary_lines)
    total = sum(counts.values())
    tally = format_tally(counts)
    killed = counts.get("KILLED", 0)
    if killed:
        tally += " [%d KILLED -- budget kill, investigate the run]" % killed

    if run_exit_code == 2 or (run_exit_code == 0 and total == 0):
        reason = ("run.py reported no scenario selection" if run_exit_code == 2
                  else "run.py exited 0 but wrote no result line")
        return CadenceOutcome(OUTCOME_NO_SELECTION,
                              "%s -- nothing flew (%s)" % (reason, tally))

    if run_exit_code == 0:
        return CadenceOutcome(OUTCOME_GREEN, tally)

    nothing_flew = counts.get("INVALID", 0) == total
    if nothing_flew and _contains_any(runner_captured_lines, INSTANCE_LOCKED_MARKERS):
        return CadenceOutcome(
            OUTCOME_LOCKED,
            "lost the preflight race -- run.py refused on the machine lock (%s)" % tally)

    if any(is_admission_drift_line(l) for l in runner_captured_lines) or \
            any(is_admission_drift_line(l) for l in new_summary_lines):
        return CadenceOutcome(OUTCOME_NEEDS_PROVISION,
                              "%s (%s)" % (PROVISION_HINT, tally))

    return CadenceOutcome(OUTCOME_RED, tally)


def format_history_line(now_iso: str, tier: str, outcome: str, exit_code: int,
                        detail: str, log_name: str) -> str:
    """One skim-surface line. Whitespace-collapsed so a multi-line detail can
    never break the one-line-per-invocation contract."""
    flat = " ".join((detail or "").split()) or "-"
    return "%s tier=%s outcome=%s exit=%d %s log=%s" % (
        now_iso, tier, outcome, exit_code, flat, log_name)


def parse_cadence_log_stamp(filename: str) -> Optional[datetime]:
    """UTC datetime encoded in a ``<tier>_<YYYY-MM-DD_HHMMSS>.log`` name, or
    None when the name is not one of ours. Deliberately strict: the tier prefix
    must be a known cadence tier, so nothing else in the directory can ever be
    mistaken for a rotatable runner log."""
    name = filename or ""
    if not name.endswith(".log"):
        return None
    stem = name[: -len(".log")]
    parts = stem.split("_")
    if len(parts) < 3 or parts[0] not in TIER_CHOICES:
        return None
    try:
        stamp = datetime.strptime("_".join(parts[-2:]), LOG_STAMP_FORMAT)
    except ValueError:
        return None
    return stamp.replace(tzinfo=timezone.utc)


def select_stale_cadence_logs(filenames: Sequence[str], now: datetime,
                              keep_days: int = CADENCE_LOG_KEEP_DAYS) -> List[str]:
    """The runner's OWN logs older than ``keep_days``, oldest first.

    Never selects ``history.txt``, a non-``.log`` file, a name whose stamp does
    not parse, or a stamp in the FUTURE (clock skew must not delete evidence).
    A log exactly ``keep_days`` old is KEPT -- the boundary is strictly older.
    Nothing outside ``results/cadence/`` is ever passed in.
    """
    cutoff = now - timedelta(days=keep_days)
    stale: List[Tuple[datetime, str]] = []
    for name in filenames:
        stamp = parse_cadence_log_stamp(name)
        if stamp is None or stamp > now:
            continue
        if stamp < cutoff:
            stale.append((stamp, name))
    return [name for _, name in sorted(stale)]


def evaluate_lock_preflight(existing_lock: Optional[Dict], pid: int, now: float,
                            is_alive_fn: Callable[[int], bool],
                            lease_seconds: Optional[float] = provlib.DEFAULT_LEASE_SECONDS,
                            ) -> Tuple[bool, str]:
    """Advisory preflight: may this cadence proceed to invoke run.py?

    Composes ``provlib.acquire_lock`` as a PURE decision over an already-read
    lock payload -- nothing is written, nothing is reclaimed, and run.py's own
    acquire stays the authority. THIS IS A RACE BY CONSTRUCTION: a sibling can
    take the lock between this read and run.py's acquire, in which case run.py
    refuses and the run classifies LOCKED. That is fine -- the preflight is an
    optimisation to avoid stamping N junk INVALID rows in the common case, not a
    correctness mechanism, and it must never be promoted into one (a runner that
    HELD the lock across the spawn would deadlock run.py against itself).

    Returns ``(proceed, reason)``; ``reason`` is the provlib decision reason.
    """
    decision = provlib.acquire_lock(existing_lock, pid, now, is_alive_fn,
                                    lease_seconds=lease_seconds)
    return bool(decision.acquired), decision.reason


# ---------------------------------------------------------------------------
# I/O shell
# ---------------------------------------------------------------------------


def console_write_default(text: str) -> None:
    """The real console write, isolated behind a name so TeeLog can be handed a
    failing one in tests without monkeypatching builtins."""
    print(text)
    sys.stdout.flush()


class TeeLog:
    """Runner log: every line goes to the console AND the dated cadence log,
    flushed per line so the file is tail-able while a multi-hour tier runs.

    THE FILE IS THE RECORD OF TRUTH; THE CONSOLE IS BEST-EFFORT.

    This runner is started and left alone -- through an agent's or a shell
    wrapper's stdout pipe, a terminal that may be closed, a detached session,
    or (if ever scheduled) a console host that can vanish. Any of those can
    break the pipe underneath a multi-hour flight. An unguarded ``print`` then
    raises OSError out of ``raw`` and unwinds the whole run: no history line, no
    classification, and an orphaned run.py still holding the machine lock. Since
    the console is nobody's evidence once the human has walked away, console
    death is a DOWNGRADE (latch it, note it in the file, keep flying), never an
    outcome.
    """

    def __init__(self, path: str, console_write: Optional[Callable[[str], None]] = None):
        self.path = path
        # Latched on first console failure: one warn into the file, then never
        # touch the console again (a broken pipe stays broken, and retrying it
        # per line would raise on every subsequent write).
        self.console_dead = False
        self._console_write = console_write or console_write_default
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self._fh = open(path, "a", encoding="utf-8")

    def _file_write(self, text: str) -> None:
        if self._fh is None:
            return
        try:
            self._fh.write(text + "\n")
            self._fh.flush()
        except (OSError, ValueError):
            # Both surfaces are now gone. Raising here would kill the run for a
            # LOG, which is the precise failure this class exists to prevent.
            # Drop the handle so we stop retrying a dead file every line.
            self._fh = None

    def raw(self, text: str) -> None:
        if not self.console_dead:
            try:
                self._console_write(text)
            except (OSError, ValueError) as exc:
                # ValueError covers "I/O operation on closed file", which is how
                # a closed stdout presents rather than OSError.
                self.console_dead = True
                self._file_write(CONSOLE_LOST_TEMPLATE % exc)
        self._file_write(text)

    def log(self, level: str, step: str, message: str) -> None:
        self.raw("[Cadence][%s][%s] %s" % (level, step, message))

    def info(self, step, msg):
        self.log("Info", step, msg)

    def warn(self, step, msg):
        self.log("Warn", step, msg)

    def error(self, step, msg):
        self.log("Error", step, msg)

    def close(self) -> None:
        if self._fh:
            self._fh.close()
            self._fh = None


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def utcnow_iso() -> str:
    return utcnow().strftime("%Y-%m-%dT%H:%M:%SZ")


def pid_alive(pid: int) -> bool:
    """Liveness probe for the lock preflight. FAILS CLOSED (mirrors
    ``run.py`` Runtime.pid_alive): if the probe cannot run, report ALIVE so the
    preflight skips rather than queueing behind a holder it failed to verify was
    gone. Parses the CSV pid COLUMN, not a substring of the whole row (a bare
    `str(pid) in text` also matches the Mem Usage column)."""
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "PID eq %d" % pid, "/FO", "CSV", "/NH"],
            capture_output=True, text=True)
    except OSError:
        return True
    for row in (out.stdout or "").splitlines():
        cells = [c.strip('"') for c in row.split('","')]
        if len(cells) >= 2 and cells[1].strip() == str(pid):
            return True
    return False


def ksp_pid() -> Optional[int]:
    """Pid of a live ``KSP_x64.exe``, ``-1`` when one is alive but its pid did
    not parse, else None. Deliberately COARSE, exactly like run.py's zombie
    preflight: the probe cannot bind a pid to an install, so an interactive dev
    KSP also blocks the cadence -- correct on a one-GPU box, where it would
    perturb the flight anyway."""
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq KSP_x64.exe", "/FO", "CSV", "/NH"],
            capture_output=True, text=True)
    except OSError:
        return None
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


# SetThreadExecutionState flags (winbase.h).
_ES_CONTINUOUS = 0x80000000
_ES_SYSTEM_REQUIRED = 0x00000001


def assert_system_awake(log: TeeLog) -> bool:
    """Keep the machine awake for the whole invocation.

    You start a multi-hour tier and walk away; the idle timer then suspends the
    box roughly two minutes later -- mid boot, mid flight, mid verification --
    and a KSP that was asleep for four hours is not evidence of anything. The
    same hold covers the case where a future scheduler WAKES the machine to run
    (it would otherwise re-sleep on the same timer). ES_SYSTEM_REQUIRED with
    ES_CONTINUOUS holds the system awake until the flag is cleared (per THREAD,
    and the main thread lives for the whole run). DISPLAY sleep is deliberately
    NOT requested: the monitor powering off does not suspend KSP, and holding a
    display on for hours for nobody is rude to the hardware.

    Non-win32 and any ctypes failure degrade to a Warn and continue -- an
    unattended run that cannot pin the power state is still worth attempting.
    """
    if sys.platform != "win32":
        log.info("Power", "system-awake skipped: platform=%s (win32 only)" % sys.platform)
        return False
    try:
        import ctypes
        rc = ctypes.windll.kernel32.SetThreadExecutionState(
            _ES_CONTINUOUS | _ES_SYSTEM_REQUIRED)
    except Exception as exc:  # noqa: BLE001 - any ctypes trouble is non-fatal here
        log.warn("Power", "system-awake request failed (%s); the machine may sleep mid-run" % exc)
        return False
    if not rc:
        log.warn("Power", "system-awake request returned 0 (previous state unknown); "
                          "the machine may sleep mid-run")
        return False
    # rc is the PREVIOUS execution state. ctypes types the return as a signed
    # int, so ES_CONTINUOUS (0x80000000) comes back negative; mask it before
    # rendering or the log reads "prev=0x-80000000".
    log.info("Power", "system-awake asserted (ES_CONTINUOUS|ES_SYSTEM_REQUIRED, prev=0x%08X)"
             % (rc & 0xFFFFFFFF))
    return True


def release_system_awake(log: TeeLog) -> None:
    if sys.platform != "win32":
        return
    try:
        import ctypes
        ctypes.windll.kernel32.SetThreadExecutionState(_ES_CONTINUOUS)
        log.info("Power", "system-awake released")
    except Exception as exc:  # noqa: BLE001
        log.warn("Power", "system-awake release failed: %s" % exc)


def read_summary_lines(path: str = SUMMARY_PATH) -> List[str]:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read().splitlines()
    except OSError:
        return []


def append_history(line: str, log: Optional[TeeLog] = None) -> None:
    try:
        os.makedirs(CADENCE_DIR, exist_ok=True)
        with open(HISTORY_PATH, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError as exc:
        if log:
            log.error("History", "could not append to %s: %s" % (HISTORY_PATH, exc))


def refresh_index(log: TeeLog) -> None:
    """Refresh ``results/index.html``. run.py already does this after every
    attempt and after its own INVALID short-circuit, so this only matters on the
    paths where run.py never ran (a skip) -- but it is unconditional and cheap,
    and a stale index is exactly what a post-run review would misread.
    Failure-isolated, mirroring run.py's own sheet isolation: never changes the
    exit code."""
    try:
        import contact_sheet
        os.makedirs(RESULTS_DIR, exist_ok=True)
        path = contact_sheet.generate_index(RESULTS_DIR)
        log.info("Index", "contact-sheet index refreshed %s" % path)
    except Exception as exc:  # noqa: BLE001 - a sheet failure must never sink a cadence
        log.warn("Index", "contact-sheet index refresh failed: %s" % exc)


def rotate_cadence_logs(log: TeeLog, keep_days: int = CADENCE_LOG_KEEP_DAYS) -> int:
    """Delete the runner's own stale logs. Scoped to ``results/cadence/*.log``
    and to names the strict parser recognises, so ``history.txt``, result JSONs,
    contact sheets and ``.claim`` stakes are structurally out of reach."""
    try:
        names = os.listdir(CADENCE_DIR)
    except OSError:
        return 0
    stale = select_stale_cadence_logs(names, utcnow(), keep_days)
    removed = 0
    for name in stale:
        try:
            os.remove(os.path.join(CADENCE_DIR, name))
            removed += 1
        except OSError as exc:
            log.warn("Rotate", "could not remove %s: %s" % (name, exc))
    if removed:
        log.info("Rotate", "rotated %d cadence log(s) older than %d days (%d candidate(s))"
                 % (removed, keep_days, len(stale)))
    return removed


def build_run_argv(tier: str) -> List[str]:
    """The run.py command line. ``PARSEK_CADENCE_RUNPY_ARGS`` (test seam, see the
    module docstring) appends extra args; it can add flags but never removes
    ``--tier``."""
    argv = [sys.executable, RUN_PY, "--tier", tier]
    extra = os.environ.get("PARSEK_CADENCE_RUNPY_ARGS", "").strip()
    if extra:
        argv.extend(shlex.split(extra))
    return argv


def spawn_run(argv: Sequence[str], log: TeeLog) -> Tuple[int, List[str]]:
    """Run run.py, teeing its merged stdout/stderr into the runner log line by
    line, and return ``(exit_code, evidence_lines)``.

    NO TIMER HERE, deliberately. run.py's per-spec budgets own hang-killing, and
    Ctrl-C (or, under a scheduler, its ExecutionTimeLimit) is the outer backstop;
    a third timeout in the middle would only be able to kill a run WITHOUT
    writing its verdict.
    """
    evidence: List[str] = []
    proc = subprocess.Popen(list(argv), cwd=HARNESS_ROOT,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, bufsize=1, encoding="utf-8", errors="replace")
    if proc.stdout is not None:
        for raw in proc.stdout:
            line = raw.rstrip("\r\n")
            log.raw(line)
            if len(evidence) < EVIDENCE_LINE_CAP and is_evidence_line(line):
                evidence.append(line)
        proc.stdout.close()
    return proc.wait(), evidence


def _finish(log: TeeLog, tier: str, outcome: CadenceOutcome, log_name: str) -> int:
    """Common tail: history line, index refresh, log rotation, exit code."""
    exit_code = outcome.exit_code
    line = format_history_line(utcnow_iso(), tier, outcome.outcome, exit_code,
                               outcome.detail, log_name)
    append_history(line, log)
    log.info("History", line)
    refresh_index(log)
    rotate_cadence_logs(log)
    log.info("Done", "tier=%s outcome=%s exit=%d" % (tier, outcome.outcome, exit_code))
    return exit_code


def run(tier: str, log: TeeLog, log_name: str) -> int:
    log.info("Start", "cadence tier=%s harness=%s umbrella=%s python=%s"
             % (tier, HARNESS_ROOT, UMBRELLA_ROOT, sys.executable))
    assert_system_awake(log)
    try:
        # ---- PREFLIGHT (advisory; run.py's own acquire is authoritative) ----
        lock_path = machinelock.lock_path_for(UMBRELLA_ROOT)
        existing = machinelock.read_lock(lock_path)
        proceed, reason = evaluate_lock_preflight(existing, os.getpid(), time.time(),
                                                  pid_alive)
        if not proceed:
            holder = machinelock.describe_holder(existing)
            log.warn("Preflight", "SKIP: machine lock held (%s) -- not queueing behind "
                                  "the holder (%s, lock=%s)" % (holder, reason, lock_path))
            return _finish(log, tier, CadenceOutcome(
                OUTCOME_SKIPPED, "machine lock held (%s)" % holder), log_name)
        log.info("Preflight", "machine lock free (decision=%s, lock=%s)" % (reason, lock_path))

        busy = ksp_pid()
        if busy is not None:
            log.warn("Preflight", "SKIP: KSP_x64.exe already running (pid=%s; interactive "
                                  "session or zombie) -- not queueing" % busy)
            return _finish(log, tier, CadenceOutcome(
                OUTCOME_SKIPPED, "KSP_x64.exe alive (pid=%s)" % busy), log_name)
        log.info("Preflight", "no live KSP_x64.exe; proceeding")

        # ---- INVOKE ---------------------------------------------------------
        before = len(read_summary_lines())
        argv = build_run_argv(tier)
        log.info("Invoke", "spawning %s (cwd=%s, summary lines before=%d)"
                 % (" ".join(argv), HARNESS_ROOT, before))
        started = time.time()
        exit_code, evidence = spawn_run(argv, log)
        elapsed = int(round(time.time() - started))
        new_lines = read_summary_lines()[before:]
        log.info("Invoke", "run.py exited %d after %ds; %d new summary line(s)"
                 % (exit_code, elapsed, len(new_lines)))

        outcome = classify_outcome(exit_code, new_lines, evidence)
        level = log.info if outcome.outcome == OUTCOME_GREEN else log.warn
        level("Classify", "%s: %s" % (outcome.outcome, outcome.detail))
        return _finish(log, tier, outcome, log_name)
    finally:
        release_system_awake(log)


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="One-command cadence-tier runner for the Parsek automated-test "
                    "harness (invoked on demand; schedulable via "
                    "scripts/register-cadence-tasks.ps1 if that is ever wanted)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=("exit codes (also Task Scheduler's 'Last Run Result', if ever scheduled):\n"
                "  0  GREEN            every selected scenario terminated green\n"
                "  1  RED              a finding, a KILLED, an XPASS, or nothing flew\n"
                "  2  NEEDS-PROVISION  the instance is not fit; provisioning is a HUMAN call\n"
                "  3  SKIPPED          machine lock held / a KSP is running; nothing attempted\n"
                "  4  ERROR            the runner itself broke\n"))
    parser.add_argument("--tier", required=True, choices=list(TIER_CHOICES),
                        help="which cadence tier to fly (exact tier match; "
                             "run.py's own --tier accepts any string and silently "
                             "selects zero on a typo, which is why this is a choices set)")
    args = parser.parse_args(argv)

    stamp = utcnow().strftime(LOG_STAMP_FORMAT)
    log_name = "%s_%s.log" % (args.tier, stamp)
    log_path = os.path.join(CADENCE_DIR, log_name)
    log: Optional[TeeLog] = None
    try:
        log = TeeLog(log_path)
        return run(args.tier, log, log_name)
    except Exception:  # noqa: BLE001 - an internal fault must still leave a history line
        # ORDER IS LOAD-BEARING: the history line is appended FIRST, before any
        # attempt to report the fault. Reporting is an I/O act that can itself
        # fail (the very pipe that broke may be what threw us in here), and a
        # handler that dies while announcing a problem leaves no trace of the
        # problem at all. `log=None` keeps this append off the logger entirely.
        detail = "runner internal error: %s" % traceback.format_exc(limit=3).replace("\n", " | ")
        append_history(format_history_line(utcnow_iso(), args.tier, OUTCOME_ERROR,
                                           EXIT_ERROR, detail, log_name), None)
        # Belt AND braces: TeeLog no longer raises on console loss, but this
        # handler must hold even if the logger is broken in a way it does not
        # model, so the announcement can never change the outcome.
        try:
            if log:
                log.error("Error", detail)
            else:
                console_write_default("[Cadence][Error][Error] %s" % detail)
        except Exception:  # noqa: BLE001
            pass
        return EXIT_ERROR
    finally:
        if log:
            log.close()


if __name__ == "__main__":
    sys.exit(main())
