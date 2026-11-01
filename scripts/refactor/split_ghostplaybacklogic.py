#!/usr/bin/env python3
"""Split GhostPlaybackLogic.cs into more partial-class files, one per playback responsibility.

A pure file move: every class-level member (method, delegate or nested type) is copied
verbatim, with its leading comments, doc comments and attributes, into
``GhostPlaybackLogic.<Area>.cs``; nothing inside a member is edited. The class is already
``internal static partial class GhostPlaybackLogic`` (with the hand-split siblings
``.SpanClock.cs``, ``.StandaloneLoopClock.cs``, ``.WarpLoopPolicy.cs`` and ``.WatchMode.cs``,
which this script never reads or writes), so the declaration line is unchanged.

Static state rule: C# runs the static field initializers of a partial class in an
undefined order across its files, so EVERY field (static or not, with or without an
initializer), every property and any constructor or static constructor stays in the main
file ``GhostPlaybackLogic.cs``. Only methods, delegates, nested types and ``const``
declarations (compile-time literals, no runtime initializer) may move. The script refuses
a mapping that moves anything else.

Region directives: ``#region`` / ``#endregion`` lines are kept verbatim and travel as a
pair to the file of the first member inside the region (the main file when it holds
none), so every output's regions stay balanced; a region whose members are spread over
several files is closed in the file that holds its first member. No other preprocessor
directive is accepted at class level.

Members are mapped by NAME (all overloads of a name travel together). A member the
mapping does not name stays in the main file and is reported. A mapped name that no
longer exists fails the run, so a stale mapping is noticed.

Before writing anything the script verifies that the multiset of non-blank lines across
the outputs equals the original's plus only the new files' headers / class wrappers, that
every output's braces and regions balance, that no field / property / constructor left
the main file, and that the member count is unchanged. Any mismatch fails loudly and
nothing is written.

Re-running on a newer main (for example after a merge conflict on the split branch);
remove ONLY the files this script writes, never the four hand-split siblings:

    git checkout origin/main -- Source/Parsek/GhostPlaybackLogic.cs
    (cd Source/Parsek && rm -f GhostPlaybackLogic.GhostInfo.cs GhostPlaybackLogic.Visibility.cs \
        GhostPlaybackLogic.PartEvents.cs GhostPlaybackLogic.EngineRcsFx.cs GhostPlaybackLogic.Audio.cs \
        GhostPlaybackLogic.Robotics.cs GhostPlaybackLogic.SynthesizedMotion.cs \
        GhostPlaybackLogic.HeatDeployablesLights.cs GhostPlaybackLogic.SpawnDecision.cs)
    python3 scripts/refactor/split_ghostplaybacklogic.py

then map any member it reports as unmapped (edit FAMILIES below) and run it again.
``--list`` prints every class-level member with its kind, line range and target file and
writes nothing; ``--check`` runs the split and verification without writing.

Plain stdlib, self-contained (no shared module with the other split scripts).
"""

import argparse
import collections
import os
import re
import sys

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_SRC = os.path.join(REPO_ROOT, "Source", "Parsek", "GhostPlaybackLogic.cs")
CLASS_NAME = "GhostPlaybackLogic"
CLASS_DECL = "    internal static partial class GhostPlaybackLogic"
# Hand-split sibling files that already exist; an area name may not reuse one.
EXISTING_SIBLINGS = ("SpanClock", "StandaloneLoopClock", "WarpLoopPolicy", "WatchMode")

