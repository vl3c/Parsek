using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // The debris body-fixed trace and the attitude-for-trace family.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        /// <summary>
        /// Trace-Sep playback row for a parent-anchored debris ghost positioned from its
        /// body-fixed primary: bracketing samples, lerp alpha, predicted vs rendered world
        /// position and the rendered / recorded parent distances. Called only while ghost
        /// render tracing is on and the playback window is open.
        /// </summary>
        private void TraceDebrisBodyFixedPrimaryPlayback(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            RelativeSectionPlaybackTarget target,
            int bodyFixedCount,
            bool firstPlaybackRender,
            Vector3d ghostWorldBefore)
        {
            Vector3d ghostWorld = state.ghost.transform != null
                ? (Vector3d)state.ghost.transform.position : Vector3d.zero;
            FindTraceBracketFrames(
                target.Section.bodyFixedFrames, playbackUT,
                out TrajectoryPoint? beforeFrame, out TrajectoryPoint? afterFrame);
            string beforeStr = FormatTraceBracketFrame(beforeFrame);
            string afterStr = FormatTraceBracketFrame(afterFrame);
            // Lerp alpha + manually-predicted ghost world position. The
            // lerp alpha is what InterpolateAndPosition computed
            // implicitly from playbackUT / bracket UTs; we reconstruct it
            // here so the log row carries the interpolation factor
            // explicitly. The predicted world position is a body-fixed
            // linear blend of the bracketing LLAs (each resolved via
            // body.GetWorldSurfacePosition independently then linearly
            // blended in world-space). If `ghostWorld` differs from
            // `predictedWorld` by more than rounding, the discrepancy is
            // playback-math; if they match but the ghost is still
            // slipping forward relative to truth, the discrepancy lives
            // in the recorded LLAs themselves (recorder-side phase
            // offset).
            double lerpAlpha = double.NaN;
            Vector3d predictedWorld = Vector3d.zero;
            if (beforeFrame.HasValue && afterFrame.HasValue)
            {
                lerpAlpha = TraceSeparation.ComputeLerpAlpha(
                    beforeFrame.Value.ut, afterFrame.Value.ut, playbackUT);
                CelestialBody body = !string.IsNullOrEmpty(beforeFrame.Value.bodyName)
                    ? FindBodyByNameOrNull(FlightGlobals.Bodies, beforeFrame.Value.bodyName)
                    : null;
                if (body != null && !double.IsNaN(lerpAlpha))
                {
                    Vector3d worldBefore = body.GetWorldSurfacePosition(
                        beforeFrame.Value.latitude,
                        beforeFrame.Value.longitude,
                        beforeFrame.Value.altitude);
                    Vector3d worldAfter = body.GetWorldSurfacePosition(
                        afterFrame.Value.latitude,
                        afterFrame.Value.longitude,
                        afterFrame.Value.altitude);
                    predictedWorld = worldBefore + (worldAfter - worldBefore) * lerpAlpha;
                }
            }
            Vector3d worldStep = ghostWorld - ghostWorldBefore;
            Vector3d predictedVsActual = ghostWorld - predictedWorld;
            // Parent ghost world position (rendered) so we can log the
            // RENDERED parent-vs-debris distance on screen. Looked up via
            // the engine's recordingId mapping; returns false when the
            // parent ghost hasn't materialized yet (in which case the
            // distance fields stay NaN in the log).
            Vector3d parentGhostWorld = Vector3d.zero;
            bool parentGhostFound = host.engine != null
                && host.engine.TryGetGhostWorldByRecordingId(
                    traj.ParentAnchorRecordingId, out parentGhostWorld);
            double renderedParentDist = parentGhostFound
                ? (ghostWorld - parentGhostWorld).magnitude
                : double.NaN;
            // Recorded anchor-local distance at the bracketing
            // before-frame: in Relative sections the `frames[]` entries
            // store anchor-local Cartesian metres in `latitude/longitude/altitude`,
            // so the offset magnitude IS the parent-vs-debris distance
            // the recorder captured at the bracket's left edge.
            //
            // Do NOT compare this directly to renderedParentDist as a
            // fidelity check - this field is the seed-only value and
            // stays constant across the entire bracket (e.g. the 600 ms
            // seed->first-sample gap on a fresh debris recording),
            // while the recorded relative motion may genuinely evolve
            // across that bracket and the rendered distance follows
            // the bracket's linear interpolation. Use
            // `interpolatedAnchorLocalDist` (below) for the
            // fidelity check; this field is retained for the
            // "bracket-left-edge snapshot" view and for direct
            // comparison against the section's `bodyFixedFrames[]`
            // bracketing-before sample via `recordedBodyFixedDist`.
            double recordedAnchorLocalDist = double.NaN;
            // Interpolated anchor-local distance: same `frames[]` surface
            // as recordedAnchorLocalDist, but linearly interpolated at the
            // current playbackUT instead of clamped to the bracketing-
            // before entry. Use this to ask "is the rendered distance
            // tracking the recorded relative motion, or actually
            // diverging?" - the bracketing-before-only field is stable
            // across an entire bracket (e.g. the 0.6 s seed->first-sample
            // gap on fresh debris recordings) while the interpolated
            // value evolves with the recorded geometry.
            double interpolatedAnchorLocalDist = double.NaN;
            if (target.Section.referenceFrame == ReferenceFrame.Relative
                && target.Section.frames != null
                && target.Section.frames.Count > 0)
            {
                recordedAnchorLocalDist = ResolveRecordedAnchorLocalDistanceBefore(
                    target.Section.frames, playbackUT);
                interpolatedAnchorLocalDist = TraceSeparation
                    .InterpolateAnchorLocalOffsetMagnitude(
                        target.Section.frames, playbackUT);
            }
            // Recorded body-fixed distance: subtract the parent's
            // body-fixed primary world position at the bracketing UT from
            // the debris's body-fixed primary world. Computed
            // independently of playback math - ghost transforms drop out
            // entirely. Reflects what the recorder wrote into the two
            // independent body-fixed streams. If this doesn't match the
            // recorded anchor-local distance, the two recording surfaces
            // disagree about parent-vs-debris geometry.
            double recordedBodyFixedDist = double.NaN;
            if (beforeFrame.HasValue && !string.IsNullOrEmpty(beforeFrame.Value.bodyName)
                && !string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId))
            {
                CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, beforeFrame.Value.bodyName);
                catalog.TryFindRecording(
                    traj.ParentAnchorRecordingId,
                    CatalogRecordingScope.CommittedListOnly,
                    out Recording parentRec);
                if (body != null
                    && parentRec != null
                    && parentRec.TrackSections != null)
                {
                    TrajectoryPoint? parentBefore = FindLastBodyFixedFrameAtOrBefore(
                        parentRec.TrackSections, playbackUT);
                    if (parentBefore.HasValue)
                    {
                        Vector3d parentBodyFixedWorld = body.GetWorldSurfacePosition(
                            parentBefore.Value.latitude,
                            parentBefore.Value.longitude,
                            parentBefore.Value.altitude);
                        Vector3d debrisBodyFixedWorld = body.GetWorldSurfacePosition(
                            beforeFrame.Value.latitude,
                            beforeFrame.Value.longitude,
                            beforeFrame.Value.altitude);
                        recordedBodyFixedDist =
                            (debrisBodyFixedWorld - parentBodyFixedWorld).magnitude;
                    }
                }
            }
            TraceSeparation.PlaybackLog("PositionDebris",
                "recId=" + (target.RecordingId ?? "<null>") +
                " parentRecId=" + (traj.ParentAnchorRecordingId ?? "<null>") +
                " playbackUT=" + playbackUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " bodyFixedCount=" + bodyFixedCount +
                " first=" + firstPlaybackRender +
                " bracketBefore=[" + beforeStr + "]" +
                " bracketAfter=[" + afterStr + "]" +
                " lerpAlpha=" + (double.IsNaN(lerpAlpha) ? "NaN" : lerpAlpha.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)) +
                " ghostWorldBefore=" + TraceSeparation.FormatVector3d(ghostWorldBefore) +
                " ghostWorld=" + TraceSeparation.FormatVector3d(ghostWorld) +
                " worldStep=" + TraceSeparation.FormatVector3d(worldStep) +
                " |worldStep|=" + worldStep.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                " predictedWorld=" + TraceSeparation.FormatVector3d(predictedWorld) +
                " predictedVsActual=" + TraceSeparation.FormatVector3d(predictedVsActual) +
                " |predDelta|=" + predictedVsActual.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                " parentGhostFound=" + parentGhostFound +
                " parentGhostWorld=" + (parentGhostFound ? TraceSeparation.FormatVector3d(parentGhostWorld) : "<unresolved>") +
                " renderedParentDist=" + FormatTraceDistance(renderedParentDist) +
                " recordedAnchorLocalDist=" + FormatTraceDistance(recordedAnchorLocalDist) +
                " interpolatedAnchorLocalDist=" + FormatTraceDistance(interpolatedAnchorLocalDist) +
                " recordedBodyFixedDist=" + FormatTraceDistance(recordedBodyFixedDist));
        }

        /// <summary>
        /// The last sample at or before the playback UT and the first sample at or after it
        /// (null when none qualifies or the list is empty).
        /// </summary>
        internal static void FindTraceBracketFrames(
            List<TrajectoryPoint> frames,
            double playbackUT,
            out TrajectoryPoint? beforeFrame,
            out TrajectoryPoint? afterFrame)
        {
            beforeFrame = null;
            afterFrame = null;
            if (frames != null && frames.Count > 0)
            {
                int beforeIdx = -1;
                int afterIdx = -1;
                for (int j = 0; j < frames.Count; j++)
                {
                    if (frames[j].ut <= playbackUT)
                        beforeIdx = j;
                    if (frames[j].ut >= playbackUT && afterIdx < 0)
                        afterIdx = j;
                }
                if (beforeIdx >= 0) beforeFrame = frames[beforeIdx];
                if (afterIdx >= 0) afterFrame = frames[afterIdx];
            }
        }

        internal static string FormatTraceBracketFrame(TrajectoryPoint? frame)
        {
            return frame.HasValue
                ? ("ut=" + frame.Value.ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + " lla=(" + frame.Value.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "," + frame.Value.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "," + frame.Value.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")")
                : "<none>";
        }

        /// <summary>
        /// Magnitude of the anchor-local offset of the last relative frame at or before the
        /// playback UT, scanning in order and stopping at the first later frame; NaN when the
        /// first frame is already later.
        /// </summary>
        internal static double ResolveRecordedAnchorLocalDistanceBefore(
            List<TrajectoryPoint> frames, double playbackUT)
        {
            int beforeIdx = -1;
            for (int j = 0; j < frames.Count; j++)
            {
                if (frames[j].ut <= playbackUT)
                    beforeIdx = j;
                else break;
            }
            if (beforeIdx >= 0)
            {
                TrajectoryPoint relFrame = frames[beforeIdx];
                Vector3d localOffset = new Vector3d(
                    relFrame.latitude, relFrame.longitude, relFrame.altitude);
                return localOffset.magnitude;
            }
            return double.NaN;
        }

        /// <summary>
        /// Walks every section's body-fixed frames in order and returns the last sample at or
        /// before the playback UT; within a section the scan stops at the first later sample,
        /// and a later section's qualifying sample replaces an earlier one.
        /// </summary>
        internal static TrajectoryPoint? FindLastBodyFixedFrameAtOrBefore(
            List<TrackSection> trackSections, double playbackUT)
        {
            TrajectoryPoint? parentBefore = null;
            for (int s = 0; s < trackSections.Count; s++)
            {
                var ps = trackSections[s];
                if (ps.bodyFixedFrames == null || ps.bodyFixedFrames.Count == 0)
                    continue;
                for (int j = 0; j < ps.bodyFixedFrames.Count; j++)
                {
                    if (ps.bodyFixedFrames[j].ut <= playbackUT)
                        parentBefore = ps.bodyFixedFrames[j];
                    else break;
                }
            }
            return parentBefore;
        }

        internal static string FormatTraceDistance(double value)
        {
            return double.IsNaN(value)
                ? "NaN"
                : value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Tracing-only expected-attitude source for <see cref="GhostRenderTrace"/>'s
        /// <c>AfterUpdate</c> line (<c>dRotDeg=</c> / <c>rotRef=</c>). Resolves the world
        /// rotation the RECORDING implies at <paramref name="playbackUT"/>, independently of
        /// the positioner's per-ghost state (cached bracket index, orbit cache, the ghost's
        /// current transform), and through the SAME production conventions the positioner
        /// uses for the section's frame:
        /// <list type="bullet">
        /// <item>OrbitalCheckpoint / orbit segment: <see cref="ParsekFlight.ComputeOrbitalRotation"/> over a
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
        internal bool TryResolveRecordedAttitudeForTrace(
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
            if (hasSection && ParsekFlight.HasRenderableCheckpointTrackSection(section))
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
            if (hasSection && ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(section, out List<TrajectoryPoint> sectionFrames))
            {
                if (ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
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
                OrbitSegment? seg = ParsekFlight.FindOrbitSegment(traj.OrbitSegments, playbackUT);
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

            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, segment.bodyName);
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
            var (ghostRot, _) = ParsekFlight.ComputeOrbitalRotation(
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

            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, before.bodyName);
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
                    out ParsekFlight.RelativeAnchorPose anchorPose,
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
