#!/usr/bin/env python3
"""Split ParsekFlightGhostPositioner.cs into partial-class files by responsibility.

A pure file move: every class-level member (method, expression-bodied property or nested
type) is copied verbatim, with its leading comments, doc comments and attributes, into
``ParsekFlightGhostPositioner.<Group>.cs`` beside the main file; nothing inside a member is
edited. The one edit outside the members is the class declaration line, which gains
``partial`` (``internal sealed class ParsekFlightGhostPositioner : IGhostPositioner`` ->
``internal sealed partial class ParsekFlightGhostPositioner : IGhostPositioner``); a new
file declares ``internal sealed partial class ParsekFlightGhostPositioner`` (the base list
lives on the main part).

The grouping is the multiplayer plan thread's (positioner-split-groups.md): the main file
keeps the class comment, ``host``, the constructor and EVERY field, constant and static
readonly delegate in today's order (C# runs static field initializers of a partial class in
an undefined order across its files, so no state may move), plus the thin IGhostPositioner
entry points, the body lookups and the engine distance queries. Only methods, nested types
and expression-bodied (stateless) properties may move; the script refuses a mapping that
moves anything else. Within a file the members keep their relative order.

Members are mapped by NAME. A bare name maps every overload of that name; ``Name@k`` maps
only the k-th member of that name in file order (1-based), for the one name whose overloads
sit in different groups (``InterpolateAndPosition``). A name is mapped either bare or with
every occurrence numbered, never both. Every movable member must be mapped by a group or by
MAIN_MEMBERS (an unknown new member fails the run, so a rerun on a newer tree never
silently misfiles code), and a mapped name or occurrence that no longer exists fails the
run, so a stale mapping is noticed.

Usings: each output carries only the usings of the original that its own text needs, found
by the identifiers each namespace contributes (USING_TYPES, matched on the code text: comments
and literal text removed, interpolation holes kept). A using can only be dropped, never added, so a missed identifier
shows up as a compile error and an extra one as a using the build proves unneeded; no
binding can change, because a type in the enclosing namespace wins over any using.

Before writing anything the script verifies that the multiset of non-blank lines across the
outputs equals the original's (declaration made partial, using lines aside) plus only the
new files' namespace / class wrappers, that every output's usings are a subset of the
original's, that every output re-parses with balanced braces, that the member count is
preserved, and that no field-like member landed outside the main file. Any mismatch fails
loudly and nothing is written. A second run over its own output is refused (the partial
files exist, and the main declaration is already partial).

Re-running on a newer tree (for example after a merge conflict on the split branch):

    python3 scripts/refactor/split_ghostpositioner.py --remove-outputs
    git checkout <pre-split commit> -- Source/Parsek/Controllers/ParsekFlightGhostPositioner.cs
    python3 scripts/refactor/split_ghostpositioner.py

``--list`` prints every class-level member with its kind, line range and target file and
writes nothing; ``--check`` runs the split and verification without writing.

Plain stdlib, self-contained (no shared module with the other split scripts).
"""

import argparse
import bisect
import collections
import os
import re
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_SRC = os.path.join(REPO_ROOT, "Source", "Parsek", "Controllers", "ParsekFlightGhostPositioner.cs")
CLASS_NAME = "ParsekFlightGhostPositioner"
INPUT_DECL = "    internal sealed class ParsekFlightGhostPositioner : IGhostPositioner"
CLASS_DECL = "    internal sealed partial class ParsekFlightGhostPositioner : IGhostPositioner"
PART_DECL = "    internal sealed partial class ParsekFlightGhostPositioner"