# Family -> (one-line description written above the class wrapper, member names in it).
# Order here is the order the files are reported in; members keep their original order
# inside each file.
FAMILIES = collections.OrderedDict([
    ("GhostInfo", (
        "Ghost info dictionaries, spawn-time snapshot baselines and orphan engine playback.",
        [
            "BuildDictByPid", "PopulateGhostInfoDictionaries", "ApplySnapshotBaselines",
            "SnapshotBaselineActions", "SnapshotParachuteAction",
            "ResolveSnapshotBaselineActions", "LightBaselineSaysSomething",
            "ClassifySnapshotParachuteAction", "ApplySnapshotParachuteBaseline",
            "ApplySnapshotRoboticBaseline", "ApplyRoboticSpawnBaseline",
            "RestoreRoboticSpawnBaselines", "ShouldEvaluateOrphanEnginePlayback",
            "PopulateDeployableInfos", "PopulateEngineInfos", "PopulateHeatInfos",
            "PopulateLightInfos", "PopulateRcsInfos", "PopulateRoboticInfos",
            "PopulateAudioInfos", "AutoStartOrphanEnginePlayback", "BuildEngineEventKeySet",
        ])),
    ("Visibility", (
        "Explosion triggers, ghost part visibility, the loop-cycle reset and compound-part visibility.",
        [
            "ShouldTriggerExplosion", "TryGetEarlyDestroyedDebrisExplosionUT",
            "ShouldTriggerExplosionAtPlaybackUT", "HideAllGhostParts",
            "ReactivateGhostPartHierarchyForLoopRewind", "ResetForLoopCycle",
            "ReapplySpawnTimeModuleBaselinesForLoopCycle", "RefreshCompoundPartVisibility",
            "ShouldHideCompoundPart", "ShouldRestoreCompoundPart",
            "RemovePartSubtreeFromLogicalPresence", "RestoreCompoundPartsForPlacedTargets",
        ])),
    ("PartEvents", (
        "Part event replay: decouple / destroy / parachute / inventory / flag events and fake canopies.",
        [
            "ApplyPartEvents", "RecordEngineEvent", "RecordLightPowerEvent",
            "RecordCargoBayEvent", "ApplyFairingJettisonedState",
            "ClassifyFairingJettisonApply", "ApplyDecoupledPartEvent", "CollectSubtreePids",
            "StopFxAndAudioForSubtree", "ApplyDestroyedPartEvent",
            "PlayPartDestroyedFxAtPart", "ResolvePartExplosionPower",
            "ExplosionPotentialLookup", "DefaultExplosionPotentialLookup",
            "ApplyParachuteSemiDeployedEvent", "TryResolveParachuteCapActive",
            "ApplyParachuteCutEvent", "ApplyParachuteRepackedEvent",
            "ApplyParachuteDestroyedEvent", "ApplyInventoryPartPlacedEvent",
            "ApplyInventoryPartRemovedEvent", "InventoryApplyOutcome", "GhostCarriesPart",
            "SpawnPartPuffAtPart", "HideGhostPart", "SetGhostPartActive",
            "InitializeInventoryPlacementVisibility", "InitializeFlagVisibility",
            "ApplyFlagEvents", "SpawnFlagVesselsUpToUT", "FlagReplayOutcome",
            "TrySpawnFlagVessel", "FlagExistsAtPosition", "HidePartSubtree",
            "RecalculateCameraPivot", "ApplyParachuteDeployedEvent", "TrackFakeCanopy",
            "DestroyFakeCanopy", "DestroyAllFakeCanopies", "DestroyCanopyAndMaterial",
        ])),
    ("EngineRcsFx", (
        "Engine and RCS particle FX: emission, plume magnitude scaling and deferred power restores.",
        [
            "BuildEngineFxEmissionDiagnostic", "FormatVector3Invariant",
            "SetEngineEmission", "ClassifyEngineEmissionApply",
            "SetEngineEmissionWithOutcome", "StopEngineFxForPart", "StopRcsFxForPart",
            "StopAllEngineFx", "RestoreActiveEngineFx", "StopAllRcsFx",
            "DetachAndLingerParticleSystems", "StopAndClearParticleSystems",
            "SetParticleRenderersEnabled", "SetKspEmittersEnabled", "SetRcsEmission",
            "ClassifyRcsEmissionApply", "SetRcsEmissionWithOutcome",
            "FxMagnitudeMinVisibleRatio", "ComputeEngineEmissionRate", "ComputeEngineSpeed",
            "ComputeFxMagnitudeRatio", "ResetFxMagnitudeWriteFailureCountForTesting",
            "TryConvertMagnitudeForField", "IsSupportedMagnitudeScalarFieldType",
            "IsSupportedMagnitudeVectorFieldType", "TryWriteMagnitudeScalarField",
            "TryWriteMagnitudeVectorField", "AppendFailure", "ScaleEmitterLocalVelocity",
            "ApplyFxMagnitudeScale", "ComputeScaledRcsEmissionRate",
            "ComputeScaledRcsSpeed", "StopAllRcsEmissions", "RestoreAllRcsEmissions",
            "CollectDeferredEnginePowerRestores", "ClearTrackedEnginePowerForPart",
            "CollectDeferredRcsPowerRestores", "ClearTrackedRcsPowerForPart",
            "CollectDeferredAudioPowerRestores", "ClearTrackedAudioPowerForPart",
            "RestoreDeferredRuntimeFxState",
        ])),
    ("Audio", (
        "Ghost audio: looped engine audio and its playback cap, explosion one-shots, mute / pause.",
        [
            "ResolveAudioPriorityDistance", "UpdateLoopedAudioPriority",
            "SelectHighestPriorityActiveLoopedGhostAudioSources",
            "EnforceLoopedAudioPlaybackCapWithTestingOverride",
            "EnforceLoopedAudioPlaybackCap", "ClassifyEngineAudioApply", "SetEngineAudio",
            "ShouldEnforceLoopedAudioPlaybackCap", "CanStartLoopedGhostAudio",
            "StopAudioForPart", "TryTriggerStockExplosionFxOrCustom",
            "TryPlayIndependentExplosionOneShot", "ResolveExplosionOneShotAudioCandidate",
            "QueueExplosionOneShotAudio", "PauseExplosionOneShotAudio",
            "PauseFxMongerExplosionAudioSources", "UnpauseExplosionOneShotAudio",
            "TrackExplosionOneShotAudioSource", "PruneExplosionOneShotAudioSources",
            "PruneMissingAudioSources", "ContainsAudioSourceReference",
            "NormalizeOneShotDurationSeconds", "MuteAllAudio", "UnmuteAllAudio",
            "PauseAllAudio", "UnpauseAllAudio", "ComputeGhostAudioVolume",
            "ComputeAtmosphereFactor", "UpdateAudioAtmosphere", "AppendIfNotNull",
            "ShouldLoopedAudioCandidatePrecede", "InsertLoopedAudioSelectionCandidate",
            "IsLoopedAudioSelectedForPlayback", "StartLoopedGhostAudio",
            "StopLoopedGhostAudio",
        ])),
    ("Robotics", (
        "Robotic part poses, wheel spin from ground speed and wheel steering.",
        [
            "ComputeRotorDeltaDegrees", "DefaultWheelRadiusMeters", "MinWheelRadiusMeters",
            "WheelStationarySpeedMetersPerSecond", "ComputeSurfaceHorizontalVelocity",
            "ComputeWheelRollForward", "ComputeWheelSpinDeltaDegrees",
            "IsWheelSpinGroundContactEnvironment", "ResolveWheelGroundContact",
            "ApplyRoboticPose", "ApplyRoboticEvent", "UpdateActiveRobotics",
            "ComputeTargetWheelSteeringDegrees", "WheelSteeringGainDegPerDegPerSec",
            "MaxWheelSteeringDegrees", "WheelSteeringSlewDegPerSec",
            "MinWheelSteeringGroundSpeed", "WheelSteeringHeadingEmaAlpha",
            "ResolveGhostHeadingRateDegPerSec", "TryResolveWheelGroundSpeedInputs",
            "TryComputeWheelGroundInputs", "LogWheelGroundSpeed",
            "FormatWheelGroundSpeedLine", "LogWheelGroundContactDecision",
        ])),
    ("SynthesizedMotion", (
        "Synthesized motion (gimbals, control surfaces, sun tracking, converters, EVA jetpack) and launch dust.",
        [
            "MaxSynthSampleSeconds", "SynthAngularEmaAlpha",
            "SynthAngularDeadbandDegPerSec", "GimbalGainDegPerDegPerSec",
            "ControlSurfaceGainDegPerDegPerSec", "DefaultControlSurfaceRangeDegrees",
            "DefaultGimbalRangeDegrees", "SynthDeflectionSlewDegPerSec",
            "SunTrackingSlewDegPerSec", "ComputeLocalAngularVelocityDegPerSec",
            "DecayRateTowardZero", "ComputeEmaVector", "ComputeSynthDeflectionDegrees",
            "SlewTowardDegrees", "ComputeHeadingRateDegPerSec", "TryComputeAimAngleDegrees",
            "RestoreSynthesizedMotionNeutralPoses", "UpdateSynthesizedMotion",
            "ComputeConverterLoopPhase", "ResolveConverterLoopBlend", "DriveConverterLoops",
            "ApplyConverterLoopState", "ApplyConverterLoopStateWithOutcome",
            "ShouldEmitEvaJetpackPlume", "ApplyEvaState", "ApplyEvaStateWithOutcome",
            "TryUpdateEvaFlags", "EvaPlumeReconcileAction", "ClassifyEvaPlumeReconcile",
            "ReconcileEvaJetpackPlume", "UpdateEvaJetpackPlumeForFrame",
            "DriveSynthesizedGimbals", "DriveSynthesizedControlSurfaces",
            "DriveSunTracking", "ApplySunTrackingAngle", "ResolveSunTrackingAxisLocal",
            "ResolveSunTrackingReferenceLocal", "DescribeDeployableGateForPart",
            "DescribeSunTrackingState", "IsAnyEnginePowerOnPart",
            "IsDeployableTransitionRunningForPart", "IsDeployablePoseHeldForPart",
            "IsDeployablePoseFullyDeployedForPart", "ResolveCachedGhostBody",
            "IsGhostInAtmosphere", "LaunchDustMaxAglMeters", "LaunchDustEmissionMax",
            "LaunchDustSizeMin", "LaunchDustSizeMax", "LaunchDustMinIntensity",
            "LaunchDustGroundLatchScanCap", "SumEnginePower",
            "TryLatchLaunchDustGroundReference", "TryComputeLaunchDustIntensity",
        ])),
    ("HeatDeployablesLights", (
        "Heat / reentry state, deployables, cargo bays and jettison panels, and lights.",
        [
            "ApplyHeatState", "ClassifyHeatApply", "ApplyHeatStateWithOutcome",
            "ResetReentryFx", "DefaultDeployableClipSeconds", "MinDeployableClipSeconds",
            "MaxDeployableClipSeconds", "ClampDeployableClipSeconds",
            "ComputeDeployableTransitionFraction", "ApplyDeployableFraction",
            "UpdateActiveDeployables", "ClearActiveDeployableTransitions",
            "ApplyDeployableState", "ApplyDeployableBrokenState",
            "ClassifyDeployableBrokenApply", "ApplyDeployableBrokenStateWithOutcome",
            "ClassifyDeployableApply", "ApplyDeployableStateWithOutcome",
            "ApplyCargoBayState", "ApplyCargoBayStateWithOutcomes",
            "ApplyJettisonPanelState", "ClassifyJettisonPanelApply",
            "ApplyJettisonPanelStateWithOutcome", "GetOrCreateLightPlaybackState",
            "ApplyLightPowerEvent", "ApplyLightPowerEventWithOutcomes",
            "ApplyLightBlinkModeEvent", "ApplyLightBlinkModeEventWithOutcome",
            "ApplyLightBlinkRateEvent", "ApplyLightBlinkRateEventWithOutcome",
            "UpdateBlinkingLights", "SetLightState", "SetLightStateWithOutcomes",
            "WriteUnityLightState", "ClassifyUnityLightApply",
            "ApplyColorChangerLightState", "ApplyColorChangerLightStateWithOutcome",
            "WriteColorChangerLightState", "ClassifyColorChangerLightApply",
            "ApplyColorChangerCharState",
        ])),
    ("SpawnDecision", (
        "Past-end completion, loop first-run spawn and the spawn-at-recording-end decision.",
        [
            "ShouldFireHiddenPastEndCompletion", "ShouldAttemptLoopFirstRunSpawn",
            "FormatChainLoopFirstRunSpawn", "LogChainLoopFirstRunSpawn",
            "ResolveHistoricalNeverReplayed", "ShouldSuppressSpawnForChain",
            "IsSpawnableTerminal", "TryMapSituationNameToTerminalState",
            "ShouldSpawnAtRecordingEnd", "LiveFlownRecordingSpawnReason",
            "IsLiveFlownRecording", "ShouldRefuseLiveFlownSpawn",
            "ComposeFlightFinalNeedsSpawn", "IsFinalSpawnSegment",
            "ShouldBlockSpawnForRewindSuppression", "SetLiveSameCraftOverrideForTesting",
            "ResetLiveSameCraftOverrideForTesting", "AnyLiveRealVesselSharesRecordedCraft",
            "ResolveRewindSuppressionLiveLaunchPresence", "IsNearbySpawnCandidateEligible",
            "IsSpawnSettledUnspawnable", "ShouldSpawnAtKscEnd", "IsNonLeafInCommittedTree",
            "IsNonLeafInTree", "IsEffectiveLeafForVessel",
            "RecordingContinuesPastChildBranch", "TryGetContinuedPastChildBranch",
            "TryResolveTreeContext", "IsSnapshotSituationUnsafe",
        ])),
])

