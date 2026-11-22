"""Watch lanes: the pure decisions behind ``run.py --hold`` and
``tools/make_watch_lane.py`` (design-autotest-harness-core.md -> "Attended hold
mode (watch lanes)", harness/README.md -> "Watch lanes").

A watch lane sets up a looped mission for the owner to look at. Flown unattended
it is an ordinary lane: setup, a screenshot (and a map-geometry dump in map view),
FlushAndQuit, a light verdict. Flown with ``--hold`` it runs the setup steps, then
stops driving before its hold point and keeps the game open until the owner
closes KSP; the run then writes an OBSERVED result that no coverage, flake, run
index or lane-health reader counts.

Stdlib only and I/O free: every function takes text or values and returns values.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence, Tuple

import saveparse

# ---------------------------------------------------------------------------
# The spec surface: the optional [watch] table.
# ---------------------------------------------------------------------------

WATCH_TABLE = "watch"
# Optional: the label of the step the hold stops BEFORE. Absent = the first
# FlushAndQuit step, or the end of the step list when there is none.
HOLD_BEFORE_KEY = "holdBefore"
# Informational keys the generator writes (never read by run.py).
WATCH_SCENE_KEY = "scene"
WATCH_TREE_HANDLE_KEY = "treeHandle"

HOLD_DEFAULT_STOP_VERB = "FlushAndQuit"

SCENE_LIFTOFF = "liftoff"
SCENE_LANDING = "landing"
SCENE_DOCKING = "docking"
SCENE_TRANSFER = "transfer"
SCENES: Tuple[str, ...] = (SCENE_LIFTOFF, SCENE_LANDING, SCENE_DOCKING, SCENE_TRANSFER)

VIEW_FLIGHT = "flight"
VIEW_MAP = "map"
SCENE_VIEW: Dict[str, str] = {
    SCENE_LIFTOFF: VIEW_FLIGHT,
    SCENE_LANDING: VIEW_FLIGHT,
    SCENE_DOCKING: VIEW_FLIGHT,
    SCENE_TRANSFER: VIEW_MAP,
}

# ---------------------------------------------------------------------------
# Hold mode: the result vocabulary.
# ---------------------------------------------------------------------------

# Not a verdict: a hold run asserts nothing. hlib.HOLD_VERDICT_OBSERVED is the
# same literal (hlib owns the result predicate every reader filters on).
VERDICT_OBSERVED = "OBSERVED"
VERDICT_INVALID = "INVALID"

# Why the hold ended (result hold.endReason).
END_OWNER_CLOSED = "owner-closed"        # KSP exited while held: the normal end
END_STOP_REQUEST = "stop-request"        # results/.stop-requested was dropped
END_CEILING = "hold-ceiling"             # --hold-max-minutes ran out
END_CLOSED_DURING_SETUP = "closed-during-setup"  # KSP exited before the hold point
END_BOOT_CRASH = "boot-crash"
END_BOOT_HANG = "boot-hang"

HOLD_END_REASONS: Tuple[str, ...] = (
    END_OWNER_CLOSED, END_STOP_REQUEST, END_CEILING, END_CLOSED_DURING_SETUP,
    END_BOOT_CRASH, END_BOOT_HANG)

# Result subkinds.
SUBKIND_SETUP_INCOMPLETE = "setup-incomplete"

DEFAULT_HOLD_MAX_MINUTES = 240
HOLD_HEARTBEAT_SECONDS = 60.0
HOLD_STATUS_LOG_SECONDS = 300.0


@dataclass(frozen=True)
class HoldPlan:
    """Which steps a hold run drives. ``hold_index`` is the first step NOT driven;
    ``skipped`` lists every step from there on as (index, cmd)."""

    hold_index: int
    step_count: int
    source: str                     # "holdBefore", "FlushAndQuit" or "end"
    skipped: Tuple[Tuple[int, str], ...] = ()

    @property
    def setup_count(self) -> int:
        return self.hold_index


def hold_refusal(spec: Dict) -> Optional[str]:
    """Why ``spec`` cannot be held, or None. Only seam drivers hold: a mission
    subprocess owns the game while it flies, and an autorun batch quits KSP
    itself."""
    driver = (spec or {}).get("driver", {}) or {}
    if driver.get("kind") != "seam":
        return "only kind = \"seam\" drivers can be held (this one is %r)" % driver.get("kind")
    if driver.get("autorun"):
        return "a [driver.autorun] batch quits KSP by itself; it cannot be held"
    steps = driver.get("steps", []) or []
    if not steps:
        return "the driver has no steps"
    for step in steps:
        if isinstance(step, dict) and step.get("phase") == "mission":
            return "a phase = \"mission\" step cannot be held"
    watch = (spec or {}).get(WATCH_TABLE)
    if watch is not None and not isinstance(watch, dict):
        return "[watch] must be a table"
    hold_before = (watch or {}).get(HOLD_BEFORE_KEY)
    if hold_before is not None:
        labels = [s.get("label") for s in steps if isinstance(s, dict)]
        if hold_before not in labels:
            return "[watch] holdBefore = %r names no step label" % (hold_before,)
    return None


def plan_hold(spec: Dict) -> HoldPlan:
    """The hold point of ``spec`` (call ``hold_refusal`` first)."""
    steps = ((spec or {}).get("driver", {}) or {}).get("steps", []) or []
    watch = (spec or {}).get(WATCH_TABLE) or {}
    hold_before = watch.get(HOLD_BEFORE_KEY) if isinstance(watch, dict) else None
    index = len(steps)
    source = "end"
    if hold_before is not None:
        for i, step in enumerate(steps):
            if isinstance(step, dict) and step.get("label") == hold_before:
                index, source = i, HOLD_BEFORE_KEY
                break
    else:
        for i, step in enumerate(steps):
            if isinstance(step, dict) and step.get("cmd") == HOLD_DEFAULT_STOP_VERB:
                index, source = i, HOLD_DEFAULT_STOP_VERB
                break
    skipped = tuple((i, str((steps[i] or {}).get("cmd", "") or (steps[i] or {}).get("phase", "")))
                    for i in range(index, len(steps)))
    return HoldPlan(hold_index=index, step_count=len(steps), source=source, skipped=skipped)


def decide_hold_end(held_seconds: float, ceiling_seconds: float, exited: bool,
                    stop_requested: bool) -> Optional[str]:
    """The reason the hold ends now, or None to keep holding. An exit outranks a
    stop request in the same poll: the owner closed the game, nothing was killed."""
    if exited:
        return END_OWNER_CLOSED
    if stop_requested:
        return END_STOP_REQUEST
    if ceiling_seconds > 0 and held_seconds >= ceiling_seconds:
        return END_CEILING
    return None


def classify_hold(end_reason: str, setup_complete: bool) -> Tuple[str, str, str]:
    """(verdict, subkind, note) of a finished hold run. Never PASS: the run
    asserted nothing. A boot that never reached the seam is INVALID (nothing was
    shown); everything else is OBSERVED, with ``setup-incomplete`` when a setup
    step did not come back as expected."""
    if end_reason == END_BOOT_CRASH:
        return VERDICT_INVALID, "boot-crash", "KSP exited before the seam answered; nothing was held"
    if end_reason == END_BOOT_HANG:
        return VERDICT_INVALID, "boot-hang", "KSP hung before the seam answered; nothing was held"
    subkind = "" if setup_complete else SUBKIND_SETUP_INCOMPLETE
    notes = {
        END_OWNER_CLOSED: "held until the owner closed KSP",
        END_STOP_REQUEST: "hold ended by a stop request; KSP was closed by the harness",
        END_CEILING: "hold ceiling reached; KSP was closed by the harness",
        END_CLOSED_DURING_SETUP: "KSP closed before the setup reached the hold point",
    }
    note = notes.get(end_reason, "hold ended: %s" % end_reason)
    if not setup_complete and end_reason != END_CLOSED_DURING_SETUP:
        note += " (setup incomplete: the hold started early)"
    return VERDICT_OBSERVED, subkind, note


def build_hold_block(plan: HoldPlan, end_reason: str, setup_complete: bool,
                     held_seconds: Optional[float], unmet_step: Optional[Dict],
                     max_minutes: float) -> Dict:
    """The result record's ``hold`` block."""
    return {
        "holdIndex": plan.hold_index,
        "holdSource": plan.source,
        "setupSteps": plan.setup_count,
        "setupComplete": bool(setup_complete),
        "unmetStep": dict(unmet_step) if unmet_step else None,
        "endReason": end_reason,
        "heldSeconds": int(round(held_seconds)) if held_seconds is not None else None,
        "maxMinutes": max_minutes,
        "skippedSteps": [{"index": i, "cmd": c} for i, c in plan.skipped],
    }