AREAS = collections.OrderedDict([
    ("Points", (
        "Point interpolation core, the spline gates, and point and surface placement.",
        [
            "InterpolateAndPosition@2", "ComputePointHermiteMaxDeviationMeters",
            "allowAnchorCorrectionInterval", "allowSplinePositioningForPlayback",
            "TryComputeLateUpdateSplineWorldPosition", "PositionGhostAt", "PositionGhostAtSurface",
            "IsRelativeSectionAtUT", "TryDispatchFlatPointEndpoints",
        ])),
    ("Orbit", (
        "Orbit placement and every reader of the orbit cache.",
        [
            "PositionFromOrbit", "SetOrbitTailInterpolated", "InterpolateAndPosition@3",
            "ClearOrbitPlaybackCaches", "PositionGhostFromOrbit", "ResolveOrbitContinuityOffset",
            "FormatFiniteOrNa", "SafeOrbitalLookRotation", "PositionGhostFromOrbitOnly",
            "TryGetOrbitForSegment", "TryResolveOrbitTailWorldPosition", "TryResolveOrbitWorldPosition",
        ])),
    ("OrbitTailContinuity", (
        "The predicted orbit tail continuity offset.",
        [
            "TryResolvePredictedOrbitTailContinuityOffset", "BuildPredictedOrbitTailContinuityCacheKey",
            "ShouldApplyPredictedOrbitTailContinuity",
            "TryResolvePredictedOrbitTailContinuityAnchorWorldPosition",
            "FindNearestTrackSectionIndexForContinuityAnchor",
        ])),
    ("Checkpoint", (
        "OrbitalCheckpoint section placement.",
        [
            "TryInterpolateAndPositionCheckpointSection",
            "TryInterpolateAndPositionCheckpointSectionWithOrbitRotation", "TryFindCheckpointOrbitSegment",
            "TryResolveCheckpointPointInterpolation", "BuildCheckpointOrbitCacheKey",
            "TryGetCheckpointTrackSection", "LogCheckpointPointPlayback",
        ])),
    ("WorldPosition", (
        "TryResolvePlaybackWorldPosition and its resolvers (no placement, no host state).",
        [
            "FormatStandaloneCoverageRange", "LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed",
            "TryResolvePlaybackWorldPosition", "TryResolveInterpolatedWorldPosition",
            "TryFindAdjacentAbsolutePlaybackFrame", "GetAbsolutePlaybackFrameList",
            "IsEligibleBoundaryBridgeFrame", "TryResolvePointWorldPosition", "TryResolveTrajectoryPointWorldPosition",
            "TryResolveRelativeWorldPosition", "TryResolveRelativeOffsetWorldPosition",
            "TryResolveSurfaceWorldPosition",
        ])),
    ("RecordedRelative", (
        "Non-loop Relative section placement.",
        [
            "InterpolateAndPositionRelative", "WithRelativeSectionSurfaceVelocity", "ShortRecordingId",
            "BuildRecordedRelativeOffsetAppliedLog", "InterpolateAndPositionRecordedRelative",
            "RecordedRelativeOffset", "LogRecordedRelativeOffsetSpikeCorrection",
            "TryPositionGhostRecordedRelativeAt", "TryUseRelativeBodyFixedPrimaryFallback",
            "RetireUnresolvedRecordedRelative",
        ])),
    ("RelativeAnchor", (
        "Recorded relative anchor pose resolution.",
        [
            "TryResolveRecordedRelativeAnchorPose", "TryFindRelativeAnchorFocusTree",
        ])),
    ("BodyFixedPrimary", (
        "Body-fixed primary placement for parent-anchored recordings.",
        [
            "TryPositionFromBodyFixedPrimary", "ResolveBodyFixedPrimaryBracketUTs",
        ])),
    ("Trace", (
        "The debris body-fixed trace and the attitude-for-trace family.",
        [
            "TraceDebrisBodyFixedPrimaryPlayback", "FindTraceBracketFrames", "FormatTraceBracketFrame",
            "ResolveRecordedAnchorLocalDistanceBefore", "FindLastBodyFixedFrameAtOrBefore",
            "FormatTraceDistance", "TryResolveRecordedAttitudeForTrace",
            "TryResolveRecordedAttitudeForTraceCore", "TryResolveOrbitAttitudeForTrace",
            "TryResolveSurfaceAttitudeForTrace", "TryResolveRelativeAttitudeForTrace",
        ])),
    ("Loop", (
        "Loop placement, loop-relative placement, and the loop seam and re-aim traces.",
        [
            "TrackLoopSeamTeleport", "LogReaimGhostTrace", "PositionLoop", "ShiftLoopSeamTracesAfterDelete",
            "ShiftLoopSeamTracesAfterInsert", "CountKeysAbove", "ReaimLastGhostFrameForTesting",
            "LoopSeamLastGhostFrameForTesting", "TryResolveLoopLiveAnchorPose", "PositionLoopGhost",
            "InterpolateAndPositionLoopRelative", "PositionLoopGhostRelativeAt",
        ])),
    ("TailLift", (
        "The tail-lift plan and its recording lookup.",
        [
            "ResolveRecordingById", "ResolveRecordingForTailLift", "ResolveTailLiftPlan",
            "CacheInactiveTailLiftPlanIfStable", "IsTransientTailLiftInactiveReason",
            "ResolveTailLiftTerminalBodyName", "ResolveTailLiftTerminalBody", "TailLiftBodyMatches",
            "TailLiftTerminalPointIsAbsolute", "ResolveTailLiftInactiveReason",
            "ResolveTailLiftInactiveReasonCore", "LogTailLiftActive", "LogTailLiftInactive",
        ])),
])