# Members that stay in the main file on purpose: the delegates and nested types declared
# beside the static fields, the test resets, the external-vessel ghost policy (it owns
# the vessel-existence cache fields), and the zone / soft-cap fidelity / auto loop range
# policy. Listed so that "unmapped" reports only members a newer main added.
MAIN_MEMBERS = [
    "TryTriggerStockExplosionFxDelegate", "ResolveExplosionOneShotAudioCandidateDelegate",
    "PlayExplosionOneShotAudioDelegate", "HideGhostForRetire", "StockExplosionFxResult",
    "ExplosionOneShotAudioCandidate", "ResetForTesting", "SetFlagExistsOverrideForTesting",
    "SetSpawnFlagOverrideForTesting", "ResetFlagReplayOverridesForTesting",
    "SetVesselExistsOverrideForTesting", "SetVesselGuidResolverOverrideForTesting",
    "ResetVesselGuidResolverOverrideForTesting", "SetIsGhostedOverride",
    "ResetIsGhostedOverride", "RealVesselExists", "RealVesselExistsForRecording",
    "IsLiveAnchorDoubleSuppressed", "ResolveLiveVesselGuid", "IsGhostedByChain",
    "ShouldSkipExternalVesselGhost", "IsVesselRecordedByTree", "ResetVesselExistsOverride",
    "InvalidateVesselCache", "ShouldSkipTimelinePlaybackForPendingReFlyInvoke",
    "ResetVesselCacheForTesting", "GetZoneRenderingPolicy",
    "ShouldForceWatchedFullFidelity", "ApplyWatchedFullFidelityOverride",
    "ApplyDistanceLodPolicy", "DetectZoneTransition", "EvaluateLoopedGhostSpawn",
    "ReduceGhostFidelity", "RestoreGhostFidelity", "RestoreWatchedFullFidelityState",
    "ApplyDistanceLodFidelity", "IsProtectedGhost", "IsWatchProtectedRecording",
    "ComputeWatchLineageProtectionUntilUT", "IsLoopSyncedDebrisOfWatchedLineage",
    "IsDebrisDescendedFromWatchedLineage", "FindTreeById", "FindBranchPointById",
    "IsBoringEnvironment", "ComputeAutoLoopRange",
]

