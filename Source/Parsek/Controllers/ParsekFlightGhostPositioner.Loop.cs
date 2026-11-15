using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Loop placement, loop-relative placement, and the loop seam and re-aim traces.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        /// <summary>
        /// Loop-seam teleport tracker: on an orbit-segment change, measure the
        /// ghost's world-position discontinuity and raise the Tier-C
        /// <c>loop-seam-teleport</c> anomaly through
        /// <see cref="GhostRenderTrace.EmitAnomaly"/> when
        /// <see cref="GhostRenderTrace.IsLoopSeamTeleport"/> says the jump is
        /// defect-class (floor 1,000 km vs the ~7-10 km healthy seams and the
        /// ~1-SOI-radius defect population). Cheap early-out when
        /// ghostRenderTracing is off; never throws.
        /// </summary>
        private void TrackLoopSeamTeleport(int memberIndex, IPlaybackTrajectory traj,
            OrbitSegment seg, int segmentIndex, double loopUT, Vector3 ghostPos)
        {
            try
            {
                if (ParsekSettings.Current == null || !ParsekSettings.Current.ghostRenderTracing)
                    return;
                var ic = CultureInfo.InvariantCulture;
                double nowUT = Planetarium.GetUniversalTime();
                int frame = Time.frameCount;
                if (loopSeamLastGhostFrame.TryGetValue(memberIndex, out var last)
                    && last.segIdx != segmentIndex)
                {
                    double jump = (ghostPos - last.pos).magnitude;
                    CelestialBody segBody = FindLiveBodyByBodyNameOrNull(FlightGlobals.Bodies, seg.bodyName);
                    double bodySpeed = segBody != null && segBody.orbit != null
                        ? segBody.orbit.orbitalSpeed
                        : 0.0;
                    if (GhostRenderTrace.IsLoopSeamTeleport(
                            jump,
                            bodySpeed + LoopSeamGhostSpeedAllowanceMps,
                            nowUT - last.currentUT,
                            frame,
                            last.frame,
                            ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame,
                            loopUT - last.loopUT))
                    {
                        double soi = segBody != null ? segBody.sphereOfInfluence : double.NaN;
                        GhostRenderTrace.EmitAnomaly(
                            traj.RecordingId, memberIndex, nowUT, loopUT,
                            "loop-seam-teleport",
                            "member=" + memberIndex.ToString(ic)
                            + " seg=" + last.segIdx.ToString(ic) + "->" + segmentIndex.ToString(ic)
                            + " body=" + (seg.bodyName ?? "<none>")
                            + " jump=" + jump.ToString("F0", ic) + "m"
                            + " soi=" + soi.ToString("F0", ic) + "m"
                            + " reaim=" + (traj is Parsek.Reaim.ReaimedTrajectory ? "true" : "false"));
                    }
                }
                loopSeamLastGhostFrame[memberIndex] = (segmentIndex, ghostPos, loopUT, nowUT, frame);
            }
            catch { /* diagnostic only */ }
        }

        /// <summary>
        /// Diagnostic trace of a re-aimed orbit ghost vs the live bodies. Logs (rate-limited) the ghost's
        /// world position and its distance to the segment's own body, the launch body, and the target, so
        /// "arrives where the target is not" is measurable. On a SEGMENT CHANGE (a trajectory seam) it logs
        /// the world-position discontinuity between the prior frame and this one - a large jump is a
        /// teleport (no trajectory continuity across the seam). Verbose; never throws.
        /// </summary>
        private void LogReaimGhostTrace(int memberIndex, OrbitSegment seg, int segmentIndex, double loopUT, Vector3 ghostPos)
        {
            try
            {
                var ic = CultureInfo.InvariantCulture;
                double currentUT = Planetarium.GetUniversalTime();
                CelestialBody segBody = FindLiveBodyByBodyNameOrNull(FlightGlobals.Bodies, seg.bodyName);
                CelestialBody target = null, launch = null;
                if (host.cachedLoopUnits != null && host.cachedLoopUnits.TryGetUnitForMember(memberIndex, out GhostPlaybackLogic.LoopUnit unit)
                    && unit.ReaimPlan.HasValue)
                {
                    target = FindLiveBodyByBodyNameOrNull(FlightGlobals.Bodies, unit.ReaimPlan.Value.TargetBody);
                    launch = FindLiveBodyByBodyNameOrNull(FlightGlobals.Bodies, unit.ReaimPlan.Value.LaunchBody);
                }
                double dSeg = segBody != null ? (ghostPos - (Vector3)segBody.position).magnitude : double.NaN;
                double dLaunch = launch != null ? (ghostPos - (Vector3)launch.position).magnitude : double.NaN;
                double dTarget = target != null ? (ghostPos - (Vector3)target.position).magnitude : double.NaN;

                // Seam-teleport detection: if the segment changed since the last frame, log the jump.
                if (reaimLastGhostFrame.TryGetValue(memberIndex, out var last) && last.segIdx != segmentIndex)
                {
                    double jump = (ghostPos - last.pos).magnitude;
                    ParsekLog.Verbose("ReaimSeam",
                        $"SEAM member={memberIndex} seg#{last.segIdx}->{segmentIndex} body={seg.bodyName} " +
                        $"loopUT {last.loopUT.ToString("F0", ic)}->{loopUT.ToString("F0", ic)} " +
                        $"jump={jump.ToString("F0", ic)}m (prevPos->thisPos discontinuity)");
                }
                reaimLastGhostFrame[memberIndex] = (segmentIndex, ghostPos, loopUT);

                ParsekLog.VerboseRateLimited("ReaimSeam", "trace-" + memberIndex.ToString(ic),
                    $"member={memberIndex} seg#{segmentIndex} body={seg.bodyName} loopUT={loopUT.ToString("F0", ic)} " +
                    $"currentUT={currentUT.ToString("F0", ic)} dist[{seg.bodyName}]={dSeg.ToString("F0", ic)}m " +
                    $"dist[{(launch != null ? launch.bodyName : "launch")}]={dLaunch.ToString("F0", ic)}m " +
                    $"dist[{(target != null ? target.bodyName : "target")}]={dTarget.ToString("F0", ic)}m" +
                    (target != null ? $"(SOI={target.sphereOfInfluence.ToString("F0", ic)})" : ""), 2.0);
            }
            catch { /* diagnostic only */ }
        }

        void IGhostPositioner.PositionLoop(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx)
        {
            if (state?.ghost == null || traj == null) return;
            int playbackIdx = state.playbackIndex;
            bool useAnchor = GhostPlaybackLogic.ShouldUseLoopAnchor(traj);
            InterpolationResult interpResult;
            // PR #594 P1 review fix: pass `state` so the relative-frame
            // retirement branch can set state.anchorRetiredThisFrame for
            // the loop-playback path. Same rationale as in
            // IGhostPositioner.InterpolateAndPositionRelative above.
            activeBodyFixedShiftSeconds = state.bodyFixedShiftSeconds;
            try
            {
                PositionLoopGhost(state.ghost, traj, ref playbackIdx, ut,
                    index, index * 10000, useAnchor, state,
                    ParsekFlight.ShouldAutoActivateGhost(state), out interpResult);
            }
            finally
            {
                activeBodyFixedShiftSeconds = 0.0;
            }
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        // The two loop-seam trace dictionaries are keyed by committed-list index, so they move with
        // the list through IndexShift like every other index-keyed slot. Without the shift a member's
        // first seam check after a mid-list change compares against the frame of the recording that
        // used to hold its index.
        internal void ShiftLoopSeamTracesAfterDelete(int removedIndex)
        {
            int reaimShifted = CountKeysAbove(reaimLastGhostFrame, removedIndex);
            int seamShifted = CountKeysAbove(loopSeamLastGhostFrame, removedIndex);
            bool reaimDropped = reaimLastGhostFrame.ContainsKey(removedIndex);
            bool seamDropped = loopSeamLastGhostFrame.ContainsKey(removedIndex);
            IndexShift.DictAfterDelete(reaimLastGhostFrame, removedIndex);
            IndexShift.DictAfterDelete(loopSeamLastGhostFrame, removedIndex);
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Flight",
                "Loop-seam traces reindexed after delete at #" + removedIndex.ToString(ic)
                + ": reaim shifted=" + reaimShifted.ToString(ic)
                + " dropped=" + (reaimDropped ? "1" : "0")
                + " count=" + reaimLastGhostFrame.Count.ToString(ic)
                + ", loopSeam shifted=" + seamShifted.ToString(ic)
                + " dropped=" + (seamDropped ? "1" : "0")
                + " count=" + loopSeamLastGhostFrame.Count.ToString(ic));
        }

        internal void ShiftLoopSeamTracesAfterInsert(int insertedIndex)
        {
            int reaimShifted = CountKeysAbove(reaimLastGhostFrame, insertedIndex - 1);
            int seamShifted = CountKeysAbove(loopSeamLastGhostFrame, insertedIndex - 1);
            IndexShift.DictAfterInsert(reaimLastGhostFrame, insertedIndex);
            IndexShift.DictAfterInsert(loopSeamLastGhostFrame, insertedIndex);
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Flight",
                "Loop-seam traces reindexed after insert at #" + insertedIndex.ToString(ic)
                + ": reaim shifted=" + reaimShifted.ToString(ic)
                + " count=" + reaimLastGhostFrame.Count.ToString(ic)
                + ", loopSeam shifted=" + seamShifted.ToString(ic)
                + " count=" + loopSeamLastGhostFrame.Count.ToString(ic));
        }

        internal static int CountKeysAbove<T>(Dictionary<int, T> dict, int index)
        {
            int count = 0;
            foreach (int key in dict.Keys)
                if (key > index) count++;
            return count;
        }

        internal Dictionary<int, (int segIdx, Vector3 pos, double loopUT)> ReaimLastGhostFrameForTesting
            => reaimLastGhostFrame;

        internal Dictionary<int, (int segIdx, Vector3 pos, double loopUT, double currentUT, int frame)>
            LoopSeamLastGhostFrameForTesting => loopSeamLastGhostFrame;

        internal bool TryResolveLoopLiveAnchorPose(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            if (anchorVesselId == 0u)
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "missing-anchor-pid",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId);
            if (anchor == null)
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "anchor-not-loaded",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            // Match the recorder's live-anchor frame (FlightRecorder /
            // BackgroundRecorder TryResolveAnchorPoseForCandidate). The recorder
            // writes loop-relative offsets against vesselTransform.position; the
            // matching rotation is vesselTransform.rotation. GetWorldPos3D (CoMD)
            // here would re-introduce the same CoM-vs-vesselTransform mismatch
            // the recorder fix removed, but inverted at playback time. Use
            // anchor.transform explicitly (not GetTransform(), which follows
            // ReferenceTransform / "Control From Here").
            Transform anchorTransform = anchor.transform;
            Quaternion anchorRotation = anchorTransform != null
                ? anchorTransform.rotation
                : Quaternion.identity;
            Vector3d anchorWorld = anchorTransform != null
                ? (Vector3d)anchorTransform.position
                : anchor.GetWorldPos3D();
            if (!ParsekFlight.IsFiniteVector3d(anchorWorld))
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "non-finite-live-anchor",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: anchorWorld,
                    anchorRotation: anchorRotation,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            pose.worldPos = anchorWorld;
            pose.worldRotation = anchorRotation;
            pose.fromRecordedTrajectory = false;
            GhostRenderTrace.EmitRelativeResolver(
                victimRecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "loop-live-anchor",
                "live-selected",
                anchorVesselId,
                null,
                success: true,
                fromRecordedTrajectory: false,
                anchorPosition: pose.worldPos,
                anchorRotation: pose.worldRotation,
                localOffset: Vector3d.zero,
                outputPosition: pose.worldPos);
            return true;
        }

        /// <summary>
        /// Unified positioning method for loop ghosts. Selects between anchor-relative
        /// (InterpolateAndPositionRelative) and absolute (InterpolateAndPosition) based
        /// on the useAnchor flag and the TrackSection at the given loopUT.
        /// </summary>
        void PositionLoopGhost(
            GameObject ghost,
            IPlaybackTrajectory rec,
            ref int playbackIdx,
            double loopUT,
            int recIdx,
            int ghostIdSalt,
            bool useAnchor,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (TryInterpolateAndPositionCheckpointSection(
                recIdx,
                rec,
                ghost,
                ref playbackIdx,
                loopUT,
                allowActivation,
                out interpResult))
            {
                return;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, loopUT);
            if (sectionIdx >= 0 && rec.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
            {
                var section = rec.TrackSections[sectionIdx];
                var sectionFrames = section.frames ?? rec.Points;
                uint anchorVesselId = section.anchorVesselId != 0
                    ? section.anchorVesselId
                    : rec.LoopAnchorVesselId;

                if (useAnchor)
                {
                    long relKey = ((long)recIdx << 32) | anchorVesselId;
                    if (host.loggedRelativeStart.Add(relKey))
                        ParsekLog.Info("Loop",
                            $"Anchor-relative loop playback started: recording #{recIdx} " +
                            $"\"{rec.VesselName}\" anchorPid={anchorVesselId} " +
                            $"contract=anchor-local " +
                            $"version={rec.RecordingFormatVersion} " +
                            $"sectionUT=[{section.startUT:F1},{section.endUT:F1}]");

                    InterpolateAndPositionLoopRelative(
                        ghost, sectionFrames, ref playbackIdx, loopUT,
                        anchorVesselId, rec.RecordingFormatVersion,
                        recIdx, rec.RecordingId, rec.VesselName, retireSignalState,
                        allowActivation, out interpResult);
                    if (GhostPlaybackEngine.IsInterpolationResultValid(interpResult))
                        interpResult = WithRelativeSectionSurfaceVelocity(interpResult, section, loopUT);
                    return;
                }

                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long missingAnchorKey = RelativeAnchorResolution.DedupeKey(recIdx, anchorVesselId);
                if (host.loggedAnchorNotFound.Add(missingAnchorKey))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recIdx,
                            rec.VesselName,
                            anchorVesselId,
                            "PositionLoopGhost"));
                interpResult = InterpolationResult.Zero;
                return;
            }

            if (sectionIdx >= 0
                && ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                    rec.TrackSections,
                    sectionIdx,
                    out List<TrajectoryPoint> absoluteSectionFrames,
                    out _))
            {
                InterpolateAndPosition(
                    ghost,
                    absoluteSectionFrames,
                    null,
                    ref playbackIdx,
                    loopUT,
                    ghostIdSalt,
                    out interpResult,
                    allowActivation: allowActivation,
                    skipOrbitSegments: true,
                    recordingId: rec.RecordingId,
                    sectionIndex: sectionIdx);
                return;
            }

            // Absolute positioning fallback
            // Phase 1: pass section context for spline lookup. The fallback is
            // the body-fixed loop path, which is exactly where ABSOLUTE
            // ExoPropulsive / ExoBallistic splines are meant to apply.
            int splineSectionIdx = sectionIdx;
            InterpolateAndPosition(ghost, rec.Points, rec.OrbitSegments,
                ref playbackIdx, loopUT, ghostIdSalt, out interpResult,
                allowActivation: allowActivation,
                recordingId: rec.RecordingId, sectionIndex: splineSectionIdx,
                pointFrameSource: "loop-flat-points",
                flatPointSections: rec.TrackSections);
        }

        void InterpolateAndPositionLoopRelative(
            GameObject ghost,
            List<TrajectoryPoint> frames,
            ref int cachedIndex,
            double targetUT,
            uint anchorVesselId,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingId,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (frames == null || frames.Count == 0)
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before first frame — position at first frame's offset
                PositionLoopGhostRelativeAt(
                    ghost,
                    frames[0],
                    anchorVesselId,
                    recordingFormatVersion,
                    recordingIndex,
                    recordingId,
                    recordingVesselName,
                    retireSignalState,
                    allowActivation);
                interpResult = new InterpolationResult(frames[0].velocity, frames[0].bodyName, 0);
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionLoopGhostRelativeAt(
                    ghost,
                    before,
                    anchorVesselId,
                    recordingFormatVersion,
                    recordingIndex,
                    recordingId,
                    recordingVesselName,
                    retireSignalState,
                    allowActivation);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, 0);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            // Interpolate offset (dx, dy, dz stored in lat/lon/alt)
            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;

            // Interpolate rotation in the recording's stored RELATIVE frame.
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            string bodyName = before.bodyName ?? "Kerbin";
            ParsekFlight.RelativeAnchorPose anchorPose;

            if (TryResolveLoopLiveAnchorPose(anchorVesselId, recordingId, targetUT, out anchorPose))
            {
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPose.worldPos,
                    anchorPose.worldRotation,
                    dx,
                    dy,
                    dz);

                if (double.IsNaN(ghostPos.x) || double.IsNaN(ghostPos.y) || double.IsNaN(ghostPos.z))
                {
                    ParsekLog.Warn("Flight", "InterpolateAndPositionLoopRelative: NaN in ghost position, using anchor position");
                    ghostPos = anchorPose.worldPos;
                }

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchorPose.worldRotation,
                    interpolatedRot);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-relative-position",
                    "resolved",
                    anchorVesselId,
                    null,
                    success: true,
                    fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                    anchorPosition: anchorPose.worldPos,
                    anchorRotation: anchorPose.worldRotation,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: ghostPos);
                double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT;
                if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
                {
                    GhostRenderTrace.EmitPhase(
                        recordingId,
                        recordingIndex,
                        traceCurrentUT,
                        targetUT,
                        "UpdatePath",
                        "mode=LoopRelative anchorPid=" + anchorVesselId.ToString(CultureInfo.InvariantCulture)
                        + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                        + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                        + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                        + " anchorSource=" + (anchorPose.fromRecordedTrajectory ? "recorded" : "live")
                        + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                        + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                        + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                        + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
                }

                ParsekLog.VerboseRateLimited("Flight", "relative-offset-applied",
                    ParsekFlight.BuildRelativeOffsetAppliedLog(
                        recordingFormatVersion,
                        dx,
                        dy,
                        dz,
                        anchorVesselId,
                        anchorPose.fromRecordedTrajectory),
                    2.0);

                // Compute altitude for InterpolationResult
                CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, bodyName);
                double alt = body != null ? body.GetAltitude(ghostPos) : 0;

                host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
                {
                    ghost = ghost,
                    mode = ParsekFlight.GhostPosMode.Relative,
                    recordingId = recordingId,
                    recordingIndex = recordingIndex,
                    hasRecordingIndex = true,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = interpolatedRot,
                    relativeBodyName = bodyName,
                    bodyBefore = body, // for fallback in LateUpdate
                    latBefore = before.latitude, lonBefore = before.longitude, altBefore = before.altitude,
                    relativeRecordedAnchorPose = ParsekFlight.RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                    relativeLoopLiveAnchor = true,
                });

                interpResult = new InterpolationResult(
                    Vector3.Lerp(before.velocity, after.velocity, t),
                    bodyName, alt);
            }
            else
            {
                // No live or recorded anchor pose is available. Retire for this
                // frame so v6 metre offsets are never interpreted as surface
                // lat/lon/alt or left frozen at a stale transform.
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-relative-position",
                    "anchor-unresolved-retired",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long key = RelativeAnchorResolution.DedupeKey(recordingIndex, anchorVesselId);
                if (host.loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recordingIndex,
                            recordingVesselName,
                            anchorVesselId,
                            "InterpolateAndPositionLoopRelative"));

                // Return zero result -- ghost stays hidden until the next
                // absolute section or until the anchor resolves.
                interpResult = InterpolationResult.Zero;
            }
        }

        /// <summary>
        /// Positions a ghost at a single RELATIVE frame point (no interpolation).
        /// Used for edge cases (before first frame, zero-duration segments).
        /// </summary>
        void PositionLoopGhostRelativeAt(
            GameObject ghost,
            TrajectoryPoint point,
            uint anchorVesselId,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingId,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation)
        {
            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            double dx = point.latitude;
            double dy = point.longitude;
            double dz = point.altitude;
            string bodyName = point.bodyName ?? "Kerbin";
            ParsekFlight.RelativeAnchorPose anchorPose;

            if (TryResolveLoopLiveAnchorPose(anchorVesselId, recordingId, point.ut, out anchorPose))
            {
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPose.worldPos,
                    anchorPose.worldRotation,
                    dx,
                    dy,
                    dz);

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchorPose.worldRotation,
                    sanitized);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                    point.ut,
                    "loop-relative-single",
                    "resolved",
                    anchorVesselId,
                    null,
                    success: true,
                    fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                    anchorPosition: anchorPose.worldPos,
                    anchorRotation: anchorPose.worldRotation,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: ghostPos);
                double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut;
                if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
                {
                    GhostRenderTrace.EmitPhase(
                        recordingId,
                        recordingIndex,
                        traceCurrentUT,
                        point.ut,
                        "UpdatePath",
                        "mode=LoopRelativeSingle anchorPid=" + anchorVesselId.ToString(CultureInfo.InvariantCulture)
                        + " anchorSource=" + (anchorPose.fromRecordedTrajectory ? "recorded" : "live")
                        + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                        + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                        + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                        + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
                }

                CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, bodyName);
                host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
                {
                    ghost = ghost,
                    mode = ParsekFlight.GhostPosMode.Relative,
                    recordingId = recordingId,
                    recordingIndex = recordingIndex,
                    hasRecordingIndex = true,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = sanitized,
                    relativeBodyName = bodyName,
                    bodyBefore = body,
                    latBefore = dx, lonBefore = dy, altBefore = dz,
                    relativeRecordedAnchorPose = ParsekFlight.RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                    relativeLoopLiveAnchor = true,
                });
            }
            else
            {
                // No live or recorded anchor pose is available. Retire for this
                // frame; see InterpolateAndPositionLoopRelative for the same gate.
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                    point.ut,
                    "loop-relative-single",
                    "anchor-unresolved-retired",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long key = RelativeAnchorResolution.DedupeKey(recordingIndex, anchorVesselId);
                if (host.loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recordingIndex,
                            recordingVesselName,
                            anchorVesselId,
                            "PositionGhostRelativeAt"));
            }
        }
    }
}
