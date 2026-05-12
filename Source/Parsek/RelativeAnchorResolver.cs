using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal readonly struct AnchorPose
    {
        public readonly Vector3d WorldPos;
        public readonly Quaternion WorldRotation;
        public readonly int ResolvedSectionIndex;
        public readonly string ResolvedRecordingId;

        public AnchorPose(
            Vector3d worldPos,
            Quaternion worldRotation,
            int resolvedSectionIndex,
            string resolvedRecordingId = null)
        {
            WorldPos = worldPos;
            WorldRotation = TrajectoryMath.SanitizeQuaternion(worldRotation);
            ResolvedSectionIndex = resolvedSectionIndex;
            ResolvedRecordingId = resolvedRecordingId;
        }
    }

    internal delegate bool TryResolveOrbitalAnchorPose(
        Recording recording,
        TrackSection section,
        int sectionIndex,
        double ut,
        out AnchorPose pose);

    internal readonly struct RelativeAnchorResolverContext
    {
        public readonly string FocusRecordingId;
        public readonly string FocusTreeId;
        public readonly ReFlySessionMarker ActiveReFlyMarker;
        public readonly RecordingTree FocusTree;
        public readonly IReadOnlyDictionary<string, Recording> ProvisionalRecordings;
        public readonly RecordingTree PendingTree;
        public readonly Func<Recording, TrackSection, int, string> SectionAnchorRecordingIdResolver;
        public readonly Func<TrajectoryPoint, Vector3d> AbsoluteWorldPositionResolver;
        public readonly Func<TrajectoryPoint, Quaternion> BodyWorldRotationResolver;
        public readonly TryResolveOrbitalAnchorPose OrbitalCheckpointPoseResolver;
        public readonly Func<uint, string, double, (Vector3d pos, Quaternion rot)?> TryResolveLiveAnchorTransform;

        public RelativeAnchorResolverContext(
            RecordingTree focusTree,
            string focusRecordingId = null,
            string focusTreeId = null,
            ReFlySessionMarker activeReFlyMarker = null,
            IReadOnlyDictionary<string, Recording> provisionalRecordings = null,
            RecordingTree pendingTree = null,
            Func<Recording, TrackSection, int, string> sectionAnchorRecordingIdResolver = null,
            Func<TrajectoryPoint, Vector3d> absoluteWorldPositionResolver = null,
            Func<TrajectoryPoint, Quaternion> bodyWorldRotationResolver = null,
            TryResolveOrbitalAnchorPose orbitalCheckpointPoseResolver = null,
            Func<uint, string, double, (Vector3d pos, Quaternion rot)?> tryResolveLiveAnchorTransform = null)
        {
            FocusTree = focusTree;
            FocusRecordingId = focusRecordingId;
            FocusTreeId = !string.IsNullOrEmpty(focusTreeId)
                ? focusTreeId
                : focusTree?.Id;
            ActiveReFlyMarker = activeReFlyMarker;
            ProvisionalRecordings = provisionalRecordings;
            PendingTree = pendingTree;
            SectionAnchorRecordingIdResolver = sectionAnchorRecordingIdResolver;
            AbsoluteWorldPositionResolver = absoluteWorldPositionResolver;
            BodyWorldRotationResolver = bodyWorldRotationResolver;
            OrbitalCheckpointPoseResolver = orbitalCheckpointPoseResolver;
            TryResolveLiveAnchorTransform = tryResolveLiveAnchorTransform;
        }
    }

    internal static class RelativeAnchorResolver
    {
        internal const int RecordingAnchorChainFormatVersion =
            RecordingStore.RecordingAnchorChainFormatVersion;
        private const double UtEpsilon = 1e-6;
        internal const double SectionBoundaryEpsilonSeconds = 1e-9;
        private const double TerminalClampDoubleSlopSeconds = 1e-6;
        private const double HeadlessTestFixedDeltaTimeSeconds = 0.02;
        private static readonly bool RunningInHeadlessTestRunner = DetectHeadlessTestRunner();
        // Default applies when section cadence is unavailable; maximum clamps the
        // cadence-derived threshold so sparse recordings cannot broaden the fallback.
        private const double DefaultSmallSectionGapSeconds = 0.10;
        private const double MinimumSmallSectionGapSeconds = 0.05;
        private const double MaximumSmallSectionGapSeconds = 0.10;

        // Terminal anchor probes can arrive one physics tick after playback has
        // already held or retired the ghost at its final sample. This is one
        // fixed tick plus floating-point slop, not a generic gap tolerance.
        internal static double TerminalClampPhysicsTickSeconds =>
            ResolveFixedDeltaTimeSeconds();

        internal static double TerminalClampThresholdSeconds =>
            ResolveFixedDeltaTimeSeconds() + TerminalClampDoubleSlopSeconds;

        internal static bool TryResolveAnchorPose(
            RelativeAnchorResolverContext context,
            string anchorRecordingId,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    context.FocusRecordingId,
                    anchorRecordingId,
                    ut);
                return false;
            }

            HashSet<string> activeVisited = visited ?? new HashSet<string>(StringComparer.Ordinal);
            if (!activeVisited.Add(anchorRecordingId))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.AnchorCycleDetected,
                    "anchor-cycle-detected",
                    context.FocusRecordingId,
                    anchorRecordingId,
                    ut);
                return false;
            }

            try
            {
                if (!TryFindRecording(context, anchorRecordingId, out Recording recording, out string reason))
                {
                    failure = WarnUnresolved(
                        MapRecordingLookupFailureOutcome(reason),
                        reason,
                        context.FocusRecordingId,
                        anchorRecordingId,
                        ut);
                    return false;
                }

                if (!TryResolveActiveReFlyAnchorRecording(
                        context,
                        anchorRecordingId,
                        recording,
                        ut,
                        out recording))
                {
                    failure = WarnUnresolved(
                        RelativeAnchorResolveOutcome.AnchorOutOfScope,
                        "active-provisional-out-of-scope",
                        context.FocusRecordingId,
                        anchorRecordingId,
                        ut);
                    return false;
                }

                return TryResolveRecordingPose(context, recording, ut, activeVisited, out pose, out failure);
            }
            finally
            {
                activeVisited.Remove(anchorRecordingId);
            }
        }

        private static bool IsDebrisFocusRecording(RelativeAnchorResolverContext context)
        {
            return TryGetFocusRecording(context, out Recording focus)
                && focus != null
                && focus.IsDebris;
        }

        private static bool TryGetFocusRecording(
            RelativeAnchorResolverContext context,
            out Recording focus)
        {
            focus = null;
            if (string.IsNullOrWhiteSpace(context.FocusRecordingId))
                return false;

            if (context.ProvisionalRecordings != null
                && context.ProvisionalRecordings.TryGetValue(context.FocusRecordingId, out focus)
                && focus != null)
            {
                return true;
            }

            if (context.FocusTree?.Recordings != null
                && context.FocusTree.Recordings.TryGetValue(context.FocusRecordingId, out focus)
                && focus != null)
            {
                return true;
            }

            if (context.PendingTree?.Recordings != null
                && context.PendingTree.Recordings.TryGetValue(context.FocusRecordingId, out focus)
                && focus != null)
            {
                return true;
            }

            return false;
        }

        internal static bool TryResolveRecordingPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (recording == null)
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-null",
                    context.FocusRecordingId,
                    null,
                    ut);
                return false;
            }

            bool debrisFocusRecording = IsDebrisFocusRecording(context);
            if (recording.LoopAnchorVesselId != 0u
                && !debrisFocusRecording)
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.AnchorOutOfScope,
                    "loop-anchor-out-of-scope",
                    recording.RecordingId,
                    recording.RecordingId,
                    ut);
                return false;
            }
            if (recording.LoopAnchorVesselId != 0u)
            {
                ParsekLog.VerboseRateLimited(
                    "Anchor",
                    "loop-anchor-carveout-" + (recording.RecordingId ?? "(none)"),
                    $"loop-anchor-carveout-allowed: recordingId={recording.RecordingId ?? "(none)"} " +
                    $"focusRecordingId={context.FocusRecordingId ?? "(none)"} " +
                    $"loopAnchorVesselId={recording.LoopAnchorVesselId.ToString(CultureInfo.InvariantCulture)} " +
                    $"ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
            }

            if (recording.TrackSections != null && recording.TrackSections.Count > 0)
            {
                int sectionIndex = FindTrackSectionForUT(recording.TrackSections, ut);
                if (sectionIndex < 0)
                {
                    if (TryResolveSmallSectionGapPose(context, recording, ut, out pose, out failure))
                        return true;
                    if (failure.HasFailure)
                        return false;

                    if (TryResolveSameChainContinuationPose(
                            context,
                            recording,
                            ut,
                            visited,
                            out pose,
                            out failure))
                    {
                        return true;
                    }
                    if (failure.HasFailure)
                        return false;

                    if (TryResolveTerminalClampedPose(
                            context,
                            recording,
                            ut,
                            visited,
                            out pose,
                            out failure))
                    {
                        return true;
                    }
                    if (failure.HasFailure)
                        return false;

                    ResolveTrackSectionRange(
                        recording.TrackSections,
                        out double rangeStartUT,
                        out double rangeEndUT);
                    failure = WarnUnresolved(
                        RelativeAnchorResolveOutcome.NoSectionAtUT,
                        "anchor-out-of-recorded-range",
                        recording.RecordingId,
                        recording.RecordingId,
                        ut,
                        rangeStartUT: rangeStartUT,
                        rangeEndUT: rangeEndUT);
                    return false;
                }

                TrackSection section = recording.TrackSections[sectionIndex];
                switch (section.referenceFrame)
                {
                    case ReferenceFrame.Absolute:
                        return TryResolveAbsoluteSectionPose(
                            context,
                            recording,
                            section,
                            sectionIndex,
                            ut,
                            out pose,
                            out failure);
                    case ReferenceFrame.Relative:
                        return TryResolveRelativeSectionPose(
                            context,
                            recording,
                            section,
                            sectionIndex,
                            ut,
                            visited,
                            out pose,
                            out failure);
                    case ReferenceFrame.OrbitalCheckpoint:
                        return TryResolveOrbitalSectionPose(
                            context,
                            recording,
                            section,
                            sectionIndex,
                            ut,
                            out pose,
                            out failure);
                    default:
                        failure = WarnUnresolved(
                            RelativeAnchorResolveOutcome.Other,
                            "anchor-section-frame-unknown",
                            recording.RecordingId,
                            recording.RecordingId,
                            ut,
                            sectionIndex);
                        return false;
                }
            }

            if (recording.RecordingFormatVersion >= RecordingStore.RelativeLocalFrameFormatVersion)
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.TrackSectionsMissing,
                    "anchor-track-sections-missing",
                    recording.RecordingId,
                    recording.RecordingId,
                    ut);
                return false;
            }

            return TryResolveAbsoluteFramesPose(
                context,
                recording.Points,
                ut,
                resolvedSectionIndex: -1,
                resolvedRecordingId: recording.RecordingId,
                sectionStartUT: double.NaN,
                sectionEndUT: double.NaN,
                out pose,
                out failure);
        }

        private static bool TryResolveSameChainContinuationPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (!TryFindSameChainContinuationRecording(
                    context,
                    recording,
                    ut,
                    out Recording continuation))
            {
                return false;
            }

            string continuationId = continuation.RecordingId;
            if (string.IsNullOrEmpty(continuationId))
                return false;

            HashSet<string> activeVisited = visited ?? new HashSet<string>(StringComparer.Ordinal);
            if (!activeVisited.Add(continuationId))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.AnchorCycleDetected,
                    "anchor-cycle-detected",
                    recording.RecordingId,
                    continuationId,
                    ut);
                return false;
            }

            try
            {
                Recording resolvedContinuation = continuation;
                if (!TryResolveActiveReFlyAnchorRecording(
                        context,
                        continuationId,
                        continuation,
                        ut,
                        out resolvedContinuation))
                {
                    failure = WarnUnresolved(
                        RelativeAnchorResolveOutcome.AnchorOutOfScope,
                        "active-provisional-out-of-scope",
                        recording.RecordingId,
                        continuationId,
                        ut);
                    return false;
                }

                ParsekLog.VerboseRateLimited(
                    "RelativeAnchorResolver",
                    "anchor-chain-continuation|"
                        + (recording.RecordingId ?? "(none)") + "|"
                        + continuationId,
                    "Anchor recording continued through same-chain successor: "
                    + "recordingId=" + (recording.RecordingId ?? "(none)")
                    + " successorRecordingId=" + continuationId
                    + " chainId=" + (recording.ChainId ?? "(none)")
                    + " chainBranch=" + recording.ChainBranch.ToString(CultureInfo.InvariantCulture)
                    + " fromIndex=" + recording.ChainIndex.ToString(CultureInfo.InvariantCulture)
                    + " toIndex=" + continuation.ChainIndex.ToString(CultureInfo.InvariantCulture)
                    + " ut=" + ut.ToString("R", CultureInfo.InvariantCulture),
                    5.0);

                return TryResolveRecordingPose(
                    context,
                    resolvedContinuation,
                    ut,
                    activeVisited,
                    out pose,
                    out failure);
            }
            finally
            {
                activeVisited.Remove(continuationId);
            }
        }

        private static bool TryFindSameChainContinuationRecording(
            RelativeAnchorResolverContext context,
            Recording recording,
            double ut,
            out Recording continuation)
        {
            continuation = null;
            if (recording == null
                || string.IsNullOrEmpty(recording.ChainId)
                || recording.ChainIndex < 0)
            {
                return false;
            }

            int continuationPriority = -1;
            TryFindSameChainContinuationRecordingInTree(
                context.FocusTree,
                recording,
                ut,
                priority: 0,
                ref continuationPriority,
                ref continuation);

            if (context.PendingTree != null && PendingTreeIsInScope(context))
            {
                TryFindSameChainContinuationRecordingInTree(
                    context.PendingTree,
                    recording,
                    ut,
                    priority: 1,
                    ref continuationPriority,
                    ref continuation);
            }

            if (context.ProvisionalRecordings != null)
            {
                TryFindSameChainContinuationRecordingInMap(
                    context.ProvisionalRecordings,
                    recording,
                    ut,
                    requiredTreeId: ResolveContinuationScopeTreeId(context, recording),
                    priority: 2,
                    ref continuationPriority,
                    ref continuation);
            }

            return continuation != null;
        }

        private static void TryFindSameChainContinuationRecordingInTree(
            RecordingTree tree,
            Recording recording,
            double ut,
            int priority,
            ref int bestPriority,
            ref Recording best)
        {
            if (tree?.Recordings == null)
                return;

            TryFindSameChainContinuationRecordingInMap(
                tree.Recordings,
                recording,
                ut,
                requiredTreeId: null,
                priority: priority,
                ref bestPriority,
                ref best);
        }

        private static void TryFindSameChainContinuationRecordingInMap(
            IEnumerable<KeyValuePair<string, Recording>> recordings,
            Recording recording,
            double ut,
            string requiredTreeId,
            int priority,
            ref int bestPriority,
            ref Recording best)
        {
            if (recordings == null)
                return;

            foreach (KeyValuePair<string, Recording> pair in recordings)
            {
                Recording candidate = pair.Value;
                if (candidate == null)
                    continue;
                if (candidate.ChainIndex <= recording.ChainIndex)
                    continue;
                if (candidate.ChainBranch != recording.ChainBranch)
                    continue;
                if (!string.Equals(candidate.ChainId, recording.ChainId, StringComparison.Ordinal))
                    continue;
                if (!ContinuationMatchesRequiredTree(candidate, requiredTreeId))
                    continue;
                if (candidate.TrackSections == null || candidate.TrackSections.Count == 0)
                    continue;
                if (FindTrackSectionForUT(candidate.TrackSections, ut) < 0)
                    continue;
                // First chronological same-chain continuation covering the UT wins.
                // Priority only breaks ties when the same chain index exists in
                // multiple overlays, such as committed vs pending tree state.
                if (best == null
                    || candidate.ChainIndex < best.ChainIndex
                    || (candidate.ChainIndex == best.ChainIndex && priority > bestPriority))
                {
                    best = candidate;
                    bestPriority = priority;
                }
            }
        }

        private static string ResolveContinuationScopeTreeId(
            RelativeAnchorResolverContext context,
            Recording recording)
        {
            if (!string.IsNullOrEmpty(recording?.TreeId))
                return recording.TreeId;
            if (!string.IsNullOrEmpty(context.FocusTreeId))
                return context.FocusTreeId;
            if (!string.IsNullOrEmpty(context.ActiveReFlyMarker?.TreeId))
                return context.ActiveReFlyMarker.TreeId;
            return null;
        }

        private static bool ContinuationMatchesRequiredTree(
            Recording candidate,
            string requiredTreeId)
        {
            if (string.IsNullOrEmpty(requiredTreeId))
                return true;

            return candidate != null
                && !string.IsNullOrEmpty(candidate.TreeId)
                && string.Equals(candidate.TreeId, requiredTreeId, StringComparison.Ordinal);
        }

        private static double ResolveFixedDeltaTimeSeconds()
        {
            if (RunningInHeadlessTestRunner)
                return HeadlessTestFixedDeltaTimeSeconds;

            double fixedDeltaTime = ResolveUnityFixedDeltaTimeSeconds();
            if (IsFinite(fixedDeltaTime) && fixedDeltaTime > 0.0)
                return fixedDeltaTime;

            return HeadlessTestFixedDeltaTimeSeconds;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ResolveUnityFixedDeltaTimeSeconds()
        {
            // Keep Unity's ECall isolated so the headless xUnit runner can
            // choose the test fallback without JIT-compiling this accessor.
            return Time.fixedDeltaTime;
        }

        private static bool DetectHeadlessTestRunner()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(
                    assembly.GetName().Name,
                    "Parsek.Tests",
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindTrackSectionForUT(List<TrackSection> sections, double ut)
        {
            int strictIndex = TrajectoryMath.FindTrackSectionForUT(sections, ut);
            if (strictIndex >= 0)
                return strictIndex;

            if (sections == null || sections.Count == 0 || !IsFinite(ut))
                return -1;

            for (int i = 0; i < sections.Count; i++)
            {
                if (IsFinite(sections[i].startUT)
                    && Math.Abs(ut - sections[i].startUT) <= SectionBoundaryEpsilonSeconds)
                {
                    return i;
                }
            }

            for (int i = sections.Count - 1; i >= 0; i--)
            {
                if (IsFinite(sections[i].endUT)
                    && Math.Abs(ut - sections[i].endUT) <= SectionBoundaryEpsilonSeconds)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TryResolveTerminalClampedPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            double requestedUT,
            HashSet<string> visited,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (!TryFindTerminalClamp(
                    recording,
                    requestedUT,
                    out int sectionIndex,
                    out double clampedUT,
                    out double sectionEndUT,
                    out double terminalPlayableUT,
                    out double overshootSeconds,
                    out double thresholdSeconds))
            {
                return false;
            }

            TrackSection section = recording.TrackSections[sectionIndex];
            bool resolved;
            switch (section.referenceFrame)
            {
                case ReferenceFrame.Absolute:
                    resolved = TryResolveAbsoluteSectionPose(
                        context,
                        recording,
                        section,
                        sectionIndex,
                        clampedUT,
                        out pose,
                        out failure);
                    break;
                case ReferenceFrame.Relative:
                    resolved = TryResolveRelativeSectionPose(
                        context,
                        recording,
                        section,
                        sectionIndex,
                        clampedUT,
                        visited,
                        out pose,
                        out failure);
                    break;
                case ReferenceFrame.OrbitalCheckpoint:
                    resolved = TryResolveOrbitalSectionPose(
                        context,
                        recording,
                        section,
                        sectionIndex,
                        clampedUT,
                        out pose,
                        out failure);
                    break;
                default:
                    failure = WarnUnresolved(
                        RelativeAnchorResolveOutcome.Other,
                        "anchor-section-frame-unknown",
                        recording.RecordingId,
                        recording.RecordingId,
                        clampedUT,
                        sectionIndex);
                    resolved = false;
                    break;
            }

            if (!resolved)
                return false;

            LogTerminalClamp(
                context,
                recording,
                section,
                sectionIndex,
                requestedUT,
                clampedUT,
                sectionEndUT,
                terminalPlayableUT,
                overshootSeconds,
                thresholdSeconds);
            return true;
        }

        private static bool TryFindTerminalClamp(
            Recording recording,
            double requestedUT,
            out int sectionIndex,
            out double clampedUT,
            out double sectionEndUT,
            out double terminalPlayableUT,
            out double overshootSeconds,
            out double thresholdSeconds)
        {
            sectionIndex = -1;
            clampedUT = double.NaN;
            sectionEndUT = double.NaN;
            terminalPlayableUT = double.NaN;
            overshootSeconds = double.NaN;
            thresholdSeconds = TerminalClampThresholdSeconds;

            if (recording?.TrackSections == null
                || recording.TrackSections.Count == 0
                || !IsFinite(requestedUT)
                || !IsFinite(thresholdSeconds)
                || thresholdSeconds <= 0.0)
            {
                return false;
            }

            // The 1e-9 section-boundary lookup can return a terminal section
            // even when frame coverage later rejects it because
            // endUT > lastFrame.ut + UtEpsilon. This clamp is the separate
            // endpoint-playback path: one physics tick plus slop, and only
            // when the final playable sample is itself within that window.
            if (!TryFindFinalPlayableSection(
                    recording,
                    out sectionIndex,
                    out TrackSection section,
                    out terminalPlayableUT))
            {
                return false;
            }

            sectionEndUT = section.endUT;
            if (!IsFinite(sectionEndUT) || !IsFinite(terminalPlayableUT))
                return false;

            if (requestedUT <= sectionEndUT + SectionBoundaryEpsilonSeconds)
                return false;

            overshootSeconds = requestedUT - sectionEndUT;
            if (overshootSeconds < 0.0
                || overshootSeconds > thresholdSeconds + SectionBoundaryEpsilonSeconds)
            {
                return false;
            }

            double terminalSampleGapSeconds = sectionEndUT - terminalPlayableUT;
            if (terminalSampleGapSeconds < -SectionBoundaryEpsilonSeconds
                || terminalSampleGapSeconds > thresholdSeconds + SectionBoundaryEpsilonSeconds)
            {
                return false;
            }

            clampedUT = terminalPlayableUT;
            return true;
        }

        private static bool TryFindFinalPlayableSection(
            Recording recording,
            out int sectionIndex,
            out TrackSection section,
            out double terminalPlayableUT)
        {
            sectionIndex = -1;
            section = default;
            terminalPlayableUT = double.NaN;
            if (recording?.TrackSections == null)
                return false;

            for (int i = recording.TrackSections.Count - 1; i >= 0; i--)
            {
                TrackSection candidate = recording.TrackSections[i];
                if (!TryResolveTerminalPlayableUT(recording, candidate, out double candidatePlayableUT))
                    continue;

                sectionIndex = i;
                section = candidate;
                terminalPlayableUT = candidatePlayableUT;
                return true;
            }

            return false;
        }

        private static bool TryResolveTerminalPlayableUT(
            Recording recording,
            TrackSection section,
            out double terminalPlayableUT)
        {
            terminalPlayableUT = double.NaN;
            if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
            {
                if (section.checkpoints == null || section.checkpoints.Count == 0)
                    return false;

                terminalPlayableUT = section.checkpoints[section.checkpoints.Count - 1].endUT;
                return IsFinite(terminalPlayableUT);
            }

            if (section.frames != null && section.frames.Count > 0)
            {
                terminalPlayableUT = section.frames[section.frames.Count - 1].ut;
                return IsFinite(terminalPlayableUT);
            }

            if (section.referenceFrame == ReferenceFrame.Absolute
                && recording?.Points != null
                && recording.Points.Count > 0)
            {
                terminalPlayableUT = recording.Points[recording.Points.Count - 1].ut;
                return IsFinite(terminalPlayableUT);
            }

            return false;
        }

        private static void LogTerminalClamp(
            RelativeAnchorResolverContext context,
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double requestedUT,
            double clampedUT,
            double sectionEndUT,
            double terminalPlayableUT,
            double overshootSeconds,
            double thresholdSeconds)
        {
            string recordingId = recording?.RecordingId ?? "(none)";
            string anchorRecordingId = ResolveTerminalClampAnchorRecordingIdForLog(
                context,
                recording,
                section,
                sectionIndex);
            string key = "relative-anchor-terminal-clamp|"
                + recordingId + "|"
                + sectionIndex.ToString(CultureInfo.InvariantCulture);
            ParsekLog.VerboseRateLimited(
                "RelativeAnchorResolver",
                key,
                "relative-anchor-terminal-clamp: "
                + "recordingId=" + recordingId
                + " anchorRecordingId=" + anchorRecordingId
                + " sectionIndex=" + sectionIndex.ToString(CultureInfo.InvariantCulture)
                + " requestedUT=" + FormatDoubleR(requestedUT)
                + " clampedUT=" + FormatDoubleR(clampedUT)
                + " sectionEndUT=" + FormatDoubleR(sectionEndUT)
                + " terminalPlayableUT=" + FormatDoubleR(terminalPlayableUT)
                + " overshootSeconds=" + FormatDoubleR(overshootSeconds)
                + " thresholdSeconds=" + FormatDoubleR(thresholdSeconds),
                5.0);
        }

        private static string ResolveTerminalClampAnchorRecordingIdForLog(
            RelativeAnchorResolverContext context,
            Recording recording,
            TrackSection section,
            int sectionIndex)
        {
            if (section.referenceFrame == ReferenceFrame.Relative
                && TryResolveSectionAnchorRecordingId(
                    context,
                    recording,
                    section,
                    sectionIndex,
                    out string anchorRecordingId)
                && !string.IsNullOrEmpty(anchorRecordingId))
            {
                return anchorRecordingId;
            }

            return "(none)";
        }

        internal static bool TryResolveSectionAnchorRecordingId(
            RelativeAnchorResolverContext context,
            Recording recording,
            TrackSection section,
            int sectionIndex,
            out string anchorRecordingId)
        {
            anchorRecordingId = null;
            if (context.SectionAnchorRecordingIdResolver != null)
                anchorRecordingId = context.SectionAnchorRecordingIdResolver(recording, section, sectionIndex);

            if (string.IsNullOrWhiteSpace(anchorRecordingId))
                anchorRecordingId = section.anchorRecordingId;

            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                anchorRecordingId = null;
                return false;
            }

            anchorRecordingId = anchorRecordingId.Trim();
            return true;
        }

        private static bool TryResolveAbsoluteSectionPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            List<TrajectoryPoint> frames =
                section.frames != null && section.frames.Count > 0
                    ? section.frames
                    : recording.Points;
            if (frames == section.frames
                && !FrameListCoversUT(frames, section.startUT, section.endUT, ut))
            {
                List<TrajectoryPoint> flatFallbackFrames = recording.Points;
                if (TrajectoryTextSidecarCodec.TryBuildBodyFixedPrimaryFlatPointsForRelativeSections(
                        recording,
                        out List<TrajectoryPoint> safeRelativeFlatPoints))
                {
                    flatFallbackFrames = safeRelativeFlatPoints;
                }

                if (FrameListCoversUT(flatFallbackFrames, section.startUT, section.endUT, ut))
                {
                    frames = flatFallbackFrames;
                    ParsekLog.VerboseRateLimited(
                        "RelativeAnchorResolver",
                        "absolute-section-flat-fallback|"
                            + (recording.RecordingId ?? "(none)") + "|"
                            + sectionIndex.ToString(CultureInfo.InvariantCulture),
                        "Absolute section anchor pose fell back to flat trajectory coverage: "
                        + "recordingId=" + (recording.RecordingId ?? "(none)")
                        + " sectionIndex=" + sectionIndex.ToString(CultureInfo.InvariantCulture)
                        + " ut=" + ut.ToString("R", CultureInfo.InvariantCulture)
                        + " sectionStartUT=" + section.startUT.ToString("R", CultureInfo.InvariantCulture)
                        + " sectionEndUT=" + section.endUT.ToString("R", CultureInfo.InvariantCulture)
                        + " sectionFrameCount=" + (section.frames?.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                        + " flatFrameCount=" + flatFallbackFrames.Count.ToString(CultureInfo.InvariantCulture),
                        5.0);
                }
            }

            return TryResolveAbsoluteFramesPose(
                context,
                frames,
                ut,
                sectionIndex,
                recording.RecordingId,
                section.startUT,
                section.endUT,
                out pose,
                out failure);
        }

        private static bool TryResolveSmallSectionGapPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (recording?.TrackSections == null
                || recording.TrackSections.Count < 2
                || !IsFinite(ut))
            {
                return false;
            }

            if (!TryFindSmallSectionGap(
                    recording.TrackSections,
                    ut,
                    out int previousSectionIndex,
                    out int nextSectionIndex,
                    out double gapSeconds,
                    out double thresholdSeconds))
            {
                return false;
            }

            if (!TryGetSafeFlatPointsForSectionGap(recording, out List<TrajectoryPoint> flatPoints)
                || flatPoints == null
                || flatPoints.Count == 0)
            {
                return false;
            }

            if (!TryFindLocalFlatPointBracket(
                    flatPoints,
                    ut,
                    thresholdSeconds,
                    out TrajectoryPoint before,
                    out TrajectoryPoint after,
                    out float t))
            {
                return false;
            }

            if (!TryResolveAbsoluteBracketPose(
                    context,
                    recording.RecordingId,
                    before,
                    after,
                    t,
                    previousSectionIndex,
                    out pose,
                    out failure))
            {
                return false;
            }

            TrackSection previous = recording.TrackSections[previousSectionIndex];
            TrackSection next = recording.TrackSections[nextSectionIndex];
            ParsekLog.VerboseRateLimited(
                "RelativeAnchorResolver",
                "anchor-gap-flat-points-fallback|"
                    + (recording.RecordingId ?? "(none)") + "|"
                    + FormatDoubleR(previous.endUT) + "|"
                    + FormatDoubleR(next.startUT),
                "Anchor recording pose resolved from flat trajectory inside small section gap: "
                + "recordingId=" + (recording.RecordingId ?? "(none)")
                + " ut=" + FormatDoubleR(ut)
                + " previousSectionIndex=" + previousSectionIndex.ToString(CultureInfo.InvariantCulture)
                + " nextSectionIndex=" + nextSectionIndex.ToString(CultureInfo.InvariantCulture)
                + " previousEndUT=" + FormatDoubleR(previous.endUT)
                + " nextStartUT=" + FormatDoubleR(next.startUT)
                + " gapSeconds=" + FormatDoubleR(gapSeconds)
                + " thresholdSeconds=" + FormatDoubleR(thresholdSeconds)
                + " frameCount=" + flatPoints.Count.ToString(CultureInfo.InvariantCulture)
                + " bracketBeforeUT=" + FormatDoubleR(before.ut)
                + " bracketAfterUT=" + FormatDoubleR(after.ut),
                5.0);
            return true;
        }

        private static bool TryFindSmallSectionGap(
            List<TrackSection> sections,
            double ut,
            out int previousSectionIndex,
            out int nextSectionIndex,
            out double gapSeconds,
            out double thresholdSeconds)
        {
            previousSectionIndex = -1;
            nextSectionIndex = -1;
            gapSeconds = 0.0;
            thresholdSeconds = DefaultSmallSectionGapSeconds;

            for (int i = 0; i < sections.Count - 1; i++)
            {
                TrackSection previous = sections[i];
                TrackSection next = sections[i + 1];
                if (!IsFinite(previous.endUT) || !IsFinite(next.startUT))
                    continue;

                double gap = next.startUT - previous.endUT;
                if (gap <= UtEpsilon)
                    continue;

                double threshold = ResolveSmallSectionGapThreshold(previous, next);
                if (gap > threshold + UtEpsilon)
                    continue;

                if (ut < previous.endUT - UtEpsilon || ut > next.startUT + UtEpsilon)
                    continue;

                previousSectionIndex = i;
                nextSectionIndex = i + 1;
                gapSeconds = gap;
                thresholdSeconds = threshold;
                return true;
            }

            return false;
        }

        private static double ResolveSmallSectionGapThreshold(TrackSection previous, TrackSection next)
        {
            double cadenceSeconds = double.NaN;
            ConsiderSampleRate(previous.sampleRateHz, ref cadenceSeconds);
            ConsiderSampleRate(next.sampleRateHz, ref cadenceSeconds);

            if (!IsFinite(cadenceSeconds))
                return DefaultSmallSectionGapSeconds;

            return Math.Min(
                MaximumSmallSectionGapSeconds,
                Math.Max(MinimumSmallSectionGapSeconds, 3.0 * cadenceSeconds));
        }

        private static void ConsiderSampleRate(float sampleRateHz, ref double cadenceSeconds)
        {
            if (float.IsNaN(sampleRateHz) || float.IsInfinity(sampleRateHz) || sampleRateHz <= 0f)
                return;

            double candidate = 1.0 / sampleRateHz;
            // Adjacent sections can report different rates; use the slower
            // cadence so the gap threshold survives an asymmetric pair.
            if (!IsFinite(cadenceSeconds) || candidate > cadenceSeconds)
                cadenceSeconds = candidate;
        }

        private static bool TryGetSafeFlatPointsForSectionGap(
            Recording recording,
            out List<TrajectoryPoint> flatPoints)
        {
            flatPoints = null;
            if (recording == null)
                return false;

            if (RecordingContainsRelativeSections(recording))
                return TrajectoryTextSidecarCodec.TryBuildBodyFixedPrimaryFlatPointsForRelativeSections(recording, out flatPoints);

            if (!RecordingContainsOnlyAbsoluteSections(recording)
                || recording.Points == null
                || recording.Points.Count == 0)
            {
                return false;
            }

            flatPoints = recording.Points;
            return true;
        }

        private static bool RecordingContainsRelativeSections(Recording recording)
        {
            if (recording?.TrackSections == null)
                return false;

            for (int i = 0; i < recording.TrackSections.Count; i++)
            {
                if (recording.TrackSections[i].referenceFrame == ReferenceFrame.Relative)
                    return true;
            }

            return false;
        }

        private static bool RecordingContainsOnlyAbsoluteSections(Recording recording)
        {
            if (recording?.TrackSections == null || recording.TrackSections.Count == 0)
                return false;

            for (int i = 0; i < recording.TrackSections.Count; i++)
            {
                if (recording.TrackSections[i].referenceFrame != ReferenceFrame.Absolute)
                    return false;
            }

            return true;
        }

        private static bool TryFindLocalFlatPointBracket(
            List<TrajectoryPoint> points,
            double ut,
            double maxSpanSeconds,
            out TrajectoryPoint before,
            out TrajectoryPoint after,
            out float t)
        {
            before = default;
            after = default;
            t = 0f;
            if (points == null || points.Count == 0 || maxSpanSeconds < 0.0)
                return false;
            if (!FlatPointUTsAreFiniteAndMonotonic(points))
                return false;

            int afterIndex = FindFirstFlatPointAtOrAfterUT(points, ut);
            if (afterIndex < points.Count)
            {
                TrajectoryPoint point = points[afterIndex];
                if (Math.Abs(point.ut - ut) <= UtEpsilon)
                {
                    before = point;
                    after = point;
                    return true;
                }
            }

            int beforeIndex = afterIndex - 1;
            if (beforeIndex >= 0)
            {
                TrajectoryPoint point = points[beforeIndex];
                if (Math.Abs(point.ut - ut) <= UtEpsilon)
                {
                    before = point;
                    after = point;
                    return true;
                }
            }

            if (beforeIndex >= 0 && afterIndex < points.Count)
            {
                TrajectoryPoint candidateBefore = points[beforeIndex];
                TrajectoryPoint candidateAfter = points[afterIndex];
                if (!IsFinite(candidateBefore.ut) || !IsFinite(candidateAfter.ut))
                    return false;

                double span = candidateAfter.ut - candidateBefore.ut;
                if (span <= UtEpsilon || span > maxSpanSeconds + UtEpsilon)
                    return false;

                if (ut < candidateBefore.ut - UtEpsilon || ut > candidateAfter.ut + UtEpsilon)
                    return false;

                double rawT = (ut - candidateBefore.ut) / span;
                if (rawT < 0.0)
                    rawT = 0.0;
                else if (rawT > 1.0)
                    rawT = 1.0;

                before = candidateBefore;
                after = candidateAfter;
                t = (float)rawT;
                return true;
            }

            return false;
        }

        private static int FindFirstFlatPointAtOrAfterUT(List<TrajectoryPoint> points, double ut)
        {
            int low = 0;
            int high = points.Count;
            while (low < high)
            {
                int mid = low + ((high - low) / 2);
                double pointUT = points[mid].ut;
                if (IsFinite(pointUT) && pointUT < ut)
                    low = mid + 1;
                else
                    high = mid;
            }
            return low;
        }

        private static bool FlatPointUTsAreFiniteAndMonotonic(List<TrajectoryPoint> points)
        {
            double previousUT = double.NegativeInfinity;
            for (int i = 0; i < points.Count; i++)
            {
                double pointUT = points[i].ut;
                if (!IsFinite(pointUT) || pointUT < previousUT - UtEpsilon)
                    return false;
                previousUT = pointUT;
            }

            return true;
        }

        private static bool TryResolveAbsoluteBracketPose(
            RelativeAnchorResolverContext context,
            string resolvedRecordingId,
            TrajectoryPoint before,
            TrajectoryPoint after,
            float t,
            int resolvedSectionIndex,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (Math.Abs(before.ut - after.ut) <= UtEpsilon)
                return TryBuildAbsolutePoseFromPoint(
                    context,
                    before,
                    before.ut + (after.ut - before.ut) * t,
                    resolvedSectionIndex,
                    resolvedRecordingId,
                    out pose,
                    out failure);

            if (!TryResolveWorldPosition(context, before, out Vector3d beforeWorld)
                || !TryResolveWorldPosition(context, after, out Vector3d afterWorld))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.Other,
                    "absolute-position-unresolved",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    before.ut + (after.ut - before.ut) * t,
                    resolvedSectionIndex);
                return false;
            }

            // Match normal absolute interpolation: interpolate recorded
            // surface-relative rotation, and sample body rotation at the
            // bracket start. Gap fallback spans are capped at <= 0.10 s.
            Quaternion bodyRotation = ResolveBodyWorldRotation(context, before);
            Quaternion surfaceRotation = TrajectoryMath.PureSlerp(before.rotation, after.rotation, t);
            Quaternion worldRotation = TrajectoryMath.PureMultiply(bodyRotation, surfaceRotation);
            pose = new AnchorPose(
                Vector3d.Lerp(beforeWorld, afterWorld, t),
                worldRotation,
                resolvedSectionIndex,
                resolvedRecordingId);
            if (IsFinite(pose.WorldPos) && IsFinite(pose.WorldRotation))
                return true;

            pose = default;
            failure = WarnUnresolved(
                RelativeAnchorResolveOutcome.PoseNonFinite,
                "absolute-pose-nonfinite",
                resolvedRecordingId,
                resolvedRecordingId,
                before.ut + (after.ut - before.ut) * t,
                resolvedSectionIndex);
            return false;
        }

        internal static bool TryResolveRelativeSectionPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            AnchorPose parentPose;
            if (!TryResolveSectionAnchorRecordingId(
                    context,
                    recording,
                    section,
                    sectionIndex,
                    out string anchorRecordingId))
            {
                uint liveAnchorVesselId = section.anchorVesselId != 0u
                    ? section.anchorVesselId
                    : recording.LoopAnchorVesselId;
                if (liveAnchorVesselId != 0u
                    && IsDebrisFocusRecording(context)
                    && context.TryResolveLiveAnchorTransform != null)
                {
                    (Vector3d pos, Quaternion rot)? livePose =
                        context.TryResolveLiveAnchorTransform(
                            liveAnchorVesselId,
                            recording.RecordingId,
                            ut);
                    if (livePose.HasValue)
                    {
                        parentPose = new AnchorPose(
                            livePose.Value.pos,
                            livePose.Value.rot,
                            sectionIndex,
                            recording.RecordingId);
                    }
                    else
                    {
                        failure = WarnUnresolved(
                            RelativeAnchorResolveOutcome.AnchorOutOfScope,
                            "loop-live-anchor-unresolved",
                            recording.RecordingId,
                            null,
                            ut,
                            sectionIndex);
                        return false;
                    }
                }
                else
                {
                    string reason = recording.RecordingFormatVersion >= RecordingAnchorChainFormatVersion
                        ? "anchor-recording-id-missing"
                        : "legacy-anchor-recording-id-missing";
                    failure = WarnUnresolved(
                        RelativeAnchorResolveOutcome.Other,
                        reason,
                        recording.RecordingId,
                        null,
                        ut,
                        sectionIndex);
                    return false;
                }
            }
            else if (!TryResolveAnchorPose(
                    context,
                    anchorRecordingId,
                    ut,
                    visited,
                    out parentPose,
                    out failure))
            {
                return false;
            }

            if (!TryInterpolateRelativeFrame(
                    section.frames,
                    section.startUT,
                    section.endUT,
                    ut,
                    out double dx,
                    out double dy,
                    out double dz,
                    out Quaternion relativeRotation))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.OutOfSectionRange,
                    "anchor-out-of-recorded-range",
                    recording.RecordingId,
                    anchorRecordingId,
                    ut,
                    sectionIndex,
                    rangeStartUT: IsFinite(section.startUT) ? section.startUT : double.NaN,
                    rangeEndUT: IsFinite(section.endUT) ? section.endUT : double.NaN);
                return false;
            }

            Vector3d worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                parentPose.WorldPos,
                parentPose.WorldRotation,
                dx,
                dy,
                dz,
                recording.RecordingFormatVersion);
            Quaternion worldRotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                parentPose.WorldRotation,
                relativeRotation);

            if (!IsFinite(worldPos) || !IsFinite(worldRotation))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.PoseNonFinite,
                    "relative-pose-nonfinite",
                    recording.RecordingId,
                    anchorRecordingId,
                    ut,
                    sectionIndex);
                return false;
            }

            pose = new AnchorPose(worldPos, worldRotation, sectionIndex, recording.RecordingId);
            return true;
        }

        private static bool TryResolveOrbitalSectionPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (context.OrbitalCheckpointPoseResolver == null)
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.Other,
                    "orbital-pose-resolver-missing",
                    recording.RecordingId,
                    recording.RecordingId,
                    ut,
                    sectionIndex);
                return false;
            }

            if (!context.OrbitalCheckpointPoseResolver(
                    recording,
                    section,
                    sectionIndex,
                    ut,
                    out pose))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.Other,
                    "orbital-pose-unresolved",
                    recording.RecordingId,
                    recording.RecordingId,
                    ut,
                    sectionIndex);
                return false;
            }

            if (!IsFinite(pose.WorldPos) || !IsFinite(pose.WorldRotation))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.PoseNonFinite,
                    "orbital-pose-nonfinite",
                    recording.RecordingId,
                    recording.RecordingId,
                    ut,
                    sectionIndex);
                pose = default;
                return false;
            }

            return true;
        }

        private static bool TryResolveAbsoluteFramesPose(
            RelativeAnchorResolverContext context,
            List<TrajectoryPoint> frames,
            double ut,
            int resolvedSectionIndex,
            string resolvedRecordingId,
            double sectionStartUT,
            double sectionEndUT,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (!FrameListCoversUT(frames, sectionStartUT, sectionEndUT, ut))
            {
                ResolveFrameRange(
                    frames,
                    sectionStartUT,
                    sectionEndUT,
                    out double rangeStartUT,
                    out double rangeEndUT);
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.OutOfSectionRange,
                    "anchor-out-of-recorded-range",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    ut,
                    resolvedSectionIndex,
                    rangeStartUT,
                    rangeEndUT);
                return false;
            }

            if (frames.Count == 1)
                return TryBuildAbsolutePoseFromPoint(
                    context,
                    frames[0],
                    ut,
                    resolvedSectionIndex,
                    resolvedRecordingId,
                    out pose,
                    out failure);

            int cachedIndex = 0;
            if (!TrajectoryMath.InterpolatePoints(
                    frames,
                    ref cachedIndex,
                    ut,
                    out TrajectoryPoint before,
                    out TrajectoryPoint after,
                    out float t))
            {
                ResolveFrameRange(
                    frames,
                    sectionStartUT,
                    sectionEndUT,
                    out double rangeStartUT,
                    out double rangeEndUT);
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.OutOfSectionRange,
                    "anchor-out-of-recorded-range",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    ut,
                    resolvedSectionIndex,
                    rangeStartUT,
                    rangeEndUT);
                return false;
            }

            if (!TryResolveWorldPosition(context, before, out Vector3d beforeWorld)
                || !TryResolveWorldPosition(context, after, out Vector3d afterWorld))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.Other,
                    "absolute-position-unresolved",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    ut,
                    resolvedSectionIndex);
                return false;
            }

            Quaternion bodyRotation = ResolveBodyWorldRotation(context, before);
            Quaternion surfaceRotation = TrajectoryMath.PureSlerp(before.rotation, after.rotation, t);
            Quaternion worldRotation = TrajectoryMath.PureMultiply(bodyRotation, surfaceRotation);
            pose = new AnchorPose(
                Vector3d.Lerp(beforeWorld, afterWorld, t),
                worldRotation,
                resolvedSectionIndex,
                resolvedRecordingId);
            if (IsFinite(pose.WorldPos) && IsFinite(pose.WorldRotation))
                return true;

            pose = default;
            failure = WarnUnresolved(
                RelativeAnchorResolveOutcome.PoseNonFinite,
                "absolute-pose-nonfinite",
                resolvedRecordingId,
                resolvedRecordingId,
                ut,
                resolvedSectionIndex);
            return false;
        }

        private static bool TryBuildAbsolutePoseFromPoint(
            RelativeAnchorResolverContext context,
            TrajectoryPoint point,
            double requestedUT,
            int resolvedSectionIndex,
            string resolvedRecordingId,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (!TryResolveWorldPosition(context, point, out Vector3d worldPos))
            {
                failure = WarnUnresolved(
                    RelativeAnchorResolveOutcome.Other,
                    "absolute-position-unresolved",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    requestedUT,
                    resolvedSectionIndex);
                return false;
            }

            Quaternion bodyRotation = ResolveBodyWorldRotation(context, point);
            Quaternion surfaceRotation = TrajectoryMath.SanitizeQuaternion(point.rotation);
            Quaternion worldRotation = TrajectoryMath.PureMultiply(bodyRotation, surfaceRotation);
            pose = new AnchorPose(worldPos, worldRotation, resolvedSectionIndex, resolvedRecordingId);
            if (IsFinite(pose.WorldPos) && IsFinite(pose.WorldRotation))
                return true;

            pose = default;
            failure = WarnUnresolved(
                RelativeAnchorResolveOutcome.PoseNonFinite,
                "absolute-pose-nonfinite",
                resolvedRecordingId,
                resolvedRecordingId,
                requestedUT,
                resolvedSectionIndex);
            return false;
        }

        private static bool TryInterpolateRelativeFrame(
            List<TrajectoryPoint> frames,
            double sectionStartUT,
            double sectionEndUT,
            double ut,
            out double dx,
            out double dy,
            out double dz,
            out Quaternion relativeRotation)
        {
            dx = dy = dz = 0.0;
            relativeRotation = Quaternion.identity;
            if (frames == null || frames.Count == 0)
                return false;
            if (double.IsNaN(ut) || double.IsInfinity(ut))
                return false;
            if (frames.Count == 1)
            {
                if (!SingleFrameCoversUT(frames[0], sectionStartUT, sectionEndUT, ut))
                    return false;

                TrajectoryPoint point = frames[0];
                dx = point.latitude;
                dy = point.longitude;
                dz = point.altitude;
                relativeRotation = TrajectoryMath.SanitizeQuaternion(point.rotation);
                return true;
            }

            if (!PointListCoversUT(frames, ut))
                return false;

            int cachedIndex = 0;
            if (!TrajectoryMath.InterpolatePoints(
                    frames,
                    ref cachedIndex,
                    ut,
                    out TrajectoryPoint before,
                    out TrajectoryPoint after,
                    out float t))
            {
                return false;
            }

            dx = before.latitude + (after.latitude - before.latitude) * t;
            dy = before.longitude + (after.longitude - before.longitude) * t;
            dz = before.altitude + (after.altitude - before.altitude) * t;
            relativeRotation = TrajectoryMath.PureSlerp(before.rotation, after.rotation, t);
            return true;
        }

        private static bool TryInterpolateRelativeFrameWithBracket(
            List<TrajectoryPoint> frames,
            double sectionStartUT,
            double sectionEndUT,
            double ut,
            out double dx,
            out double dy,
            out double dz,
            out Quaternion relativeRotation,
            out TrajectoryPoint before,
            out TrajectoryPoint after,
            out float t)
        {
            dx = dy = dz = 0.0;
            relativeRotation = Quaternion.identity;
            before = default;
            after = default;
            t = 0f;
            if (frames == null || frames.Count == 0)
                return false;
            if (double.IsNaN(ut) || double.IsInfinity(ut))
                return false;
            if (frames.Count == 1)
            {
                if (!SingleFrameCoversUT(frames[0], sectionStartUT, sectionEndUT, ut))
                    return false;

                TrajectoryPoint point = frames[0];
                dx = point.latitude;
                dy = point.longitude;
                dz = point.altitude;
                relativeRotation = TrajectoryMath.SanitizeQuaternion(point.rotation);
                before = point;
                after = point;
                return true;
            }

            if (!PointListCoversUT(frames, ut))
                return false;

            int cachedIndex = 0;
            if (!TrajectoryMath.InterpolatePoints(
                    frames,
                    ref cachedIndex,
                    ut,
                    out before,
                    out after,
                    out t))
            {
                return false;
            }

            dx = before.latitude + (after.latitude - before.latitude) * t;
            dy = before.longitude + (after.longitude - before.longitude) * t;
            dz = before.altitude + (after.altitude - before.altitude) * t;
            relativeRotation = TrajectoryMath.PureSlerp(before.rotation, after.rotation, t);
            return true;
        }

        private static bool SingleFrameCoversUT(
            TrajectoryPoint point,
            double sectionStartUT,
            double sectionEndUT,
            double ut)
        {
            if (Math.Abs(point.ut - ut) <= UtEpsilon)
                return true;
            if (double.IsNaN(sectionStartUT)
                || double.IsNaN(sectionEndUT)
                || double.IsInfinity(sectionStartUT)
                || double.IsInfinity(sectionEndUT))
            {
                return false;
            }

            double start = Math.Min(sectionStartUT, sectionEndUT);
            double end = Math.Max(sectionStartUT, sectionEndUT);
            return ut >= start - UtEpsilon
                && ut <= end + UtEpsilon;
        }

        private static bool FrameListCoversUT(
            List<TrajectoryPoint> frames,
            double sectionStartUT,
            double sectionEndUT,
            double ut)
        {
            if (frames == null || frames.Count == 0)
                return false;
            if (double.IsNaN(ut) || double.IsInfinity(ut))
                return false;
            if (frames.Count == 1)
                return SingleFrameCoversUT(frames[0], sectionStartUT, sectionEndUT, ut);
            return PointListCoversUT(frames, ut);
        }

        private static bool PointListCoversUT(List<TrajectoryPoint> frames, double ut)
        {
            if (frames == null || frames.Count == 0)
                return false;
            if (double.IsNaN(ut) || double.IsInfinity(ut))
                return false;

            if (frames.Count == 1)
                return Math.Abs(frames[0].ut - ut) <= UtEpsilon;

            return ut >= frames[0].ut - UtEpsilon
                && ut <= frames[frames.Count - 1].ut + UtEpsilon;
        }

        private static bool TryResolveWorldPosition(
            RelativeAnchorResolverContext context,
            TrajectoryPoint point,
            out Vector3d worldPos)
        {
            worldPos = default;
            if (context.AbsoluteWorldPositionResolver == null)
                return false;

            worldPos = context.AbsoluteWorldPositionResolver(point);
            return IsFinite(worldPos);
        }

        private static Quaternion ResolveBodyWorldRotation(
            RelativeAnchorResolverContext context,
            TrajectoryPoint point)
        {
            if (context.BodyWorldRotationResolver == null)
                return Quaternion.identity;

            return TrajectoryMath.SanitizeQuaternion(context.BodyWorldRotationResolver(point));
        }

        private static bool TryFindRecording(
            RelativeAnchorResolverContext context,
            string recordingId,
            out Recording recording,
            out string reason)
        {
            recording = null;
            reason = "anchor-recording-not-found";

            if (context.ProvisionalRecordings != null
                && context.ProvisionalRecordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            if (context.FocusTree?.Recordings != null
                && context.FocusTree.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            if (context.PendingTree?.Recordings != null
                && context.PendingTree.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                if (PendingTreeIsInScope(context))
                    return true;

                recording = null;
                reason = "anchor-cross-tree-out-of-scope";
                return false;
            }

            return false;
        }

        private static bool PendingTreeIsInScope(RelativeAnchorResolverContext context)
        {
            if (context.PendingTree == null)
                return false;

            string pendingTreeId = context.PendingTree.Id;
            string markerTreeId = context.ActiveReFlyMarker?.TreeId;
            if (!string.IsNullOrEmpty(markerTreeId))
                return string.Equals(pendingTreeId, markerTreeId, StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(context.FocusTreeId))
                return string.Equals(pendingTreeId, context.FocusTreeId, StringComparison.Ordinal);

            return false;
        }

        private static bool TryResolveActiveReFlyAnchorRecording(
            RelativeAnchorResolverContext context,
            string recordingId,
            Recording recording,
            double ut,
            out Recording resolvedRecording)
        {
            resolvedRecording = recording;
            if (!IsActiveReFlyRecordingId(context, recordingId))
                return true;

            string sessionId = context.ActiveReFlyMarker?.SessionId;
            if (recording != null && recording.HasPreReFlyAnchorTrajectory(sessionId))
            {
                Recording frozen = recording.BuildPreReFlyAnchorTrajectoryRecording(sessionId);
                if (frozen != null)
                {
                    resolvedRecording = frozen;
                    var ic = CultureInfo.InvariantCulture;
                    ParsekLog.VerboseRateLimited(
                        "RelativeAnchorResolver",
                        "active-refly-anchor-frozen|" + (context.FocusRecordingId ?? "(none)") + "|" + recordingId,
                        "Using frozen pre-Re-Fly anchor trajectory: " +
                        $"recordingId={recordingId} focusRecordingId={context.FocusRecordingId ?? "(none)"} " +
                        $"sessionId={sessionId ?? "(none)"} ut={ut.ToString("F3", ic)}",
                        5.0);
                    return true;
                }
            }

            return false;
        }

        private static bool IsActiveReFlyRecordingId(
            RelativeAnchorResolverContext context,
            string recordingId)
        {
            return context.ActiveReFlyMarker != null
                && !string.IsNullOrEmpty(context.ActiveReFlyMarker.ActiveReFlyRecordingId)
                && string.Equals(
                    context.ActiveReFlyMarker.ActiveReFlyRecordingId,
                    recordingId,
                    StringComparison.Ordinal);
        }

        private static bool IsFinite(Vector3d v)
        {
            return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        }

        private static bool IsFinite(Quaternion q)
        {
            return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string FormatDoubleR(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static RelativeAnchorResolveOutcome MapRecordingLookupFailureOutcome(string reason)
        {
            // TryFindRecording currently emits only these two reasons. Add a
            // branch here when adding a new lookup failure reason.
            return string.Equals(reason, "anchor-cross-tree-out-of-scope", StringComparison.Ordinal)
                ? RelativeAnchorResolveOutcome.AnchorOutOfScope
                : RelativeAnchorResolveOutcome.AnchorRecordingNotFound;
        }

        private static void ResolveTrackSectionRange(
            List<TrackSection> sections,
            out double rangeStartUT,
            out double rangeEndUT)
        {
            rangeStartUT = double.NaN;
            rangeEndUT = double.NaN;
            if (sections == null || sections.Count == 0)
                return;

            TrackSection first = sections[0];
            TrackSection last = sections[sections.Count - 1];
            if (IsFinite(first.startUT))
                rangeStartUT = first.startUT;
            if (IsFinite(last.endUT))
                rangeEndUT = last.endUT;
        }

        private static void ResolveFrameRange(
            List<TrajectoryPoint> frames,
            double sectionStartUT,
            double sectionEndUT,
            out double rangeStartUT,
            out double rangeEndUT)
        {
            rangeStartUT = double.NaN;
            rangeEndUT = double.NaN;
            if (frames != null && frames.Count > 0)
            {
                rangeStartUT = frames[0].ut;
                rangeEndUT = frames[frames.Count - 1].ut;
            }
        }

        private static RelativeAnchorResolveFailure WarnUnresolved(
            RelativeAnchorResolveOutcome outcome,
            string reason,
            string recordingId,
            string anchorRecordingId,
            double ut,
            int sectionIndex = -1,
            double rangeStartUT = double.NaN,
            double rangeEndUT = double.NaN)
        {
            var ic = CultureInfo.InvariantCulture;
            string rec = string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId;
            string anchor = string.IsNullOrEmpty(anchorRecordingId) ? "(none)" : anchorRecordingId;
            string section = sectionIndex >= 0 ? sectionIndex.ToString(ic) : "(none)";
            string key = "relative-anchor-resolver|" + rec + "|" + anchor + "|" + reason + "|" + section;
            ParsekLog.WarnRateLimited(
                "RelativeAnchorResolver",
                key,
                "relative-anchor-unresolved: reason=" + reason +
                " recordingId=" + rec +
                " anchorRecordingId=" + anchor +
                " sectionIndex=" + section +
                " ut=" + ut.ToString("R", ic),
                5.0);
            return RelativeAnchorResolveFailure.Create(
                outcome,
                reason,
                recordingId,
                anchorRecordingId,
                ut,
                sectionIndex,
                rangeStartUT,
                rangeEndUT);
        }
    }
}
