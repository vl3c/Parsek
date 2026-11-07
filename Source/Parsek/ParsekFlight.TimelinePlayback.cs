using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Timeline auto-playback driven through the engine (chain ghosts, skip reasons, playback
    // flags, CommNet, loop units, anchor resolvers, timeline ghost teardown), manual preview
    // playback, and the camera pre-cull ghost-position reapply.
    public partial class ParsekFlight
    {
        internal enum GhostCameraPreCullAction
        {
            /// <summary>No pending entries, or this frame's post-LateUpdate pass already ran.</summary>
            Nothing,

            /// <summary>
            /// A camera is rendering before this frame's LateUpdate ran (a manual
            /// Camera.Render() from Update or a coroutine). LateUpdate has not yet
            /// reapplied or terrain-clamped the entries, so they are left for it.
            /// </summary>
            KeepForLateUpdate,

            /// <summary>First camera after this frame's LateUpdate: run the pass and consume the entries.</summary>
            RunAndConsume,
        }

        /// <summary>
        /// Decides what the Camera.onPreCull hook does with the frame's ghost reapply
        /// entries. Every render after LateUpdate sees the same final pose only if the
        /// entries reach LateUpdate intact: a pre-cull that fires earlier in the frame
        /// (stock kerbal portraits, any mod camera rendered from a coroutine) must not
        /// consume them, or that frame renders surface ghosts at the raw Update pose
        /// without the terrain clamp while the neighbouring frames render them clamped.
        /// </summary>
        internal static GhostCameraPreCullAction ResolveGhostCameraPreCullAction(
            int frameCount,
            int lastLateUpdateReapplyFrame,
            int lastPreCullReapplyFrame,
            int pendingEntryCount)
        {
            if (pendingEntryCount <= 0)
                return GhostCameraPreCullAction.Nothing;
            if (lastLateUpdateReapplyFrame != frameCount)
                return GhostCameraPreCullAction.KeepForLateUpdate;
            if (lastPreCullReapplyFrame == frameCount)
                return GhostCameraPreCullAction.Nothing;
            return GhostCameraPreCullAction.RunAndConsume;
        }

        private void ApplyGhostPosEntries(GhostPositionReapplyPhase phase)
        {
            int skippedNonReFlyPreCull = 0;
            for (int i = 0; i < ghostPosEntries.Count; i++)
            {
                var e = ghostPosEntries[i];
                if (e.ghost == null || !e.ghost.activeSelf) continue;
                if (!ShouldProcessGhostPositionReapply(phase))
                {
                    skippedNonReFlyPreCull++;
                    continue;
                }
                Vector3d positionBeforeReapply = e.ghost.transform.position;

                switch (e.mode)
                {
                    case GhostPosMode.PointInterp:
                    {
                        if (e.bodyBefore == null || e.bodyAfter == null) break;

                        // Phase 1 + Phase 4 spline re-evaluation (design doc
                        // §6.1 / §6.2 / §18 Phase 1+4 / HR-9). Without the
                        // re-eval, the raw lat/lon/alt bracket lerp on the
                        // fallback path would overwrite the spline-positioned
                        // transform on every LateUpdate — Phase 1's smoothing
                        // would only survive a single Update frame before
                        // FloatingOrigin re-positioning snapped it back.
                        // Frame-tag dispatch matches the Update path: tag 0 →
                        // body-fixed surface lookup, tag 1 → inertial re-lower
                        // at the playback UT, unknown → NaN (silent fallthrough
                        // here; the Warn fires from the Update path's
                        // unknownFrameTagWarned dedup so LateUpdate doesn't
                        // double-emit).
                        Vector3d pos;
                        if (!TryComputeLateUpdateSplineWorldPosition(e, out pos))
                        {
                            Vector3d posBefore = e.bodyBefore.GetWorldSurfacePosition(
                                e.latBefore, e.lonBefore, e.altBefore);
                            Vector3d posAfter = e.bodyAfter.GetWorldSurfacePosition(
                                e.latAfter, e.lonAfter, e.altAfter);
                            if (!allowPointHermiteInterpolation(
                                    false,
                                    splineApplied: false,
                                    allowNormalPlaybackHermite: NormalPlaybackPointHermiteEnabled,
                                    out _))
                            {
                                pos = Vector3d.Lerp(posBefore, posAfter, e.t);
                            }
                            else
                            {
                                double hermiteMaxDeviationMeters =
                                    ComputePointHermiteMaxDeviationMeters(posBefore, posAfter);
                                if (!TrajectoryMath.TryInterpolateWorldHermite(
                                        posBefore,
                                        e.velocityBefore,
                                        posAfter,
                                        e.velocityAfter,
                                        e.pointDeltaTimeSeconds,
                                        e.t,
                                        hermiteMaxDeviationMeters,
                                        out pos,
                                        out _,
                                        out _))
                                {
                                    pos = Vector3d.Lerp(posBefore, posAfter, e.t);
                                }
                            }
                        }
                        // Phase 2 + Phase 3 anchor correction (design doc
                        // §6.3 / §6.4 / §18 Phase 2-3): re-apply the world-
                        // space ε so the FloatingOrigin frame-velocity shift
                        // cannot snap the ghost back to the un-corrected
                        // lerped position. Phase 3: the lookup is re-fetched
                        // each LateUpdate so the lerp interval (§6.4 Both
                        // case) progresses with the playback UT instead of
                        // freezing to the value captured at Update time.
                        // The ε is additive on top of either the spline-
                        // positioned or the raw-lerp position above.
                        if (allowRenderAnchorCorrectionInterval(
                                e.anchorRecordingId, e.anchorSectionIndex, e.pointUT,
                                false,
                                out Vector3d lateEps, out _))
                        {
                            pos += lateEps;
                        }
                        ApplyGhostReapplyTransform(
                            e,
                            pos,
                            e.bodyBefore.bodyTransform.rotation * e.interpolatedRot,
                            phase);
                        break;
                    }
                    case GhostPosMode.SinglePoint:
                    {
                        if (e.bodyBefore == null) break;
                        Vector3d pos = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        ApplyGhostReapplyTransform(
                            e,
                            pos,
                            e.bodyBefore.bodyTransform.rotation * e.interpolatedRot,
                            phase);
                        break;
                    }
                    case GhostPosMode.Orbit:
                    {
                        Orbit orbit;
                        if (orbitCache.TryGetValue(e.orbitCacheKey, out orbit))
                        {
                            // Keep the raw orbit position for rotation math
                            // (orbital frame derives radialOut from
                            // pos - orbitBody.position; if pos carries the
                            // tree offset the radial vector is shifted and the
                            // ghost's orbit-frame attitude rolls/pitches
                            // incorrectly). Orbit-tail continuity is also a
                            // rigid render translation from the last authored
                            // point into the predicted tail. Both corrections
                            // are applied to transform.position, not to the
                            // orbital rotation inputs.
                            Vector3d rawOrbitPos = orbit.getPositionAtUT(e.orbitUT);
                            Vector3d orbitContinuityOffset = ResolveOrbitContinuityOffset(e, e.orbitUT);
                            Vector3d targetPos = rawOrbitPos + orbitContinuityOffset;
                            Quaternion targetRot = e.ghost.transform.rotation;

                            // FRAME: lifted to Y-up WORLD at the read, so every LookRotation in
                            // this block pairs a world forward axis with the world radial built
                            // from getPositionAtUT — the same orbital-frame contract
                            // ComputeOrbitalRotation states and every producer encodes in. This
                            // block is a hand-inlined copy of that decode (LateUpdate re-apply
                            // after FloatingOrigin shifts) and MUST move with it.
                            Vector3d vel = TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                                orbit.getOrbitalVelocityAtUT(e.orbitUT));

                            if (e.isSpinning)
                            {
                                // Spin-forward: recompute boundary world rotation (positions may have shifted by FloatingOrigin)
                                Vector3d velAtStart = TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                                    orbit.getOrbitalVelocityAtUT(e.orbitSegmentStartUT));
                                Vector3d posAtStart = orbit.getPositionAtUT(e.orbitSegmentStartUT);

                                if (e.orbitBody != null)
                                {
                                    Vector3d radialAtStart = (posAtStart - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrameAtStart;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)velAtStart).normalized, ((Vector3)radialAtStart).normalized)) > 0.99f)
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, Vector3.up);
                                    else
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, radialAtStart);

                                    Quaternion bwRot = orbFrameAtStart * e.orbitFrameRot;
                                    double dt = e.orbitUT - e.orbitSegmentStartUT;
                                    Vector3 worldAxis = bwRot * e.orbitAngularVelocity;
                                    float angle = (float)((double)e.orbitAngularVelocity.magnitude * dt * Mathf.Rad2Deg);
                                    targetRot = Quaternion.AngleAxis(angle, worldAxis) * bwRot;
                                }
                                else
                                {
                                    // Body null fallback
                                    if (vel.sqrMagnitude > 0.001)
                                        targetRot = Quaternion.LookRotation(vel);
                                    ParsekLog.VerboseRateLimited("Playback", $"orbit-late-body-null-{e.orbitCacheKey}",
                                        $"Orbit LateUpdate: orbitBody null for cache={e.orbitCacheKey}, velocity fallback");
                                }
                            }
                            else if (e.hasOrbitFrameRot && vel.sqrMagnitude > 0.001)
                            {
                                // Orbital-frame-relative path
                                if (e.orbitBody != null)
                                {
                                    // Use rawOrbitPos for radialOut so the
                                    // tree offset doesn't alter orbital-frame
                                    // attitude.
                                    Vector3d radialOut = (rawOrbitPos - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrame;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)vel).normalized, ((Vector3)radialOut).normalized)) > 0.99f)
                                        orbFrame = Quaternion.LookRotation(vel, Vector3.up);
                                    else
                                        orbFrame = Quaternion.LookRotation(vel, radialOut);

                                    targetRot = orbFrame * e.orbitFrameRot;
                                }
                                else
                                {
                                    targetRot = Quaternion.LookRotation(vel);
                                    ParsekLog.VerboseRateLimited("Playback", $"orbit-late-body-null-{e.orbitCacheKey}",
                                        $"Orbit LateUpdate: orbitBody null for cache={e.orbitCacheKey}, velocity fallback");
                                }
                            }
                            else if (vel.sqrMagnitude > 0.001)
                            {
                                // Prograde fallback (old recordings)
                                targetRot = Quaternion.LookRotation(vel);
                            }
                            ApplyGhostReapplyTransform(e, targetPos, targetRot, phase);
                        }
                        break;
                    }
                    case GhostPosMode.CheckpointPoint:
                    {
                        if (e.bodyBefore == null || e.bodyAfter == null) break;
                        Vector3d posBefore = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        Vector3d posAfter = e.bodyAfter.GetWorldSurfacePosition(
                            e.latAfter, e.lonAfter, e.altAfter);
                        Vector3d rawCheckpointPos = Vector3d.Lerp(posBefore, posAfter, e.t);
                        // §7.7 (and any §7.x anchor that lands on a
                        // Checkpoint section): re-apply the world-space ε
                        // here so the FloatingOrigin shift between Update
                        // and LateUpdate cannot snap the ghost back to the
                        // bare lerp position. Phase 3 evaluates ε at the
                        // current playback UT — the lerp interval (§6.4
                        // Both case) progresses each frame, not freezes.
                        if (allowRenderAnchorCorrectionInterval(
                                e.anchorRecordingId, e.anchorSectionIndex, e.pointUT,
                                false,
                                out Vector3d cpLateEps, out _))
                        {
                            rawCheckpointPos += cpLateEps;
                        }
                        Vector3d targetPos = rawCheckpointPos;
                        Quaternion targetRot = e.ghost.transform.rotation;

                        Orbit orbit;
                        if (orbitCache.TryGetValue(e.orbitCacheKey, out orbit))
                        {
                            // FRAME: same world/world orbital-frame contract as the Orbit-mode
                            // block above (and as ComputeOrbitalRotation); the Zup velocity is
                            // lifted at the read so nothing downstream can pair it with the
                            // world radial by accident.
                            Vector3d vel = TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                                orbit.getOrbitalVelocityAtUT(e.orbitUT));

                            if (e.isSpinning)
                            {
                                Vector3d velAtStart = TrajectoryMath.SwizzleZupBodyRelativeToWorld(
                                    orbit.getOrbitalVelocityAtUT(e.orbitSegmentStartUT));
                                Vector3d posAtStart = orbit.getPositionAtUT(e.orbitSegmentStartUT);

                                if (e.orbitBody != null)
                                {
                                    Vector3d radialAtStart = (posAtStart - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrameAtStart;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)velAtStart).normalized, ((Vector3)radialAtStart).normalized)) > 0.99f)
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, Vector3.up);
                                    else
                                        orbFrameAtStart = Quaternion.LookRotation(velAtStart, radialAtStart);

                                    Quaternion bwRot = orbFrameAtStart * e.orbitFrameRot;
                                    double dt = e.orbitUT - e.orbitSegmentStartUT;
                                    Vector3 worldAxis = bwRot * e.orbitAngularVelocity;
                                    float angle = (float)((double)e.orbitAngularVelocity.magnitude * dt * Mathf.Rad2Deg);
                                    targetRot = Quaternion.AngleAxis(angle, worldAxis) * bwRot;
                                }
                                else if (vel.sqrMagnitude > 0.001)
                                {
                                    targetRot = Quaternion.LookRotation(vel);
                                }
                            }
                            else if (e.hasOrbitFrameRot && vel.sqrMagnitude > 0.001)
                            {
                                if (e.orbitBody != null)
                                {
                                    // Use rawCheckpointPos for radialOut so the
                                    // tree offset doesn't alter orbital-frame
                                    // attitude.
                                    Vector3d radialOut = (rawCheckpointPos - (Vector3d)e.orbitBody.position).normalized;
                                    Quaternion orbFrame;
                                    if (Mathf.Abs(Vector3.Dot(((Vector3)vel).normalized, ((Vector3)radialOut).normalized)) > 0.99f)
                                        orbFrame = Quaternion.LookRotation(vel, Vector3.up);
                                    else
                                        orbFrame = Quaternion.LookRotation(vel, radialOut);

                                    targetRot = orbFrame * e.orbitFrameRot;
                                }
                                else
                                {
                                    targetRot = Quaternion.LookRotation(vel);
                                }
                            }
                            else if (vel.sqrMagnitude > 0.001)
                            {
                                targetRot = Quaternion.LookRotation(vel);
                            }
                        }
                        else
                        {
                            targetRot = e.bodyBefore.bodyTransform.rotation * e.interpolatedRot;
                        }
                        ApplyGhostReapplyTransform(e, targetPos, targetRot, phase);
                        break;
                    }
                    case GhostPosMode.Surface:
                    {
                        if (e.bodyBefore == null) break;
                        Vector3d surfacePos = e.bodyBefore.GetWorldSurfacePosition(
                            e.latBefore, e.lonBefore, e.altBefore);
                        ApplyGhostReapplyTransform(
                            e,
                            surfacePos,
                            e.bodyBefore.bodyTransform.rotation * e.surfaceRot,
                            phase);
                        break;
                    }
                    case GhostPosMode.Relative:
                    {
                        Vector3d anchorPos = Vector3d.zero;
                        Quaternion anchorRotation = Quaternion.identity;
                        bool haveAnchor = false;

                        if (!string.IsNullOrEmpty(e.relativeAnchorRecordingId))
                        {
                            haveAnchor = TryResolveRecordedRelativeAnchorPose(
                                e.anchorRecordingId,
                                e.relativeAnchorRecordingId,
                                e.anchorSectionIndex,
                                e.pointUT,
                                out RelativeAnchorPose lateRecordedPose);
                            if (haveAnchor)
                            {
                                anchorPos = lateRecordedPose.worldPos;
                                anchorRotation = lateRecordedPose.worldRotation;
                            }
                        }
                        else if (e.relativeRecordedAnchorPose != null)
                        {
                            haveAnchor = TryResolveStoredRecordedAnchorPose(
                                e.relativeRecordedAnchorPose, out anchorPos, out anchorRotation);
                        }
                        else if (e.relativeLoopLiveAnchor)
                        {
                            // Loop-relative entries keep the explicit live
                            // loop-anchor contract. Non-loop recorded-relative
                            // entries set relativeAnchorRecordingId and resolve
                            // through the branch above.
                            if (TryResolveLoopLiveAnchorPose(
                                    e.anchorVesselId,
                                    e.recordingId,
                                    e.pointUT,
                                    out RelativeAnchorPose liveLoopAnchorPose))
                            {
                                anchorPos = liveLoopAnchorPose.worldPos;
                                anchorRotation = liveLoopAnchorPose.worldRotation;
                                haveAnchor = true;
                            }
                            else
                            {
                                // LateUpdate only reapplies the pose chosen in Update for
                                // the post-FloatingOrigin frame. If the live anchor vanishes
                                // between Update and LateUpdate, the next Update pass reruns
                                // the full live-or-recorded resolver.
                                anchorPos = Vector3d.zero;
                                anchorRotation = Quaternion.identity;
                            }
                        }
                        else
                        {
                            if (e.anchorVesselId != 0u)
                            {
                                Rendering.NonLoopLivePidGuard.NonLoopRelativeLivePidLookupAttempted(
                                    "LateUpdate.Relative");
                            }
                            anchorPos = Vector3d.zero;
                            anchorRotation = Quaternion.identity;
                        }

                        if (haveAnchor)
                        {
                            Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                                anchorPos,
                                anchorRotation,
                                e.relDx,
                                e.relDy,
                                e.relDz);
                            ApplyGhostReapplyTransform(
                                e,
                                ghostPos,
                                TrajectoryMath.ResolveRelativePlaybackRotation(
                                    anchorRotation,
                                    e.relativeRot),
                                phase);
                        }
                        else
                        {
                            // Anchor not loaded — hide ghost (offsets are meters, not lat/lon/alt)
                            e.ghost.SetActive(false);
                        }
                        break;
                    }
                }

                TraceGhostPositionReapply(e, phase, positionBeforeReapply, e.ghost.transform.position);
            }

            LogSkippedNonReFlyCameraPreCull("reapply", skippedNonReFlyPreCull);
        }

        private void ApplyGhostReapplyTransform(
            GhostPosEntry entry,
            Vector3d targetPosition,
            Quaternion targetRotation,
            GhostPositionReapplyPhase phase)
        {
            if (entry.ghost == null)
                return;

            entry.ghost.transform.position = targetPosition;
            entry.ghost.transform.rotation = targetRotation;
        }

        private static string ResolveGhostPosEntryRecordingId(GhostPosEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.recordingId)) return entry.recordingId;
            if (!string.IsNullOrEmpty(entry.anchorRecordingId)) return entry.anchorRecordingId;
            if (!string.IsNullOrEmpty(entry.relativeAnchorRecordingId)) return entry.relativeAnchorRecordingId;
            return null;
        }

        private static void TraceGhostPositionReapply(
            GhostPosEntry entry,
            GhostPositionReapplyPhase phase,
            Vector3d before,
            Vector3d after)
        {
            double delta = Vector3d.Distance(before, after);

            string recordingId =
                !string.IsNullOrEmpty(entry.recordingId) ? entry.recordingId :
                !string.IsNullOrEmpty(entry.anchorRecordingId) ? entry.anchorRecordingId :
                !string.IsNullOrEmpty(entry.relativeAnchorRecordingId) ? entry.relativeAnchorRecordingId :
                null;
            double currentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : entry.pointUT;
            GhostRenderTrace.EmitReapply(
                recordingId,
                entry.hasRecordingIndex ? entry.recordingIndex : -1,
                currentUT,
                entry.pointUT,
                phase == GhostPositionReapplyPhase.CameraPreCull ? "CameraPreCull" : "LateUpdate",
                entry.mode.ToString(),
                before,
                after,
                entry.ghost != null ? entry.ghost.transform.rotation : Quaternion.identity,
                null);

            if (phase != GhostPositionReapplyPhase.CameraPreCull)
                return;

            if (delta < 1.0)
                return;

            if (string.IsNullOrEmpty(recordingId))
                recordingId = "<none>";
            string shortRecordingId = recordingId.Length > 8 ? recordingId.Substring(0, 8) : recordingId;
            string key = "ghost-camera-pre-cull-reapply-" + shortRecordingId + "-" + entry.mode;
            ParsekLog.VerboseRateLimited(
                "Playback",
                key,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Ghost camera pre-cull reapply: rec={0} mode={1} ut={2:F3} deltaMeters={3:F2} before=({4:F2},{5:F2},{6:F2}) after=({7:F2},{8:F2},{9:F2})",
                    shortRecordingId,
                    entry.mode,
                    entry.pointUT,
                    delta,
                    before.x, before.y, before.z,
                    after.x, after.y, after.z),
                1.0);
        }

        internal static bool ShouldProcessGhostPositionReapply(GhostPositionReapplyPhase phase)
        {
            return phase != GhostPositionReapplyPhase.CameraPreCull;
        }

        private static void LogSkippedNonReFlyCameraPreCull(string operation, int count)
        {
            if (count <= 0)
                return;

            ParsekLog.VerboseRateLimited(
                "Playback",
                "ghost-camera-pre-cull-non-refly-skip-" + operation,
                $"Ghost camera pre-cull skipped non-Re-Fly {operation} entries: count={count}",
                1.0);
        }

        private static bool TryResolveStoredRecordedAnchorPose(
            RelativeAnchorPoseSnapshot snapshot,
            out Vector3d anchorPos,
            out Quaternion anchorRotation)
        {
            anchorPos = snapshot != null ? snapshot.worldPos : Vector3d.zero;
            anchorRotation = snapshot != null ? snapshot.worldRotation : Quaternion.identity;
            if (snapshot == null)
                return false;

            if (snapshot.hasSurfacePose)
            {
                CelestialBody bodyBefore = snapshot.bodyBefore;
                CelestialBody bodyAfter = snapshot.bodyAfter ?? bodyBefore;
                if (bodyBefore == null || bodyBefore.bodyTransform == null || bodyAfter == null)
                    return false;

                Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                    snapshot.latBefore,
                    snapshot.lonBefore,
                    snapshot.altBefore);
                Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                    snapshot.latAfter,
                    snapshot.lonAfter,
                    snapshot.altAfter);
                anchorPos = Vector3d.Lerp(posBefore, posAfter, snapshot.t);
                anchorRotation = bodyBefore.bodyTransform.rotation * snapshot.surfaceRot;
            }

            return IsFiniteVector3d(anchorPos);
        }

        /// <summary>
        /// Terrain clamp: prevent ghosts from appearing below terrain surface.
        /// Sub-orbital orbit reconstruction can place ghosts underground (periapsis
        /// below surface), and procedural terrain height varies between sessions.
        /// </summary>
        private void ClampGhostsToTerrain(GhostPositionReapplyPhase phase)
        {
            Vector3d activeVesselPos = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.GetWorldPos3D()
                : Vector3d.zero;

            int skippedNonReFlyPreCull = 0;
            for (int i = 0; i < ghostPosEntries.Count; i++)
            {
                var e = ghostPosEntries[i];
                if (e.ghost == null || !e.ghost.activeSelf) continue;
                if (!ShouldProcessGhostPositionReapply(phase))
                {
                    skippedNonReFlyPreCull++;
                    continue;
                }
                if (e.mode == GhostPosMode.Relative) continue;

                CelestialBody body = (e.mode == GhostPosMode.Orbit) ? e.orbitBody : e.bodyBefore;
                if (body == null || body.pqsController == null) continue;

                Vector3d pos = e.ghost.transform.position;
                double alt = body.GetAltitude(pos);
                if (alt > TerrainClampAltitudeLimit) continue;

                double lat = body.GetLatitude(pos);
                double lon = body.GetLongitude(pos);
                double terrainHeight = body.TerrainAltitude(lat, lon, true);

                // Outside the physics bubble, the rendered terrain mesh uses lower
                // LOD and can overshoot the PQS height at ridges/transitions. Use a
                // larger clearance to prevent the ghost from clipping underground.
                double distToVessel = Vector3d.Distance(pos, activeVesselPos);
                double clearance = ComputeTerrainClearance(distToVessel);

                double clamped = TerrainCorrector.ClampAltitude(alt, terrainHeight, clearance);
                if (clamped > alt)
                {
                    Vector3d beforeClamp = e.ghost.transform.position;
                    Vector3d afterClamp = body.GetWorldSurfacePosition(lat, lon, clamped);
                    e.ghost.transform.position = afterClamp;
                    string traceRecordingId =
                        !string.IsNullOrEmpty(e.recordingId) ? e.recordingId :
                        !string.IsNullOrEmpty(e.anchorRecordingId) ? e.anchorRecordingId :
                        !string.IsNullOrEmpty(e.relativeAnchorRecordingId) ? e.relativeAnchorRecordingId :
                        null;
                    double currentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : e.pointUT;
                    GhostRenderTrace.EmitTerrainClamp(
                        traceRecordingId,
                        e.hasRecordingIndex ? e.recordingIndex : -1,
                        currentUT,
                        e.pointUT,
                        e.mode.ToString(),
                        beforeClamp,
                        afterClamp,
                        alt,
                        terrainHeight,
                        clamped,
                        clearance);
                    ParsekLog.VerboseRateLimited("TerrainCorrect", "clamp-ghost",
                        string.Format(CultureInfo.InvariantCulture,
                            "Ghost terrain clamp: alt={0:F1} terrain={1:F1} -> {2:F1} (clearance={3:F1}m)",
                            alt, terrainHeight, clamped, clearance));
                }
            }

            LogSkippedNonReFlyCameraPreCull("terrain-clamp", skippedNonReFlyPreCull);
        }

        public void StartPlayback()
        {
            if (recording.Count < 2)
            {
                Log("Not enough recording points for playback!");
                return;
            }

            ExitAllWarpForPlaybackStart("Preview");

            // Build preview ghost from CaptureAtStop snapshot (same as timeline ghosts)
            // so that part events play back identically.
            previewRecording = null;
            previewGhostState = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;

            var capture = recorder?.CaptureAtStop;
            if (capture != null)
            {
                previewRecording = capture;
                var buildResult = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    previewRecording, "Parsek_Ghost_Preview");
                if (buildResult != null)
                    ghost = buildResult.root;
                builtFromSnapshot = ghost != null;

                if (builtFromSnapshot)
                {
                    previewGhostState = new GhostPlaybackState
                    {
                        ghost = ghost,
                        playbackIndex = 0,
                        partEventIndex = 0,
                        partTree = GhostVisualBuilder.BuildPartSubtreeMap(
                            GhostVisualBuilder.GetGhostSnapshot(previewRecording)),
                        logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(
                            GhostVisualBuilder.GetGhostSnapshot(previewRecording))
                    };

                    previewGhostState.materials = new List<Material>();

                    GhostPlaybackLogic.PopulateGhostInfoDictionaries(previewGhostState, buildResult);

                    GhostPlaybackLogic.InitializeInventoryPlacementVisibility(previewRecording, previewGhostState);
                    GhostPlaybackLogic.RefreshCompoundPartVisibility(previewGhostState);

                    // Same reentry-potential gate as the timeline engine path (#406):
                    // skip the mesh-combine / ParticleSystem build for trajectories that
                    // cannot possibly produce reentry visuals.
                    if (TrajectoryMath.HasReentryPotential(previewRecording))
                    {
                        previewGhostState.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
                            ghost, previewGhostState.heatInfos, -1, previewRecording.VesselName);
                    }
                    else
                    {
                        previewGhostState.reentryFxInfo = null;
                        ParsekLog.Verbose("ReentryFx",
                            $"Skipped reentry FX build for preview ghost \"{previewRecording.VesselName}\" " +
                            $"— trajectory peak speed below {TrajectoryMath.ReentryPotentialSpeedFloor:F0} m/s and no orbit segments");
                    }

                    // Initialize flag event index for preview playback
                    GhostPlaybackLogic.InitializeFlagVisibility(previewRecording, previewGhostState);

                    Log("Manual preview ghost: built from recording-start snapshot (with part events)");
                }
            }

            // P2 review (PR #706): make the preview Recording visible to the
            // tail-lift resolver so manual preview gets the same descent lift
            // as live watch. CaptureAtStop is never committed to RecordingStore.
            if (builtFromSnapshot && previewRecording != null
                && !string.IsNullOrEmpty(previewRecording.RecordingId))
            {
                RegisterPreviewRecordingForTailLift(previewRecording);
            }

            if (!builtFromSnapshot)
            {
                Color previewColor = Color.green;
                ghost = GhostVisualBuilder.CreateGhostSphere("Parsek_Ghost_Preview", previewColor);
                var m = ghost.GetComponent<Renderer>()?.material;
                previewGhostMaterials = m != null ? new List<Material> { m } : new List<Material>();
                previewGhostState = null;
                previewRecording = null;
                Log("Manual preview ghost: using sphere fallback");
            }
            else
            {
                previewGhostMaterials = previewGhostState.materials;
            }

            ghostObject = ghost;

            // Replay from current time (ghost starts at recording start position)
            playbackStartUT = Planetarium.GetUniversalTime();
            recordingStartUT = recording[0].ut;
            lastPlaybackIndex = 0;

            isPlaying = true;

            Log($"Manual playback started. Duration: {recording[recording.Count - 1].ut - recording[0].ut:F1}s");
            ScreenMessage("Preview Playback STARTED", 2f);
        }

        public void StopPlayback()
        {
            if (isPlaying)
                Log("Manual playback stopped");
            isPlaying = false;

            if (previewGhostState != null)
            {
                if (previewGhostState.materials != null)
                {
                    for (int i = 0; i < previewGhostState.materials.Count; i++)
                    {
                        if (previewGhostState.materials[i] != null)
                            Destroy(previewGhostState.materials[i]);
                    }
                }

                if (previewGhostState.engineInfos != null)
                {
                    foreach (var info in previewGhostState.engineInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                Destroy(info.particleSystems[i].gameObject);
                }

                if (previewGhostState.rcsInfos != null)
                {
                    foreach (var info in previewGhostState.rcsInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                Destroy(info.particleSystems[i].gameObject);
                }

                engine.DestroyReentryFxResources(previewGhostState.reentryFxInfo);

                GhostPlaybackLogic.DestroyAllFakeCanopies(previewGhostState);
                previewGhostState = null;
            }
            else if (previewGhostMaterials != null)
            {
                // Sphere fallback path — no GhostPlaybackState
                for (int i = 0; i < previewGhostMaterials.Count; i++)
                {
                    if (previewGhostMaterials[i] != null)
                        Destroy(previewGhostMaterials[i]);
                }
            }
            previewGhostMaterials.Clear();
            previewRecording = null;
            ClearPreviewRecordingForTailLift();

            if (ghostObject != null)
            {
                Destroy(ghostObject);
                ghostObject = null;
            }

            ClearOrbitPlaybackCaches();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            reFlyDenseAbsolutePlaybackFrameCache.Clear();
            loggedReFlyDenseAbsolutePlaybackFrameSelections.Clear();
        }

        void UpdatePlayback()
        {
            if (ghostObject == null || recording.Count < 2)
            {
                StopPlayback();
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            double elapsedSinceStart = currentUT - playbackStartUT;
            double recordingTime = recordingStartUT + elapsedSinceStart;

            if (recordingTime > recording[recording.Count - 1].ut)
            {
                Log("Manual playback complete — reached end of recording");
                // explosionAlreadyFired=false is safe: preview is one-shot (StopPlayback called immediately after)
                if (previewRecording != null && ghostObject != null
                    && !GhostPlaybackLogic.ShouldSuppressVisualFx(TimeWarp.CurrentRate)
                    && GhostPlaybackLogic.ShouldTriggerExplosion(false, previewRecording.TerminalStateValue,
                           true, previewRecording.VesselName, -1))
                {
                    Vector3 pos = ghostObject.transform.position;
                    float len = GhostVisualBuilder.ComputeGhostLength(ghostObject);
                    double power = Mathf.Clamp01(len / GhostAudioPresets.VesselLengthPowerDivisor);
                    ParsekLog.Info("ExplosionFx",
                        $"Stock FXMonger.Explode for manual preview \"{previewRecording.VesselName}\" " +
                        $"at ({pos.x:F1},{pos.y:F1},{pos.z:F1}) vesselLength={len:F1}m " +
                        $"power={power.ToString("F2", CultureInfo.InvariantCulture)}");
                    // Hide ghost parts before explosion renders
                    if (previewGhostState != null)
                        GhostPlaybackLogic.HideAllGhostParts(previewGhostState);
                    else
                    {
                        var t = ghostObject.transform;
                        for (int c = 0; c < t.childCount; c++)
                            t.GetChild(c).gameObject.SetActive(false);
                    }
                    GhostPlaybackLogic.TryTriggerStockExplosionFxOrCustom(
                        pos,
                        power,
                        len,
                        $"manual preview \"{previewRecording.VesselName}\"");
                }
                StopPlayback();
                ScreenMessage("Preview playback complete", 2f);
                return;
            }

            InterpolationResult interpResult;
            bool surfaceSkip = previewRecording != null &&
                TrajectoryMath.IsSurfaceAtUT(previewRecording.TrackSections, recordingTime);
            InterpolateAndPosition(ghostObject, recording, orbitSegments,
                ref lastPlaybackIndex, recordingTime, 10000, out interpResult,
                skipOrbitSegments: surfaceSkip,
                recordingId: previewRecording?.RecordingId,
                pointFrameSource: "preview-flat-points",
                flatPointSections: previewRecording?.TrackSections);

            if (previewGhostState != null && previewRecording != null)
            {
                previewGhostState.SetInterpolated(interpResult);
                GhostPlaybackLogic.ApplyPartEvents(-1, previewRecording, recordingTime, previewGhostState);
                GhostPlaybackLogic.ApplyFlagEvents(previewGhostState, previewRecording, recordingTime);
                engine.UpdateReentryFx(-1, previewGhostState, previewRecording.VesselName ?? "Preview", TimeWarp.CurrentRate);
            }
        }

        /// <summary>
        /// Returns the GhostChain if this recording is a chain tip that should spawn via VesselGhoster.
        /// Returns null for non-chain recordings, intermediate links, or terminated chains.
        /// </summary>
        internal static GhostChain FindChainTipForRecording(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0)
                return null;

            foreach (var kvp in chains)
            {
                if (kvp.Value.TipRecordingId == rec.RecordingId && !kvp.Value.IsTerminated)
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>
        /// Finds the first committed recording that covers the given UT for a vessel PID.
        /// This is a global PID-only lookup. Chain playback must use the chain-scoped
        /// helper below to avoid selecting a different alternate-history tree that
        /// happens to reuse the same PID.
        /// </summary>
        internal static Recording FindBackgroundRecordingForVessel(
            IReadOnlyList<Recording> committedRecordings,
            uint vesselPid,
            double currentUT,
            ISet<string> excludedTreeIds = null)
        {
            if (committedRecordings == null) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (excludedTreeIds != null
                    && !string.IsNullOrEmpty(rec.TreeId)
                    && excludedTreeIds.Contains(rec.TreeId))
                    continue;
                if (rec.VesselPersistentId == vesselPid &&
                    rec.Points != null && rec.Points.Count > 0 &&
                    currentUT >= rec.StartUT && currentUT <= rec.EndUT)
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// Finds the first committed recording with non-point playback data that covers
        /// the given UT for a vessel PID. Used by chain ghost fallback positioning.
        /// </summary>
        internal static Recording FindPlaybackFallbackRecordingForVessel(
            IReadOnlyList<Recording> committedRecordings,
            uint vesselPid,
            double currentUT,
            ISet<string> excludedTreeIds = null)
        {
            if (committedRecordings == null) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (excludedTreeIds != null
                    && !string.IsNullOrEmpty(rec.TreeId)
                    && excludedTreeIds.Contains(rec.TreeId))
                    continue;
                if (rec.VesselPersistentId != vesselPid)
                    continue;
                if (!RecordingHasUsableNonPointPlaybackDataAtUT(rec, currentUT))
                    continue;

                return rec;
            }

            return null;
        }

        /// <summary>
        /// Finds a point-backed trajectory recording for a chain at the given UT.
        /// Uses the exact chain path within committed trees when available, and only
        /// falls back to global PID lookup before the first claim.
        /// </summary>
        internal static Recording FindBackgroundRecordingForChain(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT)
        {
            if (committedRecordings == null || chain == null)
                return null;

            var chainRec = FindExactChainRecordingAtUT(committedTrees, chain, currentUT);
            if (chainRec != null)
                return chainRec.Points != null && chainRec.Points.Count > 0 ? chainRec : null;

            if (ChainHasStarted(chain, currentUT))
                return null;

            var preClaimRec = FindPreClaimChainRecordingAtUT(
                committedTrees, chain, currentUT,
                out bool hasPreClaimCoverage, out bool preClaimAmbiguous);
            if (hasPreClaimCoverage && !preClaimAmbiguous)
                return preClaimRec != null && preClaimRec.Points != null && preClaimRec.Points.Count > 0
                    ? preClaimRec : null;

            return FindBackgroundRecordingForVessel(
                committedRecordings, chain.OriginalVesselPid, currentUT,
                preClaimAmbiguous ? GetFirstClaimTreeIds(chain) : null);
        }

        internal static Recording FindPreClaimChainRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT,
            out bool hasChainLocalCoverage,
            out bool isAmbiguous)
        {
            hasChainLocalCoverage = false;
            isAmbiguous = false;
            if (committedTrees == null || chain == null || chain.Links == null || chain.Links.Count == 0)
                return null;

            var firstClaimTreeIds = GetFirstClaimTreeIds(chain);
            if (firstClaimTreeIds.Count == 0)
                return null;

            Recording unique = null;
            foreach (var treeId in firstClaimTreeIds)
            {
                RecordingTree firstClaimTree = FindCommittedTree(committedTrees, treeId);
                if (firstClaimTree == null)
                    continue;

                foreach (var rec in firstClaimTree.Recordings.Values)
                {
                    if (rec.VesselPersistentId != chain.OriginalVesselPid)
                        continue;
                    if (currentUT < rec.StartUT || currentUT > rec.EndUT)
                        continue;
                    if (!RecordingHasUsablePlaybackDataAtUT(rec, currentUT))
                        continue;

                    if (unique != null)
                    {
                        isAmbiguous = true;
                        hasChainLocalCoverage = true;
                        return null;
                    }

                    unique = rec;
                }
            }

            hasChainLocalCoverage = unique != null;
            return unique;
        }

        internal static Recording FindExactChainRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees, GhostChain chain, double currentUT)
        {
            if (committedTrees == null || chain == null || chain.Links == null)
                return null;

            for (int i = chain.Links.Count - 1; i >= 0; i--)
            {
                var link = chain.Links[i];
                if (link.ut > currentUT + 0.001)
                    continue;

                return FindChainPathRecordingAtUT(
                    committedTrees, link, chain.OriginalVesselPid, currentUT);
            }

            return null;
        }

        /// <summary>
        /// Finds the best non-point playback source for positioning a chain ghost when
        /// point-backed trajectory playback is unavailable.
        /// </summary>
        internal static Recording FindPositionFallbackRecordingForChain(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            GhostChain chain,
            double currentUT)
        {
            if (chain == null)
                return null;

            var chainRec = FindExactChainRecordingAtUT(committedTrees, chain, currentUT);
            if (RecordingHasUsableNonPointPlaybackDataAtUT(chainRec, currentUT))
                return chainRec;

            if (ChainHasStarted(chain, currentUT))
                return null;

            var preClaimRec = FindPreClaimChainRecordingAtUT(
                committedTrees, chain, currentUT,
                out bool hasPreClaimCoverage, out bool preClaimAmbiguous);
            if (hasPreClaimCoverage && !preClaimAmbiguous)
                return RecordingHasUsableNonPointPlaybackDataAtUT(preClaimRec, currentUT)
                    ? preClaimRec
                    : null;

            return FindPlaybackFallbackRecordingForVessel(
                committedRecordings, chain.OriginalVesselPid, currentUT,
                preClaimAmbiguous ? GetFirstClaimTreeIds(chain) : null);
        }

        private static HashSet<string> GetFirstClaimTreeIds(GhostChain chain)
        {
            var firstClaimTreeIds = new HashSet<string>();
            if (chain == null || chain.Links == null || chain.Links.Count == 0)
                return firstClaimTreeIds;

            double firstClaimUT = double.MaxValue;
            for (int i = 0; i < chain.Links.Count; i++)
            {
                var link = chain.Links[i];
                if (link.ut < firstClaimUT)
                    firstClaimUT = link.ut;
            }

            if (firstClaimUT == double.MaxValue)
                return firstClaimTreeIds;

            for (int i = 0; i < chain.Links.Count; i++)
            {
                var link = chain.Links[i];
                if (string.IsNullOrEmpty(link.treeId))
                    continue;
                if (Math.Abs(link.ut - firstClaimUT) > 0.001)
                    continue;

                firstClaimTreeIds.Add(link.treeId);
            }

            return firstClaimTreeIds;
        }

        private static Recording FindChainPathRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees,
            ChainLink link,
            uint vesselPid,
            double currentUT)
        {
            if (committedTrees == null || string.IsNullOrEmpty(link.treeId))
                return null;

            RecordingTree tree = FindCommittedTree(committedTrees, link.treeId);
            if (tree == null)
                return null;

            Recording startRec;
            if (!tree.Recordings.TryGetValue(link.recordingId, out startRec))
                return null;

            return FindChainPathRecordingAtUT(tree, startRec, vesselPid, currentUT);
        }

        private static Recording FindChainPathRecordingAtUT(
            RecordingTree tree,
            Recording startRec,
            uint vesselPid,
            double currentUT)
        {
            if (tree == null || startRec == null)
                return null;

            var visited = new HashSet<string>();
            var current = startRec;
            while (current != null && !visited.Contains(current.RecordingId))
            {
                visited.Add(current.RecordingId);

                if (current.VesselPersistentId == vesselPid
                    && currentUT >= current.StartUT && currentUT <= current.EndUT)
                {
                    return current;
                }

                var nextChainRec = FindNextChainRecording(tree, current);
                if (nextChainRec != null)
                {
                    current = nextChainRec;
                    continue;
                }

                current = FindPreferredChildRecording(tree, current);
            }

            return null;
        }

        private static Recording FindNextChainRecording(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.ChainId))
                return null;

            int nextIdx = rec.ChainIndex + 1;
            foreach (var kvp in tree.Recordings)
            {
                if (kvp.Value.ChainId == rec.ChainId
                    && kvp.Value.ChainIndex == nextIdx
                    && kvp.Value.ChainBranch == rec.ChainBranch)
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        private static Recording FindPreferredChildRecording(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.ChildBranchPointId))
                return null;

            BranchPoint bp = null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                if (tree.BranchPoints[i].Id == rec.ChildBranchPointId)
                {
                    bp = tree.BranchPoints[i];
                    break;
                }
            }

            if (bp == null || bp.ChildRecordingIds.Count == 0)
                return null;

            string bestChildId = bp.ChildRecordingIds[0];
            if (rec.VesselPersistentId != 0)
            {
                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                {
                    Recording candidate;
                    if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out candidate)
                        && candidate.VesselPersistentId == rec.VesselPersistentId)
                    {
                        bestChildId = bp.ChildRecordingIds[c];
                        break;
                    }
                }
            }

            Recording child;
            return tree.Recordings.TryGetValue(bestChildId, out child) ? child : null;
        }

        private static RecordingTree FindCommittedTree(
            IReadOnlyList<RecordingTree> committedTrees, string treeId)
        {
            if (committedTrees == null || string.IsNullOrEmpty(treeId))
                return null;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (committedTrees[i].Id == treeId)
                    return committedTrees[i];
            }

            return null;
        }

        private static bool ChainHasStarted(GhostChain chain, double currentUT)
        {
            if (chain == null || chain.Links == null)
                return false;

            for (int i = 0; i < chain.Links.Count; i++)
            {
                if (chain.Links[i].ut <= currentUT + 0.001)
                    return true;
            }

            return false;
        }

        private bool TryPositionChainFallbackFromRecording(
            GameObject ghostGO, GhostChain chain, Recording rec, double currentUT)
        {
            if (ghostGO == null || chain == null || rec == null)
                return false;

            if (rec.HasOrbitSegments)
            {
                if (!HasOrbitCoverageAtUT(rec, currentUT))
                    return false;

                PositionGhostFromOrbitOnly(ghostGO, rec, currentUT,
                    (int)(chain.OriginalVesselPid * 10000), allowActivation: true);
                UpdateChainGhostOrbitIfNeeded(chain, rec.OrbitSegments, currentUT);
                ParsekLog.VerboseRateLimited("Flight",
                    "chain-ghost-orbit-" + chain.OriginalVesselPid,
                    string.Format(CultureInfo.InvariantCulture,
                        "Chain ghost positioned from orbit: pid={0} rec={1} tree={2} UT={3:F1}",
                        chain.OriginalVesselPid, rec.RecordingId, rec.TreeId, currentUT));
                return true;
            }

            if (HasSurfaceCoverageAtUT(rec, currentUT))
            {
                PositionGhostAtSurface(ghostGO, rec.SurfacePos.Value, allowActivation: true,
                    recordingId: rec.RecordingId);
                ParsekLog.VerboseRateLimited("Flight",
                    "chain-ghost-surface-" + chain.OriginalVesselPid,
                    string.Format(CultureInfo.InvariantCulture,
                        "Chain ghost positioned at surface: pid={0} rec={1} tree={2} body={3}",
                        chain.OriginalVesselPid, rec.RecordingId, rec.TreeId,
                        rec.SurfacePos.Value.body));
                return true;
            }

            return false;
        }

        private static bool RecordingHasUsablePlaybackDataAtUT(Recording rec, double currentUT)
        {
            if (rec == null)
                return false;

            if (rec.Points != null && rec.Points.Count > 0)
                return true;

            if (HasOrbitCoverageAtUT(rec, currentUT))
                return true;

            return HasSurfaceCoverageAtUT(rec, currentUT);
        }

        private static bool RecordingHasUsableNonPointPlaybackDataAtUT(
            Recording rec, double currentUT)
        {
            if (rec == null)
                return false;

            if (HasOrbitCoverageAtUT(rec, currentUT))
                return true;

            return HasSurfaceCoverageAtUT(rec, currentUT);
        }

        private static bool HasOrbitCoverageAtUT(Recording rec, double currentUT)
        {
            if (rec == null || !rec.HasOrbitSegments)
                return false;

            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                if (currentUT >= rec.OrbitSegments[s].startUT && currentUT <= rec.OrbitSegments[s].endUT)
                    return true;
            }

            return false;
        }

        private static bool HasSurfaceCoverageAtUT(Recording rec, double currentUT)
        {
            return rec != null
                && rec.SurfacePos.HasValue
                && currentUT >= rec.StartUT
                && currentUT <= rec.EndUT;
        }

        /// <summary>
        /// Positions all chain ghosts each frame using background recording trajectory data.
        /// For UTs outside recorded range, falls back to orbital propagation or surface hold.
        /// Spawn-blocked chains are skipped here: the visible ghost of a blocked chain is the
        /// tip recording's own playback ghost, held by the policy past its end UT and drawn at
        /// the tip's propagated position by <see cref="UpdateSpawnBlockedChainTipGhosts"/>
        /// (phase 6b-4, design 12.9.2); the spawn retry runs in SpawnVesselOrChainTip.
        /// </summary>
        private void PositionChainGhosts(double currentUT)
        {
            if (vesselGhoster == null || activeGhostChains == null || activeGhostChains.Count == 0)
                return;

            foreach (var kvp in activeGhostChains)
            {
                GhostChain chain = kvp.Value;

                // Spawn-blocked chains: the tip's held playback ghost is drawn by
                // UpdateSpawnBlockedChainTipGhosts after the policy's held-ghost retries.
                if (chain.SpawnBlocked)
                    continue;

                var info = vesselGhoster.GetGhostedInfo(chain.OriginalVesselPid);
                if (info == null || info.ghostGO == null) continue;

                // Find background recording covering current UT within the chain's
                // own tree sequence, not by global PID-only lookup.
                Recording bgRec = FindBackgroundRecordingForChain(
                    RecordingStore.CommittedRecordings, RecordingStore.CommittedTrees, chain, currentUT);

                if (bgRec != null && bgRec.Points.Count > 0)
                {
                    // Position from trajectory points (same interpolation as existing ghost positioning)
                    bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(bgRec.TrackSections, currentUT);
                    // Phase 1: background path bypasses splines pending section
                    // indexing — the BackgroundRecorder does not yet emit
                    // TrackSections, so there is no body-fixed section index
                    // to feed the spline gate. Tracked in the Phase 1 plan.
                    InterpolateAndPosition(info.ghostGO, bgRec.Points, bgRec.OrbitSegments,
                        ref chain.CachedTrajectoryIndex, currentUT, (int)(chain.OriginalVesselPid * 10000),
                        out _, skipOrbitSegments: surfaceSkip,
                        recordingId: bgRec.RecordingId,
                        pointFrameSource: "chain-flat-points",
                        flatPointSections: bgRec.TrackSections);

                    // Update ghost map ProtoVessel orbit if segment changed
                    UpdateChainGhostOrbitIfNeeded(chain, bgRec.OrbitSegments, currentUT);

                    ParsekLog.VerboseRateLimited("Flight", "chain-ghost-trajectory-" + chain.OriginalVesselPid,
                        string.Format(CultureInfo.InvariantCulture,
                            "Chain ghost positioned from trajectory: pid={0} rec={1} UT={2:F1}",
                            chain.OriginalVesselPid, bgRec.RecordingId, currentUT));
                }
                else
                {
                    PositionChainGhostFallback(info.ghostGO, chain, currentUT);
                }
            }
        }

        /// <summary>
        /// Fallback positioning for chain ghosts: searches committed recordings for any
        /// orbit segment or surface position matching the chain's vessel PID.
        /// </summary>
        private void PositionChainGhostFallback(GameObject ghostGO, GhostChain chain, double currentUT)
        {
            bool positioned = false;
            var committed = RecordingStore.CommittedRecordings;
            var committedTrees = RecordingStore.CommittedTrees;
            if (committed != null)
            {
                var fallbackRec = FindPositionFallbackRecordingForChain(
                    committed, committedTrees, chain, currentUT);
                positioned = TryPositionChainFallbackFromRecording(
                    ghostGO, chain, fallbackRec, currentUT);
            }

            if (!positioned)
            {
                ParsekLog.VerboseRateLimited("Flight",
                    "chain-ghost-no-data-" + chain.OriginalVesselPid,
                    string.Format(CultureInfo.InvariantCulture,
                        "Chain ghost has no positioning data: pid={0} UT={1:F1}",
                        chain.OriginalVesselPid, currentUT));
            }
        }

        /// <summary>
        /// Check if the chain ghost's orbit segment changed and update the ghost map ProtoVessel.
        /// Called each frame after positioning a chain ghost to keep the map orbit line in sync.
        /// </summary>
        private static void UpdateChainGhostOrbitIfNeeded(
            GhostChain chain, List<OrbitSegment> segments, double currentUT)
        {
            if (segments == null || segments.Count == 0) return;

            OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(segments, currentUT);
            if (!seg.HasValue) return;

            if (!TryStampChainMapOrbit(chain, seg.Value))
                return;

            GhostMapPresence.UpdateGhostOrbit(chain.OriginalVesselPid, seg.Value);
        }

        /// <summary>
        /// Stamps the chain's last-rendered map orbit from <paramref name="seg"/> and reports
        /// whether it changed. Change detection reads body, SMA, and eccentricity (covers SOI
        /// transitions, orbit changes, and inclination maneuvers at constant altitude). Exact
        /// equality is intentional: segment values are stored doubles that don't drift, so a
        /// difference means a different OrbitSegment was found, not floating-point accumulation.
        /// An unchanged segment leaves the chain untouched and returns false, which is the
        /// caller's early-out; a changed one writes the three fields and returns true.
        /// </summary>
        internal static bool TryStampChainMapOrbit(GhostChain chain, OrbitSegment seg)
        {
            if (chain == null) return false;

            if (seg.bodyName == chain.LastMapOrbitBodyName
                && seg.semiMajorAxis == chain.LastMapOrbitSma
                && seg.eccentricity == chain.LastMapOrbitEcc)
                return false;

            chain.LastMapOrbitBodyName = seg.bodyName;
            chain.LastMapOrbitSma = seg.semiMajorAxis;
            chain.LastMapOrbitEcc = seg.eccentricity;
            return true;
        }

        /// <summary>
        /// The recording the live recorder writes right now (a split in progress keeps it live
        /// while pendingSplitRecorder holds the recorder), or null.
        /// </summary>
        private string ResolveLiveRecorderRecordingId()
        {
            return ResolveLiveResumedRecordingId(
                (recorder != null && recorder.IsRecording) || pendingSplitRecorder != null,
                activeTree?.ActiveRecordingId);
        }

        /// <summary>
        /// True when <paramref name="rec"/> is the tip of an active ghost chain in this scene, so
        /// its end-of-recording spawn goes through the chain path (VesselGhoster), which owns the
        /// chain cleanup. The policy's warp-time KSC retirement shortcut skips such a tip.
        /// </summary>
        internal bool IsActiveGhostChainTipFromPolicy(Recording rec)
        {
            return rec != null && FindChainTipForRecording(activeGhostChains, rec) != null;
        }

        /// <summary>Called by policy when a mid-chain segment ends while watched.</summary>
        internal int FindNextWatchTargetFromPolicy(int currentIndex, Recording currentRec) =>
            watchMode.FindNextWatchTarget(currentIndex, currentRec);

        /// <summary>Called by policy to transfer watch to the next chain segment.</summary>
        internal bool TransferWatchToNextSegmentFromPolicy(int nextIndex) =>
            watchMode.TransferWatchToNextSegment(nextIndex);

        /// <summary>Called by policy to exit watch mode.</summary>
        internal void ExitWatchModeFromPolicy() => watchMode.ExitWatchModePreservingLineage();

        internal void ExitWatchModePreservingLineage(bool skipCameraRestore = false) =>
            watchMode.ExitWatchModePreservingLineage(skipCameraRestore);

        /// <summary>Called by policy to start the watch-mode hold timer at recording end.</summary>
        internal void StartWatchHoldFromPolicy(
            float holdUntilRealTime,
            double pendingActivationUT = double.NaN,
            float holdMaxRealTime = -1f) =>
            watchMode.StartWatchHold(holdUntilRealTime, pendingActivationUT, holdMaxRealTime);

        private static float GetCurrentWarpRateSafe()
        {
            try
            {
                return TimeWarp.CurrentRate;
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test host does not provide the live KSP warp singleton.
                return 1f;
            }
            catch (MethodAccessException)
            {
                return 1f;
            }
            catch (MissingMethodException)
            {
                return 1f;
            }
        }

        internal static GhostPlaybackSkipReason ResolveGhostPlaybackSkipReason(
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed,
            bool supersededByRelation = false,
            bool rewindRetired = false,
            bool historicalNeverReplayed = false)
        {
            if (!hasRenderableData)
                return GhostPlaybackSkipReason.NoRenderableData;
            if (!playbackEnabled)
                return GhostPlaybackSkipReason.PlaybackDisabled;
            if (rewindRetired)
                return GhostPlaybackSkipReason.RewindRetired;
            if (supersededByRelation)
                return GhostPlaybackSkipReason.SupersededByRelation;
            if (externalVesselSuppressed)
                return GhostPlaybackSkipReason.ExternalVesselSuppressed;
            // BUG-B: keep a purely-historical committed recording (never rewound to
            // replay) dormant. Ordered last so a structural/career skip still wins;
            // only an otherwise-renderable recording is parked as historical.
            if (historicalNeverReplayed)
                return GhostPlaybackSkipReason.HistoricalNotReplayed;
            return GhostPlaybackSkipReason.None;
        }

        private static string BuildGhostSkipReasonIdentity(int index, string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                ? recordingId
                : "idx-" + index.ToString(CultureInfo.InvariantCulture);
        }

        internal static string BuildGhostSkipReasonMessage(
            int index,
            string recordingId,
            string vesselName,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed,
            bool supersededByRelation = false,
            bool rewindRetired = false)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Ghost playback skip state: #{0} id={1} vessel=\"{2}\" skip={3} reason={4} " +
                "hasRenderableData={5} playbackEnabled={6} externalVesselSuppressed={7} " +
                "supersededByRelation={8} rewindRetired={9}",
                index,
                string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId,
                vesselName ?? "?",
                reason != GhostPlaybackSkipReason.None,
                reason.ToLogToken(),
                hasRenderableData,
                playbackEnabled,
                externalVesselSuppressed,
                supersededByRelation,
                rewindRetired);
        }

        private static void LogGhostSkipReasonChangeCore(
            int index,
            string recordingId,
            string vesselName,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed,
            bool supersededByRelation = false,
            bool rewindRetired = false)
        {
            string identity = "ghost-skip|" + BuildGhostSkipReasonIdentity(index, recordingId);
            ParsekLog.VerboseOnChange(
                "Flight",
                identity,
                reason.ToLogToken(),
                BuildGhostSkipReasonMessage(
                    index,
                    recordingId,
                    vesselName,
                    reason,
                    hasRenderableData,
                    playbackEnabled,
                    externalVesselSuppressed,
                    supersededByRelation,
                    rewindRetired));
        }

        private void ClearGhostSkipReasonLogState()
        {
            activeGhostSkipReasonLogIdentities.Clear();
            ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix("Flight", "ghost-skip|");
        }

        internal void ClearGhostSkipReasonLogStateForTesting()
        {
            ClearGhostSkipReasonLogState();
        }

        internal static void LogGhostSkipReasonChangeForTesting(
            int index,
            string recordingId,
            string vesselName,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool playbackEnabled,
            bool externalVesselSuppressed,
            bool supersededByRelation = false)
        {
            LogGhostSkipReasonChangeCore(
                index,
                recordingId,
                vesselName,
                reason,
                hasRenderableData,
                playbackEnabled,
                externalVesselSuppressed,
                supersededByRelation);
        }

        private void LogGhostSkipReasonChangeIfNeeded(
            int index,
            Recording rec,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool externalVesselSuppressed,
            bool supersededByRelation = false,
            bool rewindRetired = false)
        {
            string identity = BuildGhostSkipReasonIdentity(index, rec?.RecordingId);
            bool hasLoggedActiveSkip = activeGhostSkipReasonLogIdentities.Contains(identity);
            if (reason == GhostPlaybackSkipReason.None && !hasLoggedActiveSkip)
                return;

            LogGhostSkipReasonChangeCore(
                index,
                rec?.RecordingId,
                rec?.VesselName,
                reason,
                hasRenderableData,
                rec?.GhostVisible ?? false,
                externalVesselSuppressed,
                supersededByRelation,
                rewindRetired);

            if (reason == GhostPlaybackSkipReason.None)
                activeGhostSkipReasonLogIdentities.Remove(identity);
            else
                activeGhostSkipReasonLogIdentities.Add(identity);
        }

        internal struct FastForwardWatchHandoffDecision
        {
            public bool canEnterWatch;
            public bool warn;
            public int index;
            public bool recordingExists;
            public bool playbackEnabled;
            public bool activeGhost;
            public GhostPlaybackSkipReason skipReason;
            public string reason;
            public string recordingId;
            public string vesselName;
        }

        internal static FastForwardWatchHandoffDecision ClassifyFastForwardWatchHandoff(
            string pendingRecordingId,
            IReadOnlyList<Recording> committed,
            TrajectoryPlaybackFlags[] flags,
            Func<int, bool> hasActiveGhost)
        {
            var stale = new FastForwardWatchHandoffDecision
            {
                canEnterWatch = false,
                warn = false,
                index = -1,
                recordingExists = false,
                playbackEnabled = false,
                activeGhost = false,
                skipReason = GhostPlaybackSkipReason.None,
                reason = "stale-recording-id",
                recordingId = pendingRecordingId,
                vesselName = "?"
            };

            if (string.IsNullOrEmpty(pendingRecordingId) || committed == null)
                return stale;

            for (int i = 0; i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || rec.RecordingId != pendingRecordingId)
                    continue;

                bool activeGhost = hasActiveGhost != null && hasActiveGhost(i);
                GhostPlaybackSkipReason skipReason = flags != null && i < flags.Length
                    ? flags[i].skipReason
                    : ResolveGhostPlaybackSkipReason(
                        GhostPlaybackEngine.HasRenderableGhostData(rec),
                        rec.GhostVisible,
                        externalVesselSuppressed: false);
                string reason = activeGhost
                    ? "active-ghost"
                    : (skipReason != GhostPlaybackSkipReason.None
                        ? skipReason.ToLogToken()
                        : "active-ghost-missing");
                return new FastForwardWatchHandoffDecision
                {
                    canEnterWatch = activeGhost,
                    warn = !activeGhost,
                    index = i,
                    recordingExists = true,
                    playbackEnabled = rec.GhostVisible,
                    activeGhost = activeGhost,
                    skipReason = skipReason,
                    reason = reason,
                    recordingId = rec.RecordingId,
                    vesselName = rec.VesselName
                };
            }

            return stale;
        }

        internal static string BuildFastForwardWatchHandoffMessage(
            FastForwardWatchHandoffDecision decision,
            double currentUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Deferred FF watch handoff {0}: id={1} index={2} vessel=\"{3}\" " +
                "committedExists={4} playbackEnabled={5} currentUT={6:F1} activeGhost={7} " +
                "skipReason={8} reason={9}",
                decision.canEnterWatch ? "ready" : "failed",
                string.IsNullOrEmpty(decision.recordingId) ? "(none)" : decision.recordingId,
                decision.index,
                decision.vesselName ?? "?",
                decision.recordingExists,
                decision.playbackEnabled,
                currentUT,
                decision.activeGhost,
                decision.skipReason.ToLogToken(),
                decision.reason ?? "(none)");
        }

        private static void LogFastForwardWatchHandoff(
            FastForwardWatchHandoffDecision decision,
            double currentUT)
        {
            string message = BuildFastForwardWatchHandoffMessage(decision, currentUT);
            if (decision.warn)
                ParsekLog.Warn("CameraFollow", message);
            else
                ParsekLog.Verbose("CameraFollow", message);
        }

        internal static bool ResolveReFlySettleStabilityForTesting(
            Recording rec,
            int frame,
            out string anchorRecordingId,
            out string reason)
        {
            return ResolveReFlySettleStability(rec, frame, out anchorRecordingId, out reason);
        }

        private static bool ResolveReFlySettleStability(
            Recording rec,
            int frame,
            out string anchorRecordingId,
            out string reason)
        {
            anchorRecordingId = null;
            reason = null;
            if (rec == null)
                return false;

            if (rec.IsDebris
                && !string.IsNullOrEmpty(rec.ParentAnchorRecordingId)
                && TryGetReFlySettleStabilityReasonForRecording(
                    rec.ParentAnchorRecordingId, frame, out reason))
            {
                anchorRecordingId = rec.ParentAnchorRecordingId;
                return true;
            }

            // Defensive: covers loop/overlap re-arming paths that may evaluate the settled
            // recording itself, even though the live Re-Fly focus normally does not render as a ghost.
            if (!string.IsNullOrEmpty(rec.RecordingId)
                && TryGetReFlySettleStabilityReasonForRecording(
                    rec.RecordingId, frame, out reason))
            {
                anchorRecordingId = rec.RecordingId;
                return true;
            }

            return false;
        }

        private static bool TryGetReFlySettleStabilityReasonForRecording(
            string recordingId,
            int frame,
            out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            if (FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(recordingId))
            {
                reason = "settle-active";
                return true;
            }

            return ReFlySettleStabilityTracker.TryGetHoldReasonForRecording(
                recordingId, frame, out reason);
        }

        private void RecordReFlyAnchorHoldCandidate(string anchorRecordingId, string reason)
        {
            if (string.IsNullOrEmpty(anchorRecordingId))
                return;

            int count;
            reFlyAnchorHoldCountsThisFrame.TryGetValue(anchorRecordingId, out count);
            reFlyAnchorHoldCountsThisFrame[anchorRecordingId] = count + 1;
            if (!reFlyAnchorHoldReasonsThisFrame.ContainsKey(anchorRecordingId))
                reFlyAnchorHoldReasonsThisFrame[anchorRecordingId] = reason ?? "(none)";
        }

        private void LogReFlyAnchorHoldTransitions(int frame)
        {
            foreach (var kvp in reFlyAnchorHoldCountsThisFrame)
            {
                if (reFlyAnchorHoldStartFrameByAnchor.ContainsKey(kvp.Key))
                    continue;

                reFlyAnchorHoldStartFrameByAnchor[kvp.Key] = frame;
                string reason;
                reFlyAnchorHoldReasonsThisFrame.TryGetValue(kvp.Key, out reason);
                ParsekLog.Info("ReFlySettle",
                    $"Anchor ghost visibility hold engaged: anchorRec={ShortRecordingId(kvp.Key)} " +
                    $"ghosts={kvp.Value.ToString(CultureInfo.InvariantCulture)} " +
                    $"reason={reason ?? "(none)"} frame={frame.ToString(CultureInfo.InvariantCulture)}");
            }

            reFlyAnchorHoldReleaseScratch.Clear();
            foreach (var kvp in reFlyAnchorHoldStartFrameByAnchor)
            {
                if (!reFlyAnchorHoldCountsThisFrame.ContainsKey(kvp.Key))
                    reFlyAnchorHoldReleaseScratch.Add(kvp.Key);
            }

            for (int i = 0; i < reFlyAnchorHoldReleaseScratch.Count; i++)
            {
                string anchorId = reFlyAnchorHoldReleaseScratch[i];
                int startFrame = reFlyAnchorHoldStartFrameByAnchor[anchorId];
                int heldFrames = Math.Max(0, frame - startFrame);
                ParsekLog.Info("ReFlySettle",
                    $"Anchor ghost visibility hold released: anchorRec={ShortRecordingId(anchorId)} " +
                    $"heldFrames={heldFrames.ToString(CultureInfo.InvariantCulture)} " +
                    $"frame={frame.ToString(CultureInfo.InvariantCulture)}");
                reFlyAnchorHoldStartFrameByAnchor.Remove(anchorId);
            }

            reFlyAnchorHoldReleaseScratch.Clear();
        }

        private static bool HasAnchorReFlyUnstableFlag(TrajectoryPlaybackFlags[] flags)
        {
            if (flags == null)
                return false;

            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i].anchorReFlyUnstable)
                    return true;
            }

            return false;
        }

        private void LogReFlySettleActiveVesselPoseIfArmed(string phase, int frame)
        {
            if (frame != reFlySettlePoseLogActiveFrame)
                return;

            Vector3d pos = FlightGlobals.ActiveVessel != null
                ? (Vector3d)FlightGlobals.ActiveVessel.transform.position
                : Vector3d.zero;
            ParsekLog.Verbose("ReFlySettle",
                $"Active vessel pose {phase}: pos={DiagnosticFormatters.FormatVector3d(pos)} " +
                $"frame={frame.ToString(CultureInfo.InvariantCulture)} " +
                $"activeVessel={(FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : "(none)")}");
        }

        /// <summary>
        /// Pure-static predicate for <c>TrajectoryPlaybackFlags.isChainSeamSuccessor</c>: the
        /// successor qualifies for chain-seam handoff treatment iff its branch-0 predecessor in
        /// the same chain currently has a ghost state and the current UT is at or past the
        /// successor's own seam UT — i.e. its first playable payload sample (chain handoff
        /// moment).
        /// <para>
        /// The seam UT is the SUCCESSOR's activation-start UT, NOT the predecessor's
        /// <c>Recording.EndUT</c>. The predecessor's <c>EndUT</c> is the recording's outer
        /// semantic envelope and is widened by <c>ExplicitEndUT</c> (orbit-tail projections,
        /// Re-Fly tail estimates), so on a chain-head with an orbit projection the predecessor
        /// <c>EndUT</c> can sit far past the actual chain handoff (the Kerbal X playtest
        /// 2026-05-19 had predecessor <c>EndUT</c>=1289 while the seam was at UT 123.54). The
        /// successor's activation UT is the true seam contract because that is when the engine
        /// first-spawns the successor.
        /// </para>
        /// <para>
        /// The <c>1e-6</c> epsilon mirrors other "at or past" comparisons in the engine
        /// (e.g. early debris completion at <c>GhostPlaybackEngine.cs</c>) so floating-point
        /// dust around an exact endpoint match doesn't flip the predicate frame-to-frame.
        /// </para>
        /// </summary>
        internal static bool IsChainSeamSuccessor(
            double currentUT, double successorSeamUT, bool predecessorHasGhostState)
        {
            if (!predecessorHasGhostState) return false;
            return currentUT + 1e-6 >= successorSeamUT;
        }

        private bool IsChainSeamSuccessorAtFrame(Recording rec, double currentUT)
        {
            // Cheap call-site early-outs for the standalone-recording majority. GetChainPredecessorIndex
            // performs the same checks before its O(N) scan, but the function call itself is the cost
            // we save here — this runs once per committed recording per frame inside ComputePlaybackFlags
            // (200+ recording saves spent most of that budget on entry/exit of the helper).
            if (rec == null) return false;
            if (string.IsNullOrEmpty(rec.ChainId)) return false;
            if (rec.ChainBranch != 0) return false;
            if (rec.ChainIndex <= 0) return false;

            int predIdx = RecordingChainQueries.GetChainPredecessorIndex(rec);
            if (predIdx < 0) return false;
            var committed = RecordingStore.CommittedRecordings;
            if (predIdx >= committed.Count) return false;
            Recording predecessor = committed[predIdx];
            if (predecessor == null) return false;
            bool predHasGhost = engine != null
                && engine.TryGetGhostState(predIdx, out GhostPlaybackState predState)
                && predState != null;
            double seamUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec);
            return IsChainSeamSuccessor(currentUT, seamUT, predHasGhost);
        }

        /// <summary>
        /// Pre-computes policy flags for all committed recordings. Called once per frame
        /// before the engine's update loop. Eliminates per-recording RecordingStore queries
        /// from the hot path (was O(n^2), now O(n)).
        /// </summary>
        private TrajectoryPlaybackFlags[] ComputePlaybackFlags(
            IReadOnlyList<Recording> committed,
            double currentUT,
            ReFlySessionMarker activeReFlyMarker)
        {
            var flags = new TrajectoryPlaybackFlags[committed.Count];
            if (ghostCommNetInputs == null || ghostCommNetInputs.Length != committed.Count)
                ghostCommNetInputs = new GhostCommNetEligibilityInput[committed.Count];
            int frame = FlightRecorder.GetFrameCount();
            reFlyAnchorHoldCountsThisFrame.Clear();
            reFlyAnchorHoldReasonsThisFrame.Clear();
            var scenario = ParsekScenario.Instance;
            var timelineInactiveIds = EffectiveState.ComputeTimelineInactiveRecordingIds(
                committed,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingRewindRetirements);
            // Spawn-suppression diagnostic is accumulated per-reason across the whole
            // committed list and emitted as ONE batched summary after the loop (see the
            // batch-counting convention in CLAUDE.md): a per-recording line here was the
            // single worst log-spam source (one VerboseOnChange line per recording per
            // frame, ballooning to thousands of lines/second once the committed list grows
            // and a recording's reason oscillates frame-to-frame). The per-reason histogram
            // preserves the "what is suppressed and why" signal without the per-item churn.
            var spawnSuppressedByReason = spawnSuppressedReasonScratch;
            spawnSuppressedByReason.Clear();
            int spawnSuppressedCount = 0;
            int liveAnchorSuppressedCount = 0;
            // The recording the player is flying right now never spawns its terminal
            // vessel (todo REFLY-FORK-SELF-SPAWN-AFTER-QUICKSAVE): the Re-Fly session's
            // recording and the live recorder's recording (a split in progress keeps the
            // recording live while pendingSplitRecorder holds the recorder).
            string activeReFlyRecordingId = activeReFlyMarker?.ActiveReFlyRecordingId;
            string liveRecorderRecordingId = ResolveLiveRecorderRecordingId();
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                bool hasData = GhostPlaybackEngine.HasRenderableGhostData(rec);

                bool chainLooping = rec.IsChainRecording &&
                    RecordingChainQueries.IsChainLooping(rec.ChainId);

                bool externalVesselSuppressed = GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                    rec, IsActiveTreeRecording(rec));
                // Loop-membership term, mirroring the map side's loopMemberInWindow gate.
                // Computed here (the later BUG-B dormancy block reuses it) so the Step-2
                // suppression below can scope itself to loop members only.
                bool loopingLike = rec.LoopPlayback
                    || chainLooping
                    || (cachedLoopUnits != null && cachedLoopUnits.IsMember(i));
                // Step 2 (Logistics route live-anchor bind): suppress a LOOP MEMBER's OWN
                // loop ghost as a live-anchor duplicate ONLY while its launch-matched live
                // vessel is loaded AND it was resolved as the LIVE docking anchor of an
                // in-window relative member during this-or-the-previous frame (the Step-1
                // live-bind event, captured at RelativeAnchorResolver). NOT for the whole
                // loop: RealVesselExistsForRecording is true for EVERY parked route craft
                // loaded in the scene, so the earlier whole-loop gate hid every delivery
                // mesh when a fresh new-mission launch watched the looped route (18 parked
                // craft loaded => all 11 route meshes suppressed). The inbound delivery
                // member is the resolver FOCUS, never the resolved anchor, so it is never
                // in the bind set and is never suppressed - its mesh draws docking the live
                // station; only the station BEING docked right now is hidden as a duplicate.
                //
                // One-frame lag is correct and safe: ComputePlaybackFlags runs BEFORE
                // engine.UpdatePlayback (where positioning fires the Step-1 binds), so this
                // predicate reads the PREVIOUS frame's live-bind set. A continuously-docking
                // anchor re-stamps every frame, so WasLiveBoundThisOrLastFrame's <=1
                // tolerance keeps it suppressed every frame; a docking that just started or
                // ended flashes the duplicate at most ~1 frame (sub-perceptual at the
                // ~20 km standoff). Guid gate (VesselLaunchIdentity) means a same-craft
                // DIFFERENT-launch live vessel never suppresses; loopingLike MATCHES the
                // map-side loopMemberInWindow carve-out so the gate never reaches the active
                // recording, an active re-fly session, or a tree-history ghost.
                bool liveAnchorDoubleSuppressed =
                    GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(rec, loopingLike);
                if (liveAnchorDoubleSuppressed)
                {
                    externalVesselSuppressed = true;
                    liveAnchorSuppressedCount++;
                    ParsekLog.VerboseRateLimited(
                        "Anchor",
                        "step2-live-bind-suppress|" + (rec.RecordingId ?? "(none)"),
                        "Step-2 suppressed live-anchor double (live-bind event): rec="
                            + (rec.RecordingId ?? "(none)")
                            + " vessel=\"" + (rec.VesselName ?? "(none)") + "\""
                            + " liveBoundThisOrLastFrame=true"
                            + " (own ghost hidden while a delivery member is docking its launch-matched live vessel)",
                        5.0);
                }
                TimelineInactiveReason inactiveReason = TimelineInactiveReason.None;
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    timelineInactiveIds.TryGetValue(rec.RecordingId, out inactiveReason);
                bool supersededByRelation = inactiveReason == TimelineInactiveReason.SupersededByRelation;
                bool rewindRetired = inactiveReason == TimelineInactiveReason.RewindRetired;

                // BUG-B: keep a purely-historical committed recording dormant during
                // normal forward play. A recording only replays (and spawns its
                // terminal vessel) when the player rewound so its window lies ahead of
                // the live playhead; PlaybackScopeTracker latches that per recording.
                // Looping / chain-loop / mission-unit members (explicit live-replay
                // opt-in) keep RENDERING regardless, but their first-run terminal spawn
                // is a real spawn and takes the same gate (spawnHistoricalNeverReplayed).
                // The active re-fly session is exempt from both. Every committed
                // recording is noted each frame so a later rewind re-arms its scope.
                double activationStartUT = GhostPlaybackEngine.ResolveGhostActivationStartUT(rec);
                PlaybackScopeTracker.NotePlayhead(rec.RecordingId, currentUT, activationStartUT);
                bool scopeHistorical = PlaybackScopeTracker.IsHistoricalNeverReplayed(
                    rec.RecordingId, currentUT, activationStartUT);
                bool historicalNeverReplayed = GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                    loopingLike, activeReFlyMarker != null, scopeHistorical, forSpawn: false);
                bool spawnHistoricalNeverReplayed = GhostPlaybackLogic.ResolveHistoricalNeverReplayed(
                    loopingLike, activeReFlyMarker != null, scopeHistorical, forSpawn: true);

                GhostPlaybackSkipReason skipReason = ResolveGhostPlaybackSkipReason(
                    hasData,
                    rec.GhostVisible,
                    externalVesselSuppressed,
                    supersededByRelation,
                    rewindRetired,
                    historicalNeverReplayed);
                LogGhostSkipReasonChangeIfNeeded(
                    i,
                    rec,
                    skipReason,
                    hasData,
                    externalVesselSuppressed,
                    supersededByRelation,
                    rewindRetired);

                bool liveSameLaunchVesselPresent =
                    GhostPlaybackLogic.ResolveRewindSuppressionLiveLaunchPresence(rec);
                var spawnResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    rec, isActiveChainMember: false, treeContext: null,
                    liveSameLaunchVesselPresent);

                var chainSuppressed = activeGhostChains != null
                    ? GhostPlaybackLogic.ShouldSuppressSpawnForChain(activeGhostChains, rec)
                    : (suppressed: false, reason: "");

                bool liveFlownRecording = GhostPlaybackLogic.IsLiveFlownRecording(
                    rec.RecordingId, activeReFlyRecordingId, liveRecorderRecordingId);

                bool finalNeedsSpawn = GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(
                    spawnResult.needsSpawn,
                    chainSuppressed.suppressed,
                    supersededByRelation,
                    rewindRetired,
                    spawnHistoricalNeverReplayed,
                    liveFlownRecording);
                string anchorReFlyUnstableAnchorId;
                string anchorReFlyUnstableReason;
                bool anchorReFlyUnstable = ResolveReFlySettleStability(
                    rec, frame, out anchorReFlyUnstableAnchorId, out anchorReFlyUnstableReason);
                if (anchorReFlyUnstable)
                    RecordReFlyAnchorHoldCandidate(
                        anchorReFlyUnstableAnchorId, anchorReFlyUnstableReason);

                // Accumulate the spawn-suppression reason for non-debris recordings into
                // the per-reason histogram; the batched summary is emitted after the loop.
                if (!finalNeedsSpawn && !rec.IsDebris)
                {
                    // A loop member keeps its spawn-gate reason first (its render is not
                    // history-gated); the history gate names itself only when it is the
                    // gate that actually held a loop member's first-run spawn.
                    string reason = liveFlownRecording
                        ? GhostPlaybackLogic.LiveFlownRecordingSpawnReason
                        : historicalNeverReplayed
                        ? "historical-never-replayed"
                        : !spawnResult.needsSpawn ? spawnResult.reason
                        : chainSuppressed.suppressed ? chainSuppressed.reason
                        : spawnHistoricalNeverReplayed ? "historical-never-replayed"
                        : chainSuppressed.reason;
                    string reasonKey = string.IsNullOrEmpty(reason) ? "(none)" : reason;
                    spawnSuppressedByReason.TryGetValue(reasonKey, out int reasonCount);
                    spawnSuppressedByReason[reasonKey] = reasonCount + 1;
                    spawnSuppressedCount++;
                }

                bool isMidChain = RecordingChainQueries.IsChainMidSegment(rec);
                double chainEndUT = RecordingChainQueries.GetChainEndUT(rec);
                // Ghost CommNet relay window inputs (design 15.6): the same gates as the
                // ghost, minus the display-only playback toggle, plus the real-run scope
                // gate (the spawn variant, so a loop member's first run is gated too).
                GhostCommNetEligibilityInput commNetInput = new GhostCommNetEligibilityInput
                {
                    HasRecordingId = !string.IsNullOrEmpty(rec.RecordingId),
                    IsDebris = rec.IsDebris,
                    HasRenderableData = hasData,
                    SupersededByRelation = supersededByRelation,
                    RewindRetired = rewindRetired,
                    SessionSuppressed = activeReFlyMarker != null
                        && SessionSuppressionState.IsSuppressedRecordingIndex(i),
                    ExternalVesselSuppressed = externalVesselSuppressed,
                    HistoricalNeverReplayed = spawnHistoricalNeverReplayed,
                    ActivationStartUT = activationStartUT,
                    EndUT = rec.EndUT,
                    NeedsSpawn = finalNeedsSpawn,
                    VesselSpawned = rec.VesselSpawned,
                    SpawnAbandoned = rec.SpawnAbandoned,
                    CannotSpawnSafely = rec.TerminalSpawnCannotSpawnSafely,
                    IsMidChain = isMidChain,
                    ChainEndUT = chainEndUT,
                };
                // Scenario 15: a vessel whose terminal spawn a later continuation owns (an
                // intermediate ghost-chain link, or a superseded terminal spawn) stays on rails
                // at its end state until that continuation takes it over; its node is held there.
                if (hasData && !rec.IsDebris && !supersededByRelation && !rewindRetired
                    && !spawnHistoricalNeverReplayed)
                {
                    GhostCommNetMath.ApplyContinuationHold(
                        ref commNetInput, rec, committed, activeGhostChains,
                        spawnResult.needsSpawn && chainSuppressed.suppressed,
                        GhostPlaybackLogic.RealVesselExistsForRecording,
                        "FLIGHT", ghostCommNetClaimScratch, ghostCommNetCarrierScratch);
                }
                ghostCommNetInputs[i] = commNetInput;

                flags[i] = new TrajectoryPlaybackFlags
                {
                    skipGhost = skipReason != GhostPlaybackSkipReason.None,
                    skipReason = skipReason,
                    skipReasonDetail = skipReason == GhostPlaybackSkipReason.None
                        ? null
                        : BuildGhostSkipReasonMessage(
                            i,
                            rec.RecordingId,
                            rec.VesselName,
                            skipReason,
                            hasData,
                            rec.GhostVisible,
                            externalVesselSuppressed,
                            supersededByRelation,
                            rewindRetired),
                    isMidChain = isMidChain,
                    chainEndUT = chainEndUT,
                    needsSpawn = finalNeedsSpawn,
                    isActiveChainMember = false,
                    isChainLooping = chainLooping,
                    isChainSeamSuccessor = IsChainSeamSuccessorAtFrame(rec, currentUT),
                    segmentLabel = RecordingSegmentLabels.GetSegmentPhaseLabel(rec),
                    recordingId = rec.RecordingId,
                    vesselPersistentId = rec.VesselPersistentId,
                    sessionSuppressedRenderCarveOutEligible =
                        RecordingEligibleForSessionSuppressedRenderCarveOut(rec),
                    sessionSuppressed = activeReFlyMarker != null
                        && SessionSuppressionState.IsSuppressedRecordingIndex(i),
                    anchorReFlyUnstable = anchorReFlyUnstable,
                };
            }
            // One batched, rate-limited summary instead of one line per suppressed
            // recording. Shared key + VerboseRateLimited hard-caps the rate so the
            // per-frame summary cannot spam even when the committed list is large or a
            // reason oscillates frame-to-frame. The Step-2 live-anchor double-suppression
            // is an externalVesselSuppressed skip already covered by the generic
            // skip-reason logging (LogGhostSkipReasonChangeIfNeeded) plus its own
            // dedicated "Anchor" Verbose line at the decision site above.
            if (spawnSuppressedCount > 0)
            {
                ParsekLog.VerboseRateLimited(
                    "Spawner",
                    "spawn-suppressed-summary",
                    () => BuildSpawnSuppressedSummary(spawnSuppressedCount, spawnSuppressedByReason),
                    2.0);
            }
            // One batched, rate-limited summary (shared key) for the Step-2 live-anchor
            // double-suppression count this frame, so the per-recording decision lines
            // above (themselves rate-limited) are backed by a single aggregate even when
            // the per-recording lines are throttled.
            if (liveAnchorSuppressedCount > 0)
            {
                int suppressedThisFrame = liveAnchorSuppressedCount;
                ParsekLog.VerboseRateLimited(
                    "Anchor",
                    "step2-live-anchor-double-summary",
                    () => "Step-2 live-anchor-double: suppressed="
                        + suppressedThisFrame.ToString(CultureInfo.InvariantCulture)
                        + " this frame (gate=live-bind-event)",
                    2.0);
            }
            LogReFlyAnchorHoldTransitions(frame);
            return flags;
        }

        /// <summary>
        /// Builds the batched spawn-suppression summary line: a total plus a
        /// per-reason histogram sorted by descending count (ties broken by reason text
        /// for determinism). Replaces the former one-line-per-recording diagnostic that
        /// was the worst log-spam source. Pure for unit testing.
        /// </summary>
        internal static string BuildSpawnSuppressedSummary(
            int total, IReadOnlyDictionary<string, int> byReason)
        {
            string head = "Spawn suppressed: "
                + total.ToString(CultureInfo.InvariantCulture) + " recording(s)";
            if (byReason == null || byReason.Count == 0)
                return head;

            var ordered = new List<KeyValuePair<string, int>>(byReason);
            ordered.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            var parts = new List<string>(ordered.Count);
            for (int r = 0; r < ordered.Count; r++)
                parts.Add(ordered[r].Key + "=" + ordered[r].Value.ToString(CultureInfo.InvariantCulture));
            return head + " | " + string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// Host-side row gate for render-only session-suppression carve-outs.
        /// Route-specific narrowing stays inside the playback engine.
        /// </summary>
        internal static bool RecordingEligibleForSessionSuppressedRenderCarveOut(Recording rec)
        {
            return rec != null
                && rec.MergeState != MergeState.NotCommitted
                && !string.IsNullOrWhiteSpace(rec.RecordingId);
        }

        /// <summary>
        /// Engine-based timeline playback. Builds FrameContext and TrajectoryPlaybackFlags,
        /// delegates per-frame ghost positioning to GhostPlaybackEngine, then applies
        /// resource deltas and watch-mode validity checks.
        /// </summary>
        void UpdateTimelinePlaybackViaEngine()
        {
            GhostPlaybackLogic.InvalidateVesselCache();
            if (GhostPlaybackLogic.ShouldSkipTimelinePlaybackForPendingReFlyInvoke(RewindInvokeContext.Pending))
            {
                ParsekLog.VerboseRateLimited("Flight", "timeline-playback-skip-refly-invoke",
                    "UpdateTimelinePlaybackViaEngine: skipped while re-fly post-load invocation is pending");
                return;
            }

            var committed = RecordingStore.CommittedRecordings;
            double currentUT = Planetarium.GetUniversalTime();

            // Chain ghost positioning (independent of engine)
            PositionChainGhosts(currentUT);
            // Held-back vessels before their first claim, drawn from the despawn snapshot.
            UpdateClaimedVesselHoldGhosts(currentUT);

            // Pre-engine: detect spawned vessels that died since last frame (#132)
            policy.RunSpawnDeathChecks();

            // Flush deferred spawns from warp (policy owns the queue)
            policy.FlushDeferredSpawns();

            if (committed.Count == 0)
            {
                // Removes any ghost CommNet node left over from a deleted recording.
                DriveGhostCommNet(committed, currentUT);
                UpdateSpawnBlockedChainTipGhosts(currentUT);
                return;
            }

            // Snapshot the active re-fly marker once per frame so the per-recording
            // suppression bit (computed inside ComputePlaybackFlags) and the FrameContext
            // copy below see the same value. All marker writers run outside this
            // synchronous call chain, so a single snapshot is exact, not approximate.
            ReFlySessionMarker activeReFlyMarker = SessionSuppressionState.ActiveMarker;

            // Pre-compute flags
            cachedFlags = ComputePlaybackFlags(committed, currentUT, activeReFlyMarker);
            var flags = cachedFlags;

            // Build frame context
            var ctx = new FrameContext
            {
                currentUT = currentUT,
                warpRate = TimeWarp.CurrentRate,
                warpRateIndex = TimeWarp.CurrentRateIndex,
                activeVesselPos = FlightGlobals.ActiveVessel != null
                    ? (Vector3d)FlightGlobals.ActiveVessel.transform.position
                    : Vector3d.zero,
                protectedIndex = watchMode.WatchedRecordingIndex,
                protectedLoopCycleIndex = watchMode.WatchedLoopCycleIndex,
                externalGhostCount = activeGhostChains?.Count ?? 0,
                mapViewEnabled = MapView.MapIsEnabled,
                autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                    ?? LoopTiming.DefaultLoopIntervalSeconds,
                activeReFlyMarker = activeReFlyMarker,
            };

            // Build trajectory list (Recording implements IPlaybackTrajectory)
            cachedTrajectories.Clear();
            for (int i = 0; i < committed.Count; i++)
                cachedTrajectories.Add(committed[i]);

            // === Mission loop-unit drive (Phase D2) ===
            // Recompute the LoopUnitSet only when the inputs that shape it change. A cheap signature
            // string over the looping mission's identity/selection/cadence plus the committed list
            // identity gates the allocating, Verbose-logging MissionLoopUnitBuilder.Build. The cached
            // set is pushed into the engine EVERY frame (member-index alignment is the engine's
            // contract), but Build (and its log) only fires on an actual input change.
            DriveMissionLoopUnits(committed);

            // === Re-aim per-window transfer substitution (Phase 3c) ===
            // For each looping mission classified as a cross-parent single-hop interplanetary transfer,
            // replace the OWNER member's recorded trajectory with a per-window re-aimed transfer (the
            // recorded geometry re-planned to the target's actual position at this synodic window). The
            // resolver caches by window, so the live Lambert/PatchedConics solve runs only when the
            // window advances. Non-re-aim units + non-owner members are untouched (faithful path).
            SubstituteReaimTrajectories(currentUT);

            if (HasAnchorReFlyUnstableFlag(flags))
            {
                reFlySettlePoseLogActiveFrame = Time.frameCount;
                LogReFlySettleActiveVesselPoseIfArmed("pre-engine", Time.frameCount);
            }

            // Run engine rendering
            engine.UpdatePlayback(cachedTrajectories, flags, ctx);

            // Deferred watch after fast-forward: enter watch mode on the FF target
            // now that the engine has positioned ghosts for the new UT.
            if (pendingWatchAfterFFId != null)
            {
                string ffId = pendingWatchAfterFFId;
                pendingWatchAfterFFId = null;
                FastForwardWatchHandoffDecision handoff =
                    ClassifyFastForwardWatchHandoff(
                        ffId,
                        committed,
                        flags,
                        idx => watchMode.HasActiveGhost(idx));
                if (handoff.canEnterWatch)
                {
                    watchMode.EnterWatchMode(handoff.index);
                    if (watchMode.IsWatchingGhost && watchMode.WatchedRecordingIndex == handoff.index)
                    {
                        ParsekLog.Info("CameraFollow",
                            $"Deferred FF watch entered: #{handoff.index}");
                    }
                    else
                    {
                        handoff.canEnterWatch = false;
                        handoff.warn = true;
                        handoff.reason = "enter-watch-refused";
                        LogFastForwardWatchHandoff(handoff, currentUT);
                    }
                }
                else
                {
                    LogFastForwardWatchHandoff(handoff, currentUT);
                }
            }

            // Retry held ghost spawns (#96): ghosts held at final position while
            // waiting for a blocked/deferred spawn to resolve
            policy.RetryHeldGhostSpawns();

            // Spawn-blocked chain tips (phase 6b-4, design 12.9.2): draw each held tip ghost
            // at the tip's propagated position, or release it once the tip spawned.
            UpdateSpawnBlockedChainTipGhosts(currentUT);

            // Create deferred ghost map ProtoVessels when ghosts enter orbital segments.
            // Phase 8d.0: routed through the scene adapter; MapViewScene.DriveMapPresence delegates
            // VERBATIM to policy.CheckPendingMapVessels(currentUT) with the identical argument. Pure
            // indirection — same method, same arg, same slot, same surrounding guard/try-catch.
            mapViewScene.DriveMapPresence(Planetarium.GetUniversalTime());

            // Ghost CommNet relay / control-point nodes (design 15.6). After the spawn
            // passes above; DriveGhostCommNet re-reads each recording's spawn state, so a
            // vessel spawned this frame hands its node over to the real vessel's stock node
            // in the same frame.
            DriveGhostCommNet(committed, currentUT);

            // Phase 4 decision-only shadow: run the new map-render pipeline (chain -> sample -> intent)
            // over the live map ghosts and reconcile each intent against the OLD path's rendered truth
            // via MapRenderProbe. Writes NOTHING to the stock surfaces. Gated on ShadowRenderDriver.Enabled
            // (mapRenderTracing OR the director-drive gate, which is default on since it needs the
            // StockConic seed). Runs after CheckPendingMapVessels (map presence is current) and well
            // before the end-of-frame probe.
            if (MapRender.ShadowRenderDriver.Enabled)
            {
                try
                {
                    mapViewScene.SetFrameInputs(cachedLoopUnits, Planetarium.GetUniversalTime());
                    MapRender.ShadowRenderDriver.RunFrame(mapViewScene);
                }
                catch (System.Exception ex)
                {
                    // The shadow is diagnostic-only and must NEVER break the live flight update. Log
                    // (rate-limited) and carry on; the old render path is untouched regardless.
                    ParsekLog.VerboseRateLimited("MapRender", "shadow-run-exception",
                        "shadow RunFrame threw (suppressed): " + ex.GetType().Name + ": " + ex.Message, 10.0);
                }
            }

            // Phase F: per-frame resource delta application removed.
            // The standalone applier (ApplyResourceDeltas, gated by ManagesOwnResources)
            // and the tree lump-sum applier (ApplyTreeResourceDeltas/ApplyTreeLumpSum)
            // were the dual source of truth alongside the ledger. The ledger is now
            // the single source of truth: GameActions/RecalculationEngine drives
            // Funding.Instance.Funds, ResearchAndDevelopment.Instance.Science, and
            // Reputation.Instance.reputation via RecalculateAndPatch.

            // Watch-mode ghost validity check
            watchMode.ValidateWatchedGhostStillActive();
        }

        /// <summary>
        /// Feeds every committed recording's relay-window verdict (and every chain-ghosted real
        /// vessel) to the ghost CommNet manager (design 15.6). Runs each frame after the spawn
        /// passes; the verdict inputs come from <see cref="ComputePlaybackFlags"/>.
        /// </summary>
        private void DriveGhostCommNet(IReadOnlyList<Recording> committed, double currentUT)
        {
            try
            {
                if (ghostCommNet == null)
                    ghostCommNet = new GhostCommNetManager("FLIGHT", TryResolveGhostCommNetRecordingPosition);

                ghostCommNetCandidates.Clear();
                int count = committed != null ? committed.Count : 0;
                bool inputsAligned = ghostCommNetInputs != null && ghostCommNetInputs.Length == count;
                var scenario = ParsekScenario.Instance;
                for (int i = 0; inputsAligned && i < count; i++)
                {
                    Recording rec = committed[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;
                    GhostCommNetEligibilityInput input = ghostCommNetInputs[i];
                    // ComputePlaybackFlags ran BEFORE engine.UpdatePlayback and the spawn
                    // passes; re-read the spawn state so a vessel spawned this frame drops its
                    // ghost node this frame, not the next.
                    input.VesselSpawned = rec.VesselSpawned;
                    input.SpawnAbandoned = rec.SpawnAbandoned;
                    input.CannotSpawnSafely = rec.TerminalSpawnCannotSpawnSafely;
                    GhostCommNetEligibility eligibility = GhostCommNetMath.EvaluateEligibility(input, currentUT);
                    // The chain successor scan only runs for a segment that would otherwise
                    // hold in a chain gap (past its end, every other gate passed).
                    if (eligibility.Reason == GhostCommNetMath.ReasonChainGapHold)
                    {
                        int next = GhostPlaybackLogic.ResolveChainNextSlotIndex(
                            i, committed,
                            object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes);
                        input.ChainSuccessorStarted = next >= 0
                            && currentUT >= GhostPlaybackEngine.ResolveGhostActivationStartUT(committed[next]);
                        if (input.ChainSuccessorStarted)
                            eligibility = GhostCommNetMath.EvaluateEligibility(input, currentUT);
                    }
                    ghostCommNetCandidates.Add(new GhostCommNetCandidate
                    {
                        Key = rec.RecordingId,
                        RecordingId = rec.RecordingId,
                        RecordingIndex = i,
                        Recording = rec,
                        VesselName = rec.VesselName,
                        VesselPid = rec.VesselPersistentId,
                        LaunchGuid = rec.RecordedVesselGuid,
                        Eligibility = eligibility,
                        WindowStartUT = input.ActivationStartUT,
                        EndUT = input.EndUT,
                    });
                }

                // Chain-ghosted real vessels: before its first claim a despawned vessel sits
                // on rails, so it relays from its despawn snapshot. From the claim on, the
                // committed recordings that carry it relay instead, and between two of them
                // (a recording ended, the next claim or recording of the vessel not started)
                // the ended one's continuation hold keeps the vessel at its end state. A chain
                // holds back every vessel its tip ends, each until its own first claim.
                if (activeGhostChains != null && vesselGhoster != null)
                {
                    foreach (var kv in activeGhostChains)
                    {
                        GhostChain chain = kv.Value;
                        if (chain == null) continue;
                        List<ChainClaimedVessel> held = chain.HeldBackVessels();
                        for (int h = 0; h < held.Count; h++)
                        {
                            ChainClaimedVessel claim = held[h];
                            var info = vesselGhoster.GetGhostedInfo(claim.Pid);
                            if (info == null || info.snapshot == null) continue;
                            bool preClaim = currentUT < claim.FirstClaimUT;
                            ghostCommNetCandidates.Add(new GhostCommNetCandidate
                            {
                                Key = GhostCommNetMath.ChainKey(claim.Pid),
                                RecordingIndex = -1,
                                ChainSnapshot = info.snapshot,
                                VesselName = info.vesselName,
                                VesselPid = claim.Pid,
                                LaunchGuid = claim.LaunchGuid,
                                Eligibility = new GhostCommNetEligibility
                                {
                                    Eligible = preClaim,
                                    Reason = preClaim ? "chain-pre-claim" : "chain-claimed",
                                },
                                WindowStartUT = double.NegativeInfinity,
                                EndUT = claim.FirstClaimUT,
                            });
                        }
                    }
                }

                ghostCommNet.Tick(ghostCommNetCandidates, currentUT);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(GhostCommNetMath.Tag, "flight-drive-exception",
                    "DriveGhostCommNet threw (suppressed): " + ex.GetType().Name + ": " + ex.Message, 10.0);
            }
        }

        /// <summary>
        /// Mesh-independent world position of a committed recording at a UT, for its ghost
        /// CommNet node. The index hint is verified against the recording id because the
        /// committed list can shift between the host tick and the CommNet rebuild.
        /// </summary>
        internal bool TryResolveGhostCommNetRecordingPosition(
            string recordingId, int indexHint, double ut, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || string.IsNullOrEmpty(recordingId))
                return false;
            int index = indexHint;
            if (index < 0 || index >= committed.Count || committed[index] == null
                || committed[index].RecordingId != recordingId)
            {
                index = -1;
                for (int i = 0; i < committed.Count; i++)
                {
                    if (committed[i] != null && committed[i].RecordingId == recordingId)
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                    return false;
            }
            return TryResolvePlaybackWorldPosition(index, committed[index], null, ut, out worldPos);
        }

        /// <summary>The flight scene's ghost CommNet manager (in-game tests); null before the first tick.</summary>
        internal GhostCommNetManager GhostCommNet => ghostCommNet;

        // Replaces each re-aim loop unit member's heliocentric leg in cachedTrajectories with the
        // per-window re-aimed transfer (Phase 3c). Runs after DriveMissionLoopUnits (so cachedLoopUnits
        // is fresh) and before engine.UpdatePlayback. Iterates ALL members of a re-aim unit: the
        // resolver substitutes only the member(s) that actually carry a heliocentric leg (the transfer
        // leg of the mission chain) and leaves launch / arrival / debris members faithful (their
        // body-relative segments already follow their bodies). The resolver caches by window, so this is
        // cheap on non-advancing frames. The map-presence / tracking-station orbit path resolves the
        // SAME segments from the shared resolver, so the map orbit line stays in sync with the flight ghost.
        //
        // SUPPLY-ROUTE RE-AIM AUDIT (Phase 3): the unioned cachedLoopUnits now also carries route
        // backing-mission units. This re-aim reader (and the LogReaimGhostTrace diagnostic) are
        // re-aim-IRRELEVANT for v0 route members: route units are built with bodyInfo passed but never
        // carry a ReaimPlan (v0 is same-body, no re-aim / synodic windows - design §0.8), so unit.IsReaim
        // is false for every route unit and the `if (!unit.IsReaim ...) continue;` guard below skips them
        // entirely. Neither reader gates member TEARDOWN (teardown is the engine's job; the member-loss
        // watch-exit gate is IsMember in DriveMissionLoopUnits, which already reads the unioned set), so
        // no further wiring is needed here - route members simply never appear in the re-aim substitution.
        private void SubstituteReaimTrajectories(double currentUT)
        {
            if (cachedLoopUnits == null || cachedLoopUnits.Count == 0)
                return;

            int substituted = 0;
            foreach (var kv in cachedLoopUnits.UnitsByOwner)
            {
                GhostPlaybackLogic.LoopUnit unit = kv.Value;
                if (!unit.IsReaim || unit.MemberIndices == null)
                    continue;

                for (int m = 0; m < unit.MemberIndices.Length; m++)
                {
                    int memberIdx = unit.MemberIndices[m];
                    if (memberIdx < 0 || memberIdx >= cachedTrajectories.Count)
                        continue;
                    IPlaybackTrajectory inner = cachedTrajectories[memberIdx];
                    if (inner == null)
                        continue;

                    IPlaybackTrajectory sub = Parsek.Reaim.ReaimPlaybackResolver.Shared.ResolveForFrame(
                        inner.RecordingId, inner, unit.ReaimPlan.Value, unit.ReaimSchedule.Value,
                        unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds,
                        currentUT, out long windowIndex);

                    if (!ReferenceEquals(sub, inner))
                    {
                        cachedTrajectories[memberIdx] = sub;
                        substituted++;
                        ParsekLog.VerboseRateLimited("Reaim", $"sub-{memberIdx}",
                            $"re-aim member={memberIdx} id={inner.RecordingId} window={windowIndex} heliocentric leg substituted");
                    }
                }
            }
            if (substituted > 0)
                ParsekLog.VerboseRateLimited("Reaim", "sub-summary",
                    $"re-aim substituted {substituted} member trajectory(ies) this frame");
        }

        // Recompute the Mission LoopUnitSet only when its inputs change, then push the cached set
        // into the engine every frame. The signature captures everything MissionLoopUnitBuilder.Build
        // reads that can move the unit: the looping mission's identity (Id), tree (TreeId), cadence
        // (LoopIntervalSeconds + LoopTimeUnit), phase anchor (LoopAnchorUT), and selection (both the
        // sorted ExcludedThroughLineHeadIds and the interval-level ExcludedIntervalKeys that actually
        // drive the members + span), plus the looping tree's topology (branch + recording counts) and
        // the committed-list identity (count + a rolling hash of RecordingIds, since member indices
        // are committed-list indices). No looping mission -> a constant "none:" prefix over the same
        // committed signature, so toggling looping off still rebuilds to Empty exactly once.
        private void DriveMissionLoopUnits(IReadOnlyList<Recording> committed)
        {
            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;
            // Phase-lock (mission periodicity): pass the live-body seam so a supported looping
            // mission snaps its phase anchor to the next faithful launch window. An unsupported
            // config (cross-parent / rendezvous / no constraint) falls back to today's behavior.
            IBodyInfo bodyInfo = FlightGlobalsBodyInfo.Instance;
            // Zero-drift mode (pinned Loose): how a looped mission's transited-body landing rotation is treated.
            TransitedBodyRotationMode tbrMode = ParsekSettings.LandingBodyAlignmentMode;
            // Force-faithful knob (harness-only since the 2026-08-27 settings simplification; set
            // via the M-A2 command seam): when on, a re-aim-SUPPORTED mission stays on the verbatim
            // recorded trajectory instead of auto-engaging re-aim. Folded into the signature too, so
            // a flipped value rebuilds the cached set.
            bool forceFaithful = ParsekSettings.Current?.forceFaithfulLoopPlayback ?? false;

            // === Supply-route render union (Phase 3) ===
            // Append each ghost-driving route's route-owned backing Mission to a NEW unioned list
            // (MissionStore.Missions is IReadOnlyList<Mission> and cannot be appended in place). The
            // unioned list is the SINGLE argument to the unchanged MissionLoopUnitBuilder.Build /
            // BuildSignature, so route missions fold into the existing signature + owner/member
            // collision logging automatically (Phase 2's mutual exclusion prevents a same-tree
            // route+manual collision upstream; Build's owner-collision drop stays purely defensive).
            double routeSelectUT = Planetarium.GetUniversalTime();
            IReadOnlyList<Mission> routeMissions =
                Parsek.Logistics.RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                    Parsek.Logistics.RouteStore.CommittedRoutes, routeSelectUT);
            List<Mission> unioned = new List<Mission>(MissionStore.Missions);
            unioned.AddRange(routeMissions);

            string signature = MissionLoopUnitBuilder.BuildSignature(
                unioned, RecordingStore.CommittedTrees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode,
                forceFaithful);
            if (!string.Equals(signature, lastLoopUnitSignature, StringComparison.Ordinal))
            {
                // Loop seam markers (design-dock-event-graph.md 7.3): the FLIGHT scene is the only
                // consumer (the engine raises the events; KSC / TS get no markers by design), so the
                // dock-event graph is threaded in HERE rather than read inside the pure builder. The
                // cache is signature-gated, and this whole block only runs when the loop-unit
                // signature moved, so this is a handful of calls per topology change - never per
                // frame. A null graph (nothing committed yet) simply yields marker-free units.
                builtLoopUnits = MissionLoopUnitBuilder.Build(
                    unioned, RecordingStore.CommittedTrees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode,
                    forceFaithful, DockEventGraphCache.GetOrBuild());
                lastLoopUnitSignature = signature;
                // The committed set / missions changed: drop every cached per-window re-aim adapter so a
                // stale window transfer can never survive a recording edit / re-classification.
                Parsek.Reaim.ReaimPlaybackResolver.Shared.Clear();
                ParsekLog.Verbose("Mission",
                    $"Mission loop units rebuilt (signature changed): committed={committed?.Count ?? 0} " +
                    $"routeMissions={routeMissions.Count}");
            }
            // M-A7 render-composition PLAN capture (capture point 1). Called every frame so the
            // recorder always holds the live unit set for its per-frame clock sampling; the plan
            // RECORD itself is signature-deduped inside NotePlan, so it is appended once per builder
            // signature per host. Instant no-op when the manifest env gate is unarmed.
            Parsek.MapRender.RenderCompositionRecorder.NotePlan(
                "Flight", signature, builtLoopUnits, committed, unioned);
            // Publish the LIVE view: a unit before its first loop instance is left out, so its
            // members play their first run as ordinary recordings (operator ruling 2026-09-24).
            cachedLoopUnits = MissionLoopUnitBuilder.ResolveLiveUnits(
                "Flight", builtLoopUnits, cachedLoopUnits, routeSelectUT, committed);
            engine.SetLoopUnits(cachedLoopUnits);

            // If the player is WATCHING a member of a mission loop and the new selection just
            // dropped that member from the unit (interval trimmed out, the loop turned off, or
            // every segment unchecked), exit watch NOW - this runs BEFORE the engine's
            // UpdatePlayback tears the member's ghost down (and, for a Destroyed-terminal member,
            // explodes it). Otherwise the policy starts a watch-hold on the now-dying ghost and the
            // camera latches onto a destroyed transform, leaving FlightCamera.Target null and
            // flooding stock KSP (FlightGlobals / Sun / CrewHatchController / AmbienceControl) with
            // NREs. Gated on WatchedLoopCycleIndex >= 0 so only loop-cycle watches are affected (a
            // plain non-loop watch is never a unit member and must not be disturbed).
            if (watchMode != null
                && watchMode.WatchedRecordingIndex >= 0
                && watchMode.WatchedLoopCycleIndex >= 0
                && !cachedLoopUnits.IsMember(watchMode.WatchedRecordingIndex))
            {
                ParsekLog.Info("CameraFollow",
                    $"Watched mission member #{watchMode.WatchedRecordingIndex} left the loop unit " +
                    "(interval trimmed out or loop off) - exiting watch before its ghost is torn down");
                watchMode.ExitWatchModePreservingLineage();
            }
        }

        // UpdateTimelinePlayback removed (T25 Phase 9 — engine is primary path)
        // ValidateWatchedGhostStillActive moved to WatchModeController

        // FlushDeferredSpawns moved to ParsekPlaybackPolicy (#132)

        internal IEnumerable<RecordingAnchorCandidate> BuildGhostRecordingAnchorCandidatesForRecorder(
            RecordingTree focusTree,
            string focusedRecordingId,
            uint focusedVesselPid,
            double ut)
        {
            cachedGhostRecordingAnchorCandidates.Clear();
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (!ShouldRecordGhostAnchorCandidates(marker))
            {
                ParsekLog.VerboseRateLimited("Anchor", "ghost-recording-anchor-candidates-disabled",
                    $"Ghost recording anchor candidates skipped: reason=no-active-refly " +
                    $"focusRecordingId={focusedRecordingId ?? "(none)"} focusedVesselPid={focusedVesselPid}",
                    5.0);
                return cachedGhostRecordingAnchorCandidates;
            }

            if (engine == null
                || focusTree?.Recordings == null
                || cachedTrajectories.Count == 0)
            {
                return cachedGhostRecordingAnchorCandidates;
            }

            int scanned = 0;
            int skippedUnpositioned = 0;
            int skippedScope = 0;
            int skippedActiveProvisional = 0;
            int unresolved = 0;
            var context = BuildFlightRelativeAnchorResolverContext(
                focusTree,
                focusedRecordingId,
                marker);

            foreach (GhostAnchorCandidate candidate in engine.GetActiveAnchorCandidates(cachedTrajectories))
            {
                scanned++;
                if (!candidate.PositionedThisFrame)
                {
                    skippedUnpositioned++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(candidate.RecordingId)
                    || string.Equals(candidate.RecordingId, focusedRecordingId, StringComparison.Ordinal))
                {
                    skippedScope++;
                    continue;
                }
                if (marker != null
                    && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                    && string.Equals(
                        marker.ActiveReFlyRecordingId,
                        candidate.RecordingId,
                        StringComparison.Ordinal))
                {
                    skippedActiveProvisional++;
                    continue;
                }
                if (!focusTree.Recordings.TryGetValue(candidate.RecordingId, out Recording recording)
                    || recording == null)
                {
                    skippedScope++;
                    continue;
                }
                if (recording.LoopAnchorVesselId != 0u)
                {
                    skippedScope++;
                    continue;
                }

                if (!RelativeAnchorResolver.TryResolveAnchorPose(
                        context,
                        candidate.RecordingId,
                        ut,
                        new HashSet<string>(StringComparer.Ordinal),
                        out AnchorPose pose,
                        out _))
                {
                    unresolved++;
                    continue;
                }

                cachedGhostRecordingAnchorCandidates.Add(
                    new RecordingAnchorCandidate(
                        candidate.RecordingId,
                        pose.WorldPos,
                        pose.WorldRotation,
                        AnchorCandidateSource.Ghost,
                        recording.VesselPersistentId,
                        candidate.Index));
            }

            ParsekLog.VerboseRateLimited("Anchor", "ghost-recording-anchor-candidates",
                $"Ghost recording anchor candidates: focusRecordingId={focusedRecordingId ?? "(none)"} " +
                $"scanned={scanned} added={cachedGhostRecordingAnchorCandidates.Count} " +
                $"skippedUnpositioned={skippedUnpositioned} skippedScope={skippedScope} " +
                $"skippedActiveProvisional={skippedActiveProvisional} unresolved={unresolved} " +
                $"focusedVesselPid={focusedVesselPid}",
                5.0);

            return cachedGhostRecordingAnchorCandidates;
        }

        internal static bool ShouldRecordGhostAnchorCandidates(ReFlySessionMarker marker)
        {
            return marker != null && !string.IsNullOrWhiteSpace(marker.ActiveReFlyRecordingId);
        }

        private static RelativeAnchorResolverContext BuildFlightRelativeAnchorResolverContext(
            RecordingTree focusTree,
            string focusedRecordingId,
            ReFlySessionMarker marker)
        {
            return new RelativeAnchorResolverContext(
                focusTree,
                focusRecordingId: focusedRecordingId,
                focusTreeId: focusTree?.Id,
                activeReFlyMarker: marker,
                pendingTree: PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null,
                absoluteWorldPositionResolver: ResolveAnchorResolverPointWorldPosition,
                bodyWorldRotationResolver: ResolveAnchorResolverBodyWorldRotation,
                orbitalCheckpointPoseResolver: TryResolveFlightOrbitalAnchorPose,
                tryResolveLiveAnchorTransform: TryGetLiveAnchorTransformDelegate(),
                tryResolveLiveLaunchMatchedAnchorPose: TryResolveLiveLaunchMatchedAnchorPoseForResolver);
        }

        // Logistics route live-anchor bind (Step 1): resolves the LIVE pose of an
        // anchor recording's own launch-matched live vessel for the relative
        // resolver. Returns null (recorded-anchor fallback) when: the recording is
        // null / has no pid; the launch-matched live vessel is not loaded (guid-gated
        // RealVesselExistsForRecording, so a same-craft DIFFERENT-launch live vessel
        // never binds); or the live transform is unavailable. Reuses
        // TryResolveLoopLiveAnchorPose so the anchor frame is the recorder's
        // vesselTransform.position / rotation (NOT GetWorldPos3D/CoMD), keeping the
        // docking attitude correct. Headless-safe: RealVesselExistsForRecording and
        // FindVesselByPid both return false/null without FlightGlobals.
        //
        // The bind is guid-GATED (RealVesselExistsForRecording) but the transform is
        // then resolved by pid (FindVesselByPid inside TryResolveLoopLiveAnchorPose).
        // KSP regenerates a craft-baked pid on collision with a currently-live vessel,
        // so two same-craft launches cannot be simultaneously loaded sharing the pid;
        // the pid-only transform resolve therefore cannot pick the wrong live vessel
        // after the guid gate passes.
        //
        // UT-ALIGNMENT: `ut` is intentionally unused here — the bind returns the live
        // anchor's CURRENT transform, not its pose at `ut`. Correct for the steady
        // route-station case; for the broadening risk on non-looped relative ghosts
        // whose live anchor has moved on, see the "Known limitation / playtest-verify"
        // note in docs/dev/todo-and-known-bugs.md (2026-06-13 entry).
        internal static (Vector3d pos, Quaternion rot)? TryResolveLiveLaunchMatchedAnchorPoseForResolver(
            Recording anchorRec,
            double ut)
        {
            if (anchorRec == null || anchorRec.VesselPersistentId == 0u)
                return null;

            ParsekFlight instance = Instance;
            if (instance == null)
                return null;

            if (!GhostPlaybackLogic.RealVesselExistsForRecording(anchorRec))
                return null;

            if (!instance.TryResolveLoopLiveAnchorPose(
                    anchorRec.VesselPersistentId,
                    anchorRec.RecordingId,
                    ut,
                    out RelativeAnchorPose pose))
            {
                return null;
            }

            // Step 1 (the live-bind pose) is all that happens here. The resolver stamps
            // the live-bind ledger (RelativeAnchorResolver.RecordLiveBoundAnchor) on the
            // source=live bind event, which the Step-2 double-suppression consumes one
            // frame later via RelativeAnchorResolver.WasLiveBoundThisOrLastFrame: the
            // anchor's own loop ghost is hidden ONLY while a relative member is actually
            // docking its live vessel (scoped to the live-bind event), NOT for the whole
            // loop merely because its guid-matched live vessel is loaded.
            return (pose.worldPos, pose.worldRotation);
        }

        internal static Func<uint, string, double, (Vector3d pos, Quaternion rot)?> TryGetLiveAnchorTransformDelegate()
        {
            return LiveAnchorTransformDelegate;
        }

        private static (Vector3d pos, Quaternion rot)? TryResolveLiveAnchorTransformForResolver(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT)
        {
            ParsekFlight instance = Instance;
            if (instance == null)
                return null;

            if (!instance.TryResolveLoopLiveAnchorPose(
                    anchorVesselId,
                    victimRecordingId,
                    targetUT,
                    out RelativeAnchorPose pose))
            {
                return null;
            }

            return (pose.worldPos, pose.worldRotation);
        }

        private static Vector3d ResolveAnchorResolverPointWorldPosition(TrajectoryPoint point)
        {
            CelestialBody body = FindBodyForAnchorResolver(point.bodyName);
            if (body == null)
                return new Vector3d(double.NaN, double.NaN, double.NaN);

            return body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
        }

        private static Quaternion ResolveAnchorResolverBodyWorldRotation(TrajectoryPoint point)
        {
            CelestialBody body = FindBodyForAnchorResolver(point.bodyName);
            return body != null && body.bodyTransform != null
                ? body.bodyTransform.rotation
                : Quaternion.identity;
        }

        private static bool TryResolveFlightOrbitalAnchorPose(
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

                CelestialBody body = FindBodyForAnchorResolver(segment.bodyName);
                if (body == null)
                    return false;

                Orbit orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
                Vector3d worldPos = orbit.getPositionAtUT(ut);
                Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);
                var rotation = ComputeOrbitalRotation(
                    segment,
                    orbit,
                    ut,
                    velocity,
                    worldPos,
                    body.position,
                    Quaternion.identity,
                    sectionIndex,
                    TrajectoryMath.HasOrbitalFrameRotation(segment),
                    TrajectoryMath.IsSpinning(segment));

                pose = new AnchorPose(
                    worldPos,
                    rotation.ghostRot,
                    sectionIndex,
                    recording?.RecordingId);
                return true;
            }

            return false;
        }

        private static CelestialBody FindBodyForAnchorResolver(string bodyName)
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

        private bool ShouldLoopPlayback(Recording rec) => GhostPlaybackEngine.ShouldLoopPlayback(rec);

        // IsActiveTreeRecording stays in ParsekFlight (reads RecordingStore.CommittedTrees)
        private bool IsActiveTreeRecording(Recording rec)
        {
            if (!rec.IsTreeRecording) return false;
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == rec.TreeId)
                    return trees[i].ActiveRecordingId != null
                        && trees[i].ActiveRecordingId == rec.RecordingId;
            }
            return false;
        }

        private bool TryGetLoopSchedule(
            Recording rec,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            intervalSeconds = 0.0;
            if (rec == null || !GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            double baseIntervalSeconds = engine.GetLoopIntervalSeconds(rec, globalInterval);
            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;

            if (recIdx >= 0)
            {
                cachedTrajectories.Clear();
                var committed = RecordingStore.CommittedRecordings;
                for (int i = 0; i < committed.Count; i++)
                    cachedTrajectories.Add(committed[i]);

                if (GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                        cachedTrajectories,
                        recIdx,
                        baseIntervalSeconds,
                        out var autoSchedule))
                {
                    scheduleStartUT = autoSchedule.LaunchStartUT;
                    intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                }
            }

            return true;
        }

        private double GetLoopIntervalSeconds(Recording rec, int recIdx = -1)
        {
            if (!TryGetLoopSchedule(
                    rec, recIdx,
                    out _, out _, out _, out double intervalSeconds))
            {
                double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                        ?? LoopTiming.DefaultLoopIntervalSeconds;
                return engine.GetLoopIntervalSeconds(rec, globalInterval);
            }

            return intervalSeconds;
        }

        private bool TryComputeLoopPlaybackUT(
            Recording rec, double currentUT,
            out double loopUT, out long cycleIndex, out bool inPauseWindow,
            int recIdx = -1)
        {
            loopUT = 0.0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (!TryGetLoopSchedule(
                    rec, recIdx,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
                return false;

            double phaseOffset;
            if (recIdx >= 0 && engine.loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
            {
                ParsekLog.Verbose("Engine", $"TryComputeLoopPlaybackUT: applying phase offset {phaseOffset:F2}s for recIdx={recIdx}");
                scheduleStartUT -= phaseOffset;
            }

            if (!GhostPlaybackLogic.TryComputeLoopPlaybackPhase(
                    currentUT, scheduleStartUT, duration, intervalSeconds,
                    out double playbackPhase, out cycleIndex, out inPauseWindow))
            {
                return false;
            }

            loopUT = playbackStartUT + playbackPhase;
            return true;
        }


        // UpdateLoopingTimelinePlayback removed (T25 Phase 9)

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        // UpdateOverlapLoopPlayback removed (T25 Phase 9)

        // Phase F: ApplyResourceDeltas, ApplyTreeResourceDeltas, ApplyTreeLumpSum
        // removed. The ledger (GameActions/RecalculationEngine) is the single source
        // of truth for funds/science/reputation; per-recording lump-sum replay and
        // tree-level snapshot reconciliation are no longer used.

        RecordingTree FindCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == treeId) return trees[i];
            }
            Log($"FindCommittedTree: treeId='{treeId}' not found (checked {trees.Count} committed trees)");
            return null;
        }

        /// <summary>
        /// Finds a committed recording by its RecordingId. Searches all committed recordings.
        /// Used for ghost map vessel creation (looking up chain tip recordings).
        /// </summary>
        private static Recording FindTipRecordingById(string recordingId)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i].RecordingId == recordingId)
                    return committed[i];
            }
            return null;
        }

        internal void DestroyTimelineGhost(int index)
        {
            engine.DestroyGhost(index);
        }

        public void DestroyAllTimelineGhosts()
        {
            ExitWatchModeBeforeTimelineGhostCleanup("DestroyAllTimelineGhosts");

            // Remove ghost map ProtoVessels before engine cleanup
            GhostMapPresence.RemoveAllGhostVessels("rewind");

            // Engine destroys all ghost GOs and clears engine-owned state.
            // Fires OnAllGhostsDestroying so policy clears its own state.
            engine.DestroyAllGhosts();

            // Clear ParsekFlight-local state (positioning, proximity, diagnostics)
            ClearOrbitPlaybackCaches();
            loggedOrbitSegments.Clear();
            loggedOrbitRotationSegments.Clear();
            nearbySpawnCandidates.Clear();
            proximityVelocitySamples.Clear();
            notifiedSpawnRecordingIds.Clear();
            loggedRelativeStart.Clear();
            loggedRecordedRelativeStart.Clear();
            loggedRelativeBodyFixedPrimaryStart?.Clear();
            loggedAnchorNotFound.Clear();
            unknownFrameTagWarned.Clear();
            ClearGhostSkipReasonLogState();
        }

        /// <summary>
        /// Checks whether the recording ended with destruction and spawns an explosion FX if so.
        /// Pure decision logic is in ShouldTriggerExplosion; this method handles the side effects.
        /// </summary>

        /// <summary>
        /// Destroys all overlap ghosts for a recording index.
        /// Handles camera state reset if the watched recording's overlap tracking is affected.
        /// </summary>
        private void DestroyAllOverlapGhosts(int recIdx)
        {
            engine.DestroyAllOverlapGhosts(recIdx);

            // Camera state cleanup — engine doesn't know about watch mode
            watchMode.OnOverlapGhostsDestroyed(recIdx);
        }
    }
}
