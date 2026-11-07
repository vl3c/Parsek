#!/usr/bin/env python3
"""Split Source/Parsek/ParsekFlight.cs into more partial-class files, one per responsibility.

A pure file move: every moved class-level member (method, property or nested type) is
copied verbatim, with its leading comments, doc comments and attributes, into
``ParsekFlight.<Area>.cs``; nothing inside a member is edited and the members keep their
original relative order. The main file keeps the class declaration with its attribute,
base list and doc comment; every new file declares ``public partial class ParsekFlight``.

Field rule: C# runs the field initializers (static AND instance) of a partial class in an
order undefined across its files, so EVERY field, constant, auto-property with an
initializer, constructor and static constructor stays in the main file ``ParsekFlight.cs``.
Only methods, nested types and properties WITHOUT an initializer (accessor-bodied or
expression-bodied) may move. The script refuses a mapping that moves anything else.

Regions: ParsekFlight.cs groups members in ``#region`` blocks whose members land in
different files (Timeline Auto-Playback keeps its pad-evidence helpers in the main file,
the lifecycle region gives up its ghost-pos reapply helpers), so a region cannot travel
whole. The script therefore drops EVERY ``#region`` / ``#endregion`` line of the main file
before splitting (the file names replace them); when a dropped line sat between two blank
lines, one of the blanks goes with it. The line-multiset check allows exactly those lines.

Members are mapped by NAME (all overloads of a name travel together). A member the mapping
does not name stays in the main file and is reported. A mapped name that no longer exists
fails the run, so a stale mapping is noticed.

Before writing anything the script verifies that the multiset of non-blank lines across the
outputs equals the original's minus the region lines, plus only the new files' headers /
class wrappers, that every output's braces balance, that no output holds a region line, and
that the member count is preserved. Any mismatch fails loudly and nothing is written.

Re-running on a newer tip (for example after a merge conflict on the split branch):

    git checkout <tip> -- Source/Parsek/ParsekFlight.cs
    python3 scripts/refactor/split_parsekflight.py --clean

``--clean`` deletes this script's own outputs (the AREAS files) before splitting; the other
sibling partials (EXISTING_SIBLINGS) are NOT outputs of this script and are never touched.
Then map any member it reports as unmapped (edit AREAS below) and run it again. ``--list``
prints every class-level member with its kind, line range, enclosing region and target file
and writes nothing; ``--check`` runs the split and verification without writing.

Plain stdlib, self-contained (no shared module with the other split scripts).
"""

import argparse
import bisect
import collections
import os
import re
import sys
import textwrap

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_SRC = os.path.join(REPO_ROOT, "Source", "Parsek", "ParsekFlight.cs")
CLASS_NAME = "ParsekFlight"
CLASS_DECL = "    public partial class ParsekFlight : MonoBehaviour, IGhostPositioner"
PARTIAL_DECL = "    public partial class ParsekFlight"

# Existing sibling partials (not produced here); an area must not reuse their names.
EXISTING_SIBLINGS = ("AttitudeTrace", "BlockedChainTipGhost", "BreakupChildSeed",
                     "ClaimedVesselHoldGhost", "Finalization", "ForeignInventoryTransfer",
                     "GroundPartPlacement", "PlacementJump", "PostSwitchPredicates", "TailLift",
                     "TerminalEvents", "TerminalOrbit", "TimeJump")