# Movable members that stay in the main file on purpose: the thin IGhostPositioner entry
# points, the body lookups and the engine distance queries. Listed so that "unmapped"
# reports only members a newer tree added.
MAIN_MEMBERS = [
    "InterpolateAndPosition@1", "InterpolateAndPositionEntryCore", "PositionAtPoint",
    "TryResolveExplosionAnchorPosition", "ResolveImmediateLandedGhostClearanceMeters",
    "PositionAtSurface", "ApplyZoneRendering", "FindFirstMatching", "FindBodyByName",
    "FindBodyByNameOrNull", "FindLiveBodyByBodyNameOrNull", "FindBodyByNameNoAlloc",
    "ResolvePlaybackDistanceForEngine", "ResolveCameraAnchorWorldPosition",
    "ResolvePlaybackActiveVesselDistanceForEngine", "ResolvePlaybackDistanceFromReferencePosition",
    "ResolveUnresolvedPlaybackDistanceFallback",
]

# The identifiers each using of the original file contributes to this class. A file keeps a
# using when its masked text names one of them as a whole word. Names two of these
# namespaces share (Object, Random) are absent on purpose: the original could not have used
# them unqualified.
USING_TYPES = collections.OrderedDict([
    ("using System;", [
        "Math", "Func", "Action", "Predicate", "Comparison", "StringComparer", "StringComparison",
        "String", "Double", "Single", "Int32", "Int64", "UInt32", "UInt64", "Boolean", "Byte", "Char",
        "Exception", "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
        "InvalidOperationException", "NotSupportedException", "NotImplementedException",
        "IndexOutOfRangeException", "NullReferenceException", "FormatException", "OverflowException",
        "Guid", "Tuple", "ValueTuple", "Array", "Convert", "BitConverter", "Nullable", "TimeSpan",
        "DateTime", "Type", "IComparable", "IEquatable", "IDisposable", "Enum", "Lazy", "GC",
        "Environment", "Buffer", "MidpointRounding", "StringSplitOptions", "Attribute", "Delegate",
        "EventHandler", "Obsolete", "ObsoleteAttribute", "Flags", "FlagsAttribute", "Serializable",
        "NonSerialized", "IntPtr", "Decimal",
    ]),
    ("using System.Collections.Generic;", [
        "List", "Dictionary", "HashSet", "IList", "IReadOnlyList", "IReadOnlyCollection",
        "ICollection", "IEnumerable", "IEnumerator", "KeyValuePair", "Queue", "Stack", "SortedList",
        "SortedDictionary", "SortedSet", "LinkedList", "EqualityComparer", "Comparer", "IComparer",
        "IEqualityComparer", "KeyNotFoundException", "IDictionary", "IReadOnlyDictionary",
    ]),
    ("using System.Globalization;", [
        "CultureInfo", "NumberStyles", "NumberFormatInfo",
    ]),
    ("using UnityEngine;", [
        "Vector2", "Vector3", "Vector4", "Quaternion", "Mathf", "Debug", "Time", "Transform",
        "GameObject", "Color", "Matrix4x4", "Rigidbody", "Renderer", "Camera", "MonoBehaviour",
        "Component", "Physics", "RaycastHit", "Ray", "Bounds", "LayerMask", "Collider", "Application",
        "Screen", "Input", "Rect", "GUI", "GUILayout", "Light", "ParticleSystem", "LineRenderer",
        "MeshRenderer", "Material", "Shader", "Mesh", "Texture2D", "AnimationCurve", "Keyframe",
        "WaitForSeconds", "Coroutine", "Space", "HideFlags", "SerializeField", "QueryTriggerInteraction",
    ]),
])

MOVABLE_KINDS = ("method", "type", "exprproperty")
REGION_RE = re.compile(r"^\s*#(region|endregion)\b")
OCCURRENCE_RE = re.compile(r"^(\w+)@([1-9][0-9]*)$")


class SplitError(Exception):
    pass


# ---------------------------------------------------------------------------
# Lexer: walks C# text, skipping comments and string / char literals, and reports
# the offsets where a class-level member ends.
# ---------------------------------------------------------------------------

def _skip_line_comment(text, i):
    j = text.find("\n", i)
    return len(text) if j < 0 else j


def _skip_block_comment(text, i):
    j = text.find("*/", i + 2)
    if j < 0:
        raise SplitError("unterminated block comment at offset %d" % i)
    return j + 2


