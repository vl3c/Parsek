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
  - the SUPPLY-ROUTE surface (``ROUTES`` / ``DORMANT_ROUTES`` and the two sparse
    candidate-intent sibling nodes), shapes pinned against
    ``RouteStore.SaveRoutesTo`` + ``RouteCodec.SerializeInto``.
  - the ``[expectations.rewind]`` / ``[expectations.recordings.structure]`` /
    ``[expectations.recordings.points]`` / ``[expectations.routes]`` evaluation
    (``evaluate_save_structure``) and their spec-surface validators
    (``validate_rewind_expectations`` / ``validate_structure_expectations`` /
    ``validate_points_expectations`` / ``validate_routes_expectations``, called
    from ``hlib.validate_spec``).

The third block, ``[expectations.recordings.points]``, closes GATE 12 ("AN
ORBITAL EVA RECORDS NOTHING"). ``recordings.count`` counts ``.prec`` FILES, and
two EMPTY recordings are still two files, so even an EXACT
``count = { min = 2, max = 2 }`` pin is STRUCTURALLY INCAPABLE of seeing a
recording that recorded nothing. (Stated precisely, because it is easy to
overclaim: no EVA-2 run is on record having passed with empty recordings. The
empty recordings were measured on ``2026-07-30_1532_S0.7-exit-auto-commit``,
which flew EVA-2's PROFILE and finished PARSEK-FAIL. The defect here is the
BLIND SPOT, not a specific green run that exploited it.) The points block
asserts over the per-recording
``pointCount`` the save itself carries, so "a recording exists" and "a recording
recorded something" become different claims. Reading it out of the SAVE (not the
``FinalizeTreeRecordings: ... points=N`` log line) is deliberate: that line is
``ParsekLog.Verbose`` and would silently stop being emitted on any spec that
does not pin ``verboseLogging = true``, turning the assertion into a no-op in
the fail-open direction.

VERDICT NEUTRALITY (binding, and the reason the verifier ships REPORT-ONLY):
S4.1-rewind-merge already DECLARES an ``[expectations.rewind]`` block, so a
naively-gating verifier would change a committed nightly's verdict without a
live run to prove the readings. Evaluation therefore defaults to REPORT-ONLY
(status ``REPORT``; mismatches recorded in the run JSON, verdict untouched)
unless the block itself opts in with ``gating = true`` - a key exactly ONE
committed spec declares (guarded by an allowlist test-suite sweep, mirroring
the ``[expectations.unityExceptions]`` precedent). Promotion to gating is an
operator decision taken after reading the report-only facets off a live run.
S4.1-rewind-merge was promoted 2026-07-31 through that workflow: reading run
``2026-07-31_1628`` measured supersedeRows 0 / tombstones 0 against its
declared ``max = 0`` windows, ``_1635`` then flew PASS armed, and a ``min = 1``
negative control reddened ``_1637`` PARSEK-FAIL(save-structure). CL-2 stage B
is the next candidate and is still unauthored.

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

Route serialization facts, pinned against ``RouteStore.SaveRoutesTo`` and
``RouteCodec.SerializeInto`` / ``RouteNodeCodec.SerializeEndpoint``:

  - ``ROUTES`` is a direct child of the ParsekScenario SCENARIO node
    (``ParsekScenario.OnSave`` -> ``savePhase = "routes"``), written ONLY when
    the committed store is non-empty, with one ``ROUTE`` child per route;
  - ``DORMANT_ROUTES`` is a SPARSE SIBLING carrying the same ``ROUTE`` children
    (rewind-visibility: routes created after the last rewind cutoff, invisible
    to every committed-route consumer until re-materialized), and
    ``DISMISSED_ROUTE_CANDIDATES`` / ``PROMPTED_ROUTE_CANDIDATES`` are two more
    sparse siblings carrying repeated ``treeId`` values;
  - ``status`` is a NAME string (``RouteStatus``); an ABSENT or unknown value
    loads back as ``Active`` (``RouteCodec.ParseStatusOrWarn``), so this parser
    defaults the same way rather than inventing an "unknown" bucket - the raw
    spelling is kept beside it (``RouteRow.status_raw``) and counted
    (``unknownStatuses``) so the mapping is visible rather than silent;
  - ``connectionKind`` on a ``STOP`` is a NAME string (``RouteConnectionKind``);
  - the endpoint keys are ``vesselPersistentId`` (SPARSE - omitted at pid 0, the
    KSC-origin shape), ``bodyName`` (sparse on empty), lat/lon/alt, ``isSurface``;
  - ESCROW IS NOT SERIALIZED. ``RouteStore``'s ``cargoEscrow`` is pure RAM
    (``SaveRoutesTo`` writes only the four nodes above and ``LoadRoutesFrom``
    clears only those), so there is deliberately NO escrow / reservation facet
    here - a save cannot carry that state and a window over it would assert
    over nothing.

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

# RouteStatus (Source/Parsek/Logistics/RouteStatus.cs) - NAME string on disk
# (`node.AddValue("status", route.Status.ToString())`). Ordinals are part of the
# save format and the enum's own comment forbids reordering, so this tuple is in
# ordinal order even though nothing here reads it positionally.
ROUTE_STATUS_NAMES: Tuple[str, ...] = (
    "Active", "InTransit", "WaitingForResources", "WaitingForFunds",
    "DestinationFull", "EndpointLost", "MissingSourceRecording",
    "SourceChanged", "Paused",
)
# `RouteCodec.ParseStatusOrWarn`: absent OR unknown -> Active. Mirrored here so a
# missing key reads the way the game reads it, not as a third state.
ROUTE_STATUS_DEFAULT = "Active"

# RouteConnectionKind (Source/Parsek/RouteProofMetadata.cs) - written as a NAME
# by `RouteCodec.SerializeStop`. In ORDINAL order: the numeric spelling the
# loader also accepts is an INDEX into this tuple, so it must not be reordered.
ROUTE_CONNECTION_KIND_NAMES: Tuple[str, ...] = (
    "None", "DockingPort", "Grapple", "StockCrossfeed", "Unknown",
)
ROUTE_CONNECTION_KIND_DEFAULT = "None"
ROUTE_CONNECTION_KIND_UNKNOWN = "Unknown"