# Area -> (one-line description written above the class wrapper, member names in it).
# Order here is the order the files are reported in; members keep their original order
# inside each file. Every class-level member not named here stays in ParsekFlight.cs.
AREAS = collections.OrderedDict([
    ("GhostPositioning", (
        "Ghost positioning shared by manual and timeline playback: point, orbit, relative-anchor, "
        "body-fixed and surface placement, spline evaluation and the positioning caches.",
        [
            "WithRelativeSectionSurfaceVelocity", "FindBodyByNameNoAlloc",
            "ComputePointHermiteMaxDeviationMeters", "allowSplinePositioning", "allowAnchorCorrection",
            "allowAnchorCorrectionInterval", "allowRenderAnchorCorrectionInterval",
            "allowPointHermiteInterpolation", "allowSplinePositioningForPlayback",
            "FormatStandaloneCoverageRange",
            "LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed", "ResolveRecordingById",
            "TryComputeLateUpdateSplineWorldPosition", "TryComputeLateUpdateSplineWorldPositionPure",
            "RecordSplineEvalForLogging", "ResetSplineEvalLoggingForTesting", "FindWaypointIndex",
            "PositionGhostAt", "FindOrbitSegment", "PredictedOrbitTailContinuityCacheEntry",
            "ClearOrbitPlaybackCaches", "PositionGhostFromOrbit", "ResolveOrbitContinuityOffset",
            "TryResolvePredictedOrbitTailContinuityOffset", "BuildPredictedOrbitTailContinuityCacheKey",
            "ShouldApplyPredictedOrbitTailContinuity",
            "TryResolvePredictedOrbitTailContinuityAnchorWorldPosition",
            "FindNearestTrackSectionIndexForContinuityAnchor", "FormatFiniteOrNa",
            "ComputeOrbitalRotation", "SafeOrbitalLookRotation", "PositionGhostFromOrbitOnly",
            "PositionGhostAtSurface", "TryResolvePlaybackDistanceReferencePosition", "IsFiniteVector3d",
            "ResolvePlaybackDistanceForEngine", "ResolveCameraAnchorWorldPosition",
            "ResolvePlaybackActiveVesselDistanceForEngine",
            "ResolvePlaybackDistanceFromReferencePosition", "ResolveUnresolvedPlaybackDistanceFallback",
            "ResolveUnresolvedRelativeSectionDistanceFallback", "IsRelativeSectionAtUT",
            "TryInterpolateAndPositionCheckpointSection",
            "TryInterpolateAndPositionCheckpointSectionWithOrbitRotation", "TryGetOrbitForSegment",
            "TryFindCheckpointOrbitSegment", "TryResolveCheckpointPointInterpolation",
            "BuildCheckpointOrbitCacheKey", "TryGetCheckpointTrackSection",
            "HasRenderableCheckpointTrackSection", "LogCheckpointPointPlayback",
            "TryResolvePlaybackWorldPosition", "TryResolveInterpolatedWorldPosition",
            "TryResolveOrbitTailWorldPosition", "TryGetAbsoluteSectionPlaybackFrames",
            "TryGetAbsoluteSectionPlaybackFramesForPlayback", "TryFindAdjacentAbsolutePlaybackFrame",
            "GetAbsolutePlaybackFrameList", "IsEligibleBoundaryBridgeFrame",
            "ReFlyDenseAbsolutePlaybackFrameCacheEntry", "TryGetDenseReFlyAbsolutePlaybackFrames",
            "ShouldUseDenseReFlyAbsolutePlaybackFrames", "BuildDensebodyFixedFramesForSection",
            "CountDensebodyFixedFramesForSection", "TryGetDenseSectionBracketWidthSeconds",
            "TryGetBracketWidthSeconds", "PointBelongsToSection", "TryResolvePointWorldPosition",
            "TryResolveTrajectoryPointWorldPosition", "TryResolveOrbitWorldPosition",
            "TryResolveRelativeWorldPosition", "TryResolveRelativeOffsetWorldPosition",
            "TryResolveLoopLiveAnchorPose", "ResolveBodyFixedPrimaryPlaybackFrames",
            "TryFindBodyFixedPrimaryBridgeFrame", "ShortRecordingId", "TryResolveActiveReFlyPid",
            "TryResolveRecordedAnchorPoseWithCoverage", "ShouldSkipMutableActiveReFlyAnchorCandidate",
            "TryResolvePreReFlyFrozenAnchorPose", "FindActiveReFlyRecording",
            "ReconcileInPlaceForkIntoTreeIfNeeded", "ShouldUsePreReFlyAnchorTrajectory",
            "TryResolveRecordedAnchorPoseFromCandidate",
            "TryResolveNearestRecordedAnchorAbsoluteSectionPose",
            "MaybeWarnLargeRecordedAnchorFallbackGap", "ShouldWarnRecordedAnchorFallbackGap",
            "BuildRecordedAnchorFallbackGapLog", "DistanceOutsideRecordedAnchorCoverage",
            "RecordedAnchorPointListCoversUT", "BuildRelativeOffsetAppliedLog",
            "BuildRecordedRelativeOffsetAppliedLog", "TryResolveRecordedAnchorPointPose",
            "TryBuildRecordedAnchorPoseFromPoint", "TryResolveSurfaceWorldPosition",
            "PositionLoopGhost", "InterpolateAndPositionRecordedRelative",
            "TryCorrectIsolatedRecordedRelativeOffsetSpike", "RecordedRelativeOffset",
            "LogRecordedRelativeOffsetSpikeCorrection", "TryPositionGhostRecordedRelativeAt",
            "TryResolveRecordedRelativeAnchorPose", "TryBuildRelativeAnchorResolverContext",
            "TryFindRelativeAnchorFocusTree", "TryUseRelativeBodyFixedPrimaryFallback",
            "RetireUnresolvedRecordedRelative", "InterpolateAndPositionLoopRelative",
            "PositionLoopGhostRelativeAt", "TryDispatchFlatPointEndpoints"
        ])),
    ("GhostPositioner", (
        "The IGhostPositioner implementation the playback engine calls, and zone rendering shared "
        "by normal, background and looped ghosts.",
        [
            "ApplyZoneRenderingImpl", "ShouldAutoActivateGhost", "DescribeWatchCutoffContext",
            "ResolveZoneWatchState", "InterpolateAndPosition", "InterpolateAndPositionEntryCore",
            "InterpolateAndPositionRelative", "TryPositionFromBodyFixedPrimary",
            "BodyFixedPrimaryCoversPlaybackUT", "ResolveBodyFixedPrimaryBracketUTs", "PositionAtPoint",
            "TryResolveExplosionAnchorPosition", "ResolveImmediateLandedGhostClearanceFallbackReason",
            "ResolveExplosionAnchorBodyName", "ResolveImmediateLandedGhostClearanceMeters",
            "ResolveNaNFallbackLandedGhostClearanceMeters",
            "ShouldApplyImmediateSurfacePositionClearance", "ShouldApplyImmediatePointSurfaceClearance",
            "ApplyLandedGhostClearance", "PositionAtSurface", "PositionFromOrbit",
            "SetOrbitTailInterpolated", "TrackLoopSeamTeleport", "LogReaimGhostTrace", "PositionLoop",
            "ApplyZoneRendering", "ClearOrbitCache"
        ])),
    ("TimelinePlayback", (
        "Timeline auto-playback driven through the engine (chain ghosts, skip reasons, playback "
        "flags, CommNet, loop units, anchor resolvers, timeline ghost teardown), manual preview "
        "playback, and the camera pre-cull ghost-position reapply.",
        [
            "GhostCameraPreCullAction", "ResolveGhostCameraPreCullAction", "ApplyGhostPosEntries",
            "ApplyGhostReapplyTransform", "ResolveGhostPosEntryRecordingId",
            "TraceGhostPositionReapply", "ShouldProcessGhostPositionReapply",
            "LogSkippedNonReFlyCameraPreCull", "TryResolveStoredRecordedAnchorPose",
            "ClampGhostsToTerrain", "StartPlayback", "StopPlayback", "UpdatePlayback",
            "FindChainTipForRecording", "FindBackgroundRecordingForVessel",
            "FindPlaybackFallbackRecordingForVessel", "FindBackgroundRecordingForChain",
            "FindPreClaimChainRecordingAtUT", "FindExactChainRecordingAtUT",
            "FindPositionFallbackRecordingForChain", "GetFirstClaimTreeIds",
            "FindChainPathRecordingAtUT", "FindNextChainRecording", "FindPreferredChildRecording",
            "FindCommittedTree", "ChainHasStarted", "TryPositionChainFallbackFromRecording",
            "RecordingHasUsablePlaybackDataAtUT", "RecordingHasUsableNonPointPlaybackDataAtUT",
            "HasOrbitCoverageAtUT", "HasSurfaceCoverageAtUT", "PositionChainGhosts",
            "PositionChainGhostFallback", "UpdateChainGhostOrbitIfNeeded", "TryStampChainMapOrbit",
            "ResolveLiveRecorderRecordingId", "IsActiveGhostChainTipFromPolicy",
            "FindNextWatchTargetFromPolicy", "TransferWatchToNextSegmentFromPolicy",
            "ExitWatchModeFromPolicy", "ExitWatchModePreservingLineage", "StartWatchHoldFromPolicy",
            "GetCurrentWarpRateSafe", "ResolveGhostPlaybackSkipReason", "BuildGhostSkipReasonIdentity",
            "BuildGhostSkipReasonMessage", "LogGhostSkipReasonChangeCore",
            "ClearGhostSkipReasonLogState", "ClearGhostSkipReasonLogStateForTesting",
            "LogGhostSkipReasonChangeForTesting", "LogGhostSkipReasonChangeIfNeeded",
            "FastForwardWatchHandoffDecision", "ClassifyFastForwardWatchHandoff",
            "BuildFastForwardWatchHandoffMessage", "LogFastForwardWatchHandoff",
            "ResolveReFlySettleStabilityForTesting", "ResolveReFlySettleStability",
            "TryGetReFlySettleStabilityReasonForRecording", "RecordReFlyAnchorHoldCandidate",
            "LogReFlyAnchorHoldTransitions", "HasAnchorReFlyUnstableFlag",
            "LogReFlySettleActiveVesselPoseIfArmed", "IsChainSeamSuccessor",
            "IsChainSeamSuccessorAtFrame", "ComputePlaybackFlags", "BuildSpawnSuppressedSummary",
            "RecordingEligibleForSessionSuppressedRenderCarveOut", "UpdateTimelinePlaybackViaEngine",
            "DriveGhostCommNet", "TryResolveGhostCommNetRecordingPosition", "GhostCommNet",
            "SubstituteReaimTrajectories", "DriveMissionLoopUnits",
            "BuildGhostRecordingAnchorCandidatesForRecorder", "ShouldRecordGhostAnchorCandidates",
            "BuildFlightRelativeAnchorResolverContext",
            "TryResolveLiveLaunchMatchedAnchorPoseForResolver", "TryGetLiveAnchorTransformDelegate",
            "TryResolveLiveAnchorTransformForResolver", "ResolveAnchorResolverPointWorldPosition",
            "ResolveAnchorResolverBodyWorldRotation", "TryResolveFlightOrbitalAnchorPose",
            "FindBodyForAnchorResolver", "ShouldLoopPlayback", "IsActiveTreeRecording",
            "TryGetLoopSchedule", "GetLoopIntervalSeconds", "TryComputeLoopPlaybackUT",
            "FindTipRecordingById", "DestroyTimelineGhost", "DestroyAllTimelineGhosts",
            "DestroyAllOverlapGhosts"
        ])),
    ("Spawning", (
        "Vessel spawning from playback: chain-tip spawn, tree-leaf spawn, chain ghosting at "
        "flight ready, orphaned and recovered timeline spawns, proximity candidates, warp-to and "
        "fast-forward, and ghost labels.",
        [
            "EvaluateAndApplyGhostChains", "SelectChainPidsToGhost", "LiveVesselGuidOrNull",
            "FilterAndGhostChains", "SpawnTreeLeaves", "CleanupOrphanedSpawnedVessels",
            "IsTimelineSpawnOfLeafVessel", "RecoverTimelineSpawnedVessel",
            "SpawnVesselOrChainTipFromPolicy", "ResolveChainGhostOnSpawn", "RetargetReplacedChainTip",
            "RetireChainAtKsc", "DeferredActivateVesselFromPolicy", "SpawnVesselOrChainTip",
            "IsAnyWarpActive", "ExitAllWarpForPlaybackStart", "DeferredActivateVessel",
            "DetermineRecordingBlockReason", "DrawGhostLabels", "GetChainStatusForRecording",
            "UpdateProximityCheck", "CollectNearbySpawnCandidates", "NotifyNewProximityCandidates",
            "WarpToRecordingEnd", "ArmSpawnWarpWatch", "TickSpawnWarpWatch",
            "BuildSpawnWarpOutcomeInput", "WarpToDeparture", "FastForwardToRecording",
            "FastForwardToEventUT", "WarpToNextCraftSpawn"
        ])),
    ("Accessors", (
        "Public accessors for ParsekUI, the watch-mode diagnostics and the pre-cleanup watch exit "
        "and camera retarget.",
        [
            "IsRecording", "IsPlaying", "PreviewGhost", "TimelineGhosts", "HasActiveTree",
            "StructuralSplitPending", "BoardMergeQuiescent", "IsWatchingGhost",
            "WatchedRecordingIndexForDiagnostics", "WatchedLoopCycleIndexForDiagnostics",
            "WatchedRecordingIndex", "DescribeWatchFocusForLogs", "DescribeWatchEligibilityForLogs",
            "ExitWatchModeBeforeTimelineGhostCleanup", "RetargetStockCamerasBeforeTimelineGhostCleanup"
        ])),
])