MOVABLE_KINDS = ("method", "type", "const")


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


def _at_line_start(text, i):
    """True when only whitespace precedes offset i on its line."""
    k = i - 1
    while k >= 0 and text[k] in " \t":
        k -= 1
    return k < 0 or text[k] == "\n"


def _skip_region_directive(text, i):
    """text[i] is a '#' that starts a line. Only #region / #endregion are supported: a
    directive's text is skipped like a line comment (a region name may hold parentheses)."""
    j = text.find("\n", i)
    line = text[i:len(text) if j < 0 else j]
    if not re.match(r"#(region|endregion)\b", line):
        raise SplitError("preprocessor directive %r is not supported (offset %d)" % (line.strip(), i))
    return len(text) if j < 0 else j


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
        if c == "#" and _at_line_start(body, i):
            i = _skip_region_directive(body, i)
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
    """Comments and region directives removed, string / char literal contents replaced by placeholders."""
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "#" and _at_line_start(text, i):
            i = _skip_region_directive(text, i)
            continue
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


_MODIFIER_KEYWORDS = frozenset([
    "public", "private", "internal", "protected", "static", "readonly", "new", "unsafe",
    "extern", "override", "virtual", "abstract", "sealed", "async", "partial", "ref", "const",
])


def _first_declaration_stop(decl):
    """Offset of the first '(', '=', ';' or '{' that ends a declaration head. A '(' that
    opens a tuple type (inside generic arguments, or not preceded by a name) is skipped
    with its matching ')', so ``List<(ulong key, float power)> Name(`` stops at ``Name(``."""
    angle = 0
    i = 0
    n = len(decl)
    while i < n:
        c = decl[i]
        if c == "<":
            angle += 1
        elif c == ">":
            angle -= 1
        elif c == "(":
            k = i - 1
            while k >= 0 and decl[k].isspace():
                k -= 1
            w = k
            while w >= 0 and (decl[w].isalnum() or decl[w] == "_"):
                w -= 1
            word = decl[w + 1:k + 1]
            named = k >= 0 and (decl[k] == ">" or (word != "" and word not in _MODIFIER_KEYWORDS))
            if angle == 0 and named:
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
        elif c in "=;{" and angle == 0:
            return i
        i += 1
    return n


