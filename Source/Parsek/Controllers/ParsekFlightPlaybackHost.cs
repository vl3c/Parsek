using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // The flight-scene timeline playback host: builds the per-frame trajectory list and playback
    // flags, drives GhostPlaybackEngine, the ghost CommNet and the mission loop units, positions chain
    // ghosts, re-applies ghost positions in LateUpdate / OnCameraPreCull, owns the held
    // (claimed-vessel and blocked chain-tip) ghosts and the manual preview playback. ParsekFlight keeps
    // the Unity callbacks and their order, the committed-list notification handlers, the engine,
    // policy and watch-mode fields, and the policy callbacks; it reaches this host through PlaybackHost.
    internal sealed class ParsekFlightPlaybackHost
    {
        private readonly ParsekFlight host;

        internal ParsekFlightPlaybackHost(ParsekFlight host)
        {
            this.host = host;
        }

        /// <summary>Above this altitude, skip the per-frame terrain clamp (no terrain risk in flight).</summary>
        const double TerrainClampAltitudeLimit = 10000;

        private List<Material> previewGhostMaterials = new List<Material>();

        private GhostPlaybackState previewGhostState;

        private Recording previewRecording;

        // Cached per-frame allocations for engine path (avoid GC pressure)
        private readonly List<IPlaybackTrajectory> cachedTrajectories = new List<IPlaybackTrajectory>();

        // Mission loop-unit drive cache (Phase D2). The LoopUnitSet only changes when the looping
        // mission, its tree/selection/cadence, or the committed list changes, so the (allocating,
        // Verbose-logging) MissionLoopUnitBuilder.Build is gated behind a cheap signature compare.
        // SetLoopUnits still runs every frame with the cached value so the engine always sees the
        // current set. builtLoopUnits is the signature-cached build; cachedLoopUnits is the view
        // every playback consumer reads, the build minus the units still before their first loop
        // instance (MissionLoopUnitBuilder.ResolveLiveUnits), so a looped mission's first run plays
        // as an ordinary recording until its anchor.
        private string lastLoopUnitSignature;

        private GhostPlaybackLogic.LoopUnitSet builtLoopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

        private readonly List<RecordingAnchorCandidate> cachedGhostRecordingAnchorCandidates =
            new List<RecordingAnchorCandidate>();

        private TrajectoryPlaybackFlags[] cachedFlags;

        // Per-committed-index relay window inputs, filled by ComputePlaybackFlags.
        private GhostCommNetEligibilityInput[] ghostCommNetInputs = new GhostCommNetEligibilityInput[0];

        private readonly List<GhostCommNetCandidate> ghostCommNetCandidates = new List<GhostCommNetCandidate>();

        private readonly List<double> ghostCommNetClaimScratch = new List<double>();

        private readonly List<KeyValuePair<double, double>> ghostCommNetCarrierScratch =
            new List<KeyValuePair<double, double>>();

        private readonly HashSet<string> activeGhostSkipReasonLogIdentities = new HashSet<string>();

        // Reused per-frame scratch for the batched spawn-suppression summary (see
        // ComputePlaybackFlags). Cleared and refilled each frame; never read across frames.
        private readonly Dictionary<string, int> spawnSuppressedReasonScratch =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal void ApplyGhostPosEntries(ParsekFlight.GhostPositionReapplyPhase phase)
        {
            int skippedNonReFlyPreCull = 0;
            for (int i = 0; i < host.ghostPosEntries.Count; i++)
            {
                var e = host.ghostPosEntries[i];
                if (e.ghost == null || !e.ghost.activeSelf) continue;
                if (!ParsekFlight.ShouldProcessGhostPositionReapply(phase))
                {
                    skippedNonReFlyPreCull++;
                    continue;
                }
                Vector3d positionBeforeReapply = e.ghost.transform.position;

                switch (e.mode)
                {
                    case ParsekFlight.GhostPosMode.PointInterp:
                        ReapplyPointInterpEntry(ref e, phase);
                        break;
                    case ParsekFlight.GhostPosMode.SinglePoint:
                        ReapplySinglePointEntry(ref e, phase);
                        break;
                    case ParsekFlight.GhostPosMode.Orbit:
                        ReapplyOrbitEntry(ref e, phase);
                        break;
                    case ParsekFlight.GhostPosMode.CheckpointPoint:
                        ReapplyCheckpointPointEntry(ref e, phase);
                        break;
                    case ParsekFlight.GhostPosMode.Surface:
                        ReapplySurfaceEntry(ref e, phase);
                        break;
                    case ParsekFlight.GhostPosMode.Relative:
                        ReapplyRelativeEntry(ref e, phase);
                        break;
                }

                TraceGhostPositionReapply(e, phase, positionBeforeReapply, e.ghost.transform.position);
            }

            LogSkippedNonReFlyCameraPreCull("reapply", skippedNonReFlyPreCull);
        }

        private void ReapplyPointInterpEntry(ref ParsekFlight.GhostPosEntry e, ParsekFlight.GhostPositionReapplyPhase phase)
        {
            if (e.bodyBefore == null || e.bodyAfter == null) return;

            // Phase 1 + Phase 4 spline re-evaluation (design doc
            // section 6.1 / section 6.2 / section 18 Phase 1+4 / HR-9). Without the
            // re-eval, the raw lat/lon/alt bracket lerp on the
            // fallback path would overwrite the spline-positioned
            // transform on every LateUpdate - Phase 1's smoothing
            // would only survive a single Update frame before
            // FloatingOrigin re-positioning snapped it back.
            // Frame-tag dispatch matches the Update path: tag 0 ->
            // body-fixed surface lookup, tag 1 -> inertial re-lower
            // at the playback UT, unknown -> NaN (silent fallthrough
            // here; the Warn fires from the Update path's
            // unknownFrameTagWarned dedup so LateUpdate doesn't
            // double-emit).
            Vector3d pos;
            if (!host.Positioner.TryComputeLateUpdateSplineWorldPosition(e, out pos))
            {
                Vector3d posBefore = e.bodyBefore.GetWorldSurfacePosition(
                    e.latBefore, e.lonBefore, e.altBefore);
                Vector3d posAfter = e.bodyAfter.GetWorldSurfacePosition(
                    e.latAfter, e.lonAfter, e.altAfter);
                if (!ParsekFlight.allowPointHermiteInterpolation(
                        false,
                        splineApplied: false,
                        allowNormalPlaybackHermite: ParsekFlight.NormalPlaybackPointHermiteEnabled,
                        out _))
                {
                    pos = Vector3d.Lerp(posBefore, posAfter, e.t);
                }
                else
                {
                    double hermiteMaxDeviationMeters =
                        ParsekFlightGhostPositioner.ComputePointHermiteMaxDeviationMeters(posBefore, posAfter);
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
            // section 6.3 / section 6.4 / section 18 Phase 2-3): re-apply the world-
            // space eps so the FloatingOrigin frame-velocity shift
            // cannot snap the ghost back to the un-corrected
            // lerped position. Phase 3: the lookup is re-fetched
            // each LateUpdate so the lerp interval (section 6.4 Both
            // case) progresses with the playback UT instead of
            // freezing to the value captured at Update time.
            // The eps is additive on top of either the spline-
            // positioned or the raw-lerp position above.
            if (ParsekFlight.allowRenderAnchorCorrectionInterval(
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
        }

        private void ReapplySinglePointEntry(ref ParsekFlight.GhostPosEntry e, ParsekFlight.GhostPositionReapplyPhase phase)
        {
            if (e.bodyBefore == null) return;
            Vector3d pos = e.bodyBefore.GetWorldSurfacePosition(
                e.latBefore, e.lonBefore, e.altBefore);
            ApplyGhostReapplyTransform(
                e,
                pos,
                e.bodyBefore.bodyTransform.rotation * e.interpolatedRot,
                phase);
        }

        private void ReapplyOrbitEntry(ref ParsekFlight.GhostPosEntry e, ParsekFlight.GhostPositionReapplyPhase phase)
        {
            Orbit orbit;
            if (host.orbitCache.TryGetValue(e.orbitCacheKey, out orbit))
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
                Vector3d orbitContinuityOffset = ParsekFlightGhostPositioner.ResolveOrbitContinuityOffset(e, e.orbitUT);
                Vector3d targetPos = rawOrbitPos + orbitContinuityOffset;
                Quaternion targetRot = e.ghost.transform.rotation;

                // FRAME: lifted to Y-up WORLD at the read, so every LookRotation in
                // this block pairs a world forward axis with the world radial built
                // from getPositionAtUT - the same orbital-frame contract
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
        }

        private void ReapplyCheckpointPointEntry(ref ParsekFlight.GhostPosEntry e, ParsekFlight.GhostPositionReapplyPhase phase)
        {
            if (e.bodyBefore == null || e.bodyAfter == null) return;
            Vector3d posBefore = e.bodyBefore.GetWorldSurfacePosition(
                e.latBefore, e.lonBefore, e.altBefore);
            Vector3d posAfter = e.bodyAfter.GetWorldSurfacePosition(
                e.latAfter, e.lonAfter, e.altAfter);
            Vector3d rawCheckpointPos = Vector3d.Lerp(posBefore, posAfter, e.t);
            // section 7.7 (and any section 7.x anchor that lands on a
            // Checkpoint section): re-apply the world-space eps
            // here so the FloatingOrigin shift between Update
            // and LateUpdate cannot snap the ghost back to the
            // bare lerp position. Phase 3 evaluates eps at the
            // current playback UT - the lerp interval (section 6.4
            // Both case) progresses each frame, not freezes.
            if (ParsekFlight.allowRenderAnchorCorrectionInterval(
                    e.anchorRecordingId, e.anchorSectionIndex, e.pointUT,
                    false,
                    out Vector3d cpLateEps, out _))
            {
                rawCheckpointPos += cpLateEps;
            }
            Vector3d targetPos = rawCheckpointPos;
            Quaternion targetRot = e.ghost.transform.rotation;

            Orbit orbit;
            if (host.orbitCache.TryGetValue(e.orbitCacheKey, out orbit))
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
        }

        private void ReapplySurfaceEntry(ref ParsekFlight.GhostPosEntry e, ParsekFlight.GhostPositionReapplyPhase phase)
        {
            if (e.bodyBefore == null) return;
            Vector3d surfacePos = e.bodyBefore.GetWorldSurfacePosition(
                e.latBefore, e.lonBefore, e.altBefore);
            ApplyGhostReapplyTransform(
                e,
                surfacePos,
                e.bodyBefore.bodyTransform.rotation * e.surfaceRot,
                phase);
        }

        private void ReapplyRelativeEntry(ref ParsekFlight.GhostPosEntry e, ParsekFlight.GhostPositionReapplyPhase phase)
        {
            Vector3d anchorPos = Vector3d.zero;
            Quaternion anchorRotation = Quaternion.identity;
            bool haveAnchor = false;

            if (!string.IsNullOrEmpty(e.relativeAnchorRecordingId))
            {
                haveAnchor = host.Positioner.TryResolveRecordedRelativeAnchorPose(
                    e.anchorRecordingId,
                    e.relativeAnchorRecordingId,
                    e.anchorSectionIndex,
                    e.pointUT,
                    out ParsekFlight.RelativeAnchorPose lateRecordedPose);
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
                if (host.Positioner.TryResolveLoopLiveAnchorPose(
                        e.anchorVesselId,
                        e.recordingId,
                        e.pointUT,
                        out ParsekFlight.RelativeAnchorPose liveLoopAnchorPose))
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
                // Anchor not loaded - hide ghost (offsets are meters, not lat/lon/alt)
                e.ghost.SetActive(false);
            }
        }
        private void ApplyGhostReapplyTransform(
            ParsekFlight.GhostPosEntry entry,
            Vector3d targetPosition,
            Quaternion targetRotation,
            ParsekFlight.GhostPositionReapplyPhase phase)
        {
            if (entry.ghost == null)
                return;

            entry.ghost.transform.position = targetPosition;
            entry.ghost.transform.rotation = targetRotation;
        }

        internal static string ResolveGhostPosEntryRecordingId(ParsekFlight.GhostPosEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.recordingId)) return entry.recordingId;
            if (!string.IsNullOrEmpty(entry.anchorRecordingId)) return entry.anchorRecordingId;
            if (!string.IsNullOrEmpty(entry.relativeAnchorRecordingId)) return entry.relativeAnchorRecordingId;
            return null;
        }

        private static void TraceGhostPositionReapply(
            ParsekFlight.GhostPosEntry entry,
            ParsekFlight.GhostPositionReapplyPhase phase,
            Vector3d before,
            Vector3d after)
        {
            double delta = Vector3d.Distance(before, after);

            string recordingId = ResolveGhostPosEntryRecordingId(entry);
            double currentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : entry.pointUT;
            GhostRenderTrace.EmitReapply(
                recordingId,
                entry.hasRecordingIndex ? entry.recordingIndex : -1,
                currentUT,
                entry.pointUT,
                phase == ParsekFlight.GhostPositionReapplyPhase.CameraPreCull ? "CameraPreCull" : "LateUpdate",
                entry.mode.ToString(),
                before,
                after,
                entry.ghost != null ? entry.ghost.transform.rotation : Quaternion.identity,
                null);

            if (phase != ParsekFlight.GhostPositionReapplyPhase.CameraPreCull)
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
                BuildCameraPreCullReapplyMessage(
                    shortRecordingId, entry.mode, entry.pointUT, delta, before, after),
                1.0);
        }

        internal static string BuildCameraPreCullReapplyMessage(
            string shortRecordingId,
            ParsekFlight.GhostPosMode mode,
            double pointUT,
            double delta,
            Vector3d before,
            Vector3d after)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Ghost camera pre-cull reapply: rec={0} mode={1} ut={2:F3} deltaMeters={3:F2} before=({4:F2},{5:F2},{6:F2}) after=({7:F2},{8:F2},{9:F2})",
                shortRecordingId,
                mode,
                pointUT,
                delta,
                before.x, before.y, before.z,
                after.x, after.y, after.z);
        }

        internal static void LogSkippedNonReFlyCameraPreCull(string operation, int count)
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
            ParsekFlight.RelativeAnchorPoseSnapshot snapshot,
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

            return ParsekFlight.IsFiniteVector3d(anchorPos);
        }

        /// <summary>
        /// Terrain clamp: prevent ghosts from appearing below terrain surface.
        /// Sub-orbital orbit reconstruction can place ghosts underground (periapsis
        /// below surface), and procedural terrain height varies between sessions.
        /// </summary>
        internal void ClampGhostsToTerrain(ParsekFlight.GhostPositionReapplyPhase phase)
        {
            Vector3d activeVesselPos = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.GetWorldPos3D()
                : Vector3d.zero;

            int skippedNonReFlyPreCull = 0;
            for (int i = 0; i < host.ghostPosEntries.Count; i++)
            {
                var e = host.ghostPosEntries[i];
                if (e.ghost == null || !e.ghost.activeSelf) continue;
                if (!ParsekFlight.ShouldProcessGhostPositionReapply(phase))
                {
                    skippedNonReFlyPreCull++;
                    continue;
                }
                if (e.mode == ParsekFlight.GhostPosMode.Relative) continue;

                CelestialBody body = (e.mode == ParsekFlight.GhostPosMode.Orbit) ? e.orbitBody : e.bodyBefore;
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
                double clearance = ParsekFlight.ComputeTerrainClearance(distToVessel);

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

        public void StopPlayback()
        {
            if (host.isPlaying)
                host.Log("Manual playback stopped");
            host.isPlaying = false;

            if (previewGhostState != null)
            {
                if (previewGhostState.materials != null)
                {
                    for (int i = 0; i < previewGhostState.materials.Count; i++)
                    {
                        if (previewGhostState.materials[i] != null)
                            UnityEngine.Object.Destroy(previewGhostState.materials[i]);
                    }
                }

                if (previewGhostState.engineInfos != null)
                {
                    foreach (var info in previewGhostState.engineInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                UnityEngine.Object.Destroy(info.particleSystems[i].gameObject);
                }

                if (previewGhostState.rcsInfos != null)
                {
                    foreach (var info in previewGhostState.rcsInfos.Values)
                        for (int i = 0; i < info.particleSystems.Count; i++)
                            if (info.particleSystems[i] != null)
                                UnityEngine.Object.Destroy(info.particleSystems[i].gameObject);
                }

                host.engine.DestroyReentryFxResources(previewGhostState.reentryFxInfo);

                GhostPlaybackLogic.DestroyAllFakeCanopies(previewGhostState);
                previewGhostState = null;
            }
            else if (previewGhostMaterials != null)
            {
                // Sphere fallback path — no GhostPlaybackState
                for (int i = 0; i < previewGhostMaterials.Count; i++)
                {
                    if (previewGhostMaterials[i] != null)
                        UnityEngine.Object.Destroy(previewGhostMaterials[i]);
                }
            }
            previewGhostMaterials.Clear();
            previewRecording = null;
            ParsekFlight.ClearPreviewRecordingForTailLift();

            if (host.ghostObject != null)
            {
                UnityEngine.Object.Destroy(host.ghostObject);
                host.ghostObject = null;
            }

            host.Positioner.ClearOrbitPlaybackCaches();
            host.loggedOrbitSegments.Clear();
            host.loggedOrbitRotationSegments.Clear();
        }

        internal void UpdatePlayback()
        {
            if (host.ghostObject == null || host.recording.Count < 2)
            {
                StopPlayback();
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            double elapsedSinceStart = currentUT - host.playbackStartUT;
            double recordingTime = host.recordingStartUT + elapsedSinceStart;

            if (recordingTime > host.recording[host.recording.Count - 1].ut)
            {
                host.Log("Manual playback complete — reached end of recording");
                // explosionAlreadyFired=false is safe: preview is one-shot (StopPlayback called immediately after)
                if (previewRecording != null && host.ghostObject != null
                    && !GhostPlaybackLogic.ShouldSuppressVisualFx(TimeWarp.CurrentRate)
                    && GhostPlaybackLogic.ShouldTriggerExplosion(false, previewRecording.TerminalStateValue,
                           true, previewRecording.VesselName, -1))
                {
                    Vector3 pos = host.ghostObject.transform.position;
                    float len = GhostVisualBuilder.ComputeGhostLength(host.ghostObject);
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
                        var t = host.ghostObject.transform;
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
                host.ScreenMessage("Preview playback complete", 2f);
                return;
            }

            InterpolationResult interpResult;
            bool surfaceSkip = previewRecording != null &&
                TrajectoryMath.IsSurfaceAtUT(previewRecording.TrackSections, recordingTime);
            host.Positioner.InterpolateAndPosition(host.ghostObject, host.recording, host.orbitSegments,
                ref host.lastPlaybackIndex, recordingTime, 10000, out interpResult,
                skipOrbitSegments: surfaceSkip,
                recordingId: previewRecording?.RecordingId,
                pointFrameSource: "preview-flat-points",
                flatPointSections: previewRecording?.TrackSections);

            if (previewGhostState != null && previewRecording != null)
            {
                previewGhostState.SetInterpolated(interpResult);
                GhostPlaybackLogic.ApplyPartEvents(-1, previewRecording, recordingTime, previewGhostState);
                GhostPlaybackLogic.ApplyFlagEvents(previewGhostState, previewRecording, recordingTime);
                host.engine.UpdateReentryFx(-1, previewGhostState, previewRecording.VesselName ?? "Preview", TimeWarp.CurrentRate);
            }
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

        internal static HashSet<string> GetFirstClaimTreeIds(GhostChain chain)
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

        internal static Recording FindChainPathRecordingAtUT(
            IReadOnlyList<RecordingTree> committedTrees,
            ChainLink link,
            uint vesselPid,
            double currentUT)
        {
            if (committedTrees == null || string.IsNullOrEmpty(link.treeId))
                return null;

            RecordingTree tree = ParsekFlight.FindCommittedTree(committedTrees, link.treeId);
            if (tree == null)
                return null;

            Recording startRec;
            if (!tree.Recordings.TryGetValue(link.recordingId, out startRec))
                return null;

            return FindChainPathRecordingAtUT(tree, startRec, vesselPid, currentUT);
        }

        internal static Recording FindChainPathRecordingAtUT(
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

        internal static Recording FindNextChainRecording(RecordingTree tree, Recording rec)
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

        internal static Recording FindPreferredChildRecording(RecordingTree tree, Recording rec)
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

        internal static bool ChainHasStarted(GhostChain chain, double currentUT)
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

                host.Positioner.PositionGhostFromOrbitOnly(ghostGO, rec, currentUT,
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
                host.Positioner.PositionGhostAtSurface(ghostGO, rec.SurfacePos.Value, allowActivation: true,
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

        internal static bool RecordingHasUsablePlaybackDataAtUT(Recording rec, double currentUT)
        {
            if (rec == null)
                return false;

            if (rec.Points != null && rec.Points.Count > 0)
                return true;

            if (HasOrbitCoverageAtUT(rec, currentUT))
                return true;

            return HasSurfaceCoverageAtUT(rec, currentUT);
        }

        internal static bool RecordingHasUsableNonPointPlaybackDataAtUT(
            Recording rec, double currentUT)
        {
            if (rec == null)
                return false;

            if (HasOrbitCoverageAtUT(rec, currentUT))
                return true;

            return HasSurfaceCoverageAtUT(rec, currentUT);
        }

        internal static bool HasOrbitCoverageAtUT(Recording rec, double currentUT)
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

        internal static bool HasSurfaceCoverageAtUT(Recording rec, double currentUT)
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
            if (host.vesselGhoster == null || host.activeGhostChains == null || host.activeGhostChains.Count == 0)
                return;

            foreach (var kvp in host.activeGhostChains)
            {
                GhostChain chain = kvp.Value;

                // Spawn-blocked chains: the tip's held playback ghost is drawn by
                // UpdateSpawnBlockedChainTipGhosts after the policy's held-ghost retries.
                if (chain.SpawnBlocked)
                    continue;

                var info = host.vesselGhoster.GetGhostedInfo(chain.OriginalVesselPid);
                if (info == null || info.ghostGO == null) continue;

                // Find background recording covering current UT within the chain's
                // own tree sequence, not by global PID-only lookup.
                Recording bgRec = ParsekFlight.FindBackgroundRecordingForChain(
                    RecordingStore.CommittedRecordings, RecordingStore.CommittedTrees, chain, currentUT);

                if (bgRec != null && bgRec.Points.Count > 0)
                {
                    // Position from trajectory points (same interpolation as existing ghost positioning)
                    bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(bgRec.TrackSections, currentUT);
                    // Phase 1: background path bypasses splines pending section
                    // indexing — the BackgroundRecorder does not yet emit
                    // TrackSections, so there is no body-fixed section index
                    // to feed the spline gate. Tracked in the Phase 1 plan.
                    host.Positioner.InterpolateAndPosition(info.ghostGO, bgRec.Points, bgRec.OrbitSegments,
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
                var fallbackRec = ParsekFlight.FindPositionFallbackRecordingForChain(
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

            if (!ParsekFlight.TryStampChainMapOrbit(chain, seg.Value))
                return;

            GhostMapPresence.UpdateGhostOrbit(chain.OriginalVesselPid, seg.Value);
        }

        /// <summary>
        /// The recording the live recorder writes right now (a split in progress keeps it live
        /// while pendingSplitRecorder holds the recorder), or null.
        /// </summary>
        internal string ResolveLiveRecorderRecordingId()
        {
            return ParsekFlight.ResolveLiveResumedRecordingId(
                (host.recorder != null && host.recorder.IsRecording) || host.pendingSplitRecorder != null,
                host.activeTree?.ActiveRecordingId);
        }

        internal static string BuildGhostSkipReasonIdentity(int index, string recordingId)
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

        internal static void LogGhostSkipReasonChangeCore(
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

        internal void ClearGhostSkipReasonLogState()
        {
            activeGhostSkipReasonLogIdentities.Clear();
            ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix("Flight", "ghost-skip|");
        }

        internal void ClearGhostSkipReasonLogStateForTesting()
        {
            ClearGhostSkipReasonLogState();
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
            LogGhostSkipReasonChangeIfNeededCore(
                activeGhostSkipReasonLogIdentities,
                index,
                rec,
                reason,
                hasRenderableData,
                externalVesselSuppressed,
                supersededByRelation,
                rewindRetired);
        }

        /// <summary>
        /// Logs a skip-reason change for one recording and keeps the set of identities whose
        /// last logged state was an active skip: a recording that never skipped logs nothing,
        /// and one that stops skipping logs its None transition once and leaves the set.
        /// </summary>
        internal static void LogGhostSkipReasonChangeIfNeededCore(
            HashSet<string> activeIdentities,
            int index,
            Recording rec,
            GhostPlaybackSkipReason reason,
            bool hasRenderableData,
            bool externalVesselSuppressed,
            bool supersededByRelation,
            bool rewindRetired)
        {
            string identity = BuildGhostSkipReasonIdentity(index, rec?.RecordingId);
            bool hasLoggedActiveSkip = activeIdentities.Contains(identity);
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
                activeIdentities.Remove(identity);
            else
                activeIdentities.Add(identity);
        }

        private static void LogFastForwardWatchHandoff(
            ParsekFlight.FastForwardWatchHandoffDecision decision,
            double currentUT)
        {
            string message = ParsekFlight.BuildFastForwardWatchHandoffMessage(decision, currentUT);
            if (decision.warn)
                ParsekLog.Warn("CameraFollow", message);
            else
                ParsekLog.Verbose("CameraFollow", message);
        }

        internal static bool ResolveReFlySettleStability(
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
            RecordReFlyAnchorHoldCandidateCore(
                host.reFlyAnchorHoldCountsThisFrame,
                host.reFlyAnchorHoldReasonsThisFrame,
                anchorRecordingId,
                reason);
        }

        /// <summary>
        /// Counts one ghost held for the anchor this frame; the first reason recorded for the
        /// anchor this frame wins.
        /// </summary>
        internal static void RecordReFlyAnchorHoldCandidateCore(
            Dictionary<string, int> countsThisFrame,
            Dictionary<string, string> reasonsThisFrame,
            string anchorRecordingId,
            string reason)
        {
            if (string.IsNullOrEmpty(anchorRecordingId))
                return;

            int count;
            countsThisFrame.TryGetValue(anchorRecordingId, out count);
            countsThisFrame[anchorRecordingId] = count + 1;
            if (!reasonsThisFrame.ContainsKey(anchorRecordingId))
                reasonsThisFrame[anchorRecordingId] = reason ?? "(none)";
        }

        private void LogReFlyAnchorHoldTransitions(int frame)
        {
            LogReFlyAnchorHoldTransitionsCore(
                host.reFlyAnchorHoldCountsThisFrame,
                host.reFlyAnchorHoldReasonsThisFrame,
                host.reFlyAnchorHoldStartFrameByAnchor,
                host.reFlyAnchorHoldReleaseScratch,
                frame);
        }

        /// <summary>
        /// Engages a hold (Info line, start frame stamped) for every anchor counted this frame
        /// that was not already held, and releases (Info line, start frame dropped) every held
        /// anchor not counted this frame.
        /// </summary>
        internal static void LogReFlyAnchorHoldTransitionsCore(
            Dictionary<string, int> countsThisFrame,
            Dictionary<string, string> reasonsThisFrame,
            Dictionary<string, int> holdStartFrameByAnchor,
            List<string> releaseScratch,
            int frame)
        {
            foreach (var kvp in countsThisFrame)
            {
                if (holdStartFrameByAnchor.ContainsKey(kvp.Key))
                    continue;

                holdStartFrameByAnchor[kvp.Key] = frame;
                string reason;
                reasonsThisFrame.TryGetValue(kvp.Key, out reason);
                ParsekLog.Info("ReFlySettle",
                    $"Anchor ghost visibility hold engaged: anchorRec={ParsekFlightGhostPositioner.ShortRecordingId(kvp.Key)} " +
                    $"ghosts={kvp.Value.ToString(CultureInfo.InvariantCulture)} " +
                    $"reason={reason ?? "(none)"} frame={frame.ToString(CultureInfo.InvariantCulture)}");
            }

            releaseScratch.Clear();
            foreach (var kvp in holdStartFrameByAnchor)
            {
                if (!countsThisFrame.ContainsKey(kvp.Key))
                    releaseScratch.Add(kvp.Key);
            }

            for (int i = 0; i < releaseScratch.Count; i++)
            {
                string anchorId = releaseScratch[i];
                int startFrame = holdStartFrameByAnchor[anchorId];
                int heldFrames = Math.Max(0, frame - startFrame);
                ParsekLog.Info("ReFlySettle",
                    $"Anchor ghost visibility hold released: anchorRec={ParsekFlightGhostPositioner.ShortRecordingId(anchorId)} " +
                    $"heldFrames={heldFrames.ToString(CultureInfo.InvariantCulture)} " +
                    $"frame={frame.ToString(CultureInfo.InvariantCulture)}");
                holdStartFrameByAnchor.Remove(anchorId);
            }

            releaseScratch.Clear();
        }

        internal static bool HasAnchorReFlyUnstableFlag(TrajectoryPlaybackFlags[] flags)
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

        internal void LogReFlySettleActiveVesselPoseIfArmed(string phase, int frame)
        {
            if (frame != host.reFlySettlePoseLogActiveFrame)
                return;

            Vector3d pos = FlightGlobals.ActiveVessel != null
                ? (Vector3d)FlightGlobals.ActiveVessel.transform.position
                : Vector3d.zero;
            ParsekLog.Verbose("ReFlySettle",
                $"Active vessel pose {phase}: pos={DiagnosticFormatters.FormatVector3d(pos)} " +
                $"frame={frame.ToString(CultureInfo.InvariantCulture)} " +
                $"activeVessel={(FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : "(none)")}");
        }

        internal bool IsChainSeamSuccessorAtFrame(Recording rec, double currentUT)
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
            bool predHasGhost = host.engine != null
                && host.engine.TryGetGhostState(predIdx, out GhostPlaybackState predState)
                && predState != null;
            double seamUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec);
            return ParsekFlight.IsChainSeamSuccessor(currentUT, seamUT, predHasGhost);
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
            host.reFlyAnchorHoldCountsThisFrame.Clear();
            host.reFlyAnchorHoldReasonsThisFrame.Clear();
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
            double continuationHoldLookahead = GhostCommNetMath.HoldPredictionLookaheadSeconds(
                GhostCommNetMath.FlightHoldPredictionTickSeconds, TimeWarp.CurrentRate);
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
                    || (host.cachedLoopUnits != null && host.cachedLoopUnits.IsMember(i));
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
                        BuildStep2LiveBindSuppressKey(rec.RecordingId),
                        BuildStep2LiveBindSuppressMessage(rec.RecordingId, rec.VesselName),
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

                GhostPlaybackSkipReason skipReason = ParsekFlight.ResolveGhostPlaybackSkipReason(
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

                var chainSuppressed = host.activeGhostChains != null
                    ? GhostPlaybackLogic.ShouldSuppressSpawnForChain(host.activeGhostChains, rec)
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
                    string reason = ResolveSpawnSuppressedReason(
                        liveFlownRecording,
                        historicalNeverReplayed,
                        spawnResult.needsSpawn,
                        spawnResult.reason,
                        chainSuppressed.suppressed,
                        chainSuppressed.reason,
                        spawnHistoricalNeverReplayed);
                    TallySpawnSuppressedReason(spawnSuppressedByReason, reason);
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
                    RealVesselRelays = !externalVesselSuppressed && !rec.IsDebris
                        && GhostPlaybackLogic.RealVesselRelaysForRecording(rec),
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
                // The hold scans the committed list, so it is resolved only near or past EndUT,
                // the only UTs at which anything reads it (as the Tracking Station does).
                if (hasData && !rec.IsDebris && !supersededByRelation && !rewindRetired
                    && !spawnHistoricalNeverReplayed
                    && GhostCommNetMath.ShouldResolveContinuationHold(
                        currentUT, rec.EndUT, continuationHoldLookahead))
                {
                    GhostCommNetMath.ApplyContinuationHold(
                        ref commNetInput, rec, committed, host.activeGhostChains,
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
                        ParsekFlight.RecordingEligibleForSessionSuppressedRenderCarveOut(rec),
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
                    () => ParsekFlight.BuildSpawnSuppressedSummary(spawnSuppressedCount, spawnSuppressedByReason),
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
                    () => BuildStep2LiveAnchorDoubleSummary(suppressedThisFrame),
                    2.0);
            }
            LogReFlyAnchorHoldTransitions(frame);
            return flags;
        }

        /// <summary>
        /// The spawn-suppression reason a non-debris recording whose final needsSpawn is false
        /// is counted under in the per-frame histogram. A loop member keeps its spawn-gate
        /// reason first (its render is not history-gated); the history gate names itself only
        /// when it is the gate that actually held a loop member's first-run spawn.
        /// </summary>
        internal static string ResolveSpawnSuppressedReason(
            bool liveFlownRecording,
            bool historicalNeverReplayed,
            bool spawnNeedsSpawn,
            string spawnReason,
            bool chainSuppressed,
            string chainReason,
            bool spawnHistoricalNeverReplayed)
        {
            return liveFlownRecording
                ? GhostPlaybackLogic.LiveFlownRecordingSpawnReason
                : historicalNeverReplayed
                ? "historical-never-replayed"
                : !spawnNeedsSpawn ? spawnReason
                : chainSuppressed ? chainReason
                : spawnHistoricalNeverReplayed ? "historical-never-replayed"
                : chainReason;
        }

        /// <summary>Adds one count under the reason's histogram key ("(none)" for an empty reason).</summary>
        internal static void TallySpawnSuppressedReason(Dictionary<string, int> byReason, string reason)
        {
            string reasonKey = string.IsNullOrEmpty(reason) ? "(none)" : reason;
            byReason.TryGetValue(reasonKey, out int reasonCount);
            byReason[reasonKey] = reasonCount + 1;
        }

        internal static string BuildStep2LiveBindSuppressKey(string recordingId)
        {
            return "step2-live-bind-suppress|" + (recordingId ?? "(none)");
        }

        internal static string BuildStep2LiveBindSuppressMessage(string recordingId, string vesselName)
        {
            return "Step-2 suppressed live-anchor double (live-bind event): rec="
                + (recordingId ?? "(none)")
                + " vessel=\"" + (vesselName ?? "(none)") + "\""
                + " liveBoundThisOrLastFrame=true"
                + " (own ghost hidden while a delivery member is docking its launch-matched live vessel)";
        }

        internal static string BuildStep2LiveAnchorDoubleSummary(int suppressedThisFrame)
        {
            return "Step-2 live-anchor-double: suppressed="
                + suppressedThisFrame.ToString(CultureInfo.InvariantCulture)
                + " this frame (gate=live-bind-event)";
        }

        /// <summary>
        /// Engine-based timeline playback. Builds FrameContext and TrajectoryPlaybackFlags,
        /// delegates per-frame ghost positioning to GhostPlaybackEngine, then applies
        /// resource deltas and watch-mode validity checks.
        /// </summary>
        internal void UpdateTimelinePlaybackViaEngine()
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
            host.policy.RunSpawnDeathChecks();

            // Flush deferred spawns from warp (policy owns the queue)
            host.policy.FlushDeferredSpawns();

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
                protectedIndex = host.watchMode.WatchedRecordingIndex,
                protectedLoopCycleIndex = host.watchMode.WatchedLoopCycleIndex,
                externalGhostCount = host.activeGhostChains?.Count ?? 0,
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
                host.reFlySettlePoseLogActiveFrame = Time.frameCount;
                LogReFlySettleActiveVesselPoseIfArmed("pre-engine", Time.frameCount);
            }

            // Run engine rendering
            host.engine.UpdatePlayback(cachedTrajectories, flags, ctx);

            // Deferred watch after fast-forward: enter watch mode on the FF target
            // now that the engine has positioned ghosts for the new UT.
            if (host.pendingWatchAfterFFId != null)
            {
                string ffId = host.pendingWatchAfterFFId;
                host.pendingWatchAfterFFId = null;
                ParsekFlight.FastForwardWatchHandoffDecision handoff =
                    ParsekFlight.ClassifyFastForwardWatchHandoff(
                        ffId,
                        committed,
                        flags,
                        idx => host.watchMode.HasActiveGhost(idx));
                if (handoff.canEnterWatch)
                {
                    host.watchMode.EnterWatchMode(handoff.index);
                    if (host.watchMode.IsWatchingGhost && host.watchMode.WatchedRecordingIndex == handoff.index)
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
            host.policy.RetryHeldGhostSpawns();

            // Spawn-blocked chain tips (phase 6b-4, design 12.9.2): draw each held tip ghost
            // at the tip's propagated position, or release it once the tip spawned.
            UpdateSpawnBlockedChainTipGhosts(currentUT);

            // Create deferred ghost map ProtoVessels when ghosts enter orbital segments.
            // Phase 8d.0: routed through the scene adapter; MapViewScene.DriveMapPresence delegates
            // VERBATIM to policy.CheckPendingMapVessels(currentUT) with the identical argument. Pure
            // indirection — same method, same arg, same slot, same surrounding guard/try-catch.
            host.mapViewScene.DriveMapPresence(Planetarium.GetUniversalTime());

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
                    host.mapViewScene.SetFrameInputs(host.cachedLoopUnits, Planetarium.GetUniversalTime());
                    MapRender.ShadowRenderDriver.RunFrame(host.mapViewScene);
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
            host.watchMode.ValidateWatchedGhostStillActive();
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
                if (host.ghostCommNet == null)
                    host.ghostCommNet = new GhostCommNetManager("FLIGHT", TryResolveGhostCommNetRecordingPosition);

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
                if (host.activeGhostChains != null && host.vesselGhoster != null)
                {
                    foreach (var kv in host.activeGhostChains)
                    {
                        GhostChain chain = kv.Value;
                        if (chain == null) continue;
                        List<ChainClaimedVessel> held = chain.HeldBackVessels();
                        for (int h = 0; h < held.Count; h++)
                        {
                            ChainClaimedVessel claim = held[h];
                            var info = host.vesselGhoster.GetGhostedInfo(claim.Pid);
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

                host.ghostCommNet.Tick(ghostCommNetCandidates, currentUT);
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
            return host.Positioner.TryResolvePlaybackWorldPosition(index, committed[index], null, ut, out worldPos);
        }

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
            if (host.cachedLoopUnits == null || host.cachedLoopUnits.Count == 0)
                return;

            int substituted = 0;
            foreach (var kv in host.cachedLoopUnits.UnitsByOwner)
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
        // (LoopIntervalSeconds + LoopTimeUnit), phase anchor (LoopAnchorUT), and selection (the
        // interval-level ExcludedIntervalKeys that drive the members + span), plus the looping tree's topology (branch + recording counts) and
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
            host.cachedLoopUnits = MissionLoopUnitBuilder.ResolveLiveUnits(
                "Flight", builtLoopUnits, host.cachedLoopUnits, routeSelectUT, committed);
            host.engine.SetLoopUnits(host.cachedLoopUnits);

            // If the player is WATCHING a member of a mission loop and the new selection just
            // dropped that member from the unit (interval trimmed out, the loop turned off, or
            // every segment unchecked), exit watch NOW - this runs BEFORE the engine's
            // UpdatePlayback tears the member's ghost down (and, for a Destroyed-terminal member,
            // explodes it). Otherwise the policy starts a watch-hold on the now-dying ghost and the
            // camera latches onto a destroyed transform, leaving FlightCamera.Target null and
            // flooding stock KSP (FlightGlobals / Sun / CrewHatchController / AmbienceControl) with
            // NREs. Gated on WatchedLoopCycleIndex >= 0 so only loop-cycle watches are affected (a
            // plain non-loop watch is never a unit member and must not be disturbed).
            if (host.watchMode != null
                && host.watchMode.WatchedRecordingIndex >= 0
                && host.watchMode.WatchedLoopCycleIndex >= 0
                && !host.cachedLoopUnits.IsMember(host.watchMode.WatchedRecordingIndex))
            {
                ParsekLog.Info("CameraFollow",
                    $"Watched mission member #{host.watchMode.WatchedRecordingIndex} left the loop unit " +
                    "(interval trimmed out or loop off) - exiting watch before its ghost is torn down");
                host.watchMode.ExitWatchModePreservingLineage();
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
            if (!ParsekFlight.ShouldRecordGhostAnchorCandidates(marker))
            {
                ParsekLog.VerboseRateLimited("Anchor", "ghost-recording-anchor-candidates-disabled",
                    $"Ghost recording anchor candidates skipped: reason=no-active-refly " +
                    $"focusRecordingId={focusedRecordingId ?? "(none)"} focusedVesselPid={focusedVesselPid}",
                    5.0);
                return cachedGhostRecordingAnchorCandidates;
            }

            if (host.engine == null
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
            var context = ParsekFlight.BuildFlightRelativeAnchorResolverContext(
                focusTree,
                focusedRecordingId,
                marker);

            foreach (GhostAnchorCandidate candidate in host.engine.GetActiveAnchorCandidates(cachedTrajectories))
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

        internal static Vector3d ResolveAnchorResolverPointWorldPosition(TrajectoryPoint point)
        {
            CelestialBody body = ParsekFlight.FindBodyForAnchorResolver(point.bodyName);
            if (body == null)
                return new Vector3d(double.NaN, double.NaN, double.NaN);

            return body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude);
        }

        internal static Quaternion ResolveAnchorResolverBodyWorldRotation(TrajectoryPoint point)
        {
            CelestialBody body = ParsekFlight.FindBodyForAnchorResolver(point.bodyName);
            return body != null && body.bodyTransform != null
                ? body.bodyTransform.rotation
                : Quaternion.identity;
        }

        // IsActiveTreeRecording stays in ParsekFlight (reads RecordingStore.CommittedTrees)
        internal bool IsActiveTreeRecording(Recording rec)
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

        internal bool TryGetLoopSchedule(
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
            double baseIntervalSeconds = host.engine.GetLoopIntervalSeconds(rec, globalInterval);
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

        internal double GetLoopIntervalSeconds(Recording rec, int recIdx = -1)
        {
            if (!TryGetLoopSchedule(
                    rec, recIdx,
                    out _, out _, out _, out double intervalSeconds))
            {
                double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                        ?? LoopTiming.DefaultLoopIntervalSeconds;
                return host.engine.GetLoopIntervalSeconds(rec, globalInterval);
            }

            return intervalSeconds;
        }

        internal bool TryComputeLoopPlaybackUT(
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
            if (recIdx >= 0 && host.engine.loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
            {
                ParsekLog.Verbose("Engine", "TryComputeLoopPlaybackUT: applying phase offset " +
                    phaseOffset.ToString("F2", CultureInfo.InvariantCulture) + "s for recIdx=" +
                    recIdx.ToString(CultureInfo.InvariantCulture));
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

        /// <summary>
        /// Finds a committed recording by its RecordingId. Searches all committed recordings.
        /// Used for ghost map vessel creation (looking up chain tip recordings).
        /// </summary>
        internal static Recording FindTipRecordingById(string recordingId)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i].RecordingId == recordingId)
                    return committed[i];
            }
            return null;
        }

        public void DestroyAllTimelineGhosts()
        {
            host.ExitWatchModeBeforeTimelineGhostCleanup("DestroyAllTimelineGhosts");

            // Remove ghost map ProtoVessels before engine cleanup
            GhostMapPresence.RemoveAllGhostVessels("rewind");

            // Engine destroys all ghost GOs and clears engine-owned state.
            // Fires OnAllGhostsDestroying so policy clears its own state.
            host.engine.DestroyAllGhosts();

            // Clear ParsekFlight-local state (positioning, proximity, diagnostics)
            host.Positioner.ClearOrbitPlaybackCaches();
            host.loggedOrbitSegments.Clear();
            host.loggedOrbitRotationSegments.Clear();
            host.nearbySpawnCandidates.Clear();
            host.proximityVelocitySamples.Clear();
            host.notifiedSpawnRecordingIds.Clear();
            host.loggedRelativeStart.Clear();
            host.loggedRecordedRelativeStart.Clear();
            host.loggedRelativeBodyFixedPrimaryStart?.Clear();
            host.loggedAnchorNotFound.Clear();
            host.unknownFrameTagWarned.Clear();
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
        internal void DestroyAllOverlapGhosts(int recIdx)
        {
            host.engine.DestroyAllOverlapGhosts(recIdx);

            // Camera state cleanup — engine doesn't know about watch mode
            host.watchMode.OnOverlapGhostsDestroyed(recIdx);
        }

        internal readonly Dictionary<uint, ParsekFlight.ClaimedVesselHoldGhostState> claimedVesselHoldGhosts =
            new Dictionary<uint, ParsekFlight.ClaimedVesselHoldGhostState>();

        private readonly List<uint> claimedVesselHoldGhostReleaseScratch = new List<uint>();

        private readonly Dictionary<uint, KeyValuePair<GhostChain, ChainClaimedVessel>> claimedVesselHoldDesiredScratch =
            new Dictionary<uint, KeyValuePair<GhostChain, ChainClaimedVessel>>();

        /// <summary>
        /// Second feed of the claimed-vessel hold (the first is the ghost-chain walk): ghosts
        /// each live survivor in <paramref name="entries"/> through <see cref="VesselGhoster"/>
        /// and holds it until its first committed use. A vessel the chain feed already holds is
        /// left to it. Releases the end-spawn claim a flying recording of the same launch keeps
        /// on the despawned pid, so that recording brings the vessel back at its end. Returns
        /// the entries actually held.
        /// </summary>
        internal List<ReFlyHoldEntry> HoldReFlyInvokeVessels(
            IList<ReFlyHoldEntry> entries, out int notLive, out int alreadyHeld)
        {
            notLive = 0;
            alreadyHeld = 0;
            var held = new List<ReFlyHoldEntry>();
            if (entries == null || entries.Count == 0)
                return held;
            if (host.vesselGhoster == null)
                host.vesselGhoster = new VesselGhoster();

            for (int i = 0; i < entries.Count; i++)
            {
                ReFlyHoldEntry entry = entries[i];
                if (entry == null || entry.Pid == 0u) continue;
                if (host.vesselGhoster.IsGhosted(entry.Pid))
                {
                    alreadyHeld++;
                    continue;
                }
                if (FlightRecorder.FindVesselByPid(entry.Pid) == null || !host.vesselGhoster.GhostVessel(entry.Pid))
                {
                    notLive++;
                    continue;
                }
                AddReFlyInvokeHold(entry, "refly-invoke-hold");
                held.Add(entry);
            }

            if (held.Count > 0)
                GhostPlaybackLogic.SetIsGhostedOverride(
                    pid => host.vesselGhoster != null && host.vesselGhoster.IsGhosted(pid));
            return held;
        }

        private void AddReFlyInvokeHold(ReFlyHoldEntry entry, string logContext)
        {
            host.reFlyInvokeHolds[entry.Pid] = new ChainClaimedVessel
            {
                Pid = entry.Pid,
                LaunchGuid = entry.LaunchGuid,
                FirstClaimUT = entry.FirstClaimUT,
            };
            for (int r = 0; r < entry.FlyingRecordingIds.Count; r++)
            {
                Recording rec = FindTipRecordingById(entry.FlyingRecordingIds[r]);
                VesselGhoster.ReleaseTipSpawnClaimOnGhostedVessel(rec, entry.Pid, logContext);
            }
        }

        /// <summary>
        /// Design section 2.12 Rule 3 after a reload during a Re-Fly session (F9, a cold load
        /// of a save written mid-session, a scene reload): the invoke's holds live in memory
        /// and the held vessels were despawned before any save, so the reload left them out of
        /// the world with nothing drawing them. Re-takes every hold the session marker saved
        /// (<see cref="ReFlySessionMarker.InvokeHolds"/>): a vessel the chain feed holds is left
        /// to it, a live one is despawned as the invoke did, and a gone one is tracked with the
        /// rewind point's own copy of it as the hold ghost's shape. Skipped while a Re-Fly
        /// invoke is in flight (it takes its own holds). Logs one summary.
        /// </summary>
        internal void RestoreReFlyInvokeHoldsFromMarker(double currentUT)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario)) return;
            ReFlySessionMarker marker = scenario.ActiveReFlySessionMarker;
            if (marker == null || marker.InvokeHolds == null || marker.InvokeHolds.Count == 0)
                return;
            if (RewindInvokeContext.Pending)
            {
                ParsekLog.Verbose(ClaimedVesselHoldGhost.Tag,
                    $"Re-Fly invoke holds not restored: an invoke is in flight sess={marker.SessionId ?? "<no-id>"}");
                return;
            }
            if (host.vesselGhoster == null)
                host.vesselGhoster = new VesselGhoster();

            ConfigNode rpFlightState = null;
            bool rpFlightStateRead = false;
            int restored = 0, heldLive = 0, alreadyHeld = 0, claimPassed = 0, noSnapshot = 0, otherLaunch = 0;
            for (int i = 0; i < marker.InvokeHolds.Count; i++)
            {
                ReFlyHoldEntry entry = marker.InvokeHolds[i];
                if (entry == null || entry.Pid == 0u) continue;
                Vessel live = FlightRecorder.FindVesselByPid(entry.Pid);
                string liveGuid = live != null && live.id != Guid.Empty ? live.id.ToString("N") : null;
                ConfigNode copy = null;
                if (live == null && currentUT < entry.FirstClaimUT && !host.vesselGhoster.IsGhosted(entry.Pid))
                {
                    if (!rpFlightStateRead)
                    {
                        rpFlightState = ReadRewindPointFlightState(scenario, marker.RewindPointId);
                        rpFlightStateRead = true;
                    }
                    copy = ReFlyInvokeHold.FindHeldVesselNode(rpFlightState, entry.Pid, entry.LaunchGuid);
                }
                string action = ReFlyInvokeHold.DecideReloadAction(
                    entry, currentUT, host.vesselGhoster.IsGhosted(entry.Pid), live != null, liveGuid, copy != null);
                switch (action)
                {
                    case "claim-passed": claimPassed++; continue;
                    case "already-held": alreadyHeld++; continue;
                    case "other-launch": otherLaunch++; continue;
                    case "no-snapshot": noSnapshot++; continue;
                    case "hold-live":
                        if (!host.vesselGhoster.GhostVessel(entry.Pid)) { noSnapshot++; continue; }
                        heldLive++;
                        break;
                    case "restore":
                        if (!host.vesselGhoster.TrackHeldVesselWithoutLiveVessel(
                                entry.Pid, copy.GetValue("name"), copy.CreateCopy()))
                        {
                            alreadyHeld++;
                            continue;
                        }
                        restored++;
                        break;
                    default:
                        continue;
                }
                AddReFlyInvokeHold(entry, "refly-invoke-hold-reload");
            }

            if (restored + heldLive > 0)
                GhostPlaybackLogic.SetIsGhostedOverride(
                    pid => host.vesselGhoster != null && host.vesselGhoster.IsGhosted(pid));

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                $"Re-Fly invoke holds restored after reload: sess={marker.SessionId ?? "<no-id>"} " +
                $"saved={marker.InvokeHolds.Count.ToString(ic)} restored={restored.ToString(ic)} " +
                $"heldLive={heldLive.ToString(ic)} alreadyHeld={alreadyHeld.ToString(ic)} " +
                $"claimPassed={claimPassed.ToString(ic)} otherLaunch={otherLaunch.ToString(ic)} " +
                $"noSnapshot={noSnapshot.ToString(ic)} UT={currentUT.ToString("F1", ic)} " +
                $"marker={(marker.LoadedFromSave ? "save" : "memory")}");
        }

        /// <summary>
        /// Rewind design 10.13 on a flight load: the vessels the last plain rewind held back for
        /// their committed future (<see cref="RewindVesselHold"/>) are absent from the world, so
        /// nothing would draw them. Tracks each one whose first claim is still ahead with its
        /// rewind-time copy as the hold ghost's shape (a live one, which only a stale world can
        /// hold, is despawned as the Re-Fly hold does), so
        /// <see cref="UpdateClaimedVesselHoldGhosts"/> draws it until the committed recording
        /// takes over. Logs one summary.
        /// </summary>
        internal void RestoreRewindVesselHolds(double currentUT)
        {
            IReadOnlyList<RewindVesselHold.Hold> holds = RewindVesselHold.Holds;
            if (holds == null || holds.Count == 0)
                return;
            if (host.vesselGhoster == null)
                host.vesselGhoster = new VesselGhoster();

            int restored = 0, heldLive = 0, alreadyHeld = 0, claimPassed = 0, noSnapshot = 0, otherLaunch = 0;
            for (int i = 0; i < holds.Count; i++)
            {
                RewindVesselHold.Hold hold = holds[i];
                ReFlyHoldEntry entry = hold?.Entry;
                if (entry == null || entry.Pid == 0u) continue;
                Vessel live = FlightRecorder.FindVesselByPid(entry.Pid);
                string liveGuid = live != null && live.id != Guid.Empty ? live.id.ToString("N") : null;
                string action = ReFlyInvokeHold.DecideReloadAction(
                    entry, currentUT, host.vesselGhoster.IsGhosted(entry.Pid), live != null, liveGuid,
                    hold.Snapshot != null);
                switch (action)
                {
                    case "claim-passed": claimPassed++; continue;
                    case "already-held": alreadyHeld++; continue;
                    case "other-launch": otherLaunch++; continue;
                    case "no-snapshot": noSnapshot++; continue;
                    case "hold-live":
                        if (!host.vesselGhoster.GhostVessel(entry.Pid)) { noSnapshot++; continue; }
                        heldLive++;
                        break;
                    case "restore":
                        if (!host.vesselGhoster.TrackHeldVesselWithoutLiveVessel(
                                entry.Pid, hold.VesselName ?? hold.Snapshot.GetValue("name"),
                                hold.Snapshot.CreateCopy()))
                        {
                            alreadyHeld++;
                            continue;
                        }
                        restored++;
                        break;
                    default:
                        continue;
                }
                AddReFlyInvokeHold(entry, "rewind-hold");
            }

            if (restored + heldLive > 0)
                GhostPlaybackLogic.SetIsGhostedOverride(
                    pid => host.vesselGhoster != null && host.vesselGhoster.IsGhosted(pid));

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                $"Rewind vessel holds restored on flight load: saved={holds.Count.ToString(ic)} " +
                $"restored={restored.ToString(ic)} heldLive={heldLive.ToString(ic)} " +
                $"alreadyHeld={alreadyHeld.ToString(ic)} claimPassed={claimPassed.ToString(ic)} " +
                $"otherLaunch={otherLaunch.ToString(ic)} noSnapshot={noSnapshot.ToString(ic)} " +
                $"UT={currentUT.ToString("F1", ic)}");
        }

        /// <summary>The FLIGHTSTATE of the rewind point's quicksave, or null when it cannot be read.</summary>
        internal static ConfigNode ReadRewindPointFlightState(ParsekScenario scenario, string rewindPointId)
        {
            if (scenario.RewindPoints == null || string.IsNullOrEmpty(rewindPointId)) return null;
            RewindPoint rp = null;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                if (scenario.RewindPoints[i] != null && scenario.RewindPoints[i].RewindPointId == rewindPointId)
                {
                    rp = scenario.RewindPoints[i];
                    break;
                }
            }
            string path = RewindInvoker.ResolveAbsoluteQuicksavePath(rp);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag,
                    $"Re-Fly invoke hold reload: rewind point save unreadable rp={rewindPointId} " +
                    $"path='{path ?? "<none>"}'");
                return null;
            }
            try
            {
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return null;
                ConfigNode game = root.HasNode("GAME") ? root.GetNode("GAME") : root;
                return game?.GetNode("FLIGHTSTATE");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag,
                    $"Re-Fly invoke hold reload: reading rp={rewindPointId} threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Draws every vessel an active ghost chain holds back while the clock is before its
        /// first claim, and lets go of it at the claim (the committed recording that carries
        /// the vessel takes over), when a recording of the same launch is already drawing it,
        /// or when the chain goes away.
        /// </summary>
        internal void UpdateClaimedVesselHoldGhosts(double currentUT)
        {
            if (claimedVesselHoldGhosts.Count == 0
                && host.reFlyInvokeHolds.Count == 0
                && (host.activeGhostChains == null || host.activeGhostChains.Count == 0 || host.vesselGhoster == null))
                return;

            claimedVesselHoldDesiredScratch.Clear();
            IReadOnlyList<Recording> ers = null;
            if (host.activeGhostChains != null && host.vesselGhoster != null)
            {
                foreach (var kv in host.activeGhostChains)
                {
                    GhostChain chain = kv.Value;
                    if (chain == null || chain.IsTerminated) continue;
                    List<ChainClaimedVessel> held = chain.HeldBackVessels();
                    for (int h = 0; h < held.Count; h++)
                    {
                        ChainClaimedVessel claim = held[h];
                        if (claim.Pid == 0 || claimedVesselHoldDesiredScratch.ContainsKey(claim.Pid))
                            continue;
                        var info = host.vesselGhoster.GetGhostedInfo(claim.Pid);
                        if (info == null || info.snapshot == null || currentUT >= claim.FirstClaimUT)
                            continue;
                        if (ers == null) ers = EffectiveState.ComputeERS();
                        string reason = ClaimedVesselHoldGhost.DecideHideReason(
                            hasSnapshot: true,
                            chainActive: true,
                            currentUT: currentUT,
                            firstClaimUT: claim.FirstClaimUT,
                            coveredByRecording: ClaimedVesselHoldGhost.CoveredByRecording(
                                claim.Pid, claim.LaunchGuid, ers, currentUT));
                        if (reason != null) continue;
                        claimedVesselHoldDesiredScratch[claim.Pid] =
                            new KeyValuePair<GhostChain, ChainClaimedVessel>(chain, claim);
                    }
                }
            }

            if (host.vesselGhoster != null && host.reFlyInvokeHolds.Count > 0)
            {
                foreach (var kv in host.reFlyInvokeHolds)
                {
                    ChainClaimedVessel claim = kv.Value;
                    if (claimedVesselHoldDesiredScratch.ContainsKey(claim.Pid))
                        continue;
                    var info = host.vesselGhoster.GetGhostedInfo(claim.Pid);
                    if (info == null || info.snapshot == null || currentUT >= claim.FirstClaimUT)
                        continue;
                    if (ers == null) ers = EffectiveState.ComputeERS();
                    if (DecideReFlyInvokeHoldHideReason(claim, currentUT, ers) != null)
                        continue;
                    claimedVesselHoldDesiredScratch[claim.Pid] =
                        new KeyValuePair<GhostChain, ChainClaimedVessel>(null, claim);
                }
            }

            ReleaseClaimedVesselHoldGhosts(currentUT);

            foreach (var kv in claimedVesselHoldDesiredScratch)
            {
                ParsekFlight.ClaimedVesselHoldGhostState s;
                if (!claimedVesselHoldGhosts.TryGetValue(kv.Key, out s))
                {
                    s = CaptureClaimedVesselHoldGhost(kv.Value.Key, kv.Value.Value, currentUT);
                    claimedVesselHoldGhosts[kv.Key] = s;
                }
                PositionClaimedVesselHoldGhost(s, currentUT);
            }
            claimedVesselHoldDesiredScratch.Clear();
        }

        private void ReleaseClaimedVesselHoldGhosts(double currentUT)
        {
            if (claimedVesselHoldGhosts.Count == 0)
                return;
            claimedVesselHoldGhostReleaseScratch.Clear();
            foreach (var kvp in claimedVesselHoldGhosts)
            {
                ParsekFlight.ClaimedVesselHoldGhostState s = kvp.Value;
                var info = host.vesselGhoster != null ? host.vesselGhoster.GetGhostedInfo(s.pid) : null;
                bool sameSnapshot = info != null && ReferenceEquals(info.snapshot, s.snapshot);
                if (claimedVesselHoldDesiredScratch.ContainsKey(kvp.Key) && sameSnapshot)
                    continue;
                string reason = !sameSnapshot ? "ghosting-ended" : ResolveHoldReleaseReason(s.pid, currentUT);
                DestroyClaimedVesselHoldGhost(s, reason, currentUT);
                claimedVesselHoldGhostReleaseScratch.Add(kvp.Key);
            }
            for (int i = 0; i < claimedVesselHoldGhostReleaseScratch.Count; i++)
                claimedVesselHoldGhosts.Remove(claimedVesselHoldGhostReleaseScratch[i]);
            claimedVesselHoldGhostReleaseScratch.Clear();
        }

        /// <summary>Names why a shown hold ghost is no longer wanted (for its release line).</summary>
        internal string ResolveHoldReleaseReason(uint pid, double currentUT)
        {
            if (host.activeGhostChains != null)
            {
                foreach (var kv in host.activeGhostChains)
                {
                    GhostChain chain = kv.Value;
                    if (chain == null || chain.IsTerminated) continue;
                    List<ChainClaimedVessel> held = chain.HeldBackVessels();
                    for (int h = 0; h < held.Count; h++)
                    {
                        if (held[h].Pid != pid) continue;
                        return ClaimedVesselHoldGhost.DecideHideReason(
                            hasSnapshot: true, chainActive: true, currentUT: currentUT,
                            firstClaimUT: held[h].FirstClaimUT,
                            coveredByRecording: ClaimedVesselHoldGhost.CoveredByRecording(
                                pid, held[h].LaunchGuid, EffectiveState.ComputeERS(), currentUT))
                            ?? "unknown";
                    }
                }
            }
            ChainClaimedVessel invokeClaim;
            if (host.reFlyInvokeHolds.TryGetValue(pid, out invokeClaim))
                return DecideReFlyInvokeHoldHideReason(invokeClaim, currentUT, EffectiveState.ComputeERS())
                    ?? "unknown";
            return "chain-inactive";
        }

        internal static string DecideReFlyInvokeHoldHideReason(
            ChainClaimedVessel claim, double currentUT, IReadOnlyList<Recording> ers)
        {
            return ClaimedVesselHoldGhost.DecideHideReason(
                hasSnapshot: true,
                chainActive: true,
                currentUT: currentUT,
                firstClaimUT: claim.FirstClaimUT,
                coveredByRecording: ClaimedVesselHoldGhost.CoveredByRecording(
                    claim.Pid, claim.LaunchGuid, ers, currentUT));
        }

        private void DestroyClaimedVesselHoldGhost(ParsekFlight.ClaimedVesselHoldGhostState s, string reason, double currentUT)
        {
            if (s.ghost != null)
                UnityEngine.Object.Destroy(s.ghost);
            if (!s.buildFailed)
                ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                    ClaimedVesselHoldGhost.BuildReleasedMessage(
                        s.pid, s.vesselName, reason, s.shownSinceUT, currentUT));
            s.ghost = null;
        }

        /// <summary>Destroys every hold ghost (scene teardown, chain reset).</summary>
        internal void ClearClaimedVesselHoldGhosts(string reason)
        {
            if (claimedVesselHoldGhosts.Count == 0)
                return;
            double ut = 0.0;
            try { ut = Planetarium.GetUniversalTime(); }
            catch (Exception) { }
            foreach (var kvp in claimedVesselHoldGhosts)
                DestroyClaimedVesselHoldGhost(kvp.Value, reason, ut);
            claimedVesselHoldGhosts.Clear();
        }

        private ParsekFlight.ClaimedVesselHoldGhostState CaptureClaimedVesselHoldGhost(
            GhostChain chain, ChainClaimedVessel claim, double currentUT)
        {
            var info = host.vesselGhoster.GetGhostedInfo(claim.Pid);
            var s = new ParsekFlight.ClaimedVesselHoldGhostState
            {
                pid = claim.Pid,
                chainPid = chain != null ? chain.OriginalVesselPid : 0u,
                vesselName = info.vesselName,
                snapshot = info.snapshot,
                shownSinceUT = currentUT,
            };
            ConfigNode rewindCopy = ClaimedVesselHoldGhost.TryGetRewindSaveVessel(claim.Pid, claim.LaunchGuid);
            s.fromRewindSave = rewindCopy != null;
            s.visual = rewindCopy ?? info.snapshot;

            if (!ClaimedVesselHoldGhost.TryReadPose(s.visual, out s.pose)
                || FlightGlobals.Bodies == null || s.pose.BodyIndex >= FlightGlobals.Bodies.Count)
            {
                s.buildFailed = true;
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Claimed vessel hold ghost not drawn: pid={0} name='{1}' reason=snapshot-pose-unreadable",
                    s.pid, s.vesselName ?? "(unnamed)"));
                return s;
            }
            s.body = FlightGlobals.Bodies[s.pose.BodyIndex];
            s.surfaceRelativeRotation = s.pose.HasRotation
                ? new Quaternion((float)s.pose.RotX, (float)s.pose.RotY, (float)s.pose.RotZ, (float)s.pose.RotW)
                : Quaternion.identity;
            if (!s.pose.Landed)
            {
                try
                {
                    s.orbit = new OrbitSnapshot(s.visual.GetNode("ORBIT")).Load();
                }
                catch (Exception ex)
                {
                    s.orbit = null;
                    ParsekLog.Warn(ClaimedVesselHoldGhost.Tag, string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Claimed vessel hold ghost orbit unreadable: pid={0} name='{1}': {2}",
                        s.pid, s.vesselName ?? "(unnamed)", ex.Message));
                }
                if (s.orbit == null)
                {
                    s.buildFailed = true;
                    return s;
                }
            }

            var carrier = new Recording
            {
                VesselName = s.vesselName,
                VesselPersistentId = s.pid,
                GhostVisualSnapshot = s.visual,
            };
            GhostBuildResult build = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                carrier, "Parsek_ClaimedHold_" + s.pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (build == null || build.root == null)
            {
                s.buildFailed = true;
                ParsekLog.Warn(ClaimedVesselHoldGhost.Tag, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Claimed vessel hold ghost not drawn: pid={0} name='{1}' reason=ghost-build-failed",
                    s.pid, s.vesselName ?? "(unnamed)"));
                return s;
            }
            s.ghost = build.root;
            ConfigNode[] parts = s.visual.GetNodes("PART");
            ParsekLog.Info(ClaimedVesselHoldGhost.Tag,
                ClaimedVesselHoldGhost.BuildShownMessage(
                    s.pid, s.vesselName, s.chainPid, s.pose.Landed, s.body.name,
                    parts != null ? parts.Length : 0, claim.FirstClaimUT, currentUT, s.fromRewindSave));
            return s;
        }

        private void PositionClaimedVesselHoldGhost(ParsekFlight.ClaimedVesselHoldGhostState s, double currentUT)
        {
            if (s == null || s.ghost == null || s.body == null)
                return;

            double lat = s.pose.Lat;
            double lon = s.pose.Lon;
            double alt = s.pose.Alt;
            Vector3d pos;
            if (!s.pose.Landed && s.orbit != null)
            {
                pos = s.orbit.getPositionAtUT(currentUT);
                if (double.IsNaN(pos.x) || double.IsNaN(pos.y) || double.IsNaN(pos.z)
                    || double.IsInfinity(pos.x) || double.IsInfinity(pos.y) || double.IsInfinity(pos.z))
                    return;
                lat = s.body.GetLatitude(pos);
                lon = s.body.GetLongitude(pos);
                alt = s.body.GetAltitude(pos);
            }
            else
            {
                pos = s.body.GetWorldSurfacePosition(lat, lon, alt);
            }

            // Out of visual range the ghost is hidden, the same range every playback ghost uses.
            Vessel active = FlightGlobals.ActiveVessel;
            bool inRange = active == null
                || Vector3d.Distance(active.GetWorldPos3D(), pos) <= DistanceThresholds.GhostVisualRangeMeters;
            if (inRange != s.inRange || s.ghost.activeSelf != inRange)
            {
                s.ghost.SetActive(inRange);
                if (inRange != s.inRange)
                    ParsekLog.Verbose(ClaimedVesselHoldGhost.Tag, string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Claimed vessel hold ghost {0}: pid={1} UT={2:F2}",
                        inRange ? "in visual range" : "out of visual range", s.pid, currentUT));
                s.inRange = inRange;
            }
            if (!inRange)
                return;

            s.ghost.transform.position = pos;
            s.ghost.transform.rotation = s.body.bodyTransform.rotation * s.surfaceRelativeRotation;
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = s.ghost,
                mode = ParsekFlight.GhostPosMode.SinglePoint,
                recordingId = null,
                recordingIndex = -1,
                hasRecordingIndex = false,
                bodyBefore = s.body,
                latBefore = lat,
                lonBefore = lon,
                altBefore = alt,
                pointUT = currentUT,
                interpolatedRot = s.surfaceRelativeRotation,
            });
        }

        private const float BlockedChainTipGhostPositionLogIntervalSeconds = 5f;

        internal readonly Dictionary<uint, ParsekFlight.BlockedChainTipGhostState> blockedChainTipGhosts =
            new Dictionary<uint, ParsekFlight.BlockedChainTipGhostState>();

        private readonly List<uint> blockedChainTipGhostReleaseScratch = new List<uint>();

        /// <summary>
        /// In-game test seam: positions <paramref name="ghost"/> through the production
        /// blocked-tip positioning (orbit source when <paramref name="orbit"/> is non-null, else
        /// the body-fixed hold), registering its FloatingOrigin reapply entry exactly as the
        /// per-frame pass does. Not called by any player path.
        /// </summary>
        internal void PositionBlockedChainTipGhostForInGameTest(
            GameObject ghost, CelestialBody body, Orbit orbit, Quaternion surfaceRelativeRotation,
            double holdLat, double holdLon, double holdAlt, double ut)
        {
            var s = new ParsekFlight.BlockedChainTipGhostState
            {
                recordingId = "ingame-blocked-chain-tip-probe",
                index = -1,
                ghost = ghost,
                body = body,
                orbit = orbit,
                source = ChainTipBlockedGhost.ResolvePoseSource(orbit != null, orbit != null),
                holdLat = holdLat,
                holdLon = holdLon,
                holdAlt = holdAlt,
                surfaceRelativeRotation = surfaceRelativeRotation,
                heldSinceUT = ut,
                lastPositionLogRealTime = Time.realtimeSinceStartup,
            };
            PositionBlockedChainTipGhost(s, ut);
        }

        /// <summary>
        /// Draws the held ghost of every spawn-blocked chain tip at the tip's propagated
        /// position, and lets go of it the moment the tip spawns or the hold loses its subject.
        /// Runs after the policy's held-ghost retries, so a spawn that succeeded this frame has
        /// already destroyed the ghost and is released here before anything moves it.
        /// </summary>
        private void UpdateSpawnBlockedChainTipGhosts(double currentUT)
        {
            bool anyHeld = host.policy != null && host.policy.heldGhosts.Count > 0;
            if (blockedChainTipGhosts.Count == 0 && !anyHeld)
                return;

            var committed = RecordingStore.CommittedRecordings;
            ReleaseStaleBlockedChainTipGhosts(committed, currentUT);
            if (anyHeld)
                CaptureBlockedChainTipGhosts(committed, currentUT);

            foreach (var kvp in blockedChainTipGhosts)
                PositionBlockedChainTipGhost(kvp.Value, currentUT);
        }

        internal void ReleaseStaleBlockedChainTipGhosts(IReadOnlyList<Recording> committed, double currentUT)
        {
            if (blockedChainTipGhosts.Count == 0)
                return;

            blockedChainTipGhostReleaseScratch.Clear();
            foreach (var kvp in blockedChainTipGhosts)
            {
                ParsekFlight.BlockedChainTipGhostState s = kvp.Value;
                bool indexStillTip = committed != null
                    && s.index >= 0 && s.index < committed.Count
                    && committed[s.index] != null
                    && committed[s.index].RecordingId == s.recordingId;
                bool tipSpawned = indexStillTip && committed[s.index].VesselSpawned;
                GhostChain current = null;
                bool chainActive = host.activeGhostChains != null
                    && host.activeGhostChains.TryGetValue(s.chainPid, out current)
                    && object.ReferenceEquals(current, s.chain)
                    && !current.IsTerminated;
                bool heldByPolicy = host.policy != null && host.policy.heldGhosts.ContainsKey(s.index);
                GhostPlaybackState state;
                bool engineGhostIntact = host.engine != null
                    && host.engine.TryGetGhostState(s.index, out state)
                    && state != null
                    && object.ReferenceEquals(state.ghost, s.ghost)
                    && s.ghost != null;

                string reason = ChainTipBlockedGhost.DecideReleaseReason(
                    tipSpawned,
                    chainActive,
                    s.chain != null && s.chain.SpawnBlocked,
                    indexStillTip,
                    heldByPolicy,
                    engineGhostIntact);
                if (reason == null)
                    continue;

                ParsekLog.Info(ChainTipBlockedGhost.Tag,
                    ChainTipBlockedGhost.BuildReleasedMessage(
                        s.index, s.recordingId, s.vesselName, s.chainPid,
                        reason, s.heldSinceUT, currentUT));
                blockedChainTipGhostReleaseScratch.Add(kvp.Key);
            }

            for (int i = 0; i < blockedChainTipGhostReleaseScratch.Count; i++)
                blockedChainTipGhosts.Remove(blockedChainTipGhostReleaseScratch[i]);
            blockedChainTipGhostReleaseScratch.Clear();
        }

        internal void CaptureBlockedChainTipGhosts(IReadOnlyList<Recording> committed, double currentUT)
        {
            if (committed == null || host.activeGhostChains == null || host.activeGhostChains.Count == 0
                || host.engine == null)
                return;

            foreach (var kvp in host.policy.heldGhosts)
            {
                int index = kvp.Key;
                if (index < 0 || index >= committed.Count)
                    continue;
                Recording rec = committed[index];
                if (rec == null)
                    continue;
                GhostChain chain = ParsekFlight.FindChainTipForRecording(host.activeGhostChains, rec);
                if (chain == null)
                    continue;

                GhostPlaybackState state;
                bool engineHasGhost = host.engine.TryGetGhostState(index, out state)
                    && state != null && state.ghost != null;
                if (!ChainTipBlockedGhost.ShouldCapture(
                        alreadyCaptured: blockedChainTipGhosts.ContainsKey(chain.OriginalVesselPid),
                        tipSpawned: rec.VesselSpawned,
                        chainActive: true,
                        chainSpawnBlocked: chain.SpawnBlocked,
                        heldByPolicy: true,
                        engineHasGhost: engineHasGhost))
                    continue;

                CaptureBlockedChainTipGhost(chain, rec, index, state.ghost, currentUT);
            }
        }

        private void CaptureBlockedChainTipGhost(
            GhostChain chain, Recording rec, int index, GameObject ghost, double currentUT)
        {
            TrajectoryPoint? lastPoint = rec.Points != null && rec.Points.Count > 0
                ? (TrajectoryPoint?)rec.Points[rec.Points.Count - 1]
                : null;
            // The same body and orbit the blocked-tip spawn resolves
            // (VesselGhoster.TrySpawnBlockedChain), so the ghost is drawn where the vessel
            // would appear if the blocker moved away now.
            CelestialBody body = VesselSpawner.ResolveSpawnRotationBody(rec, lastPoint);
            bool spawnUsesOrbit = VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(rec);
            Orbit orbit = null;
            bool orbitBuilt = spawnUsesOrbit && body != null
                && VesselSpawner.TryBuildRecordedTerminalOrbitForSpawn(rec, body, currentUT, out orbit)
                && orbit != null;

            var s = new ParsekFlight.BlockedChainTipGhostState
            {
                chain = chain,
                chainPid = chain.OriginalVesselPid,
                index = index,
                recordingId = rec.RecordingId,
                vesselName = rec.VesselName,
                ghost = ghost,
                source = ChainTipBlockedGhost.ResolvePoseSource(spawnUsesOrbit, orbitBuilt),
                body = body,
                orbit = orbitBuilt ? orbit : null,
                heldSinceUT = currentUT,
                lastPositionLogRealTime = Time.realtimeSinceStartup,
            };

            if (body != null)
            {
                Vector3d endPos = ghost.transform.position;
                s.holdLat = body.GetLatitude(endPos);
                s.holdLon = body.GetLongitude(endPos);
                s.holdAlt = body.GetAltitude(endPos);
                s.surfaceRelativeRotation =
                    Quaternion.Inverse(body.bodyTransform.rotation) * ghost.transform.rotation;
            }
            else
            {
                ParsekLog.Warn(ChainTipBlockedGhost.Tag,
                    "Blocked chain tip ghost has no spawn body: #" + index
                    + " rec=" + (rec.RecordingId ?? "(none)")
                    + " - the ghost stays at its end pose without a floating-origin reapply");
            }

            blockedChainTipGhosts[s.chainPid] = s;
            ParsekLog.Info(ChainTipBlockedGhost.Tag,
                ChainTipBlockedGhost.BuildHeldMessage(
                    index, s.recordingId, s.vesselName, s.chainPid, s.source,
                    body != null ? body.name : null, currentUT, chain.BlockedSinceUT));
        }

        /// <summary>
        /// After an epoch-shift jump, rebuild each held ghost's orbit through the spawn
        /// resolver at the new UT. The resolver's seed depends on the UT (a trajectory-tail
        /// seed is built with the body's live rotation and is declined once the UT drifts too
        /// far from the tail), so the orbit built before the jump can differ from the one the
        /// spawn will use; rebuilding keeps the ghost where the blocked vessel would appear.
        /// </summary>
        internal void RefreshBlockedChainTipGhostOrbitsAfterJump()
        {
            if (blockedChainTipGhosts.Count == 0)
                return;
            double currentUT = Planetarium.GetUniversalTime();
            var committed = RecordingStore.CommittedRecordings;
            int rebuilt = 0;
            foreach (var kvp in blockedChainTipGhosts)
            {
                ParsekFlight.BlockedChainTipGhostState s = kvp.Value;
                if (s == null || s.orbit == null || s.body == null)
                    continue;
                if (s.index < 0 || s.index >= committed.Count || committed[s.index] == null
                    || committed[s.index].RecordingId != s.recordingId)
                    continue;
                if (VesselSpawner.TryBuildRecordedTerminalOrbitForSpawn(
                        committed[s.index], s.body, currentUT, out Orbit orbit) && orbit != null)
                {
                    s.orbit = orbit;
                    rebuilt++;
                }
            }
            ParsekLog.Verbose(ChainTipBlockedGhost.Tag,
                string.Format(CultureInfo.InvariantCulture,
                    "Held chain tip ghost orbits rebuilt after time jump: held={0} rebuilt={1}",
                    blockedChainTipGhosts.Count, rebuilt));
        }

        private void PositionBlockedChainTipGhost(ParsekFlight.BlockedChainTipGhostState s, double currentUT)
        {
            if (s.body == null || s.ghost == null)
                return;

            double lat = s.holdLat;
            double lon = s.holdLon;
            double alt = s.holdAlt;
            Vector3d pos;
            if (s.source == BlockedChainTipPoseSource.RecordedTerminalOrbit && s.orbit != null)
            {
                pos = s.orbit.getPositionAtUT(currentUT);
                if (double.IsNaN(pos.x) || double.IsNaN(pos.y) || double.IsNaN(pos.z)
                    || double.IsInfinity(pos.x) || double.IsInfinity(pos.y) || double.IsInfinity(pos.z))
                    return;
                lat = s.body.GetLatitude(pos);
                lon = s.body.GetLongitude(pos);
                alt = s.body.GetAltitude(pos);
            }
            else
            {
                pos = s.body.GetWorldSurfacePosition(lat, lon, alt);
            }

            s.ghost.transform.position = pos;
            s.ghost.transform.rotation = s.body.bodyTransform.rotation * s.surfaceRelativeRotation;

            // Same FloatingOrigin reapply every playback ghost registers, keyed by body
            // lat/lon/alt so a shift between Update and render cannot displace the ghost.
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = s.ghost,
                mode = ParsekFlight.GhostPosMode.SinglePoint,
                recordingId = s.recordingId,
                recordingIndex = s.index,
                hasRecordingIndex = true,
                bodyBefore = s.body,
                latBefore = lat,
                lonBefore = lon,
                altBefore = alt,
                pointUT = currentUT,
                interpolatedRot = s.surfaceRelativeRotation,
            });

            float now = Time.realtimeSinceStartup;
            if (ParsekLog.IsVerboseEnabled
                && now - s.lastPositionLogRealTime >= BlockedChainTipGhostPositionLogIntervalSeconds)
            {
                s.lastPositionLogRealTime = now;
                ParsekLog.Verbose(ChainTipBlockedGhost.Tag,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Blocked chain tip ghost positioned: #{0} rec={1} chainPid={2} source={3} " +
                        "body={4} lat={5:F4} lon={6:F4} alt={7:F1} UT={8:F1}",
                        s.index, s.recordingId ?? "(none)", s.chainPid,
                        ChainTipBlockedGhost.FormatPoseSource(s.source),
                        s.body.name, lat, lon, alt, currentUT));
            }
        }
    }
}