MOVABLE_KINDS = ("method", "type", "property")


class SplitError(Exception):
    pass


# ---------------------------------------------------------------------------
# Lexer: walks C# text, skipping comments, preprocessor lines and string / char
# literals, and reports the offsets where a class-level member ends.
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


def _at_line_start(text, i):
    k = i - 1
    while k >= 0 and text[k] in " \t":
        k -= 1
    return k < 0 or text[k] == "\n"


def _directive_end(text, i):
    """i points at a '#' that starts a line. Only #region / #endregion are allowed."""
    j = _skip_line_comment(text, i)
    word = text[i + 1:j].split()
    word = word[0] if word else ""
    if word not in ("region", "endregion"):
        raise SplitError("preprocessor directive #%s at offset %d is not supported" % (word, i))
    return j


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
        if c == "#" and _at_line_start(text, i):
            i = _directive_end(text, i)
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
        if c == "#" and _at_line_start(body, i):
            if brace != 0:
                raise SplitError("preprocessor directive inside a member at offset %d" % i)
            i = _directive_end(body, i)
            continue
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
    """Comments and directive lines removed, string / char literal contents replaced."""
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
        if c == "#" and _at_line_start(text, i):
            i = _directive_end(text, i)
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

_TYPE_RE = re.compile(r"\b(class|struct|enum|interface|record)\s+(\w+)")
_IDENT_BEFORE_RE = re.compile(r"(\w+)\s*(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>)?\s*$")


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


