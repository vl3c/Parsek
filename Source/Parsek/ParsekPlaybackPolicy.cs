using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;
using TrackingStationGhostSource = Parsek.GhostMapPresence.TrackingStationGhostSource;

namespace Parsek
{
    /// <summary>
    /// Subscribes to GhostPlaybackEngine lifecycle events and implements
    /// Parsek-specific policy: vessel spawning, resource replay, camera
    /// management, deferred spawn queue, spawn-death-loop detection.
    ///
    /// This class knows about Recording, RecordingStore, VesselSpawner —
    /// it is the bridge between the engine's visual mechanics and Parsek's
    /// recording tree / timeline / spawn system.
    /// </summary>
    // [ERS-exempt — Phase 3] ParsekPlaybackPolicy dispatches on PlaybackCompleted
    // events whose .Index is the committed-recording index used by
    // GhostPlaybackEngine.ghostStates. Every RecordingStore.CommittedRecordings
    // read in this file pairs with an index comparison against evt.Index;
    // converting to EffectiveState.ComputeERS() would de-align the two spaces
    // and silently misroute spawn / watch / hold decisions.
    // TODO(phase 6+): once ghostStates migrates to recording-id keys, route all
    // reads here through ComputeERS().
    internal class ParsekPlaybackPolicy
    {
        // Deferred ghost creation in the 2026-04-26 watch-auto-follow repro took
        // about 7.7s (KSP.log:27274); keep the retry hold comfortably above that
        // so partial ghost builds can finish before the camera gives up.
        private const float DeferredWatchTransferHoldSeconds = 30f;

        private readonly GhostPlaybackEngine engine;
        private readonly ParsekFlight host;
        internal Func<bool> IsWarpActiveOverrideForTesting;
        internal Func<double> CurrentUTOverrideForTesting;
        internal Func<float> CurrentRealTimeOverrideForTesting;
        internal Func<IReadOnlyList<Recording>, IReadOnlyDictionary<string, TimelineInactiveReason>>
            TimelineInactiveIdsOverrideForTesting;
        internal Action<Recording, int> SpawnVesselOrChainTipOverrideForTesting;
        internal Action<uint> DeferredActivateVesselOverrideForTesting;
        internal Action<int, string> DestroyGhostOverrideForTesting;
        internal const int FlagReplayWarnRetryThreshold = 3;

        // Deferred spawn queue: recording IDs queued during warp, flushed when warp ends
        internal readonly HashSet<string> pendingSpawnRecordingIds = new HashSet<string>();
        internal readonly HashSet<string> pendingFlagReplayRecordingIds = new HashSet<string>();
        private readonly Dictionary<string, int> pendingFlagReplayFailureCounts = new Dictionary<string, int>();
        internal string pendingWatchRecordingId;

        /// <summary>
        /// Tracks ghosts held at their final position while waiting for a blocked/deferred
        /// spawn to resolve. The ghost stays visible so there is no gap between ghost
        /// disappearance and real vessel appearance (#96).
        /// Key: recording index. Value: info about the held ghost (start time, watched state).
        /// </summary>
        internal readonly Dictionary<int, HeldGhostInfo> heldGhosts = new Dictionary<int, HeldGhostInfo>();

        /// <summary>Maximum real-time seconds to hold a ghost before giving up and destroying it.</summary>
        internal const float HeldGhostTimeoutSeconds = 5.0f;
        internal const float HeldGhostRetryIntervalSeconds = 1.0f;

        internal ParsekPlaybackPolicy(GhostPlaybackEngine engine, ParsekFlight host)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.host = host ?? throw new ArgumentNullException(nameof(host));

            // Subscribe to engine events
            engine.OnPlaybackCompleted += HandlePlaybackCompleted;
            engine.OnGhostCreated += HandleGhostCreated;
            engine.OnGhostDestroyed += HandleGhostDestroyed;
            engine.OnLoopRestarted += HandleLoopRestarted;
            engine.OnOverlapExpired += HandleOverlapExpired;
            engine.OnAllGhostsDestroying += HandleAllGhostsDestroying;

            // Tell the engine how to check if a ghost is being held
            engine.IsGhostHeld = idx =>
                heldGhosts.ContainsKey(idx) || host.WatchedRecordingIndex == idx;

            // Tell the engine how to resolve the chain continuation for a
            // given slot. Used by ChainHandoffLogic to shadow the head when
            // the continuation is rendering (overlap case) and bridge-hold
            // the head when the continuation has not yet activated (gap
            // case). Returns -1 when no continuation exists.
            engine.ResolveChainNextIndex = idx => ResolveChainNextSlotIndex(idx);

            ParsekLog.Info("Policy", "ParsekPlaybackPolicy created and subscribed to engine events");
        }

        /// <summary>
        /// Pre-check: detect spawned vessels that died since last frame.
        /// Runs BEFORE engine.UpdatePlayback() so flags reflect current state.
        /// If a spawned vessel's PID is no longer in FlightGlobals.Vessels, the
        /// recording is either reset for re-spawn or abandoned after MaxSpawnDeathCycles.
        ///
        /// <para>
        /// The active-re-fly-session short-circuit (re-fly marker) covers
        /// the §6.4 silent-strip contract for re-fly sessions:
        /// <see cref="PostLoadStripper.Strip"/> kills sibling vessels on
        /// purpose during the re-fly load, and those kills must not feed
        /// back into the policy as "spawned vessel died, please re-spawn".
        /// </para>
        ///
        /// <para>
        /// The <see cref="RewindContext.IsRewinding"/> short-circuit is
        /// pure defense-in-depth — by the time the FLIGHT update path
        /// runs <see cref="RunSpawnDeathChecks"/> after a plain rewind,
        /// <see cref="ParsekScenario.HandleRewindOnLoad"/> has already
        /// called <see cref="RewindContext.EndRewind"/>, so the flag is
        /// false. It does NOT close the source duplicate-spawn from the
        /// #573 playtest — that duplicate fired through
        /// <see cref="VesselGhoster.SpawnAtChainTip"/> during a
        /// warp-deferred activation, not through the spawn-death-then-respawn
        /// loop. The real fix sets scoped
        /// <see cref="Recording.SpawnSuppressedByRewind"/> metadata only on
        /// the active/source recording stripped by rewind (see
        /// <see cref="ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly"/>),
        /// which <see cref="GhostPlaybackLogic.ShouldSpawnAtRecordingEnd"/>
        /// honours regardless of the rewind flag's state. Same-tree future
        /// recordings remain spawn-eligible for #589.
        /// </para>
        /// </summary>
        internal void RunSpawnDeathChecks()
        {
            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null)
            {
                ParsekLog.VerboseRateLimited("Policy", "spawn-death-skip-refly",
                    $"RunSpawnDeathChecks: skipped during active re-fly session " +
                    $"sess={scenario.ActiveReFlySessionMarker.SessionId ?? "<no-id>"} — " +
                    "Strip kills are intentional and must not trigger respawn");
                return;
            }
            if (RewindContext.IsRewinding)
            {
                ParsekLog.VerboseRateLimited("Policy", "spawn-death-skip-rewind",
                    "RunSpawnDeathChecks: defense-in-depth skip during active rewind — " +
                    "production sequence ends rewind before this path runs; " +
                    "the real plain-rewind fix is scoped SpawnSuppressedByRewind (#573/#589)");
                return;
            }

            var committed = RecordingStore.CommittedRecordings;
            if (committed.Count == 0) return;
            var timelineInactiveIds = CurrentTimelineInactiveRecordingReasons(committed);

            int detected = 0;
            int abandoned = 0;
            int skippedTimelineInactive = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (IsTimelineInactive(rec, timelineInactiveIds))
                {
                    skippedTimelineInactive++;
                    continue;
                }

                if (!GhostPlaybackLogic.ShouldCheckForSpawnDeath(
                        rec.VesselSpawned, rec.SpawnedVesselPersistentId, rec.SpawnAbandoned))
                    continue;

                // Vessel still alive — no action needed
                if (FlightRecorder.FindVesselByPid(rec.SpawnedVesselPersistentId) != null)
                    continue;

                // Vessel died since last frame
                rec.SpawnDeathCount++;
                detected++;

                if (rec.TerminalStateValue == TerminalState.Orbiting
                    && VesselSpawner.HasRecordedTerminalOrbit(rec))
                {
                    uint terminalOldPid = rec.SpawnedVesselPersistentId;
                    rec.VesselSpawned = false;
                    rec.SpawnedVesselPersistentId = 0;
                    var decision = new TerminalOrbitSpawnSafetyDecision
                    {
                        Action = TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                        ReasonCode = TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied,
                        Reason = "spawned terminal orbit vessel died before it could remain materialized",
                        CurrentAltitude = rec.TerminalSpawnSafetyAltitude,
                        AtmosphereDepth = double.NaN,
                        SafetyMargin = TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters,
                        SafeAltitude = rec.TerminalSpawnSafetySafeAltitude,
                        PeriapsisAltitude = rec.TerminalSpawnSafetyPeriapsisAltitude,
                        ApoapsisAltitude = rec.TerminalSpawnSafetyApoapsisAltitude,
                        NextSafeUT = double.NaN,
                        NextSafeAltitude = double.NaN,
                    };
                    TerminalOrbitSpawnSafety.MarkCannotSpawnSafely(
                        rec,
                        decision,
                        CurrentUniversalTimeSafe(),
                        rec.TerminalSpawnSafetyPressure);
                    abandoned++;
                    ParsekLog.Warn("Policy",
                        $"Spawn-death detected for terminal orbit and will not be retried: " +
                        $"#{i} \"{rec.VesselName}\" pid={terminalOldPid} deathCount={rec.SpawnDeathCount} " +
                        $"reason={decision.ReasonCode}");
                    continue;
                }