def classify_member(chunk_text):
    decl = _strip_leading_attributes(mask_comments_and_literals(chunk_text))
    if not decl.strip():
        raise SplitError("empty member chunk")
    first = _first_declaration_stop(decl)
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
            return name, "constructor"
        return name, "method"
    if stop_char in ("=", ";"):
        if re.search(r"\bconst\b", head):
            return name, "const"
        return name, "field"
    if stop_char == "{":
        return name, "property"
    raise SplitError("cannot classify member: %r" % decl[:160])


def parse_file(text):
    lines = text.split("\n")
    trailing_newline = text.endswith("\n")
    if trailing_newline:
        lines = lines[:-1]
    decl_idx = [k for k, l in enumerate(lines) if l == CLASS_DECL]
    if len(decl_idx) != 1:
        raise SplitError("expected exactly one line %r, found %d" % (CLASS_DECL, len(decl_idx)))
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
    # Line start offsets of the body.
    starts = [0]
    for k, ch in enumerate(body):
        if ch == "\n":
            starts.append(k + 1)
    import bisect
    members = []
    prev_line = 0  # first body line index not yet assigned
    for off in ends:
        line_idx = bisect.bisect_right(starts, off - 1) - 1
        rest = body[off:starts[line_idx + 1] - 1] if line_idx + 1 < len(starts) else body[off:]
        rest_s = rest.strip()
        if rest_s and not rest_s.startswith("//"):
            raise SplitError("code after a member end on body line %d: %r"
                             % (line_idx + 1, body_lines[line_idx]))
        if line_idx < prev_line:
            # A second end on a line already closed (e.g. `int a; int b;` on one line).
            raise SplitError("two members end on body line %d: %r" % (line_idx + 1, body_lines[line_idx]))
        chunk = body_lines[prev_line:line_idx + 1]
        name, kind = classify_member("\n".join(chunk))
        members.append(Member(name, kind, prev_line, line_idx, chunk))
        prev_line = line_idx + 1
    tail = body_lines[prev_line:]
    if mask_comments_and_literals("\n".join(tail)).strip():
        raise SplitError("unparsed code after the last member: %r" % tail[:5])
    members, units, tail = split_directives(members, tail)
    return {
        "lines": lines,
        "trailing_newline": trailing_newline,
        "header": header,
        "footer": footer,
        "usings": usings,
        "namespace_line": namespace_line,
        "decl_index": d,
        "members": members,
        "units": units,
        "tail": tail,
        "body_offset": d + 2,
    }