# ---------------------------------------------------------------------------
# Tree handles: one mission in any save. The C# twin is
# Source/Parsek/TestCommands/TestCommandTreeHandle.cs (the seam resolves the same
# vocabulary live); test_watchlane.TreeHandleSourceSyncTests keeps the two equal.
# ---------------------------------------------------------------------------

TREE_HANDLE_FIRST = "first"
TREE_HANDLE_LATEST = "latest"
TREE_HANDLE_NAME_PREFIX = "name:"

REASON_NO_TREES = "tree-handle-no-trees"
REASON_NO_MATCH = "tree-handle-no-match"
REASON_AMBIGUOUS = "tree-handle-ambiguous"
REASON_EMPTY = "tree-handle-empty"
REASON_UNKNOWN_ID = "tree-handle-unknown-id"

TREE_ID_RE = re.compile(r"^[0-9a-f]{32}$")


@dataclass(frozen=True)
class TreeEntry:
    """One committed tree of a save, in save (commit) order."""

    tree_id: str
    display_name: str
    tree_name: str = ""
    recording_count: int = 0
    start_ut: Optional[float] = None
    end_ut: Optional[float] = None
    vessel_names: Tuple[str, ...] = ()


def is_tree_handle(value: Optional[str]) -> bool:
    """True for ``first`` / ``latest`` / ``name:<x>`` (case-insensitive keyword)."""
    v = (value or "").strip()
    low = v.lower()
    return low in (TREE_HANDLE_FIRST, TREE_HANDLE_LATEST) or low.startswith(TREE_HANDLE_NAME_PREFIX)