def route_connection_kind_name(raw: Optional[str]) -> str:
    """Normalise a raw ``connectionKind`` the way the GAME loads it.

    Mirrors ``RouteNodeCodec.ParseConnectionKind``
    (Source/Parsek/Logistics/RouteNodeCodec.cs) branch for branch, because the
    facet must bucket what the loader will HOLD, not what the bytes spell -
    the same rule ``RouteRow.status`` follows for ``ParseStatusOrWarn``:

      1. null / empty -> ``None`` (the writer never omits the key, so this is a
         hand-mutated save);
      2. an integer that is a DEFINED ordinal -> that member. The C# tries the
         numeric spelling FIRST, so reading ``connectionKinds = {"1": 1}`` off a
         save the game reads as ``DockingPort`` would put a bucket in the facet
         that no whitelisted window could ever name;
      3. a DEFINED member name -> itself (the ordinary path, what
         ``SerializeStop`` writes);
      4. anything else -> ``Unknown``, a real enum member rather than a parse
         fault. Counted separately as ``unknownConnectionKinds`` so the collapse
         is visible instead of silent.

    An out-of-range integer takes branch 4, matching ``Enum.IsDefined`` failing
    the numeric parse and ``Enum.TryParse`` then failing on the same text.
    """
    if raw is None:
        return ROUTE_CONNECTION_KIND_DEFAULT
    text = raw.strip()
    if not text:
        return ROUTE_CONNECTION_KIND_DEFAULT
    try:
        ordinal = int(text)
    except ValueError:
        pass
    else:
        if 0 <= ordinal < len(ROUTE_CONNECTION_KIND_NAMES):
            return ROUTE_CONNECTION_KIND_NAMES[ordinal]
        return ROUTE_CONNECTION_KIND_UNKNOWN
    if text in ROUTE_CONNECTION_KIND_NAMES:
        return text
    return ROUTE_CONNECTION_KIND_UNKNOWN


def _is_recognised_connection_kind(raw: Optional[str]) -> bool:
    """True when ``raw`` names a RouteConnectionKind member OUTRIGHT - as a
    defined ordinal or a defined name - so the loader resolves it rather than
    collapsing it. An ABSENT key is recognised (it is the writer's own None
    default path), and so are the literal ``Unknown`` and its ordinal ``4``:
    what ``unknownConnectionKinds`` counts is a value that HAD to be
    collapsed, never one that legitimately reads as the Unknown member."""
    if raw is None:
        return True
    text = raw.strip()
    if not text:
        return True
    try:
        ordinal = int(text)
    except ValueError:
        return text in ROUTE_CONNECTION_KIND_NAMES
    return 0 <= ordinal < len(ROUTE_CONNECTION_KIND_NAMES)


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
    # RECORDED TRAJECTORY SIZE. `RecordingTreeRecordCodec` writes `pointCount`
    # UNCONDITIONALLY on every RECORDING node (`SaveRecordingInto` ->
    # `SaveRecordingResourceAndState` -> the private `SaveMutablePlaybackState`;
    # the write is `RecordingTreeRecordCodec.cs:344`) as
    # `rec.Points != null ? rec.Points.Count : 0` -- note a NULL list serialises
    # as a real 0, the one low reading this parser cannot distinguish from a
    # genuinely empty one
    # -- the same number the `FinalizeTreeRecordings: ... points=N` line reports,
    # but written by the SAVE path rather than a Verbose log line, so reading it
    # needs no `verboseLogging` pin and no `.prec` binary decode. It is
    # write-only on the C# side ("pointCount is informational -- Points list is
    # loaded from sidecar file"), which is why nothing but this parser reads it.
    # None = the key was ABSENT or unparseable; that must never collapse to 0
    # (see `observed_points_facets`, which counts it separately as `unparsed`).
    point_count: Optional[int] = None


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
class RouteEndpointRow:
    """One ``ORIGIN`` / ``STOP > ENDPOINT`` node. Both scalar keys are SPARSE on
    the writer side: ``vesselPersistentId`` is omitted at pid 0 (the KSC-origin
    shape) and ``bodyName`` is omitted when empty, so BOTH may legitimately be
    absent and neither absence is a fault."""

    vessel_persistent_id: Optional[str]  # kept as TEXT: it is an identity, not a quantity
    body_name: str                       # "" = the key was absent
    is_surface: bool


@dataclass(frozen=True)
class RouteStopRow:
    # NORMALISED through `route_connection_kind_name` (the loader's own rule:
    # absent -> None, a defined ordinal -> its member, a defined name -> itself,
    # anything else -> Unknown). `connection_kind_raw` keeps what the BYTES said
    # (None = the key was absent), the `RouteRow.status` / `status_raw` pairing.
    connection_kind: str
    connection_kind_raw: Optional[str]
    endpoint: Optional[RouteEndpointRow]  # None = no ENDPOINT child (hand-mutated save)


@dataclass(frozen=True)
class RouteRow:
    """One ``ROUTE`` node, committed or dormant.

    ``codec_reject`` is the reason ``RouteCodec.DeserializeFrom`` would return
    null for this node, or "" when it would load. It is the route analogue of
    the points block's ``unparsed``: a route the LOADER drops is a route the
    save has already lost, and it is structurally indistinguishable from "no
    route was ever created" once the game has read the file back."""

    route_id: str
    name: str
    # absent / unknown => Active, exactly as RouteCodec.ParseStatusOrWarn loads
    # it. `status_raw` keeps what the BYTES said (None = the key was absent), so
    # an unknown spelling - a save written by a NEWER Parsek than this parser
    # knows - is visible in triage instead of vanishing into the mapping.
    status: str
    status_raw: Optional[str]
    is_ksc_origin: bool
    completed_cycles: Optional[int]  # None = absent or unparseable, NEVER a real 0
    skipped_cycles: Optional[int]
    backing_mission_tree_id: Optional[str]
    dock_member_recording_id: Optional[str]
    last_hold_kind: Optional[str]   # sparse: absent = no hold recorded
    origin: Optional[RouteEndpointRow]
    stops: Tuple[RouteStopRow, ...]
    recording_ids: Tuple[str, ...]       # RECORDING_IDS > id
    source_recording_ids: Tuple[str, ...]  # SOURCE_REFS > SOURCE > recordingId
    excluded_intervals: Tuple[str, ...]
    dormant: bool
    codec_reject: str


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
    # Supply routes. `routes` is the COMMITTED list (the ROUTES node) and
    # `dormant_routes` the sparse DORMANT_ROUTES sibling; they are kept apart
    # because a dormant route cannot fire, bind a tree or render, so folding
    # them into one count would blur every claim a window makes.
    routes: Tuple[RouteRow, ...] = ()
    dormant_routes: Tuple[RouteRow, ...] = ()
    dismissed_candidate_tree_ids: Tuple[str, ...] = ()
    prompted_candidate_tree_ids: Tuple[str, ...] = ()

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
        point_count=_parse_int(node.value("pointCount")),
    )