def _skip_char(text, i):
    # text[i] == "'"
    j = i + 1
    while j < len(text):
        c = text[j]
        if c == "\\":
            j += 2
            continue
        if c == "'":
            return j + 1
        if c == "\n":
            raise SplitError("unterminated char literal at offset %d" % i)
        j += 1
    raise SplitError("unterminated char literal at offset %d" % i)


def _skip_string(text, i, verbatim, interpolated):
    """i is the offset of the opening quote. Returns the offset after the closing quote."""
    j = i + 1
    while j < len(text):
        c = text[j]
        if verbatim:
            if c == '"':
                if j + 1 < len(text) and text[j + 1] == '"':
                    j += 2
                    continue
                return j + 1
        else:
            if c == "\\":
                j += 2
                continue
            if c == '"':
                return j + 1
            if c == "\n":
                raise SplitError("unterminated string literal at offset %d" % i)
        if interpolated and c == "{":
            if j + 1 < len(text) and text[j + 1] == "{":
                j += 2
                continue
            j = _skip_code(text, j + 1, stop_at_close_brace=True)
            continue
        if interpolated and c == "}":
            if j + 1 < len(text) and text[j + 1] == "}":
                j += 2
                continue
        j += 1
    raise SplitError("unterminated string literal at offset %d" % i)


def _literal_start(text, i):
    """If a string literal starts at i, return (quote_offset, verbatim, interpolated)."""
    c = text[i]
    if c == '"':
        if text.startswith('"""', i):
            raise SplitError("raw string literal at offset %d is not supported" % i)
        return i, False, False
    if c in "@$":
        k = i
        verbatim = interpolated = False
        while k < len(text) and text[k] in "@$" and k - i < 3:
            if text[k] == "@":
                verbatim = True
            else:
                interpolated = True
            k += 1
        if k < len(text) and text[k] == '"' and (verbatim or interpolated):
            if text.startswith('"""', k):
                raise SplitError("raw string literal at offset %d is not supported" % k)
            return k, verbatim, interpolated
    return None


def _skip_code(text, i, stop_at_close_brace):
    """Skip code inside an interpolation hole; returns the offset after its closing brace."""
    depth = 0
    while i < len(text):
        c = text[i]
        if c == "/" and text.startswith("//", i):
            i = _skip_line_comment(text, i)
            continue
        if c == "/" and text.startswith("/*", i):
            i = _skip_block_comment(text, i)
            continue
        if c == "'":
            i = _skip_char(text, i)
            continue
        lit = _literal_start(text, i)
        if lit is not None:
            i = _skip_string(text, lit[0], lit[1], lit[2])
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            if depth == 0 and stop_at_close_brace:
                return i + 1
            depth -= 1
        i += 1
    raise SplitError("unterminated interpolation hole")


def _next_significant(text, i):
    while i < len(text):
        c = text[i]
        if c.isspace():
            i += 1
            continue
        if text.startswith("//", i):
            i = _skip_line_comment(text, i)
            continue
        if text.startswith("/*", i):
            i = _skip_block_comment(text, i)
            continue
        return c
    return ""


def member_end_offsets(body):
    """Offsets (exclusive) at which each class-level member of ``body`` ends."""
    ends = []
    brace = paren = bracket = 0
    i = 0
    n = len(body)
    while i < n:
        c = body[i]
        if c == "/" and body.startswith("//", i):
            i = _skip_line_comment(body, i)
            continue
        if c == "/" and body.startswith("/*", i):
            i = _skip_block_comment(body, i)
            continue
        if c == "'":
            i = _skip_char(body, i)
            continue
        lit = _literal_start(body, i)
        if lit is not None:
            i = _skip_string(body, lit[0], lit[1], lit[2])
            continue
        if c == "#" and (i == 0 or body[i - 1] == "\n" or body[:i].split("\n")[-1].strip() == ""):
            raise SplitError("preprocessor directive at class level is not supported (offset %d)" % i)
        if c == "{":
            brace += 1
        elif c == "}":
            brace -= 1
            if brace < 0:
                raise SplitError("unbalanced '}' at offset %d" % i)
            if brace == 0 and paren == 0 and bracket == 0:
                nxt = _next_significant(body, i + 1)
                if nxt not in ("=", ";", ".", ",", ")"):
                    ends.append(i + 1)
        elif c == "(":
            paren += 1
        elif c == ")":
            paren -= 1
        elif c == "[":
            bracket += 1
        elif c == "]":
            bracket -= 1
        elif c == ";" and brace == 0 and paren == 0 and bracket == 0:
            ends.append(i + 1)
        i += 1
    if brace or paren or bracket:
        raise SplitError("unbalanced class body (brace=%d paren=%d bracket=%d)" % (brace, paren, bracket))
    return ends


