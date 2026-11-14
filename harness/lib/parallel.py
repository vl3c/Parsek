"""Parallel harness runs: every decision behind ``tools/parallel_runner.py`` (pure).

The parallel runner flies harness lanes on several provisioned KSP copies
(slots, ``slots.py``) at once, one ``run.py --slot <s>`` child per lane. This
module owns what the runner DECIDES; the shell owns files, processes, the
registry and the clock, and hands plain values in.

- Selection: ids from files / ``--ids`` / ``--tier``, unknown ids refused.
- Exclusive lanes: a lane that moves the OS cursor or takes the foreground
  (``[runtime] exclusive = true``, or a step this module recognises as a pointer
  or focus step) never shares the machine with another lane.
- Duration estimates (p50) and the longest-first order.
- The dispatch decision: which lane starts next on which slot, or why nothing
  starts now (slot cap, a free slot of the profile, the memory guard that
  projects free memory after the new copy loads, the dispatch gap after a start, an
  exclusive lane, a stop request).
- The Windows virtual-desktop registry values, parsed from bytes.
- The run folder's ``summary.txt`` lines, the settled predicate a resume uses
  (the same one ``tier_runner.full_run_done`` applies) and the exit code (the
  ``run.py`` selection rule).
- The ``status`` report an agent reads.

Stdlib only. Nothing here reads a file, a clock or an environment variable.
"""

import json
import re
import uuid
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence, Tuple

import hlib
import slots

PLAN_SCHEMA = "parsek-parallel-plan/1"
CONTROL_SCHEMA = "parsek-parallel-control/1"

# Run folder layout under harness/results/parallel/<run>/.
PARALLEL_RESULTS_DIRNAME = "parallel"
PLAN_FILE = "plan.json"
CONTROL_FILE = "control.json"
EVENTS_FILE = "events.jsonl"
SUMMARY_FILE = "summary.txt"
RUNNER_LOG_FILE = "runner.log"
LANES_DIRNAME = "lanes"

_RUN_NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")

# Estimate for a lane no record has timed (seconds).
DEFAULT_P50_SECONDS = 120.0
P50_SOURCE_DURATION = "duration.json"
P50_SOURCE_INDEX = "run-index"
P50_SOURCE_DEFAULT = "default"

# Memory guard. A lane beyond the first running one starts only when the
# machine would still keep a floor of free RAM and VRAM AFTER that new copy has
# fully loaded:
#   projected = reading - remaining footprint of each booting copy
#               - one full footprint for the new copy
# separately for RAM and VRAM, held when projected < MIN_FREE_*. An unavailable
# reading is not a refusal. Measured on the owner's PC (2026-11-14, 1280x720
# window): one copy peaks at 4.25 GB RAM and about 1 GB VRAM; free RAM is
# 14.8 GB with no KSP running and 6.07 GB at its lowest with two copies flying;
# three copies ran at full speed with 1.7 GB of RAM free.
COPY_FOOTPRINT_RAM_GB = 4.5
COPY_FOOTPRINT_VRAM_GB = 2.0
MIN_FREE_RAM_GB = 1.5
MIN_FREE_VRAM_GB = 1.5

# A copy started less than BOOT_WINDOW_SECONDS ago has not taken all of its
# memory yet, and the reading already holds the part it has taken. Its remaining
# footprint ramps down linearly: footprint * max(0, 1 - elapsed / window). Boot
# measured 37 s alone and 42-45 s beside other copies; daily lanes last 48-60 s.
BOOT_WINDOW_SECONDS = 60.0

# Seconds after a lane start before another may start while one runs. It is the
# machine-wide launch spacing run.py enforces at the launch stamp, so a child
# the runner starts does not sit waiting on that stamp; memory pressure from a
# copy still booting is the guard's job, not this gap's.
DISPATCH_GAP_SECONDS = slots.DEFAULT_LAUNCH_SPACING_SECONDS

DEFAULT_SLOTS = 2
MIN_SLOTS = 1
MAX_SLOTS = 8

# Window modes passed to each run.py child (``--window``).
WINDOW_DESKTOP = "desktop"
WINDOW_MINIMIZE = "minimize"
WINDOW_NORMAL = "normal"
WINDOW_MODES: Tuple[str, ...] = (WINDOW_DESKTOP, WINDOW_MINIMIZE, WINDOW_NORMAL)
DESKTOP_HINT = ("create a second virtual desktop with Win+Ctrl+D to keep test windows "
                "off your screen")

# Exclusive lanes.
EXCLUSIVE_SPEC_KEY = hlib.EXCLUSIVE_SPEC_KEY
# (cmd, arg, value): a step that moves the OS cursor. `UiAction op=pointer` is the
# seam's pointer op; `StockScreen act=hover` moves the cursor onto a stock control
# (ParsekTestCommandAddon.StockScreen.cs, SetCursorPos).
POINTER_STEP_RULES: Tuple[Tuple[str, str, str], ...] = (
    ("UiAction", "op", "pointer"),
    ("StockScreen", "act", "hover"),
)
# Any step arg `focus=true` brings the game window to the foreground.
FOCUS_STEP_ARG = "focus"
# In-game test categories that move the OS cursor (DisabledHoverEchoImguiTest.cs
# calls SetCursorPos), named in a `RunTests` step's comma-separated `category`.
POINTER_TEST_CATEGORIES: Tuple[str, ...] = ("DisabledHoverEcho",)