def _parse_branch_point(node: SfsNode) -> BranchPointRow:
    return BranchPointRow(
        bp_id=node.value("id") or "",
        bp_type=_parse_int(node.value("type")),
        parent_ids=tuple(node.values_named("parentId")),
        child_ids=tuple(node.values_named("childId")),
        rewind_point_id=node.value("rewindPointId"),
    )


def _parse_route_endpoint(node: Optional[SfsNode]) -> Optional[RouteEndpointRow]:
    if node is None:
        return None
    return RouteEndpointRow(
        vessel_persistent_id=node.value("vesselPersistentId"),
        body_name=node.value("bodyName") or "",
        is_surface=_parse_bool(node.value("isSurface"), default=False))


def _classify_route_codec_reject(node: SfsNode) -> str:
    """The reason ``RouteCodec.DeserializeFrom`` would return null, or "".

    Mirrors the C# ORDER, not just the C# rules: the SOURCE_REFS walk runs
    BEFORE the STOP check there, so a node failing both reports the SOURCE
    reason exactly as the game's own Warn line would.
    """
    refs = node.first("SOURCE_REFS")
    if refs is not None:
        for i, src in enumerate(refs.nodes_named("SOURCE")):
            if not (src.value("recordingId") or "") or not (src.value("treeId") or ""):
                return "SOURCE child #%d is missing recordingId or treeId" % i
    if not node.nodes_named("STOP"):
        return "zero STOP children"
    return ""


def _parse_route(node: SfsNode, dormant: bool) -> RouteRow:
    refs = node.first("SOURCE_REFS")
    rids = node.first("RECORDING_IDS")
    excluded = node.first("EXCLUDED_INTERVALS")
    stops: List[RouteStopRow] = []
    for stop in node.nodes_named("STOP"):
        kind_raw = stop.value("connectionKind")
        stops.append(RouteStopRow(
            connection_kind=route_connection_kind_name(kind_raw),
            connection_kind_raw=kind_raw,
            endpoint=_parse_route_endpoint(stop.first("ENDPOINT"))))
    status_raw = node.value("status")
    return RouteRow(
        route_id=node.value("id") or "",
        name=node.value("name") or "",
        # Absent AND unknown both read Active, the way ParseStatusOrWarn does -
        # so the facet says what the GAME will hold, not what the bytes spell.
        status=(status_raw if status_raw in ROUTE_STATUS_NAMES
                else ROUTE_STATUS_DEFAULT),
        status_raw=status_raw,
        is_ksc_origin=_parse_bool(node.value("isKscOrigin"), default=False),
        completed_cycles=_parse_int(node.value("completedCycles")),
        skipped_cycles=_parse_int(node.value("skippedCycles")),
        backing_mission_tree_id=node.value("backingMissionTreeId"),
        dock_member_recording_id=node.value("dockMemberRecordingId"),
        last_hold_kind=node.value("lastHoldKind"),
        origin=_parse_route_endpoint(node.first("ORIGIN")),
        stops=tuple(stops),
        recording_ids=tuple(rids.values_named("id")) if rids is not None else (),
        source_recording_ids=tuple(
            s.value("recordingId") or ""
            for s in (refs.nodes_named("SOURCE") if refs is not None else ())),
        excluded_intervals=(tuple(excluded.values_named("excludedInterval"))
                            if excluded is not None else ()),
        dormant=dormant,
        codec_reject=_classify_route_codec_reject(node))


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

    # Supply routes. All four nodes are SPARSE (RouteStore.SaveRoutesTo writes
    # none of them on an empty store), so an absent node is the ordinary
    # "no routes / no candidate intent" save and never a fault.
    routes_parent = sc.first("ROUTES")
    routes = tuple(_parse_route(r, dormant=False)
                   for r in (routes_parent.nodes_named("ROUTE")
                             if routes_parent is not None else ()))
    dormant_parent = sc.first("DORMANT_ROUTES")
    dormant_routes = tuple(_parse_route(r, dormant=True)
                           for r in (dormant_parent.nodes_named("ROUTE")
                                     if dormant_parent is not None else ()))
    dismissed = sc.first("DISMISSED_ROUTE_CANDIDATES")
    prompted = sc.first("PROMPTED_ROUTE_CANDIDATES")

    return ParsekSaveSnapshot(
        parsed=True, error="", scenario_found=True,
        trees=trees, supersedes=tuple(supersedes), tombstones=tuple(tombstones),
        rewind_retirements=tuple(retirements), rewind_points=tuple(rps),
        routes=routes, dormant_routes=dormant_routes,
        dismissed_candidate_tree_ids=(tuple(dismissed.values_named("treeId"))
                                      if dismissed is not None else ()),
        prompted_candidate_tree_ids=(tuple(prompted.values_named("treeId"))
                                     if prompted is not None else ()))


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


# A recording that finalized with at most ONE point recorded no TRAJECTORY: one
# point is the boundary seed, not motion. This is the gate-12 defect signature
# verbatim (the 2026-07-30 measurement finalized its recordings at
# `points=1 orbitSegs=0 maxDist=0m`), which is why the threshold is a named
# constant rather than an inline 1 - it is a statement about the defect, not a
# tuning knob.
TRIVIAL_POINT_COUNT = 1