def _is_directive(line):
    t = line.strip()
    return t.startswith("#region") or t.startswith("#endregion")


def _split_directive_prefix(chunk):
    """Split ``chunk`` into (directive units, rest). The prefix is every line up to and
    including the last region directive that is preceded only by blank lines and other
    directives; each directive becomes one unit holding the blank lines before it."""
    last = -1
    for k, line in enumerate(chunk):
        if _is_directive(line):
            last = k
        elif line.strip():
            break
    units = []
    pending = []
    for line in chunk[:last + 1]:
        pending.append(line)
        if _is_directive(line):
            units.append(("directive", pending))
            pending = []
    rest = list(chunk[last + 1:])
    for k, line in enumerate(rest):
        if _is_directive(line):
            raise SplitError("region directive inside a member chunk (after code or comments): %r" % line)
    return units, rest


def split_directives(members, tail):
    """Region directive lines are pulled out of the member chunks into their own units, so a
    ``#region`` / ``#endregion`` pair can be placed as a pair (see ``route_directives``).
    Pure line regrouping: no line is dropped, added or reordered."""
    units = []
    out_members = []
    for m in members:
        d_units, rest = _split_directive_prefix(m.lines)
        units.extend(d_units)
        moved = len(m.lines) - len(rest)
        m = m._replace(first_line=m.first_line + moved, lines=rest)
        units.append(("member", len(out_members)))
        out_members.append(m)
    d_units, tail = _split_directive_prefix(tail)
    units.extend(d_units)
    return out_members, units, tail


