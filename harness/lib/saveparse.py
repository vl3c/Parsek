"""Pure save-parse decisions for the M-C2 structural save-content verifier (R9).

This module is the R9 analogue of ``oracle.py``: the harness shell (``run.py``)
reads the produced save's ``persistent.sfs`` text off disk and every DECISION
about that text lives here, side-effect-free, so it is unit-testable with NO
KSP, NO network, and NO filesystem writes. It covers:

  - a tolerant-but-fail-loud ConfigNode TEXT parser (``parse_sfs``): the .sfs
    format is ``key = value`` lines plus ``NAME { ... }`` blocks. Unbalanced
    braces or a brace with no node name are DEFINED parse faults (recorded,
    never an exception) - a truncated save must never read as "zero rows".
  - the Parsek SCENARIO surface extraction (``parse_parsek_scenario``):
    RECORDING_TREE topology (recordings, ids, branch points, terminal states,
    merge states), RECORDING_SUPERSEDES rows, LEDGER_TOMBSTONES rows,
    RECORDING_REWIND_RETIREMENTS rows, and REWIND_POINTS/POINT rows with their
    CHILD_SLOTs. Shapes are pinned against the C# writers (see the per-surface
    notes below); the writers are SPARSE (absent key => default), so every
    optional read here defaults rather than assumes.
  - the measured structural facets (``observed_structure_facets``), mirroring
    the ``[expectations.*]`` block shape the way
    ``hlib.observed_expectation_facets`` does for ``recordings.count``.
  - the ``[expectations.rewind]`` / ``[expectations.recordings.structure]``
    evaluation (``evaluate_save_structure``) and their spec-surface validators
    (``validate_rewind_expectations`` / ``validate_structure_expectations``,
    called from ``hlib.validate_spec``).

VERDICT NEUTRALITY (binding, and the reason the verifier ships REPORT-ONLY):
S4.1-rewind-merge already DECLARES an ``[expectations.rewind]`` block, so a
naively-gating verifier would change a committed nightly's verdict without a
live run to prove the readings. Evaluation therefore defaults to REPORT-ONLY
(status ``REPORT``; mismatches recorded in the run JSON, verdict untouched)
unless the block itself opts in with ``gating = true`` - a key ZERO committed
specs declare (guarded by a test-suite sweep, mirroring the
``[expectations.unityExceptions]`` precedent). Promotion to gating is an
operator decision taken after reading the report-only facets off the next
local S4.1 / CL-2 runs.

Serialization facts consumed here, pinned against the C# writers
(``ParsekScenario.SaveRewindStagingState``, ``RecordingTree.Save``,
``RecordingTreeRecordCodec.SaveRecordingInto``, ``RewindPoint.SaveInto``,
``RecordingSupersedeRelation.SaveInto``, ``LedgerTombstone.SaveInto``,
``RecordingRewindRetirement.SaveInto``, ``ChildSlot.SaveInto``):

  - bools serialize ``True``/``False`` (read back case-insensitively);
  - ``BRANCH_POINT.type`` and ``terminalState`` are NUMERIC enum ints;
    ``mergeState`` is a NAME string whose default ``Immutable`` is OMITTED;
  - the four staging parents (``REWIND_POINTS``, ``RECORDING_SUPERSEDES``,
    ``RECORDING_REWIND_RETIREMENTS``, ``LEDGER_TOMBSTONES``) are written ONLY
    when non-empty - an absent parent means zero rows, and rows are ``ENTRY``
    children EXCEPT RewindPoints, whose child is ``POINT``;
  - the active / pending tree markers are ``isActive = True`` /
    ``isPending = True`` value keys on RECORDING_TREE; a tree with neither is
    a committed tree;
  - duplicate keys are DATA (``parentId`` / ``childId`` on BRANCH_POINT), so
    the node model keeps values as an ordered list, not a dict.

ASCII only; stdlib only.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Tuple

# ---------------------------------------------------------------------------
# Generic ConfigNode text parsing.
# ---------------------------------------------------------------------------


@dataclass
class SfsNode:
    """One ConfigNode: ordered values (duplicates preserved) + ordered children."""

    name: str
    values: List[Tuple[str, str]] = field(default_factory=list)
    nodes: List["SfsNode"] = field(default_factory=list)

    def value(self, key: str) -> Optional[str]:
        """First value for ``key`` (ConfigNode.GetValue reads the first), or None."""
        for k, v in self.values:
            if k == key:
                return v
        return None

    def values_named(self, key: str) -> List[str]:
        """Every value for ``key`` in order (duplicate keys are data)."""
        return [v for k, v in self.values if k == key]

    def nodes_named(self, name: str) -> List["SfsNode"]:
        return [n for n in self.nodes if n.name == name]

    def first(self, name: str) -> Optional["SfsNode"]:
        for n in self.nodes:
            if n.name == name:
                return n
        return None


@dataclass(frozen=True)
class SfsParseResult:
    """Outcome of ``parse_sfs``. ``root`` is a synthetic unnamed node holding the
    file's top-level nodes/values; ``error`` non-empty => the text is NOT a
    well-formed ConfigNode file and ``root`` is best-effort only (a caller must
    treat counts from an errored parse as unusable, never as zero)."""

    root: SfsNode
    error: str = ""

    @property
    def ok(self) -> bool:
        return not self.error


def parse_sfs(text: Optional[str]) -> SfsParseResult:
    """Parse ConfigNode-format text (a .sfs / .cfg body) into an SfsNode tree.

    Grammar actually produced by KSP's ConfigNode writer: a node is a NAME line
    followed by ``{`` on its own line, values are ``key = value`` lines (value
    may be empty), ``}`` closes the innermost node. Tolerated beyond that
    (matching ConfigNode.Load's PreFormatConfig leniency, which splits braces
    onto their own logical tokens): ``NAME {``, ``NAME { }``, and other
    brace-sharing line forms on NON-value lines, blank lines, and whole-line
    ``//`` comments. A line containing ``=`` is always one value (values are
    never brace-split, so a value containing a brace survives intact). DEFINED
    faults (recorded in ``error``, parse continues best-effort so triage still
    sees what WAS readable):

      - ``}`` with no open node ("unbalanced close at line N");
      - EOF with unclosed node(s) ("N unclosed node(s) at EOF") - the
        truncated-file case;
      - ``{`` with no preceding name line ("brace without node name");
      - a dangling name line never followed by ``{``.
    """
    root = SfsNode(name="")
    stack: List[SfsNode] = [root]
    pending_name: Optional[str] = None
    errors: List[str] = []

    # A leading UTF-8 BOM would rename the first node to "<BOM>GAME" and trip
    # the GAME-node requirement downstream. KSP's own ConfigNode.Save writes
    # UTF-8 without BOM, but fixture .sfs files are tool- and hand-authored
    # (Windows PowerShell 5.1 -Encoding UTF8 emits one by default), so strip it
    # here where every caller benefits (adversarial-review round-2 NEW-1).
    text = (text or "").lstrip("\ufeff")

    def open_node(name: str) -> None:
        child = SfsNode(name=name)
        stack[-1].nodes.append(child)
        stack.append(child)

    def handle_token(token: str, lineno: int) -> None:
        nonlocal pending_name
        if token == "{":
            if pending_name is None:
                errors.append("brace without node name at line %d" % lineno)
                pending_name = ""
            open_node(pending_name)
            pending_name = None
            return
        if token == "}":
            if pending_name is not None:
                errors.append("dangling node name %r at line %d" % (pending_name, lineno))
                pending_name = None
            if len(stack) == 1:
                errors.append("unbalanced close at line %d" % lineno)
                return
            stack.pop()
            return
        # Bare token: a node name awaiting its opening brace.
        if pending_name is not None:
            errors.append("dangling node name %r before line %d" % (pending_name, lineno))
        pending_name = token

    for lineno, raw in enumerate((text or "").splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("//"):
            continue

        if "=" in line:
            if pending_name is not None:
                errors.append("dangling node name %r before line %d" % (pending_name, lineno))
                pending_name = None
            key, _, value = line.partition("=")
            stack[-1].values.append((key.strip(), value.strip()))
            continue

        # Non-value line: split braces onto their own tokens (PreFormatConfig).
        buf = ""
        for ch in line:
            if ch in "{}":
                if buf.strip():
                    handle_token(buf.strip(), lineno)
                buf = ""
                handle_token(ch, lineno)
            else:
                buf += ch
        if buf.strip():
            handle_token(buf.strip(), lineno)

    if pending_name is not None:
        errors.append("dangling node name %r at EOF" % pending_name)
    if len(stack) > 1:
        errors.append("%d unclosed node(s) at EOF" % (len(stack) - 1))

    return SfsParseResult(root=root, error="; ".join(errors))


def _parse_bool(raw: Optional[str], default: bool = False) -> bool:
    """bool.TryParse semantics: 'True'/'true'/'False'/'false'; else default."""
    if raw is None:
        return default
    low = raw.strip().lower()
    if low == "true":
        return True
    if low == "false":
        return False
    return default


def _parse_int(raw: Optional[str]) -> Optional[int]:
    if raw is None:
        return None
    try:
        return int(raw.strip())
    except ValueError:
        return None


def _parse_float(raw: Optional[str]) -> Optional[float]:
    if raw is None:
        return None
    try:
        return float(raw.strip())
    except ValueError:
        return None


# ---------------------------------------------------------------------------
# Parsek SCENARIO surface extraction.
# ---------------------------------------------------------------------------

PARSEK_SCENARIO_NAME = "ParsekScenario"

# TerminalState (Source/Parsek/TerminalState.cs) - NUMERIC on disk.
TERMINAL_STATE_NAMES: Tuple[str, ...] = (
    "Orbiting", "Landed", "Splashed", "SubOrbital",
    "Destroyed", "Recovered", "Docked", "Boarded",
)

# BranchPointType (Source/Parsek/BranchPoint.cs) - NUMERIC on disk, so
# VesselSwitchContinuation appears as ``type = 8``.
BRANCH_TYPE_NAMES: Tuple[str, ...] = (
    "Undock", "EVA", "Dock", "Board", "JointBreak",
    "Launch", "Breakup", "Terminal", "VesselSwitchContinuation",
)

# MergeState (Source/Parsek/MergeState.cs) - NAME string on disk; the default
# Immutable is OMITTED by the writer, so an absent key means Immutable.
MERGE_STATE_DEFAULT = "Immutable"


def terminal_state_name(value: Optional[int]) -> Optional[str]:
    """Numeric terminalState -> enum name; unknown ints -> their decimal string
    (a future enum member must surface as itself, never crash or collapse)."""
    if value is None:
        return None
    if 0 <= value < len(TERMINAL_STATE_NAMES):
        return TERMINAL_STATE_NAMES[value]
    return str(value)


def branch_type_name(value: Optional[int]) -> Optional[str]:
    if value is None:
        return None
    if 0 <= value < len(BRANCH_TYPE_NAMES):
        return BRANCH_TYPE_NAMES[value]
    return str(value)


@dataclass(frozen=True)
class RecordingRow:
    recording_id: str
    vessel_name: str
    terminal_state: Optional[int]  # None = key absent (recording not terminal)
    merge_state: str               # absent key => Immutable (writer omits default)
    is_debris: bool
    parent_anchor_recording_id: Optional[str]
    schema_generation: Optional[int]


@dataclass(frozen=True)
class BranchPointRow:
    bp_id: str
    bp_type: Optional[int]  # None = unparseable 'type' (defined fault, surfaced)
    parent_ids: Tuple[str, ...]
    child_ids: Tuple[str, ...]
    rewind_point_id: Optional[str]


@dataclass(frozen=True)
class TreeRow:
    tree_id: str
    root_recording_id: str
    is_active: bool     # isActive = True marker (the in-flight tree)
    is_pending: bool    # isPending = True marker (the stashed pending tree)
    schema_generation: Optional[int]
    recordings: Tuple[RecordingRow, ...]
    branch_points: Tuple[BranchPointRow, ...]

    @property
    def is_committed(self) -> bool:
        """A tree with NEITHER marker is USUALLY a committed tree. One shipped
        writer exception: ParsekScenario.SavePendingTreeIfAny's fallback branch
        serializes a Limbo/LimboVesselSwitch pending tree marker-less when an
        active-tree node is already present ("as committed-node"), so a pin on
        the committedTrees facet can over-count by that pending tree."""
        return not self.is_active and not self.is_pending


@dataclass(frozen=True)
class SupersedeRow:
    relation_id: str
    old_recording_id: str
    new_recording_id: str
    ut: Optional[float]


@dataclass(frozen=True)
class TombstoneRow:
    tombstone_id: str
    action_id: str
    retiring_recording_id: str
    ut: Optional[float]


@dataclass(frozen=True)
class RewindRetirementRow:
    retirement_id: str
    recording_id: str
    reason: str


@dataclass(frozen=True)
class RewindPointRow:
    rewind_point_id: str
    branch_point_id: str
    session_provisional: bool  # ALWAYS written by RewindPoint.SaveInto
    corrupted: bool            # sparse: only written when true
    quicksave_filename: str
    slot_count: int            # CHILD_SLOT children


@dataclass(frozen=True)
class ParsekSaveSnapshot:
    """The parsed Parsek SCENARIO surface of one .sfs text.

    ``parsed`` False => the TEXT was not a readable ConfigNode file (truncated,
    unbalanced) and every count below is UNUSABLE - a consumer must fail loud,
    never read zero rows off a torn file. ``scenario_found`` False with
    ``parsed`` True => a well-formed save with NO ParsekScenario node (the
    fresh-* templates): all counts are genuinely zero there.
    """

    parsed: bool
    error: str
    scenario_found: bool
    trees: Tuple[TreeRow, ...] = ()
    supersedes: Tuple[SupersedeRow, ...] = ()
    tombstones: Tuple[TombstoneRow, ...] = ()
    rewind_retirements: Tuple[RewindRetirementRow, ...] = ()
    rewind_points: Tuple[RewindPointRow, ...] = ()

    @property
    def recordings(self) -> Tuple[RecordingRow, ...]:
        out: List[RecordingRow] = []
        for t in self.trees:
            out.extend(t.recordings)
        return tuple(out)

    @property
    def branch_points(self) -> Tuple[BranchPointRow, ...]:
        out: List[BranchPointRow] = []
        for t in self.trees:
            out.extend(t.branch_points)
        return tuple(out)


def _find_parsek_scenarios(root: SfsNode) -> List[SfsNode]:
    """Every SCENARIO node with name=ParsekScenario, depth-first. The real path
    is GAME > SCENARIO, but a recursive walk keeps the extraction robust to the
    wrapper (an RP quicksave sidecar is also a GAME file)."""
    found: List[SfsNode] = []
    stack = [root]
    while stack:
        node = stack.pop()
        if node.name == "SCENARIO" and node.value("name") == PARSEK_SCENARIO_NAME:
            found.append(node)
        # reversed() keeps document order in the DFS output.
        stack.extend(reversed(node.nodes))
    return found


def _parse_recording(node: SfsNode) -> RecordingRow:
    return RecordingRow(
        recording_id=node.value("recordingId") or "",
        vessel_name=node.value("vesselName") or "",
        terminal_state=_parse_int(node.value("terminalState")),
        merge_state=node.value("mergeState") or MERGE_STATE_DEFAULT,
        is_debris=_parse_bool(node.value("isDebris"), default=False),
        parent_anchor_recording_id=node.value("parentAnchorRecordingId"),
        schema_generation=_parse_int(node.value("recordingSchemaGeneration")),
    )


def _parse_branch_point(node: SfsNode) -> BranchPointRow:
    return BranchPointRow(
        bp_id=node.value("id") or "",
        bp_type=_parse_int(node.value("type")),
        parent_ids=tuple(node.values_named("parentId")),
        child_ids=tuple(node.values_named("childId")),
        rewind_point_id=node.value("rewindPointId"),
    )


def _parse_tree(node: SfsNode) -> TreeRow:
    return TreeRow(
        tree_id=node.value("id") or "",
        root_recording_id=node.value("rootRecordingId") or "",
        is_active=_parse_bool(node.value("isActive"), default=False),
        is_pending=_parse_bool(node.value("isPending"), default=False),
        schema_generation=_parse_int(node.value("recordingSchemaGeneration")),
        recordings=tuple(_parse_recording(r) for r in node.nodes_named("RECORDING")),
        branch_points=tuple(_parse_branch_point(b) for b in node.nodes_named("BRANCH_POINT")),
    )


def parse_parsek_scenario(text: Optional[str]) -> ParsekSaveSnapshot:
    """Parse a .sfs text into the Parsek structural snapshot.

    Never raises on malformed input: a torn/unbalanced file returns
    ``parsed=False`` with the fault named in ``error``. Multiple ParsekScenario
    SCENARIO nodes in one file is a writer-contract violation and is surfaced
    the same way (never a silent first-wins). A text with NO top-level ``GAME``
    node is also a defined fault: every KSP save (persistent.sfs, quicksave,
    RP sidecar - all 15 committed fixture .sfs files included) is a GAME file,
    and the degenerate truncation - a zero-byte / whitespace-only file, exactly
    what an interrupted save write leaves behind - is trivially brace-balanced,
    so without this check it would read as a clean all-zero parse and an armed
    ``max = 0`` window would PASS on a torn save (adversarial-review finding 1).
    """
    res = parse_sfs(text)
    if not res.ok:
        return ParsekSaveSnapshot(parsed=False, error=res.error, scenario_found=False)
    if res.root.first("GAME") is None:
        return ParsekSaveSnapshot(
            parsed=False,
            error="no top-level GAME node (empty, truncated-to-empty, or non-save text)",
            scenario_found=False)

    scenarios = _find_parsek_scenarios(res.root)
    if not scenarios:
        return ParsekSaveSnapshot(parsed=True, error="", scenario_found=False)
    if len(scenarios) > 1:
        return ParsekSaveSnapshot(
            parsed=False,
            error="%d ParsekScenario SCENARIO nodes in one file" % len(scenarios),
            scenario_found=True)
    sc = scenarios[0]

    trees = tuple(_parse_tree(t) for t in sc.nodes_named("RECORDING_TREE"))

    supersedes: List[SupersedeRow] = []
    sup_parent = sc.first("RECORDING_SUPERSEDES")
    if sup_parent is not None:
        for e in sup_parent.nodes_named("ENTRY"):
            supersedes.append(SupersedeRow(
                relation_id=e.value("relationId") or "",
                old_recording_id=e.value("oldRecordingId") or "",
                new_recording_id=e.value("newRecordingId") or "",
                ut=_parse_float(e.value("ut"))))

    tombstones: List[TombstoneRow] = []
    tomb_parent = sc.first("LEDGER_TOMBSTONES")
    if tomb_parent is not None:
        for e in tomb_parent.nodes_named("ENTRY"):
            tombstones.append(TombstoneRow(
                tombstone_id=e.value("tombstoneId") or "",
                action_id=e.value("actionId") or "",
                retiring_recording_id=e.value("retiringRecordingId") or "",
                ut=_parse_float(e.value("ut"))))

    retirements: List[RewindRetirementRow] = []
    ret_parent = sc.first("RECORDING_REWIND_RETIREMENTS")
    if ret_parent is not None:
        for e in ret_parent.nodes_named("ENTRY"):
            retirements.append(RewindRetirementRow(
                retirement_id=e.value("retirementId") or "",
                recording_id=e.value("recordingId") or "",
                reason=e.value("reason") or ""))

    rps: List[RewindPointRow] = []
    rp_parent = sc.first("REWIND_POINTS")
    if rp_parent is not None:
        # The one staging list whose child is POINT, not ENTRY (writer contract).
        for p in rp_parent.nodes_named("POINT"):
            rps.append(RewindPointRow(
                rewind_point_id=p.value("rewindPointId") or "",
                branch_point_id=p.value("branchPointId") or "",
                session_provisional=_parse_bool(p.value("sessionProvisional"), default=True),
                corrupted=_parse_bool(p.value("corrupted"), default=False),
                quicksave_filename=p.value("quicksaveFilename") or "",
                slot_count=len(p.nodes_named("CHILD_SLOT"))))

    return ParsekSaveSnapshot(
        parsed=True, error="", scenario_found=True,
        trees=trees, supersedes=tuple(supersedes), tombstones=tuple(tombstones),
        rewind_retirements=tuple(retirements), rewind_points=tuple(rps))


def duplicate_recording_ids(snapshot: ParsekSaveSnapshot) -> Tuple[str, ...]:
    """Recording ids appearing more than once across ALL trees, first-seen order.
    A duplicated id is a writer-contract violation worth surfacing in triage
    (and exactly the hand-mutation an adversarial reader would try)."""
    seen: Dict[str, int] = {}
    order: List[str] = []
    for rec in snapshot.recordings:
        rid = rec.recording_id
        if not rid:
            continue
        seen[rid] = seen.get(rid, 0) + 1
        if seen[rid] == 2:
            order.append(rid)
    return tuple(order)


# ---------------------------------------------------------------------------
# Measured structural facets (the ``observed`` mirror of the spec blocks).
# ---------------------------------------------------------------------------


def observed_structure_facets(snapshot: Optional[ParsekSaveSnapshot]) -> Dict[str, Any]:
    """The MEASURED facets recorded alongside the verifier row, mirroring the
    ``[expectations.*]`` spec shape so a future pin slots in beside its spec
    counterpart (the ``hlib.observed_expectation_facets`` convention). Recorded
    UNCONDITIONALLY on a readable save - that is how a scenario with no block
    yet earns its first honest window off a green run. ``None`` / unparsed
    snapshot => ``{}`` (ABSENT means "not measured", never zero)."""
    if snapshot is None or not snapshot.parsed:
        return {}
    terminal_counts: Dict[str, int] = {}
    for rec in snapshot.recordings:
        name = terminal_state_name(rec.terminal_state)
        if name is not None:
            terminal_counts[name] = terminal_counts.get(name, 0) + 1
    branch_counts: Dict[str, int] = {}
    for bp in snapshot.branch_points:
        name = branch_type_name(bp.bp_type)
        if name is None:
            name = "unparsed"
        branch_counts[name] = branch_counts.get(name, 0) + 1
    return {
        "rewind": {
            "supersedeRows": len(snapshot.supersedes),
            "tombstones": len(snapshot.tombstones),
            "rewindPoints": len(snapshot.rewind_points),
            # Measured-only facet (no spec window yet): the retirement rows are
            # parsed, so record them - a parsed-then-discarded surface is a
            # doc claim nothing backs (adversarial-review finding 5).
            "rewindRetirements": len(snapshot.rewind_retirements),
        },
        "recordings": {
            "structure": {
                "trees": len(snapshot.trees),
                "committedTrees": sum(1 for t in snapshot.trees if t.is_committed),
                "recordings": len(snapshot.recordings),
                "terminalStates": terminal_counts,
                "branchPoints": branch_counts,
                # Measured-only triage facet: a duplicated recording id is a
                # writer-contract violation; surfacing it here is what makes
                # the duplicate_recording_ids helper reach the run JSON.
                "duplicateRecordingIds": list(duplicate_recording_ids(snapshot)),
            },
        },
    }


# ---------------------------------------------------------------------------
# Spec-surface vocabulary + validation ([expectations.rewind] /
# [expectations.recordings.structure]).
# ---------------------------------------------------------------------------

GATING_KEY = "gating"

REWIND_BLOCK = "rewind"
REWIND_BLOCK_KEYS: Tuple[str, ...] = (
    GATING_KEY, "supersedeRows", "tombstones", "rewindPoints")

STRUCTURE_BLOCK = "structure"  # nested under [expectations.recordings]
STRUCTURE_BLOCK_KEYS: Tuple[str, ...] = (
    GATING_KEY, "trees", "committedTrees", "recordings",
    "terminalStates", "branchPoints")

STATUS_REPORT = "REPORT"
STATUS_PASS = "PASS"
STATUS_FAIL = "FAIL"
STATUS_SKIPPED = "SKIPPED"


def _validate_window(prefix: str, val: Any) -> List[str]:
    """A count assertion is either a bare non-negative int (exact pin) or a
    ``{ min =, max = }`` table with at least one bound, ints >= 0, min <= max.
    (bool is an int subclass in Python - rejected explicitly.)"""
    if isinstance(val, bool):
        return ["%s: %r must be a non-negative int or { min =, max = }" % (prefix, val)]
    if isinstance(val, int):
        return [] if val >= 0 else ["%s: %d must be >= 0" % (prefix, val)]
    if not isinstance(val, dict):
        return ["%s: %r must be a non-negative int or { min =, max = }" % (prefix, val)]
    errs: List[str] = []
    unknown = sorted(k for k in val if k not in ("min", "max"))
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: min, max)" % (prefix, unknown))
    if not any(k in val for k in ("min", "max")):
        errs.append("%s: an empty window gates nothing - declare min and/or max" % prefix)
    for k in ("min", "max"):
        if k in val and (isinstance(val[k], bool) or not isinstance(val[k], int) or val[k] < 0):
            errs.append("%s.%s: %r must be a non-negative int" % (prefix, k, val[k]))
    lo, hi = val.get("min"), val.get("max")
    if (isinstance(lo, int) and not isinstance(lo, bool)
            and isinstance(hi, int) and not isinstance(hi, bool) and lo > hi):
        errs.append("%s: min %d > max %d" % (prefix, lo, hi))
    return errs


def _validate_gating(prefix: str, block: Dict) -> List[str]:
    if GATING_KEY in block and not isinstance(block[GATING_KEY], bool):
        return ["%s.%s: %r must be a bool" % (prefix, GATING_KEY, block[GATING_KEY])]
    return []


def _validate_armed_empty(prefix: str, block: Dict, assertion_keys: Tuple[str, ...]) -> List[str]:
    """``gating = true`` with ZERO assertion keys is a gate the author believes
    is on and that can never red - the same failure ``_validate_window`` refuses
    one level down, applied one level up (adversarial-review finding 4)."""
    if block.get(GATING_KEY) is True and not any(k in block for k in assertion_keys):
        return ["%s: gating = true with no assertion key gates nothing - "
                "declare at least one of %s" % (prefix, list(assertion_keys))]
    return []


def validate_rewind_expectations(block: Any) -> List[str]:
    """Validate the ``[expectations.rewind]`` spec surface (pre-launch, pure).
    None => no block declared => valid."""
    if block is None:
        return []
    if not isinstance(block, dict):
        return ["expectations.rewind: must be a table"]
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in REWIND_BLOCK_KEYS)
    if unknown:
        errs.append("expectations.rewind: unknown key(s) %s (accepted: %s)"
                    % (unknown, list(REWIND_BLOCK_KEYS)))
    errs.extend(_validate_gating("expectations.rewind", block))
    errs.extend(_validate_armed_empty(
        "expectations.rewind", block, ("supersedeRows", "tombstones", "rewindPoints")))
    for key in ("supersedeRows", "tombstones", "rewindPoints"):
        if key in block:
            errs.extend(_validate_window("expectations.rewind.%s" % key, block[key]))
    return errs


def validate_structure_expectations(block: Any) -> List[str]:
    """Validate the ``[expectations.recordings.structure]`` spec surface.
    None => no block declared => valid."""
    if block is None:
        return []
    if not isinstance(block, dict):
        return ["expectations.recordings.structure: must be a table"]
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in STRUCTURE_BLOCK_KEYS)
    if unknown:
        errs.append("expectations.recordings.structure: unknown key(s) %s (accepted: %s)"
                    % (unknown, list(STRUCTURE_BLOCK_KEYS)))
    errs.extend(_validate_gating("expectations.recordings.structure", block))
    errs.extend(_validate_armed_empty(
        "expectations.recordings.structure", block,
        ("trees", "committedTrees", "recordings", "terminalStates", "branchPoints")))
    for key in ("trees", "committedTrees", "recordings"):
        if key in block:
            errs.extend(_validate_window(
                "expectations.recordings.structure.%s" % key, block[key]))
    for key, names in (("terminalStates", TERMINAL_STATE_NAMES),
                       ("branchPoints", BRANCH_TYPE_NAMES)):
        if key not in block:
            continue
        sub = block[key]
        prefix = "expectations.recordings.structure.%s" % key
        if not isinstance(sub, dict):
            errs.append("%s: must be a table of { <Name> = <window> }" % prefix)
            continue
        unknown = sorted(k for k in sub if k not in names)
        if unknown:
            errs.append("%s: unknown name(s) %s (accepted: %s)"
                        % (prefix, unknown, list(names)))
        for name, window in sub.items():
            if name in names:
                errs.extend(_validate_window("%s.%s" % (prefix, name), window))
    return errs


def save_structure_expectation_warnings(expectations: Optional[Dict]) -> List[str]:
    """Inert-declaration WARNINGS (never errors): a declared-but-assertion-less
    UNARMED block degrades silently to an empty report row - the author wrote a
    header and asserted nothing. Mirrors ``unity_exception_expectation_warnings``.
    (The ARMED-and-empty form is a hard error in the validators above.)"""
    expectations = expectations or {}
    warns: List[str] = []
    rewind = expectations.get(REWIND_BLOCK)
    if isinstance(rewind, dict) and not any(
            k in rewind for k in ("supersedeRows", "tombstones", "rewindPoints")):
        warns.append("expectations.rewind: declared with no assertion key - the "
                     "block reports nothing and gates nothing")
    recordings = expectations.get("recordings")
    structure = (recordings.get(STRUCTURE_BLOCK)
                 if isinstance(recordings, dict) else None)
    if isinstance(structure, dict) and not any(
            k in structure for k in ("trees", "committedTrees", "recordings",
                                     "terminalStates", "branchPoints")):
        warns.append("expectations.recordings.structure: declared with no assertion "
                     "key - the block reports nothing and gates nothing")
    return warns


# ---------------------------------------------------------------------------
# Evaluation (the verifier decision). Pure over the parsed snapshot.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class SaveStructureResult:
    """Verifier outcome. ``status``:

    - ``REPORT``: no block armed (verdict-neutral default). Mismatches, if
      any, are RECORDED but move no verdict - the promotion path reads them
      off the run JSON.
    - ``PASS`` / ``FAIL``: at least one declared block armed ``gating = true``.
      GATING IS PER-BLOCK (adversarial-review finding 3): ``status`` is
      computed from the ARMED blocks' mismatches only, so arming a proven
      block while a second exploratory block keeps reporting does NOT
      silently promote the exploratory block to a gate. ``FAIL`` must be
      mapped by the caller to the ``save_structure_mismatch`` verifier flag
      (PARSEK-FAIL, subkind ``save-structure``).
    ``mismatches`` carries EVERY mismatch (armed or not - the report value);
    ``armed_mismatches`` carries the verdict-driving subset. ``observed``
    carries the measured facets regardless of status. A structural fault
    (unreadable save / no ParsekScenario node) is attributed to every
    declared block, so it gates whenever anything is armed.
    """

    status: str
    gating: bool                      # any declared block armed
    mismatches: Tuple[str, ...]       # all mismatches, armed + report-only
    observed: Dict[str, Any]
    blocks: Tuple[str, ...]           # declared blocks: "rewind" / "recordings.structure"
    armed_blocks: Tuple[str, ...]     # the gating = true subset of blocks
    armed_mismatches: Tuple[str, ...]  # the verdict-driving subset
    scenario_found: Optional[bool]    # None = save unreadable / not read


def _structure_block(expectations: Dict) -> Optional[Dict]:
    recordings = expectations.get("recordings")
    structure = recordings.get(STRUCTURE_BLOCK) if isinstance(recordings, dict) else None
    return structure if isinstance(structure, dict) else None


def declared_structure_blocks(expectations: Optional[Dict]) -> Tuple[str, ...]:
    """Which of the two M-C2 blocks the spec declares, stable order."""
    expectations = expectations or {}
    out: List[str] = []
    if isinstance(expectations.get(REWIND_BLOCK), dict):
        out.append(REWIND_BLOCK)
    if _structure_block(expectations) is not None:
        out.append("recordings.structure")
    return tuple(out)


def armed_structure_blocks(expectations: Optional[Dict]) -> Tuple[str, ...]:
    """The subset of declared blocks carrying ``gating = true``, stable order.
    Arming is PER-BLOCK: the key lives inside each block and gates only that
    block's assertions."""
    expectations = expectations or {}
    out: List[str] = []
    rewind = expectations.get(REWIND_BLOCK)
    if isinstance(rewind, dict) and rewind.get(GATING_KEY) is True:
        out.append(REWIND_BLOCK)
    structure = _structure_block(expectations)
    if structure is not None and structure.get(GATING_KEY) is True:
        out.append("recordings.structure")
    return tuple(out)


def gating_armed(expectations: Optional[Dict]) -> bool:
    """True iff ANY declared M-C2 block carries ``gating = true``. Zero
    committed specs do (guarded by a test-suite sweep); arming is an operator
    decision taken after reading report-only facets off green runs."""
    return bool(armed_structure_blocks(expectations))


def _check_window(label: str, spec_val: Any, measured: int,
                  mismatches: List[str]) -> None:
    """Evaluate one count assertion. Bare int = exact pin; table = min/max
    window (either bound optional). Shapes beyond that were rejected at spec
    validation; tolerate them here as no-ops so a drifted spec cannot crash
    the verifier mid-chain."""
    if isinstance(spec_val, bool):
        return
    if isinstance(spec_val, int):
        if measured != spec_val:
            mismatches.append("%s %d != %d" % (label, measured, spec_val))
        return
    if not isinstance(spec_val, dict):
        return
    lo, hi = spec_val.get("min"), spec_val.get("max")
    if isinstance(lo, int) and not isinstance(lo, bool) and measured < lo:
        mismatches.append("%s %d < min %d" % (label, measured, lo))
    if isinstance(hi, int) and not isinstance(hi, bool) and measured > hi:
        mismatches.append("%s %d > max %d" % (label, measured, hi))


def evaluate_save_structure(
        expectations: Optional[Dict],
        snapshot: Optional[ParsekSaveSnapshot]) -> SaveStructureResult:
    """Evaluate the declared M-C2 blocks against the parsed save snapshot.

    Two structural faults are DEFINED mismatches whenever any block is
    declared, attributed to every declared block (so they gate whenever
    anything is armed):

    - ``snapshot`` None (persistent.sfs missing) or ``parsed=False`` (torn /
      unbalanced / empty text): "a save that cannot be parsed proves nothing"
      - fail loud, never read a torn file as zero rows.
    - a readable save with NO ParsekScenario SCENARIO node
      (adversarial-review finding 2): ParsekScenario is AddToAllGames, so its
      absence from a PRODUCED save means Parsek never loaded (wrong DLL, MM
      failure, load exception) - the most common environmental failure a run
      has, and structurally indistinguishable from "Parsek wrote zero rows"
      without this check. (The fresh-* templates legitimately lack the node,
      but they are never the produced save of a run declaring an M-C2 block.)

    With no block declared, both degrade to an empty REPORT row with the
    fault visible in the caller's ``parsed`` / ``scenarioFound`` fields.
    """
    expectations = expectations or {}
    blocks = declared_structure_blocks(expectations)
    armed = armed_structure_blocks(expectations)
    observed = observed_structure_facets(snapshot)
    # Per-block mismatch partition (finding 3): only armed blocks' mismatches
    # drive PASS/FAIL; everything lands in the report list.
    per_block: Dict[str, List[str]] = {b: [] for b in blocks}

    unreadable = snapshot is None or not snapshot.parsed
    scenario_found: Optional[bool] = None if unreadable else snapshot.scenario_found
    if unreadable:
        reason = "missing persistent.sfs" if snapshot is None else snapshot.error
        for b in blocks:
            per_block[b].append("save unreadable: %s" % (reason,))
    elif not snapshot.scenario_found and blocks:
        for b in blocks:
            per_block[b].append(
                "no ParsekScenario node in the produced save (Parsek never "
                "loaded, or the save is not a Parsek save)")
    else:
        facets = observed  # parsed => facets present
        rewind_spec = expectations.get(REWIND_BLOCK)
        if isinstance(rewind_spec, dict):
            measured = facets["rewind"]
            for key in ("supersedeRows", "tombstones", "rewindPoints"):
                if key in rewind_spec:
                    _check_window("rewind.%s" % key, rewind_spec[key],
                                  measured[key], per_block[REWIND_BLOCK])
        structure_spec = _structure_block(expectations)
        if structure_spec is not None:
            out = per_block["recordings.structure"]
            measured = facets["recordings"]["structure"]
            for key in ("trees", "committedTrees", "recordings"):
                if key in structure_spec:
                    _check_window("recordings.structure.%s" % key,
                                  structure_spec[key], measured[key], out)
            for group in ("terminalStates", "branchPoints"):
                sub = structure_spec.get(group)
                if not isinstance(sub, dict):
                    continue
                for name, window in sub.items():
                    count = measured[group].get(name, 0)
                    _check_window("recordings.structure.%s.%s" % (group, name),
                                  window, count, out)

    # Dedupe while preserving order: a STRUCTURAL fault is attributed to every
    # declared block (it must gate whichever block is armed), but the flattened
    # report list should not show the same string twice (round-2 NEW-2). Real
    # window mismatches cannot collide across blocks (labels are block-prefixed).
    mismatches = tuple(dict.fromkeys(m for b in blocks for m in per_block[b]))
    armed_mismatches = tuple(dict.fromkeys(m for b in armed for m in per_block[b]))
    if armed:
        status = STATUS_PASS if not armed_mismatches else STATUS_FAIL
    else:
        status = STATUS_REPORT
    return SaveStructureResult(status=status, gating=bool(armed),
                               mismatches=mismatches, observed=observed,
                               blocks=blocks, armed_blocks=armed,
                               armed_mismatches=armed_mismatches,
                               scenario_found=scenario_found)