# Verdicts. The run.py terminal verdicts plus one the runner writes itself when
# a child left no report (it crashed, or was killed with the runner).
VERDICT_NO_REPORT = "NO-REPORT"
# run.py's report verdict for a selected lane that wrote no result (a stop request
# it honoured, an unresolvable instance).
VERDICT_NOT_RUN = "NOT-RUN"
UNSETTLED_VERDICTS: Tuple[str, ...] = (VERDICT_NO_REPORT, VERDICT_NOT_RUN)
GREEN_VERDICTS: Tuple[str, ...] = (hlib.VERDICT_PASS, hlib.VERDICT_EXPECTED_FAIL)
RED_VERDICTS: Tuple[str, ...] = (hlib.VERDICT_PARSEK_FAIL, hlib.VERDICT_INVALID,
                                 hlib.VERDICT_KILLED, hlib.VERDICT_XPASS,
                                 VERDICT_NO_REPORT, VERDICT_NOT_RUN)
KNOWN_VERDICTS: Tuple[str, ...] = GREEN_VERDICTS + RED_VERDICTS

# Exit codes: tier_runner's vocabulary, with run.py's selection rule for 0 / 1.
EXIT_GREEN = 0
EXIT_RED = 1
EXIT_NEEDS_PROVISION = 2
EXIT_SKIPPED = 3
EXIT_ERROR = 4
EXIT_USAGE = 2

OUTCOME_GREEN = "GREEN"
OUTCOME_RED = "RED"
OUTCOME_STOPPED = "STOPPED"
OUTCOME_NO_SELECTION = "NO-SELECTION"


class SelectionError(ValueError):
    """A selection the runner refuses up front (unknown id, unknown tier)."""


def valid_run_name(name: str) -> bool:
    return isinstance(name, str) and bool(_RUN_NAME_RE.match(name))


# ---------------------------------------------------------------------------
# Selection
# ---------------------------------------------------------------------------


def parse_id_lines(text: str) -> List[str]:
    """Ids from an ids file or stdin: one per line, ``#`` comments and blank
    lines ignored, a repeat kept once (``run.py --ids-file``'s own grammar)."""
    return hlib.parse_ids_file_text(text)


def split_ids_arg(value: Optional[str]) -> List[str]:
    """``--ids a,b,c`` -> ``[a, b, c]`` (blanks dropped)."""
    return [p.strip() for p in (value or "").split(",") if p.strip()]


def resolve_selection(specs: Sequence[Dict], ids: Sequence[str],
                      tiers: Sequence[str]) -> List[Dict]:
    """The selected specs: the named ids first, in the order given, then every
    spec of each ``tiers`` entry in spec file order; a lane selected twice runs
    once. Raises SelectionError naming every unknown id and tier, so nothing
    flies on a typo."""
    bad_tiers = [t for t in tiers if t not in hlib.TIERS]
    selected, unknown = hlib.select_scenarios_by_ids(specs, list(ids))
    problems = []
    if unknown:
        problems.append("unknown lane id(s): %s" % ", ".join(unknown))
    if bad_tiers:
        problems.append("unknown tier(s): %s (known: %s)"
                        % (", ".join(bad_tiers), ", ".join(hlib.TIERS)))
    if problems:
        raise SelectionError("; ".join(problems))
    seen = {s.get("id") for s in selected}
    for tier in tiers:
        for spec in specs:
            sid = spec.get("id")
            if spec.get("tier") == tier and isinstance(sid, str) and sid not in seen:
                seen.add(sid)
                selected.append(spec)
    return selected


# ---------------------------------------------------------------------------
# Exclusive lanes
# ---------------------------------------------------------------------------


