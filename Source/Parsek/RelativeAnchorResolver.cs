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
            const double epsilon = 1e-6;
            if (Math.Abs(point.ut - ut) <= epsilon)
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
            return ut >= start - epsilon
                && ut <= end + epsilon;
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

            const double epsilon = 1e-6;
            if (frames.Count == 1)
                return Math.Abs(frames[0].ut - ut) <= epsilon;

            return ut >= frames[0].ut - epsilon
                && ut <= frames[frames.Count - 1].ut + epsilon;
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