def mask_comments_and_literals(text):
    """Comments removed, string / char literal contents replaced by placeholders."""
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "/" and text.startswith("//", i):
            i = _skip_line_comment(text, i)
            continue
        if c == "/" and text.startswith("/*", i):
            i = _skip_block_comment(text, i)
            out.append(" ")
            continue
        if c == "'":
            i = _skip_char(text, i)
            out.append("'c'")
            continue
        lit = _literal_start(text, i)
        if lit is not None:
            i = _skip_string(text, lit[0], lit[1], lit[2])
            out.append('"s"')
            continue
        out.append(c)
        i += 1
    return "".join(out)



# ---------------------------------------------------------------------------
# Member model
# ---------------------------------------------------------------------------

Member = collections.namedtuple("Member", "name kind first_line last_line lines")
# kind: "method" | "type" | "const" | "field" | "property" | "exprproperty" | "constructor"
#       | "region" | "endregion" (a directive line plus the blank lines before it)

_TYPE_RE = re.compile(r"\b(class|struct|enum|interface|record)\s+(\w+)")
_IDENT_BEFORE_RE = re.compile(r"(\w+)\s*(?:<[^<>]*>)?\s*$")


def _strip_leading_attributes(decl):
    decl = decl.lstrip()
    while decl.startswith("["):
        depth = 0
        for k, ch in enumerate(decl):
            if ch == "[":
                depth += 1
            elif ch == "]":
                depth -= 1
                if depth == 0:
                    decl = decl[k + 1:].lstrip()
                    break
        else:
            raise SplitError("unbalanced attribute in: %r" % decl[:120])
    return decl


_MODIFIER_WORDS = {"public", "private", "internal", "protected", "static", "readonly",
                   "const", "unsafe", "extern", "new", "override", "virtual", "sealed",
                   "abstract", "partial", "volatile", "async", "ref"}


def _first_stop(decl):
    """Offset of the first '(' / '=' / ';' / '{' that ends a declaration head.

    Skips generic argument lists (``Dictionary<int, (double a, double b)>``) and tuple
    types (``internal Dictionary<int, (int segIdx, Vector3 pos)> Name``): a '(' is a
    parameter list only when it follows an identifier that is not a modifier, or a
    generic method's '>'.
    """
    angle = 0
    i = 0
    n = len(decl)
    while i < n:
        c = decl[i]
        if c == "<":
            angle += 1
        elif c == ">" and angle > 0:
            angle -= 1
        elif angle == 0 and c in "=;{":
            return i
        elif c == "(":
            prev = decl[:i].rstrip()
            word = re.search(r"(\w+)$", prev)
            is_params = angle == 0 and ((word is not None and word.group(1) not in _MODIFIER_WORDS)
                                        or prev.endswith(">"))
            if is_params:
                return i
            depth = 0
            while i < n:
                if decl[i] == "(":
                    depth += 1
                elif decl[i] == ")":
                    depth -= 1
                    if depth == 0:
                        break
                i += 1
        i += 1
    return n


def classify_member(chunk_text):
    decl = _strip_leading_attributes(mask_comments_and_literals(chunk_text))
    if not decl.strip():
        raise SplitError("empty member chunk")
    first = _first_stop(decl)
    tm = _TYPE_RE.search(decl)
    if tm and tm.start() < first:
        return tm.group(2), "type"
    head = decl[:first]
    stop_char = decl[first] if first < len(decl) else ""
    m = _IDENT_BEFORE_RE.search(head)
    if not m:
        raise SplitError("cannot name member: %r" % decl[:160])
    name = m.group(1)
    if re.search(r"\bevent\b", head):
        return name, "field"
    if stop_char == "(":
        if name == CLASS_NAME:
            return name, "constructor"
        return name, "method"
    if stop_char == "=" and decl.startswith("=>", first):
        # An expression-bodied property holds no state of its own.
        return name, "exprproperty"
    if stop_char in ("=", ";"):
        if re.search(r"\bconst\b", head):
            return name, "const"
        return name, "field"
    if stop_char == "{":
        return name, "property"
    raise SplitError("cannot classify member: %r" % decl[:160])


def _split_regions(body_lines, start, end):
    """Split body lines [start, end] at region directive lines (one pseudo-member each)."""
    out = []
    cur = start
    for k in range(start, end + 1):
        m = REGION_RE.match(body_lines[k])
        if not m:
            continue
        for j in range(cur, k):
            if body_lines[j].strip():
                raise SplitError("non-blank line %r before a region directive on body line %d"
                                 % (body_lines[j], k + 1))
        out.append(Member(body_lines[k].strip(), m.group(1), cur, k, body_lines[cur:k + 1]))
        cur = k + 1
    return out, cur


