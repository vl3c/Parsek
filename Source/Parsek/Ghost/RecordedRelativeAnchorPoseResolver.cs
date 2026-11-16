using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Body-pose read seam for recorded-anchor playback. The live implementation
    /// walks <c>FlightGlobals.Bodies</c> and reads
    /// <c>CelestialBody.GetWorldSurfacePosition</c> / <c>bodyTransform.rotation</c>;
    /// injecting a deterministic stand-in makes the selection, interpolation, and
    /// RELATIVE-frame composition around it exercisable headless. Implementations
    /// return false when the body (or its transform) is unavailable; the caller
    /// then yields the same non-finite position / identity rotation the live path
    /// has always produced for an unknown body.
    /// </summary>
    internal interface IAnchorBodyPoseSource
    {
        bool TryGetSurfaceWorldPosition(
            string bodyName,
            double latitude,
            double longitude,
            double altitude,
            out Vector3d worldPos);

        bool TryGetBodyWorldRotation(string bodyName, out Quaternion rotation);
    }

    internal static class RecordedRelativeAnchorPoseResolver
    {
        private static readonly IAnchorBodyPoseSource LiveBodyPoseSource =
            new FlightGlobalsBodyPoseSource();

        /// <summary>
        /// Test-only override for <see cref="LiveBodyPoseSource"/>. Null in
        /// production; set by headless tests and cleared by
        /// <see cref="ResetForTesting"/>.
        /// </summary>
        internal static IAnchorBodyPoseSource BodyPoseSourceForTesting;

        private static IAnchorBodyPoseSource ActiveBodyPoseSource =>
            BodyPoseSourceForTesting ?? LiveBodyPoseSource;

        internal static void ResetForTesting()
        {
            BodyPoseSourceForTesting = null;
        }

        /// <summary>
        /// Resolves the recorded anchor's world pose for a RELATIVE section of
        /// <paramref name="focusRecording"/>. The focus tree and the pending
        /// tree come from <paramref name="catalog"/>; null reads the own
        /// timeline (<see cref="RecordingPlaybackCatalog.Own"/>).
        /// </summary>
        internal static bool TryResolveSectionAnchorPose(
            Recording focusRecording,
            TrackSection section,
            double ut,
            out AnchorPose pose,
            IPlaybackCatalog catalog = null)
        {
            return TryResolveSectionAnchorPose(
                focusRecording,
                section,
                ut,
                out pose,
                out _,
                catalog);
        }

        internal static bool TryResolveSectionAnchorPose(
            Recording focusRecording,
            TrackSection section,
            double ut,
            out AnchorPose pose,
            out RelativeAnchorResolveFailure failure,
            IPlaybackCatalog catalog = null)
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
            if (!TryBuildContext(
                    focusRecording,
                    catalog ?? RecordingPlaybackCatalog.Own,
                    out RelativeAnchorResolverContext context))
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

        /// <summary>
        /// The recording a trajectory plays: the trajectory itself when it is
        /// a <see cref="Recording"/>, else the recording with its id in
        /// <paramref name="catalog"/> (committed trees, then the pending tree,
        /// then the flat committed list). Null reads the own timeline.
        /// </summary>
        internal static bool TryFindFocusRecording(
            IPlaybackTrajectory trajectory,
            out Recording recording,
            IPlaybackCatalog catalog = null)
        {
            recording = null;
            if (trajectory is Recording direct)
            {
                recording = direct;
                return true;
            }

            return (catalog ?? RecordingPlaybackCatalog.Own).TryFindRecording(
                trajectory?.RecordingId,
                CatalogRecordingScope.TreesThenPendingThenList,
                out recording);
        }

        private static bool TryBuildContext(
            Recording focusRecording,
            IPlaybackCatalog catalog,
            out RelativeAnchorResolverContext context)
        {
            context = default;
            if (focusRecording == null || string.IsNullOrEmpty(focusRecording.RecordingId))
                return false;
            if (!catalog.TryFindFocusTree(
                    focusRecording.RecordingId,
                    focusRecording.TreeId,
                    out RecordingTree focusTree))
                return false;

            context = new RelativeAnchorResolverContext(
                focusTree,
                focusRecordingId: focusRecording.RecordingId,
                focusTreeId: focusTree?.Id,
                activeReFlyMarker: ParsekScenario.Instance?.ActiveReFlySessionMarker,
                pendingTree: catalog.PendingTree,
                absoluteWorldPositionResolver: ResolveAbsoluteWorldPosition,
                bodyWorldRotationResolver: ResolveBodyWorldRotation,
                orbitalCheckpointPoseResolver: ParsekFlight.TryResolveOrbitalCheckpointAnchorPose,
                // Logistics route live-anchor bind (Step 1): map-presence / KSC
                // playback binds the anchor pose to the live launch-matched vessel
                // when its craft is loaded, so a looped delivery ghost tracks the
                // live station rather than its recorded Mun position. The delegate
                // is FLIGHT/MAP/TS-safe and guid-gated; null headless.
                tryResolveLiveLaunchMatchedAnchorPose:
                    ParsekFlight.TryResolveLiveLaunchMatchedAnchorPoseForResolver);
            return true;
        }

        /// <summary>
        /// Absolute-frame world position for a recorded point. ONLY valid for
        /// Absolute / body-fixed points: in a RELATIVE section the
        /// latitude / longitude / altitude fields carry anchor-local Cartesian
        /// METRE offsets, and feeding those to a surface-position read puts the
        /// ghost deep inside the planet. Relative sections resolve through
        /// <c>RelativeAnchorResolver.TryResolveRelativeSectionPose</c> instead.
        /// </summary>
        internal static Vector3d ResolveAbsoluteWorldPosition(TrajectoryPoint point)
        {
            return ActiveBodyPoseSource.TryGetSurfaceWorldPosition(
                point.bodyName,
                point.latitude,
                point.longitude,
                point.altitude,
                out Vector3d worldPos)
                ? worldPos
                : new Vector3d(double.NaN, double.NaN, double.NaN);
        }

        internal static Quaternion ResolveBodyWorldRotation(TrajectoryPoint point)
        {
            return ActiveBodyPoseSource.TryGetBodyWorldRotation(point.bodyName, out Quaternion rotation)
                ? rotation
                : Quaternion.identity;
        }

        /// <summary>
        /// Live body-pose read. Behaviour-identical to the inline FlightGlobals
        /// reads this seam replaced: an unresolvable body yields false so the
        /// caller returns NaN / identity.
        /// </summary>
        private sealed class FlightGlobalsBodyPoseSource : IAnchorBodyPoseSource
        {
            public bool TryGetSurfaceWorldPosition(
                string bodyName,
                double latitude,
                double longitude,
                double altitude,
                out Vector3d worldPos)
            {
                worldPos = default;
                CelestialBody body = ResolveBody(bodyName);
                if (body == null)
                    return false;

                worldPos = body.GetWorldSurfacePosition(latitude, longitude, altitude);
                return true;
            }

            public bool TryGetBodyWorldRotation(string bodyName, out Quaternion rotation)
            {
                rotation = Quaternion.identity;
                CelestialBody body = ResolveBody(bodyName);
                if (body == null || body.bodyTransform == null)
                    return false;

                rotation = body.bodyTransform.rotation;
                return true;
            }
        }

        // Same lookup as ParsekFlight.FindBodyForAnchorResolver, kept here because the nested
        // pose source reads it: a call from that type to ParsekFlight would add a backward
        // Ghost -> Controllers type pair to the architecture graph.
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