def observed_points_facets(snapshot: Optional[ParsekSaveSnapshot]) -> Dict[str, int]:
    """Summarise the per-recording ``pointCount`` distribution of a parsed save.

    Gate 12's defect class ("a recording is a FILE, and an EMPTY recording is
    still a file") needs a statistic that separates a tree which recorded motion
    from one which recorded a single sample per recording:

    - ``total``    - sum of ``pointCount`` over every RECORDING node.
    - ``largest``  - the biggest single recording's ``pointCount``.
    - ``smallest`` - the smallest single recording's ``pointCount``.
    - ``trivialRecordings`` - how many recordings finalized at
      ``<= TRIVIAL_POINT_COUNT`` points, i.e. recorded no trajectory at all.

    WHICH KEY TO PIN, and why the three order statistics are NOT enough on their
    own. ``largest`` / ``total`` / ``smallest`` are AGGREGATES, so they answer
    "did SOMETHING record" rather than "did everything that should have".
    ``largest = { min = 2 }`` passes a save where one recording is healthy and
    every sibling is empty - the PARTIAL recurrence of the very defect this
    block exists for (a 4-recording tree reading ``(208, 1, 1, 1)`` passes).
    ``total`` is weaker still: one long recording swamps any floor. That is what
    ``trivialRecordings`` is for - it is the only PER-RECORDING key here, and it
    is the one that closes the partial-recurrence hole. Pin it, and use the
    order statistics as corroboration rather than as the whole claim.

    A ``max`` on ``trivialRecordings`` is a BUDGET, not a floor of zero, because
    a healthy tree can legitimately contain trivial recordings - see below. Size
    it from a green run: EVA-2 measures exactly 1 (the pod), so
    ``trivialRecordings = { max = 1 }`` reds the moment a second recording goes
    empty while staying green on the healthy shape.

    CALIBRATION. Measured on ``2026-08-01_1626_EVA-2-orbital-board``: the EVA
    kerbal recorded 5 points over its ~10 s dwell while the pod recorded 1 (it
    lives 0.167 s, from EvaBoard to StopRecording, against a 0.200 s minimum
    sample interval), so ``largest = 5`` / ``smallest = 1`` /
    ``trivialRecordings = 1``. The BROKEN measurement
    (``2026-07-30_1532_S0.7-exit-auto-commit``, which flew EVA-2's PROFILE and
    finished PARSEK-FAIL) finalized BOTH recordings at 1 point:
    ``largest = 1`` / ``trivialRecordings = 2``. Note that ``smallest`` is 1 in
    BOTH, which is exactly why a per-recording FLOOR cannot be the discriminator
    and a per-recording BUDGET can.

    DEGENERATE CASES, both deliberate:

    - ZERO recordings => ``total``/``largest``/``smallest`` are all 0. A save
      that recorded nothing at all is the STRONGER form of the same defect, so
      it must red the same window rather than vanish into an absent facet.
    - ``unparsed`` counts RECORDING nodes whose ``pointCount`` was absent or
      unparseable. Those contribute NOTHING to the three statistics (a missing
      count must never read as a real 0 -- the same "never read a torn file as
      zero rows" rule the snapshot-level faults follow), and a non-zero
      ``unparsed`` is a DEFINED mismatch for a declared points block: the
      distribution is not fully measured, so no window over it can be trusted.
    - ``recordings`` is how many nodes DID contribute, so a reader can tell
      "smallest = 0 across 4 recordings" from "no recordings at all".

    THREE KNOWN LEGITIMATE LOWS. `pointCount` is the FLAT `rec.Points` list,
    which is NOT the same thing as "how much flight this recording covers". Each
    of these produces a low or zero count on a tree nobody would call broken, so
    each must be absorbed by a scenario's `trivialRecordings` budget rather than
    read as a defect, and together they are why `smallest` is the least safe key
    to pin. The general statement is "any recording that never bound a recorder,
    or whose coverage lives somewhere other than the flat list" - do NOT read
    the list below as exhaustive when arming a new scenario; size the budget
    from that scenario's own green run.

    1. PARENT-ANCHORED DEBRIS.
       `DebrisRelativeRecorderPolicy.TrimFlatPointsPastRenderableTail`
       deliberately EMPTIES the flat list on a parent-anchored debris recording
       with no renderable tail - its real coverage lives in
       `TrackSection.frames` / `bodyFixedFrames`, which this facet cannot see.
    2. TIME WARP. `FlightRecorder.OnPhysicsFrame` early-returns on `isOnRails`
       (Source/Parsek/FlightRecorder.cs:7713), so NO flat points are sampled
       while warping. On a warping scenario `pointCount` measures OFF-RAILS
       frame time, not flight coverage: a healthy multi-hour warped coast can
       carry a tiny flat count while its real coverage sits in OrbitSegments.
       Any scenario that warps (the B5/B6/B7/B11/B12/B15/B16 family) must size
       its windows from a green run of THAT scenario and must not inherit
       EVA-2's numbers, which come from a 10 s un-warped dwell.
    3. AN UNBOUND RE-FLY PROVISIONAL. Found in the field, NOT predicted from
       the writers: `logs/2026-07-31_1938_S4.1-rewind-merge`'s produced save
       carries `rec_cf071aac74...` with `pointCount = 0` and NEITHER `isDebris`
       NOR `parentAnchorRecordingId` - a Re-Fly provisional no recorder ever
       bound (the same run raises `outcome=unbound-refly-provisional`, the
       R1-EMPTY-PROVISIONAL observation). Whether that is healthy is an open
       product question, but the harness fact is settled: S4.1 flips between
       `recordings=3, smallest=3` and `recordings=4, smallest=0` across runs of
       one spec. So a zero is NOT exclusive to debris scenarios, and a rewind
       scenario arming this block needs a budget with slack for it.

    The section-authoritative sidecar path is NOT such a case - it changes what
    the `.prec` stores, while `rec.Points` stays populated in memory (the writer
    logs both numbers separately).

    A `None` / unparsed snapshot yields `{}` - ABSENT means "not measured",
    never zero - matching `observed_structure_facets`.
    """
    if snapshot is None or not snapshot.parsed:
        return {}
    # DEDUPE BY RECORDING ID FIRST. `snapshot.recordings` is the flattened union
    # of EVERY RECORDING_TREE, and production writes one tree TWICE: OnSave
    # serializes the committed trees and then `SaveActiveTreeIfAny` writes the
    # still-active tree again as a second `RECORDING_TREE isActive = True`.
    # Measured across the 149 real produced saves under logs/: 5 carry duplicate
    # recording ids, and `2026-07-28_1818_S4.1-rewind-merge` reads total 24
    # where the deduped truth is 12 - while `2026-07-28_1939`, the SAME
    # scenario, reads 12. The trigger is whether the tree is still active at
    # FlushAndQuit, so it varies run to run WITHIN one spec: a summed facet
    # would be a coin-flip 2x. `largest` / `smallest` are invariant to the
    # duplication, but `total`, `recordings` and `trivialRecordings` are not,
    # and `trivialRecordings` is the load-bearing key - a doubled trivial
    # recording would FALSE-RED a `max` budget sized on a green run. C#'s own
    # tally (ParsekScenario's `treeTotalPoints`) sums committed trees only and
    # does not double count; this matches it. First occurrence wins - the two
    # copies are one tree serialized twice in a single OnSave, so they agree.
    # Rows with an EMPTY id cannot be deduped and are all kept (a writer-contract
    # violation should not silently collapse rows); `observed_structure_facets`
    # surfaces `duplicateRecordingIds` beside this for triage.
    seen_ids = set()
    rows = []
    for r in snapshot.recordings:
        if r.recording_id:
            if r.recording_id in seen_ids:
                continue
            seen_ids.add(r.recording_id)
        rows.append(r)
    # A NEGATIVE count is as much "not a real count" as an absent key, so it
    # joins `unparsed` instead of silently deflating total/smallest (which is
    # the fail-open direction for an upper-bound window). Unreachable from the
    # writer - `rec.Points.Count` is a List<T>.Count - so this is the parser
    # honouring its own "never read a torn value as a real number" rule against
    # a hand-mutated or corrupted save.
    counts = [r.point_count for r in rows
              if r.point_count is not None and r.point_count >= 0]
    unparsed = sum(1 for r in rows
                   if r.point_count is None or r.point_count < 0)
    return {
        "total": sum(counts),
        "largest": max(counts) if counts else 0,
        "smallest": min(counts) if counts else 0,
        "trivialRecordings": sum(1 for c in counts if c <= TRIVIAL_POINT_COUNT),
        "recordings": len(counts),
        "unparsed": unparsed,
    }