def parse_file(text, decl=INPUT_DECL):
    lines = text.split("\n")
    trailing_newline = text.endswith("\n")
    if trailing_newline:
        lines = lines[:-1]
    decl_idx = [k for k, l in enumerate(lines) if l == decl]
    if len(decl_idx) != 1:
        raise SplitError("expected exactly one line %r, found %d" % (decl, len(decl_idx)))
    d = decl_idx[0]
    if lines[d + 1] != "    {":
        raise SplitError("expected '    {' after the class declaration")
    if lines[-2:] != ["    }", "}"]:
        raise SplitError("expected the file to end with '    }' and '}'")
    header = lines[:d + 2]
    footer = lines[-2:]
    body_lines = lines[d + 2:-2]
    ns_idx = [k for k, l in enumerate(header) if l.startswith("namespace ")]
    if len(ns_idx) != 1:
        raise SplitError("expected one namespace line in the header")
    usings = header[:ns_idx[0]]
    for l in usings:
        if l.strip() and not re.match(r"^using [\w.]+;$", l):
            raise SplitError("unsupported line before the namespace: %r" % l)
    namespace_line = header[ns_idx[0]]
    if header[ns_idx[0] + 1] != "{":
        raise SplitError("expected '{' after the namespace line")

    lex_lines = [(" " * len(l)) if REGION_RE.match(l) else l for l in body_lines]
    body = "\n".join(lex_lines) + "\n"
    ends = member_end_offsets(body)
    starts = [0]
    for k, ch in enumerate(body):
        if ch == "\n":
            starts.append(k + 1)
    members = []
    prev_line = 0
    for off in ends:
        line_idx = bisect.bisect_right(starts, off - 1) - 1
        rest = body[off:starts[line_idx + 1] - 1] if line_idx + 1 < len(starts) else body[off:]
        rest_s = rest.strip()
        if rest_s and not rest_s.startswith("//"):
            raise SplitError("code after a member end on body line %d: %r"
                             % (line_idx + 1, body_lines[line_idx]))
        if line_idx < prev_line:
            raise SplitError("two members end on body line %d: %r" % (line_idx + 1, body_lines[line_idx]))
        regions, chunk_start = _split_regions(body_lines, prev_line, line_idx)
        members.extend(regions)
        chunk = body_lines[chunk_start:line_idx + 1]
        name, kind = classify_member("\n".join(chunk))
        members.append(Member(name, kind, chunk_start, line_idx, chunk))
        prev_line = line_idx + 1
    regions, tail_start = _split_regions(body_lines, prev_line, len(body_lines) - 1)
    members.extend(regions)
    if any(m.kind in ("region", "endregion") for m in members):
        raise SplitError("#region directives are not supported by this split")
    tail = body_lines[tail_start:]
    if mask_comments_and_literals("\n".join(tail)).strip():
        raise SplitError("unparsed code after the last member: %r" % tail[:5])
    return {
        "lines": lines,
        "trailing_newline": trailing_newline,
        "header": header,
        "footer": footer,
        "usings": [l for l in usings if l.strip()],
        "namespace_line": namespace_line,
        "decl_index": d,
        "members": members,
        "tail": tail,
        "body_offset": d + 2,
    }


# ---------------------------------------------------------------------------
# Split
# ---------------------------------------------------------------------------

def member_keys(members):
    """(name, occurrence) per member, occurrence counted 1-based per name in file order."""
    seen = collections.Counter()
    keys = []
    for m in members:
        seen[m.name] += 1
        keys.append((m.name, seen[m.name]))
    return keys


def _parse_entry(entry):
    m = OCCURRENCE_RE.match(entry)
    if m:
        return m.group(1), int(m.group(2))
    if not re.match(r"^\w+$", entry):
        raise SplitError("bad mapping entry %r" % entry)
    return entry, None