def resolve_tree_handle(handle: Optional[str], trees: Sequence[TreeEntry]
                        ) -> Tuple[Optional[str], Optional[str]]:
    """(tree id, None) or (None, reason). Same rules as the seam:

    - an exact tree id resolves to itself;
    - ``first`` / ``latest`` are the first / last committed tree in save order;
    - ``name:<x>`` matches the tree's display name (its original mission's name,
      else its root group name, else its launch name), case-insensitively, and
      must match exactly one tree.
    Anything else: REASON_UNKNOWN_ID offline (the seam passes it through to the
    verb's own unknown-tree refusal)."""
    v = (handle or "").strip()
    if not v:
        return None, REASON_EMPTY
    for t in trees:
        if t.tree_id == v:
            return v, None
    low = v.lower()
    if low in (TREE_HANDLE_FIRST, TREE_HANDLE_LATEST):
        if not trees:
            return None, REASON_NO_TREES
        return (trees[0] if low == TREE_HANDLE_FIRST else trees[-1]).tree_id, None
    if low.startswith(TREE_HANDLE_NAME_PREFIX):
        want = v[len(TREE_HANDLE_NAME_PREFIX):].strip().lower()
        matches = [t for t in trees if (t.display_name or "").strip().lower() == want]
        if not matches:
            return None, REASON_NO_MATCH
        if len(matches) > 1:
            return None, REASON_AMBIGUOUS
        return matches[0].tree_id, None
    return None, REASON_UNKNOWN_ID