_MODIFIERS = {"public", "private", "protected", "internal", "static", "readonly", "override",
              "virtual", "abstract", "sealed", "new", "unsafe", "extern", "async", "partial", ""}


def _first_stop(decl):
    """Offset of the first '(', '=', ';' or '{' that ends the declaration head.

    Characters inside generic angle brackets are skipped, and so is a tuple type: a '('
    whose preceding token is a modifier keyword (or nothing) opens a type, not a
    parameter list.
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
        elif angle == 0 and c in "(=;{":
            if c == "(":
                before = decl[:i].rstrip()
                prev = re.search(r"(\w*)$", before).group(1)
                if prev in _MODIFIERS and (prev or not before):
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
                    continue
            return i
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
    if stop_char == "(":
        if name == CLASS_NAME:
            return name, ("static-constructor" if re.search(r"\bstatic\b", head) else "constructor")
        if re.search(r"\b(delegate|event)\b", head):
            return name, "field"
        return name, "method"
    if stop_char == "=" and decl[first:first + 2] == "=>":
        # Expression-bodied property: no initializer, no state.
        return name, "property"
    if stop_char in ("=", ";"):
        if re.search(r"\bconst\b", head):
            return name, "const"
        return name, "field"
    if stop_char == "{":
        if re.search(r"\bevent\b", head):
            return name, "field"
        if decl.rstrip().endswith(";"):
            # `T Name { get; set; } = initializer;`
            return name, "property-with-initializer"
        return name, "property"
    raise SplitError("cannot classify member: %r" % decl[:160])


def _is_trivia(line):
    s = line.strip()
    return (not s or s.startswith("//") or s.startswith("#region") or s.startswith("#endregion"))


def _rehome_endregions(chunks):
    """Move each chunk's leading `#endregion` (and the trivia before it) to the previous chunk.

    A member chunk is the run of lines from the end of the previous member to the end of
    this one, so an `#endregion` closing the previous member's region would otherwise ride
    in front of the next member.
    """
    out = [list(c) for c in chunks]
    for k in range(1, len(out)):
        chunk = out[k]
        cut = -1
        for j, line in enumerate(chunk):
            if not _is_trivia(line):
                break
            if line.strip().startswith("#endregion"):
                cut = j
        if cut >= 0:
            out[k - 1].extend(chunk[:cut + 1])
            out[k] = chunk[cut + 1:]
    return out


def parse_file(text, class_decl=CLASS_DECL):
    lines = text.split("\n")
    trailing_newline = text.endswith("\n")
    if trailing_newline:
        lines = lines[:-1]
    decl_idx = [k for k, l in enumerate(lines) if l == class_decl]
    if len(decl_idx) != 1:
        raise SplitError("expected exactly one line %r, found %d" % (class_decl, len(decl_idx)))
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
    namespace_line = header[ns_idx[0]]
    if header[ns_idx[0] + 1] != "{":
        raise SplitError("expected '{' after the namespace line")

    body = "\n".join(body_lines) + "\n"
    ends = member_end_offsets(body)
    starts = [0]
    for k, ch in enumerate(body):
        if ch == "\n":
            starts.append(k + 1)
    spans = []
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
        spans.append((prev_line, line_idx))
        prev_line = line_idx + 1
    tail = body_lines[prev_line:]
    if mask_comments_and_literals("\n".join(tail)).strip():
        raise SplitError("unparsed code after the last member: %r" % tail[:5])
    chunks = _rehome_endregions([body_lines[a:b + 1] for a, b in spans] + [tail])
    tail = chunks[-1]
    members = []
    for (a, b), chunk in zip(spans, chunks[:-1]):
        name, kind = classify_member("\n".join(chunk))
        members.append(Member(name, kind, a, b, chunk))
    return {
        "lines": lines,
        "trailing_newline": trailing_newline,
        "header": header,
        "footer": footer,
        "usings": usings,
        "namespace_line": namespace_line,
        "decl_index": d,
        "members": members,
        "tail": tail,
        "body_offset": d + 2,
    }


# ---------------------------------------------------------------------------
# Split
# ---------------------------------------------------------------------------

def _is_region_line(line):
    s = line.strip()
    return s.startswith("#region") or s.startswith("#endregion")


def strip_regions(lines):
    """Drop every region directive line. Returns (kept lines, dropped lines, region per kept line).

    A dropped line between two blank lines takes the following blank with it, so no new
    double blank appears. The third result names the innermost region each kept line sat
    in (for --list only).
    """
    kept, dropped, where = [], [], []
    stack = []
    skip_blank = False
    for k, line in enumerate(lines):
        s = line.strip()
        if _is_region_line(line):
            dropped.append(line)
            if s.startswith("#region"):
                stack.append(s[len("#region"):].strip())
            elif stack:
                stack.pop()
            nxt = lines[k + 1] if k + 1 < len(lines) else None
            if kept and not kept[-1].strip() and nxt is not None and not nxt.strip():
                skip_blank = True
            continue
        if skip_blank:
            skip_blank = False
            if not s:
                dropped.append(line)
                continue
        kept.append(line)
        where.append(stack[-1] if stack else "")
    if stack:
        raise SplitError("unbalanced regions in the source: %s left open" % ", ".join(stack))
    return kept, dropped, where


def assign(members):
    for area in AREAS:
        if area in EXISTING_SIBLINGS:
            raise SplitError("area %s collides with an existing sibling partial file" % area)
    name_to_area = {}
    for area, (_, names) in AREAS.items():
        for nm in names:
            if nm in name_to_area:
                raise SplitError("member %s mapped twice (%s and %s)" % (nm, name_to_area[nm], area))
            name_to_area[nm] = area
    present = set(m.name for m in members)
    stale = sorted(n for n in name_to_area if n not in present)
    if stale:
        raise SplitError("mapping names members that no longer exist: %s" % ", ".join(stale))
    targets = []
    unmapped = []
    for m in members:
        area = name_to_area.get(m.name)
        if area is not None and m.kind not in MOVABLE_KINDS:
            raise SplitError("%s %s is mapped to %s, but fields, constants, initialized properties "
                             "and constructors must stay in the main file" % (m.kind, m.name, area))
        if area is None and m.kind in MOVABLE_KINDS:
            unmapped.append(m.name)
        targets.append(area)
    return targets, unmapped


def wrapper_lines(parsed, area):
    desc = AREAS[area][0]
    head = list(parsed["usings"])
    comment = textwrap.wrap(desc, width=92, break_long_words=False, break_on_hyphens=False)
    head += [parsed["namespace_line"], "{"] + ["    // " + c for c in comment] + [PARTIAL_DECL, "    {"]
    return head, ["    }", "}"]


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
    main = list(parsed["header"])
    body = per_file[None] + parsed["tail"]
    while body and not body[-1].strip():
        body = body[:-1]
    main += body + parsed["footer"]
    outputs[None] = main
    for area in AREAS:
        if not per_file[area]:
            continue
        body = per_file[area]
        while body and not body[-1].strip():
            body = body[:-1]
        head, foot = wrapper_lines(parsed, area)
        outputs[area] = head + body + foot
    return outputs


def verify(original_lines, dropped, parsed, outputs):
    def nonblank(lines):
        return collections.Counter(l for l in lines if l.strip())

    expected = nonblank(original_lines) - nonblank(dropped)
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
        if any(_is_region_line(l) for l in lines):
            raise SplitError("verification: %s holds a region line" % (area or "(main)"))
        sub = parse_file("\n".join(lines) + "\n", CLASS_DECL if area is None else PARTIAL_DECL)
        total += len(sub["members"])
        for m in sub["members"]:
            if area is not None and m.kind not in MOVABLE_KINDS:
                raise SplitError("verification: %s %s landed in %s" % (m.kind, m.name, area))
    if total != len(parsed["members"]):
        raise SplitError("verification: member count %d != original %d" % (total, len(parsed["members"])))


def out_path(src, area):
    if area is None:
        return src
    base = os.path.splitext(src)[0]
    return "%s.%s.cs" % (base, area)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--src", default=DEFAULT_SRC, help="the unsplit ParsekFlight.cs (default: %(default)s)")
    ap.add_argument("--list", action="store_true", help="print the member table and exit")
    ap.add_argument("--check", action="store_true", help="split and verify, write nothing")
    ap.add_argument("--clean", action="store_true",
                    help="delete this script's own output files (the AREAS files) before splitting")
    args = ap.parse_args(argv)

    src = os.path.abspath(args.src)
    with open(src, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    if "\r" in text:
        raise SplitError("CRLF line endings are not supported; normalize first")
    if text.startswith("\ufeff"):
        raise SplitError("a BOM is not supported")
    trailing_newline = text.endswith("\n")
    original_lines = text.split("\n")
    if trailing_newline:
        original_lines = original_lines[:-1]
    kept, dropped, where = strip_regions(original_lines)
    parsed = parse_file("\n".join(kept) + ("\n" if trailing_newline else ""), CLASS_DECL)
    targets, unmapped = assign(parsed["members"])
    if args.list:
        off = parsed["body_offset"]
        for m, area in zip(parsed["members"], targets):
            first = m.first_line
            for k, line in enumerate(m.lines):
                if line.strip() and not line.strip().startswith(("//", "[")):
                    first = m.first_line + k
                    break
            print("%5d-%5d %4d  %-26s %-20s %-40s %s" % (
                m.first_line + off + 1, m.last_line + off + 1, m.last_line - m.first_line + 1,
                m.kind, area or "(main)", where[first + off][:40], m.name))
        return 0
    if not args.check:
        if args.clean:
            for a in AREAS:
                if os.path.exists(out_path(src, a)):
                    os.remove(out_path(src, a))
        existing = [out_path(src, a) for a in AREAS if os.path.exists(out_path(src, a))]
        if existing:
            raise SplitError("partial files already exist; remove them first (or pass --clean): %s"
                             % ", ".join(existing))
    outputs = build_outputs(parsed, targets)
    verify(original_lines, dropped, parsed, outputs)
    for area, lines in outputs.items():
        path = out_path(src, area)
        content = "\n".join(lines) + ("\n" if trailing_newline else "")
        print("%-60s %6d lines" % (os.path.relpath(path, REPO_ROOT), len(lines)))
        if not args.check:
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(content)
    print("region lines dropped: %d" % sum(1 for l in dropped if _is_region_line(l)))
    if unmapped:
        print("unmapped members kept in the main file: %d names" % len(set(unmapped)))
    print("verified: non-blank line multiset preserved apart from the region lines; braces balanced; "
          "fields, constants, initialized properties and constructors all in the main file")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SplitError as exc:
        sys.stderr.write("split_parsekflight: FAILED: %s\n" % exc)
        sys.exit(1)