def assign(members):
    """Target area per member (None = main file) and the unmapped movable members."""
    table = [(area, names) for area, (_, names) in AREAS.items()] + [(None, MAIN_MEMBERS)]
    bare = {}
    numbered = {}
    for area, names in table:
        for entry in names:
            name, occ = _parse_entry(entry)
            if occ is None:
                if name in bare or any(k[0] == name for k in numbered):
                    raise SplitError("member %s mapped twice" % name)
                bare[name] = area
            else:
                if name in bare or (name, occ) in numbered:
                    raise SplitError("member %s mapped twice" % entry)
                numbered[(name, occ)] = area
    keys = member_keys(members)
    present = set(keys)
    counts = collections.Counter(m.name for m in members)
    stale = sorted([n for n in bare if n not in counts]
                   + ["%s@%d" % k for k in numbered if k not in present])
    if stale:
        raise SplitError("mapping names members that no longer exist: %s" % ", ".join(stale))
    for name in set(k[0] for k in numbered):
        missing = [i for i in range(1, counts[name] + 1) if (name, i) not in numbered]
        if missing:
            raise SplitError("%s is mapped by occurrence but occurrence(s) %s are not mapped"
                             % (name, ", ".join(str(i) for i in missing)))
    targets = []
    unmapped = []
    for m, key in zip(members, keys):
        mapped = key in numbered or m.name in bare
        area = numbered[key] if key in numbered else bare.get(m.name)
        if mapped and m.kind not in MOVABLE_KINDS:
            raise SplitError("%s %s is mapped, but state (%s) stays in the main file without a mapping"
                             % (m.kind, m.name, m.kind))
        if not mapped and m.kind in MOVABLE_KINDS:
            unmapped.append("%s %s (body line %d)" % (m.kind, m.name, m.first_line + 1))
        targets.append(area)
    return targets, unmapped


def code_text(text):
    """Comments and literal text removed, the code inside interpolation holes kept.

    ``mask_comments_and_literals`` drops a whole interpolated string, holes included; a
    using is needed for a type named only inside a hole (``$"{x.ToString(CultureInfo...)}"``)
    just the same.
    """
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "/" and text.startswith("//", i):
            i = _skip_line_comment(text, i)
            out.append("\n")
            continue
        if c == "/" and text.startswith("/*", i):
            i = _skip_block_comment(text, i)
            out.append(" ")
            continue
        if c == "'":
            i = _skip_char(text, i)
            out.append(" ")
            continue
        lit = _literal_start(text, i)
        if lit is not None:
            q, verbatim, interpolated = lit
            end = _skip_string(text, q, verbatim, interpolated)
            out.append(" ")
            if interpolated:
                j = q + 1
                while j < end - 1:
                    if text[j] == "\\" and not verbatim:
                        j += 2
                        continue
                    if text[j] == '"' and verbatim:
                        j += 2
                        continue
                    if text[j] == "{":
                        if text.startswith("{{", j):
                            j += 2
                            continue
                        hole_end = _skip_code(text, j + 1, stop_at_close_brace=True)
                        out.append(" " + code_text(text[j + 1:hole_end - 1]) + " ")
                        j = hole_end
                        continue
                    j += 1
            i = end
            continue
        out.append(c)
        i += 1
    return "".join(out)


def _masked_words(lines):
    return set(re.findall(r"\b\w+\b", code_text("\n".join(lines))))


def needed_usings(original_usings, body_lines):
    words = _masked_words(body_lines)
    out = []
    for u in original_usings:
        if u not in USING_TYPES:
            raise SplitError("no identifier table for %r; add it to USING_TYPES" % u)
        if words.intersection(USING_TYPES[u]):
            out.append(u)
    return out


def build_outputs(parsed, targets):
    members = parsed["members"]
    per_file = collections.OrderedDict()
    per_file[None] = []
    for area in AREAS:
        per_file[area] = []
    last_index = {}
    for idx, (m, area) in enumerate(zip(members, targets)):
        chunk = list(m.lines)
        out = per_file[area]
        if not out:
            if area is not None:
                while chunk and not chunk[0].strip():
                    chunk = chunk[1:]
        elif last_index.get(area) != idx - 1 and chunk and chunk[0].strip() and out[-1].strip():
            # Not adjacent in the original: keep one blank line between members.
            out.append("")
        out.extend(chunk)
        last_index[area] = idx
    outputs = collections.OrderedDict()
    ns_at = parsed["header"].index(parsed["namespace_line"])
    main_rest = [CLASS_DECL if l == INPUT_DECL else l for l in parsed["header"][ns_at:]]
    main_body = main_rest + per_file[None] + parsed["tail"] + parsed["footer"]
    main_usings = needed_usings(parsed["usings"], main_body)
    outputs[None] = main_usings + [""] + main_body
    for area in AREAS:
        if not per_file[area]:
            raise SplitError("area %s received no members" % area)
        head, foot = wrapper_lines(parsed, area)
        usings = needed_usings(parsed["usings"], per_file[area])
        outputs[area] = usings + [""] + head + per_file[area] + foot
    return outputs


def wrapper_lines(parsed, area):
    desc = AREAS[area][0]
    return ([parsed["namespace_line"], "{", "    // " + desc, PART_DECL, "    {"],
            ["    }", "}"])