def _float_or_none(text: Optional[str]) -> Optional[float]:
    try:
        return float(text) if text not in (None, "") else None
    except ValueError:
        return None


def read_save_trees(sfs_text: str) -> Tuple[List[TreeEntry], str]:
    """The committed trees of a persistent.sfs text, in save order, with their
    display names, plus a parse error ("" when clean). The in-flight copy of an
    active tree (``isActive = True``) and a pending tree are not committed and are
    left out, as ``RecordingStore.CommittedTrees`` leaves them out."""
    res = saveparse.parse_sfs(sfs_text)
    if not res.ok:
        return [], res.error
    game = res.root.first("GAME")
    if game is None:
        return [], "no GAME node"
    scen = None
    for sc in game.nodes_named("SCENARIO"):
        if sc.value("name") == "ParsekScenario":
            scen = sc
            break
    if scen is None:
        return [], ""
    mission_names: Dict[str, str] = {}
    for m in scen.nodes_named("MISSION"):
        tid = m.value("treeId") or ""
        name = m.value("name") or ""
        # The original mission is the FIRST of its tree (clones follow it).
        if tid and tid not in mission_names and name:
            mission_names[tid] = name
    out: List[TreeEntry] = []
    for t in scen.nodes_named("RECORDING_TREE"):
        if (t.value("isActive") or "").strip().lower() == "true":
            continue
        if (t.value("isPending") or "").strip().lower() == "true":
            continue
        tid = t.value("id") or ""
        tree_name = t.value("treeName") or ""
        root_group = t.value("autoGeneratedRootGroupName") or ""
        display = mission_names.get(tid) or root_group or tree_name
        recs = t.nodes_named("RECORDING")
        starts = [x for x in (_float_or_none(r.value("explicitStartUT")) for r in recs) if x is not None]
        ends = [x for x in (_float_or_none(r.value("explicitEndUT")) for r in recs) if x is not None]
        names: List[str] = []
        for r in recs:
            n = r.value("vesselName") or ""
            if n and n not in names:
                names.append(n)
        out.append(TreeEntry(tree_id=tid, display_name=display, tree_name=tree_name,
                             recording_count=len(recs),
                             start_ut=min(starts) if starts else None,
                             end_ut=max(ends) if ends else None,
                             vessel_names=tuple(names)))
    return out, ""


def format_tree_table(trees: Sequence[TreeEntry]) -> List[str]:
    """Human lines for ``make_watch_lane.py --list``."""
    lines = []
    for i, t in enumerate(trees):
        span = ""
        if t.start_ut is not None and t.end_ut is not None:
            span = " ut=[%.1f, %.1f]" % (t.start_ut, t.end_ut)
        lines.append("%2d  %s  %r  recordings=%d%s  vessels=%s"
                     % (i, t.tree_id, t.display_name, t.recording_count, span,
                        ", ".join(t.vessel_names[:4])))
    return lines


# ---------------------------------------------------------------------------
# The view preamble per scene kind, and the spec generator.
# ---------------------------------------------------------------------------

LANE_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
LABEL_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]*$")
BODY_RE = re.compile(r"^[A-Za-z][A-Za-z0-9 _-]*$")

DEFAULT_MAP_PITCH = 60.0
DEFAULT_MAP_HOLD_SECONDS = 4.5
# Lead from a TimeJump to the 1x arrival, as the TP picture lanes do: the jump
# lands at the instant, then five game seconds at 1x settle the scene.
INSTANT_SETTLE_SECONDS = 5.0

WATCH_TAGS: Tuple[str, ...] = ("watch-lane", "loop-playback", "screenshots")