def observed_routes_facets(snapshot: Optional[ParsekSaveSnapshot]) -> Dict[str, Any]:
    """Summarise the parsed SUPPLY-ROUTE surface of a save.

    EVERY AGGREGATE BELOW IS OVER THE COMMITTED LIST ONLY (the ``ROUTES`` node).
    A dormant route cannot dispatch, bind its tree or render - `RouteStore` hands
    `CommittedRoutes` to the orchestrator tick, `RouteTreeGuard`, the candidate
    finder and the Logistics window, and `DormantRoutes` to nobody - so summing
    the two would make every window mean "a route exists somewhere", which is the
    weaker claim in exactly the direction that matters. ``dormant`` is its own
    count beside them.

    - ``count``            - ``ROUTE`` children under ``ROUTES``.
    - ``dormant``          - ``ROUTE`` children under ``DORMANT_ROUTES``.
      A NODE COUNT, not the set the loader will hold: `LoadDormantRoutesFrom`
      runs after the committed list is filled and DROPS a dormant entry whose
      id collides with a committed route (Warn, committed wins), and the codec
      rejects below apply to a dormant node exactly as they do to a committed
      one. So `dormant` says what the SAVE carries; the number the game ends up
      with can be lower. `count` carries the same caveat through
      ``codecRejects``, which is why that counter is a defined mismatch.
    - ``stops``            - ``STOP`` children summed over the committed routes.
    - ``sourceRefs``       - ``SOURCE`` rows summed over the committed routes.
    - ``completedCycles`` / ``skippedCycles`` - the two dispatch counters summed.
    - ``statuses``         - ``RouteStatus`` NAME -> count.
    - ``connectionKinds``  - ``RouteConnectionKind`` NAME -> count, over STOPs,
      NORMALISED through ``route_connection_kind_name`` (the loader's own
      rule, which also accepts a numeric spelling), so a bucket is always a
      name a whitelisted window can pin.
    - ``originBodies`` / ``destinationBodies`` - ``bodyName`` -> count, from the
      ``ORIGIN`` node and from each ``STOP``'s ``ENDPOINT``.
    - ``ids``              - committed route ids, file order.
    - ``destinationVesselPids`` - every STOP endpoint ``vesselPersistentId``,
      file order, as TEXT (an identity, never arithmetic).
    - ``dismissedCandidates`` / ``promptedCandidates`` - the two sparse
      candidate-intent sibling nodes' ``treeId`` counts.
    - ``holdKinds`` - MEASURED-ONLY (no spec window): the sparse ``lastHoldKind``
      values, ``RouteDispatchEvaluator.EligibilityFailureKind`` NAME -> count,
      over the committed routes that recorded a hold. Recorded because the row
      parses the key and a parsed-then-discarded surface is a doc claim nothing
      backs; NOT an assertion key because its enum vocabulary is not pinned here
      and a lane wanting to gate a hold reason has the ``logContracts`` token
      the orchestrator prints.

    TWO FAULT COUNTERS, both DEFINED MISMATCHES for a declared block (the points
    block's ``unparsed`` precedent - a distribution that is not fully measured
    cannot back a window):

    - ``codecRejects`` - committed routes ``RouteCodec.DeserializeFrom`` would
      DROP on the next load (zero ``STOP`` children, or a ``SOURCE`` missing
      ``recordingId`` / ``treeId``). The save carries the node; the game will
      not. Note ``count`` counts NODES, so the count the game loads is
      ``count - codecRejects``.
    - ``unparsed`` - committed routes whose ``completedCycles`` /
      ``skippedCycles`` were absent or unreadable. The writer emits both
      unconditionally, so this fires only on a hand-mutated save - and a missing
      counter must never deflate a sum into a smaller real number.

    ``unknownStatuses`` and ``unknownConnectionKinds`` are two more counters and
    deliberately NOT mismatches:
    routes carrying a ``status`` spelling this parser does not know, which
    ``statuses`` has already bucketed under ``Active`` the way the game's own
    ``ParseStatusOrWarn`` will. ``RouteStatus`` is an APPEND-ONLY enum, so a
    non-zero here means the save was written by a newer Parsek than this
    harness - a version signal for triage, not a defect. Redding on it would
    fail closed on an additive change that broke nothing.
    ``unknownConnectionKinds`` is its exact sibling for ``connectionKind``:
    STOPs whose raw value the loader had to COLLAPSE to the ``Unknown``
    member (neither a defined ordinal nor a defined name), which
    ``connectionKinds`` has already bucketed under ``Unknown``. A literal
    ``Unknown`` and its ordinal ``4`` resolve outright and count as neither.

    WHICH KEY TO PIN. ``count`` is the anti-vacuity floor every route lane wants
    (a lane whose fixture route silently failed to load renders a dark map and
    otherwise looks green). ``statuses`` is the one that catches the failure the
    G1 lanes actually fear: a load-time optimizer merge moving a source
    recording's ``startUT`` flips the route to ``SourceChanged`` and the map goes
    dark, with ``count`` unmoved. Pin both; ``ids`` /
    ``destinationVesselPids`` add IDENTITY on top of shape, which is what stops
    a re-harvest swapping in a different route of the same topology.

    A ``None`` / unparsed snapshot yields ``{}`` - ABSENT means "not measured",
    never zero - matching ``observed_points_facets``.
    """
    if snapshot is None or not snapshot.parsed:
        return {}
    committed = snapshot.routes
    statuses: Dict[str, int] = {}
    kinds: Dict[str, int] = {}
    origin_bodies: Dict[str, int] = {}
    dest_bodies: Dict[str, int] = {}
    hold_kinds: Dict[str, int] = {}
    dest_pids: List[str] = []
    stops = 0
    source_refs = 0
    completed = 0
    skipped = 0
    unparsed = 0
    unknown_statuses = 0
    unknown_connection_kinds = 0
    for route in committed:
        statuses[route.status] = statuses.get(route.status, 0) + 1
        if route.status_raw is not None and route.status_raw not in ROUTE_STATUS_NAMES:
            unknown_statuses += 1
        # Sparse on the writer side: a never-held route writes no key at all, so
        # an absent value is "no hold recorded" and buckets nowhere.
        if route.last_hold_kind:
            hold_kinds[route.last_hold_kind] = hold_kinds.get(route.last_hold_kind, 0) + 1
        stops += len(route.stops)
        source_refs += len(route.source_recording_ids)
        if route.completed_cycles is None or route.skipped_cycles is None:
            unparsed += 1
        else:
            completed += route.completed_cycles
            skipped += route.skipped_cycles
        # A body name the writer omitted (sparse on empty) buckets NOWHERE
        # rather than under a "" key, so sum(originBodies) <= count and a pin
        # that assumes equality reds - the honest direction.
        if route.origin is not None and route.origin.body_name:
            origin_bodies[route.origin.body_name] = (
                origin_bodies.get(route.origin.body_name, 0) + 1)
        for stop in route.stops:
            kinds[stop.connection_kind] = kinds.get(stop.connection_kind, 0) + 1
            # A raw spelling the loader had to COLLAPSE to `Unknown`. An
            # absent key is not unknown (it is the None default), and the
            # literal "Unknown" or its ordinal "4" are DEFINED members that
            # resolve to themselves - only an unrecognised value counts.
            if not _is_recognised_connection_kind(stop.connection_kind_raw):
                unknown_connection_kinds += 1
            if stop.endpoint is None:
                continue
            if stop.endpoint.body_name:
                dest_bodies[stop.endpoint.body_name] = (
                    dest_bodies.get(stop.endpoint.body_name, 0) + 1)
            if stop.endpoint.vessel_persistent_id:
                dest_pids.append(stop.endpoint.vessel_persistent_id)
    return {
        "count": len(committed),
        "dormant": len(snapshot.dormant_routes),
        "stops": stops,
        "sourceRefs": source_refs,
        "completedCycles": completed,
        "skippedCycles": skipped,
        "codecRejects": sum(1 for r in committed if r.codec_reject),
        "unparsed": unparsed,
        "unknownStatuses": unknown_statuses,
        "unknownConnectionKinds": unknown_connection_kinds,
        "statuses": statuses,
        "connectionKinds": kinds,
        "originBodies": origin_bodies,
        "destinationBodies": dest_bodies,
        # Measured-only (no spec window) - see the docstring.
        "holdKinds": hold_kinds,
        "ids": [r.route_id for r in committed],
        "destinationVesselPids": dest_pids,
        "dismissedCandidates": len(snapshot.dismissed_candidate_tree_ids),
        "promptedCandidates": len(snapshot.prompted_candidate_tree_ids),
    }


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
            # Gate 12: the recorded-POINTS distribution, sibling of `structure`
            # and independently armable. Recorded UNCONDITIONALLY, which is how
            # a scenario sizes its first honest window off a green run.
            "points": observed_points_facets(snapshot),
        },
        # The SUPPLY-ROUTE surface. TOP-LEVEL beside `rewind`, not nested under
        # `recordings`, because that is where it lives in the save: ROUTES is a
        # direct child of the ParsekScenario node, a sibling of RECORDING_TREE
        # and REWIND_POINTS, and a route is not a property of a recording (it
        # merely NAMES recordings through SOURCE_REFS).
        "routes": observed_routes_facets(snapshot),
    }


