using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

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
    internal class ParsekPlaybackPolicy
    {
        private readonly GhostPlaybackEngine engine;
        private readonly ParsekFlight host;

        // Deferred spawn queue: recording IDs queued during warp, flushed when warp ends
        internal readonly HashSet<string> pendingSpawnRecordingIds = new HashSet<string>();
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

            ParsekLog.Info("Policy", "ParsekPlaybackPolicy created and subscribed to engine events");
        }

        /// <summary>
        /// Pre-check: detect spawned vessels that died since last frame.
        /// Runs BEFORE engine.UpdatePlayback() so flags reflect current state.
        /// If a spawned vessel's PID is no longer in FlightGlobals.Vessels, the
        /// recording is either reset for re-spawn or abandoned after MaxSpawnDeathCycles.
        /// </summary>
        internal void RunSpawnDeathChecks()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed.Count == 0) return;

            int detected = 0;
            int abandoned = 0;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!GhostPlaybackLogic.ShouldCheckForSpawnDeath(
                        rec.VesselSpawned, rec.SpawnedVesselPersistentId, rec.SpawnAbandoned))
                    continue;

                // Vessel still alive — no action needed
                if (FlightRecorder.FindVesselByPid(rec.SpawnedVesselPersistentId) != null)
                    continue;

                // Vessel died since last frame
                rec.SpawnDeathCount++;
                detected++;

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
        }

        /// <summary>
        /// Removes a recording ID from the deferred spawn queue (e.g. on delete).
        /// </summary>
        internal void RemovePendingSpawn(string recordingId)
        {
            pendingSpawnRecordingIds.Remove(recordingId);
        }

        /// <summary>
        /// Flush deferred spawns that were queued during warp.
        /// Runs AFTER engine.UpdatePlayback() when warp ends.
        /// Iterates committed recordings, spawns eligible vessels, skips
        /// out-of-bubble spawns (kept in queue for next check).
        /// </summary>
        internal void FlushDeferredSpawns()
        {
            if (!GhostPlaybackLogic.ShouldFlushDeferredSpawns(
                    pendingSpawnRecordingIds.Count,
                    GhostPlaybackEngine.IsAnyWarpActiveFromGlobals()))
                return;

            var committed = RecordingStore.CommittedRecordings;
            int spawnedCount = 0;
            int deferredOutOfBubble = 0;
            var flushedIds = new List<string>();
            Vector3d activeVesselPos = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.GetWorldPos3D()
                : Vector3d.zero;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!pendingSpawnRecordingIds.Contains(rec.RecordingId)) continue;
                if (GhostPlaybackLogic.ShouldSkipDeferredSpawn(rec.VesselSpawned, rec.VesselSnapshot != null))
                {
                    ParsekLog.Verbose("Policy",
                        $"Deferred spawn skipped — #{i} \"{rec.VesselName}\" already spawned or no snapshot");
                    flushedIds.Add(rec.RecordingId);
                    continue;
                }

                // Physics-bubble scoping: skip out-of-bubble spawns (keep in queue)
                if (FlightGlobals.ActiveVessel != null &&
                    ParsekFlight.ShouldDeferSpawnOutsideBubble(rec, activeVesselPos))
                {
                    deferredOutOfBubble++;
                    ParsekLog.Verbose("Policy",
                        $"Deferred spawn kept in queue (outside physics bubble): #{i} \"{rec.VesselName}\"");
                    continue;
                }

                ParsekLog.Info("Policy",
                    $"Deferred spawn executing: #{i} \"{rec.VesselName}\" id={rec.RecordingId}");
                host.SpawnVesselOrChainTipFromPolicy(rec, i);
                spawnedCount++;
                flushedIds.Add(rec.RecordingId);

                // Restore camera follow if this recording was being watched when deferred
                if (GhostPlaybackLogic.ShouldRestoreWatchMode(
                        pendingWatchRecordingId, rec.RecordingId, rec.SpawnedVesselPersistentId))
                {
                    ParsekLog.Info("Policy",
                        $"Deferred watch: switching to spawned vessel pid={rec.SpawnedVesselPersistentId}");
                    host.DeferredActivateVesselFromPolicy(rec.SpawnedVesselPersistentId);
                }
            }

            // Remove flushed IDs, keep out-of-bubble spawns in queue
            for (int j = 0; j < flushedIds.Count; j++)
                pendingSpawnRecordingIds.Remove(flushedIds[j]);

            if (deferredOutOfBubble > 0)
            {
                ParsekLog.Info("Policy",
                    $"Warp ended — flushed {spawnedCount} deferred spawn(s), " +
                    $"{deferredOutOfBubble} kept (outside bubble), " +
                    $"{pendingSpawnRecordingIds.Count} remaining");
            }
            else
            {
                ParsekLog.Info("Policy",
                    $"Warp ended — flushed {spawnedCount}/{flushedIds.Count} deferred spawn(s)");
                pendingSpawnRecordingIds.Clear();
            }

            if (pendingSpawnRecordingIds.Count == 0)
                pendingWatchRecordingId = null;
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
                            host.TransferWatchToNextSegmentFromPolicy(nextTarget);
                            ParsekLog.Info("Policy",
                                $"Mid-chain auto-follow: #{evt.Index} → #{nextTarget}");
                        }
                        else
                        {
                            // Next chain segment ghost hasn't spawned yet — set a hold timer
                            // so UpdateWatchCamera retries FindNextWatchTarget every frame.
                            // Without this, the camera stays stuck on the stale ghost position
                            // indefinitely (no retry mechanism, no hold timer).
                            float holdSeconds = 30f;
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
                            ParsekLog.Info("Policy",
                                $"Auto-follow on completion: #{evt.Index} → #{nextTarget} " +
                                $"(vessel={committed[nextTarget].VesselName})");
                            host.TransferWatchToNextSegmentFromPolicy(nextTarget);
                            engine.DestroyGhost(evt.Index, evt.Trajectory, evt.Flags,
                                reason: "auto-followed to next stage");
                            return;
                        }
                    }

                    // No continuation found — hold ghost for camera (3-5 real seconds).
                    // Uses real time (Time.time), not UT, so the hold duration is warp-independent.
                    float holdSeconds = evt.Trajectory?.TerminalStateValue == TerminalState.Destroyed
                        ? 5f : 3f;
                    host.StartWatchHoldFromPolicy(Time.time + holdSeconds);

                    // Trigger explosion if terminal was Destroyed
                    engine.TriggerExplosionIfDestroyed(evt.State, evt.Trajectory, evt.Index,
                        TimeWarp.CurrentRate);

                    ParsekLog.Info("Policy",
                        $"Watch hold started for #{evt.Index}: {holdSeconds:F0}s " +
                        $"terminal={evt.Trajectory?.TerminalStateValue}");
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
            float now = Time.time;

            // Collect indices to release and retry-time updates (cannot modify dict during iteration)
            List<KeyValuePair<int, string>> toRelease = null;  // index + destroy reason
            List<KeyValuePair<int, float>> retryTimeUpdates = null;

            foreach (var kvp in heldGhosts)
            {
                int index = kvp.Key;
                HeldGhostInfo info = kvp.Value;

                var decision = DecideHeldGhostAction(
                    index, info, committed, now, HeldGhostTimeoutSeconds,
                    HeldGhostRetryIntervalSeconds);

                switch (decision)
                {
                    case HeldGhostAction.RetrySpawn:
                        // Recording is valid and not yet spawned — retry
                        if (retryTimeUpdates == null) retryTimeUpdates = new List<KeyValuePair<int, float>>();
                        retryTimeUpdates.Add(new KeyValuePair<int, float>(index, now));
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
                                    host.DeferredActivateVesselFromPolicy(spawnedPid);
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
                    engine.DestroyGhost(index, reason: reason);
                    heldGhosts.Remove(index);
                }
            }
        }

        /// <summary>
        /// Pure decision logic for held ghost retry. Determines what action to take
        /// for a held ghost based on current state. Testable without Unity side effects.
        /// </summary>
        internal static HeldGhostAction DecideHeldGhostAction(
            int index, HeldGhostInfo info, IReadOnlyList<Recording> committed,
            float currentTime, float timeoutSeconds,
            float retryIntervalSeconds = 1.0f)
        {
            // Invalid index — recording list may have changed
            if (index < 0 || index >= committed.Count)
                return HeldGhostAction.InvalidIndex;

            var rec = committed[index];

            // Verify this is still the same recording (indices can shift after deletes)
            if (rec.RecordingId != info.recordingId)
                return HeldGhostAction.InvalidIndex;

            // Already spawned by another path
            if (rec.VesselSpawned)
                return HeldGhostAction.ReleaseSpawned;

            // Timeout check
            float elapsed = currentTime - info.holdStartTime;
            if (elapsed >= timeoutSeconds)
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
        private readonly Dictionary<int, IPlaybackTrajectory> pendingMapVessels =
            new Dictionary<int, IPlaybackTrajectory>();

        /// <summary>
        /// Tracks the last orbit segment body+SMA per recording index for change detection.
        /// Used to update the ghost ProtoVessel orbit as the ghost traverses segments.
        /// </summary>
        private const float MapOrbitUpdateIntervalSec = 0.5f;
        private float nextMapOrbitUpdateTime;

        private readonly Dictionary<int, (string body, double sma, double ecc)> lastMapOrbitByIndex =
            new Dictionary<int, (string body, double sma, double ecc)>();

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
        internal const double StateVectorCreateAltitude = 1500;   // meters (airless bodies only)
        internal const double StateVectorCreateSpeed = 60;        // m/s
        internal const double StateVectorRemoveAltitude = 500;    // meters (airless bodies only)
        internal const double StateVectorRemoveSpeed = 30;        // m/s

        private void HandleGhostCreated(GhostLifecycleEvent evt)
        {
            if (evt.Trajectory == null || evt.Trajectory.IsDebris)
            {
                if (evt.Trajectory?.IsDebris == true)
                    ParsekLog.Verbose("Policy",
                        $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory?.VesselName}\" — debris");
                return;
            }

            // Skip recordings with non-orbital terminal states (Destroyed, Landed, etc.)
            // but allow: terminal=null (intermediate chain segments), Orbiting, Docked,
            // SubOrbital (ballistic arc orbit line during coast phases).
            var terminal = evt.Trajectory.TerminalStateValue;
            if (terminal.HasValue
                && terminal.Value != TerminalState.Orbiting
                && terminal.Value != TerminalState.Docked
                && terminal.Value != TerminalState.SubOrbital)
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

            // Check if the ghost starts in an orbital segment (orbit-only recording
            // or UT is already within an orbital segment). If so, create immediately.
            // Otherwise, defer — the per-frame check will create it when the ghost
            // enters its first orbital segment.
            double startUT = evt.Trajectory.StartUT;
            if (StartsInOrbit(evt.Trajectory, startUT))
            {
                GhostMapPresence.CreateGhostVesselForRecording(evt.Index, evt.Trajectory);
                // Seed segment tracking for per-frame orbit updates
                OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(evt.Trajectory.OrbitSegments, startUT);
                if (seg.HasValue)
                    lastMapOrbitByIndex[evt.Index] = (seg.Value.bodyName, seg.Value.semiMajorAxis, seg.Value.eccentricity);
            }
            else
            {
                pendingMapVessels[evt.Index] = evt.Trajectory;
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
            double minAltitude = atmosphereDepth > 0 ? atmosphereDepth : StateVectorCreateAltitude;
            return altitude > minAltitude && speed > StateVectorCreateSpeed;
        }

        /// <summary>
        /// Pure: should we remove a state-vector orbit ProtoVessel at this altitude/speed?
        /// On bodies with atmosphere, removes immediately when descending into atmosphere.
        /// On airless bodies, uses lower hysteresis thresholds.
        /// </summary>
        internal static bool ShouldRemoveStateVectorOrbit(double altitude, double speed, double atmosphereDepth)
        {
            if (atmosphereDepth > 0 && altitude < atmosphereDepth)
                return true;
            return altitude < StateVectorRemoveAltitude || speed < StateVectorRemoveSpeed;
        }

        /// <summary>
        /// Look up atmosphere depth for a body by name. Returns 0 for airless bodies or if
        /// FlightGlobals is unavailable (test environment).
        /// </summary>
        internal static double GetAtmosphereDepth(string bodyName)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i].name == bodyName && bodies[i].atmosphere)
                    return bodies[i].atmosphereDepth;
            }
            return 0;
        }

        /// <summary>
        /// Pure: is the recording in a RELATIVE reference frame at the given UT?
        /// RELATIVE frames store dx/dy/dz offsets, not lat/lon/alt — state-vector
        /// orbit construction would produce nonsense.
        /// </summary>
        internal static bool IsInRelativeFrame(IPlaybackTrajectory traj, double ut)
        {
            if (traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative;
        }

        /// <summary>
        /// Per-frame check for ghost map ProtoVessels. Handles three responsibilities:
        /// 1. Creates deferred ProtoVessels when ghosts enter their first orbital segment
        /// 2. Creates state-vector orbit ProtoVessels for physics-only suborbital recordings
        /// 3. Updates existing ProtoVessel orbits (segment-based and state-vector-based)
        /// </summary>
        internal void CheckPendingMapVessels(double currentUT)
        {
            // 1. Create deferred ProtoVessels for ghosts that just entered an orbital segment
            //    OR for physics-only recordings that crossed the state-vector threshold.
            if (pendingMapVessels.Count > 0)
            {
                List<KeyValuePair<int, OrbitSegment>> toCreateFromSegment = null;
                List<KeyValuePair<int, TrajectoryPoint>> toCreateFromStateVectors = null;

                foreach (var kvp in pendingMapVessels)
                {
                    int idx = kvp.Key;
                    IPlaybackTrajectory traj = kvp.Value;

                    // Path A: orbit-segment-based (existing)
                    OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, currentUT);
                    if (seg.HasValue)
                    {
                        if (toCreateFromSegment == null)
                            toCreateFromSegment = new List<KeyValuePair<int, OrbitSegment>>();
                        toCreateFromSegment.Add(new KeyValuePair<int, OrbitSegment>(idx, seg.Value));
                        continue;
                    }

                    // Path B: state-vector-based (new — physics-only suborbital)
                    if (!traj.HasOrbitSegments)
                    {
                        if (traj.Points == null || traj.Points.Count == 0) continue;
                        if (IsInRelativeFrame(traj, currentUT)) continue;

                        if (!stateVectorCachedIndices.ContainsKey(idx))
                            stateVectorCachedIndices[idx] = -1;
                        int cached = stateVectorCachedIndices[idx];
                        TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(traj.Points, currentUT, ref cached);
                        stateVectorCachedIndices[idx] = cached;

                        double atmosCreate = pt.HasValue ? GetAtmosphereDepth(pt.Value.bodyName) : 0;
                        if (pt.HasValue && ShouldCreateStateVectorOrbit(pt.Value.altitude, pt.Value.velocity.magnitude, atmosCreate))
                        {
                            if (toCreateFromStateVectors == null)
                                toCreateFromStateVectors = new List<KeyValuePair<int, TrajectoryPoint>>();
                            toCreateFromStateVectors.Add(new KeyValuePair<int, TrajectoryPoint>(idx, pt.Value));
                        }
                    }
                }

                // Apply segment-based creations
                if (toCreateFromSegment != null)
                {
                    for (int i = 0; i < toCreateFromSegment.Count; i++)
                    {
                        int idx = toCreateFromSegment[i].Key;
                        OrbitSegment initialSeg = toCreateFromSegment[i].Value;
                        if (pendingMapVessels.TryGetValue(idx, out var traj))
                        {
                            // Per-chain dedup before deferred creation
                            RemovePreviousChainMapVessel(idx);
                            Vessel ghost = GhostMapPresence.HasOrbitData(traj)
                                ? GhostMapPresence.CreateGhostVesselForRecording(idx, traj)
                                : GhostMapPresence.CreateGhostVesselFromSegment(idx, traj, initialSeg);
                            pendingMapVessels.Remove(idx);

                            if (ghost != null)
                                GhostMapPresence.UpdateGhostOrbitForRecording(idx, initialSeg);

                            lastMapOrbitByIndex[idx] = (initialSeg.bodyName, initialSeg.semiMajorAxis, initialSeg.eccentricity);

                            ParsekLog.Info("Policy",
                                $"Created deferred ghost map vessel for #{idx} \"{traj.VesselName}\" " +
                                $"— entered segment body={initialSeg.bodyName} sma={initialSeg.semiMajorAxis:F0}");
                        }
                    }
                }

                // Apply state-vector-based creations
                if (toCreateFromStateVectors != null)
                {
                    for (int i = 0; i < toCreateFromStateVectors.Count; i++)
                    {
                        int idx = toCreateFromStateVectors[i].Key;
                        TrajectoryPoint pt = toCreateFromStateVectors[i].Value;
                        if (pendingMapVessels.TryGetValue(idx, out var traj))
                        {
                            Vessel ghost = GhostMapPresence.CreateGhostVesselFromStateVectors(idx, traj, pt, currentUT);
                            pendingMapVessels.Remove(idx);
                            stateVectorOrbitTrajectories[idx] = traj;

                            ParsekLog.Info("Policy", string.Format(CultureInfo.InvariantCulture,
                                "Created state-vector ghost map vessel for #{0} \"{1}\" — alt={2:F0} speed={3:F1}",
                                idx, traj.VesselName, pt.altitude, pt.velocity.magnitude));
                        }
                    }
                }
            }

            // 2. Rate-limited orbit updates for existing ProtoVessels.
            if (UnityEngine.Time.time < nextMapOrbitUpdateTime) return;
            nextMapOrbitUpdateTime = UnityEngine.Time.time + MapOrbitUpdateIntervalSec;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return;

            // 2a. Segment-based orbit updates (existing)
            List<KeyValuePair<int, (string body, double sma, double ecc)>> orbitUpdates = null;
            List<int> toRemoveFromMap = null;

            foreach (var kvp in lastMapOrbitByIndex)
            {
                int idx = kvp.Key;
                if (idx < 0 || idx >= committed.Count) continue;

                var rec = committed[idx];
                if (!rec.HasOrbitSegments) continue;

                OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT);

                // UT is past all orbit segments — ghost is no longer orbital (landed, destroyed, etc.).
                // Remove the ghost map ProtoVessel so the stale orbit line disappears.
                if (!seg.HasValue)
                {
                    GhostMapPresence.RemoveGhostVesselForRecording(idx, "left-orbit-segments");
                    if (toRemoveFromMap == null) toRemoveFromMap = new List<int>();
                    toRemoveFromMap.Add(idx);
                    continue;
                }

                if (seg.Value.bodyName == kvp.Value.body
                    && seg.Value.semiMajorAxis == kvp.Value.sma
                    && seg.Value.eccentricity == kvp.Value.ecc)
                    continue;

                GhostMapPresence.UpdateGhostOrbitForRecording(idx, seg.Value);
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

            // 2b. State-vector orbit updates (new — physics-only suborbital)
            if (stateVectorOrbitTrajectories.Count > 0)
            {
                List<int> toReDefer = null;

                foreach (var kvp in stateVectorOrbitTrajectories)
                {
                    int idx = kvp.Key;
                    IPlaybackTrajectory traj = kvp.Value;

                    if (!stateVectorCachedIndices.ContainsKey(idx))
                        stateVectorCachedIndices[idx] = -1;
                    int cached = stateVectorCachedIndices[idx];
                    TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(traj.Points, currentUT, ref cached);
                    stateVectorCachedIndices[idx] = cached;

                    if (!pt.HasValue) continue;

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

                    GhostMapPresence.UpdateGhostOrbitFromStateVectors(idx, pt.Value, currentUT);
                }

                if (toReDefer != null)
                {
                    for (int i = 0; i < toReDefer.Count; i++)
                    {
                        int idx = toReDefer[i];
                        if (stateVectorOrbitTrajectories.TryGetValue(idx, out var traj))
                            pendingMapVessels[idx] = traj;
                        stateVectorOrbitTrajectories.Remove(idx);
                        // stateVectorCachedIndices[idx] intentionally kept — avoids
                        // O(n) re-scan if the ghost re-ascends above threshold.
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the trajectory is in an orbital segment at the given UT
        /// (i.e., recording starts in orbit, no atmospheric ascent phase before first segment).
        /// </summary>
        internal static bool StartsInOrbit(IPlaybackTrajectory traj, double ut)
        {
            if (!traj.HasOrbitSegments)
                return false;
            // If recording has no trajectory points, it's orbit-only
            if (traj.Points == null || traj.Points.Count == 0)
                return true;
            return TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, ut) != null;
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
            stateVectorCachedIndices.Remove(evt.Index);

            // Remove both recording-index and chain-based ghost map ProtoVessels
            var committed = RecordingStore.CommittedRecordings;
            uint vesselPid = 0;
            if (committed != null && evt.Index >= 0 && evt.Index < committed.Count)
                vesselPid = committed[evt.Index].VesselPersistentId;

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
            pendingWatchRecordingId = null;
            heldGhosts.Clear();
            pendingMapVessels.Clear();
            lastMapOrbitByIndex.Clear();
            chainMapOwner.Clear();
            stateVectorOrbitTrajectories.Clear();
            stateVectorCachedIndices.Clear();
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
            stateVectorCachedIndices.Clear();
            ParsekLog.Info("Policy", "ParsekPlaybackPolicy disposed and unsubscribed from 6 engine events");
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

        /// <summary>Timeout exceeded — destroy ghost without spawn.</summary>
        Timeout,

        /// <summary>Index is invalid or recording ID mismatch — release ghost.</summary>
        InvalidIndex,
    }
}