# The header line that records the generator command (make_watch_lane.recorded_argv).
GENERATED_WITH_PREFIX = "# Generated with: python harness/tools/make_watch_lane.py "


@dataclass
class WatchLaneParams:
    lane_id: str
    scene: str
    save_template: str
    tree_id: str
    description: str = ""
    tree_handle: str = ""
    focus_pid: Optional[int] = None
    at_ut: Optional[float] = None
    map_focus: str = "Kerbin"
    map_distance: float = 0.0
    map_pitch: float = DEFAULT_MAP_PITCH
    instance_profile: str = "stock-minimal"
    budget_seconds: int = 900
    count_min: Optional[int] = None
    count_max: Optional[int] = None
    extra_tags: Tuple[str, ...] = ()
    notes: Tuple[str, ...] = field(default_factory=tuple)
    # The generator command line the header records (make_watch_lane.py writes
    # its canonical argv here; the sync test re-runs it).
    command: str = ""


def validate_params(p: WatchLaneParams) -> List[str]:
    errors: List[str] = []
    if not LANE_ID_RE.match(p.lane_id or ""):
        errors.append("lane id %r is not filename-safe" % (p.lane_id,))
    if p.scene not in SCENES:
        errors.append("scene %r not in %s" % (p.scene, list(SCENES)))
    if not TREE_ID_RE.match(p.tree_id or ""):
        errors.append("tree id %r is not a 32-hex tree id" % (p.tree_id,))
    if not p.save_template or p.save_template.startswith("/") or ":" in p.save_template:
        errors.append("saveTemplate %r must be a relative fixture path" % (p.save_template,))
    if p.focus_pid is not None and p.focus_pid <= 0:
        errors.append("focus pid must be a positive integer")
    if p.at_ut is not None and p.at_ut < 0:
        errors.append("--at must be a non-negative UT")
    if SCENE_VIEW.get(p.scene) == VIEW_MAP:
        if not BODY_RE.match(p.map_focus or ""):
            errors.append("map focus %r is not a body name" % (p.map_focus,))
        if p.map_distance <= 0:
            errors.append("a transfer lane needs --map-distance (metres from the focus body)")
        if not (-90.0 <= p.map_pitch <= 90.0):
            errors.append("map pitch must be in [-90, 90]")
    if p.budget_seconds <= 0:
        errors.append("budget must be positive")
    if (p.count_min is None) != (p.count_max is None):
        errors.append("give both --count-min and --count-max, or neither")
    return errors


def _fmt_num(value: float) -> str:
    if float(value).is_integer():
        return "%d" % int(value)
    return repr(float(value))


def shot_label(lane_id: str, scene: str) -> str:
    """The CaptureScreenshot / DumpMapGeometry label: lowercase lane id + scene,
    restricted to the label grammar."""
    base = re.sub(r"[^A-Za-z0-9_-]+", "-", lane_id).strip("-").lower()
    if scene in base.split("-"):
        return base
    return "%s-%s" % (base, scene)


def _step(cmd: str, args: Optional[Dict[str, str]] = None, expect: str = "OK",
          budget: Optional[int] = None, label: Optional[str] = None) -> Dict:
    s: Dict = {"cmd": cmd}
    if args:
        s["args"] = dict(args)
    if label:
        s["label"] = label
    s["expect"] = expect
    if budget is not None:
        s["budget"] = budget
    return s


