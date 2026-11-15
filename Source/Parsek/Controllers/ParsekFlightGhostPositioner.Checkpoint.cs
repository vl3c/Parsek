using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // OrbitalCheckpoint section placement.
    internal sealed partial class ParsekFlightGhostPositioner
    {
        bool TryInterpolateAndPositionCheckpointSection(
            int recordingIndex,
            IPlaybackTrajectory traj,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryGetCheckpointTrackSection(traj, playbackUT, out int sectionIdx, out TrackSection section))
                return false;

            if (!TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
                    recordingIndex,
                    sectionIdx,
                    section,
                    ghost,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    recordingId: traj?.RecordingId,
                    out interpResult))
            {
                // Phase 1: pass section context. Checkpoint sections are
                // OrbitalCheckpoint reference frame, which ShouldFitSection
                // filters out — the spline gate will naturally fall through
                // to legacy lerp here. Threaded so a future spline-on-
                // checkpoint extension wouldn't need to revisit the call site.
                InterpolateAndPosition(
                    ghost,
                    section.frames,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    out interpResult,
                    recordingId: traj?.RecordingId,
                    sectionIndex: sectionIdx);
            }

            int logIndex = playbackIdx;
            if (TryResolvePointWorldPosition(section.frames, ref logIndex, playbackUT, out Vector3d worldPos))
                LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);

            return true;
        }

        bool TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
            int recordingIndex,
            int sectionIdx,
            TrackSection section,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            string recordingId,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryFindCheckpointOrbitSegment(section, playbackUT, out OrbitSegment segment, out int checkpointIdx))
                return false;

            long cacheKey = BuildCheckpointOrbitCacheKey(recordingIndex, sectionIdx, checkpointIdx);
            if (!TryGetOrbitForSegment(segment, cacheKey, out Orbit orbit, out CelestialBody orbitBody))
                return false;

            bool hasCheckpointFrames = section.frames != null && section.frames.Count > 0;
            if (!hasCheckpointFrames)
            {
                if (allowActivation && !ghost.activeSelf)
                    ghost.SetActive(true);

                PositionGhostFromOrbit(ghost, segment, playbackUT, cacheKey, recordingId);

                Vector3d orbitWorldPos = orbit.getPositionAtUT(playbackUT);
                Vector3d orbitVelocity = orbit.getOrbitalVelocityAtUT(playbackUT);
                double orbitAltitude = orbitBody.GetAltitude(orbitWorldPos);
                interpResult = new InterpolationResult(
                    (Vector3)orbitVelocity,
                    segment.bodyName,
                    orbitAltitude).WithSurfaceVelocity(
                        GhostSurfaceVelocity.FromOrbitVelocity(
                            orbitVelocity, orbitBody.getRFrmVel(orbitWorldPos)),
                        GhostSurfaceVelocitySource.Orbit);

                string recId = string.IsNullOrEmpty(recordingId) ? "(null)" : recordingId;
                ParsekLog.VerboseRateLimited(
                    "Playback",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "orbital-checkpoint-orbit-only-{0}-{1}-{2}",
                        recId,
                        sectionIdx,
                        checkpointIdx),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "OrbitalCheckpoint orbit-only playback: rec={0} currentUT={1:F1} section[{2}] sectionUT={3:F1}-{4:F1} checkpointIdx={5} segmentUT={6:F1}-{7:F1} world={8} alt={9:F1}",
                        recId,
                        playbackUT,
                        sectionIdx,
                        section.startUT,
                        section.endUT,
                        checkpointIdx,
                        segment.startUT,
                        segment.endUT,
                        GhostRenderTrace.FormatVector3d(orbitWorldPos),
                        orbitAltitude),
                    5.0);
                return true;
            }

            bool hasPointInterpolation = TryResolveCheckpointPointInterpolation(
                section.frames,
                ref playbackIdx,
                playbackUT,
                out TrajectoryPoint before,
                out TrajectoryPoint after,
                out float t);
            if (!hasPointInterpolation)
            {
                if (section.frames == null || section.frames.Count == 0)
                {
                    ghost.SetActive(false);
                    return true;
                }

                before = section.frames[0];
                after = before;
                t = 0f;
            }

            CelestialBody bodyBefore = FindBodyByNameOrNull(FlightGlobals.Bodies, before.bodyName);
            CelestialBody bodyAfter = FindBodyByNameOrNull(FlightGlobals.Bodies, after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            if (allowActivation && !ghost.activeSelf)
                ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
                interpolatedPos = posBefore;

            // §7.7 BubbleEntry/Exit (and any other §7.x candidate landing on
            // a Checkpoint section): apply the rigid-translation ε on top of
            // the Kepler-bracketed lerp before assigning the transform.
            // Without this, the ε computed by AnchorPropagator and stored in
            // RenderSessionState would be invisible — the spline / lerp paths
            // already query allowAnchorCorrectionInterval, but the checkpoint
            // path used to skip it because the original §7.5 OrbitalCheckpoint
            // anchor lands on the ABSOLUTE neighbour rather than the
            // Checkpoint section itself; §7.7 changed that, and so did the
            // Phase 6 anchor-bound resolver work that produces ε at the
            // Checkpoint side. Same lookup as the PointInterp path uses
            // (Phase 2/3 ε, additive in world-space, HR-15 frozen-once).
            bool hasAnchorEps = ParsekFlight.allowRenderAnchorCorrectionInterval(
                recordingId, sectionIdx, playbackUT, false,
                out Vector3d anchorEps, out string anchorCorrectionReason);
            if (hasAnchorEps)
                interpolatedPos += anchorEps;

            Vector3d velocity = orbit.getOrbitalVelocityAtUT(playbackUT);
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);
            var (ghostRot, _) = ParsekFlight.ComputeOrbitalRotation(
                segment,
                orbit,
                playbackUT,
                velocity,
                interpolatedPos,
                orbitBody.position,
                ghost.transform.rotation,
                cacheKey,
                hasOfr,
                spinning);

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = ghostRot;
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : playbackUT;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    recordingIndex,
                    traceCurrentUT,
                    playbackUT,
                    "UpdatePath",
                    "mode=CheckpointPoint sectionIndex=" + sectionIdx.ToString(CultureInfo.InvariantCulture)
                    + " cacheKey=" + cacheKey.ToString(CultureInfo.InvariantCulture)
                    + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                    + " rawBefore=" + GhostRenderTrace.FormatVector3d(posBefore)
                    + " rawAfter=" + GhostRenderTrace.FormatVector3d(posAfter)
                    + " anchorCorrectionHit=" + (hasAnchorEps ? "true" : "false")
                    + " anchorCorrectionReason=" + anchorCorrectionReason
                    + " anchorEps=" + GhostRenderTrace.FormatVector3d(hasAnchorEps ? anchorEps : Vector3d.zero)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            Quaternion pointRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            pointRot = TrajectoryMath.SanitizeQuaternion(pointRot);
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.CheckpointPoint,
                recordingId = recordingId,
                recordingIndex = recordingIndex,
                hasRecordingIndex = true,
                bodyBefore = bodyBefore,
                bodyAfter = bodyAfter,
                latBefore = before.latitude,
                lonBefore = before.longitude,
                altBefore = before.altitude,
                latAfter = after.latitude,
                lonAfter = after.longitude,
                altAfter = after.altitude,
                t = t,
                pointUT = playbackUT,
                interpolatedRot = pointRot,
                orbitCacheKey = cacheKey,
                orbitUT = playbackUT,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = orbitBody,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT,
                // §7.7 anchor lookup keys for LateUpdate re-application.
                // Without these, the LateUpdate FloatingOrigin shift
                // would replay the bare lerp position and the rendered
                // ghost would drop the anchor correction every late frame.
                anchorRecordingId = recordingId,
                anchorSectionIndex = sectionIdx,
            });

            interpResult = new InterpolationResult(
                (Vector3)velocity,
                segment.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
            if (hasPointInterpolation
                && ReferenceEquals(bodyBefore, bodyAfter)
                && GhostSurfaceVelocity.TryFromBracketPositions(
                    posBefore, posAfter, after.ut - before.ut, out Vector3 checkpointSurfaceVelocity))
            {
                interpResult = interpResult.WithSurfaceVelocity(
                    checkpointSurfaceVelocity, GhostSurfaceVelocitySource.Positions);
            }
            return true;
        }

        internal static bool TryFindCheckpointOrbitSegment(
            TrackSection section,
            double playbackUT,
            out OrbitSegment segment,
            out int checkpointIdx)
        {
            segment = default(OrbitSegment);
            checkpointIdx = -1;
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                OrbitSegment candidate = section.checkpoints[i];
                if (playbackUT >= candidate.startUT && playbackUT <= candidate.endUT)
                {
                    segment = candidate;
                    checkpointIdx = i;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveCheckpointPointInterpolation(
            List<TrajectoryPoint> frames,
            ref int playbackIdx,
            double playbackUT,
            out TrajectoryPoint before,
            out TrajectoryPoint after,
            out float t)
        {
            return TrajectoryMath.InterpolatePoints(
                frames,
                ref playbackIdx,
                playbackUT,
                out before,
                out after,
                out t);
        }

        internal static long BuildCheckpointOrbitCacheKey(
            int recordingIndex,
            int sectionIdx,
            int checkpointIdx)
        {
            // Regular orbit playback cache keys are non-negative int-derived values.
            // Checkpoint keys live in a negative long namespace with explicit strides
            // so section/checkpoint growth cannot alias the ordinary segment cache.
            long rec = Math.Min(CheckpointOrbitCacheMaxRecordingIndex, Math.Max(0, (long)recordingIndex));
            long section = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)sectionIdx));
            long checkpoint = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)checkpointIdx));
            return unchecked(
                CheckpointOrbitCacheBase
                + rec * CheckpointOrbitCacheRecordingStride
                + section * CheckpointOrbitCacheSectionStride
                + checkpoint);
        }

        bool TryGetCheckpointTrackSection(
            IPlaybackTrajectory traj,
            double playbackUT,
            out int sectionIdx,
            out TrackSection section)
        {
            sectionIdx = -1;
            section = default(TrackSection);
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIdx < 0)
                return false;

            section = traj.TrackSections[sectionIdx];
            return ParsekFlight.HasRenderableCheckpointTrackSection(section);
        }

        void LogCheckpointPointPlayback(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            double playbackUT,
            Vector3d worldPos)
        {
            int pointIdx = 0;
            TrajectoryPoint? point = TrajectoryMath.BracketPointAtUT(
                section.frames,
                playbackUT,
                ref pointIdx);
            if (!point.HasValue && section.frames != null && section.frames.Count > 0)
                point = section.frames[0];

            string recId = string.IsNullOrEmpty(traj?.RecordingId) ? "(null)" : traj.RecordingId;
            string pointDetail = point.HasValue
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "pointUT={0:F1} body={1} alt={2:F0} speed={3:F1}",
                    point.Value.ut,
                    point.Value.bodyName ?? "(null)",
                    point.Value.altitude,
                    point.Value.velocity.magnitude)
                : "pointUT=(none) body=(none) alt=0 speed=0";

            // Bug #595: the previous 1.0s rate-limit window was tight enough that
            // a long-playing OrbitalCheckpoint section still emitted ~14
            // lines/min per (rec, section) pair (413 lines in a single 30-min
            // session). Use the default 5s window to keep one line per section
            // per (typical) rate-limit window without losing the section-change
            // signal — the key is per-(recId, sectionIdx) so a new section
            // still logs immediately on first frame.
            ParsekLog.VerboseRateLimited(
                "Playback",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "orbital-checkpoint-point-{0}-{1}",
                    recId,
                    sectionIdx),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "OrbitalCheckpoint point playback: rec={0} currentUT={1:F1} section[{2}] sectionUT={3:F1}-{4:F1} {5} world={6} frames={7}",
                    recId,
                    playbackUT,
                    sectionIdx,
                    section.startUT,
                    section.endUT,
                    pointDetail,
                    ParsekFlight.FormatVector3d(worldPos),
                    section.frames?.Count ?? 0));
        }
    }
}