                if (VesselSpawner.ShouldAbandonSpawnDeathLoop(
                        rec.SpawnDeathCount, VesselSpawner.MaxSpawnDeathCycles))
                {
                    rec.SpawnAbandoned = true;
                    abandoned++;
                    ParsekLog.Warn("Policy",
                        $"Spawn-death loop abandoned: #{i} \"{rec.VesselName}\" " +
                        $"pid={rec.SpawnedVesselPersistentId} deathCount={rec.SpawnDeathCount} " +
                        $"— exceeded {VesselSpawner.MaxSpawnDeathCycles} max cycles");
                }
                else
                {
                    uint oldPid = rec.SpawnedVesselPersistentId;
                    rec.VesselSpawned = false;
                    rec.SpawnedVesselPersistentId = 0;
                    rec.SpawnAttempts = 0;
                    ParsekLog.Info("Policy",
                        $"Spawn-death detected: #{i} \"{rec.VesselName}\" " +
                        $"pid={oldPid} deathCount={rec.SpawnDeathCount} — reset for re-spawn");
                }
            }

            if (detected > 0)
                ParsekLog.Info("Policy",
                    $"RunSpawnDeathChecks: {detected} death(s) detected, {abandoned} abandoned");
            if (skippedTimelineInactive > 0)
                ParsekLog.VerboseRateLimited("Policy", "spawn-death-skip-timeline-inactive",
                    $"RunSpawnDeathChecks: skipped {skippedTimelineInactive} timeline-inactive recording(s)");
        }

        /// <summary>
        /// Removes a recording ID from the deferred spawn / deferred flag replay queues
        /// (for example when the recording is deleted).
        /// </summary>
        internal void RemovePendingSpawn(string recordingId)
        {
            pendingSpawnRecordingIds.Remove(recordingId);
            pendingFlagReplayRecordingIds.Remove(recordingId);
            pendingFlagReplayFailureCounts.Remove(recordingId);
        }

        /// <summary>
        /// Flush deferred spawns that were queued during warp.
        /// Runs AFTER engine.UpdatePlayback() when warp ends.
        /// Iterates committed recordings and materializes eligible vessels.
        /// </summary>
        internal void FlushDeferredSpawns()
        {
            bool isWarpActive = IsWarpActiveOverrideForTesting != null
                ? IsWarpActiveOverrideForTesting()
                : GhostPlaybackEngine.IsAnyWarpActiveFromGlobals();
            var committed = RecordingStore.CommittedRecordings;
            var timelineInactiveIds = CurrentTimelineInactiveRecordingReasons(committed);
            PurgeTimelineInactiveDeferredQueues(timelineInactiveIds);

            if (!GhostPlaybackLogic.ShouldFlushDeferredSpawns(
                    pendingSpawnRecordingIds.Count + pendingFlagReplayRecordingIds.Count,
                    isWarpActive))
                return;

            double currentUT = CurrentUTOverrideForTesting != null
                ? CurrentUTOverrideForTesting()
                : Planetarium.GetUniversalTime();
            int spawnedCount = 0;
            var flushedSpawnIds = new List<string>();
            var clearedFlagReplayIds = new List<string>();

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (IsTimelineInactive(rec, timelineInactiveIds))
                    continue;

                bool pendingSpawn = pendingSpawnRecordingIds.Contains(rec.RecordingId);
                bool pendingFlagReplay = pendingFlagReplayRecordingIds.Contains(rec.RecordingId);
                if (!pendingSpawn && !pendingFlagReplay)
                    continue;

                bool spawnedNow = false;
                if (pendingSpawn && rec.TerminalSpawnCannotSpawnSafely)
                {
                    ParsekLog.Warn("Policy",
                        $"Deferred spawn cannot execute safely: #{i} \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} reason={rec.TerminalSpawnSafetyReasonCode ?? "(none)"}");
                    flushedSpawnIds.Add(rec.RecordingId);
                    pendingSpawn = false;
                }
                else if (pendingSpawn
                    && TerminalOrbitSpawnSafety.ShouldHoldDeferredSpawnUntilUT(
                        rec,
                        currentUT,
                        out string terminalHoldReason))
                {
                    ParsekLog.VerboseRateLimited("Policy",
                        "terminal-deferred-spawn-held-" + (rec.RecordingId ?? i.ToString(CultureInfo.InvariantCulture)),
                        $"Deferred spawn held for terminal orbit safety: #{i} \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} reason={terminalHoldReason}");
                    continue;
                }
                else if (pendingSpawn && GhostPlaybackLogic.ShouldSkipDeferredSpawn(
                        rec.VesselSpawned, rec.VesselSnapshot != null))
                {
                    ParsekLog.Verbose("Policy",
                        $"Deferred spawn skipped — #{i} \"{rec.VesselName}\" already spawned or no snapshot");
                    flushedSpawnIds.Add(rec.RecordingId);
                    pendingSpawn = false;
                }
                else if (pendingSpawn)
                {
                    ParsekLog.Info("Policy",
                        $"Deferred spawn executing: #{i} \"{rec.VesselName}\" id={rec.RecordingId}");
                    if (SpawnVesselOrChainTipOverrideForTesting != null)
                        SpawnVesselOrChainTipOverrideForTesting(rec, i);
                    else
                        host.SpawnVesselOrChainTipFromPolicy(rec, i);

                    if (rec.VesselSpawned)
                    {
                        spawnedCount++;
                        flushedSpawnIds.Add(rec.RecordingId);
                        pendingSpawn = false;
                        spawnedNow = true;
                    }
                    else if (rec.TerminalSpawnCannotSpawnSafely)
                    {
                        ParsekLog.Warn("Policy",
                            $"Deferred spawn resolved as cannot-spawn-safely: #{i} \"{rec.VesselName}\" " +
                            $"id={rec.RecordingId} reason={rec.TerminalSpawnSafetyReasonCode ?? "(none)"}");
                        flushedSpawnIds.Add(rec.RecordingId);
                        pendingSpawn = false;
                    }
                    else if (TerminalOrbitSpawnSafety.HasActiveHold(rec))
                    {
                        ParsekLog.Info("Policy",
                            $"Deferred spawn remains pending after terminal orbit safety decision: " +
                            $"#{i} \"{rec.VesselName}\" id={rec.RecordingId} " +
                            $"reason={rec.TerminalSpawnSafetyReasonCode ?? "(none)"}");
                        continue;
                    }
                    else
                    {
                        ParsekLog.Verbose("Policy",
                            $"Deferred spawn flushed without materializing: #{i} \"{rec.VesselName}\" " +
                            $"id={rec.RecordingId} reason=non-terminal-spawn-failed-or-skipped");
                        flushedSpawnIds.Add(rec.RecordingId);
                        pendingSpawn = false;
                    }
                }

                if (spawnedNow || pendingFlagReplay)
                {
                    var (eligibleFlags, spawnedFlags, alreadyPresentFlags, failedFlags) =
                    GhostPlaybackLogic.SpawnFlagVesselsUpToUT(rec, currentUT);

                    if (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
                    {
                        ParsekLog.Verbose("Policy",
                            $"Deferred flag flush: #{i} \"{rec.VesselName}\" " +
                            $"spawned {spawnedFlags}/{eligibleFlags} flag(s) " +
                            $"alreadyPresent={alreadyPresentFlags} failed={failedFlags} " +
                            $"up to UT {currentUT.ToString("R", CultureInfo.InvariantCulture)}");
                    }

                    if (failedFlags > 0)
                    {
                        pendingFlagReplayRecordingIds.Add(rec.RecordingId);
                        int failureCount = 1;
                        if (pendingFlagReplayFailureCounts.TryGetValue(rec.RecordingId, out int existingFailureCount))
                            failureCount = existingFailureCount + 1;
                        pendingFlagReplayFailureCounts[rec.RecordingId] = failureCount;

                        if (failureCount == FlagReplayWarnRetryThreshold)
                        {
                            ParsekLog.Warn("Policy",
                                $"Deferred flag replay still failing after {failureCount} flush attempt(s): " +
                                $"#{i} \"{rec.VesselName}\" eligible={eligibleFlags} spawned={spawnedFlags} " +
                                $"alreadyPresent={alreadyPresentFlags} failed={failedFlags}");
                        }
                    }
                    else
                    {
                        clearedFlagReplayIds.Add(rec.RecordingId);
                        pendingFlagReplayFailureCounts.Remove(rec.RecordingId);
                    }
                }

                // Restore camera follow if this recording was being watched when deferred
                if (spawnedNow && GhostPlaybackLogic.ShouldRestoreWatchMode(
                        pendingWatchRecordingId, rec.RecordingId, rec.SpawnedVesselPersistentId))
                {
                    ParsekLog.Info("Policy",
                        $"Deferred watch: switching to spawned vessel pid={rec.SpawnedVesselPersistentId}");
                    if (DeferredActivateVesselOverrideForTesting != null)
                        DeferredActivateVesselOverrideForTesting(rec.SpawnedVesselPersistentId);
                    else
                        host.DeferredActivateVesselFromPolicy(rec.SpawnedVesselPersistentId);
                }
            }

            // Remove flushed IDs; failed flag replays stay queued.
            for (int j = 0; j < flushedSpawnIds.Count; j++)
                pendingSpawnRecordingIds.Remove(flushedSpawnIds[j]);
            for (int j = 0; j < clearedFlagReplayIds.Count; j++)
            {
                pendingFlagReplayRecordingIds.Remove(clearedFlagReplayIds[j]);
                pendingFlagReplayFailureCounts.Remove(clearedFlagReplayIds[j]);
            }

            if (spawnedCount > 0 || flushedSpawnIds.Count > 0)
            {
                ParsekLog.Info("Policy",
                    $"Warp ended — flushed {spawnedCount}/{flushedSpawnIds.Count} deferred spawn(s)");
            }
            else if (pendingSpawnRecordingIds.Count > 0)
            {
                ParsekLog.VerboseRateLimited("Policy", "deferred-spawn-flush-waiting",
                    $"Deferred spawn flush waiting: pending={pendingSpawnRecordingIds.Count}");
            }

            if (pendingSpawnRecordingIds.Count == 0)
                pendingWatchRecordingId = null;
        }

        private static IReadOnlyDictionary<string, TimelineInactiveReason> CurrentTimelineInactiveRecordingReasons(
            IReadOnlyList<Recording> committed)
        {
            var scenario = ParsekScenario.Instance;
            return EffectiveState.ComputeTimelineInactiveRecordingIds(
                committed,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingSupersedes,
                object.ReferenceEquals(null, scenario) ? null : scenario.RecordingRewindRetirements);
        }

        private static bool IsTimelineInactive(
            Recording rec,
            IReadOnlyDictionary<string, TimelineInactiveReason> timelineInactiveIds)
        {
            return rec != null
                && !string.IsNullOrEmpty(rec.RecordingId)
                && timelineInactiveIds != null
                && timelineInactiveIds.ContainsKey(rec.RecordingId);
        }

        private void PurgeTimelineInactiveDeferredQueues(
            IReadOnlyDictionary<string, TimelineInactiveReason> timelineInactiveIds)
        {
            if (timelineInactiveIds == null || timelineInactiveIds.Count == 0)
                return;

            var purged = new HashSet<string>();
            foreach (var id in timelineInactiveIds.Keys)
            {
                if (pendingSpawnRecordingIds.Remove(id))
                    purged.Add(id);
                if (pendingFlagReplayRecordingIds.Remove(id))
                    purged.Add(id);
                if (pendingFlagReplayFailureCounts.Remove(id))
                    purged.Add(id);
            }

            bool clearedWatch = !string.IsNullOrEmpty(pendingWatchRecordingId)
                && timelineInactiveIds.ContainsKey(pendingWatchRecordingId);
            if (clearedWatch)
                pendingWatchRecordingId = null;

            if (purged.Count > 0 || clearedWatch)
            {
                ParsekLog.Info("Policy",
                    $"Purged {purged.Count} deferred spawn/flag replay id(s) " +
                    $"timelineInactive; clearedWatch={clearedWatch}");
            }
        }

        private void HandlePlaybackCompleted(PlaybackCompletedEvent evt)
        {
            bool isWatched = host.WatchedRecordingIndex == evt.Index;

            ParsekLog.Verbose("Policy",
                $"PlaybackCompleted index={evt.Index} vessel={evt.Trajectory?.VesselName} " +
                $"ghostWasActive={evt.GhostWasActive} pastEffectiveEnd={evt.PastEffectiveEnd} " +
                $"needsSpawn={evt.Flags.needsSpawn} isMidChain={evt.Flags.isMidChain} watched={isWatched}");

            // Mid-chain segments: hold ghost at final position
            if (evt.Flags.isMidChain && !evt.PastEffectiveEnd)
            {
                // Auto-follow watch mode to next chain segment
                if (isWatched)
                {
                    var committed = RecordingStore.CommittedRecordings;
                    if (evt.Index >= 0 && evt.Index < committed.Count)
                    {
                        int nextTarget = host.FindNextWatchTargetFromPolicy(evt.Index, committed[evt.Index]);
                        if (nextTarget >= 0)
                        {
                            if (host.TransferWatchToNextSegmentFromPolicy(nextTarget))
                            {
                                ParsekLog.Info("Policy",
                                    $"Mid-chain auto-follow: #{evt.Index} → #{nextTarget}");
                            }
                            else
                            {
                                float deferredHoldSeconds = DeferredWatchTransferHoldSeconds;
                                host.StartWatchHoldFromPolicy(Time.time + deferredHoldSeconds);
                                ParsekLog.Info("Policy",
                                    $"Mid-chain watch transfer deferred: #{evt.Index} → #{nextTarget} " +
                                    $"target ghost not active yet, retrying for {deferredHoldSeconds:F0}s");
                            }
                        }
                        else
                        {
                            // Next chain segment ghost hasn't spawned yet — set a hold timer
                            // so UpdateWatchCamera retries FindNextWatchTarget every frame.
                            // Without this, the camera stays stuck on the stale ghost position
                            // indefinitely (no retry mechanism, no hold timer).
                            float holdSeconds = DeferredWatchTransferHoldSeconds;
                            host.StartWatchHoldFromPolicy(Time.time + holdSeconds);
                            ParsekLog.Info("Policy",
                                $"Mid-chain watch hold started for #{evt.Index}: " +
                                $"next chain ghost not spawned yet, retrying for {holdSeconds:F0}s");
                        }
                    }
                }
                return;
            }

            // Spawn vessel if needed
            bool spawned = false;
            if (evt.Flags.needsSpawn && evt.PastEffectiveEnd)
            {
                bool isWarp = GhostPlaybackEngine.IsAnyWarpActiveFromGlobals();
                if (isWarp)
                {
                    pendingSpawnRecordingIds.Add(evt.Flags.recordingId);
                    if (isWatched)
                    {
                        pendingWatchRecordingId = evt.Flags.recordingId;
                        host.ExitWatchModeFromPolicy();
                    }

                    // Hold ghost during warp-deferred spawn (#96)
                    if (evt.GhostWasActive)
                    {
                        heldGhosts[evt.Index] = new HeldGhostInfo
                        {
                            holdStartTime = Time.time,
                            recordingId = evt.Flags.recordingId,
                            vesselName = evt.Trajectory?.VesselName,
                        };
                        ParsekLog.Info("Policy",
                            $"Ghost held during warp-deferred spawn: #{evt.Index} \"{evt.Trajectory?.VesselName}\" " +
                            $"id={evt.Flags.recordingId}");
                    }

                    ParsekLog.Info("Policy",
                        $"Deferred spawn during warp: #{evt.Index} \"{evt.Trajectory?.VesselName}\"");
                    return; // Do not destroy ghost — held during warp
                }
                else
                {
                    var committed = RecordingStore.CommittedRecordings;
                    if (evt.Index >= 0 && evt.Index < committed.Count)
                    {
                        // If watching, exit watch mode and switch to spawned vessel
                        if (isWatched)
                            host.ExitWatchModeFromPolicy();

                        host.SpawnVesselOrChainTipFromPolicy(committed[evt.Index], evt.Index);

                        // Check if spawn actually succeeded
                        spawned = committed[evt.Index].VesselSpawned;

                        // If spawn succeeded, switch camera to spawned vessel if we were watching
                        if (spawned && isWatched)
                        {
                            uint spawnedPid = committed[evt.Index].SpawnedVesselPersistentId;
                            if (spawnedPid != 0)
                                host.DeferredActivateVesselFromPolicy(spawnedPid);
                        }

                        // If spawn was blocked, hold the ghost (#96)
                        if (!spawned && evt.GhostWasActive)
                        {
                            DiagnosticsState.health.spawnFailures++;
                            heldGhosts[evt.Index] = new HeldGhostInfo
                            {
                                holdStartTime = Time.time,
                                recordingId = evt.Flags.recordingId,
                                vesselName = evt.Trajectory?.VesselName,
                                };
                            ParsekLog.Info("Policy",
                                $"Ghost held pending spawn retry: #{evt.Index} \"{evt.Trajectory?.VesselName}\" " +
                                $"id={evt.Flags.recordingId} — spawn blocked, ghost stays visible");
                            return; // Do not destroy the ghost
                        }
                    }
                }
            }

            // Destroy ghost (held paths returned early above)
            if (evt.GhostWasActive)
            {
                // If watching and not spawning: try auto-follow to next stage, else hold
                if (isWatched && !spawned && !evt.Flags.needsSpawn)
                {
                    // Try to find a continuation (tree branch or chain) to auto-follow
                    var committed = RecordingStore.CommittedRecordings;
                    if (evt.Index >= 0 && evt.Index < committed.Count)
                    {
                        int nextTarget = host.FindNextWatchTargetFromPolicy(evt.Index, committed[evt.Index]);
                        if (nextTarget >= 0)
                        {
                            if (host.TransferWatchToNextSegmentFromPolicy(nextTarget))
                            {
                                ParsekLog.Info("Policy",
                                    $"Auto-follow on completion: #{evt.Index} → #{nextTarget} " +
                                    $"(vessel={committed[nextTarget].VesselName})");
                                engine.DestroyGhost(evt.Index, evt.Trajectory, evt.Flags,
                                    reason: "auto-followed to next stage");
                            }
                            else
                            {
                                float deferredHoldSeconds = DeferredWatchTransferHoldSeconds;
                                host.StartWatchHoldFromPolicy(Time.time + deferredHoldSeconds);
                                ParsekLog.Info("Policy",
                                    $"Auto-follow on completion deferred: #{evt.Index} → #{nextTarget} " +
                                    $"target ghost not active yet, retrying for {deferredHoldSeconds:F0}s");
                            }
                            return;
                        }
                    }

                    // No continuation found — hold ghost for camera (3-5 real seconds).
                    // Pending-activation holds also gate expiry on game UT inside WatchModeController
                    // so warp-rate changes do not expire the hold before the continuation can spawn.
                    float holdSeconds = evt.Trajectory?.TerminalStateValue == TerminalState.Destroyed
                        ? 5f : 3f;
                    float holdStartedRealTime = Time.time;
                    float holdUntilRealTime = holdStartedRealTime + holdSeconds;
                    float holdMaxRealTime = holdUntilRealTime;
                    string holdDetail = null;
                    double pendingContinuationUT = double.NaN;
                    var scenarioForActivation = ParsekScenario.Instance;
                    var supersedesForActivation = object.ReferenceEquals(null, scenarioForActivation)
                        ? null : scenarioForActivation.RecordingSupersedes;
                    if (evt.Index >= 0 && evt.Index < committed.Count
                        && GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                            committed[evt.Index],
                            committed,
                            RecordingStore.CommittedTrees,
                            engine.HasActiveGhost,
                            out pendingContinuationUT,
                            supersedesForActivation))
                    {
                        GhostPlaybackLogic.ComputePendingWatchHoldWindow(
                            holdSeconds,
                            holdStartedRealTime,
                            evt.CurrentUT,
                            pendingContinuationUT,
                            TimeWarp.CurrentRate,
                            out holdUntilRealTime,
                            out holdMaxRealTime);
                        float extendedHold = holdUntilRealTime - holdStartedRealTime;
                        if (extendedHold > holdSeconds)
                        {
                            holdSeconds = extendedHold;
                            holdDetail = string.Format(
                                CultureInfo.InvariantCulture,
                                " pendingContinuationUT={0:F1} currentUT={1:F1}",
                                pendingContinuationUT,
                                evt.CurrentUT);
                        }
                    }
                    host.StartWatchHoldFromPolicy(holdUntilRealTime, pendingContinuationUT, holdMaxRealTime);

                    // Trigger explosion if terminal was Destroyed
                    engine.TriggerExplosionIfDestroyed(evt.State, evt.Trajectory, evt.Index,
                        TimeWarp.CurrentRate);

                    ParsekLog.Info("Policy",
                        $"Watch hold started for #{evt.Index}: {holdSeconds:F0}s " +
                        $"terminal={evt.Trajectory?.TerminalStateValue}" +
                        (holdDetail ?? string.Empty));
                    // Ghost stays alive — ParsekFlight's watch hold timer will destroy it
                    return;
                }

                engine.DestroyGhost(evt.Index, evt.Trajectory, evt.Flags, reason: "playback completed");
            }
        }

        /// <summary>
        /// Retries spawn for ghosts that are held at their final position while waiting
        /// for a blocked/deferred spawn to resolve. Called each frame after the engine
        /// update. On success or timeout, destroys the ghost.
        /// </summary>
        internal void RetryHeldGhostSpawns()
        {
            if (heldGhosts.Count == 0) return;

            var committed = RecordingStore.CommittedRecordings;
            var timelineInactiveIds = TimelineInactiveIdsOverrideForTesting != null
                ? TimelineInactiveIdsOverrideForTesting(committed)
                : CurrentTimelineInactiveRecordingReasons(committed);
            float now = CurrentRealTimeOverrideForTesting != null
                ? CurrentRealTimeOverrideForTesting()
                : CurrentUnityRealTime();
            double currentUT = CurrentUTOverrideForTesting != null
                ? CurrentUTOverrideForTesting()
                : CurrentUniversalTimeSafe();

            // Collect indices to release and retry-time updates (cannot modify dict during iteration)
            List<KeyValuePair<int, string>> toRelease = null;  // index + destroy reason
            List<KeyValuePair<int, float>> retryTimeUpdates = null;

            foreach (var kvp in heldGhosts)
            {
                int index = kvp.Key;
                HeldGhostInfo info = kvp.Value;

                var decision = DecideHeldGhostAction(
                    index, info, committed, now, HeldGhostTimeoutSeconds,
                    HeldGhostRetryIntervalSeconds,
                    timelineInactiveIds,
                    currentUT);

                switch (decision)
                {
                    case HeldGhostAction.RetrySpawn:
                        // Recording is valid and not yet spawned — retry
                        DiagnosticsState.health.spawnRetries++;
                        if (retryTimeUpdates == null) retryTimeUpdates = new List<KeyValuePair<int, float>>();
                        retryTimeUpdates.Add(new KeyValuePair<int, float>(index, now));
                        if (SpawnVesselOrChainTipOverrideForTesting != null)
                            SpawnVesselOrChainTipOverrideForTesting(committed[index], index);
                        else
                            host.SpawnVesselOrChainTipFromPolicy(committed[index], index);
                        if (committed[index].VesselSpawned)
                        {
                            ParsekLog.Info("Policy",
                                $"Held ghost spawn succeeded on retry: #{index} \"{info.vesselName}\" " +
                                $"id={info.recordingId} held={now - info.holdStartTime:F1}s");

                            // If user is currently watching this recording, activate the spawned vessel
                            if (host.WatchedRecordingIndex == index)
                            {
                                uint spawnedPid = committed[index].SpawnedVesselPersistentId;
                                if (spawnedPid != 0)
                                {
                                    if (DeferredActivateVesselOverrideForTesting != null)
                                        DeferredActivateVesselOverrideForTesting(spawnedPid);
                                    else
                                        host.DeferredActivateVesselFromPolicy(spawnedPid);
                                }
                            }

                            if (toRelease == null) toRelease = new List<KeyValuePair<int, string>>();
                            toRelease.Add(new KeyValuePair<int, string>(index, "held-spawn-succeeded"));
                        }
                        break;

                    case HeldGhostAction.ReleaseSpawned:
                        // Already spawned by another path (e.g. FlushDeferredSpawns)
                        ParsekLog.Info("Policy",
                            $"Held ghost released (already spawned): #{index} \"{info.vesselName}\" " +
                            $"id={info.recordingId} held={now - info.holdStartTime:F1}s");
                        if (toRelease == null) toRelease = new List<KeyValuePair<int, string>>();
                        toRelease.Add(new KeyValuePair<int, string>(index, "held-already-spawned"));
                        break;

                    case HeldGhostAction.ReleaseSupersededByRelation:
                        ParsekLog.Info("Policy",
                            $"Held ghost released (superseded by relation): #{index} \"{info.vesselName}\" " +
                            $"id={info.recordingId} held={now - info.holdStartTime:F1}s");
                        if (toRelease == null) toRelease = new List<KeyValuePair<int, string>>();
                        toRelease.Add(new KeyValuePair<int, string>(index, "held-superseded-by-relation"));
                        break;

                    case HeldGhostAction.ReleaseRewindRetired:
                        ParsekLog.Info("Policy",
                            $"Held ghost released (rewind retired): #{index} \"{info.vesselName}\" " +
                            $"id={info.recordingId} held={now - info.holdStartTime:F1}s");
                        if (toRelease == null) toRelease = new List<KeyValuePair<int, string>>();
                        toRelease.Add(new KeyValuePair<int, string>(index, "held-rewind-retired"));
                        break;

                    case HeldGhostAction.Timeout:
                        ParsekLog.Warn("Policy",
                            $"Held ghost timed out: #{index} \"{info.vesselName}\" " +
                            $"id={info.recordingId} held={now - info.holdStartTime:F1}s " +
                            $"— destroying ghost without spawn");
                        if (toRelease == null) toRelease = new List<KeyValuePair<int, string>>();
                        toRelease.Add(new KeyValuePair<int, string>(index, "held-spawn-timeout"));
                        break;

                    case HeldGhostAction.InvalidIndex:
                        ParsekLog.Warn("Policy",
                            $"Held ghost has invalid index: #{index} \"{info.vesselName}\" " +
                            $"id={info.recordingId} — releasing");
                        if (toRelease == null) toRelease = new List<KeyValuePair<int, string>>();
                        toRelease.Add(new KeyValuePair<int, string>(index, "held-invalid-index"));
                        break;

                    case HeldGhostAction.Hold:
                        // Keep waiting
                        break;
                }
            }

            // Apply retry-time updates (deferred to avoid dict mutation during iteration)
            if (retryTimeUpdates != null)
            {
                for (int i = 0; i < retryTimeUpdates.Count; i++)
                {
                    int idx = retryTimeUpdates[i].Key;
                    if (heldGhosts.TryGetValue(idx, out var updated))
                    {
                        updated.lastRetryTime = retryTimeUpdates[i].Value;
                        heldGhosts[idx] = updated;
                    }
                }
            }

            if (toRelease != null)
            {
                for (int i = 0; i < toRelease.Count; i++)
                {
                    int index = toRelease[i].Key;
                    string reason = toRelease[i].Value;
                    if (DestroyGhostOverrideForTesting != null)
                        DestroyGhostOverrideForTesting(index, reason);
                    else
                        engine.DestroyGhost(index, reason: reason);
                    heldGhosts.Remove(index);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float CurrentUnityRealTime()
        {
            return Time.time;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double CurrentUniversalTimeSafe()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch
            {
                return double.NaN;
            }
        }

        /// <summary>
        /// Pure decision logic for held ghost retry. Determines what action to take
        /// for a held ghost based on current state. Testable without Unity side effects.
        /// </summary>
        internal static HeldGhostAction DecideHeldGhostAction(
            int index, HeldGhostInfo info, IReadOnlyList<Recording> committed,
            float currentTime, float timeoutSeconds,
            float retryIntervalSeconds = 1.0f,
            IReadOnlyDictionary<string, TimelineInactiveReason> timelineInactiveIds = null,
            double currentUT = double.NaN)
        {
            // Invalid index — recording list may have changed
            if (index < 0 || index >= committed.Count)
                return HeldGhostAction.InvalidIndex;

            var rec = committed[index];

            // Verify this is still the same recording (indices can shift after deletes)
            if (rec.RecordingId != info.recordingId)
                return HeldGhostAction.InvalidIndex;

            if (!string.IsNullOrEmpty(rec.RecordingId)
                && timelineInactiveIds != null
                && timelineInactiveIds.TryGetValue(rec.RecordingId, out TimelineInactiveReason inactiveReason))
            {
                if (inactiveReason == TimelineInactiveReason.RewindRetired)
                    return HeldGhostAction.ReleaseRewindRetired;
                if (inactiveReason == TimelineInactiveReason.SupersededByRelation)
                    return HeldGhostAction.ReleaseSupersededByRelation;
            }

            // Already spawned by another path
            if (rec.VesselSpawned)
                return HeldGhostAction.ReleaseSpawned;

            TerminalOrbitDeferredSpawnState terminalDeferredState =
                TerminalOrbitSpawnSafety.GetDeferredSpawnState(
                    rec,
                    currentUT,
                    out _);
            if (terminalDeferredState == TerminalOrbitDeferredSpawnState.Hold)
                return HeldGhostAction.Hold;

            // Timeout check
            float elapsed = currentTime - info.holdStartTime;
            if (terminalDeferredState != TerminalOrbitDeferredSpawnState.Ready
                && elapsed >= timeoutSeconds)
                return HeldGhostAction.Timeout;

            // Throttle retry attempts — avoid hammering spawn every frame
            float sinceLast = currentTime - info.lastRetryTime;
            if (sinceLast < retryIntervalSeconds)
                return HeldGhostAction.Hold;

            return HeldGhostAction.RetrySpawn;
        }

        /// <summary>
        /// Recording indices eligible for ghost map ProtoVessels but deferred until
        /// the ghost enters an orbital segment. Avoids showing orbit lines during
        /// atmospheric ascent when the ghost mesh is still on the pad.
        /// </summary>
        private struct PendingMapVessel
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

        private readonly Dictionary<int, PendingMapVessel> pendingMapVessels =
            new Dictionary<int, PendingMapVessel>();

        private readonly Dictionary<int, string> soiGapStateVectorExpectedBodies =
            new Dictionary<int, string>();

        /// <summary>
        /// Tracks the last orbit segment body+SMA per recording index for change detection.
        /// Used to update the ghost ProtoVessel orbit as the ghost traverses segments.
        /// </summary>
        private const float MapOrbitUpdateIntervalSec = 0.5f;
        private float nextMapOrbitUpdateTime;

        private readonly Dictionary<int, (string body, double sma, double ecc)> lastMapOrbitByIndex =
            new Dictionary<int, (string body, double sma, double ecc)>();

        private readonly HashSet<string> terminalMapRetentionLoggedIds =
            new HashSet<string>();

        /// <summary>
        /// Per-chain dedup: maps chainId → recording index that currently owns the ghost map vessel.
        /// When a new chain segment creates a ghost map vessel, the previous segment's is removed.
        /// Prevents duplicate orbit lines during fast time warp across chain boundaries.
        /// </summary>
        private readonly Dictionary<string, int> chainMapOwner = new Dictionary<string, int>();

        /// <summary>
        /// State-vector orbit tracking: recording indices with physics-only suborbital
        /// orbit lines (no orbit segments). Maps index → trajectory for re-defer.
        /// </summary>
        private readonly Dictionary<int, IPlaybackTrajectory> stateVectorOrbitTrajectories =
            new Dictionary<int, IPlaybackTrajectory>();

        /// <summary>
        /// Per-recording cached waypoint indices for InterpolateAtUT calls (avoids
        /// O(n) scan on every 0.5s orbit update).
        /// </summary>
        private readonly Dictionary<int, int> stateVectorCachedIndices =
            new Dictionary<int, int>();

        // Hysteresis thresholds for state-vector orbit creation/removal.
        // Higher thresholds to create, lower to remove — prevents churn for borderline altitudes.
        // On bodies with atmosphere, the atmosphere depth overrides these (orbit lines are
        // only meaningful in vacuum — atmospheric drag makes the Keplerian approximation wild).
        internal const double StateVectorCreateAltitude = GhostMapPresence.StateVectorCreateAltitude;
        internal const double StateVectorCreateSpeed = GhostMapPresence.StateVectorCreateSpeed;
        internal const double StateVectorRemoveAltitude = GhostMapPresence.StateVectorRemoveAltitude;
        internal const double StateVectorRemoveSpeed = GhostMapPresence.StateVectorRemoveSpeed;

        private void HandleGhostCreated(GhostLifecycleEvent evt)
        {
            // Chain-seam auto-follow: if this is a chain-seam first-spawn AND the watched
            // recording is the same-chain predecessor, transfer the camera to the successor
            // immediately. The standard auto-follow path (HandlePlaybackCompleted -> Mid-chain
            // auto-follow) does NOT fire for chain-head predecessors whose Recording.EndUT is
            // widened by ExplicitEndUT / orbit-tail projection past the actual seam UT — the
            // engine's pastEnd check (currentUT > traj.EndUT) stays false, PlaybackCompleted
            // never fires, the policy never transfers, and the camera stays glued to the
            // predecessor while the successor is rendering correctly somewhere else (the
            // "duplicate ghost suspended in air" the Kerbal X playtest 2026-05-19 reproduced).
            TryAutoFollowChainSeamSpawn(evt);

            // KEEP debris-only: this is the policy decision to NOT give debris
            // recordings their own tracking-station map presence / orbit lines.
            // Controlled-decoupled children (extension of the parent-anchor
            // contract) carry IsDebris=false and correctly pass this gate so
            // they continue to receive map presence as today.
            if (evt.Trajectory == null || evt.Trajectory.IsDebris)
            {
                if (evt.Trajectory?.IsDebris == true)
                    ParsekLog.Verbose("Policy",
                        $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory?.VesselName}\" - debris");
                return;
            }

            var terminal = evt.Trajectory.TerminalStateValue;
            if (!GhostMapPresence.IsTerminalStateEligibleForMapPresence(terminal))
            {
                ParsekLog.VerboseRateLimited("Policy", $"skip-map-terminal-{evt.Index}",
                    $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory.VesselName}\" — terminal={terminal.Value}");
                return;
            }

            // Accept recordings with terminal orbit data, orbit segments, or trajectory
            // points (physics-only suborbital recordings have points but no orbit data).
            bool hasOrbitData = GhostMapPresence.HasOrbitData(evt.Trajectory);
            bool hasOrbitSegments = evt.Trajectory.HasOrbitSegments;
            bool hasPoints = evt.Trajectory.Points != null && evt.Trajectory.Points.Count > 0;
            if (!hasOrbitData && !hasOrbitSegments && !hasPoints)
                return;

            // Per-chain dedup: if another segment from the same chain already has a ghost
            // map vessel, remove it before creating the new one. Prevents duplicate orbit
            // lines during fast time warp across chain boundaries.
            RemovePreviousChainMapVessel(evt.Index);

            // Check whether the ghost starts in a map-visible source. Otherwise,
            // defer — the per-frame shared resolver will create it when it enters
            // an orbital segment or state-vector fallback range.
            double startUT = evt.Trajectory.StartUT;
            int cachedStateVectorIndex = stateVectorCachedIndices.TryGetValue(evt.Index, out int cached)
                ? cached
                : -1;
            TrackingStationGhostSource source = GhostMapPresence.ResolveMapPresenceGhostSource(
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
                recordingIndex: evt.Index);
            stateVectorCachedIndices[evt.Index] = cachedStateVectorIndex;

            if (source != TrackingStationGhostSource.None)
            {
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    evt.Index,
                    evt.Trajectory,
                    source,
                    segment,
                    stateVectorPoint,
                    startUT);
                if (ghost != null)
                {
                    if (GhostMapPresence.IsStateVectorGhostSource(source))
                    {
                        stateVectorOrbitTrajectories[evt.Index] = evt.Trajectory;
                        if (source != TrackingStationGhostSource.StateVectorSoiGap)
                            soiGapStateVectorExpectedBodies.Remove(evt.Index);
                    }
                    else if (TryGetMapOrbitKey(source, segment, out var orbitKey))
                    {
                        soiGapStateVectorExpectedBodies.Remove(evt.Index);
                        lastMapOrbitByIndex[evt.Index] = orbitKey;
                    }
                }
            }
            else
            {
                // Initial/pre-orbital deferrals are not SOI-gap recoveries; only the
                // gap-between-orbit-segments requeue path opts into this fallback.
                pendingMapVessels[evt.Index] = new PendingMapVessel(
                    evt.Trajectory,
                    allowSoiGapStateVectorFallback: false,
                    expectedSoiGapBody: null);
                ParsekLog.Verbose("Policy",
                    $"Deferred ghost map vessel for #{evt.Index} \"{evt.Trajectory.VesselName}\" " +
                    "— recording starts pre-orbital");
            }
        }

        /// <summary>
        /// Pure: should we create a state-vector orbit ProtoVessel at this altitude/speed?
        /// On bodies with atmosphere, requires altitude above atmosphere (Keplerian orbits
        /// are meaningless during atmospheric flight — drag makes them wild/flickering).
        /// On airless bodies, uses the fixed altitude threshold.
        /// </summary>
        internal static bool ShouldCreateStateVectorOrbit(double altitude, double speed, double atmosphereDepth)
        {
            return GhostMapPresence.ShouldCreateStateVectorOrbit(altitude, speed, atmosphereDepth);
        }

        /// <summary>
        /// Pure decision predicate: should the policy auto-follow the watch camera onto a
        /// freshly spawned chain-seam successor? True iff the spawn was flagged as a chain
        /// seam, an active watch exists, the watch is not already on the new spawn, and the
        /// new spawn's chain predecessor IS the currently watched recording. The chain-topology
        /// half stays single-sourced in <c>RecordingStore.GetChainPredecessorIndex</c>; this
        /// predicate composes that with the spawn-time signal and the watch state.
        /// <para>
        /// <b>Gates intentionally NOT added (Opus PR review 2026-05-19):</b>
        /// </para>
        /// <list type="bullet">
        /// <item><b>Watch-hold timer active</b>: the hold timer (set by <c>HandlePlaybackCompleted</c>
        /// when a mid-chain transfer defers and consumed by <c>ProcessWatchEndHoldTimer</c>'s
        /// per-frame retry) IS the retry-handoff path for the same seam this predicate handles.
        /// Auto-follow during the hold is the desired-earlier version of the per-frame retry —
        /// gating on the hold would just add 1 frame of latency before the retry catches up to
        /// the same transfer. <c>TransferWatchToNextSegmentFromPolicy</c> clears the hold on
        /// success (<c>WatchModeController.cs:2738</c>).</item>
        /// <item><b>Time warp active</b>: the existing <c>Mid-chain auto-follow</c> path
        /// (<c>HandlePlaybackCompleted</c> mid-chain branch above) does not gate on warp either;
        /// the warp-special branch only triggers for <c>needsSpawn &amp;&amp; PastEffectiveEnd</c>
        /// (end-of-chain real-vessel spawn), not for mid-chain segment handoffs. Gating only the
        /// new path would diverge from the existing path's behavior; gating both is a larger
        /// scope decision out of scope for this PR.</item>
        /// <item><b>Pre-switch decision dialog up</b>: MergeDialog runs from MAP / TS contexts
        /// (rapid-switch flows, scene-exit Merge / Discard); flight-scene Watch and the dialog
        /// path do not realistically overlap. No accessor exposes "any dialog open" today.</item>
        /// </list>
        /// </summary>
        internal static bool ShouldAutoFollowChainSeamSpawn(
            bool spawnedAtChainSeam,
            int watchedIndex,
            int spawnIndex,
            int predecessorIndexOfSpawn)
        {
            if (!spawnedAtChainSeam) return false;
            if (watchedIndex < 0) return false;
            if (watchedIndex == spawnIndex) return false;
            return predecessorIndexOfSpawn == watchedIndex;
        }

        /// <summary>
        /// At chain-seam first-spawn, transfer the watch camera from the same-chain
        /// predecessor to this new successor. The standard <c>HandlePlaybackCompleted</c>
        /// -&gt; <c>Mid-chain auto-follow</c> path does not fire for chain-head predecessors
        /// whose <c>Recording.EndUT</c> is widened past the actual seam by
        /// <c>ExplicitEndUT</c> / orbit-tail projection / Re-Fly origin-split residue.
        /// The engine's <c>pastEnd = currentUT &gt; traj.EndUT</c> check stays false in
        /// that case, so the policy never sees a completion event for the predecessor and
        /// the camera stays glued to the now-frozen predecessor while the successor
        /// renders correctly somewhere else (the "duplicate ghost suspended in air"
        /// symptom). The seam-spawn flag is a direct, predecessor-EndUT-independent
        /// signal that the chain handoff is happening, so we transfer on it.
        /// <para>
        /// <b>Engine timing contract relied on:</b> <c>QueueOrEmitGhostCreated</c>
        /// (<c>GhostPlaybackEngine.cs</c>) defers the Created event into
        /// <c>deferredCreatedEvents</c> whenever <c>updateStopwatch.IsRunning</c>; the deferred
        /// pump flushes Created events BEFORE Completed events, AFTER
        /// <c>FinalizePendingSpawnLifecycle</c> has populated <c>state.ghost</c>,
        /// <c>state.cameraPivot</c>, and <c>state.horizonProxy</c>. By the time this method
        /// runs, the new ghost is fully built and <c>TransferWatchToNextSegmentFromPolicy</c>
        /// can target it. <c>TransferWatchToNextSegment</c> (<c>WatchModeController.cs:2660</c>)
        /// independently re-checks <c>gs.ghost != null</c> and falls back to deferred-retry
        /// if the contract is ever violated. No double-transfer risk: the new path runs first
        /// and flips <c>WatchedRecordingIndex</c> to the successor; when <c>HandlePlaybackCompleted</c>
        /// then fires for the predecessor (if ever — see EndUT-widening note above), the
        /// <c>isWatched = host.WatchedRecordingIndex == evt.Index</c> test is now false so the
        /// Mid-chain branch skips.
        /// </para>
        /// </summary>
        private void TryAutoFollowChainSeamSpawn(GhostLifecycleEvent evt)
        {
            if (evt == null || evt.State == null) return;
            if (evt.Trajectory == null) return;

            int watchedIndex = host.WatchedRecordingIndex;
            var committed = RecordingStore.CommittedRecordings;
            if (evt.Index < 0 || evt.Index >= committed.Count) return;
            Recording successor = committed[evt.Index];
            if (successor == null) return;
            int predIdxForSuccessor = RecordingStore.GetChainPredecessorIndex(successor);

            if (!ShouldAutoFollowChainSeamSpawn(
                evt.State.spawnedAtChainSeam, watchedIndex, evt.Index, predIdxForSuccessor))
            {
                return;
            }

            if (watchedIndex >= committed.Count) return;
            Recording watched = committed[watchedIndex];
            if (watched == null) return;

            if (host.TransferWatchToNextSegmentFromPolicy(evt.Index))
            {
                ParsekLog.Info("Policy",
                    $"Chain-seam auto-follow: #{watchedIndex} \"{watched.VesselName}\" -> " +
                    $"#{evt.Index} \"{successor.VesselName}\" " +
                    "(seam-spawn while predecessor watched; predecessor EndUT does not gate PlaybackCompleted)");
            }
            else
            {
                ParsekLog.VerboseRateLimited("Policy", $"chain-seam-transfer-failed-{evt.Index}",
                    $"Chain-seam auto-follow declined: #{watchedIndex} -> #{evt.Index} " +
                    "(TransferWatchToNextSegmentFromPolicy returned false)",
                    1.0);
            }
        }

        /// <summary>
        /// Pure: should we remove a state-vector orbit ProtoVessel at this altitude/speed?
        /// On bodies with atmosphere, removes immediately when descending into atmosphere.
        /// On airless bodies, uses lower hysteresis thresholds.
        /// </summary>
        internal static bool ShouldRemoveStateVectorOrbit(double altitude, double speed, double atmosphereDepth)
        {
            return GhostMapPresence.ShouldRemoveStateVectorOrbit(altitude, speed, atmosphereDepth);
        }

        /// <summary>
        /// Look up atmosphere depth for a body by name. Returns 0 for airless bodies or if
        /// FlightGlobals is unavailable (test environment).
        /// </summary>
        internal static double GetAtmosphereDepth(string bodyName)
        {
            return GhostMapPresence.GetAtmosphereDepth(bodyName);
        }

        /// <summary>
        /// Pure: is the recording in a RELATIVE reference frame at the given UT?
        /// RELATIVE frames store dx/dy/dz offsets, not lat/lon/alt — state-vector
        /// orbit construction would produce nonsense.
        /// </summary>
        internal static bool IsInRelativeFrame(IPlaybackTrajectory traj, double ut)
        {
            return GhostMapPresence.IsInRelativeFrame(traj, ut);
        }

        internal static bool ShouldRetainMapPresenceForTerminalRealSpawn(
            Recording rec,
            bool hasFutureSegment)
        {
            if (hasFutureSegment || rec == null)
                return false;

            // KEEP debris-only: `rec.IsDebris` here is one of several semantic
            // gates that exclude debris from terminal-real-spawn map presence
            // retention. Controlled-decoupled children (extension of the parent-
            // anchor contract) carry IsDebris=false and correctly pass this gate.
            if (rec.VesselSpawned
                || rec.SpawnAbandoned
                || rec.VesselSnapshot == null
                || rec.IsDebris
                || rec.IsGhostOnly
                || rec.ChainBranch > 0)
            {
                return false;
            }

            if (rec.TerminalStateValue != TerminalState.Orbiting)
                return false;

            if (!string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId)
                || rec.SpawnSuppressedByRewind)
            {
                return false;
            }

            return VesselSpawner.HasRecordedTerminalOrbit(rec)
                || rec.HasOrbitSegments
                || TerminalOrbitSpawnSafety.HasActiveHold(rec);
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
        private double ResolveMapPresenceSampleUT(
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
        /// Per-frame check for ghost map ProtoVessels. Handles three responsibilities:
        /// 1. Creates deferred ProtoVessels when ghosts enter their first orbital segment
        /// 2. Creates state-vector orbit ProtoVessels for physics-only suborbital recordings
        /// 3. Updates existing ProtoVessel orbits (segment-based and state-vector-based)
        /// </summary>
        internal void CheckPendingMapVessels(double currentUT)
        {
            // Mission-loop span clock: the same per-frame LoopUnitSet the flight engine drives the
            // ghost MESHES with (engine.CurrentLoopUnits). The Tracking Station and KSC map drivers
            // already remap the live UT through this clock; this is the flight-scene equivalent so
            // a looped Mission's map orbit lines + icons sample the recording at the loop-mapped
            // effUT instead of the raw live UT (which is far outside a looped member's recorded UT
            // range). Empty (the common case) makes every ResolveMapPresenceSampleUT below return
            // the unchanged live UT with shift 0, so non-looping behavior is byte-identical.
            GhostPlaybackLogic.LoopUnitSet loopUnits = engine.CurrentLoopUnits;

            // 1. Create deferred ProtoVessels for ghosts that just entered an orbital segment
            //    OR for physics-only recordings that crossed the state-vector threshold.
            if (pendingMapVessels.Count > 0)
            {
                List<(int idx, TrackingStationGhostSource source, OrbitSegment segment, TrajectoryPoint point, string expectedBody, double effUT)> toCreate = null;

                foreach (var kvp in pendingMapVessels)
                {
                    int idx = kvp.Key;
                    PendingMapVessel pending = kvp.Value;
                    IPlaybackTrajectory traj = pending.Trajectory;

                    // Loop-mapped sample UT for this member (effUT == currentUT off the loop path).
                    // renderHidden => this member is outside its loop window this cycle: skip the
                    // create, exactly like the Tracking Station create pass skips it.
                    double effUT = ResolveMapPresenceSampleUT(
                        idx, traj.StartUT, traj.EndUT, currentUT, loopUnits,
                        out bool renderHidden, out _);
                    if (renderHidden)
                        continue;

                    int cachedStateVectorIndex = stateVectorCachedIndices.TryGetValue(idx, out int cached)
                        ? cached
                        : -1;
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
                        expectedSoiGapBody: pending.ExpectedSoiGapBody);
                    stateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    if (source == TrackingStationGhostSource.Segment
                        || GhostMapPresence.IsStateVectorGhostSource(source)
                        || source == TrackingStationGhostSource.TerminalOrbit)
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
                        if (pendingMapVessels.TryGetValue(idx, out var pending))
                        {
                            IPlaybackTrajectory traj = pending.Trajectory;
                            // Per-chain dedup before deferred creation
                            RemovePreviousChainMapVessel(idx);
                            // Create at the loop-mapped effUT so the source/segment/point match the
                            // looped phase (effUT == currentUT off the loop path). The orbit epoch is
                            // not yet shifted here; the rate-limited update pass below (every
                            // MapOrbitUpdateIntervalSec ~0.5s) re-seeds it with the live-frame epoch
                            // shift, so a freshly (re)materialized loop ghost can show the wrong
                            // orbital phase for up to ~0.5s after each window re-entry before it
                            // snaps to the replayed position.
                            Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                                idx,
                                traj,
                                toCreate[i].source,
                                toCreate[i].segment,
                                toCreate[i].point,
                                toCreate[i].effUT,
                                out bool retryLater);
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
                            pendingMapVessels.Remove(idx);

                            if (ghost != null)
                            {
                                if (GhostMapPresence.IsStateVectorGhostSource(toCreate[i].source))
                                {
                                    stateVectorOrbitTrajectories[idx] = traj;
                                    if (toCreate[i].source == TrackingStationGhostSource.StateVectorSoiGap)
                                        soiGapStateVectorExpectedBodies[idx] = toCreate[i].expectedBody;
                                    else
                                        soiGapStateVectorExpectedBodies.Remove(idx);
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
                                    soiGapStateVectorExpectedBodies.Remove(idx);
                                    lastMapOrbitByIndex[idx] = orbitKey;

                                    ParsekLog.Info("Policy",
                                        $"Created deferred ghost map vessel for #{idx} \"{traj.VesselName}\" " +
                                        $"— source={toCreate[i].source} body={orbitKey.body} sma={orbitKey.sma:F0}");
                                }
                            }
                        }
                    }
                }
            }

            // 2. Rate-limited orbit updates for existing ProtoVessels.
            if (UnityEngine.Time.time < nextMapOrbitUpdateTime) return;
            nextMapOrbitUpdateTime = UnityEngine.Time.time + MapOrbitUpdateIntervalSec;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return;
            PruneTerminalMapRetentionLogKeys(committed);

            // 2a. Segment-based orbit updates (existing)
            List<KeyValuePair<int, (string body, double sma, double ecc)>> orbitUpdates = null;
            List<int> toRemoveFromMap = null;
            List<(int idx, string expectedBody)> toRequeue = null;

            foreach (var kvp in lastMapOrbitByIndex)
            {
                int idx = kvp.Key;
                if (idx < 0 || idx >= committed.Count) continue;

                var rec = committed[idx];
                if (!rec.HasOrbitSegments) continue;

                // Loop-mapped sample UT + live-frame epoch shift (effUT == currentUT, shift 0 off
                // the loop path). renderHidden => the member left its loop window this cycle: tear
                // the orbit ghost down, mirroring the Tracking Station refresh pass.
                double effUT = ResolveMapPresenceSampleUT(
                    idx, rec.StartUT, rec.EndUT, currentUT, loopUnits,
                    out bool renderHidden, out double loopEpochShiftSeconds);
                if (renderHidden)
                {
                    GhostMapPresence.RemoveGhostVesselForRecording(idx, "mission-loop-out-of-window");
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

                // Same-body carry: while the playback head is inside a body frame, briefly
                // dropping the ghost between two non-orbit-equivalent segments (e.g., capture
                // burn between two Mun orbits) would tear down and recreate the ProtoVessel,
                // producing the visible flicker the user reported near Mun SOI. We only want
                // to drop the ghost when the body actually changes (SOI / frame change) or
                // when the recording is truly past its last segment. Body-frame carry keeps
                // the previous segment's orbit active until UT enters the next segment.
                OrbitSegment? seg = TrajectoryMath.FindOrbitSegmentOrSameBodyCarry(rec.OrbitSegments, effUT);

                // No map-visible orbit at current UT — either we've truly left orbital
                // playback, or the next segment is in a different SOI/body.
                if (!seg.HasValue)
                {
                    int cachedStateVectorIndex = stateVectorCachedIndices.TryGetValue(idx, out int cached)
                        ? cached
                        : -1;
                    if (TryResolveTerminalFallbackMapOrbitUpdate(
                        rec,
                        idx,
                        effUT,
                        kvp.Value,
                        IsMaterializedForMapPresence(rec),
                        ref cachedStateVectorIndex,
                        out OrbitSegment fallbackSegment,
                        out var fallbackKey,
                        out bool fallbackChanged))
                    {
                        stateVectorCachedIndices[idx] = cachedStateVectorIndex;
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
                    stateVectorCachedIndices[idx] = cachedStateVectorIndex;

                    bool hasFutureSegment = false;
                    string futureSegmentBody = null;
                    var segs = rec.OrbitSegments;
                    for (int s = 0; s < segs.Count; s++)
                    {
                        if (segs[s].startUT > effUT)
                        {
                            hasFutureSegment = true;
                            futureSegmentBody = segs[s].bodyName;
                            break;
                        }
                    }

                    if (ShouldRetainMapPresenceForTerminalRealSpawn(rec, hasFutureSegment))
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
                        if (terminalMapRetentionLoggedIds.Add(retentionKey))
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
                    loopEpochShiftSeconds: loopEpochShiftSeconds);
                if (orbitUpdates == null) orbitUpdates = new List<KeyValuePair<int, (string, double, double)>>();
                orbitUpdates.Add(new KeyValuePair<int, (string, double, double)>(
                    idx, (seg.Value.bodyName, seg.Value.semiMajorAxis, seg.Value.eccentricity)));
            }

            if (orbitUpdates != null)
            {
                for (int i = 0; i < orbitUpdates.Count; i++)
                    lastMapOrbitByIndex[orbitUpdates[i].Key] = orbitUpdates[i].Value;
            }
            if (toRemoveFromMap != null)
            {
                for (int i = 0; i < toRemoveFromMap.Count; i++)
                    lastMapOrbitByIndex.Remove(toRemoveFromMap[i]);
            }
            if (toRequeue != null)
            {
                for (int i = 0; i < toRequeue.Count; i++)
                {
                    int idx = toRequeue[i].idx;
                    if (!pendingMapVessels.ContainsKey(idx) && idx < committed.Count)
                    {
                        var traj = committed[idx] as IPlaybackTrajectory;
                        if (traj != null)
                        {
                            pendingMapVessels[idx] = new PendingMapVessel(
                                traj,
                                allowSoiGapStateVectorFallback: true,
                                expectedSoiGapBody: toRequeue[i].expectedBody);
                            ParsekLog.Verbose("MapPresence",
                                $"Re-queued recording #{idx} to pendingMapVessels (gap between orbit segments expectedBody={toRequeue[i].expectedBody ?? "(none)"})");
                        }
                    }
                }
            }

            // 2b. State-vector orbit updates (new — physics-only suborbital)
            if (stateVectorOrbitTrajectories.Count > 0)
            {
                List<int> toReDefer = null;
                List<int> toExitStateVector = null;
                List<KeyValuePair<int, (string body, double sma, double ecc)>> stateVectorSegmentUpdates = null;

                foreach (var kvp in stateVectorOrbitTrajectories)
                {
                    int idx = kvp.Key;
                    IPlaybackTrajectory traj = kvp.Value;

                    if (!stateVectorCachedIndices.ContainsKey(idx))
                        stateVectorCachedIndices[idx] = -1;
                    int cached = stateVectorCachedIndices[idx];

                    // Loop-mapped sample UT + live-frame epoch shift (effUT == currentUT, shift 0
                    // off the loop path). renderHidden => the member is outside its loop window
                    // this cycle: tear the ghost down and re-defer so the create pass re-materializes
                    // it when the span clock sweeps back into its window.
                    double effUT = ResolveMapPresenceSampleUT(
                        idx, traj.StartUT, traj.EndUT, currentUT, loopUnits,
                        out bool renderHidden, out double loopEpochShiftSeconds);
                    if (renderHidden)
                    {
                        GhostMapPresence.RemoveGhostVesselForRecording(idx, "mission-loop-out-of-window");
                        if (toReDefer == null) toReDefer = new List<int>();
                        toReDefer.Add(idx);
                        continue;
                    }

                    if (soiGapStateVectorExpectedBodies.TryGetValue(idx, out string expectedSoiGapBody))
                    {
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
                            expectedSoiGapBody: expectedSoiGapBody);
                        stateVectorCachedIndices[idx] = cached;

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

                        if (source == TrackingStationGhostSource.Segment
                            || source == TrackingStationGhostSource.TerminalOrbit)
                        {
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
                    stateVectorCachedIndices[idx] = cached;

                    if (!pt.HasValue) continue;

                    // Relative-frame points reuse `TrajectoryPoint.altitude` as the
                    // anchor-local dz offset (metres along the anchor's local z axis),
                    // not as geographic altitude. Feeding the dz into the
                    // `ShouldRemoveStateVectorOrbit` altitude threshold trips the
                    // remove path on every typical rendezvous frame (dz ~ 0) and
                    // re-defers the ghost — but pending-create currently skips
                    // Relative frames, so the ghost disappears through the section
                    // rather than staying attached to the anchor (#547 P1 review).
                    // Skip the threshold check entirely for Relative-frame points;
                    // `UpdateGhostOrbitFromStateVectors` already dispatches on
                    // `referenceFrame` and resolves the world position via
                    // anchor + offset for that branch.
                    bool inRelativeFrame = GhostMapPresence.IsInRelativeFrame(traj, effUT);
                    if (!inRelativeFrame)
                    {
                        double atmosRemove = GetAtmosphereDepth(pt.Value.bodyName);
                        if (ShouldRemoveStateVectorOrbit(pt.Value.altitude, pt.Value.velocity.magnitude, atmosRemove))
                        {
                            GhostMapPresence.RemoveGhostVesselForRecording(idx, "below-state-vector-threshold");
                            if (toReDefer == null) toReDefer = new List<int>();
                            toReDefer.Add(idx);
                            ParsekLog.Info("Policy", string.Format(CultureInfo.InvariantCulture,
                                "Removed state-vector ghost map vessel for #{0} — alt={1:F0} speed={2:F1} below threshold",
                                idx, pt.Value.altitude, pt.Value.velocity.magnitude));
                            continue;
                        }
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
                        if (stateVectorOrbitTrajectories.TryGetValue(idx, out var traj))
                            pendingMapVessels[idx] = new PendingMapVessel(
                                traj,
                                allowSoiGapStateVectorFallback: false,
                                expectedSoiGapBody: null);
                        stateVectorOrbitTrajectories.Remove(idx);
                        soiGapStateVectorExpectedBodies.Remove(idx);
                        // stateVectorCachedIndices[idx] intentionally kept — avoids
                        // O(n) re-scan if the ghost re-ascends above threshold.
                    }
                }

                if (stateVectorSegmentUpdates != null)
                {
                    for (int i = 0; i < stateVectorSegmentUpdates.Count; i++)
                        lastMapOrbitByIndex[stateVectorSegmentUpdates[i].Key] = stateVectorSegmentUpdates[i].Value;
                }

                if (toExitStateVector != null)
                {
                    for (int i = 0; i < toExitStateVector.Count; i++)
                    {
                        int idx = toExitStateVector[i];
                        stateVectorOrbitTrajectories.Remove(idx);
                        soiGapStateVectorExpectedBodies.Remove(idx);
                    }
                }
            }

            GhostMapPresence.EmitLifecycleSummary("flight-map-presence", currentUT);
        }

        private void PruneTerminalMapRetentionLogKeys(IReadOnlyList<Recording> committed)
        {
            if (terminalMapRetentionLoggedIds.Count == 0)
                return;

            if (committed == null || committed.Count == 0)
            {
                terminalMapRetentionLoggedIds.Clear();
                return;
            }

            List<string> staleKeys = null;
            foreach (string key in terminalMapRetentionLoggedIds)
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
                terminalMapRetentionLoggedIds.Remove(staleKeys[i]);
        }

        private static bool TryGetMapOrbitKey(
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

        internal static bool TryResolveTerminalFallbackMapOrbitUpdate(
            Recording rec,
            int idx,
            double currentUT,
            (string body, double sma, double ecc) currentKey,
            bool alreadyMaterialized,
            ref int cachedStateVectorIndex,
            out OrbitSegment fallbackSegment,
            out (string body, double sma, double ecc) fallbackKey,
            out bool changed)
        {
            fallbackSegment = default(OrbitSegment);
            fallbackKey = default((string, double, double));
            changed = false;

            TrackingStationGhostSource fallbackSource = GhostMapPresence.ResolveMapPresenceGhostSource(
                rec,
                false,
                alreadyMaterialized,
                currentUT,
                true,
                "map-presence-orbit-update",
                ref cachedStateVectorIndex,
                out fallbackSegment,
                out _,
                out _,
                recordingIndex: idx);
            if (fallbackSource != TrackingStationGhostSource.TerminalOrbit)
                return false;

            fallbackKey = (
                fallbackSegment.bodyName,
                fallbackSegment.semiMajorAxis,
                fallbackSegment.eccentricity);
            changed = fallbackKey.body != currentKey.body
                || fallbackKey.sma != currentKey.sma
                || fallbackKey.ecc != currentKey.ecc;
            if (changed)
            {
                ParsekLog.Info("Policy",
                    $"Switched ghost map orbit for #{idx} \"{rec?.VesselName}\" to terminal-orbit fallback " +
                    $"during sparse gap body={fallbackKey.body} sma={fallbackKey.sma:F0}");

                // Structured GhostMap line so the per-recording trace stays
                // consistent across files. Emits the new orbit shape and the
                // ghost's resolved world position (post-update).
                var fields = GhostMapPresence.NewDecisionFields("update-terminal-orbit-fallback");
                fields.RecordingId = rec?.RecordingId;
                fields.RecordingIndex = idx;
                fields.VesselName = rec?.VesselName;
                fields.Source = "TerminalOrbit";
                fields.Branch = "(n/a)";
                fields.Body = fallbackSegment.bodyName;
                fields.Segment = fallbackSegment;
                fields.TerminalBody = rec?.TerminalOrbitBody;
                fields.TerminalSma = rec?.TerminalOrbitSemiMajorAxis ?? double.NaN;
                fields.TerminalEcc = rec?.TerminalOrbitEccentricity ?? double.NaN;
                fields.UT = currentUT;
                fields.Reason = "sparse-gap-fallback";
                if (GhostMapPresence.TryGetGhostWorldPosForRecording(idx, out Vector3d worldPos))
                    fields.WorldPos = worldPos;
                ParsekLog.VerboseRateLimited(
                    "GhostMap",
                    string.Format(CultureInfo.InvariantCulture,
                        "gm-terminal-fallback-{0}",
                        rec?.RecordingId ?? idx.ToString(CultureInfo.InvariantCulture)),
                    GhostMapPresence.BuildGhostMapDecisionLine(fields),
                    5.0);
            }

            return true;
        }

        private static bool IsMaterializedForMapPresence(IPlaybackTrajectory traj)
        {
            var rec = traj as Recording;
            if (rec == null)
                return false;

            bool realVesselExists = false;
            if (rec.VesselPersistentId != 0)
            {
                try
                {
                    realVesselExists = FlightRecorder.FindVesselByPid(rec.VesselPersistentId) != null;
                }
                catch (Exception)
                {
                    realVesselExists = false;
                }
            }

            return GhostMapPresence.IsTrackingStationRecordingMaterialized(rec, realVesselExists);
        }

        /// <summary>
        /// Returns true if the trajectory is in an orbital segment at the given UT
        /// (i.e., recording starts in orbit, no atmospheric ascent phase before first segment).
        /// </summary>
        internal static bool StartsInOrbit(IPlaybackTrajectory traj, double ut)
        {
            return GhostMapPresence.StartsInOrbit(traj, ut);
        }

        /// <summary>
        /// If the recording at the given index belongs to a chain and another segment
        /// from the same chain currently owns a ghost map vessel, remove the old one.
        /// Updates chainMapOwner to track the new owner.
        /// </summary>
        private void RemovePreviousChainMapVessel(int newIndex)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || newIndex < 0 || newIndex >= committed.Count) return;

            string chainId = committed[newIndex].ChainId;
            if (string.IsNullOrEmpty(chainId)) return;

            if (chainMapOwner.TryGetValue(chainId, out int oldIndex) && oldIndex != newIndex)
            {
                GhostMapPresence.RemoveGhostVesselForRecording(oldIndex, "chain-segment-replaced");
                lastMapOrbitByIndex.Remove(oldIndex);
                ParsekLog.Verbose("Policy",
                    $"Chain dedup: removed ghost map for #{oldIndex} (replaced by #{newIndex} in chain {chainId})");
            }

            chainMapOwner[chainId] = newIndex;
        }

        private void HandleGhostDestroyed(GhostLifecycleEvent evt)
        {
            string name = evt.State?.vesselName ?? evt.Trajectory?.VesselName ?? "Unknown";
            ParsekLog.Verbose("Policy",
                $"GhostDestroyed index={evt.Index} vessel={name}");

            // If a held ghost was destroyed externally (e.g. soft cap, DestroyAllGhosts),
            // remove it from the held set so we don't try to destroy it again
            heldGhosts.Remove(evt.Index);
            pendingMapVessels.Remove(evt.Index);
            lastMapOrbitByIndex.Remove(evt.Index);
            stateVectorOrbitTrajectories.Remove(evt.Index);
            soiGapStateVectorExpectedBodies.Remove(evt.Index);
            stateVectorCachedIndices.Remove(evt.Index);

            // Remove both recording-index and chain-based ghost map ProtoVessels
            var committed = RecordingStore.CommittedRecordings;
            uint vesselPid = 0;
            if (committed != null && evt.Index >= 0 && evt.Index < committed.Count)
            {
                vesselPid = committed[evt.Index].VesselPersistentId;
                if (!string.IsNullOrEmpty(committed[evt.Index].RecordingId))
                    terminalMapRetentionLoggedIds.Remove(committed[evt.Index].RecordingId);
            }

            GhostMapPresence.RemoveAllGhostPresenceForIndex(evt.Index, vesselPid, "ghost-destroyed");
        }

        private void HandleLoopRestarted(LoopRestartedEvent evt)
        {
            ParsekLog.VerboseRateLimited("Policy", "loop-restarted",
                $"LoopRestarted index={evt.Index} cycle={evt.PreviousCycleIndex}->{evt.NewCycleIndex} " +
                $"explosion={evt.ExplosionFired}");
        }

        private void HandleOverlapExpired(OverlapExpiredEvent evt)
        {
            ParsekLog.VerboseRateLimited("Policy", "overlap-expired",
                $"OverlapExpired index={evt.Index} cycle={evt.CycleIndex} explosion={evt.ExplosionFired}");
        }

        private void HandleAllGhostsDestroying()
        {
            ParsekLog.Info("Policy", "AllGhostsDestroying — clearing policy state");
            pendingSpawnRecordingIds.Clear();
            pendingFlagReplayRecordingIds.Clear();
            pendingFlagReplayFailureCounts.Clear();
            pendingWatchRecordingId = null;
            heldGhosts.Clear();
            pendingMapVessels.Clear();
            lastMapOrbitByIndex.Clear();
            chainMapOwner.Clear();
            stateVectorOrbitTrajectories.Clear();
            soiGapStateVectorExpectedBodies.Clear();
            stateVectorCachedIndices.Clear();
            terminalMapRetentionLoggedIds.Clear();
        }

        /// <summary>
        /// Unsubscribe from engine events. Called from host's OnDestroy().
        /// </summary>
        internal void Dispose()
        {
            engine.OnPlaybackCompleted -= HandlePlaybackCompleted;
            engine.OnGhostCreated -= HandleGhostCreated;
            engine.OnGhostDestroyed -= HandleGhostDestroyed;
            engine.OnLoopRestarted -= HandleLoopRestarted;
            engine.OnOverlapExpired -= HandleOverlapExpired;
            engine.OnAllGhostsDestroying -= HandleAllGhostsDestroying;
            heldGhosts.Clear();
            pendingMapVessels.Clear();
            lastMapOrbitByIndex.Clear();
            chainMapOwner.Clear();
            stateVectorOrbitTrajectories.Clear();
            soiGapStateVectorExpectedBodies.Clear();
            stateVectorCachedIndices.Clear();
            terminalMapRetentionLoggedIds.Clear();
            ParsekLog.Info("Policy", "ParsekPlaybackPolicy disposed and unsubscribed from 6 engine events");
        }

        /// <summary>
        /// Live-state adapter for the pure chain-next resolver in
        /// <see cref="GhostPlaybackLogic.ResolveChainNextSlotIndex"/>. Reads
        /// the current <see cref="RecordingStore.CommittedRecordings"/> and
        /// <see cref="ParsekScenario.RecordingSupersedes"/> and delegates the
        /// lookup. Behaviour is fully covered by
        /// <c>ResolveChainNextSlotIndexTests</c> against the pure helper.
        /// </summary>
        internal int ResolveChainNextSlotIndex(int slotIndex)
        {
            return GhostPlaybackLogic.ResolveChainNextSlotIndex(
                slotIndex,
                RecordingStore.CommittedRecordings,
                ParsekScenario.Instance?.RecordingSupersedes);
        }
    }

    /// <summary>
    /// Tracks a ghost held at its final position while waiting for spawn to complete (#96).
    /// </summary>
    internal struct HeldGhostInfo
    {
        /// <summary>Time.time when the ghost was held (real time, not UT).</summary>
        public float holdStartTime;

        /// <summary>Time.time of last spawn retry attempt (throttles retries to 1/sec).
        /// Defaults to 0 so the first retry fires immediately after hold starts — intentional.</summary>
        public float lastRetryTime;

        /// <summary>Recording ID to verify index stability after deletes.</summary>
        public string recordingId;

        /// <summary>Vessel name for logging.</summary>
        public string vesselName;

        /// <summary>Whether this ghost was being watched when held.</summary>
    }

    /// <summary>
    /// Decision result for held ghost retry logic. Returned by the pure
    /// DecideHeldGhostAction method for testability.
    /// </summary>
    internal enum HeldGhostAction
    {
        /// <summary>Keep holding — retry interval not yet elapsed (throttles to 1/sec).</summary>
        Hold,

        /// <summary>Retry the spawn attempt.</summary>
        RetrySpawn,

        /// <summary>Recording was already spawned by another path — release ghost.</summary>
        ReleaseSpawned,

        /// <summary>Recording was retired by an explicit supersede relation — release ghost.</summary>
        ReleaseSupersededByRelation,

        /// <summary>Recording was retired by rewind rollback — release ghost.</summary>
        ReleaseRewindRetired,

        /// <summary>Timeout exceeded — destroy ghost without spawn.</summary>
        Timeout,

        /// <summary>Index is invalid or recording ID mismatch — release ghost.</summary>
        InvalidIndex,
    }
}