def setup_steps(p: WatchLaneParams) -> List[Dict]:
    """Load, quiet the recorder, arm the loop, start it (the clock lands 15 s
    before the next launch), then jump to the instant, or without one ride the
    15 s to the launch at 1x."""
    load_args = {"save": "${runSave}", "name": "persistent"}
    if p.focus_pid is not None:
        load_args["focusPid"] = str(int(p.focus_pid))
    steps = [
        _step("LoadGame", load_args, budget=300),
        _step("StopRecording"),
        _step("DiscardTree"),
        _step("SetSetting", {"name": "autoRecordOnLaunch", "value": "false"}),
        _step("SetSetting", {"name": "ghostRenderTracing", "value": "true"}),
        _step("SetSetting", {"name": "mapRenderTracing", "value": "true"}),
        _step("SetSetting", {"name": "verboseLogging", "value": "true"}),
        _step("MissionConfig", {"tree": p.tree_id, "loop": "true"}),
        _step("StartLoopPlayback", {"tree": p.tree_id}, budget=300, label="slp"),
    ]
    if p.at_ut is not None:
        steps.append(_step("TimeJump", {"ut": _fmt_num(p.at_ut)}, budget=300))
        steps.append(_step("WarpToUT", {"ut": _fmt_num(p.at_ut + INSTANT_SETTLE_SECONDS),
                                        "maxRate": "1"}, budget=120))
    else:
        # StartLoopPlayback lands 15 s before the launch; ride the clock to it at
        # 1x so the ghost exists when the camera looks for it.
        steps.append(_step("WarpToUT", {"ut": "${slp.relaunchUt}", "maxRate": "1"},
                           budget=120))
    return steps


def view_preamble_steps(p: WatchLaneParams) -> List[Dict]:
    """The reusable camera setup per scene kind, plus the artifacts the same run
    leaves for review: a screenshot, and in map view the frame's geometry."""
    label = shot_label(p.lane_id, p.scene)
    if SCENE_VIEW[p.scene] == VIEW_FLIGHT:
        return [
            _step("EnterWatchMode", {"tree": p.tree_id}, budget=120),
            _step("CaptureScreenshot", {"label": label}),
        ]
    return [
        _step("EnterMapView", budget=60),
        _step("MapCamera", {"focus": p.map_focus, "pitch": _fmt_num(p.map_pitch),
                            "heading": "0", "distance": _fmt_num(p.map_distance),
                            "hold": _fmt_num(DEFAULT_MAP_HOLD_SECONDS)}),
        _step("CaptureScreenshot", {"label": label}),
        _step("DumpMapGeometry", {"label": label}),
    ]


def watch_lane_steps(p: WatchLaneParams) -> List[Dict]:
    return setup_steps(p) + view_preamble_steps(p) + [_step("FlushAndQuit")]


def required_log_contracts(p: WatchLaneParams) -> List[str]:
    label = shot_label(p.lane_id, p.scene)
    req = ["missionconfig applied:", "startloopplayback initiated:",
           "capturescreenshot ok label=%s bytes=" % label]
    if SCENE_VIEW[p.scene] == VIEW_FLIGHT:
        req.insert(2, "enterwatchmode initiated:")
    else:
        req.insert(2, "entermapview ok mapOpen=true")
        req.append("dumpmapgeometry ok label=%s scene=FLIGHT " % label)
    return req


FORBIDDEN_LOG_CONTRACTS: Tuple[str, ...] = (
    "\\[Parsek\\]\\[ERROR\\]",
    "timejump refused reason=backward-jump",
    "mapcamera rejected reason=",
    "capturescreenshot rejected reason=",
    "dumpmapgeometry rejected reason=",
)


def _toml_str(value: str) -> str:
    return '"%s"' % str(value).replace("\\", "\\\\").replace('"', '\\"')


def _toml_inline(d: Dict) -> str:
    parts = []
    for k, v in d.items():
        if isinstance(v, dict):
            parts.append("%s = %s" % (k, _toml_inline(v)))
        elif isinstance(v, bool):
            parts.append("%s = %s" % (k, "true" if v else "false"))
        elif isinstance(v, int):
            parts.append("%s = %d" % (k, v))
        else:
            parts.append("%s = %s" % (k, _toml_str(v)))
    return "{ %s }" % ", ".join(parts)