def _truthy_arg(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    return isinstance(value, str) and value.strip().lower() == "true"


def exclusive_reason(spec: Dict) -> Optional[str]:
    """Why a lane must fly alone, or None. The spec key wins; otherwise the
    first driver step that moves the cursor or takes the foreground names it."""
    runtime = (spec or {}).get("runtime") or {}
    if runtime.get(EXCLUSIVE_SPEC_KEY) is True:
        return "spec key [runtime] %s = true" % EXCLUSIVE_SPEC_KEY
    steps = (((spec or {}).get("driver") or {}).get("steps") or [])
    for index, step in enumerate(steps):
        if not isinstance(step, dict):
            continue
        cmd = step.get("cmd")
        args = step.get("args") or {}
        if not isinstance(args, dict):
            continue
        for rule_cmd, arg, value in POINTER_STEP_RULES:
            if cmd == rule_cmd and str(args.get(arg, "")).strip() == value:
                return "step %d %s %s=%s moves the OS cursor" % (index, cmd, arg, value)
        if cmd == "RunTests":
            cats = [c.strip() for c in str(args.get("category", "")).split(",")]
            hit = [c for c in POINTER_TEST_CATEGORIES if c in cats]
            if hit:
                return "step %d RunTests category %s moves the OS cursor" % (index, hit[0])
        if _truthy_arg(args.get(FOCUS_STEP_ARG)):
            return "step %d %s %s=true takes the foreground" % (index, cmd, FOCUS_STEP_ARG)
    return None


# ---------------------------------------------------------------------------
# Duration estimates and the plan
# ---------------------------------------------------------------------------


def _number(value: Any) -> Optional[float]:
    if isinstance(value, (int, float)) and not isinstance(value, bool) and value >= 0:
        return float(value)
    return None


def median(values: Sequence[float]) -> Optional[float]:
    ordered = sorted(values)
    n = len(ordered)
    if n == 0:
        return None
    mid = n // 2
    return ordered[mid] if n % 2 else (ordered[mid - 1] + ordered[mid]) / 2.0


def index_pass_medians(rows: Iterable[Dict]) -> Dict[str, float]:
    """Per lane, the median ``wall`` of the run index's PASS rows."""
    walls: Dict[str, List[float]] = {}
    for row in rows:
        if not isinstance(row, dict) or row.get("verdict") != hlib.VERDICT_PASS:
            continue
        lane = row.get("lane")
        wall = _number(row.get("wall"))
        if isinstance(lane, str) and wall is not None:
            walls.setdefault(lane, []).append(wall)
    return {lane: median(v) for lane, v in walls.items() if v}


def lane_p50(scenario_id: str, durations: Dict[str, Dict], index_medians: Dict[str, float],
             default: float = DEFAULT_P50_SECONDS) -> Tuple[float, str]:
    """(estimate seconds, source): ``coverage/duration.json``'s p50, else the
    local run index's PASS median, else ``default``."""
    entry = (durations or {}).get(scenario_id) or {}
    p50 = _number(entry.get("p50")) if isinstance(entry, dict) else None
    if p50 is not None and p50 > 0:
        return p50, P50_SOURCE_DURATION
    med = (index_medians or {}).get(scenario_id)
    if med is not None and med > 0:
        return float(med), P50_SOURCE_INDEX
    return float(default), P50_SOURCE_DEFAULT


@dataclass(frozen=True)
class Lane:
    lane_id: str
    profile: str
    tier: str
    p50: float
    p50_source: str
    exclusive_reason: Optional[str] = None

    @property
    def exclusive(self) -> bool:
        return self.exclusive_reason is not None

    def to_json(self) -> Dict[str, Any]:
        return {"id": self.lane_id, "profile": self.profile, "tier": self.tier,
                "p50": self.p50, "p50Source": self.p50_source,
                "exclusive": self.exclusive, "exclusiveReason": self.exclusive_reason}

    @staticmethod
    def from_json(obj: Dict[str, Any]) -> "Lane":
        return Lane(str(obj.get("id")), str(obj.get("profile")), str(obj.get("tier")),
                    float(obj.get("p50") or DEFAULT_P50_SECONDS),
                    str(obj.get("p50Source") or P50_SOURCE_DEFAULT),
                    obj.get("exclusiveReason") if obj.get("exclusive") else None)


def build_lanes(selected: Sequence[Dict], durations: Dict[str, Dict],
                index_medians: Dict[str, float],
                local_fixture_staged: Callable[[str], bool]
                ) -> Tuple[List[Lane], List[Tuple[str, str]]]:
    """Lanes for the selected specs, plus (id, reason) for each lane that cannot
    fly here: an operator-local fixture that is not staged (it would only refuse
    pre-boot INVALID(staging), tier_runner.plan_full_run's rule)."""
    lanes: List[Lane] = []
    skipped: List[Tuple[str, str]] = []
    for spec in selected:
        sid = str(spec.get("id"))
        template = ((spec.get("fixture") or {}).get("saveTemplate"))
        if hlib.is_local_fixture_template(template) and not local_fixture_staged(template):
            skipped.append((sid, "operator-local fixture not staged: %s"
                            % (hlib.local_fixture_hint(template) or template)))
            continue
        p50, source = lane_p50(sid, durations, index_medians)
        lanes.append(Lane(sid, str(spec.get("instanceProfile")), str(spec.get("tier")),
                          p50, source, exclusive_reason(spec)))
    return lanes, skipped


def order_lanes(lanes: Sequence[Lane]) -> List[Lane]:
    """Longest p50 first (greedy packing keeps the tail short); ties keep the
    selection order. Exclusive lanes go last, in the same order."""
    indexed = list(enumerate(lanes))
    normal = sorted((t for t in indexed if not t[1].exclusive), key=lambda t: (-t[1].p50, t[0]))
    exclusive = sorted((t for t in indexed if t[1].exclusive), key=lambda t: (-t[1].p50, t[0]))
    return [lane for _, lane in normal + exclusive]


def partition_exclusive(lanes: Sequence[Lane], include_exclusive: bool
                        ) -> Tuple[List[Lane], List[Lane]]:
    """(lanes to fly, exclusive lanes left out). Exclusive lanes fly only with
    ``--include-exclusive``."""
    if include_exclusive:
        return list(lanes), []
    return [l for l in lanes if not l.exclusive], [l for l in lanes if l.exclusive]


def slots_by_profile(slot_names: Sequence[str]) -> Dict[str, List[str]]:
    """Slot names grouped by profile, copy order kept."""
    out: Dict[str, List[str]] = {}
    for name in slot_names:
        try:
            profile, _copy = slots.parse_slot_name(name)
        except slots.SlotError:
            continue
        out.setdefault(profile, []).append(name)
    return out


def missing_profiles(lanes: Sequence[Lane], by_profile: Dict[str, List[str]]) -> List[str]:
    """Profiles a lane needs that no provisioned slot serves, sorted."""
    return sorted({l.profile for l in lanes if not by_profile.get(l.profile)})


def check_uniform_dll(sha_by_slot: Sequence[Tuple[str, Optional[str]]]) -> Optional[str]:
    """None when every slot deploys the same Parsek.dll, else the refusal text
    naming each odd slot. The reference is copy 1 of the reference profile when
    it is among the slots, else the first slot listed."""
    if not sha_by_slot:
        return None
    missing = [s for s, sha in sha_by_slot if not sha]
    if missing:
        return ("slot(s) without a deployed GameData/Parsek/Plugins/Parsek.dll: %s; "
                "provision them" % ", ".join(missing))
    by_name = dict(sha_by_slot)
    ref = hlib.REFERENCE_DLL_PROFILE if hlib.REFERENCE_DLL_PROFILE in by_name else sha_by_slot[0][0]
    ref_sha = by_name[ref]
    odd = [(s, sha) for s, sha in sha_by_slot if sha != ref_sha]
    if not odd:
        return None
    return ("slots deploy different Parsek.dll builds: %s (sha256 %s) vs %s; "
            "provision the odd slot(s) from the same build"
            % (ref, ref_sha[:12], ", ".join("%s (sha256 %s)" % (s, sha[:12]) for s, sha in odd)))


# ---------------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Running:
    lane_id: str
    slot: str
    exclusive: bool
    started_epoch: float


@dataclass(frozen=True)
class MemoryReading:
    free_ram_gb: Optional[float] = None
    free_vram_gb: Optional[float] = None


ACTION_START = "start"
ACTION_WAIT = "wait"
ACTION_DONE = "done"

WAIT_STOP = "stop"
WAIT_SLOTS = "slots"
WAIT_SLOT_BUSY = "slot-busy"
WAIT_MEMORY = "memory"
WAIT_GAP = "dispatch-gap"
WAIT_EXCLUSIVE = "exclusive"
WAIT_DRAIN = "drain"


@dataclass(frozen=True)
class DispatchDecision:
    action: str
    lane_id: Optional[str] = None
    slot: Optional[str] = None
    window: Optional[str] = None      # set for an exclusive start (normal)
    wait_kind: Optional[str] = None
    reason: str = ""
    log_key: str = ""                 # set for a memory wait (memory_wait_log_key)


def boot_remaining_share(started_epoch: float, now_epoch: float,
                         boot_window_seconds: float = BOOT_WINDOW_SECONDS) -> float:
    """The share (0..1) of one copy's footprint a copy started at
    ``started_epoch`` has still to take: ``max(0, 1 - elapsed / window)``,
    capped at 1 for a start stamp ahead of the clock."""
    if boot_window_seconds <= 0:
        return 0.0
    share = 1.0 - (now_epoch - started_epoch) / boot_window_seconds
    return max(0.0, min(1.0, share))


def booting_share(running: Sequence[Running], now_epoch: float,
                  boot_window_seconds: float = BOOT_WINDOW_SECONDS) -> float:
    """Sum of the remaining footprint shares of the running lanes, in copies."""
    return sum(boot_remaining_share(r.started_epoch, now_epoch, boot_window_seconds)
               for r in running)


def booting_count(running: Sequence[Running], now_epoch: float,
                  boot_window_seconds: float = BOOT_WINDOW_SECONDS) -> int:
    """How many running lanes started less than ``boot_window_seconds`` ago."""
    return sum(1 for r in running if now_epoch - r.started_epoch < boot_window_seconds)


def memory_holds(reading: Optional[MemoryReading], running_count: int,
                 booting_copies: float = 0.0,
                 min_free_ram_gb: float = MIN_FREE_RAM_GB,
                 min_free_vram_gb: float = MIN_FREE_VRAM_GB,
                 footprint_ram_gb: float = COPY_FOOTPRINT_RAM_GB,
                 footprint_vram_gb: float = COPY_FOOTPRINT_VRAM_GB) -> List[Tuple[str, str]]:
    """The readings that hold the next lane back, as ``(label, arithmetic)``
    pairs (label ``RAM`` / ``VRAM``); empty when nothing holds. Never holds the
    FIRST lane (one lane always runs), and an unavailable reading never holds.
    ``booting_copies`` is the remaining footprint of the copies still booting,
    in copies (``booting_share``). Each reading is projected to after those
    copies and the new one have fully loaded, and holds when the projection is
    under its floor."""
    if running_count < 1 or reading is None:
        return []
    booting_copies = max(0.0, float(booting_copies))
    holds = []
    for label, free, footprint, floor in (
            ("RAM", reading.free_ram_gb, footprint_ram_gb, min_free_ram_gb),
            ("VRAM", reading.free_vram_gb, footprint_vram_gb, min_free_vram_gb)):
        if free is None:
            continue
        booting = booting_copies * footprint
        projected = free - booting - footprint
        if projected < floor:
            parts = ["free %s %.1f GB" % (label, free)]
            if booting > 0:
                parts.append("booting %.1f GB" % booting)
            parts.append("new copy %.1f GB" % footprint)
            holds.append((label, "%s = %.1f GB < %.1f GB"
                          % (" - ".join(parts), projected, floor)))
    return holds


def memory_deferral(reading: Optional[MemoryReading], running_count: int,
                    booting_copies: float = 0.0,
                    min_free_ram_gb: float = MIN_FREE_RAM_GB,
                    min_free_vram_gb: float = MIN_FREE_VRAM_GB,
                    footprint_ram_gb: float = COPY_FOOTPRINT_RAM_GB,
                    footprint_vram_gb: float = COPY_FOOTPRINT_VRAM_GB) -> Optional[str]:
    """Why the memory guard holds the next lane back (``memory_holds`` joined),
    or None."""
    holds = memory_holds(reading, running_count, booting_copies, min_free_ram_gb,
                         min_free_vram_gb, footprint_ram_gb, footprint_vram_gb)
    return "; ".join(text for _, text in holds) or None


def memory_wait_log_key(reading: Optional[MemoryReading], labels: Sequence[str],
                        running_count: int, booting: int) -> str:
    """What a memory wait is logged once per: which readings hold, the reading
    itself and the running / booting counts. The booting ramp moves the reason's
    numbers every poll, so the reason text is not the key."""
    ram = vram = None
    if reading is not None:
        ram, vram = reading.free_ram_gb, reading.free_vram_gb
    return "%s|ram=%s|vram=%s|running=%d|booting=%d" % (
        ",".join(labels), ram, vram, running_count, booting)


def parse_nvidia_smi_free_gb(text: str) -> Optional[float]:
    """Free VRAM in GB of the FIRST GPU from ``nvidia-smi
    --query-gpu=memory.total,memory.used --format=csv,noheader,nounits`` (MiB
    values), or None when the output does not parse."""
    for raw in (text or "").splitlines():
        parts = [p.strip() for p in raw.split(",")]
        if len(parts) < 2:
            continue
        try:
            total, used = float(parts[0]), float(parts[1])
        except ValueError:
            continue
        if total <= 0 or used < 0:
            continue
        return max(0.0, total - used) / 1024.0
    return None


def decide_next(pending: Sequence[Lane], running: Sequence[Running],
                by_profile: Dict[str, List[str]], max_running: int, stop: bool,
                memory: Optional[MemoryReading], now_epoch: float,
                last_start_epoch: Optional[float],
                gap_seconds: float = DISPATCH_GAP_SECONDS,
                boot_window_seconds: float = BOOT_WINDOW_SECONDS) -> DispatchDecision:
    """What the runner does next. ``pending`` is in dispatch order (ordered by
    ``order_lanes``); ``max_running`` is control.json's ``slots``, re-read
    before every call (a lower value never stops a running lane, it only holds
    the next start)."""
    if stop:
        if running:
            return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_STOP,
                                    reason="stop requested; %d running lane(s) finish, "
                                           "nothing new starts" % len(running))
        return DispatchDecision(ACTION_DONE, reason="stopped")
    if not pending:
        if running:
            return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_DRAIN,
                                    reason="no lane left to start; %d running" % len(running))
        return DispatchDecision(ACTION_DONE, reason="every lane dispatched")
    if any(r.exclusive for r in running):
        return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_EXCLUSIVE,
                                reason="an exclusive lane is flying alone")
    busy = {r.slot for r in running}
    normal = [l for l in pending if not l.exclusive]
    if normal:
        cap = max(MIN_SLOTS, int(max_running))
        if len(running) >= cap:
            return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_SLOTS,
                                    reason="%d running, slots=%d" % (len(running), cap))
        if running and last_start_epoch is not None and \
                now_epoch - last_start_epoch < gap_seconds:
            return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_GAP,
                                    reason="the last start was %.0f s ago (< %.0f s dispatch gap)"
                                    % (now_epoch - last_start_epoch, gap_seconds))
        share = booting_share(running, now_epoch, boot_window_seconds)
        holds = memory_holds(memory, len(running), share)
        if holds:
            booting = booting_count(running, now_epoch, boot_window_seconds)
            return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_MEMORY,
                                    reason="memory guard: %s; %d running, %d booting"
                                    % ("; ".join(t for _, t in holds), len(running), booting),
                                    log_key=memory_wait_log_key(
                                        memory, [l for l, _ in holds], len(running), booting))
        for lane in normal:
            for slot in by_profile.get(lane.profile, []):
                if slot not in busy:
                    return DispatchDecision(ACTION_START, lane_id=lane.lane_id, slot=slot,
                                            reason="p50=%.0fs" % lane.p50)
        return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_SLOT_BUSY,
                                reason="every slot of the pending lanes' profiles is busy")
    # Only exclusive lanes remain: each flies alone once the others drained.
    if running:
        return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_EXCLUSIVE,
                                reason="exclusive lane(s) wait for %d running lane(s) to finish"
                                % len(running))
    lane = pending[0]
    choices = by_profile.get(lane.profile, [])
    if not choices:
        return DispatchDecision(ACTION_WAIT, wait_kind=WAIT_SLOT_BUSY,
                                reason="no slot serves profile %s" % lane.profile)
    return DispatchDecision(ACTION_START, lane_id=lane.lane_id, slot=choices[0],
                            window=WINDOW_NORMAL,
                            reason="exclusive (%s), alone" % lane.exclusive_reason)


