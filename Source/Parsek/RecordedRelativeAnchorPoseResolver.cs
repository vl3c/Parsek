using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal static class RecordedRelativeAnchorPoseResolver
    {
        internal static bool TryResolveSectionAnchorPose(
            Recording focusRecording,
            TrackSection section,
            double ut,
            out AnchorPose pose)
        {
            return TryResolveSectionAnchorPose(
                focusRecording,
                section,
                ut,
                out pose,
                out _);
        }

        internal static bool TryResolveSectionAnchorPose(
            Recording focusRecording,
            TrackSection section,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default;
            failure = default;
            if (focusRecording == null)
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-null",
                    null,
                    null,
                    ut);
                return false;
            }
            if (section.referenceFrame != ReferenceFrame.Relative)
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-section-frame-unknown",
                    focusRecording.RecordingId,
                    null,
                    ut);
                return false;
            }
            if (string.IsNullOrWhiteSpace(section.anchorRecordingId))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    focusRecording.RecordingId,
                    section.anchorRecordingId,
                    ut);
                return false;
            }
            if (!TryBuildContext(focusRecording, out RelativeAnchorResolverContext context))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "focus-tree-missing",
                    focusRecording.RecordingId,
                    section.anchorRecordingId,
                    ut);
                return false;
            }

            return RelativeAnchorResolver.TryResolveAnchorPose(
                context,
                section.anchorRecordingId.Trim(),
                ut,
                new HashSet<string>(StringComparer.Ordinal),
                out pose,
                out failure);
        }

        internal static bool TryFindFocusRecording(
            IPlaybackTrajectory trajectory,
            out Recording recording)
        {
            recording = null;
            if (trajectory is Recording direct)
            {
                recording = direct;
                return true;
            }

            return TryFindRecordingById(trajectory?.RecordingId, out recording);
        }

        private static bool TryBuildContext(
            Recording focusRecording,
            out RelativeAnchorResolverContext context)
        {
            context = default;
            if (focusRecording == null || string.IsNullOrEmpty(focusRecording.RecordingId))
                return false;
            if (!TryFindFocusTree(focusRecording, out RecordingTree focusTree))
                return false;

            context = new RelativeAnchorResolverContext(
                focusTree,
                focusRecordingId: focusRecording.RecordingId,
                focusTreeId: focusTree?.Id,
                activeReFlyMarker: ParsekScenario.Instance?.ActiveReFlySessionMarker,
                pendingTree: RecordingStore.HasPendingTree ? RecordingStore.PendingTree : null,
                absoluteWorldPositionResolver: ResolveAbsoluteWorldPosition,
                bodyWorldRotationResolver: ResolveBodyWorldRotation,
                orbitalCheckpointPoseResolver: TryResolveOrbitalAnchorPose);
            return true;
        }

        private static bool TryFindFocusTree(
            Recording focusRecording,
            out RecordingTree focusTree)
        {
            focusTree = null;
            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings == null)
                        continue;
                    if ((!string.IsNullOrEmpty(focusRecording.TreeId)
                            && string.Equals(tree.Id, focusRecording.TreeId, StringComparison.Ordinal))
                        || tree.Recordings.ContainsKey(focusRecording.RecordingId))
                    {
                        focusTree = tree;
                        return true;
                    }
                }
            }

            RecordingTree pending = RecordingStore.HasPendingTree
                ? RecordingStore.PendingTree
                : null;
            if (pending?.Recordings != null
                && (pending.Recordings.ContainsKey(focusRecording.RecordingId)
                    || (!string.IsNullOrEmpty(focusRecording.TreeId)
                        && string.Equals(pending.Id, focusRecording.TreeId, StringComparison.Ordinal))))
            {
                focusTree = pending;
                return true;
            }

            return false;
        }

        private static bool TryFindRecordingById(
            string recordingId,
            out Recording recording)
        {
            recording = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.TryGetValue(recordingId, out recording)
                        && recording != null)
                    {
                        return true;
                    }
                }
            }

            RecordingTree pending = RecordingStore.HasPendingTree
                ? RecordingStore.PendingTree
                : null;
            if (pending?.Recordings != null
                && pending.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            // [ERS-exempt] Recorded-anchor playback resolves an exact
            // recording id for map/KSC diagnostics. ERS filtering would hide
            // NotCommitted active Re-Fly attempts, which are valid focus or
            // anchor records during the session.
            IReadOnlyList<Recording> committedRecordings = RecordingStore.CommittedRecordings;
            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording candidate = committedRecordings[i];
                    if (candidate != null
                        && string.Equals(candidate.RecordingId, recordingId, StringComparison.Ordinal))
                    {
                        recording = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static Vector3d ResolveAbsoluteWorldPosition(TrajectoryPoint point)
        {
            CelestialBody body = ResolveBody(point.bodyName);
            if (body == null)
                return new Vector3d(double.NaN, double.NaN, double.NaN);

            return body.GetWorldSurfacePosition(
                point.latitude,
                point.longitude,
                point.altitude);
        }

        private static Quaternion ResolveBodyWorldRotation(TrajectoryPoint point)
        {
            CelestialBody body = ResolveBody(point.bodyName);
            return body != null && body.bodyTransform != null
                ? body.bodyTransform.rotation
                : Quaternion.identity;
        }

        private static bool TryResolveOrbitalAnchorPose(
            Recording recording,
            TrackSection section,
            int sectionIndex,
            double ut,
            out AnchorPose pose)
        {
            pose = default;
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                OrbitSegment segment = section.checkpoints[i];
                if (ut < segment.startUT || ut > segment.endUT)
                    continue;

                if (!OrbitResolution.TryCreateOrbitFromSegment(
                        segment,
                        ResolveBody,
                        OrbitSegmentValidationMode.ValidateAndLog,
                        recording?.RecordingId,
                        "recorded-anchor",
                        out Orbit orbit,
                        out CelestialBody body,
                        out _))
                {
                    continue;
                }

                if (!OrbitResolution.TryComputeOrbitWorldPosition(
                        orbit,
                        body,
                        ut,
                        Vector3d.zero,
                        clampToSurface: true,
                        out OrbitPlacementResult placement,
                        out _))
                {
                    continue;
                }

                var rotation = ParsekFlight.ComputeOrbitalRotation(
                    segment,
                    orbit,
                    ut,
                    placement.Velocity,
                    placement.RawWorldPosition,
                    body.position,
                    Quaternion.identity,
                    sectionIndex,
                    TrajectoryMath.HasOrbitalFrameRotation(segment),
                    TrajectoryMath.IsSpinning(segment));

                pose = new AnchorPose(
                    placement.WorldPosition,
                    rotation.ghostRot,
                    sectionIndex,
                    recording?.RecordingId);
                return true;
            }

            return false;
        }

        private static CelestialBody ResolveBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;

            try
            {
                return FlightGlobals.Bodies?.Find(b => b != null && b.name == bodyName);
            }
            catch (TypeInitializationException)
            {
                return null;
            }
        }
    }
}