def default_description(p: WatchLaneParams) -> str:
    where = ("map view framed on %s" % p.map_focus if SCENE_VIEW[p.scene] == VIEW_MAP
             else "flight camera in watch mode on the ghost")
    when = ("at UT %s" % _fmt_num(p.at_ut)) if p.at_ut is not None else "from the launch"
    return ("WATCH LANE (%s): loop tree %s on %s, %s, %s. Unattended it takes one "
            "picture and quits; with run.py --hold it holds the game open for the "
            "owner to watch." % (p.scene, p.tree_id, os.path.basename(p.save_template),
                                 where, when))


def build_watch_spec(p: WatchLaneParams) -> str:
    """The spec TOML text for one watch lane. Deterministic: the committed lanes
    are this function's output byte for byte (test_watchlane pins them)."""
    errors = validate_params(p)
    if errors:
        raise ValueError("; ".join(errors))
    description = p.description or default_description(p)
    tags = list(WATCH_TAGS) + [p.scene, SCENE_VIEW[p.scene] + "-view"] + list(p.extra_tags)
    lines: List[str] = []
    lines.append("schema = 1")
    lines.append("id = %s" % _toml_str(p.lane_id))
    lines.append("# " + "=" * 84)
    lines.append("# GENERATED by harness/tools/make_watch_lane.py (harness/lib/watchlane.py).")
    lines.append("# Re-run the tool to change it; do not hand-edit the steps.")
    if p.command:
        lines.append("%s%s" % (GENERATED_WITH_PREFIX, p.command))
    lines.append("#")
    lines.append("# Unattended: setup, one picture, FlushAndQuit, light expectations.")
    lines.append("# Attended:   python harness/run.py --id %s --hold   (close KSP to end it)."
                 % p.lane_id)
    for note in p.notes:
        lines.append("#" + ("" if not note else " " + note))
    lines.append("# " + "=" * 84)
    lines.append('tier = "operator"')
    lines.append("description = %s" % _toml_str(description))
    lines.append("instanceProfile = %s" % _toml_str(p.instance_profile))
    lines.append("tags = [%s]" % ", ".join(_toml_str(t) for t in tags))
    lines.append("")
    lines.append("[fixture]")
    lines.append("saveTemplate       = %s" % _toml_str(p.save_template))
    lines.append('injectedRecordings = "none"')
    lines.append("craft              = []")
    lines.append("")
    lines.append("[watch]")
    lines.append("%s = %s" % (WATCH_SCENE_KEY, _toml_str(p.scene)))
    if p.tree_handle and p.tree_handle != p.tree_id:
        lines.append("%s = %s" % (WATCH_TREE_HANDLE_KEY, _toml_str(p.tree_handle)))
    lines.append("")
    lines.append("[driver]")
    lines.append('kind = "seam"')
    lines.append("steps = [")
    for s in watch_lane_steps(p):
        lines.append("  %s," % _toml_inline(s))
    lines.append("]")
    lines.append("")
    lines.append("[dimensionsCovered]")
    lines.append("")
    lines.append("[expectations]")
    lines.append("allowedAnomalies = []")
    if p.count_min is not None:
        lines.append("")
        lines.append("[expectations.recordings]")
        lines.append("count = { min = %d, max = %d }" % (p.count_min, p.count_max))
    lines.append("")
    lines.append("[expectations.logContracts]")
    lines.append("required = [")
    for r in required_log_contracts(p):
        lines.append("  %s," % _toml_str(r))
    lines.append("]")
    lines.append("forbidden = [")
    for f in FORBIDDEN_LOG_CONTRACTS:
        lines.append("  %s," % _toml_str(f))
    lines.append("]")
    lines.append("")
    lines.append("[runtime]")
    lines.append("budgetSeconds = %d" % p.budget_seconds)
    lines.append("")
    lines.append("[retry]")
    lines.append('policy = "once"')
    lines.append("")
    lines.append("[expectedFail]")
    lines.append('bugId = ""')
    return "\n".join(lines) + "\n"