def verify(parsed, outputs):
    def nonblank(lines):
        return collections.Counter(l for l in lines if l.strip() and l not in parsed["usings"])

    for area, lines in outputs.items():
        ns_at = lines.index(parsed["namespace_line"])
        using_lines = [l for l in lines[:ns_at] if l.strip()]
        if not set(using_lines) <= set(parsed["usings"]) or len(set(using_lines)) != len(using_lines):
            raise SplitError("verification: %s carries usings outside the original's: %r"
                             % (area or "main", using_lines))
        if any(l in parsed["usings"] for l in lines[ns_at:]):
            raise SplitError("verification: a using line inside the namespace of %s" % (area or "main"))
    expected = nonblank([CLASS_DECL if l == INPUT_DECL else l for l in parsed["lines"]])
    for area in outputs:
        if area is None:
            continue
        head, foot = wrapper_lines(parsed, area)
        expected.update(nonblank(head + foot))
    actual = collections.Counter()
    for lines in outputs.values():
        actual.update(nonblank(lines))
    if actual != expected:
        missing = expected - actual
        extra = actual - expected
        msg = ["non-blank line multiset mismatch"]
        for l, c in list(missing.items())[:20]:
            msg.append("  missing x%d: %r" % (c, l))
        for l, c in list(extra.items())[:20]:
            msg.append("  extra   x%d: %r" % (c, l))
        raise SplitError("\n".join(msg))
    total = 0
    for area, lines in outputs.items():
        sub = parse_file("\n".join(lines) + "\n", CLASS_DECL if area is None else PART_DECL)
        for m in sub["members"]:
            total += 1
            if area is not None and m.kind not in MOVABLE_KINDS:
                raise SplitError("verification: %s %s landed in %s" % (m.kind, m.name, area))
    original = len(parsed["members"])
    if total != original:
        raise SplitError("verification: member count %d != original %d" % (total, original))


def out_path(src, area):
    if area is None:
        return src
    base = os.path.splitext(src)[0]
    return "%s.%s.cs" % (base, area)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--src", default=DEFAULT_SRC,
                    help="the unsplit ParsekFlightGhostPositioner.cs (default: %(default)s)")
    ap.add_argument("--list", action="store_true", help="print the member table and exit")
    ap.add_argument("--check", action="store_true", help="split and verify, write nothing")
    ap.add_argument("--remove-outputs", action="store_true",
                    help="delete the AREAS partial files (only those) and exit")
    args = ap.parse_args(argv)

    src = os.path.abspath(args.src)
    if args.remove_outputs:
        for area in AREAS:
            path = out_path(src, area)
            if os.path.exists(path):
                os.remove(path)
                print("removed %s" % os.path.relpath(path, REPO_ROOT))
        return 0
    if not (args.check or args.list):
        existing = [out_path(src, a) for a in AREAS if os.path.exists(out_path(src, a))]
        if existing:
            raise SplitError("partial files already exist; remove them first (--remove-outputs): %s"
                             % ", ".join(existing))
    with open(src, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    if "\r" in text:
        raise SplitError("CRLF line endings are not supported; normalize first")
    if text.startswith("﻿"):
        raise SplitError("a BOM is not supported")
    if INPUT_DECL not in text.split("\n"):
        raise SplitError("%r not found: the input must be the unsplit file "
                         "(is it already split? restore it first)" % INPUT_DECL)
    parsed = parse_file(text, INPUT_DECL)
    targets, unmapped = assign(parsed["members"])
    if args.list:
        off = parsed["body_offset"] + 1
        keys = member_keys(parsed["members"])
        for m, area, key in zip(parsed["members"], targets, keys):
            print("%5d-%5d %5d  %-12s %-20s %s@%d" % (m.first_line + off, m.last_line + off,
                                                     m.last_line - m.first_line + 1, m.kind,
                                                     area or "(main)", key[0], key[1]))
        return 0
    if unmapped:
        raise SplitError("unmapped members (add each to AREAS or MAIN_MEMBERS):\n  "
                         + "\n  ".join(unmapped))
    outputs = build_outputs(parsed, targets)
    verify(parsed, outputs)
    for area, lines in outputs.items():
        path = out_path(src, area)
        content = "\n".join(lines) + ("\n" if parsed["trailing_newline"] else "")
        print("%-72s %6d lines" % (os.path.relpath(path, REPO_ROOT), len(lines)))
        if not args.check:
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(content)
    print("verified: non-blank line multiset preserved; usings a subset of the original's; "
          "braces balance; every field, constant and constructor stays in the main file")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SplitError as exc:
        sys.stderr.write("split_ghostpositioner: FAILED: %s\n" % exc)
        sys.exit(1)