# ---------------------------------------------------------------------------
# Spec-surface vocabulary + validation ([expectations.rewind] /
# [expectations.recordings.structure] / [expectations.recordings.points]).
# ---------------------------------------------------------------------------

GATING_KEY = "gating"

REWIND_BLOCK = "rewind"
REWIND_BLOCK_KEYS: Tuple[str, ...] = (
    GATING_KEY, "supersedeRows", "tombstones", "rewindPoints")

STRUCTURE_BLOCK = "structure"  # nested under [expectations.recordings]
STRUCTURE_BLOCK_KEYS: Tuple[str, ...] = (
    GATING_KEY, "trees", "committedTrees", "recordings",
    "terminalStates", "branchPoints")

# Gate 12. A SIBLING of `structure`, not a key inside it, and deliberately so:
# gating is PER-BLOCK (see SaveStructureResult / adversarial-review finding 3),
# so folding points into `structure` would mean a spec that had already armed
# `structure` auto-armed every points window the day one was added. A separate
# block keeps a NEW, unproven assertion class independently armable -- the same
# reason `rewind` and `structure` are two blocks rather than one.
POINTS_BLOCK = "points"  # nested under [expectations.recordings]
# `trivialRecordings` is the only PER-RECORDING key and the one that closes the
# partial-recurrence hole the three order statistics leave open (one healthy
# recording hides any number of empty siblings). See observed_points_facets.
POINTS_ASSERTION_KEYS: Tuple[str, ...] = (
    "total", "largest", "smallest", "trivialRecordings")
POINTS_BLOCK_KEYS: Tuple[str, ...] = (GATING_KEY,) + POINTS_ASSERTION_KEYS

# The SUPPLY-ROUTE block. TOP-LEVEL (`[expectations.routes]`), sibling of
# `[expectations.rewind]` - see observed_structure_facets for why it is not
# nested under `recordings`.
#
# THE NAME IS PLURAL, AND THAT IS DELIBERATE. `hlib.RESERVED_EXPECTATION_BLOCKS`
# still carries the SINGULAR `route` with zero declarers and no evaluator; this
# block owns the ROUTES NODE and is named after it. `hlib.validate_spec` WARNS on
# a spec that declares the singular near-miss, because a reserved block is
# recorded SKIPPED and gates nothing - the one way this pair can bite.
ROUTES_BLOCK = "routes"
# Count windows (bare int = exact pin, or { min =, max = }).
ROUTES_COUNT_KEYS: Tuple[str, ...] = (
    "count", "dormant", "stops", "sourceRefs", "completedCycles", "skippedCycles")