# ---------------------------------------------------------------------------
# Window mode (Windows virtual desktops)
# ---------------------------------------------------------------------------

GUID_BYTES = 16


def guid_from_bytes(blob: Optional[bytes]) -> Optional[str]:
    """A registry GUID (16 bytes, ``uuid`` bytes_le layout) as its lowercase
    hyphenated text, or None."""
    if not isinstance(blob, (bytes, bytearray)) or len(blob) != GUID_BYTES:
        return None
    return str(uuid.UUID(bytes_le=bytes(blob)))


def parse_virtual_desktop_ids(blob: Optional[bytes]) -> List[str]:
    """``VirtualDesktopIDs`` (REG_BINARY, 16 bytes per desktop, in desktop order)
    as GUID strings. A trailing partial record is ignored."""
    if not isinstance(blob, (bytes, bytearray)):
        return []
    out = []
    for start in range(0, len(blob) - GUID_BYTES + 1, GUID_BYTES):
        out.append(str(uuid.UUID(bytes_le=bytes(blob[start:start + GUID_BYTES]))))
    return out


@dataclass(frozen=True)
class WindowResolution:
    mode: str
    desktop_id: Optional[str] = None
    hint: Optional[str] = None
    detail: str = ""


def resolve_window_mode(requested: str, desktop_ids: Sequence[str],
                        current: Optional[str]) -> WindowResolution:
    """The window mode every child gets. ``desktop`` targets the LAST desktop
    that is not the current one; with one desktop (or none readable) it falls
    back to ``minimize`` with the one-line hint."""
    if requested != WINDOW_DESKTOP:
        return WindowResolution(requested, detail="requested %s" % requested)
    current_l = current.lower() if current else None
    others = [d for d in desktop_ids if d.lower() != current_l]
    if len(desktop_ids) < 2 or not others:
        return WindowResolution(WINDOW_MINIMIZE, hint=DESKTOP_HINT,
                                detail="%d virtual desktop(s); falling back to minimize"
                                % len(desktop_ids))
    target = others[-1]
    return WindowResolution(WINDOW_DESKTOP, desktop_id=target,
                            detail="desktop %d of %d (current=%s)"
                            % (list(desktop_ids).index(target) + 1, len(desktop_ids),
                               current or "unknown"))