def route_directives(units, member_targets):
    """Target file of every unit. A member unit goes where the mapping sends it. A
    ``#region`` / ``#endregion`` pair goes, as a pair, to the file of the first member
    inside it (the main file when it holds none). Any set of pairs taken from a properly
    nested sequence is still properly nested, so every output stays balanced even when
    a region's members are spread over several files."""
    targets = [None] * len(units)
    stack = []
    for k, (kind, payload) in enumerate(units):
        if kind == "member":
            targets[k] = member_targets[payload]
            continue
        if payload[-1].strip().startswith("#region"):
            stack.append(k)
            continue
        if not stack:
            raise SplitError("#endregion without #region: %r" % payload[-1])
        open_k = stack.pop()
        dest = None
        for j in range(open_k + 1, k):
            if units[j][0] == "member":
                dest = member_targets[units[j][1]]
                break
        targets[open_k] = targets[k] = dest
    if stack:
        raise SplitError("%d #region line(s) never closed" % len(stack))
    return targets


def region_balance_error(lines):
    """None when every ``#region`` in ``lines`` is closed in order, else a message."""
    depth = 0
    for k, line in enumerate(lines):
        t = line.strip()
        if t.startswith("#endregion"):
            depth -= 1
            if depth < 0:
                return "#endregion without #region on line %d" % (k + 1)
        elif t.startswith("#region"):
            depth += 1
    if depth:
        return "%d #region line(s) left open" % depth
    return None


# ---------------------------------------------------------------------------
# Split
# ---------------------------------------------------------------------------

def assign(members):
    clash = sorted(set(FAMILIES) & set(EXISTING_SIBLINGS))
    if clash:
        raise SplitError("area name(s) collide with an existing sibling file: %s" % ", ".join(clash))
    name_to_family = {}
    for fam, (_, names) in FAMILIES.items():
        for nm in names:
            if nm in name_to_family:
                raise SplitError("member %s mapped twice (%s and %s)" % (nm, name_to_family[nm], fam))
            name_to_family[nm] = fam
    present = set(m.name for m in members)
    for nm in MAIN_MEMBERS:
        if nm in name_to_family:
            raise SplitError("member %s is both in MAIN_MEMBERS and mapped to %s" % (nm, name_to_family[nm]))
    stale = sorted(n for n in list(name_to_family) + MAIN_MEMBERS if n not in present)
    if stale:
        raise SplitError("mapping names members that no longer exist: %s (if most of the mapping is "
                         "listed, the file is already split: restore the unsplit file first, see the "
                         "docstring)" % ", ".join(stale))
    targets = []
    unmapped = []
    for m in members:
        fam = name_to_family.get(m.name)
        if fam is not None and m.kind not in MOVABLE_KINDS:
            raise SplitError("%s %s is mapped to %s, but static state (%s) must stay in the main file"
                             % (m.kind, m.name, fam, m.kind))
        if fam is None and m.kind in ("method", "type") and m.name not in MAIN_MEMBERS:
            unmapped.append(m.name)
        targets.append(fam)
    return targets, unmapped


def wrapper_lines(parsed, family):
    desc = FAMILIES[family][0]
    head = list(parsed["usings"])
    head += [parsed["namespace_line"], "{", "    // " + desc, CLASS_DECL, "    {"]
    return head, ["    }", "}"]


def unit_lines(parsed, unit):
    kind, payload = unit
    return list(parsed["members"][payload].lines) if kind == "member" else list(payload)