# { <Name> = <window> } tables. The first two are enum-name keyed and their names
# are whitelisted; the body tables are free-form (a body name is not an enum).
ROUTES_ENUM_GROUPS: Tuple[Tuple[str, Tuple[str, ...]], ...] = (
    ("statuses", ROUTE_STATUS_NAMES),
    ("connectionKinds", ROUTE_CONNECTION_KIND_NAMES),
)
ROUTES_BODY_GROUPS: Tuple[str, ...] = ("originBodies", "destinationBodies")
# Exact-SET assertions over a list of strings: identity on top of shape, so a
# re-harvest that swapped in a different route of the same topology reds.
ROUTES_SET_KEYS: Tuple[str, ...] = ("ids", "destinationVesselPids")
ROUTES_ASSERTION_KEYS: Tuple[str, ...] = (
    ROUTES_COUNT_KEYS + tuple(g for g, _ in ROUTES_ENUM_GROUPS)
    + ROUTES_BODY_GROUPS + ROUTES_SET_KEYS)
ROUTES_BLOCK_KEYS: Tuple[str, ...] = (GATING_KEY,) + ROUTES_ASSERTION_KEYS

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


def _validate_armed_unreddable(prefix: str, block: Dict,
                               assertion_keys: Tuple[str, ...]) -> List[str]:
    """An ARMED window whose only bound is ``min = 0`` can never fail.

    Third notch of the same rule: ``_validate_window`` refuses an EMPTY window,
    ``_validate_armed_empty`` refuses an armed block with no window, and this
    refuses an armed window that is present but unsatisfiable-as-a-gate. Every
    facet here is a count, so ``measured < 0`` is impossible and ``min = 0``
    asserts nothing; with no ``max`` beside it the window is decorative. That is
    exactly the shape a "this gate is flaky, loosen it" edit converges on, and
    it fails SILENTLY (a gate that cannot red looks identical to a gate that
    passes). Checked only when the block is ARMED - an unarmed block is a
    reading, and a reading of ``min = 0`` is merely uninformative, not a lie.

    Deliberately NOT generalised to "prove this window can red against the
    measured data": an absurd ``max = 999999`` is equally decorative but not
    mechanically distinguishable from a deliberately loose bound. This catches
    the one form that is decidable from the spec alone.
    """
    if block.get(GATING_KEY) is not True:
        return []
    errs: List[str] = []
    for key in assertion_keys:
        win = block.get(key)
        if not isinstance(win, dict):
            continue
        if win.get("min") == 0 and not isinstance(win.get("min"), bool) and "max" not in win:
            errs.append(
                "%s.%s: an ARMED { min = 0 } window can never red (counts are "
                "never negative) - give it a max, raise the min, or drop the key"
                % (prefix, key))
    return errs


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
    errs.extend(_validate_armed_unreddable(
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
    errs.extend(_validate_armed_unreddable(
        "expectations.recordings.structure", block,
        ("trees", "committedTrees", "recordings")))
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


def validate_points_expectations(block: Any) -> List[str]:
    """Validate the ``[expectations.recordings.points]`` spec surface (gate 12).
    None => no block declared => valid."""
    if block is None:
        return []
    if not isinstance(block, dict):
        return ["expectations.recordings.points: must be a table"]
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in POINTS_BLOCK_KEYS)
    if unknown:
        errs.append("expectations.recordings.points: unknown key(s) %s (accepted: %s)"
                    % (unknown, list(POINTS_BLOCK_KEYS)))
    errs.extend(_validate_gating("expectations.recordings.points", block))
    errs.extend(_validate_armed_empty(
        "expectations.recordings.points", block, POINTS_ASSERTION_KEYS))
    errs.extend(_validate_armed_unreddable(
        "expectations.recordings.points", block, POINTS_ASSERTION_KEYS))
    for key in POINTS_ASSERTION_KEYS:
        if key in block:
            errs.extend(_validate_window(
                "expectations.recordings.points.%s" % key, block[key]))
    return errs


def _validate_string_set(prefix: str, val: Any) -> List[str]:
    """A set assertion is a list of NON-EMPTY strings. An empty list is legal and
    means "the save carries none" - unlike an empty count window, that is a real
    claim, so it is not refused here."""
    if not isinstance(val, list):
        return ["%s: %r must be a list of strings" % (prefix, val)]
    errs: List[str] = []
    for i, item in enumerate(val):
        if not isinstance(item, str) or not item.strip():
            errs.append("%s[%d]: %r must be a non-empty string" % (prefix, i, item))
    return errs


def validate_routes_expectations(block: Any) -> List[str]:
    """Validate the ``[expectations.routes]`` spec surface (pre-launch, pure).
    None => no block declared => valid."""
    if block is None:
        return []
    if not isinstance(block, dict):
        return ["expectations.routes: must be a table"]
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in ROUTES_BLOCK_KEYS)
    if unknown:
        errs.append("expectations.routes: unknown key(s) %s (accepted: %s)"
                    % (unknown, list(ROUTES_BLOCK_KEYS)))
    errs.extend(_validate_gating("expectations.routes", block))
    errs.extend(_validate_armed_empty("expectations.routes", block,
                                      ROUTES_ASSERTION_KEYS))
    # Only the COUNT keys can take the unreddable `{ min = 0 }` shape; a group
    # table's members are checked one level down by the same helper below.
    errs.extend(_validate_armed_unreddable("expectations.routes", block,
                                           ROUTES_COUNT_KEYS))
    for key in ROUTES_COUNT_KEYS:
        if key in block:
            errs.extend(_validate_window("expectations.routes.%s" % key, block[key]))
    for key, names in ROUTES_ENUM_GROUPS:
        if key not in block:
            continue
        sub = block[key]
        prefix = "expectations.routes.%s" % key
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
    for key in ROUTES_BODY_GROUPS:
        if key not in block:
            continue
        sub = block[key]
        prefix = "expectations.routes.%s" % key
        if not isinstance(sub, dict):
            errs.append("%s: must be a table of { <bodyName> = <window> }" % prefix)
            continue
        for name, window in sub.items():
            if not name.strip():
                errs.append("%s: empty body name" % prefix)
                continue
            errs.extend(_validate_window("%s.%s" % (prefix, name), window))
    for key in ROUTES_SET_KEYS:
        if key in block:
            errs.extend(_validate_string_set("expectations.routes.%s" % key, block[key]))
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
    points = (recordings.get(POINTS_BLOCK)
              if isinstance(recordings, dict) else None)
    if isinstance(points, dict) and not any(k in points for k in POINTS_ASSERTION_KEYS):
        warns.append("expectations.recordings.points: declared with no assertion "
                     "key - the block reports nothing and gates nothing")
    routes = expectations.get(ROUTES_BLOCK)
    if isinstance(routes, dict) and not any(k in routes for k in ROUTES_ASSERTION_KEYS):
        warns.append("expectations.routes: declared with no assertion key - the "
                     "block reports nothing and gates nothing")
    return warns