# ---------------------------------------------------------------------------
# Child command line and report
# ---------------------------------------------------------------------------


def child_argv(python: str, run_py: str, slot: str, ids_file: str, report_json: str,
               window: str, desktop_id: Optional[str]) -> List[str]:
    """One lane's ``run.py`` command line (run from the harness dir)."""
    argv = [python, run_py, "--slot", slot, "--ids-file", ids_file, "--tests-prebuilt",
            "--no-coverage", "--keep-stop-request", "--report-json", report_json,
            "--window", window]
    if window == WINDOW_DESKTOP and desktop_id:
        argv += ["--desktop-id", desktop_id]
    return argv


RUNNER_TOKEN_ENV = "PARSEK_HARNESS_RUNNER_TOKEN"
RUNNER_PID_ENV = "PARSEK_HARNESS_RUNNER_PID"
RUNNER_LOCK_KIND = "parallel-runner"


def child_env(base: Dict[str, str], token: str, runner_pid: int) -> Dict[str, str]:
    env = dict(base)
    env[RUNNER_TOKEN_ENV] = token
    env[RUNNER_PID_ENV] = str(runner_pid)
    return env


@dataclass(frozen=True)
class ChildReport:
    verdict: str
    attempts: int
    run_ids: Tuple[str, ...]
    wall_seconds: Optional[float] = None