def build_outputs(parsed, targets):
    units = parsed["units"]
    unit_targets = route_directives(units, targets)
    per_file = collections.OrderedDict()
    per_file[None] = []
    for fam in FAMILIES:
        per_file[fam] = []
    last_index = {}
    for idx, (unit, fam) in enumerate(zip(units, unit_targets)):
        chunk = unit_lines(parsed, unit)
        out = per_file[fam]
        if not out:
            if fam is not None:
                while chunk and not chunk[0].strip():
                    chunk = chunk[1:]
        elif last_index.get(fam) != idx - 1 and chunk and chunk[0].strip() and out[-1].strip():
            # Not adjacent in the original: keep one blank line between units.
            out.append("")
        out.extend(chunk)
        last_index[fam] = idx
    outputs = collections.OrderedDict()
    main = list(parsed["header"])
    main += per_file[None] + parsed["tail"] + parsed["footer"]
    outputs[None] = main
    for fam in FAMILIES:
        if not per_file[fam]:
            continue
        head, foot = wrapper_lines(parsed, fam)
        outputs[fam] = head + per_file[fam] + foot
    return outputs


def verify(parsed, outputs):
    def nonblank(lines):
        return collections.Counter(l for l in lines if l.strip())

    expected = nonblank(parsed["lines"])
    for fam in outputs:
        if fam is None:
            continue
        head, foot = wrapper_lines(parsed, fam)
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
    # Each output must balance and, outside the main file, hold no static state.
    for fam, lines in outputs.items():
        text = "\n".join(lines) + "\n"
        sub = parse_file(text)
        region_error = region_balance_error(lines)
        if region_error:
            raise SplitError("verification: %s in %s" % (region_error, fam or "the main file"))
        for m in sub["members"]:
            if fam is not None and m.kind not in MOVABLE_KINDS:
                raise SplitError("verification: %s %s landed in %s" % (m.kind, m.name, fam))
    # Every original member is in exactly one output, in original order.
    total = sum(len(parse_file("\n".join(l) + "\n")["members"])
                for l in outputs.values())
    if total != len(parsed["members"]):
        raise SplitError("verification: member count %d != original %d" % (total, len(parsed["members"])))


def out_path(src, family):
    if family is None:
        return src
    base = os.path.splitext(src)[0]
    return "%s.%s.cs" % (base, family)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--src", default=DEFAULT_SRC, help="the unsplit GhostPlaybackLogic.cs (default: %(default)s)")
    ap.add_argument("--list", action="store_true", help="print the member table and exit")
    ap.add_argument("--check", action="store_true", help="split and verify, write nothing")
    args = ap.parse_args(argv)

    src = os.path.abspath(args.src)
    with open(src, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    if "\r" in text:
        raise SplitError("CRLF line endings are not supported; normalize first")
    if text.startswith("\ufeff"):
        raise SplitError("a BOM is not supported")
    parsed = parse_file(text)
    targets, unmapped = assign(parsed["members"])
    if args.list:
        off = parsed["body_offset"] + 1
        for m, fam in zip(parsed["members"], targets):
            print("%5d-%5d  %-11s %-16s %s" % (m.first_line + off, m.last_line + off, m.kind,
                                               fam or "(main)", m.name))
        return 0
    if not args.check:
        existing = [out_path(src, f) for f in FAMILIES if os.path.exists(out_path(src, f))]
        if existing:
            raise SplitError("partial files already exist; remove them first: %s" % ", ".join(existing))
    outputs = build_outputs(parsed, targets)
    verify(parsed, outputs)
    for fam, lines in outputs.items():
        path = out_path(src, fam)
        content = "\n".join(lines) + ("\n" if parsed["trailing_newline"] else "")
        print("%-60s %6d lines" % (os.path.relpath(path, REPO_ROOT), len(lines)))
        if not args.check:
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(content)
    if unmapped:
        print("unmapped members kept in the main file: %s" % ", ".join(sorted(set(unmapped))))
    print("verified: non-blank line multiset preserved; static fields all in the main file")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SplitError as exc:
        sys.stderr.write("split_ghostplaybacklogic: FAILED: %s\n" % exc)
        sys.exit(1)
