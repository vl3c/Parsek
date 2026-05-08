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
            TryResolveOrbitalAnchorPose orbitalCheckpointPoseResolver = null)
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
        }
    }

    internal static class RelativeAnchorResolver
    {
        internal const int RecordingAnchorChainFormatVersion =
            RecordingStore.RecordingAnchorChainFormatVersion;
        private const double UtEpsilon = 1e-6;
        // Default applies when section cadence is unavailable; maximum clamps the
        // cadence-derived threshold so sparse recordings cannot broaden the fallback.
        private const double DefaultSmallSectionGapSeconds = 0.10;
        private const double MinimumSmallSectionGapSeconds = 0.05;
        private const double MaximumSmallSectionGapSeconds = 0.10;

        internal static bool TryResolveAnchorPose(
            RelativeAnchorResolverContext context,
            string anchorRecordingId,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose)
        {
            pose = default;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                WarnUnresolved(
                    "anchor-recording-id-missing",
                    context.FocusRecordingId,
                    anchorRecordingId,
                    ut);
                return false;
            }

            HashSet<string> activeVisited = visited ?? new HashSet<string>(StringComparer.Ordinal);
            if (!activeVisited.Add(anchorRecordingId))
            {
                WarnUnresolved(
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
                    WarnUnresolved(reason, context.FocusRecordingId, anchorRecordingId, ut);
                    return false;
                }

                if (!TryResolveActiveReFlyAnchorRecording(
                        context,
                        anchorRecordingId,
                        recording,
                        ut,
                        out recording))
                {
                    WarnUnresolved(
                        "active-provisional-out-of-scope",
                        context.FocusRecordingId,
                        anchorRecordingId,
                        ut);
                    return false;
                }

                return TryResolveRecordingPose(context, recording, ut, activeVisited, out pose);
            }
            finally
            {
                activeVisited.Remove(anchorRecordingId);
            }
        }

        internal static bool TryResolveRecordingPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose)
        {
            pose = default;
            if (recording == null)
            {
                WarnUnresolved("anchor-recording-null", context.FocusRecordingId, null, ut);
                return false;
            }

            if (recording.LoopAnchorVesselId != 0u)
            {
                WarnUnresolved(
                    "loop-anchor-out-of-scope",
                    recording.RecordingId,
                    recording.RecordingId,
                    ut);
                return false;
            }

            if (recording.TrackSections != null && recording.TrackSections.Count > 0)
            {
                int sectionIndex = TrajectoryMath.FindTrackSectionForUT(recording.TrackSections, ut);
                if (sectionIndex < 0)
                {
                    if (TryResolveSmallSectionGapPose(context, recording, ut, out pose))
                        return true;

                    if (TryResolveSameChainContinuationPose(
                            context,
                            recording,
                            ut,
                            visited,
                            out pose))
                    {
                        return true;
                    }

                    WarnUnresolved(
                        "anchor-out-of-recorded-range",
                        recording.RecordingId,
                        recording.RecordingId,
                        ut);
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
                            out pose);
                    case ReferenceFrame.Relative:
                        return TryResolveRelativeSectionPose(
                            context,
                            recording,
                            section,
                            sectionIndex,
                            ut,
                            visited,
                            out pose);
                    case ReferenceFrame.OrbitalCheckpoint:
                        return TryResolveOrbitalSectionPose(
                            context,
                            recording,
                            section,
                            sectionIndex,
                            ut,
                            out pose);
                    default:
                        WarnUnresolved(
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
                WarnUnresolved(
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
                out pose);
        }

        private static bool TryResolveSameChainContinuationPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose)
        {
            pose = default;
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
                WarnUnresolved(
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
                    WarnUnresolved(
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
                    out pose);
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
                if (TrajectoryMath.FindTrackSectionForUT(candidate.TrackSections, ut) < 0)
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
            out AnchorPose pose)
        {
            List<TrajectoryPoint> frames =
                section.frames != null && section.frames.Count > 0
                    ? section.frames
                    : recording.Points;
            if (frames == section.frames
                && !FrameListCoversUT(frames, section.startUT, section.endUT, ut))
            {
                List<TrajectoryPoint> flatFallbackFrames = recording.Points;
                if (TrajectoryTextSidecarCodec.TryBuildAbsoluteShadowFlatPointsForRelativeSections(
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
                out pose);
        }

        private static bool TryResolveSmallSectionGapPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            double ut,
            out AnchorPose pose)
        {
            pose = default;
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
                    out pose))
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
                return TrajectoryTextSidecarCodec.TryBuildAbsoluteShadowFlatPointsForRelativeSections(recording, out flatPoints);

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

            // This fallback only runs after section lookup misses inside a tiny
            // proven gap, so the simple scans keep the guard easy to audit.
            for (int i = 0; i < points.Count; i++)
            {
                TrajectoryPoint point = points[i];
                if (!IsFinite(point.ut))
                    continue;

                if (Math.Abs(point.ut - ut) <= UtEpsilon)
                {
                    before = point;
                    after = point;
                    return true;
                }
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                TrajectoryPoint candidateBefore = points[i];
                TrajectoryPoint candidateAfter = points[i + 1];
                if (!IsFinite(candidateBefore.ut) || !IsFinite(candidateAfter.ut))
                    continue;

                double span = candidateAfter.ut - candidateBefore.ut;
                if (span <= UtEpsilon || span > maxSpanSeconds + UtEpsilon)
                    continue;

                if (ut < candidateBefore.ut - UtEpsilon || ut > candidateAfter.ut + UtEpsilon)
                    continue;

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

        private static bool TryResolveAbsoluteBracketPose(
            RelativeAnchorResolverContext context,
            string resolvedRecordingId,
            TrajectoryPoint before,
            TrajectoryPoint after,
            float t,
            int resolvedSectionIndex,
            out AnchorPose pose)
        {
            pose = default;
            if (Math.Abs(before.ut - after.ut) <= UtEpsilon)
                return TryBuildAbsolutePoseFromPoint(
                    context,
                    before,
                    resolvedSectionIndex,
                    resolvedRecordingId,
                    out pose);

            if (!TryResolveWorldPosition(context, before, out Vector3d beforeWorld)
                || !TryResolveWorldPosition(context, after, out Vector3d afterWorld))
            {
                WarnUnresolved(
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
            return IsFinite(pose.WorldPos) && IsFinite(pose.WorldRotation);
        }

        internal static bool TryResolveRelativeSectionPose(
            RelativeAnchorResolverContext context,
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            HashSet<string> visited,
            out AnchorPose pose)
        {
            pose = default;
            if (!TryResolveSectionAnchorRecordingId(
                    context,
                    recording,
                    section,
                    sectionIndex,
                    out string anchorRecordingId))
            {
                string reason = recording.RecordingFormatVersion >= RecordingAnchorChainFormatVersion
                    ? "anchor-recording-id-missing"
                    : "legacy-anchor-recording-id-missing";
                WarnUnresolved(reason, recording.RecordingId, null, ut, sectionIndex);
                return false;
            }

            // Resolve the parent at the focus UT, then apply the interpolated
            // local relative frame in that rigid anchor frame.
            if (!TryResolveAnchorPose(context, anchorRecordingId, ut, visited, out AnchorPose parentPose))
                return false;

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
                WarnUnresolved(
                    "anchor-out-of-recorded-range",
                    recording.RecordingId,
                    anchorRecordingId,
                    ut,
                    sectionIndex);
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

            if (!IsFinite(worldPos))
            {
                WarnUnresolved(
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
            out AnchorPose pose)
        {
            pose = default;
            if (context.OrbitalCheckpointPoseResolver == null)
            {
                WarnUnresolved(
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
                WarnUnresolved(
                    "orbital-pose-unresolved",
                    recording.RecordingId,
                    recording.RecordingId,
                    ut,
                    sectionIndex);
                return false;
            }

            if (!IsFinite(pose.WorldPos) || !IsFinite(pose.WorldRotation))
            {
                WarnUnresolved(
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
            out AnchorPose pose)
        {
            pose = default;
            if (!FrameListCoversUT(frames, sectionStartUT, sectionEndUT, ut))
            {
                WarnUnresolved(
                    "anchor-out-of-recorded-range",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    ut,
                    resolvedSectionIndex);
                return false;
            }

            if (frames.Count == 1)
                return TryBuildAbsolutePoseFromPoint(
                    context,
                    frames[0],
                    resolvedSectionIndex,
                    resolvedRecordingId,
                    out pose);

            int cachedIndex = 0;
            if (!TrajectoryMath.InterpolatePoints(
                    frames,
                    ref cachedIndex,
                    ut,
                    out TrajectoryPoint before,
                    out TrajectoryPoint after,
                    out float t))
            {
                WarnUnresolved(
                    "anchor-out-of-recorded-range",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    ut,
                    resolvedSectionIndex);
                return false;
            }

            if (!TryResolveWorldPosition(context, before, out Vector3d beforeWorld)
                || !TryResolveWorldPosition(context, after, out Vector3d afterWorld))
            {
                WarnUnresolved(
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
            return IsFinite(pose.WorldPos) && IsFinite(pose.WorldRotation);
        }

        private static bool TryBuildAbsolutePoseFromPoint(
            RelativeAnchorResolverContext context,
            TrajectoryPoint point,
            int resolvedSectionIndex,
            string resolvedRecordingId,
            out AnchorPose pose)
        {
            pose = default;
            if (!TryResolveWorldPosition(context, point, out Vector3d worldPos))
            {
                WarnUnresolved(
                    "absolute-position-unresolved",
                    resolvedRecordingId,
                    resolvedRecordingId,
                    point.ut,
                    resolvedSectionIndex);
                return false;
            }

            Quaternion bodyRotation = ResolveBodyWorldRotation(context, point);
            Quaternion surfaceRotation = TrajectoryMath.SanitizeQuaternion(point.rotation);
            Quaternion worldRotation = TrajectoryMath.PureMultiply(bodyRotation, surfaceRotation);
            pose = new AnchorPose(worldPos, worldRotation, resolvedSectionIndex, resolvedRecordingId);
            return IsFinite(pose.WorldPos) && IsFinite(pose.WorldRotation);
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

            if (context.FocusTree?.Recordings != null
                && context.FocusTree.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            if (context.ProvisionalRecordings != null
                && context.ProvisionalRecordings.TryGetValue(recordingId, out recording)
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

        private static void WarnUnresolved(
            string reason,
            string recordingId,
            string anchorRecordingId,
            double ut,
            int sectionIndex = -1)
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
        }
    }
}