REPORT_SCHEMA = "parsek-run-report/1"


def parse_child_report(obj: Any, lane_id: str) -> Optional[ChildReport]:
    """The lane's entry of a ``run.py --report-json`` file, or None when it is
    missing or unreadable. Reads run.py's ``parsek-run-report/1`` document
    (``{"lanes": [{"id", "verdict", "attempts", "runIds", "wallSeconds"}]}``) and
    the flat ``{lane: {verdict, attempts, runIds}}`` shape."""
    if not isinstance(obj, dict):
        return None
    entry = None
    lanes = obj.get("lanes")
    if isinstance(lanes, list):
        entry = next((e for e in lanes if isinstance(e, dict) and e.get("id") == lane_id), None)
    elif isinstance(obj.get(lane_id), dict):
        entry = obj.get(lane_id)
    if entry is None:
        return None
    verdict = entry.get("verdict")
    if not isinstance(verdict, str) or not verdict:
        return None
    attempts = entry.get("attempts")
    if not isinstance(attempts, int) or isinstance(attempts, bool) or attempts < 1:
        attempts = 1
    run_ids = entry.get("runIds") or []
    if not isinstance(run_ids, list):
        run_ids = []
    wall = _number(entry.get("wallSeconds", entry.get("wall")))
    return ChildReport(verdict, attempts, tuple(str(r) for r in run_ids if r), wall)


