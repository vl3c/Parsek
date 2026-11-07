using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Flight-scene map presence: the deferred create, orbit reseed and state-vector passes, and
    // the ghost created / destroyed handlers.
    internal static partial class GhostMapPresence
    {
        // ===================================================================================
        // Flight-scene map-presence lifecycle (Phase 8d.1 relocation from ParsekPlaybackPolicy).
        // FAITHFUL MOVE — the body, struct, dicts, scalars, and dict-touching helpers below were
        // copied VERBATIM from ParsekPlaybackPolicy.CheckPendingMapVessels and its helpers. The
        // only edits are: (a) engine.CurrentLoopUnits -> the loopUnits parameter, and (b) the
        // internal-static ShouldRunMapOrbitReseed / TryResolveTerminalFallbackMapOrbitUpdate /
        // ShouldRetainMapPresenceForTerminalRealSpawn helpers (left in ParsekPlaybackPolicy per the
        // plan) are now qualified with ParsekPlaybackPolicy. — those helpers' bodies are unchanged.
        // The fields use a flight* prefix to mirror the trackingStation* twin naming. They are
        // internal (not private) because the still-in-policy enqueue tail + teardowns reach them
        // until Phase 8d.2 relocates those too.
        // ===================================================================================

        /// <summary>
        /// Recording indices eligible for ghost map ProtoVessels but deferred until
        /// the ghost enters an orbital segment. Avoids showing orbit lines during
        /// atmospheric ascent when the ghost mesh is still on the pad.
        /// </summary>
        internal struct PendingMapVessel
        {
            internal IPlaybackTrajectory Trajectory;
            internal bool AllowSoiGapStateVectorFallback;
            internal string ExpectedSoiGapBody;

            internal PendingMapVessel(
                IPlaybackTrajectory trajectory,
                bool allowSoiGapStateVectorFallback,
                string expectedSoiGapBody)
            {
                Trajectory = trajectory;
                AllowSoiGapStateVectorFallback = allowSoiGapStateVectorFallback;
                ExpectedSoiGapBody = expectedSoiGapBody;
            }
        }

        /// <summary>
        /// Bulk-clear all flight-scene map-presence collections + reset the reseed timer.
        /// Faithfully reproduces the exact set of clears that ParsekPlaybackPolicy's
        /// HandleAllGhostsDestroying + Dispose performed for these collections (Phase 8d.1).
        /// </summary>
        internal static void ClearFlightMapPresenceState()
        {
            flightPendingMapVessels.Clear();
            flightLastMapOrbitByIndex.Clear();
            flightChainMapOwner.Clear();
            flightStateVectorOrbitTrajectories.Clear();
            flightSoiGapStateVectorExpectedBodies.Clear();
            flightStateVectorCachedIndices.Clear();
            flightTerminalMapRetentionLoggedIds.Clear();
            nextMapOrbitUpdateTime = 0f;
            lastFlightMapLifecycleUT = double.NaN;
        }

        /// <summary>
        /// Resolve the Mission-loop sample UT for a flight-scene map ghost and the matching
        /// live-frame epoch shift. Thin wrapper over
        /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleUT"/> (the same seam the
        /// Tracking Station and KSC map drivers use): returns the loop-mapped <c>effUT</c> for a
        /// looping member (and <paramref name="renderHidden"/>=true when it is outside its loop
        /// window this cycle), or the unchanged <paramref name="currentUT"/> with shift 0 for a
        /// non-member / when no Mission loops. <paramref name="loopEpochShiftSeconds"/> is
        /// <c>currentUT - effUT</c>: the amount the seeded orbit epoch + stored arc bounds must be
        /// pushed forward so the icon, drawn at the live clock, lands on the replayed position.
        /// </summary>
        internal static double ResolveMapPresenceSampleUT(
            int idx, double memberStartUT, double memberEndUT, double currentUT,
            GhostPlaybackLogic.LoopUnitSet loopUnits,
            out bool renderHidden, out double loopEpochShiftSeconds)
        {
            double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                idx, memberStartUT, memberEndUT, currentUT, loopUnits, out renderHidden);
            loopEpochShiftSeconds = currentUT - effUT;
            return effUT;
        }

        /// <summary>
        /// Build a per-instance overlap map ProtoVessel from a recorded state-vector point (physics-only
        /// suborbital ascent cycle), without the per-index <see cref="TrackRecordingGhostVessel"/> write
        /// or the Re-Fly suppression machinery in <see cref="CreateGhostVesselFromStateVectors"/> (which
        /// is per-index state and irrelevant to overlap loops). Resolves the world position via the same
        /// <see cref="ResolveStateVectorWorldPosition"/> the per-index path uses, then seeds an orbit
        /// from the world pos + recorded velocity.
        ///
        /// Mirrors the per-index state-vector contract (<c>UpdateGhostOrbitFromStateVectors</c>,
        /// GhostMapPresence.cs:7956-8000): the orbit is seeded at the SHIFTED epoch
        /// (<paramref name="effUT"/> + <paramref name="loopEpochShiftSeconds"/> == the instance's LIVE
        /// UT) so stock <see cref="OrbitDriver"/> propagation at the live Planetarium clock lands the
        /// icon on the world position recorded at <paramref name="effUT"/>. Physics-only recordings have
        /// no <c>OrbitSegments</c>, so there is no segment-drive raw-epoch + effUT contract here: the
        /// caller must NOT register <c>ghostOrbitEpochShift</c> / <c>ghostOrbitLoopShiftedPids</c> for a
        /// state-vector instance (that would make <see cref="GhostOrbitIconDrivePatch"/> bail on the
        /// no-bounds branch and re-subtract a shift from this already-shifted orbit). Returns null when
        /// the body or world frame cannot resolve (the cycle is then retried next tick).
        /// </summary>
        private static Vessel BuildAndLoadGhostProtoVesselFromStateVector(
            IPlaybackTrajectory traj, TrajectoryPoint point, double effUT, double loopEpochShiftSeconds,
            string logContext)
        {
            CelestialBody body = FindBodyByName(point.bodyName);
            if (body == null)
                return null;

            StateVectorWorldFrame resolution =
                ResolveStateVectorWorldPosition(traj, point, body, allowOrbitalCheckpointStateVector: true);
            if (!resolution.Resolved)
                return null;

            Vector3d worldPos = resolution.WorldPos;
            Vector3d vel = ResolveStateVectorSeedVelocity(
                traj, point, body, resolution, "overlap", -1, rateLimit: true);

            // Seed at the SHIFTED epoch (live UT), mirroring the per-index state-vector path so stock
            // propagation at the live clock lands the icon on the effUT-recorded world position.
            Orbit orbit = new Orbit();
            OrbitReseed.FromWorldPosAndRecordedVelocity(
                orbit, body, worldPos, vel, effUT + loopEpochShiftSeconds);

            return BuildAndLoadGhostProtoVesselCore(
                traj,
                orbit,
                body,
                logContext,
                "overlap-instance-state-vector",
                string.Format(ic,
                    "stateBody={0} effUT={1:F1} liveUT={2:F1} stateAlt={3:F0} stateSpeed={4:F1} frame={5}",
                    point.bodyName ?? "(null)", effUT, effUT + loopEpochShiftSeconds,
                    point.altitude, point.velocity.magnitude, resolution.Branch));
        }

        /// <summary>
        /// Per-frame check for ghost map ProtoVessels. Handles three responsibilities:
        /// 1. Creates deferred ProtoVessels when ghosts enter their first orbital segment
        /// 2. Creates state-vector orbit ProtoVessels for physics-only suborbital recordings
        /// 3. Updates existing ProtoVessel orbits (segment-based and state-vector-based)
        /// </summary>
        internal static void UpdateFlightMapGhostLifecycle(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            // Mission-loop span clock: the same per-frame LoopUnitSet the flight engine drives the
            // ghost MESHES with (engine.CurrentLoopUnits). The Tracking Station and KSC map drivers
            // already remap the live UT through this clock; this is the flight-scene equivalent so
            // a looped Mission's map orbit lines + icons sample the recording at the loop-mapped
            // effUT instead of the raw live UT (which is far outside a looped member's recorded UT
            // range). Empty (the common case) makes every ResolveMapPresenceSampleUT below return
            // the unchanged live UT with shift 0, so non-looping behavior is byte-identical.

            RefreshGhostProtoNamesIfDisplayNamesMoved("flight");
            RunFlightMapDeferredCreatePass(currentUT, loopUnits);            // Pass 1 (always runs)

            // Per-instance overlap sweep (slice i). The SOLE create/destroy authority for overlap
            // recordings on the flight map; the three legacy passes below skip overlap indices.
            // Runs in the always-runs section so cycles relaunch/expire promptly. Resolves the live
            // committed list here (the rate-limited section below resolves its own copy after the
            // gate; the overlap path must run every frame).
            RunOverlapPerInstanceSweep(currentUT, RecordingStore.CommittedRecordings, loopUnits);

            // 2. Map-orbit reseed for existing ProtoVessels. Rate-limited to the real-time timer, BUT
            // warp-aware: under time warp the playback head sprints through short segments (e.g. the many
            // short Duna-capture conics) faster than the 0.5 s timer reseeds, so the applied orbit goes
            // stale and GhostOrbitLinePatch's stale-segment guard blanks the line every frame (the ~1-min
            // warped-approach blink). Also reseed the moment a ghost's head leaves its applied segment so
            // the orbit stays current and the stale-segment guard (which still suppresses the genuine
            // pre-burn arc on a propulsive->orbital handoff) stops firing on reseed lag alone. The head-left
            // scan is computed only while warping + before the timer (so 1x is byte-identical + cost-free).
            //
            // Pass 2 gate + preamble stay HERE so the two early-returns keep skipping Pass 3.
            // Warp-scaled cadence: cap the GAME time one reseed tick may span (the 2026-06-12
            // reseed-lag icon jumps: a fixed 0.5 s REAL-time cadence spans ~170-500 game-s per tick
            // at 344x-1000x, so the icon snaps ~1 Mm on reseed). Pure law; base-interval-identical
            // at warp <= 1. The deadline clamp covers a warp STEP-UP mid-interval (an armed
            // low-rate tick would otherwise still span base*newRate game-seconds once).
            float mapReseedIntervalSec = ParsekPlaybackPolicy.ResolveMapOrbitReseedIntervalSec(
                MapOrbitUpdateIntervalSec, TimeWarp.CurrentRate);
            nextMapOrbitUpdateTime = ParsekPlaybackPolicy.ClampPendingReseedDeadline(
                nextMapOrbitUpdateTime, UnityEngine.Time.time, mapReseedIntervalSec);
            bool mapReseedTimerElapsed = UnityEngine.Time.time >= nextMapOrbitUpdateTime;
            bool mapReseedHeadLeftSegment = !mapReseedTimerElapsed
                && TimeWarp.CurrentRate > 1.0f
                && GhostMapPresence.AnyGhostHeadLeftAppliedSegment(currentUT);
            // A clock jump at 1x (a TimeJump, a Real Spawn Control warp): every head moved in one
            // frame, and the timer would leave the applied orbits stale until it fires, while the
            // stale-segment guard blanks those lines (V18T: one line dark 5 frames across a jump).
            // Reseed in this frame, before the line patch's LateUpdate reads the bounds.
            double previousLifecycleUT = lastFlightMapLifecycleUT;
            lastFlightMapLifecycleUT = currentUT;
            bool mapReseedClockJumped = !mapReseedTimerElapsed
                && ParsekPlaybackPolicy.IsMapClockDiscontinuity(
                    previousLifecycleUT, currentUT, TimeWarp.CurrentRate);
            if (mapReseedClockJumped)
            {
                ParsekLog.Verbose("Policy", string.Format(CultureInfo.InvariantCulture,
                    "Map orbit reseed forced by a clock jump: {0:F2} -> {1:F2} (warp={2:F1})",
                    previousLifecycleUT, currentUT, TimeWarp.CurrentRate));
            }
            if (!ParsekPlaybackPolicy.ShouldRunMapOrbitReseed(
                    mapReseedTimerElapsed, TimeWarp.CurrentRate, mapReseedHeadLeftSegment,
                    mapReseedClockJumped))
                return;
            nextMapOrbitUpdateTime = UnityEngine.Time.time + mapReseedIntervalSec;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return;
            PruneTerminalMapRetentionLogKeys(committed);

            RunFlightMapOrbitReseedPass(currentUT, loopUnits, committed);    // Pass 2 body
            RunFlightMapStateVectorUpdatePass(currentUT, loopUnits, committed); // Pass 3 body
        }

        // Pass 1 — deferred create. Always runs (no gate). Creates deferred ProtoVessels for ghosts
        // that just entered an orbital segment OR for physics-only recordings that crossed the
        // state-vector threshold. Body relocated verbatim from the former inline pass.
        private static void RunFlightMapDeferredCreatePass(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            // 1. Create deferred ProtoVessels for ghosts that just entered an orbital segment
            //    OR for physics-only recordings that crossed the state-vector threshold.
            if (flightPendingMapVessels.Count > 0)
            {
                List<(int idx, TrackingStationGhostSource source, OrbitSegment segment, TrajectoryPoint point, string expectedBody, double effUT)> toCreate = null;

                var committedForOverlapSkip = RecordingStore.CommittedRecordings;
                foreach (var kvp in flightPendingMapVessels)
                {
                    int idx = kvp.Key;
                    PendingMapVessel pending = kvp.Value;
                    IPlaybackTrajectory traj = pending.Trajectory;

                    // Slice (i): overlap recordings are owned solely by the per-instance sweep
                    // (RunOverlapPerInstanceSweep, run earlier this frame). Skip the legacy
                    // single-instance deferred create for them so there is no double-management.
                    if (committedForOverlapSkip != null && idx >= 0 && idx < committedForOverlapSkip.Count
                        && ShouldDriveOverlapPerInstance(committedForOverlapSkip[idx], idx, committedForOverlapSkip, loopUnits))
                        continue;

                    // Loop-mapped sample UT for this member (effUT == currentUT off the loop path).
                    // renderHidden => this member is outside its loop window this cycle: skip the
                    // create, exactly like the Tracking Station create pass skips it.
                    double effUT = ResolveMapPresenceSampleUT(
                        idx, traj.StartUT, traj.EndUT, currentUT, loopUnits,
                        out bool renderHidden, out double pendingLoopShift);
                    if (renderHidden)
                        continue;

                    // A non-zero loop epoch shift means this is a Mission-loop member replaying
                    // inside its window this cycle (effUT != live currentUT). Two consequences:
                    //  - it unlocks the no-segment terminal-orbit synthesis (plan section 1.4); and
                    //  - it lets the map ghost draw even when the recording already materialized a
                    //    persisted real terminal vessel (loopMemberInWindow). Without the latter, a
                    //    looped leg whose mission left a real craft parked at its terminal (e.g. the
                    //    Mun-return leg) gets no trajectory following the ghost, because the static
                    //    real vessel claims the materialization. Both flags default false off the loop
                    //    path, so neither the terminal-orbit synthesis nor the materialization bypass
                    //    affects non-loop members. (The EndpointTail entry in the acceptance check below
                    //    is a SEPARATE, intentional consistency fix -- it materializes a non-loop
                    //    member that resolves to EndpointTail, which the create path previously left
                    //    pending -- and is NOT gated by these flags.)
                    bool isLoopMemberInWindow = pendingLoopShift != 0.0;
                    bool acceptTerminalOrbitForLoopSynthesis =
                        isLoopMemberInWindow
                        && GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(traj);
                    if (acceptTerminalOrbitForLoopSynthesis)
                    {
                        ParsekLog.VerboseRateLimited("Policy",
                            string.Format(CultureInfo.InvariantCulture,
                                "pending-map-accept-terminal-{0}", idx),
                            string.Format(CultureInfo.InvariantCulture,
                                "Pending map-create accepting terminal-orbit synthesis for loop member " +
                                "rec=#{0} vessel=\"{1}\" pendingLoopShift={2:F1}",
                                idx,
                                traj.VesselName ?? "(null)",
                                pendingLoopShift),
                            5.0);
                    }

                    int cachedStateVectorIndex = flightStateVectorCachedIndices.TryGetValue(idx, out int cached)
                        ? cached
                        : -1;
                    // Step 2 (flight map presence): suppress this rec's OWN loop-ghost
                    // double ONLY while its guid-gated launch-matched live vessel is
                    // loaded AND it was the LIVE docking anchor of an in-window relative
                    // member this-or-last frame (the Step-1 live-bind event), NOT for the
                    // whole loop (mirrors the TS lifecycle pass). Keyed on the guid-gated
                    // RealVesselExistsForRecording, NOT the pid-only
                    // IsMaterializedForMapPresence that feeds alreadyMaterialized here, so
                    // a same-craft different-launch vessel never suppresses. Best-effort
                    // on this map path: the live-bind is stamped at member resolution
                    // (ghost (re)creation), not per on-rails frame, so the duplicate can
                    // briefly show until the next resolve (see WasLiveBoundThisOrLastFrame).
                    Recording pendingAnchorRec = (committedForOverlapSkip != null
                        && idx >= 0 && idx < committedForOverlapSkip.Count)
                        ? committedForOverlapSkip[idx]
                        : null;
                    bool liveLaunchMatchedAnchorOfActiveMember =
                        pendingAnchorRec != null
                        && GhostPlaybackLogic.RealVesselExistsForRecording(pendingAnchorRec)
                        && RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(pendingAnchorRec.RecordingId);
                    TrackingStationGhostSource source = GhostMapPresence.ResolveMapPresenceGhostSource(
                        traj,
                        false,
                        IsMaterializedForMapPresence(traj),
                        effUT,
                        true,
                        "map-presence-pending-create",
                        ref cachedStateVectorIndex,
                        out OrbitSegment segment,
                        out TrajectoryPoint point,
                        out _,
                        recordingIndex: idx,
                        allowSoiGapStateVectorFallback: pending.AllowSoiGapStateVectorFallback,
                        expectedSoiGapBody: pending.ExpectedSoiGapBody,
                        acceptTerminalOrbitForLoopSynthesis: acceptTerminalOrbitForLoopSynthesis,
                        loopMemberInWindow: isLoopMemberInWindow,
                        liveLaunchMatchedAnchorOfActiveMember: liveLaunchMatchedAnchorOfActiveMember);
                    flightStateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    // Re-aim: swap the recorded covering segment for the re-aimed one at create time so
                    // the orbit line is aimed at the target's CURRENT position from the first frame the
                    // ghost re-enters its window. A re-aim owner in a TRIM GAP (between a recorded
                    // body-relative leg and the trimmed interplanetary transfer) has no covering re-aimed
                    // segment: skip the create entirely (keep the member pending / hidden) rather than
                    // fall back to a recorded sub-segment, which re-created the ghost at a random orbit
                    // position every frame while the refresh removed it (the SOI-boundary icon flicker).
                    if (source == TrackingStationGhostSource.Segment
                        && !GhostMapPresence.TryResolveReaimedCoveringSegment(
                            idx, traj.RecordingId, traj.OrbitSegments, currentUT, effUT, loopUnits,
                            segment, out segment))
                        continue;

                    if (GhostMapPresence.IsMapCreateAcceptedSource(source))
                    {
                        if (toCreate == null)
                            toCreate = new List<(int, TrackingStationGhostSource, OrbitSegment, TrajectoryPoint, string, double)>();
                        toCreate.Add((idx, source, segment, point, pending.ExpectedSoiGapBody, effUT));
                    }
                }

                if (toCreate != null)
                {
                    for (int i = 0; i < toCreate.Count; i++)
                    {
                        int idx = toCreate[i].idx;
                        if (flightPendingMapVessels.TryGetValue(idx, out var pending))
                        {
                            IPlaybackTrajectory traj = pending.Trajectory;
                            // Per-chain dedup before deferred creation
                            RemovePreviousChainMapVessel(idx);
                            // Create at the loop-mapped effUT so the source/segment/point match the
                            // looped phase (effUT == currentUT off the loop path). Pass the live-frame
                            // epoch shift through so the orbit + body-frame cache are shifted at
                            // create-time too. Without this the body-frame cache holds raw recorded
                            // UTs on the first frame, and the orbit-line patch sees currentUT past
                            // those bounds and blanks the line until the next rate-limited update
                            // pass refreshes it (up to ~0.5s in flight, ~2s in TS) -- visible as a
                            // brief orbit-line blackout at first appearance and at every window
                            // re-entry.
                            double pendingLoopEpochShift = currentUT - toCreate[i].effUT;
                            Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                                idx,
                                traj,
                                toCreate[i].source,
                                toCreate[i].segment,
                                toCreate[i].point,
                                toCreate[i].effUT,
                                out bool retryLater,
                                loopEpochShiftSeconds: pendingLoopEpochShift);
                            // PR #574 review P2 (retry-later semantics): keep
                            // the pending entry alive when the active-Re-Fly
                            // suppression gate fires — otherwise a parent
                            // recording mid-flight in a Relative-anchored
                            // section would be permanently dropped from the
                            // pending-map queue and never re-attempt after
                            // the Re-Fly session ends.
                            if (retryLater && ghost == null)
                            {
                                ParsekLog.Verbose("Policy", string.Format(CultureInfo.InvariantCulture,
                                    "CheckPendingMapVessels: kept pending entry for #{0} \"{1}\" — " +
                                    "map ghost creation requested retryLater, will retry next tick",
                                    idx, traj.VesselName ?? "(null)"));
                                continue;
                            }
                            flightPendingMapVessels.Remove(idx);

                            if (ghost != null)
                            {
                                if (GhostMapPresence.IsStateVectorGhostSource(toCreate[i].source))
                                {
                                    flightStateVectorOrbitTrajectories[idx] = traj;
                                    if (toCreate[i].source == TrackingStationGhostSource.StateVectorSoiGap)
                                        flightSoiGapStateVectorExpectedBodies[idx] = toCreate[i].expectedBody;
                                    else
                                        flightSoiGapStateVectorExpectedBodies.Remove(idx);
                                    ParsekLog.Info("Policy", string.Format(CultureInfo.InvariantCulture,
                                        "Created state-vector ghost map vessel for #{0} \"{1}\" — alt={2:F0} speed={3:F1} source={4}",
                                        idx, traj.VesselName,
                                        toCreate[i].point.altitude,
                                        toCreate[i].point.velocity.magnitude,
                                        toCreate[i].source == TrackingStationGhostSource.StateVectorSoiGap
                                            ? "soi-gap-state-vector"
                                            : "state-vector"));
                                }
                                else if (TryGetMapOrbitKey(toCreate[i].source, toCreate[i].segment, out var orbitKey))
                                {
                                    flightSoiGapStateVectorExpectedBodies.Remove(idx);
                                    flightLastMapOrbitByIndex[idx] = orbitKey;

                                    ParsekLog.Info("Policy",
                                        $"Created deferred ghost map vessel for #{idx} \"{traj.VesselName}\" " +
                                        $"— source={toCreate[i].source} body={orbitKey.body} sma={orbitKey.sma:F0}");
                                }
                            }
                        }
                    }
                }
            }
        }

        // Pass 2 — segment-based map-orbit reseed for existing ProtoVessels. Runs only after the
        // orchestrator's gate + preamble passed (committed already resolved, non-null, and the
        // terminal-retention log keys already pruned). Body relocated verbatim from the former
        // "2a. Segment-based orbit updates" pass.
        private static void RunFlightMapOrbitReseedPass(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits, IReadOnlyList<Recording> committed)
        {
            // 2a. Segment-based orbit updates (existing)
            List<KeyValuePair<int, (string body, double sma, double ecc)>> orbitUpdates = null;
            List<int> toRemoveFromMap = null;
            List<(int idx, string expectedBody)> toRequeue = null;

            foreach (var kvp in flightLastMapOrbitByIndex)
            {
                int idx = kvp.Key;
                if (idx < 0 || idx >= committed.Count) continue;

                var rec = committed[idx];
                if (!rec.HasOrbitSegments) continue;

                // Slice (i): overlap recordings are reseeded by the per-instance sweep, not here.
                if (ShouldDriveOverlapPerInstance(rec, idx, committed, loopUnits))
                    continue;

                // Loop-mapped sample UT + live-frame epoch shift (effUT == currentUT, shift 0 off
                // the loop path). renderHidden => the member left its loop window this cycle: tear
                // the orbit ghost down, mirroring the Tracking Station refresh pass.
                double effUT = ResolveMapPresenceSampleUT(
                    idx, rec.StartUT, rec.EndUT, currentUT, loopUnits,
                    out bool renderHidden, out double loopEpochShiftSeconds);
                if (renderHidden)
                {
                    // Enrich the teardown reason with the loop role: a descent member tearing down here is the
                    // descent-revert/finish path (the icon falls back to the loiter conic), so the destroy line
                    // names it for correlation with the [ReaimDescent] DESCENT REVERTED/SKIPPED line. Empty (=>
                    // byte-identical reason) for every non-descent member.
                    GhostMapPresence.RemoveGhostVesselForRecording(idx,
                        "mission-loop-out-of-window"
                        + GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(idx, loopUnits));
                    if (toRemoveFromMap == null) toRemoveFromMap = new List<int>();
                    toRemoveFromMap.Add(idx);
                    // Re-queue to pendingMapVessels so the create pass re-materializes the orbit
                    // ghost when the span clock sweeps back into this member's window next cycle.
                    // Unlike the Tracking Station (which scans all committed indices every frame),
                    // the flight driver only creates from the pending queue, and the ProtoVessel
                    // was just removed, so 2a alone could never bring it back.
                    if (toRequeue == null) toRequeue = new List<(int, string)>();
                    toRequeue.Add((idx, null));
                    continue;
                }

                // Re-aim: for a re-aim loop owner, the flight map orbit line must follow the per-window
                // RE-AIMED transfer (aimed at the target's CURRENT position), not the recorded geometry
                // (which points at the target's RECORDED position - the "wrong place in the target's
                // orbit" the playtest showed). Resolved ONCE here from the LIVE currentUT (same window
                // the flight engine + tracking station use) and threaded through every effUT-based read
                // below; reference-identical to rec.OrbitSegments for every non-re-aim member.
                List<OrbitSegment> effectiveSegments = GhostMapPresence.ResolveEffectiveMapOrbitSegments(
                    idx, rec.RecordingId, rec.OrbitSegments, currentUT, loopUnits);

                // LOITER-GAP map-presence clamp (re-aim looped LANDING destination loiter): the descent-trigger
                // TRANSFER member's recorded loop clock (effUT) sweeps the parking conic up to its end
                // (ParkingConicEndUT = the destination loiter run end = the deorbit point). PAST that point an
                // UNCLAMPED FindOrbitSegmentOrSameBodyCarry walks INTO the CONTIGUOUS deorbit-transition
                // OrbitSegment and draws that deorbit arc as the loiter orbit (~1/3 of an ellipse), then past the
                // deorbit-arc end no segment covers effUT and the proto is destroyed ("left-orbit-segments"), so
                // the parking conic stops rendering until the descent fires. HOLD the segment-lookup sample UT at
                // ParkingConicEndUT inside the gap so the lookup keeps returning the real recorded PARKING-conic
                // segment and the proto stays alive on it. The clamp applies ONLY to this segment lookup; every
                // other read below still uses the live effUT. Layer B then stamps a per-pid line hold so
                // GhostOrbitLinePatch keeps the FULL parking ellipse drawn until the live descent trigger. False
                // (byte-identical) for every member except the destination transfer member (the owner, every
                // ride-along in a different/unshifted frame, and every descent-set member), for non-re-aim units,
                // and once the descent trigger fires (the transfer member then retires via the live-clock
                // continuation gate).
                double segmentLookupUT = effUT;
                uint flightGhostPid = vesselsByRecordingIndex.TryGetValue(idx, out Vessel flightGhostVessel)
                        && flightGhostVessel != null
                    ? flightGhostVessel.persistentId
                    : 0u;
                if (GhostPlaybackLogic.IsDescentTransferMemberInLoiterGap(loopUnits, idx, effUT))
                {
                    segmentLookupUT = GhostPlaybackLogic.ResolveLoiterGapConicEndUT(loopUnits, idx);
                    ParsekLog.VerboseRateLimited("Policy",
                        "flight-loiter-gap-clamp-" + idx,
                        string.Format(CultureInfo.InvariantCulture,
                            "Flight loiter-gap clamp: member={0} effUT={1:F1} held segment-lookup UT at parkingConicEnd={2:F1} "
                            + "(re-aim descent transfer member in captureShift loiter gap; parking conic kept rendering)",
                            idx, effUT, segmentLookupUT),
                        5.0);
                    // Layer B: hold the parking-conic LINE visible through the loiter (past the live parking-conic
                    // bound) until the live descent trigger; GhostOrbitLinePatch consults the stamp.
                    StampParkingConicLineHoldForLoiterGap(
                        idx, flightGhostPid, currentUT, loopUnits,
                        rec.StartUT, rec.EndUT, "FLIGHT", "flight-parking-conic-line-hold-");
                }
                else if (flightGhostPid != 0)
                {
                    // Not in the gap (still on the conic, or the descent fired / loop wrapped): tear down any
                    // line hold for this ghost so the stamp never lingers into the next phase.
                    ClearParkingConicLineHold(flightGhostPid);
                }

                // Same-body carry: while the playback head is inside a body frame, briefly
                // dropping the ghost between two non-orbit-equivalent segments (e.g., capture
                // burn between two Mun orbits) would tear down and recreate the ProtoVessel,
                // producing the visible flicker the user reported near Mun SOI. We only want
                // to drop the ghost when the body actually changes (SOI / frame change) or
                // when the recording is truly past its last segment. Body-frame carry keeps
                // the previous segment's orbit active until UT enters the next segment.
                OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentOrSameBodyCarry(effectiveSegments, segmentLookupUT);

                // Gap-points glide: FindOrbitSegmentOrSameBodyCarry keeps seg.HasValue true across a
                // same-body inter-segment gap by carrying the PREVIOUS segment forward. That is correct
                // for an equivalent-orbit gap (capture burn between two same Mun orbits) but wrong for a
                // real orbit RAISE (parking -> higher loiter, sma 671928 -> 731230 across a ~205s arc):
                // the stale parking orbit freezes the icon, then it teleports ~1318 km onto the loiter
                // orbit when UT enters the next segment. The recorder captured that raise as body-fixed
                // Absolute trajectory POINTS, so route the icon onto those points and glide it across the
                // gap. Gated to the NON-orbit-equivalent gap so the equivalent-orbit carry stays untouched.
                bool driveGapFromPoints =
                    seg.HasValue
                    && GhostMapPresence.ShouldDriveGapFromPoints(effectiveSegments, rec, effUT);

                // Flight-map companion to the TS covering-segment log: same MapTraj on-change diagnostic so the
                // SOI-exit blink / orbit-line GAP / body flip-flop is visible on the flight path too. When a
                // segment covers effUT the icon follows that OrbitSegment (source=Segment); when none does and
                // a terminal state-vector fallback is cached for this member, the icon is driven by that
                // fallback (resolved in the !seg.HasValue branch below), so report source=StateVector. The
                // gap-points glide above also drives the icon from the state-vector point path, so report
                // source=StateVector for it too. The membership check is stable frame-to-frame (it does not
                // flip-flop) and mirrors the TS path's trackingStationStateVectorOrbitTrajectories check.
                bool flightIsStateVector =
                    driveGapFromPoints
                    || (!seg.HasValue && flightStateVectorCachedIndices.ContainsKey(idx));
                GhostMapPresence.LogMapCoveringSegmentChange("FLIGHT", idx, effUT, seg, seg.HasValue && !driveGapFromPoints,
                    flightIsStateVector,
                    effectiveSegments != null ? effectiveSegments.Count : 0);

                // Gap-points glide branch: drive the existing (Segment-created) ghost from the recorded
                // body-fixed point at effUT via the same state-vector positioner the SOI-gap fallback uses.
                // UpdateGhostOrbitFromStateVectors clears the stale segment-drive dicts (bounds / loop-shift /
                // epoch-shift) so GhostOrbitIconDrivePatch defers to stock at live UT instead of clamping the
                // icon past-window on the parking orbit. continue skips the segment-apply tail below.
                if (driveGapFromPoints)
                {
                    int gapCachedIndex = flightStateVectorCachedIndices.TryGetValue(idx, out int gapCached)
                        ? gapCached
                        : -1;
                    TrajectoryPoint? gapPoint = TrajectoryMath.BracketPointAtUT(rec.Points, effUT, ref gapCachedIndex);
                    flightStateVectorCachedIndices[idx] = gapCachedIndex;
                    if (gapPoint.HasValue)
                    {
                        // UpdateGhostOrbitFromStateVectors returns true ONLY in the active-re-fly
                        // suppression case; false on the normal success path. The flight scene removes the
                        // ghost via its own re-fly suppression machinery (MarkStateVectorGhostDeferredForActiveReFly
                        // runs inside the call), so we just record the result for the log here.
                        bool reFlySuppressed = GhostMapPresence.UpdateGhostOrbitFromStateVectors(
                            idx, rec, gapPoint.Value, effUT,
                            stateVectorUpdateReason: "orbit-raise-gap-points",
                            loopEpochShiftSeconds: loopEpochShiftSeconds);
                        ParsekLog.VerboseRateLimited("Policy",
                            "flight-gap-points-glide-" + idx,
                            string.Format(CultureInfo.InvariantCulture,
                                "Flight gap-points glide: member={0} effUT={1:F1} body={2} alt={3:F0} reFlySuppressed={4} " +
                                "(orbit-raise gap, icon glides recorded ascent instead of segment carry)",
                                idx, effUT, gapPoint.Value.bodyName ?? "(null)", gapPoint.Value.altitude, reFlySuppressed),
                            2.0);
                        // The ghost is now state-vector-driven; record a sentinel orbit key (sma/ecc 0)
                        // via the deferred orbitUpdates list (we cannot mutate lastMapOrbitByIndex during
                        // its own enumeration) so the next in-segment frame sees a differing key and
                        // re-applies a fresh covering orbit instead of skip-unchanged.
                        if (orbitUpdates == null) orbitUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                        orbitUpdates.Add(new KeyValuePair<int, (string, double, double)>(
                            idx, (gapPoint.Value.bodyName, 0.0, 0.0)));
                        continue;
                    }
                    // No bracketing point (effUT precedes first / past last recorded point): fall through
                    // to the unchanged carry/segment-apply path. Never force a single stale point.
                    ParsekLog.VerboseRateLimited("Policy",
                        "flight-gap-points-nobracket-" + idx,
                        string.Format(CultureInfo.InvariantCulture,
                            "Flight gap-points glide skipped (no bracketing point): member={0} effUT={1:F1} " +
                            "(falling through to same-body segment carry)",
                            idx, effUT),
                        5.0);
                }

                // No map-visible orbit at current UT — either we've truly left orbital
                // playback, or the next segment is in a different SOI/body.
                if (!seg.HasValue)
                {
                    int cachedStateVectorIndex = flightStateVectorCachedIndices.TryGetValue(idx, out int cached)
                        ? cached
                        : -1;
                    if (ParsekPlaybackPolicy.TryResolveTerminalFallbackMapOrbitUpdate(
                        rec,
                        idx,
                        effUT,
                        loopEpochShiftSeconds,
                        kvp.Value,
                        IsMaterializedForMapPresence(rec),
                        ref cachedStateVectorIndex,
                        out OrbitSegment fallbackSegment,
                        out var fallbackKey,
                        out bool fallbackChanged))
                    {
                        flightStateVectorCachedIndices[idx] = cachedStateVectorIndex;
                        if (fallbackChanged)
                        {
                            GhostMapPresence.UpdateGhostOrbitForRecording(
                                idx, fallbackSegment,
                                loopEpochShiftSeconds: loopEpochShiftSeconds);
                            if (orbitUpdates == null) orbitUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                            orbitUpdates.Add(new KeyValuePair<int, (string, double, double)>(idx, fallbackKey));
                        }
                        continue;
                    }
                    flightStateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    bool hasFutureSegment = false;
                    string futureSegmentBody = null;
                    var segs = effectiveSegments;
                    for (int s = 0; s < segs.Count; s++)
                    {
                        if (segs[s].startUT > effUT)
                        {
                            hasFutureSegment = true;
                            futureSegmentBody = segs[s].bodyName;
                            break;
                        }
                    }

                    if (ParsekPlaybackPolicy.ShouldRetainMapPresenceForTerminalRealSpawn(rec, hasFutureSegment))
                    {
                        string retentionKey = rec.RecordingId ?? idx.ToString(CultureInfo.InvariantCulture);
                        string reason = rec.TerminalSpawnCannotSpawnSafely
                            ? rec.TerminalSpawnSafetyReasonCode ?? "cannot-spawn-safely"
                            : rec.TerminalSpawnSafetyDeferred
                                ? rec.TerminalSpawnSafetyReasonCode ?? "deferred"
                                : "pending-terminal-real-spawn";
                        string message = string.Format(CultureInfo.InvariantCulture,
                            "Map presence retained because terminal real spawn is pending/deferred: " +
                            "rec={0} idx={1} vessel=\"{2}\" currentUT={3:F2} reason={4}",
                            rec.RecordingId ?? "(null)",
                            idx,
                            rec.VesselName ?? "(null)",
                            currentUT,
                            reason);
                        if (flightTerminalMapRetentionLoggedIds.Add(retentionKey))
                            ParsekLog.Info("Policy", message);
                        else
                            ParsekLog.VerboseRateLimited(
                                "Policy",
                                "terminal-map-retained-" + retentionKey,
                                message);
                        continue;
                    }

                    GhostMapPresence.RemoveGhostVesselForRecording(idx,
                        hasFutureSegment ? "gap-between-orbit-segments" : "left-orbit-segments");

                    if (hasFutureSegment)
                    {
                        // Re-add to pending so the next segment creates a new ProtoVessel
                        if (toRequeue == null) toRequeue = new List<(int, string)>();
                        toRequeue.Add((idx, futureSegmentBody));
                    }

                    if (toRemoveFromMap == null) toRemoveFromMap = new List<int>();
                    toRemoveFromMap.Add(idx);
                    continue;
                }

                // Skip re-applying an unchanged orbit — EXCEPT for loop members, whose stored key
                // (body/sma/ecc) does not capture the per-cycle epoch + bounds shift, so a cycle
                // wrap onto the same-shape segment would otherwise leave a stale shift and mis-clip
                // the arc. Loop members re-apply every tick (cheap; the shift is constant within a
                // cycle so this is idempotent until the wrap).
                if (loopEpochShiftSeconds == 0.0
                    && seg.Value.bodyName == kvp.Value.body
                    && seg.Value.semiMajorAxis == kvp.Value.sma
                    && seg.Value.eccentricity == kvp.Value.ecc)
                    continue;

                GhostMapPresence.UpdateGhostOrbitForRecording(
                    idx, seg.Value,
                    loopEpochShiftSeconds: loopEpochShiftSeconds,
                    effectiveOrbitSegments: effectiveSegments);
                if (orbitUpdates == null) orbitUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                orbitUpdates.Add(new KeyValuePair<int, (string, double, double)>(
                    idx, (seg.Value.bodyName, seg.Value.semiMajorAxis, seg.Value.eccentricity)));
            }

            if (orbitUpdates != null)
            {
                for (int i = 0; i < orbitUpdates.Count; i++)
                    flightLastMapOrbitByIndex[orbitUpdates[i].Key] = orbitUpdates[i].Value;
            }
            if (toRemoveFromMap != null)
            {
                for (int i = 0; i < toRemoveFromMap.Count; i++)
                    flightLastMapOrbitByIndex.Remove(toRemoveFromMap[i]);
            }
            if (toRequeue != null)
            {
                for (int i = 0; i < toRequeue.Count; i++)
                {
                    int idx = toRequeue[i].idx;
                    if (!flightPendingMapVessels.ContainsKey(idx) && idx < committed.Count)
                    {
                        var traj = committed[idx] as IPlaybackTrajectory;
                        if (traj != null)
                        {
                            flightPendingMapVessels[idx] = new PendingMapVessel(
                                traj,
                                allowSoiGapStateVectorFallback: true,
                                expectedSoiGapBody: toRequeue[i].expectedBody);
                            ParsekLog.Verbose("MapPresence",
                                $"Re-queued recording #{idx} to pendingMapVessels (gap between orbit segments expectedBody={toRequeue[i].expectedBody ?? "(none)"})");
                        }
                    }
                }
            }
        }

        // Pass 3 — state-vector orbit updates (physics-only suborbital) for existing ProtoVessels.
        // Runs only after the orchestrator's gate + preamble passed (committed already resolved and
        // non-null). Body relocated verbatim from the former "2b. State-vector orbit updates" pass,
        // including the EmitLifecycleSummary tail.
        private static void RunFlightMapStateVectorUpdatePass(
            double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits, IReadOnlyList<Recording> committed)
        {
            // 2b. State-vector orbit updates (new — physics-only suborbital)
            if (flightStateVectorOrbitTrajectories.Count > 0)
            {
                List<int> toReDefer = null;
                List<int> toExitStateVector = null;
                List<KeyValuePair<int, (string body, double sma, double ecc)>> stateVectorSegmentUpdates = null;

                foreach (var kvp in flightStateVectorOrbitTrajectories)
                {
                    int idx = kvp.Key;
                    IPlaybackTrajectory traj = kvp.Value;

                    // Slice (i): overlap recordings are driven by the per-instance sweep, not here.
                    if (committed != null && idx >= 0 && idx < committed.Count
                        && ShouldDriveOverlapPerInstance(committed[idx], idx, committed, loopUnits))
                        continue;

                    if (!flightStateVectorCachedIndices.ContainsKey(idx))
                        flightStateVectorCachedIndices[idx] = -1;
                    int cached = flightStateVectorCachedIndices[idx];

                    // Loop-mapped sample UT + live-frame epoch shift (effUT == currentUT, shift 0
                    // off the loop path). renderHidden => the member is outside its loop window
                    // this cycle: tear the ghost down and re-defer so the create pass re-materializes
                    // it when the span clock sweeps back into its window.
                    double effUT = ResolveMapPresenceSampleUT(
                        idx, traj.StartUT, traj.EndUT, currentUT, loopUnits,
                        out bool renderHidden, out double loopEpochShiftSeconds);
                    if (renderHidden)
                    {
                        // Enrich the teardown reason with the loop role (descent member => the descent
                        // revert/finish path) so the destroy line correlates with the [ReaimDescent] line.
                        // Empty (byte-identical reason) for every non-descent member.
                        GhostMapPresence.RemoveGhostVesselForRecording(idx,
                            "mission-loop-out-of-window"
                            + GhostPlaybackLogic.DescribeLoopMemberRoleForTeardown(idx, loopUnits));
                        if (toReDefer == null) toReDefer = new List<int>();
                        toReDefer.Add(idx);
                        continue;
                    }

                    if (flightSoiGapStateVectorExpectedBodies.TryGetValue(idx, out string expectedSoiGapBody))
                    {
                        // Loop-aware caller (state-vector orbit update pass): plan section 1.4.
                        bool acceptTerminalOrbitForLoopSynthesis =
                            loopEpochShiftSeconds != 0.0
                            && GhostMapPresence.IsTerminalOrbitSynthesisSafeForLoopMember(traj);
                        TrackingStationGhostSource source = GhostMapPresence.ResolveMapPresenceGhostSource(
                            traj,
                            false,
                            IsMaterializedForMapPresence(traj),
                            effUT,
                            true,
                            "map-presence-soi-gap-state-vector-update",
                            ref cached,
                            out OrbitSegment segment,
                            out TrajectoryPoint soiGapPoint,
                            out _,
                            recordingIndex: idx,
                            allowSoiGapStateVectorFallback: true,
                            expectedSoiGapBody: expectedSoiGapBody,
                            acceptTerminalOrbitForLoopSynthesis: acceptTerminalOrbitForLoopSynthesis,
                            // Keep updating a loop member's ghost alongside any persisted real
                            // terminal vessel (mirrors the create-path bypass).
                            loopMemberInWindow: loopEpochShiftSeconds != 0.0);
                        flightStateVectorCachedIndices[idx] = cached;

                        if (source == TrackingStationGhostSource.StateVectorSoiGap)
                        {
                            if (GhostMapPresence.UpdateGhostOrbitFromStateVectors(
                                idx,
                                traj,
                                soiGapPoint,
                                effUT,
                                allowOrbitalCheckpointStateVector: true,
                                stateVectorUpdateReason: "soi-gap-state-vector-fallback",
                                loopEpochShiftSeconds: loopEpochShiftSeconds))
                            {
                                GhostMapPresence.RemoveGhostVesselForRecording(
                                    idx,
                                    GhostMapPresence.TrackingStationGhostSkipActiveReFlyRelativeUpdate);
                                if (toReDefer == null) toReDefer = new List<int>();
                                toReDefer.Add(idx);
                            }
                            continue;
                        }

                        if (GhostMapPresence.IsSegmentBearingGhostSource(source))
                        {
                            // EndpointTail populates a valid `segment` out-param exactly
                            // like TerminalOrbit (the loop-synthesis no-segment fallback),
                            // so consume it here too; otherwise a terminal-region loop
                            // member resolving to EndpointTail would fall through to the
                            // flat BracketPointAtUT path below. Matches the pending-create
                            // (line ~1387) and orbit-update (line ~1922) callers.
                            GhostMapPresence.UpdateGhostOrbitForRecording(
                                idx, segment,
                                loopEpochShiftSeconds: loopEpochShiftSeconds);
                            if (TryGetMapOrbitKey(source, segment, out var segmentKey))
                            {
                                if (stateVectorSegmentUpdates == null)
                                    stateVectorSegmentUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                                stateVectorSegmentUpdates.Add(new KeyValuePair<int, (string, double, double)>(idx, segmentKey));
                            }

                            if (toExitStateVector == null) toExitStateVector = new List<int>();
                            toExitStateVector.Add(idx);
                            continue;
                        }
                    }

                    TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(traj.Points, effUT, ref cached);
                    flightStateVectorCachedIndices[idx] = cached;

                    if (!pt.HasValue) continue;

                    // Relative-frame points reuse `TrajectoryPoint.altitude` as the
                    // anchor-local dz offset (metres along the anchor's local z axis),
                    // not as geographic altitude. Feeding the dz into the
                    // `ShouldRemoveStateVectorOrbit` altitude threshold trips the
                    // remove path on every typical rendezvous frame (dz ~ 0) and
                    // re-defers the ghost — but pending-create currently skips
                    // Relative frames, so the ghost disappears through the section
                    // rather than staying attached to the anchor (#547 P1 review).
                    // Skip the altitude floor for Relative-frame points (the speed
                    // floor stays, see ShouldRemoveStateVectorOrbitForFrame);
                    // `UpdateGhostOrbitFromStateVectors` already dispatches on
                    // `referenceFrame` and resolves the world position via
                    // anchor + offset for that branch.
                    bool inRelativeFrame = GhostMapPresence.IsInRelativeFrame(traj, effUT);
                    // Same laziness as the tracking-station pass: no FlightGlobals read
                    // for a Relative-frame point.
                    double atmosRemove = inRelativeFrame
                        ? 0.0
                        : GhostMapPresence.GetAtmosphereDepth(pt.Value.bodyName);
                    if (GhostMapPresence.ShouldRemoveStateVectorOrbitForFrame(
                        inRelativeFrame, pt.Value.altitude, pt.Value.velocity.magnitude, atmosRemove))
                    {
                        GhostMapPresence.RemoveGhostVesselForRecording(idx, "below-state-vector-threshold");
                        if (toReDefer == null) toReDefer = new List<int>();
                        toReDefer.Add(idx);
                        ParsekLog.Info("Policy", string.Format(CultureInfo.InvariantCulture,
                            "Removed state-vector ghost map vessel for #{0} — alt={1:F0} speed={2:F1} below threshold",
                            idx, pt.Value.altitude, pt.Value.velocity.magnitude));
                        continue;
                    }

                    if (GhostMapPresence.UpdateGhostOrbitFromStateVectors(idx, traj, pt.Value, effUT,
                        loopEpochShiftSeconds: loopEpochShiftSeconds))
                    {
                        GhostMapPresence.RemoveGhostVesselForRecording(
                            idx,
                            GhostMapPresence.TrackingStationGhostSkipActiveReFlyRelativeUpdate);
                        if (toReDefer == null) toReDefer = new List<int>();
                        toReDefer.Add(idx);
                    }
                }

                if (toReDefer != null)
                {
                    for (int i = 0; i < toReDefer.Count; i++)
                    {
                        int idx = toReDefer[i];
                        if (flightStateVectorOrbitTrajectories.TryGetValue(idx, out var traj))
                            flightPendingMapVessels[idx] = new PendingMapVessel(
                                traj,
                                allowSoiGapStateVectorFallback: false,
                                expectedSoiGapBody: null);
                        flightStateVectorOrbitTrajectories.Remove(idx);
                        flightSoiGapStateVectorExpectedBodies.Remove(idx);
                        // stateVectorCachedIndices[idx] intentionally kept — avoids
                        // O(n) re-scan if the ghost re-ascends above threshold.
                    }
                }

                if (stateVectorSegmentUpdates != null)
                {
                    for (int i = 0; i < stateVectorSegmentUpdates.Count; i++)
                        flightLastMapOrbitByIndex[stateVectorSegmentUpdates[i].Key] = stateVectorSegmentUpdates[i].Value;
                }

                if (toExitStateVector != null)
                {
                    for (int i = 0; i < toExitStateVector.Count; i++)
                    {
                        int idx = toExitStateVector[i];
                        flightStateVectorOrbitTrajectories.Remove(idx);
                        flightSoiGapStateVectorExpectedBodies.Remove(idx);
                    }
                }
            }

            GhostMapPresence.EmitLifecycleSummary("flight-map-presence", currentUT);
        }

        private static void PruneTerminalMapRetentionLogKeys(IReadOnlyList<Recording> committed)
        {
            if (flightTerminalMapRetentionLoggedIds.Count == 0)
                return;

            if (committed == null || committed.Count == 0)
            {
                flightTerminalMapRetentionLoggedIds.Clear();
                return;
            }

            List<string> staleKeys = null;
            foreach (string key in flightTerminalMapRetentionLoggedIds)
            {
                bool found = false;
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null)
                        continue;

                    if (string.Equals(rec.RecordingId, key, StringComparison.Ordinal)
                        || (string.IsNullOrEmpty(rec.RecordingId)
                            && key == i.ToString(CultureInfo.InvariantCulture)))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    if (staleKeys == null) staleKeys = new List<string>();
                    staleKeys.Add(key);
                }
            }

            if (staleKeys == null)
                return;

            for (int i = 0; i < staleKeys.Count; i++)
                flightTerminalMapRetentionLoggedIds.Remove(staleKeys[i]);
        }

        internal static bool TryGetMapOrbitKey(
            TrackingStationGhostSource source,
            OrbitSegment segment,
            out (string body, double sma, double ecc) orbitKey)
        {
            if (source == TrackingStationGhostSource.Segment
                || source == TrackingStationGhostSource.TerminalOrbit)
            {
                orbitKey = (segment.bodyName, segment.semiMajorAxis, segment.eccentricity);
                return true;
            }

            orbitKey = default((string, double, double));
            return false;
        }

        internal static bool IsMaterializedForMapPresence(IPlaybackTrajectory traj)
        {
            var rec = traj as Recording;
            if (rec == null)
                return false;

            bool realVesselExists = false;
            if (rec.VesselPersistentId != 0)
            {
                try
                {
                    // Launch-Guid gated (FindLaunchMatchedVesselForRecording, not the bare pid
                    // lookup): "materialized" must mean THIS recording's vessel is in the world.
                    // A preserved relaunch of the same craft carries the same craft-baked pid,
                    // and counting it here would retire the recording's map presence while its
                    // own vessel is nowhere in the scene.
                    realVesselExists =
                        FlightRecorder.FindLaunchMatchedVesselForRecording(rec) != null;
                }
                catch (Exception)
                {
                    realVesselExists = false;
                }
            }

            return GhostMapPresence.IsTrackingStationRecordingMaterialized(rec, realVesselExists);
        }

        /// <summary>
        /// If the recording at the given index belongs to a chain and another segment
        /// from the same chain currently owns a ghost map vessel, remove the old one.
        /// Updates chainMapOwner to track the new owner.
        /// </summary>
        internal static void RemovePreviousChainMapVessel(int newIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || newIndex < 0 || newIndex >= committed.Count) return;

            string chainId = committed[newIndex].ChainId;
            if (string.IsNullOrEmpty(chainId)) return;

            if (flightChainMapOwner.TryGetValue(chainId, out int oldIndex) && oldIndex != newIndex)
            {
                GhostMapPresence.RemoveGhostVesselForRecording(oldIndex, "chain-segment-replaced");
                flightLastMapOrbitByIndex.Remove(oldIndex);
                ParsekLog.Verbose("Policy",
                    $"Chain dedup: removed ghost map for #{oldIndex} (replaced by #{newIndex} in chain {chainId})");
            }

            flightChainMapOwner[chainId] = newIndex;
        }

        // ===================================================================================
        // Flight-scene ghost-create / ghost-destroy MAP-PRESENCE portions (Phase 8d.2 relocation
        // from ParsekPlaybackPolicy.HandleGhostCreated / HandleGhostDestroyed). FAITHFUL MOVE —
        // the blocks below were copied VERBATIM from those two handlers. The only edits are:
        // (a) engine.CurrentLoopUnits -> the loopUnits parameter (HandleFlightGhostCreatedMapPresence),
        // (b) the ShouldDeferLoopShiftedMapPresence predicate (left in ParsekPlaybackPolicy — it has
        // test callers there) is qualified with ParsekPlaybackPolicy., and (c) now-redundant
        // GhostMapPresence. self-qualifiers on members that are in-class here were stripped. The
        // policy keeps the engine-event subscription wiring + the non-presence concerns
        // (TryAutoFollowChainSeamSpawn camera follow, heldGhosts soft-cap state).
        // ===================================================================================

        /// <summary>
        /// Map-presence ENQUEUE portion of <c>ParsekPlaybackPolicy.HandleGhostCreated</c> (Phase 8d.2
        /// relocation): debris-skip gate, terminal-eligibility / orbit-data gates, per-chain dedup,
        /// initial source resolution, the loop-aware defer decision, and the immediate-create vs
        /// pending-queue branch. The policy's handler runs <c>TryAutoFollowChainSeamSpawn(evt)</c>
        /// (camera concern) first, then calls this. <paramref name="loopUnits"/> is the engine's
        /// per-frame <c>CurrentLoopUnits</c> the policy reads at the call site.
        /// </summary>
        internal static void HandleFlightGhostCreatedMapPresence(
            GhostLifecycleEvent evt, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            // KEEP debris-only: this is the policy decision to NOT give debris
            // recordings their own tracking-station map presence / orbit lines.
            // Controlled-decoupled children (extension of the parent-anchor
            // contract) carry IsDebris=false and correctly pass this gate so
            // they continue to receive map presence as today.
            if (evt.Trajectory == null || evt.Trajectory.IsDebris)
            {
                if (evt.Trajectory?.IsDebris == true)
                    ParsekLog.VerboseRateLimited("Policy", $"skip-map-debris-{evt.Index}",
                        $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory?.VesselName}\" - debris",
                        3.0);
                return;
            }

            var terminal = evt.Trajectory.TerminalStateValue;
            if (!IsTerminalStateEligibleForMapPresence(terminal))
            {
                ParsekLog.VerboseRateLimited("Policy", $"skip-map-terminal-{evt.Index}",
                    $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory.VesselName}\" " +
                    $"rec={evt.Trajectory.RecordingId ?? "(none)"} - terminal={terminal.Value}");
                return;
            }

            // Accept recordings with terminal orbit data, orbit segments, or trajectory
            // points (physics-only suborbital recordings have points but no orbit data).
            bool hasOrbitData = HasOrbitData(evt.Trajectory);
            bool hasOrbitSegments = evt.Trajectory.HasOrbitSegments;
            bool hasPoints = evt.Trajectory.Points != null && evt.Trajectory.Points.Count > 0;
            if (!hasOrbitData && !hasOrbitSegments && !hasPoints)
                return;

            // Slice (i): overlap recordings (looped, period < duration) under the director-drive gate
            // are owned solely by the per-frame per-instance sweep (RunOverlapPerInstanceSweep) which
            // creates ONE map vessel per live cycle. Do NOT enqueue/create a single per-index ghost
            // for them here. Off the gate (or for non-overlap recordings) this is byte-identical to
            // before: the per-instance path is unreachable and the legacy enqueue runs.
            {
                var committedForOverlap = RecordingStore.CommittedRecordings;
                if (committedForOverlap != null && evt.Index >= 0 && evt.Index < committedForOverlap.Count
                    && ShouldDriveOverlapPerInstance(committedForOverlap[evt.Index], evt.Index, committedForOverlap, loopUnits))
                {
                    ParsekLog.VerboseRateLimited("Policy", $"defer-overlap-sweep-{evt.Index}",
                        string.Format(CultureInfo.InvariantCulture,
                            "Ghost map for #{0} \"{1}\" deferred to per-instance overlap sweep " +
                            "(looped overlap recording / Mission-unit overlap member, director-drive on)",
                            evt.Index, evt.Trajectory.VesselName ?? "(null)"),
                        3.0);
                    return;
                }
            }

            // Per-chain dedup: if another segment from the same chain already has a ghost
            // map vessel, remove it before creating the new one. Prevents duplicate orbit
            // lines during fast time warp across chain boundaries.
            RemovePreviousChainMapVessel(evt.Index);

            // Check whether the ghost starts in a map-visible source. Otherwise,
            // defer — the per-frame shared resolver will create it when it enters
            // an orbital segment or state-vector fallback range.
            double startUT = evt.Trajectory.StartUT;
            int cachedStateVectorIndex = flightStateVectorCachedIndices.TryGetValue(evt.Index, out int cached)
                ? cached
                : -1;
            // Initial-create-on-first-loop-entry: pass acceptTerminalOrbitForLoopSynthesis:
            // false. This path runs at startUT with no loopUnits / effUT / shift in scope, so
            // the relaxed terminal-orbit source would seed at the raw recorded UT (wrong
            // position). The proto-vessel is created later on a loop-aware tick once the
            // pending queue and per-frame ResolveMapPresenceSampleUT compute effUT and the
            // accompanying loop epoch shift. Same reasoning as the TS-startup wrapper. Plan section 1.4.
            TrackingStationGhostSource source = ResolveMapPresenceGhostSource(
                evt.Trajectory,
                false,
                IsMaterializedForMapPresence(evt.Trajectory),
                startUT,
                true,
                "map-presence-initial-create",
                ref cachedStateVectorIndex,
                out OrbitSegment segment,
                out TrajectoryPoint stateVectorPoint,
                out _,
                recordingIndex: evt.Index,
                acceptTerminalOrbitForLoopSynthesis: false);
            flightStateVectorCachedIndices[evt.Index] = cachedStateVectorIndex;

            // Loop-shifted members must be created on a loop-aware per-frame tick
            // (CheckPendingMapVessels): that path resolves the source at the loop-mapped
            // effUT and threads the live-frame epoch shift into the orbit + arc-clip bounds.
            // Creating here at the raw recorded startUT with the default shift=0 seeds
            // recorded-UT bounds and leaves ghostOrbitLoopShiftedPids clear, so for one tick
            // the map-orbit window resolver returns a recorded-UT fallback window (the
            // icon-clamp / orbit-line glitch at first appearance and at every window re-entry).
            // Defer so the per-frame path owns loop-member creation, mirroring the TS create
            // sites and the flight pending-create path. Off the loop path effUT == currentUT
            // (shift 0, renderHidden false), so non-loop members keep the immediate create
            // byte-for-byte.
            double initialNowUT = Planetarium.GetUniversalTime();
            ResolveMapPresenceSampleUT(
                evt.Index, evt.Trajectory.StartUT, evt.Trajectory.EndUT, initialNowUT,
                loopUnits, out bool initialRenderHidden, out double initialLoopShift);
            bool deferForLoopAware =
                ParsekPlaybackPolicy.ShouldDeferLoopShiftedMapPresence(initialLoopShift, initialRenderHidden);

            if (source != TrackingStationGhostSource.None && !deferForLoopAware)
            {
                Vessel ghost = CreateGhostVesselFromSource(
                    evt.Index,
                    evt.Trajectory,
                    source,
                    segment,
                    stateVectorPoint,
                    startUT);
                if (ghost != null)
                {
                    if (IsStateVectorGhostSource(source))
                    {
                        flightStateVectorOrbitTrajectories[evt.Index] = evt.Trajectory;
                        if (source != TrackingStationGhostSource.StateVectorSoiGap)
                            flightSoiGapStateVectorExpectedBodies.Remove(evt.Index);
                    }
                    else if (TryGetMapOrbitKey(source, segment, out var orbitKey))
                    {
                        flightSoiGapStateVectorExpectedBodies.Remove(evt.Index);
                        flightLastMapOrbitByIndex[evt.Index] = orbitKey;
                    }
                }
            }
            else
            {
                // Initial/pre-orbital deferrals and loop-shifted members are not SOI-gap
                // recoveries; only the gap-between-orbit-segments requeue path opts into that
                // fallback. The per-frame CheckPendingMapVessels pass resolves the source at
                // the loop-mapped effUT and creates with the live-frame epoch shift.
                flightPendingMapVessels[evt.Index] = new PendingMapVessel(
                    evt.Trajectory,
                    allowSoiGapStateVectorFallback: false,
                    expectedSoiGapBody: null);
                ParsekLog.VerboseRateLimited("Policy", $"defer-map-{evt.Index}",
                    string.Format(CultureInfo.InvariantCulture,
                        "Deferred ghost map vessel for #{0} \"{1}\": {2} (source={3} loopShift={4:F2} renderHidden={5})",
                        evt.Index,
                        evt.Trajectory.VesselName,
                        deferForLoopAware
                            ? "loop-shifted member, deferring to loop-aware per-frame create"
                            : "recording starts pre-orbital",
                        source,
                        initialLoopShift,
                        initialRenderHidden),
                    3.0);
            }
        }

        internal static void HandleFlightGhostDestroyedMapPresence(int index)
        {
            var committed = RecordingStore.CommittedRecordings;
            uint vesselPid = 0;
            if (committed != null && index >= 0 && index < committed.Count)
                vesselPid = committed[index].VesselPersistentId;
            HandleFlightGhostDestroyedMapPresenceCore(index, vesselPid, "ghost-destroyed");
        }

        private static void HandleFlightGhostDestroyedMapPresenceCore(int index, uint vesselPid, string reason)
        {
            flightPendingMapVessels.Remove(index);
            flightLastMapOrbitByIndex.Remove(index);
            flightStateVectorOrbitTrajectories.Remove(index);
            flightSoiGapStateVectorExpectedBodies.Remove(index);
            flightStateVectorCachedIndices.Remove(index);

            // Remove both recording-index and chain-based ghost map ProtoVessels
            var committed = RecordingStore.CommittedRecordings;
            if (committed != null && index >= 0 && index < committed.Count
                && !string.IsNullOrEmpty(committed[index].RecordingId))
            {
                flightTerminalMapRetentionLoggedIds.Remove(committed[index].RecordingId);
            }

            RemoveAllGhostPresenceForIndex(index, vesselPid, reason);
        }
    }
}