def reserved_route_block_warnings(expectations: Optional[Dict]) -> List[str]:
    """WARN on the SINGULAR ``[expectations.route]`` near-miss.

    ``route`` is in ``hlib.RESERVED_EXPECTATION_BLOCKS``: it has no evaluator, so
    it is parsed and recorded SKIPPED. That was harmless while nothing near it
    existed; now that ``[expectations.routes]`` evaluates, a one-character slip
    is a block the author believes gates and that cannot. A WARNING rather than
    an error because the reserved name is still legal and a spec may declare it
    deliberately (that is what reserved means)."""
    expectations = expectations or {}
    if isinstance(expectations.get("route"), dict):
        return ["expectations.route: RESERVED (no evaluator - it is recorded "
                "SKIPPED and gates nothing). The save-parse SUPPLY-ROUTE block "
                "is [expectations.routes], plural."]
    return []


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
    # "rewind" / "recordings.structure" / "recordings.points" / "routes"
    blocks: Tuple[str, ...]
    armed_blocks: Tuple[str, ...]     # the gating = true subset of blocks
    armed_mismatches: Tuple[str, ...]  # the verdict-driving subset
    scenario_found: Optional[bool]    # None = save unreadable / not read


def _recordings_sub_block(expectations: Dict, name: str) -> Optional[Dict]:
    recordings = expectations.get("recordings")
    sub = recordings.get(name) if isinstance(recordings, dict) else None
    return sub if isinstance(sub, dict) else None


def _structure_block(expectations: Dict) -> Optional[Dict]:
    return _recordings_sub_block(expectations, STRUCTURE_BLOCK)


def _points_block(expectations: Dict) -> Optional[Dict]:
    return _recordings_sub_block(expectations, POINTS_BLOCK)


def declared_structure_blocks(expectations: Optional[Dict]) -> Tuple[str, ...]:
    """Which of the four M-C2 save-parse blocks the spec declares, stable order."""
    expectations = expectations or {}
    out: List[str] = []
    if isinstance(expectations.get(REWIND_BLOCK), dict):
        out.append(REWIND_BLOCK)
    if _structure_block(expectations) is not None:
        out.append("recordings.structure")
    if _points_block(expectations) is not None:
        out.append("recordings.points")
    if isinstance(expectations.get(ROUTES_BLOCK), dict):
        out.append(ROUTES_BLOCK)
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
    points = _points_block(expectations)
    if points is not None and points.get(GATING_KEY) is True:
        out.append("recordings.points")
    routes = expectations.get(ROUTES_BLOCK)
    if isinstance(routes, dict) and routes.get(GATING_KEY) is True:
        out.append(ROUTES_BLOCK)
    return tuple(out)


def gating_armed(expectations: Optional[Dict]) -> bool:
    """True iff ANY declared M-C2 block carries ``gating = true``. Exactly one
    committed spec does - S4.1-rewind-merge, armed 2026-07-31 (guarded by an
    allowlist test-suite sweep); arming is an operator decision taken after
    reading report-only facets off green runs."""
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
    """Evaluate the declared M-C2 blocks (``rewind`` / ``recordings.structure``
    / ``recordings.points`` / ``routes``) against the parsed save snapshot.

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
        points_spec = _points_block(expectations)
        if points_spec is not None:
            out = per_block["recordings.points"]
            measured = facets["recordings"]["points"]
            # A RECORDING node with no readable `pointCount` is a DEFINED
            # mismatch, not a silent zero: the distribution the windows below
            # are read against is incomplete, so every one of them would be
            # asserting over a number the save did not actually supply. The
            # C# writer emits the key unconditionally, so this fires only on a
            # hand-mutated save or a fixture whose RECORDING nodes predate it.
            if measured["unparsed"]:
                out.append(
                    "recordings.points: %d recording(s) carry no readable "
                    "pointCount - the distribution is not fully measured"
                    % measured["unparsed"])
            for key in POINTS_ASSERTION_KEYS:
                if key in points_spec:
                    _check_window("recordings.points.%s" % key,
                                  points_spec[key], measured[key], out)
        routes_spec = expectations.get(ROUTES_BLOCK)
        if isinstance(routes_spec, dict):
            out = per_block[ROUTES_BLOCK]
            measured = facets["routes"]
            # DEFINED mismatches, the points-block `unparsed` precedent: a route
            # the LOADER would drop is a route the save has already lost, and an
            # unreadable dispatch counter deflates a sum into a smaller real
            # number. Both fire whether or not any window below is declared.
            if measured["codecRejects"]:
                out.append(
                    "routes: %d committed route(s) would be DROPPED by "
                    "RouteCodec.DeserializeFrom on the next load"
                    % measured["codecRejects"])
            if measured["unparsed"]:
                out.append(
                    "routes: %d committed route(s) carry no readable "
                    "completedCycles/skippedCycles - the sums are not fully "
                    "measured" % measured["unparsed"])
            for key in ROUTES_COUNT_KEYS:
                if key in routes_spec:
                    _check_window("routes.%s" % key, routes_spec[key],
                                  measured[key], out)
            for group in ([g for g, _ in ROUTES_ENUM_GROUPS]
                          + list(ROUTES_BODY_GROUPS)):
                sub = routes_spec.get(group)
                if not isinstance(sub, dict):
                    continue
                for name, window in sub.items():
                    _check_window("routes.%s.%s" % (group, name), window,
                                  measured[group].get(name, 0), out)
            for key in ROUTES_SET_KEYS:
                want = routes_spec.get(key)
                if not isinstance(want, list):
                    continue
                # SET equality, not sequence equality: file order is a writer
                # detail (the store's list order), while membership is the
                # identity claim. Duplicates collapse on both sides.
                got_set = sorted(set(measured[key]))
                want_set = sorted({w for w in want if isinstance(w, str)})
                if got_set != want_set:
                    out.append("routes.%s %s != %s" % (key, got_set, want_set))

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
