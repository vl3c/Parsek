using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        /// <summary>
        /// Tracing-only expected-attitude source for <see cref="GhostRenderTrace"/>'s
        /// <c>AfterUpdate</c> line (<c>dRotDeg=</c> / <c>rotRef=</c>). Resolves the world
        /// rotation the RECORDING implies at <paramref name="playbackUT"/>, independently of
        /// the positioner's per-ghost state (cached bracket index, orbit cache, the ghost's
        /// current transform), and through the SAME production conventions the positioner
        /// uses for the section's frame:
        /// <list type="bullet">
        /// <item>OrbitalCheckpoint / orbit segment: <see cref="ComputeOrbitalRotation"/> over a
        /// freshly built <see cref="Orbit"/>, with the radial taken from the orbit's own
        /// position at the UT (not the rendered, anchor-corrected position).</item>
        /// <item>Absolute points (flat or section frames, incl. the bridged playback frames):
        /// <c>body.bodyTransform.rotation * ShiftSurfaceRelativeRotation(slerp(srfRel))</c>.</item>
        /// <item>Body-fixed primary (parent-anchored debris): the same surface decode over
        /// <c>bodyFixedFrames</c>.</item>
        /// <item>Recorded-anchor RELATIVE: <see cref="TrajectoryMath.ResolveRelativePlaybackRotation"/>
        /// over the recorded anchor pose. Live-anchor (loop) RELATIVE is not resolved.</item>
        /// </list>
        /// Conventions are shared with the positioner BY DESIGN (the H9 frame probes guard
        /// them against live vessel attitude); what this residual measures is the data
        /// selection and everything that happens to the transform after the decode. Only
        /// called while ghost render tracing is on and a line is actually being written.
        /// </summary>
        private bool TryResolveRecordedAttitudeForTrace(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            GhostRenderTrace.RenderSurface surface,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = "unresolved";
            if (traj == null || double.IsNaN(playbackUT) || double.IsInfinity(playbackUT))
                return false;

            try
            {
                return TryResolveRecordedAttitudeForTraceCore(
                    traj, state, playbackUT, surface, out expected, out rotRef);
            }
            catch (Exception ex)
            {
                // Diagnostic-only path: never let the tracer disturb playback.
                expected = Quaternion.identity;
                rotRef = "resolver-error";
                ParsekLog.VerboseRateLimited(
                    "GhostRenderTrace",
                    "attitude-resolver-error",
                    "Attitude residual resolver threw " + ex.GetType().Name + ": " + ex.Message,
                    5.0);
                return false;
            }
        }

        private bool TryResolveRecordedAttitudeForTraceCore(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            GhostRenderTrace.RenderSurface surface,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = "unresolved";
            double shiftSeconds = state != null ? state.bodyFixedShiftSeconds : 0.0;
            List<TrackSection> sections = traj.TrackSections;
            int sectionIdx = sections != null && sections.Count > 0
                ? TrajectoryMath.FindTrackSectionForUT(sections, playbackUT)
                : -1;
            bool hasSection = sectionIdx >= 0;
            TrackSection section = hasSection ? sections[sectionIdx] : default(TrackSection);

            // 1. OrbitalCheckpoint sections (the positioner's first dispatch).
            if (hasSection && HasRenderableCheckpointTrackSection(section))
            {
                if (TryFindCheckpointOrbitSegment(section, playbackUT, out OrbitSegment checkpoint, out _))
                    return TryResolveOrbitAttitudeForTrace(
                        checkpoint, playbackUT, "checkpoint-", out expected, out rotRef);
                return TryResolveSurfaceAttitudeForTrace(
                    section.frames, playbackUT, shiftSeconds, "checkpoint-points", out expected, out rotRef);
            }

            // 2. Body-fixed primary surface (parent-anchored debris).
            if (surface == GhostRenderTrace.RenderSurface.BodyFixedPrimary)
            {
                if (!hasSection || section.bodyFixedFrames == null)
                {
                    rotRef = "body-fixed-missing";
                    return false;
                }
                return TryResolveSurfaceAttitudeForTrace(
                    section.bodyFixedFrames, playbackUT, shiftSeconds, "body-fixed", out expected, out rotRef);
            }

            // 3. RELATIVE sections: recorded anchor only.
            if (hasSection && section.referenceFrame == ReferenceFrame.Relative)
                return TryResolveRelativeAttitudeForTrace(
                    traj, sectionIdx, section, playbackUT, out expected, out rotRef);

            // 4. Absolute section frames (with the same boundary bridging the positioner uses).
            if (hasSection && TryGetAbsoluteSectionPlaybackFrames(section, out List<TrajectoryPoint> sectionFrames))
            {
                if (TryGetAbsoluteSectionPlaybackFramesForPlayback(
                        sections, sectionIdx, out List<TrajectoryPoint> bridged, out _))
                    sectionFrames = bridged;
                return TryResolveSurfaceAttitudeForTrace(
                    sectionFrames, playbackUT, shiftSeconds, "surface", out expected, out rotRef);
            }

            // 5. Flat recording: orbit segments first unless the UT is a surface span,
            // then flat points (the orbit-aware InterpolateAndPosition overload's order).
            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(sections, playbackUT);
            if (!surfaceSkip && traj.OrbitSegments != null && traj.OrbitSegments.Count > 0)
            {
                OrbitSegment? seg = FindOrbitSegment(traj.OrbitSegments, playbackUT);
                if (seg.HasValue && TrajectoryMath.HasUsableOrbitSegmentElements(seg.Value))
                    return TryResolveOrbitAttitudeForTrace(
                        seg.Value, playbackUT, "", out expected, out rotRef);
            }

            return TryResolveSurfaceAttitudeForTrace(
                traj.Points, playbackUT, shiftSeconds, "points", out expected, out rotRef);
        }

        private static bool TryResolveOrbitAttitudeForTrace(
            OrbitSegment segment,
            double ut,
            string refPrefix,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);
            rotRef = refPrefix + (spinning ? "orbit-spin" : hasOfr ? "orbit-ofr" : "orbit-prograde");

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
            {
                rotRef = refPrefix + "orbit-no-body";
                return false;
            }
            Orbit orbit = MapRenderProbe.BuildOrbitFromSegment(segment, body);
            if (orbit == null)
            {
                rotRef = refPrefix + "orbit-unbuildable";
                return false;
            }

            Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);
            Vector3d worldPos = orbit.getPositionAtUT(ut);
            if (!spinning && velocity.sqrMagnitude <= 0.001)
            {
                // ComputeOrbitalRotation keeps the ghost's current rotation here: the
                // recording implies nothing to compare against.
                rotRef = refPrefix + "orbit-degenerate";
                return false;
            }

            // currentRotation is only returned on the degenerate branch excluded above,
            // and cacheKey only tags a rate-limited near-parallel log line.
            var (ghostRot, _) = ComputeOrbitalRotation(
                segment, orbit, ut, velocity, worldPos, body.position,
                Quaternion.identity, -1L, hasOfr, spinning);
            expected = ghostRot;
            return true;
        }

        private static bool TryResolveSurfaceAttitudeForTrace(
            List<TrajectoryPoint> points,
            double ut,
            double shiftSeconds,
            string refToken,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = refToken;
            if (points == null || points.Count == 0)
            {
                rotRef = refToken + "-empty";
                return false;
            }

            int cachedIndex = 0;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, ut, out TrajectoryPoint before, out TrajectoryPoint after, out float t);
            Quaternion srfRel = hasSegment && !(t == 0f && before.ut == after.ut)
                ? TrajectoryMath.SanitizeQuaternion(Quaternion.Slerp(before.rotation, after.rotation, t))
                : TrajectoryMath.SanitizeQuaternion(before.rotation);

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            if (body == null)
            {
                rotRef = refToken + "-no-body";
                return false;
            }

            expected = body.bodyTransform.rotation
                * TrajectoryMath.FrameTransform.ShiftSurfaceRelativeRotation(srfRel, shiftSeconds, body);
            return true;
        }

        private bool TryResolveRelativeAttitudeForTrace(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            double ut,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = "relative";
            if (string.IsNullOrEmpty(section.anchorRecordingId)
                || GhostPlaybackLogic.ShouldUseLoopAnchor(traj))
            {
                rotRef = "relative-live-anchor";
                return false;
            }
            List<TrajectoryPoint> frames = section.frames != null ? section.frames : traj.Points;
            if (frames == null || frames.Count == 0)
            {
                rotRef = "relative-empty";
                return false;
            }

            int cachedIndex = 0;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                frames, ref cachedIndex, ut, out TrajectoryPoint before, out TrajectoryPoint after, out float t);
            Quaternion localRot = hasSegment
                ? TrajectoryMath.SanitizeQuaternion(Quaternion.Slerp(before.rotation, after.rotation, t))
                : TrajectoryMath.SanitizeQuaternion(before.rotation);

            if (!TryResolveRecordedRelativeAnchorPose(
                    traj.RecordingId,
                    section.anchorRecordingId,
                    sectionIdx,
                    ut,
                    out RelativeAnchorPose anchorPose,
                    out _))
            {
                rotRef = "relative-anchor-unresolved";
                return false;
            }

            expected = TrajectoryMath.ResolveRelativePlaybackRotation(anchorPose.worldRotation, localRot);
            return true;
        }
    }
}