# ---------------------------------------------------------------------------
# summary.txt, settled lanes, the exit code
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LaneOutcome:
    ended_iso: str
    verdict: str
    lane_id: str
    attempts: int
    wall_seconds: int
    slot: str
    run_ids: Tuple[str, ...] = ()
    note: str = ""


def format_summary_line(o: LaneOutcome) -> str:
    """``<endedIso> <VERDICT> <id> attempts=N wall=Ns slot=S runIds=a,b``: the
    first three fields are run.py's summary.txt order, so
    ``tier_runner.tally_verdicts`` reads this file too."""
    line = "%s %s %s attempts=%d wall=%ds slot=%s runIds=%s" % (
        o.ended_iso, o.verdict, o.lane_id, o.attempts, int(o.wall_seconds), o.slot,
        ",".join(o.run_ids) or "-")
    if o.note:
        line += " note=%s" % "_".join(o.note.split())
    return line


TOTALS_PREFIX = "# totals"


def parse_summary_line(line: str) -> Optional[LaneOutcome]:
    parts = (line or "").split()
    if len(parts) < 4 or parts[0].startswith("#"):
        return None
    fields: Dict[str, str] = {}
    for token in parts[3:]:
        if "=" in token:
            k, v = token.split("=", 1)
            fields[k] = v
    try:
        attempts = int(fields.get("attempts", ""))
    except ValueError:
        return None
    wall_text = fields.get("wall", "0").rstrip("s")
    try:
        wall = int(wall_text)
    except ValueError:
        wall = 0
    run_ids = tuple(r for r in fields.get("runIds", "").split(",") if r and r != "-")
    return LaneOutcome(parts[0], parts[1], parts[2], attempts, wall,
                       fields.get("slot", "?"), run_ids, fields.get("note", ""))


def latest_outcomes(lines: Sequence[str]) -> Dict[str, LaneOutcome]:
    """Each lane's LAST summary line."""
    out: Dict[str, LaneOutcome] = {}
    for line in lines:
        o = parse_summary_line(line)
        if o is not None:
            out[o.lane_id] = o
    return out


def lane_settled(verdict: str, attempts: int) -> bool:
    """Whether a lane's latest line settles it for a resume:
    ``tier_runner.full_run_done``'s rule (only an attempt-1 INVALID re-flies),
    plus NO-REPORT / NOT-RUN (the child left nothing to judge) re-flies."""
    if verdict in UNSETTLED_VERDICTS:
        return False
    return not (verdict == hlib.VERDICT_INVALID and attempts < 2)


def remaining_lane_ids(plan_ids: Sequence[str], lines: Sequence[str]) -> List[str]:
    latest = latest_outcomes(lines)
    return [sid for sid in plan_ids
            if sid not in latest or not lane_settled(latest[sid].verdict, latest[sid].attempts)]


def tally(plan_ids: Sequence[str], lines: Sequence[str]) -> Tuple[Dict[str, int], int]:
    """(settled verdict counts, pending count) over the plan's lanes."""
    latest = latest_outcomes(lines)
    counts: Dict[str, int] = {}
    pending = 0
    for sid in plan_ids:
        o = latest.get(sid)
        if o is None or not lane_settled(o.verdict, o.attempts):
            pending += 1
            if o is not None and o.verdict in RED_VERDICTS:
                counts[o.verdict] = counts.get(o.verdict, 0) + 1
            continue
        counts[o.verdict] = counts.get(o.verdict, 0) + 1
    return counts, pending


def format_counts(counts: Dict[str, int]) -> str:
    if not counts:
        return "no-results"
    ordered = [v for v in KNOWN_VERDICTS if counts.get(v)]
    ordered += sorted(k for k in counts if k not in KNOWN_VERDICTS and counts[k])
    return " ".join("%s=%d" % (v, counts[v]) for v in ordered)


def selection_outcome(plan_ids: Sequence[str], lines: Sequence[str],
                      stopped: bool) -> Tuple[str, int, str]:
    """(outcome, exit code, detail) for the whole planned run, with run.py's
    selection rule: any PARSEK-FAIL / INVALID / KILLED / XPASS (or a lane with
    no report) is RED (1); PASS and EXPECTED-FAIL are green. Lanes still
    pending read STOPPED (3) after a stop request, else RED (the run did not
    fly what it planned); an empty plan is NO-SELECTION (2)."""
    if not plan_ids:
        return OUTCOME_NO_SELECTION, EXIT_USAGE, "nothing selected"
    latest = latest_outcomes(lines)
    counts, pending = tally(plan_ids, lines)
    red = [sid for sid in plan_ids if sid in latest and latest[sid].verdict in RED_VERDICTS]
    detail = "%s pending=%d of %d" % (format_counts(counts), pending, len(plan_ids))
    if red:
        return OUTCOME_RED, EXIT_RED, detail
    if pending:
        if stopped:
            return OUTCOME_STOPPED, EXIT_SKIPPED, detail
        return OUTCOME_RED, EXIT_RED, detail + " (lanes left unflown)"
    return OUTCOME_GREEN, EXIT_GREEN, detail


