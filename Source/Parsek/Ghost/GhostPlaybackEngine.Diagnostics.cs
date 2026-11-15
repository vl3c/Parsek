using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Diagnostics: engine iteration trace, frame summaries and skip counters, cadence logs, observability counts, mesh / loop-cycle traces and ghost appearance tracking.
    internal partial class GhostPlaybackEngine
    {
        // Engine-iteration trace (closes observability gap left by GhostRenderTrace
        // gating on IsDetailedWindowOpen). Bypasses the gate so a future repro can
        // tell from a single log line whether a recording reached the per-trajectory
        // loop, what its producer-side skipReason was, whether anchorReFlyUnstable
        // was set (separate flag the engine reads later in the loop and the centre
        // of the H2 hypothesis), whether its trajectory had renderable data, and
        // whether ghostStates still holds an entry.
        internal static string FormatEngineIterEntry(
            int index,
            string recordingId,
            GhostPlaybackSkipReason skipReason,
            bool anchorReFlyUnstable,
            bool hasRenderableData,
            bool inGhostStates,
            double endUT)
        {
            string shortId = FormatRecordingIdShort(recordingId);
            string skip = skipReason == GhostPlaybackSkipReason.None
                ? "None"
                : skipReason.ToLogToken();
            return "[i=" + index.ToString(CultureInfo.InvariantCulture)
                + " rec=" + shortId
                + " skip=" + skip
                + " aru=" + (anchorReFlyUnstable ? "T" : "F")
                + " hd=" + (hasRenderableData ? "T" : "F")
                + " hs=" + (inGhostStates ? "T" : "F")
                + " endUT=" + endUT.ToString("F1", CultureInfo.InvariantCulture)
                + "]";
        }

        // Per-frame VISIBILITY OUTCOME token appended to each engine-iter entry,
        // read from the post-loop GhostPlaybackState. This is the signal that
        // closes the gap left by FormatEngineIterEntry (which captures only
        // pre-render inputs): for the "active=1 but invisible" ghost-vanish
        // symptom it tells, at a glance per frame, whether the ghost's mesh
        // ended the frame shown (vis=T) or hidden, and if hidden, WHY — the
        // relative-anchor retire path (retired=T) versus distance LOD
        // (zone=Beyond + rdist). hasGhost=false means no GhostPlaybackState
        // entry at all (the slot was skipped/destroyed/never spawned).
        // Pure/static (primitive args only) so the format is unit-pinnable
        // without a Unity GameObject.
        internal static string FormatEngineIterOutcome(
            bool hasGhost,
            bool meshVisible,
            bool anchorRetiredThisFrame,
            RenderingZone zone,
            double renderDistance)
        {
            if (!hasGhost)
                return "[out:none]";

            return "[out:vis=" + (meshVisible ? "T" : "F")
                + " retired=" + (anchorRetiredThisFrame ? "T" : "F")
                + " zone=" + zone.ToString()
                + " rdist=" + RenderingZoneManager.FormatDistanceForLog(renderDistance)
                + "]";
        }

        internal static string FormatRecordingIdShort(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return "<none>";
            return recordingId.Length > 8
                ? recordingId.Substring(0, 8)
                : recordingId;
        }

        internal static string FormatEngineIterMessage(string entries)
        {
            if (string.IsNullOrEmpty(entries))
                return EngineFrameIterMessagePrefix;
            return EngineFrameIterMessagePrefix + " " + entries;
        }

        internal static void EmitEngineIterTrace(string entries)
        {
            ParsekLog.VerboseRateLimited(
                "Engine",
                "engine-frame-iter",
                FormatEngineIterMessage(entries),
                1.0);
        }

        private void EmitEngineIterTraceForFrame(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            TrajectoryPlaybackFlags[] flags,
            bool engineIterTraceEnabled)
        {
            if (engineIterTraceEnabled)
            {
                var engineIterBuilder =
                    new System.Text.StringBuilder(trajectories.Count * 64);
                for (int i = 0; i < trajectories.Count; i++)
                {
                    var iterTraj = trajectories[i];
                    var iterFlags = flags[i];
                    if (engineIterBuilder.Length > 0)
                        engineIterBuilder.Append(',');
                    engineIterBuilder.Append(FormatEngineIterEntry(
                        index: i,
                        recordingId: iterTraj?.RecordingId,
                        skipReason: iterFlags.skipGhost
                            ? iterFlags.skipReason
                            : GhostPlaybackSkipReason.None,
                        anchorReFlyUnstable: iterFlags.anchorReFlyUnstable,
                        hasRenderableData: HasRenderableGhostData(iterTraj),
                        inGhostStates: ghostStates.ContainsKey(i),
                        endUT: iterTraj?.EndUT ?? double.NaN));

                    GhostPlaybackState iterState;
                    ghostStates.TryGetValue(i, out iterState);
                    bool hasGhost = iterState != null;
                    bool meshVisible = hasGhost
                        && iterState.ghost != null
                        && iterState.ghost.activeSelf;
                    engineIterBuilder.Append(FormatEngineIterOutcome(
                        hasGhost,
                        meshVisible,
                        anchorRetiredThisFrame: hasGhost && iterState.anchorRetiredThisFrame,
                        zone: hasGhost ? iterState.currentZone : RenderingZone.Physics,
                        renderDistance: hasGhost ? iterState.lastRenderDistance : double.NaN));
                }
                EmitEngineIterTrace(engineIterBuilder.ToString());
            }
        }

        /// <summary>
        /// Diagnostic-only lookup: walks <see cref="ghostStates"/> for a ghost
        /// whose stamped <c>recordingId</c> matches and returns its current
        /// <c>transform.position</c>. Used by the separation-event playback
        /// trace to log the rendered parent-vs-debris distance alongside the
        /// recorded-data-derived distance so a log reader can see whether
        /// playback diverges from recorded data. Returns <c>false</c> when no
        /// ghost in any state currently has visuals materialized for that
        /// recording (parent not yet spawned, retired, or never rendered in
        /// the current window). Overlap-cycle and watch-resume ghosts are
        /// ignored; only the primary state map is consulted, which matches
        /// the slide repro: at the moment a debris first renders, its parent
        /// is the live primary ghost.
        /// </summary>
        internal bool TryGetGhostWorldByRecordingId(string recordingId, out Vector3d world)
        {
            world = Vector3d.zero;
            if (string.IsNullOrWhiteSpace(recordingId))
                return false;
            foreach (var kv in ghostStates)
            {
                GhostPlaybackState s = kv.Value;
                if (s == null || s.ghost == null || s.ghost.transform == null)
                    continue;
                if (!string.Equals(s.recordingId, recordingId, System.StringComparison.Ordinal))
                    continue;
                world = (Vector3d)s.ghost.transform.position;
                return true;
            }
            return false;
        }

        private void CountFrameSkip(GhostPlaybackSkipReason reason)
        {
            switch (reason)
            {
                case GhostPlaybackSkipReason.NoRenderableData:
                    frameSkipNoRenderableData++;
                    break;
                case GhostPlaybackSkipReason.PlaybackDisabled:
                    frameSkipPlaybackDisabled++;
                    break;
                case GhostPlaybackSkipReason.ExternalVesselSuppressed:
                    frameSkipExternalVesselSuppressed++;
                    break;
                case GhostPlaybackSkipReason.BeforeActivation:
                    frameSkipBeforeActivation++;
                    break;
                case GhostPlaybackSkipReason.AnchorMissing:
                    frameSkipAnchorMissing++;
                    break;
                case GhostPlaybackSkipReason.LoopSyncFailed:
                    frameSkipLoopSyncFailed++;
                    break;
                case GhostPlaybackSkipReason.ParentLoopPaused:
                    frameSkipParentLoopPaused++;
                    break;
                case GhostPlaybackSkipReason.WarpHidden:
                    frameSkipWarpHidden++;
                    break;
                case GhostPlaybackSkipReason.VisualLoadFailed:
                    frameSkipVisualLoadFailed++;
                    break;
                case GhostPlaybackSkipReason.SessionSuppressed:
                    frameSkipSessionSuppressed++;
                    break;
                case GhostPlaybackSkipReason.SupersededByRelation:
                    frameSkipSupersededByRelation++;
                    break;
                case GhostPlaybackSkipReason.RewindRetired:
                    frameSkipRewindRetired++;
                    break;
                case GhostPlaybackSkipReason.SpawnSuppressedDeadOnArrival:
                    frameSkipSpawnSuppressedDeadOnArrival++;
                    break;
                case GhostPlaybackSkipReason.AnchorRotationUnreliable:
                    frameSkipAnchorRotationUnreliable++;
                    break;
                case GhostPlaybackSkipReason.AnchorReFlyUnstable:
                    frameSkipAnchorReFlyUnstable++;
                    break;
                case GhostPlaybackSkipReason.ChainShadowed:
                    frameSkipChainShadowed++;
                    break;
                case GhostPlaybackSkipReason.ChainBridgeHeld:
                    frameSkipChainBridgeHeld++;
                    break;
                case GhostPlaybackSkipReason.MissionLoopUnitInactive:
                    frameSkipMissionLoopUnitInactive++;
                    break;
            }
        }

        internal static bool ShouldEmitFrameSummary(GhostPlaybackFrameCounters counters)
        {
            return counters.spawned > 0
                || counters.destroyed > 0
                || counters.deferred > 0
                || counters.beforeActivation > 0
                || counters.anchorMissing > 0
                || counters.loopSyncFailed > 0
                || counters.parentLoopPaused > 0
                || counters.warpHidden > 0
                || counters.visualLoadFailed > 0
                || counters.noRenderableData > 0
                || counters.playbackDisabled > 0
                || counters.externalVesselSuppressed > 0
                || counters.sessionSuppressed > 0
                || counters.supersededByRelation > 0
                || counters.rewindRetired > 0
                || counters.spawnSuppressedDeadOnArrival > 0
                || counters.anchorRotationUnreliable > 0
                || counters.anchorReFlyUnstable > 0
                || counters.chainShadowed > 0
                || counters.chainBridgeHeld > 0
                || counters.missionLoopUnitInactive > 0;
        }

        internal static string BuildFrameSummaryMessage(GhostPlaybackFrameCounters counters)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Frame: spawned={0} destroyed={1} deferred={2} " +
                "skips[beforeActivation={3} anchorMissing={4} loopSyncFailed={5} " +
                "parentLoopPaused={6} warpHidden={7} visualLoadFailed={8} " +
                "noRenderableData={9} playbackDisabled={10} externalVesselSuppressed={11} " +
                "sessionSuppressed={12} supersededByRelation={13} rewindRetired={14} " +
                "spawnSuppressedDeadOnArrival={15} anchorRotationUnreliable={16} " +
                "anchorReFlyUnstable={17} chainShadowed={18} chainBridgeHeld={19} " +
                "missionLoopUnitInactive={20}] " +
                "active={21}",
                counters.spawned,
                counters.destroyed,
                counters.deferred,
                counters.beforeActivation,
                counters.anchorMissing,
                counters.loopSyncFailed,
                counters.parentLoopPaused,
                counters.warpHidden,
                counters.visualLoadFailed,
                counters.noRenderableData,
                counters.playbackDisabled,
                counters.externalVesselSuppressed,
                counters.sessionSuppressed,
                counters.supersededByRelation,
                counters.rewindRetired,
                counters.spawnSuppressedDeadOnArrival,
                counters.anchorRotationUnreliable,
                counters.anchorReFlyUnstable,
                counters.chainShadowed,
                counters.chainBridgeHeld,
                counters.missionLoopUnitInactive,
                counters.active);
        }

        private GhostPlaybackFrameCounters BuildCurrentFrameCounters()
        {
            return new GhostPlaybackFrameCounters
            {
                spawned = frameSpawnCount,
                destroyed = frameDestroyCount,
                deferred = frameSpawnDeferred,
                beforeActivation = frameSkipBeforeActivation,
                anchorMissing = frameSkipAnchorMissing,
                loopSyncFailed = frameSkipLoopSyncFailed,
                parentLoopPaused = frameSkipParentLoopPaused,
                warpHidden = frameSkipWarpHidden,
                visualLoadFailed = frameSkipVisualLoadFailed,
                noRenderableData = frameSkipNoRenderableData,
                playbackDisabled = frameSkipPlaybackDisabled,
                externalVesselSuppressed = frameSkipExternalVesselSuppressed,
                sessionSuppressed = frameSkipSessionSuppressed,
                supersededByRelation = frameSkipSupersededByRelation,
                rewindRetired = frameSkipRewindRetired,
                spawnSuppressedDeadOnArrival = frameSkipSpawnSuppressedDeadOnArrival,
                anchorRotationUnreliable = frameSkipAnchorRotationUnreliable,
                anchorReFlyUnstable = frameSkipAnchorReFlyUnstable,
                chainShadowed = frameSkipChainShadowed,
                chainBridgeHeld = frameSkipChainBridgeHeld,
                missionLoopUnitInactive = frameSkipMissionLoopUnitInactive,
                active = ghostStates.Count
            };
        }

        /// <summary>
        /// Emits exactly one INFO log per (recording index, userPeriod,
        /// effectiveCadence, duration) tuple. Re-emits only when one of the
        /// inputs changes — so a dense overlap recording is silent steady-state
        /// after its cadence stabilises. Surfaces the user-visible consequence
        /// of the cap-driven cadence adjustment so the change is never silent.
        /// </summary>
        private void LogOverlapCadenceIfChanged(
            int index, IPlaybackTrajectory traj,
            double userPeriod, double effectiveCadence, double duration)
        {
            var tuple = (userPeriod, effectiveCadence, duration);
            if (lastLoggedCadence.TryGetValue(index, out var prev) && prev.Equals(tuple))
                return;
            lastLoggedCadence[index] = tuple;

            string vesselName = traj != null ? traj.VesselName ?? "?" : "?";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            long cycleCount = duration > 0 && effectiveCadence > 0
                ? (long)Math.Ceiling(duration / effectiveCadence)
                : 0;
            bool adjusted = Math.Abs(effectiveCadence - userPeriod) > 1e-6;
            string verdict = adjusted
                ? "auto-adjusted (cap reached)"
                : "no adjustment";
            ParsekLog.Info("Engine",
                $"Loop cadence #{index} \"{vesselName}\": requested={userPeriod.ToString("F2", ic)}s " +
                $"duration={duration.ToString("F2", ic)}s cap={GhostPlayback.MaxOverlapGhostsPerRecording} " +
                $"effective={effectiveCadence.ToString("F2", ic)}s (cycles={cycleCount}) {verdict}");
        }

        internal void ResetCadenceLogCacheForTesting()
        {
            lastLoggedCadence.Clear();
        }

        #region Observability helpers

        /// <summary>
        /// Aggregates the current primary/overlap ghost counts and engine/RCS FX counts.
        /// Purely observational — callers must not use this to drive gameplay decisions.
        /// </summary>
        internal GhostObservability CaptureGhostObservability()
        {
            var result = new GhostObservability();

            foreach (var kvp in ghostStates)
            {
                CountPrimaryGhostForObservability(kvp.Value, ref result);
            }

            foreach (var kvp in overlapGhosts)
            {
                var overlaps = kvp.Value;
                if (overlaps == null) continue;

                for (int i = 0; i < overlaps.Count; i++)
                    CountOverlapGhostForObservability(overlaps[i], ref result);
            }

            return result;
        }

        private static void CountPrimaryGhostForObservability(
            GhostPlaybackState state, ref GhostObservability result)
        {
            if (!HasLoadedGhostVisuals(state)) return;

            result.activePrimaryGhostCount++;

            if (state.currentZone == RenderingZone.Physics)
                result.zone1GhostCount++;
            else if (state.currentZone == RenderingZone.Visual)
                result.zone2GhostCount++;

            if (state.fidelityReduced)
                result.softCapReducedCount++;
            if (state.simplified)
                result.softCapSimplifiedCount++;

            CountFxForObservability(state, ref result);
        }

        private static void CountOverlapGhostForObservability(
            GhostPlaybackState state, ref GhostObservability result)
        {
            if (!HasLoadedGhostVisuals(state)) return;

            result.activeOverlapGhostCount++;
            CountFxForObservability(state, ref result);
        }

        // internal (was private) so the FX counting arithmetic is reachable headlessly:
        // the two callers above gate on HasLoadedGhostVisuals, which needs a Unity
        // GameObject and can never be satisfied in xUnit. Behaviour unchanged.
        internal static void CountFxForObservability(
            GhostPlaybackState state, ref GhostObservability result)
        {
            int engineModulesForGhost = CountModulesAndParticleSystems(
                state?.engineInfos, out int engineSystemsForGhost);
            if (engineModulesForGhost > 0)
            {
                result.ghostsWithEngineFx++;
                result.engineModuleCount += engineModulesForGhost;
                result.engineParticleSystemCount += engineSystemsForGhost;
            }

            int rcsModulesForGhost = CountModulesAndParticleSystems(
                state?.rcsInfos, out int rcsSystemsForGhost);
            if (rcsModulesForGhost > 0)
            {
                result.ghostsWithRcsFx++;
                result.rcsModuleCount += rcsModulesForGhost;
                result.rcsParticleSystemCount += rcsSystemsForGhost;
            }
        }

        internal static int CountModulesAndParticleSystems<TGhostInfo>(
            Dictionary<ulong, TGhostInfo> infos, out int particleSystemCount)
            where TGhostInfo : class
        {
            particleSystemCount = 0;
            if (infos == null || infos.Count == 0)
                return 0;

            int moduleCount = 0;
            foreach (var info in infos.Values)
            {
                if (info == null) continue;

                moduleCount++;
                if (info is EngineGhostInfo engineInfo)
                    particleSystemCount += engineInfo.particleSystems?.Count ?? 0;
                else if (info is RcsGhostInfo rcsInfo)
                    particleSystemCount += rcsInfo.particleSystems?.Count ?? 0;
            }

            return moduleCount;
        }

        #endregion

        // Render-EVENT (map-render-event-logging): flight-scene mesh APPEAR / DISAPPEAR. The engine fired
        // OnGhostCreated / OnGhostDestroyed for the policy, but GhostRenderTrace logged a ghost's first frame
        // only as a NEXT-frame FrameStart reason=first-seen and its retire only as a trailing retired=true -
        // no clean lifecycle EVENT mirroring the map's GhostCreated / GhostDestroyed. Emit one here so the
        // flight scene is greppable for spawn/despawn too (surface stays the GhostRenderTrace mesh surface;
        // its prefix already carries rec=/recId=, so no recId threading). important=true => always emitted
        // when enabled, routed to Info. Gated by GhostRenderTrace.IsEnabled so the Planetarium read + format
        // never run in normal play (read-only observability).
        private static bool EmitMeshLifecycleTrace(
            string phase, int index, IPlaybackTrajectory traj, GhostPlaybackState state, string reason)
        {
            string recId = traj?.RecordingId;
            if (string.IsNullOrEmpty(recId))
                recId = state?.recordingId;
            if (string.IsNullOrEmpty(recId))
                return false;
            // Enabled-gate via ShouldEmitPhase (GhostRenderTrace.IsEnabled is private): with important=true it
            // returns the tracer's enabled state, so the Planetarium read + format below never run in normal
            // (tracing-off) play. EmitPhase re-checks the same gate internally.
            if (!GhostRenderTrace.ShouldEmitPhase(recId, 0.0, important: true))
                return false;
            double ut = ReadMeshTraceUT();
            GhostRenderTrace.EmitPhase(
                recId, index, ut, ut, phase,
                FormattableString.Invariant(
                    $"vessel={traj?.VesselName ?? state?.vesselName ?? "Unknown"} reason={reason ?? "lifecycle"}"),
                important: true);
            return true;
        }

        /// <summary>
        /// Loop-cycle advance EVENT for the ghostlife per-cycle census: one tracing-gated Info line when a
        /// ghost's cycle index moves while its object lives on (reused across the boundary, demoted to the
        /// overlap list, or a mission-unit member rolling over). The vessel rides LAST because it is free
        /// text that may contain spaces.
        /// </summary>
        internal static void EmitLoopCycleTrace(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            long previousCycle, long newCycle, string mode)
        {
            string recId = traj?.RecordingId;
            if (string.IsNullOrEmpty(recId))
                recId = state?.recordingId;
            if (string.IsNullOrEmpty(recId)
                || !GhostRenderTrace.ShouldEmitPhase(recId, 0.0, important: true))
                return;
            double ut = ReadMeshTraceUT();
            GhostRenderTrace.EmitPhase(
                recId, index, ut, ut, "LoopCycle",
                FormattableString.Invariant(
                    $"cycle={newCycle} prev={previousCycle} mode={mode} vessel={traj?.VesselName ?? state?.vesselName ?? "Unknown"}"),
                important: true);
        }

        private static double ReadMeshTraceUT()
        {
            Func<double> ovr = MeshTraceUTOverrideForTesting;
            return ovr != null ? ovr() : ReadPlanetariumUT();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ReadPlanetariumUT()
        {
            return Planetarium.GetUniversalTime();
        }

        private static void ResetGhostAppearanceTracking(GhostPlaybackState state)
        {
            if (state == null)
                return;

            state.hadVisibleRenderersLastFrame = false;
        }

        private void TrackGhostAppearance(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, string reason,
            double requestedPlaybackUT = double.NaN)
        {
            if (state == null)
                return;

            if (state.ghost == null || !state.ghost.activeInHierarchy)
            {
                state.hadVisibleRenderersLastFrame = false;
                return;
            }

            Bounds visibleBounds;
            int rendererCount;
            if (!TryGetCombinedVisibleRendererBounds(state.ghost, out visibleBounds, out rendererCount))
            {
                state.hadVisibleRenderersLastFrame = false;
                return;
            }

            if (state.hadVisibleRenderersLastFrame)
                return;

            state.hadVisibleRenderersLastFrame = true;
            state.appearanceCount++;

            Vector3d rootPos = state.ghost.transform.position;
            Quaternion rootRot = state.ghost.transform.rotation;
            Vector3d boundsCenter = visibleBounds.center;
            Vector3d boundsRootDelta = boundsCenter - rootPos;
            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double requestedUT = double.IsNaN(requestedPlaybackUT) ? playbackUT : requestedPlaybackUT;
            bool firstFrameClamped = Math.Abs(requestedUT - playbackUT) > 1e-6;

            Transform firstVisiblePart = FindFirstVisibleGhostPart(state.ghost);
            string firstVisiblePartLabel = "none";
            Vector3d firstVisiblePartPos = Vector3d.zero;
            Vector3d firstVisiblePartRootDelta = Vector3d.zero;
            if (firstVisiblePart != null)
            {
                firstVisiblePartLabel = firstVisiblePart.name;
                firstVisiblePartPos = firstVisiblePart.position;
                firstVisiblePartRootDelta = firstVisiblePartPos - rootPos;
            }

            ConfigNode snapshotNode = GhostVisualBuilder.GetGhostSnapshot(traj);
            Vector3 snapshotCoM = Vector3.zero;
            bool hasSnapshotCoM = GhostVisualBuilder.TryGetSnapshotCenterOfMass(snapshotNode, out snapshotCoM);

            string rootPartSummary = "rootPart=unknown";
            Transform rootPartTransform = null;
            string rootPartName;
            uint rootPartPersistentId;
            Vector3 rootPartLocalPosition;
            Quaternion rootPartLocalRotation;
            if (GhostVisualBuilder.TryGetSnapshotRootPartInfo(
                snapshotNode,
                out rootPartName,
                out rootPartPersistentId,
                out rootPartLocalPosition,
                out rootPartLocalRotation))
            {
                rootPartSummary =
                    $"rootPart={rootPartName ?? "unknown"} pid={rootPartPersistentId} " +
                    $"rootPartLocal={FormatVector3(rootPartLocalPosition)} " +
                    $"rootPartLocalRot={FormatQuaternion(rootPartLocalRotation)}";
                if (rootPartPersistentId != 0)
                    rootPartTransform = GhostVisualBuilder.FindGhostPartTransform(state.ghost, rootPartPersistentId);
            }

            string activeSectionSummary = DescribeAppearanceActiveSection(traj, playbackUT);
            string recordingStartSummary = DescribeAppearanceRecordingStartPoint(traj, rootPos);

            string rootPartWorldSummary = string.Empty;
            if (rootPartTransform != null)
            {
                Vector3d rootPartWorldPos = rootPartTransform.position;
                rootPartWorldSummary =
                    $" rootPartWorld={FormatVector3d(rootPartWorldPos)} " +
                    $"rootPart-root={FormatVector3d(rootPartWorldPos - rootPos)}";
            }

            // #375: was Info, demoted to Verbose after the #258 fix (first-visible-frame
            // activation) was field-validated. High-volume with many debris ghosts.
            // The on-change gate above (state.hadVisibleRenderersLastFrame) is the inner
            // throttle; this outer wall-clock rate limit additionally collapses the
            // overlap-cycle-rebuild case where ResetGhostAppearanceTracking flips the
            // flag every loop/overlap cycle (many times/sec at warp).
            ParsekLog.VerboseRateLimited("GhostAppearance", $"ghost-appearance-{index}",
                () =>
                    $"Ghost #{index} \"{traj?.VesselName ?? state.vesselName ?? "unknown"}\" " +
                    $"appearance#{state.appearanceCount} reason={reason} " +
                    $"ut={playbackUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"requestedUT={requestedUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"firstFrameClamped={(firstFrameClamped ? "T" : "F")} " +
                    $"activationStart={activationStartUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"activationLead={(playbackUT - activationStartUT).ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"zone={state.currentZone} dist={FormatPlaybackDistanceForLog(state.lastDistance)} " +
                    $"{activeSectionSummary} " +
                    $"root={FormatVector3d(rootPos)} rootRot={FormatQuaternion(rootRot)} " +
                    $"boundsCenter={FormatVector3d(boundsCenter)} bounds-root={FormatVector3d(boundsRootDelta)} " +
                    $"firstVisiblePart={firstVisiblePartLabel}:{FormatVector3d(firstVisiblePartPos)} " +
                    $"part-root={FormatVector3d(firstVisiblePartRootDelta)} " +
                    $"{recordingStartSummary} " +
                    $"snapshotCoM={(hasSnapshotCoM ? FormatVector3(snapshotCoM) : "none")} " +
                    $"{rootPartSummary}{rootPartWorldSummary} visibleRenderers={rendererCount}",
                2.0);
        }

        internal static string DescribeAppearanceActiveSection(IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return "activeFrame=unsectioned";

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIdx < 0 || sectionIdx >= traj.TrackSections.Count)
                return "activeFrame=none";

            TrackSection section = traj.TrackSections[sectionIdx];
            string anchorSuffix = section.referenceFrame == ReferenceFrame.Relative
                ? !string.IsNullOrEmpty(section.anchorRecordingId)
                    ? $" anchorRec={section.anchorRecordingId}"
                    : " anchorRec=missing"
                : string.Empty;
            return
                $"activeFrame={section.referenceFrame} " +
                $"sectionUT={section.startUT.ToString("F2", CultureInfo.InvariantCulture)}-" +
                $"{section.endUT.ToString("F2", CultureInfo.InvariantCulture)}{anchorSuffix}";
        }

        internal static string DescribeAppearanceRecordingStartPoint(
            IPlaybackTrajectory traj, Vector3d rootPos)
        {
            if (traj?.Points == null || traj.Points.Count == 0)
                return "recordingStart=none";

            TrajectoryPoint firstPoint = traj.Points[0];
            ReferenceFrame frame = ReferenceFrame.Absolute;
            string anchorRecordingId = null;
            TrackSection containingSection = default;
            bool hasContainingSection = false;

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, firstPoint.ut);
                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                {
                    TrackSection section = traj.TrackSections[sectionIdx];
                    containingSection = section;
                    hasContainingSection = true;
                    frame = section.referenceFrame;
                    anchorRecordingId = section.anchorRecordingId;
                }
            }

            if (frame == ReferenceFrame.Relative)
            {
                if (!DoesPointMatchSectionFrame(hasContainingSection, containingSection, firstPoint))
                {
                    string flatSummary = DescribeAbsoluteRecordingStartPoint(
                        firstPoint,
                        rootPos,
                        "FlatFallback");
                    return
                        $"{flatSummary} sectionFrame=Relative " +
                        (!string.IsNullOrEmpty(anchorRecordingId)
                            ? $"anchorRec={anchorRecordingId}"
                            : "anchorRec=missing");
                }

                Vector3d offset = new Vector3d(firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                return
                    $"recordingStart@{firstPoint.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"frame=Relative offset={FormatVector3d(offset)} " +
                    (!string.IsNullOrEmpty(anchorRecordingId)
                        ? $"anchorRec={anchorRecordingId}"
                        : "anchorRec=missing");
            }

            return DescribeAbsoluteRecordingStartPoint(firstPoint, rootPos, frame.ToString());
        }

        internal static bool DoesPointMatchSectionFrame(
            bool hasContainingSection,
            TrackSection section,
            TrajectoryPoint point)
        {
            if (!hasContainingSection || section.frames == null)
                return false;

            for (int i = 0; i < section.frames.Count; i++)
            {
                TrajectoryPoint frame = section.frames[i];
                if (Math.Abs(frame.ut - point.ut) <= 1e-6
                    && Math.Abs(frame.latitude - point.latitude) <= 1e-6
                    && Math.Abs(frame.longitude - point.longitude) <= 1e-6
                    && Math.Abs(frame.altitude - point.altitude) <= 1e-6)
                    return true;
            }

            return false;
        }

        private static string DescribeAbsoluteRecordingStartPoint(
            TrajectoryPoint firstPoint,
            Vector3d rootPos,
            string frameLabel)
        {
            string rawSummary =
                $"recordingStart@{firstPoint.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"frame={frameLabel} body={firstPoint.bodyName ?? "unknown"} " +
                $"lla={FormatVector3d(new Vector3d(firstPoint.latitude, firstPoint.longitude, firstPoint.altitude))}";

            CelestialBody body = TryGetBodyByNameForAppearance(firstPoint.bodyName);
            if (body == null)
                return $"{rawSummary} world=unresolved";

            Vector3d worldPos = body.GetWorldSurfacePosition(
                firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
            Vector3d worldRootDelta = worldPos - rootPos;
            Quaternion worldRot = body.bodyTransform.rotation *
                TrajectoryMath.SanitizeQuaternion(firstPoint.rotation);
            return
                $"{rawSummary} world={FormatVector3d(worldPos)} " +
                $"recordingStart-root={FormatVector3d(worldRootDelta)} " +
                $"recordingStartRot={FormatQuaternion(worldRot)}";
        }

        private static CelestialBody TryGetBodyByNameForAppearance(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;

            try
            {
                return FlightGlobals.GetBodyByName(bodyName);
            }
            catch (Exception)
            {
                // Headless xUnit can trip FlightGlobals static initialization; the
                // appearance diagnostic should degrade to world=unresolved instead.
                return null;
            }
        }

        private static bool TryGetCombinedVisibleRendererBounds(
            GameObject ghost, out Bounds combinedBounds, out int rendererCount)
        {
            combinedBounds = new Bounds();
            rendererCount = 0;
            if (ghost == null)
                return false;

            Transform partContainer = GhostVisualBuilder.GetGhostPartContainer(ghost.transform);
            if (partContainer == null)
                return false;

            var renderers = partContainer.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!ShouldUseRendererForAppearance(renderer))
                    continue;

                if (rendererCount == 0)
                    combinedBounds = renderer.bounds;
                else
                    combinedBounds.Encapsulate(renderer.bounds);
                rendererCount++;
            }

            return rendererCount > 0;
        }

        private static bool ShouldUseRendererForAppearance(Renderer renderer)
        {
            return renderer != null
                && !(renderer is ParticleSystemRenderer)
                && renderer.enabled
                && renderer.gameObject.activeInHierarchy;
        }

        private static Transform FindFirstVisibleGhostPart(GameObject ghost)
        {
            if (ghost == null)
                return null;

            Transform partContainer = GhostVisualBuilder.GetGhostPartContainer(ghost.transform);
            if (partContainer == null)
                return null;

            Transform firstActivePart = null;
            for (int i = 0; i < partContainer.childCount; i++)
            {
                Transform child = partContainer.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy || !child.name.StartsWith("ghost_part_"))
                    continue;

                if (firstActivePart == null)
                    firstActivePart = child;

                var renderers = child.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    if (ShouldUseRendererForAppearance(renderers[r]))
                        return child;
                }
            }

            return firstActivePart;
        }

        private static string FormatVector3(Vector3 value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F2},{1:F2},{2:F2})",
                value.x,
                value.y,
                value.z);
        }

        private static string FormatVector3d(Vector3d value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F2},{1:F2},{2:F2})",
                value.x,
                value.y,
                value.z);
        }

        private static string FormatQuaternion(Quaternion value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F3},{1:F3},{2:F3},{3:F3})",
                value.x,
                value.y,
                value.z,
                value.w);
        }
    }
}
