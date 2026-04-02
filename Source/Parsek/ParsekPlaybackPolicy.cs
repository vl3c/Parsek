using System;
using System.Collections.Generic;
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

            ParsekLog.Info("Policy", "ParsekPlaybackPolicy created and subscribed to engine events");
        }

        /// <summary>
        /// Pre-check: detect spawned vessels that died since last frame.
        /// Runs BEFORE engine.UpdatePlayback() so flags reflect current state.
        /// </summary>
        internal void RunSpawnDeathChecks()
        {
            // TODO(#132): Move spawn-death detection from ParsekFlight
        }

        /// <summary>
        /// Flush deferred spawns that were queued during warp.
        /// Runs AFTER engine.UpdatePlayback() when warp ends.
        /// </summary>
        internal void FlushDeferredSpawns(double currentUT, float warpRate)
        {
            // TODO(#132): Move FlushDeferredSpawns from ParsekFlight
        }

        private void HandlePlaybackCompleted(PlaybackCompletedEvent evt)
        {
            bool isWatched = host.watchedRecordingIndex == evt.Index;

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
                            if (host.watchedRecordingIndex == index)
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

        private void HandleGhostCreated(GhostLifecycleEvent evt)
        {
            if (evt.Trajectory == null || evt.Trajectory.IsDebris)
            {
                if (evt.Trajectory?.IsDebris == true)
                    ParsekLog.Verbose("Policy",
                        $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory?.VesselName}\" — debris");
                return;
            }

            // Only for stable orbital terminal states
            var terminal = evt.Trajectory.TerminalStateValue;
            if (terminal.HasValue
                && terminal.Value != TerminalState.Orbiting
                && terminal.Value != TerminalState.Docked)
            {
                ParsekLog.VerboseRateLimited("Policy", $"skip-map-terminal-{evt.Index}",
                    $"Skipped ghost map for #{evt.Index} \"{evt.Trajectory.VesselName}\" — terminal={terminal.Value}");
                return;
            }

            if (!GhostMapPresence.HasOrbitData(evt.Trajectory))
                return;

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
        /// Per-frame check for ghost map ProtoVessels. Handles two responsibilities:
        /// 1. Creates deferred ProtoVessels when ghosts enter their first orbital segment
        /// 2. Updates existing ProtoVessel orbits when ghosts traverse segment boundaries
        /// </summary>
        internal void CheckPendingMapVessels(double currentUT)
        {
            // 1. Create deferred ProtoVessels for ghosts that just entered an orbital segment
            if (pendingMapVessels.Count > 0)
            {
                // Capture index + segment together to avoid a second FindOrbitSegment call
                List<KeyValuePair<int, OrbitSegment>> toCreate = null;
                foreach (var kvp in pendingMapVessels)
                {
                    OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(kvp.Value.OrbitSegments, currentUT);
                    if (seg.HasValue)
                    {
                        if (toCreate == null) toCreate = new List<KeyValuePair<int, OrbitSegment>>();
                        toCreate.Add(new KeyValuePair<int, OrbitSegment>(kvp.Key, seg.Value));
                    }
                }

                if (toCreate != null)
                {
                    for (int i = 0; i < toCreate.Count; i++)
                    {
                        int idx = toCreate[i].Key;
                        OrbitSegment initialSeg = toCreate[i].Value;
                        if (pendingMapVessels.TryGetValue(idx, out var traj))
                        {
                            GhostMapPresence.CreateGhostVesselForRecording(idx, traj);
                            pendingMapVessels.Remove(idx);

                            // Immediately update to the triggering segment's orbit.
                            // CreateGhostVesselForRecording uses terminal orbit (recording end),
                            // but the ghost just entered an intermediate segment (e.g., first
                            // circular orbit during ascent, not the final parking orbit).
                            GhostMapPresence.UpdateGhostOrbitForRecording(idx, initialSeg);

                            // Seed segment tracking from the already-found segment (no second lookup)
                            lastMapOrbitByIndex[idx] = (initialSeg.bodyName, initialSeg.semiMajorAxis, initialSeg.eccentricity);

                            ParsekLog.Info("Policy",
                                $"Created deferred ghost map vessel for #{idx} \"{traj.VesselName}\" " +
                                $"— entered segment body={initialSeg.bodyName} sma={initialSeg.semiMajorAxis:F0}");
                        }
                    }
                }
            }

            // 2. Update orbits for existing recording-index ProtoVessels when segment changes.
            // Rate-limited: orbit segment boundaries are infrequent, no need to scan per-frame.
            if (UnityEngine.Time.time < nextMapOrbitUpdateTime) return;
            nextMapOrbitUpdateTime = UnityEngine.Time.time + MapOrbitUpdateIntervalSec;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return;

            // Collect updates to apply after iteration (cannot modify dict during foreach)
            List<KeyValuePair<int, (string body, double sma, double ecc)>> orbitUpdates = null;

            foreach (var kvp in lastMapOrbitByIndex)
            {
                int idx = kvp.Key;
                if (idx < 0 || idx >= committed.Count) continue;

                var rec = committed[idx];
                if (rec.OrbitSegments == null || rec.OrbitSegments.Count == 0) continue;

                OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT);
                if (!seg.HasValue) continue;

                // Exact equality is intentional: stored segment values don't drift.
                // A change means a different OrbitSegment, not floating-point accumulation.
                // Includes eccentricity to catch inclination-change maneuvers at constant SMA.
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


        }

        /// <summary>
        /// Returns true if the trajectory is in an orbital segment at the given UT
        /// (i.e., recording starts in orbit, no atmospheric ascent phase before first segment).
        /// </summary>
        internal static bool StartsInOrbit(IPlaybackTrajectory traj, double ut)
        {
            if (traj.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;
            // If recording has no trajectory points, it's orbit-only
            if (traj.Points == null || traj.Points.Count == 0)
                return true;
            return TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, ut) != null;
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