def format_totals_line(plan_ids: Sequence[str], lines: Sequence[str],
                       skipped: Sequence[str] = ()) -> str:
    counts, pending = tally(plan_ids, lines)
    text = "%s %s pending=%d of %d" % (TOTALS_PREFIX, format_counts(counts), pending,
                                       len(plan_ids))
    if skipped:
        text += " notFlown=%s" % ",".join(skipped)
    return text


def render_summary(lane_lines: Sequence[str], plan_ids: Sequence[str],
                   skipped: Sequence[str] = ()) -> str:
    """The whole summary.txt: every lane line in the order written, then the
    totals line (rewritten each time, so it is always last)."""
    body = [l for l in lane_lines if parse_summary_line(l) is not None]
    return "\n".join(body + [format_totals_line(plan_ids, body, skipped)]) + "\n"


# ---------------------------------------------------------------------------
# control.json
# ---------------------------------------------------------------------------


def clamp_slots(value: Any) -> Optional[int]:
    """A valid slots value (an int, or its decimal text, in MIN_SLOTS..MAX_SLOTS),
    or None."""
    if isinstance(value, bool):
        return None
    if isinstance(value, str) and value.strip().isdigit():
        value = int(value.strip())
    if not isinstance(value, int):
        return None
    return value if MIN_SLOTS <= value <= MAX_SLOTS else None


def default_control(slots_value: int) -> Dict[str, Any]:
    return {"schema": CONTROL_SCHEMA, "slots": slots_value, "stop": False}


def read_control_obj(obj: Any, fallback_slots: int) -> Tuple[int, bool]:
    """(slots, stop) from a parsed control.json; an unreadable or out-of-range
    value keeps ``fallback_slots`` (never stops a run by itself)."""
    if not isinstance(obj, dict):
        return fallback_slots, False
    n = clamp_slots(obj.get("slots"))
    return (fallback_slots if n is None else n), obj.get("stop") is True


# ---------------------------------------------------------------------------
# events.jsonl and status
# ---------------------------------------------------------------------------


def event_line(event: Dict[str, Any]) -> str:
    return json.dumps(event, sort_keys=True, separators=(",", ":"))


def parse_events(text: str) -> List[Dict[str, Any]]:
    out = []
    for raw in (text or "").splitlines():
        raw = raw.strip()
        if not raw:
            continue
        try:
            obj = json.loads(raw)
        except ValueError:
            continue
        if isinstance(obj, dict):
            out.append(obj)
    return out


def running_from_events(events: Sequence[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Dispatches with no matching finish since the latest runner start."""
    open_: Dict[Any, Dict[str, Any]] = {}
    for ev in events:
        kind = ev.get("kind")
        if kind == "start":
            open_.clear()
        elif kind == "dispatch":
            open_[ev.get("seq")] = ev
        elif kind in ("finish", "abandon"):
            open_.pop(ev.get("seq"), None)
    return list(open_.values())


def _fmt_secs(seconds: float) -> str:
    s = int(max(0, seconds))
    if s >= 3600:
        return "%dh%02dm" % (s // 3600, (s % 3600) // 60)
    if s >= 60:
        return "%dm%02ds" % (s // 60, s % 60)
    return "%ds" % s


def format_status(plan: Dict[str, Any], slots_value: int, stop: bool,
                  events: Sequence[Dict[str, Any]], summary_lines: Sequence[str],
                  now_epoch: float, runner_state: str) -> List[str]:
    """The ``status`` report: runner state, slots, running lanes with elapsed
    time, queued count, done counts by verdict and the last memory deferral."""
    plan_ids = [l.get("id") for l in plan.get("lanes") or []]
    running = running_from_events(events)
    running_ids = {ev.get("lane") for ev in running}
    counts, pending = tally(plan_ids, summary_lines)
    queued = max(0, pending - len([i for i in running_ids if i in plan_ids]))
    out = ["run %s: runner %s, slots=%d%s, window=%s"
           % (plan.get("name"), runner_state, slots_value, " STOP REQUESTED" if stop else "",
              plan.get("windowMode") or "?")]
    for ev in sorted(running, key=lambda e: e.get("epoch") or 0):
        out.append("running slot=%s lane=%s elapsed=%s"
                   % (ev.get("slot"), ev.get("lane"),
                      _fmt_secs(now_epoch - float(ev.get("epoch") or now_epoch))))
    out.append("queued=%d running=%d done: %s" % (queued, len(running), format_counts(counts)))
    skipped = plan.get("notFlown") or []
    if skipped:
        out.append("not flown: %s" % ", ".join("%s (%s)" % (s.get("id"), s.get("reason"))
                                               for s in skipped))
    last_dispatch = max((ev.get("epoch") or 0 for ev in events if ev.get("kind") == "dispatch"),
                        default=0)
    deferrals = [ev for ev in events if ev.get("kind") == "wait"
                 and ev.get("waitKind") == WAIT_MEMORY and (ev.get("epoch") or 0) >= last_dispatch]
    if deferrals and running_ids:
        out.append("memory deferral: %s" % deferrals[-1].get("reason"))
    return out
