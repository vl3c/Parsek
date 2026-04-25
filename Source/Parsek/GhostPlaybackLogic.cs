using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure-static helper methods for ghost visual manipulation and playback logic.
    /// Extracted from ParsekFlight to reduce file size; all methods are stateless.
    /// </summary>
    internal static class GhostPlaybackLogic
    {
        // Tunable constants live in ParsekConfig.cs:
        //   WarpThresholds.FxSuppress / GhostHide — time-warp FX and ghost-mesh suppression levels
        //   LoopTiming.* — default/untouched loop periods, min cycle/loop duration, boundary epsilon
        //   WatchMode.ZoneGraceSeconds — wall-clock grace before zone-based watch exit
        //   WatchMode.PendingPostActivationGraceSeconds / MaxPendingHoldSeconds — pending-watch holds

        // Dedupe set for ResolveLoopInterval clamp warnings. Without this, a recording whose
        // LoopIntervalSeconds is below MinCycleDuration produces a log line every frame from
        // GhostPlaybackEngine + ParsekKSC (~3,600 lines/sec with ~20 offending recordings —
        // ~1.3M entries in a 6-minute session). Keyed on RecordingId (stable across loads)
        // with fallback to VesselName for transient fixtures lacking an id. Reset between
        // tests via ResetForTesting.
        private static readonly HashSet<string> loopIntervalClampWarned = new HashSet<string>(StringComparer.Ordinal);
        private static Func<FlagEvent, bool> flagExistsOverrideForTesting;
        private static Func<FlagEvent, bool> spawnFlagOverrideForTesting;

        internal readonly struct AutoLoopLaunchSchedule
        {
            internal AutoLoopLaunchSchedule(
                double launchStartUT,
                double launchCadenceSeconds,
                int slotIndex,
                int queueCount)
            {
                LaunchStartUT = launchStartUT;
                LaunchCadenceSeconds = launchCadenceSeconds;
                SlotIndex = slotIndex;
                QueueCount = queueCount;
            }

            internal double LaunchStartUT { get; }
            internal double LaunchCadenceSeconds { get; }
            internal int SlotIndex { get; }
            internal int QueueCount { get; }
        }

        private readonly struct AutoLoopQueueEntry
        {
            internal AutoLoopQueueEntry(
                int recordingIndex,
                double playbackStartUT,
                double playbackEndUT,
                string recordingId)
            {
                RecordingIndex = recordingIndex;
                PlaybackStartUT = playbackStartUT;
                PlaybackEndUT = playbackEndUT;
                RecordingId = recordingId ?? string.Empty;
            }

            internal int RecordingIndex { get; }
            internal double PlaybackStartUT { get; }
            internal double PlaybackEndUT { get; }
            internal string RecordingId { get; }
        }

        #region Warp / Loop Policy

        /// <summary>
        /// Bug #414: per-frame ghost spawn throttle decision.
        /// Returns true when the caller MUST defer its spawn to a later frame because the
        /// frame's spawn cap is already exhausted. Pure helper so the decision is unit-testable
        /// independently of GhostPlaybackEngine state. The cap applies only to the safe-to-defer
        /// call sites listed in docs/dev/done/plan-414-spawn-throttle.md — watch-mode and loop-cycle
        /// rebuild spawns bypass this gate at the call site (never invoke this helper).
        /// </summary>
        internal static bool ShouldThrottleSpawn(int spawnsThisFrame, int maxPerFrame)
        {
            return spawnsThisFrame >= maxPerFrame;
        }

        /// <summary>
        /// Bug #450 B3: decides whether a deferred reentry-FX build should fire on the
        /// current frame. Returns true when the ghost is inside a body's atmosphere AND
        /// moving fast enough for reentry visuals to be imminent (speed ≥ the
        /// <c>ReentryPotentialSpeedFloor</c> = 400 m/s ≈ Mach 1.2 at sea-level Kerbin —
        /// the floor shared with <c>TrajectoryMath.HasReentryPotential</c>).
        ///
        /// The speed gate prevents launch recordings (pad → ascent) from triggering the
        /// build on the very first playback frame: those ghosts are at 0 m/s at ground
        /// level on frame 1 and only cross the 400 m/s floor several seconds later, so
        /// the 7 ms build shifts off the spawn hitch by design. Atmospheric-start
        /// recordings where the ghost is already above 400 m/s (mid-reentry saves) still
        /// fire on frame 1 — that is correct because we legitimately need FX right away.
        ///
        /// Pure helper so the condition is unit-testable without Unity or FlightGlobals.
        /// </summary>
        internal static bool ShouldBuildLazyReentryFx(
            bool pendingFlag, string bodyName, bool bodyHasAtmosphere,
            double altitudeMeters, double atmosphereDepthMeters,
            float surfaceSpeedMetersPerSecond, float speedFloorMetersPerSecond)
        {
            if (!pendingFlag) return false;
            if (string.IsNullOrEmpty(bodyName)) return false;
            if (!bodyHasAtmosphere) return false;
            // Strict `<`: at exactly atmosphereDepth the existing UpdateReentryFx path
            // already calls DriveReentryToZero, so firing the build here would only pay
            // the cost for a frame that produces zero-intensity output anyway.
            if (altitudeMeters >= atmosphereDepthMeters) return false;
            // Speed gate — see XML doc above. NaN/negative compares false by IEEE rules,
            // so a malformed velocity correctly suppresses the build rather than leaking it.
            if (!(surfaceSpeedMetersPerSecond >= speedFloorMetersPerSecond)) return false;
            return true;
        }

        internal static bool ShouldSuppressVisualFx(float currentWarpRate)
        {
            return currentWarpRate > WarpThresholds.FxSuppress;
        }

        internal static bool ShouldSuppressGhosts(float currentWarpRate)
        {
            return currentWarpRate > WarpThresholds.GhostHide;
        }

        /// <summary>
        /// Returns true if a ghost should be exempt from zone-based hiding during time warp.
        /// Orbital ghosts travel far from the player during warp; hiding them at 120km causes
        /// them to disappear and complete playback while invisible (#171).
        /// </summary>
        internal static bool ShouldExemptFromZoneHide(float currentWarpRate, bool hasOrbitalSegments)
        {
            // Threshold lowered from > 4f to > 1f: KSP ramps through intermediate rates
            // when entering/exiting warp, causing frame-by-frame oscillation around higher
            // thresholds and ghost icon flicker in map view.
            return currentWarpRate > 1f && hasOrbitalSegments;
        }

        /// <summary>
        /// Warp-zone hide exemption only applies to true Beyond-zone hiding. It must not
        /// cancel the 50-120 km hidden-mesh tier introduced by distance LOD.
        /// </summary>
        internal static bool ShouldApplyWarpZoneHideExemption(
            bool shouldHideMesh, RenderingZone zone, float currentWarpRate, bool hasOrbitalSegments)
        {
            return shouldHideMesh
                && zone == RenderingZone.Beyond
                && ShouldExemptFromZoneHide(currentWarpRate, hasOrbitalSegments);
        }

        /// <summary>
        /// Returns true if a commit approval dialog should be shown instead of auto-committing (#88).
        /// Triggers when leaving Flight to KSC or Tracking Station with a landed/splashed vessel.
        /// </summary>
        internal static bool ShouldShowCommitApproval(GameScenes destination, TerminalState? terminalState)
        {
            if (destination != GameScenes.SPACECENTER && destination != GameScenes.TRACKSTATION)
                return false;
            if (!terminalState.HasValue)
                return false;
            var ts = terminalState.Value;
            return ts == TerminalState.Landed || ts == TerminalState.Splashed;
        }

        internal static bool ShouldFlushDeferredSpawns(int pendingCount, bool isWarpActive)
        {
            return pendingCount > 0 && !isWarpActive;
        }

        internal static bool ShouldSkipDeferredSpawn(bool vesselSpawned, bool hasSnapshot)
        {
            return vesselSpawned || !hasSnapshot;
        }

        internal static bool ShouldRestoreWatchMode(string pendingWatchId, string recordingId, uint spawnedPid)
        {
            return pendingWatchId != null && pendingWatchId == recordingId && spawnedPid != 0;
        }

        /// <summary>
        /// Pure decision: should this recording be checked for spawn-death (vessel spawned
        /// then immediately destroyed)? Returns true when the recording has a live spawned
        /// vessel that hasn't already been abandoned.
        /// </summary>
        internal static bool ShouldCheckForSpawnDeath(bool vesselSpawned, uint spawnedPid, bool spawnAbandoned)
        {
            return vesselSpawned && spawnedPid != 0 && !spawnAbandoned;
        }

        internal static bool ShouldPauseTimelineResourceReplay(bool isRecording)
        {
            return isRecording;
        }

        internal static bool ShouldLoopPlayback(bool recordingLoopPlayback)
        {
            return recordingLoopPlayback;
        }

        internal static bool IsAnyWarpActive(int currentRateIndex, float currentRate)
        {
            return currentRateIndex > 0 || currentRate > 1f;
        }

        internal static int ComputeTargetResourceIndex(
            List<TrajectoryPoint> points, int lastAppliedResourceIndex, double currentUT)
        {
            int targetIndex = lastAppliedResourceIndex;
            for (int j = lastAppliedResourceIndex + 1; j < points.Count; j++)
            {
                if (points[j].ut <= currentUT)
                    targetIndex = j;
                else
                    break;
            }
            return targetIndex;
        }

        /// <summary>
        /// Returns true if a recording's loop should dispatch to the multi-cycle overlap
        /// path. Under the #381 launch-to-launch semantics, overlap is required whenever
        /// the period (launch-to-launch) is shorter than the recording duration —
        /// successive launches are still flying when the next one begins.
        /// </summary>
        internal static bool IsOverlapLoop(double intervalSeconds, double duration)
        {
            return intervalSeconds < duration;
        }

        /// <summary>
        /// Computes the playback UT for a specific cycle of an overlapping-loop recording.
        /// Given the cycle's index and the loop's effective start UT, returns the UT within
        /// [loopStartUT, loopStartUT + duration] that the ghost for that cycle should be at.
        /// Phase is clamped to [0, duration] so callers don't have to special-case boundary
        /// conditions. #409: extracted so WatchModeController.ResolveWatchPlaybackUT and
        /// TryStartWatchSession agree on the reference frame (effective loop start + effective
        /// loop duration), independent of the recording's full [StartUT, EndUT] range.
        /// </summary>
        internal static double ComputeOverlapCycleLoopUT(
            double currentUT, double loopStartUT, double duration,
            double intervalSeconds, long loopCycleIndex)
        {
            return ComputeOverlapCyclePlaybackUT(
                currentUT, loopStartUT, loopStartUT, duration, intervalSeconds, loopCycleIndex);
        }

        internal static double ComputeOverlapCyclePlaybackUT(
            double currentUT, double scheduleStartUT, double playbackStartUT,
            double duration, double intervalSeconds, long loopCycleIndex)
        {
            double cycleDuration = Math.Max(intervalSeconds, LoopTiming.MinCycleDuration);
            double cycleStartUT = scheduleStartUT + loopCycleIndex * cycleDuration;
            double phase = currentUT - cycleStartUT;
            if (phase < 0) phase = 0;
            if (phase > duration) phase = duration;
            return playbackStartUT + phase;
        }

        internal static bool TryComputeLoopPlaybackPhase(
            double currentUT, double scheduleStartUT, double duration, double intervalSeconds,
            out double playbackPhase, out long cycleIndex, out bool inPauseWindow)
        {
            playbackPhase = 0.0;
            cycleIndex = 0;
            inPauseWindow = false;

            if (duration <= 0.0 || currentUT < scheduleStartUT)
                return false;

            double cycleDuration = Math.Max(intervalSeconds, LoopTiming.MinCycleDuration);
            double elapsed = currentUT - scheduleStartUT;
            cycleIndex = (long)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0)
                cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (intervalSeconds > duration && cycleTime > duration + LoopTiming.BoundaryEpsilon)
            {
                inPauseWindow = true;
                playbackPhase = duration;
                return true;
            }

            if (cycleTime < 0.0) cycleTime = 0.0;
            if (cycleTime > duration) cycleTime = duration;
            playbackPhase = cycleTime;
            return true;
        }

        internal static bool TryComputeLoopPlaybackUT(
            double currentUT, double startUT, double endUT, double intervalSeconds,
            out double loopUT, out long cycleIndex)
        {
            loopUT = startUT;
            cycleIndex = 0;

            double duration = endUT - startUT;
            if (duration <= 0 || currentUT < startUT)
                return false;

            // #381: intervalSeconds is the launch-to-launch period, not the post-cycle gap.
            // cycleDuration = period (clamped to MinCycleDuration). Overlap is handled via
            // IsOverlapLoop dispatch; the pause window only exists when period > duration.
            double cycleDuration = Math.Max(intervalSeconds, LoopTiming.MinCycleDuration);

            double elapsed = currentUT - startUT;
            cycleIndex = (long)Math.Floor(elapsed / cycleDuration);
            double phase = elapsed - (cycleIndex * cycleDuration);

            // Pause window only when the period exceeds the duration (gap between cycles).
            // #410: use shared BoundaryEpsilon so ComputeLoopPhaseFromUT stays in sync.
            if (intervalSeconds > duration)
            {
                if (phase > duration + LoopTiming.BoundaryEpsilon)
                    return false;
            }

            if (phase < 0) phase = 0;
            if (phase > duration) phase = duration;

            loopUT = startUT + phase;
            return true;
        }

        /// <summary>
        /// Determines the new watchedOverlapCycleIndex when a watched loop cycle
        /// ends and the ghost is about to be rebuilt. Returns -2 (explosion hold),
        /// -1 (ready for immediate re-target), or unchanged if not watching.
        /// </summary>
        internal static long ComputeWatchCycleOnLoopRebuild(
            long currentWatchCycle, bool isWatching, bool needsExplosion, bool inPauseWindow)
        {
            if (!isWatching) return currentWatchCycle;
            // Already in a hold — don't start another one, let the current hold
            // expire naturally. Otherwise the timer keeps resetting during time warp
            // and the camera never re-targets.
            if (currentWatchCycle == -2) return currentWatchCycle;
            if (needsExplosion && !inPauseWindow) return -2; // hold at explosion site
            return -1; // ready for immediate re-target
        }

        /// <summary>
        /// Computes the range of active loop cycles at a given time.
        /// Under #381 launch-to-launch semantics: when period >= duration, only one cycle
        /// is active at any time (firstActiveCycle == lastActiveCycle). When period &lt; duration,
        /// multiple cycles may be active simultaneously (overlap).
        /// </summary>
        internal static void GetActiveCycles(
            double currentUT, double startUT, double endUT,
            double intervalSeconds, int maxCycles,
            out long firstActiveCycle, out long lastActiveCycle)
        {
            firstActiveCycle = 0;
            lastActiveCycle = 0;

            double duration = endUT - startUT;
            if (duration <= 0 || currentUT < startUT)
                return;

            double cycleDuration = Math.Max(intervalSeconds, LoopTiming.MinCycleDuration);

            double elapsed = currentUT - startUT;
            lastActiveCycle = (long)Math.Floor(elapsed / cycleDuration);
            if (lastActiveCycle < 0) lastActiveCycle = 0;

            // First cycle whose playback hasn't finished yet
            double elapsedMinusDuration = elapsed - duration;
            if (elapsedMinusDuration < 0)
            {
                firstActiveCycle = 0;
            }
            else
            {
                firstActiveCycle = (long)Math.Floor(elapsedMinusDuration / cycleDuration) + 1;
                if (firstActiveCycle < 0) firstActiveCycle = 0;
            }

            // Cap by maxCycles
            if (firstActiveCycle < lastActiveCycle - maxCycles + 1)
                firstActiveCycle = lastActiveCycle - maxCycles + 1;
            if (firstActiveCycle < 0) firstActiveCycle = 0;
        }

        /// <summary>
        /// Computes the runtime launch cadence for an overlap-looped recording so
        /// that the number of simultaneously-live cycles (ceil(duration/cadence))
        /// never exceeds <paramref name="maxCycles"/>. Starts from the
        /// user-requested period (clamped to <see cref="LoopTiming.MinCycleDuration"/>) and
        /// raises it only as far as needed for the cap to fit. Returns the
        /// effective cadence in seconds; the user's stored loop period is
        /// unchanged — only the runtime spawn rate is adjusted. Guarantees the
        /// per-recording ghost clone count stays within the cap without silently
        /// culling cycles mid-trajectory (the pre-fix behaviour stacked ghosts
        /// near launch).
        /// </summary>
        internal static double ComputeEffectiveLaunchCadence(
            double userPeriod, double duration, int maxCycles)
        {
            double period = Math.Max(userPeriod, LoopTiming.MinCycleDuration);
            if (double.IsNaN(period) || double.IsInfinity(period))
                period = LoopTiming.MinCycleDuration;
            if (duration <= 0 || maxCycles <= 0)
                return period;

            // Minimum cadence that keeps ceil(duration / cadence) <= maxCycles.
            double cadenceFloor = duration / maxCycles;
            if (double.IsNaN(cadenceFloor) || double.IsInfinity(cadenceFloor))
                return period;

            // Floating-point division can round the exact floor slightly low,
            // so bump by one ulp until the cycle count fits.
            int safety = 4;
            while (safety-- > 0 && Math.Ceiling(duration / cadenceFloor) > maxCycles)
                cadenceFloor = NextUp(cadenceFloor);

            return Math.Max(period, cadenceFloor);
        }

        private static double NextUp(double value)
        {
            if (double.IsNaN(value) || value == double.PositiveInfinity)
                return value;
            if (value == 0.0)
                return double.Epsilon;

            long bits = BitConverter.DoubleToInt64Bits(value);
            return BitConverter.Int64BitsToDouble(bits + (value > 0.0 ? 1L : -1L));
        }

        /// <summary>
        /// Returns the launch-to-launch period in seconds. Non-negative by contract;
        /// defensively clamped to MinCycleDuration on degenerate input (NaN/inf/&lt;min).
        /// Under #381 the period is independent of recording duration — overlap emerges
        /// naturally when period &lt; duration via the IsOverlapLoop dispatch.
        /// </summary>
        internal static double ResolveLoopInterval(
            IPlaybackTrajectory rec, double globalAutoInterval,
            double defaultInterval, double minCycleDuration)
        {
            if (rec == null) return defaultInterval;

            double interval;
            if (rec.LoopTimeUnit == LoopTimeUnit.Auto)
            {
                interval = double.IsNaN(globalAutoInterval) || double.IsInfinity(globalAutoInterval)
                    ? defaultInterval : globalAutoInterval;
            }
            else
            {
                interval = double.IsNaN(rec.LoopIntervalSeconds) || double.IsInfinity(rec.LoopIntervalSeconds)
                    ? defaultInterval : rec.LoopIntervalSeconds;
            }

            if (interval < minCycleDuration)
            {
                string key = !string.IsNullOrEmpty(rec.RecordingId)
                    ? rec.RecordingId
                    : (rec.VesselName ?? string.Empty);
                if (loopIntervalClampWarned.Add(key))
                {
                    ParsekLog.Warn("Loop",
                        $"ResolveLoopInterval: period {interval.ToString("R", CultureInfo.InvariantCulture)}s below LoopTiming.MinCycleDuration " +
                        $"{minCycleDuration.ToString("R", CultureInfo.InvariantCulture)}s for '{rec.VesselName}' — clamping defensively (#381)");
                }
                return minCycleDuration;
            }
            return interval;
        }

        internal static bool ShouldUseGlobalAutoLaunchQueue(IPlaybackTrajectory traj)
        {
            return traj != null
                && traj.PlaybackEnabled
                && traj.LoopTimeUnit == LoopTimeUnit.Auto
                && GhostPlaybackEngine.ShouldLoopPlayback(traj);
        }

        private static int CompareAutoLoopQueueEntries(AutoLoopQueueEntry a, AutoLoopQueueEntry b)
        {
            int cmp = a.PlaybackStartUT.CompareTo(b.PlaybackStartUT);
            if (cmp != 0)
                return cmp;

            cmp = a.PlaybackEndUT.CompareTo(b.PlaybackEndUT);
            if (cmp != 0)
                return cmp;

            cmp = string.CompareOrdinal(a.RecordingId, b.RecordingId);
            if (cmp != 0)
                return cmp;

            return a.RecordingIndex.CompareTo(b.RecordingIndex);
        }

        internal static bool TryResolveAutoLoopLaunchSchedule(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            int recordingIndex,
            double launchGapSeconds,
            out AutoLoopLaunchSchedule schedule)
        {
            schedule = default(AutoLoopLaunchSchedule);
            if (trajectories == null
                || recordingIndex < 0
                || recordingIndex >= trajectories.Count
                || !ShouldUseGlobalAutoLaunchQueue(trajectories[recordingIndex]))
            {
                return false;
            }

            var queue = new List<AutoLoopQueueEntry>();
            for (int i = 0; i < trajectories.Count; i++)
            {
                var candidate = trajectories[i];
                if (!ShouldUseGlobalAutoLaunchQueue(candidate))
                    continue;

                queue.Add(new AutoLoopQueueEntry(
                    i,
                    GhostPlaybackEngine.EffectiveLoopStartUT(candidate),
                    GhostPlaybackEngine.EffectiveLoopEndUT(candidate),
                    candidate.RecordingId));
            }

            if (queue.Count == 0)
                return false;

            queue.Sort(CompareAutoLoopQueueEntries);

            double anchorUT = queue[0].PlaybackStartUT;
            double cadenceSeconds = launchGapSeconds * queue.Count;
            for (int slot = 0; slot < queue.Count; slot++)
            {
                if (queue[slot].RecordingIndex != recordingIndex)
                    continue;

                schedule = new AutoLoopLaunchSchedule(
                    anchorUT + (slot * launchGapSeconds),
                    cadenceSeconds,
                    slot,
                    queue.Count);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reset the per-recording clamp-warning dedupe set. Test-only — call from Dispose
        /// of any test touching ResolveLoopInterval so state doesn't leak across tests.
        /// </summary>
        internal static void ResetForTesting()
        {
            loopIntervalClampWarned.Clear();
            ResetFlagReplayOverridesForTesting();
        }

        /// <summary>
        /// Test-only override for flag dedup checks. Pass null to restore the live KSP path.
        /// </summary>
        internal static void SetFlagExistsOverrideForTesting(Func<FlagEvent, bool> checker)
        {
            flagExistsOverrideForTesting = checker;
        }

        /// <summary>
        /// Test-only override for flag spawns. Return true to simulate a successful spawn.
        /// Pass null to restore the live KSP path.
        /// </summary>
        internal static void SetSpawnFlagOverrideForTesting(Func<FlagEvent, bool> spawner)
        {
            spawnFlagOverrideForTesting = spawner;
        }

        /// <summary>
        /// Clears all test-only flag replay overrides.
        /// </summary>
        internal static void ResetFlagReplayOverridesForTesting()
        {
            flagExistsOverrideForTesting = null;
            spawnFlagOverrideForTesting = null;
        }

        /// <summary>
        /// Validates that a loop anchor vessel exists. Returns true if the anchor PID
        /// corresponds to a real vessel in the game world. Returns false if the anchor
        /// vessel is missing (loop should be broken / fall back to absolute positioning).
        /// Pure-static decision method using the existing vesselExistsOverride mechanism
        /// for testability.
        /// </summary>
        internal static bool ValidateLoopAnchor(uint anchorPid)
        {
            if (anchorPid == 0)
            {
                ParsekLog.Verbose("Loop", "ValidateLoopAnchor: anchorPid=0, no anchor configured");
                return false;
            }

            bool exists = RealVesselExists(anchorPid);
            if (exists)
            {
                ParsekLog.Verbose("Loop", $"ValidateLoopAnchor: anchor pid={anchorPid} found");
            }
            else
            {
                ParsekLog.Warn("Loop", $"ValidateLoopAnchor: anchor pid={anchorPid} NOT found — loop anchor broken");
            }
            return exists;
        }

        /// <summary>
        /// Determines whether a looping recording should use anchor-relative positioning.
        /// Returns true if the recording has a LoopAnchorVesselId set AND has RELATIVE
        /// TrackSections that contain the offset data needed for relative playback.
        /// Pure static for testability.
        /// </summary>
        internal static bool ShouldUseLoopAnchor(IPlaybackTrajectory rec)
        {
            if (rec == null || rec.LoopAnchorVesselId == 0)
                return false;

            // Only use anchor-relative mode if the recording has RELATIVE TrackSections.
            // Legacy recordings with absolute positions don't have offset data, so the
            // anchor has no effect on them.
            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
                return false;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                if (rec.TrackSections[i].referenceFrame == ReferenceFrame.Relative)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Computes where a looped recording should be in its cycle at a given UT.
        /// Returns the "loop UT" -- the position within the recording's timeline that
        /// the ghost should be at right now.
        ///
        /// Under #381 launch-to-launch semantics: cycles repeat every <paramref name="intervalSeconds"/>
        /// (the period). When period &gt; duration there is a pause window at the tail. When
        /// period &lt;= duration cycles are back-to-back or overlap and there is no pause.
        ///
        /// NOTE: this is a *phase* helper — it does not own the overlap dispatch. Callers that
        /// need overlap (multiple simultaneous ghosts) should consult IsOverlapLoop and use
        /// GetActiveCycles. This helper collapses the question "where would a single ghost be".
        /// Pre-#381 code treated negative intervals as zero-pause / continuous playback; under
        /// the new contract negative values are clamped to MinCycleDuration (extreme overlap),
        /// consistent with ResolveLoopInterval / TryComputeLoopPlaybackUT.
        ///
        /// Returns (loopUT, cycleIndex, isInPause):
        ///   loopUT: the UT within [StartUT, EndUT] to position the ghost at
        ///   cycleIndex: which cycle we're in (0-based)
        ///   isInPause: true if currentUT falls in the pause interval between cycles
        /// </summary>
        internal static (double loopUT, long cycleIndex, bool isInPause) ComputeLoopPhaseFromUT(
            double currentUT,
            double recordingStartUT,
            double recordingEndUT,
            double intervalSeconds)
        {
            // Early guard: currentUT before recording start — consistent with TryComputeLoopPlaybackUT
            if (currentUT < recordingStartUT)
            {
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: currentUT={currentUT:R} before recordingStartUT={recordingStartUT:R}, returning startUT");
                return (recordingStartUT, 0, false);
            }

            double duration = recordingEndUT - recordingStartUT;
            if (duration <= 0)
            {
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: zero/negative duration={duration:R}, returning startUT");
                return (recordingStartUT, 0, false);
            }

            // #381: period = intervalSeconds (launch-to-launch). Defensively clamp to MinCycleDuration.
            double cycleDuration = Math.Max(intervalSeconds, LoopTiming.MinCycleDuration);

            double elapsed = currentUT - recordingStartUT;

            long cycleIndex = (long)(elapsed / cycleDuration);
            double phaseInCycle = elapsed - (cycleIndex * cycleDuration);

            // #410: epsilon-tolerant boundary. At phaseInCycle == duration (exact cycle end)
            // we treat the ghost as still playing its final frame — the visual is at endUT
            // either way, but reporting isInPause=false here keeps us consistent with
            // TryComputeLoopPlaybackUT (which uses `phase > duration + epsilon`) and avoids
            // a one-frame pause-state flicker at exact cycle boundaries.
            if (phaseInCycle <= duration + LoopTiming.BoundaryEpsilon)
            {
                // In the playback portion (clamp phase to duration so loopUT == endUT at the boundary).
                double clampedPhase = phaseInCycle > duration ? duration : phaseInCycle;
                double loopUT = recordingStartUT + clampedPhase;
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: cycleIndex={cycleIndex}, loopUT={loopUT:R}, isInPause=false (phase={phaseInCycle:R}/{duration:R})");
                return (loopUT, cycleIndex, false);
            }
            else
            {
                // In the pause interval between cycles (only reachable when period > duration).
                // Ghost should be at the end position (or hidden)
                ParsekLog.Verbose("Loop", $"ComputeLoopPhaseFromUT: cycleIndex={cycleIndex}, loopUT={recordingEndUT:R}, isInPause=true (phase={phaseInCycle:R}/{duration:R})");
                return (recordingEndUT, cycleIndex, true);
            }
        }

        /// <summary>
        /// Determines whether a looped ghost should be spawned for a recording
        /// whose anchor vessel just loaded. Returns false if:
        /// - Recording is not looping
        /// - No anchor vessel ID set
        /// - Anchor vessel doesn't exist
        /// - Anchor vessel body mismatch (wrong celestial body)
        /// </summary>
        internal static bool ShouldSpawnLoopedGhost(
            IPlaybackTrajectory rec,
            bool anchorVesselExists,
            string anchorBodyName,
            string recordingBodyName)
        {
            if (rec == null)
            {
                ParsekLog.Verbose("Loop", "ShouldSpawnLoopedGhost: rec is null, returning false");
                return false;
            }

            if (!rec.LoopPlayback)
            {
                ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' not looping, returning false");
                return false;
            }

            if (rec.LoopAnchorVesselId == 0)
            {
                ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' has no anchor vessel, returning false");
                return false;
            }

            if (!anchorVesselExists)
            {
                ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' anchor pid={rec.LoopAnchorVesselId} not found, returning false");
                return false;
            }

            // Body validation: anchor must be on the same body as when recorded
            if (!string.IsNullOrEmpty(recordingBodyName) &&
                !string.IsNullOrEmpty(anchorBodyName) &&
                anchorBodyName != recordingBodyName)
            {
                ParsekLog.Warn("Loop",
                    $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' anchor vessel body mismatch: " +
                    $"expected={recordingBodyName}, actual={anchorBodyName} — loop broken");
                return false;
            }

            ParsekLog.Verbose("Loop", $"ShouldSpawnLoopedGhost: rec '{rec.VesselName}' anchor pid={rec.LoopAnchorVesselId} valid, returning true");
            return true;
        }

        /// <summary>
        /// Pure-static gating check: determines whether a looped recording with an anchor
        /// should have its ghost active right now. Returns true if:
        /// - The recording has no anchor (anchorPid == 0) — unanchored loops always run
        /// - The anchor vessel PID is in the loadedAnchors set
        /// Returns false if the recording has an anchor and it is not loaded.
        /// </summary>
        internal static bool IsAnchorLoaded(uint anchorPid, HashSet<uint> loadedAnchors)
        {
            if (anchorPid == 0)
                return true; // No anchor configured — always allow

            if (loadedAnchors == null)
            {
                ParsekLog.Verbose("Loop", $"IsAnchorLoaded: loadedAnchors set is null, anchorPid={anchorPid} — returning false");
                return false;
            }

            bool loaded = loadedAnchors.Contains(anchorPid);
            ParsekLog.Verbose("Loop", $"IsAnchorLoaded: anchorPid={anchorPid}, loaded={loaded}");
            return loaded;
        }

        #endregion

        #region External Vessel Ghost Policy

        /// <summary>
        /// Injectable override for vessel existence checks (null = use FlightGlobals).
        /// Set via SetVesselExistsOverrideForTesting for unit tests.
        /// </summary>
        private static Func<uint, bool> vesselExistsOverride;

        // Frame-cached vessel PID set for O(1) lookup. Invalidated manually per frame
        // via InvalidateVesselCache(). Using manual invalidation instead of Time.frameCount
        // because Unity native properties crash in the test environment.
        private static HashSet<uint> cachedVesselPids;
        private static bool vesselCacheValid;

        /// <summary>
        /// Injectable override for chain-ghosted vessel checks (null = assume not ghosted).
        /// Set via SetIsGhostedOverride for unit tests; in production, wired to VesselGhoster.IsGhosted.
        /// </summary>
        private static Func<uint, bool> isGhostedOverride;

        /// <summary>
        /// Sets an injectable override for RealVesselExists, enabling unit testing
        /// without FlightGlobals. Pass null to restore default behavior.
        /// </summary>
        internal static void SetVesselExistsOverrideForTesting(Func<uint, bool> finder)
        {
            vesselExistsOverride = finder;
        }

        /// <summary>
        /// Sets an injectable override for IsGhostedByChain, enabling unit testing
        /// without VesselGhoster. Pass null to restore default behavior (not ghosted).
        /// </summary>
        internal static void SetIsGhostedOverride(Func<uint, bool> checker)
        {
            isGhostedOverride = checker;
        }

        /// <summary>
        /// Resets the injectable is-ghosted override. Call from test Dispose.
        /// </summary>
        internal static void ResetIsGhostedOverride()
        {
            isGhostedOverride = null;
        }

        /// <summary>
        /// Checks if a real vessel with the given persistentId currently exists in the game.
        /// If it exists, no ghost should be spawned (the real vessel serves as its own visual).
        /// If it doesn't exist, a fallback ghost should be spawned from stored background data.
        /// Uses injectable override when set (for testing).
        /// </summary>
        internal static bool RealVesselExists(uint vesselPersistentId)
        {
            if (vesselPersistentId == 0) return false;

            if (vesselExistsOverride != null)
                return vesselExistsOverride(vesselPersistentId);

            if (FlightGlobals.Vessels == null) return false;

            if (!vesselCacheValid)
            {
                if (cachedVesselPids == null)
                    cachedVesselPids = new HashSet<uint>();
                else
                    cachedVesselPids.Clear();

                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    if (FlightGlobals.Vessels[i] != null
                        && !GhostMapPresence.IsGhostMapVessel(FlightGlobals.Vessels[i].persistentId))
                        cachedVesselPids.Add(FlightGlobals.Vessels[i].persistentId);
                }
                vesselCacheValid = true;
                ParsekLog.VerboseRateLimited("Flight", "vessel-cache-rebuild",
                    $"RealVesselExists: rebuilt vessel PID cache ({cachedVesselPids.Count} vessels)");
            }

            return cachedVesselPids.Contains(vesselPersistentId);
        }

        /// <summary>
        /// Checks if a vessel is ghosted by a chain (despawned by VesselGhoster).
        /// Uses injectable override when set (for testing); defaults to false when
        /// no override is configured (no chain system active).
        /// </summary>
        private static bool IsGhostedByChain(uint vesselPersistentId)
        {
            if (isGhostedOverride != null)
                return isGhostedOverride(vesselPersistentId);
            return false;
        }

        /// <summary>
        /// Pure decision method: determines whether a ghost should be skipped for an
        /// external background vessel whose real vessel still exists in the game world.
        /// An "external vessel" is a tree recording that was tracked via BackgroundMap
        /// (not the active vessel) and whose VesselPersistentId matches a live vessel.
        /// Returns true if the ghost should be skipped.
        /// </summary>
        internal static bool ShouldSkipExternalVesselGhost(
            string treeId, uint vesselPersistentId, bool isActiveRecording)
        {
            // Only applies to tree recordings (standalone recordings don't have BackgroundMap)
            if (string.IsNullOrEmpty(treeId)) return false;

            // Active recording is the player's own vessel — always spawn its ghost
            if (isActiveRecording) return false;

            // PID 0 means we don't know the vessel — can't check existence
            if (vesselPersistentId == 0) return false;

            // Phase 6b: If vessel is ghosted by a chain, do NOT skip.
            // The real vessel has been despawned — the background recording
            // data must produce a ghost for the chain.
            if (IsGhostedByChain(vesselPersistentId))
            {
                ParsekLog.Verbose("Ghoster",
                    $"ShouldSkipExternalVesselGhost: vessel pid={vesselPersistentId} " +
                    "is ghosted by chain — NOT skipping");
                return false;
            }

            // Tree-owned vessel: if the recording's tree has recordings with this PID,
            // the vessel is part of the tree's own flight history — always show the ghost
            // so the user can see the recorded trajectory replayed. The real vessel may sit
            // at its save-time position, which is different from the ghost's interpolated path.
            if (IsVesselRecordedByTree(treeId, vesselPersistentId))
                return false;

            if (RealVesselExists(vesselPersistentId))
            {
                ParsekLog.Verbose("Flight",
                    $"Skipping external vessel ghost: real vessel {vesselPersistentId} exists");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks whether the given vessel PID appears anywhere in the same tree's recordings.
        /// This is tree-local recorded-history membership, not a global ownership claim.
        /// Uses the cached RecordedVesselPids set on RecordingTree for O(1) lookup.
        /// </summary>
        internal static bool IsVesselRecordedByTree(string treeId, uint vesselPersistentId)
        {
            if (string.IsNullOrEmpty(treeId) || vesselPersistentId == 0) return false;

            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Id == treeId)
                    return trees[i].RecordedVesselPids.Contains(vesselPersistentId);
            }
            return false;
        }

        /// <summary>
        /// Resets the injectable vessel-exists override. Call from test Dispose.
        /// </summary>
        internal static void ResetVesselExistsOverride()
        {
            vesselExistsOverride = null;
        }

        /// <summary>
        /// Invalidate the vessel PID cache. Call once per frame before any
        /// RealVesselExists calls (e.g., first line of UpdateTimelinePlaybackViaEngine).
        /// </summary>
        internal static void InvalidateVesselCache()
        {
            vesselCacheValid = false;
        }

        /// <summary>
        /// Reset vessel cache state for testing. Clears cache and invalidation flag.
        /// Call alongside ResetVesselExistsOverride in test teardown.
        /// </summary>
        internal static void ResetVesselCacheForTesting()
        {
            cachedVesselPids = null;
            vesselCacheValid = false;
        }

        #endregion

        #region Ghost Info Population

        /// <summary>
        /// Builds a Dictionary keyed by partPersistentId from a list of ghost info items.
        /// Shared helper for the 6 simple PID-keyed dict constructions in PopulateGhostInfoDictionaries.
        /// </summary>
        private static Dictionary<uint, T> BuildDictByPid<T>(List<T> items, Func<T, uint> getPid)
        {
            var dict = new Dictionary<uint, T>();
            for (int i = 0; i < items.Count; i++)
                dict[getPid(items[i])] = items[i];
            return dict;
        }

        /// <summary>
        /// Converts a GhostBuildResult into the per-PID dictionaries on GhostPlaybackState.
        /// Shared between SpawnTimelineGhost and StartPlayback to eliminate code duplication.
        /// </summary>
        internal static void PopulateGhostInfoDictionaries(
            GhostPlaybackState state, GhostBuildResult result,
            IPlaybackTrajectory traj = null)
        {
            if (result == null) return;

            if (result.parachuteInfos != null)
                state.parachuteInfos = BuildDictByPid(result.parachuteInfos, p => p.partPersistentId);

            if (result.jettisonInfos != null)
                state.jettisonInfos = BuildDictByPid(result.jettisonInfos, j => j.partPersistentId);

            PopulateEngineInfos(state, result);

            if (result.deployableInfos != null)
                state.deployableInfos = BuildDictByPid(result.deployableInfos, d => d.partPersistentId);

            PopulateHeatInfos(state, result);
            PopulateLightInfos(state, result);

            if (result.fairingInfos != null)
                state.fairingInfos = BuildDictByPid(result.fairingInfos, f => f.partPersistentId);

            PopulateRcsInfos(state, result);
            PopulateRoboticInfos(state, result);

            if (result.colorChangerInfos != null)
                state.colorChangerInfos = GhostVisualBuilder.GroupColorChangersByPartId(result.colorChangerInfos);

            state.compoundPartInfos = result.compoundPartInfos;

            PopulateAudioInfos(state, result);

            state.oneShotAudio = result.oneShotAudio;

            AutoStartOrphanEnginePlayback(state, traj);
        }

        private static void PopulateEngineInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.engineInfos != null)
            {
                state.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
                for (int i = 0; i < result.engineInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.engineInfos[i].partPersistentId, result.engineInfos[i].moduleIndex);
                    state.engineInfos[key] = result.engineInfos[i];
                }
            }
        }

        private static void PopulateHeatInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.heatInfos != null)
            {
                state.heatInfos = BuildDictByPid(result.heatInfos, h => h.partPersistentId);

                // Initialize all heat parts to cold state at spawn — ensures FXModuleAnimateThrottle
                // parts don't inherit the prefab's baked emissive state.
                foreach (var kvp in state.heatInfos)
                {
                    var coldEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyHeatState(state, coldEvt, HeatLevel.Cold);
                }
            }
        }

        private static void PopulateLightInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.lightInfos != null)
            {
                state.lightInfos = BuildDictByPid(result.lightInfos, l => l.partPersistentId);
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();
                for (int i = 0; i < result.lightInfos.Count; i++)
                    state.lightPlaybackStates[result.lightInfos[i].partPersistentId] = new LightPlaybackState();
            }
        }

        private static void PopulateRcsInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.rcsInfos != null)
            {
                state.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
                for (int i = 0; i < result.rcsInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.rcsInfos[i].partPersistentId, result.rcsInfos[i].moduleIndex);
                    state.rcsInfos[key] = result.rcsInfos[i];
                }
            }
        }

        private static void PopulateRoboticInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.roboticInfos != null)
            {
                state.roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
                for (int i = 0; i < result.roboticInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.roboticInfos[i].partPersistentId, result.roboticInfos[i].moduleIndex);
                    state.roboticInfos[key] = result.roboticInfos[i];
                }
            }
        }

        private static void PopulateAudioInfos(GhostPlaybackState state, GhostBuildResult result)
        {
            if (result.audioInfos != null)
            {
                state.audioInfos = new Dictionary<ulong, AudioGhostInfo>();
                for (int i = 0; i < result.audioInfos.Count; i++)
                {
                    result.audioInfos[i].selectionOrder = i;
                    ulong key = FlightRecorder.EncodeEngineKey(
                        result.audioInfos[i].partPersistentId, result.audioInfos[i].moduleIndex);
                    state.audioInfos[key] = result.audioInfos[i];
                }
            }
        }

        private static void AutoStartOrphanEnginePlayback(
            GhostPlaybackState state,
            IPlaybackTrajectory traj)
        {
            // Build engine event key set for orphan detection (scan over PartEvents).
            // Debris boosters that were running at breakup have no seed events because
            // BackgroundRecorder.InitializeLoadedState finds engine.isOperational=false
            // (fuel severed by decouple). When the key set is empty (ZERO engine events),
            // all engines on the ghost are auto-started — targeting pure debris recordings.
            // RCS is NOT auto-started (RCS is typically idle; orphan auto-start would
            // incorrectly fire on virtually every ghost).
            HashSet<ulong> engineKeysWithEvents = null;
            bool hasEngineOrAudioInfos = (state.audioInfos != null && state.audioInfos.Count > 0)
                || (state.engineInfos != null && state.engineInfos.Count > 0);
            if (hasEngineOrAudioInfos && traj != null && traj.PartEvents != null)
                engineKeysWithEvents = BuildEngineEventKeySet(traj.PartEvents);

            // Auto-start audio + visual FX for ALL engines on recordings with ZERO engine
            // events. This targets pure debris recordings: boosters that were running at
            // breakup but got no seed events. If the recording has ANY engine events,
            // engines without events were legitimately idle (e.g., Poodle during first
            // stage) or already shut down before breakup — not running orphans.
            if (engineKeysWithEvents != null && engineKeysWithEvents.Count == 0)
            {
                // Audio auto-start
                if (state.audioInfos != null && state.audioInfos.Count > 0)
                {
                    foreach (var kvp in state.audioInfos)
                    {
                        kvp.Value.currentPower = 1f;
                        if (kvp.Value.audioSource != null)
                        {
                            kvp.Value.audioSource.volume = 0f; // will be set by UpdateAudioAtmosphere
                            kvp.Value.audioSource.loop = true;
                        }
                        ParsekLog.Verbose("GhostAudio",
                            $"Auto-started audio for orphan engine key={kvp.Key} " +
                            $"(no engine events in recording — likely debris booster)");
                    }

                    EnforceLoopedAudioPlaybackCap(state);
                }

                // Engine FX auto-start
                if (state.engineInfos != null && state.engineInfos.Count > 0)
                {
                    foreach (var kvp in state.engineInfos)
                    {
                        uint pid; int midx;
                        FlightRecorder.DecodeEngineKey(kvp.Key, out pid, out midx);
                        // eventType unused by SetEngineEmission — only pid+midx matter
                        var syntheticEvt = new PartEvent { partPersistentId = pid, moduleIndex = midx };
                        SetEngineEmission(state, syntheticEvt, 1f);
                        ParsekLog.Verbose("GhostFx",
                            $"Auto-started engine FX for orphan engine key={kvp.Key} pid={pid} midx={midx} " +
                            $"(no engine events in recording — likely debris booster)");
                    }
                }
            }
        }

        /// <summary>
        /// Builds a set of engine event keys from a list of PartEvents.
        /// Keys represent (pid, moduleIndex) pairs that have at least one engine
        /// event (EngineIgnited, EngineThrottle, or EngineShutdown). Used by orphan
        /// auto-start: when the set is empty, ALL engines on the ghost are
        /// auto-started. EngineShutdown is included so that dead-engine sentinel
        /// seeds (#298) prevent the auto-start from firing on debris with depleted
        /// fuel. Pure static method for testability.
        /// </summary>
        internal static HashSet<ulong> BuildEngineEventKeySet(List<PartEvent> partEvents)
        {
            var keys = new HashSet<ulong>();
            if (partEvents == null) return keys;

            for (int pe = 0; pe < partEvents.Count; pe++)
            {
                var evt = partEvents[pe];
                if (evt.eventType == PartEventType.EngineIgnited
                    || evt.eventType == PartEventType.EngineThrottle
                    || evt.eventType == PartEventType.EngineShutdown)
                    keys.Add(FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex));
            }
            return keys;
        }

        #endregion

        #region Explosion / Visibility

        /// <summary>
        /// Pure decision logic: should we trigger an explosion for this ghost/recording pair?
        /// Extracted for testability and logging of guard condition skips.
        /// Parameters are primitives so this can be called from tests without GhostPlaybackState.
        /// </summary>
        internal static bool ShouldTriggerExplosion(bool explosionAlreadyFired, TerminalState? terminalState,
            bool ghostExists, string vesselName, int recIdx)
        {
            if (explosionAlreadyFired)
                return false;
            if (terminalState != TerminalState.Destroyed)
                return false;
            if (!ghostExists)
            {
                return false;
            }
            return true;
        }

        internal static bool TryGetEarlyDestroyedDebrisExplosionUT(
            IPlaybackTrajectory traj, out double explosionUT)
        {
            explosionUT = double.NaN;

            if (traj == null || !traj.IsDebris || traj.TerminalStateValue != TerminalState.Destroyed)
                return false;

            if (traj.PartEvents == null || traj.PartEvents.Count == 0)
                return false;

            double latestEligibleUT = traj.EndUT - LoopTiming.MinEarlyDebrisExplosionLeadSeconds;
            if (latestEligibleUT <= traj.StartUT)
                return false;

            double earliestEligibleUT = double.NaN;
            for (int i = 0; i < traj.PartEvents.Count; i++)
            {
                var evt = traj.PartEvents[i];
                if (evt.eventType != PartEventType.Destroyed)
                    continue;
                if (evt.ut < traj.StartUT)
                    continue;
                if (evt.ut > latestEligibleUT)
                    continue;

                if (double.IsNaN(earliestEligibleUT) || evt.ut < earliestEligibleUT)
                    earliestEligibleUT = evt.ut;
            }

            if (double.IsNaN(earliestEligibleUT))
                return false;

            explosionUT = earliestEligibleUT;
            return true;
        }

        internal static bool ShouldTriggerExplosionAtPlaybackUT(
            IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj == null || traj.TerminalStateValue != TerminalState.Destroyed)
                return false;

            if (double.IsNaN(playbackUT) || double.IsInfinity(playbackUT))
                return false;

            if (TryGetEarlyDestroyedDebrisExplosionUT(traj, out double earlyExplosionUT))
                return playbackUT >= earlyExplosionUT;

            return playbackUT >= traj.EndUT;
        }

        internal static void HideAllGhostParts(GhostPlaybackState state)
        {
            if (state.ghost == null) return;
            MuteAllAudio(state);
            var t = state.ghost.transform;
            int hidden = 0;
            // Keep cameraPivot active — FlightCamera targets it during watch-mode hold.
            // Disabling it would make KSP snap the camera back to the active vessel.
            var pivotT = state.cameraPivot;
            for (int c = 0; c < t.childCount; c++)
            {
                var child = t.GetChild(c);
                if (pivotT != null && child == pivotT) continue;
                if (child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(false);
                    hidden++;
                }
            }
        }

        /// <summary>
        /// #406 follow-up: re-activates every ghost_part_* GameObject under the
        /// ghost's visuals container so the next loop cycle plays back from the
        /// snapshot baseline. Production ghosts created by
        /// <c>BuildTimelineGhostFromSnapshot</c> parent every ghost_part_ under
        /// a dedicated `ghost_visuals` container (see
        /// <c>GhostVisualBuilder.EnsureGhostVisualsRoot</c>); part visibility is
        /// toggled via <c>SetGhostPartActive</c> which looks up parts inside
        /// that container. Walking `state.ghost.transform` directly would miss
        /// every real part (it would only see the container + cameraPivot +
        /// horizonProxy). Call site: loop-cycle-reuse path in
        /// <c>GhostPlaybackEngine</c>. Returns the number of parts re-activated
        /// — used by the Verbose log line at the reuse call site.
        /// </summary>
        internal static int ReactivateGhostPartHierarchyForLoopRewind(GhostPlaybackState state)
        {
            if (state == null || state.ghost == null) return 0;
            // Reuse the same lookup SetGhostPartActive uses so "part hierarchy"
            // here means the same thing as at the playback event sites.
            var partContainer = GhostVisualBuilder.GetGhostPartContainer(state.ghost.transform);
            if (partContainer == null) return 0;
            int reactivated = 0;
            for (int c = 0; c < partContainer.childCount; c++)
            {
                var child = partContainer.GetChild(c);
                if (!child.gameObject.activeSelf)
                {
                    child.gameObject.SetActive(true);
                    reactivated++;
                }
            }
            return reactivated;
        }

        /// <summary>
        /// #406 follow-up: per-cycle state reset for the loop-cycle ghost reuse
        /// path. Pure-logic helper (no Unity API calls) — safe to invoke from
        /// xUnit tests. Mirrors the spawn-time baseline for iterators, per-cycle
        /// flags, AND the mutable playback fields stored inside the preserved
        /// module dictionaries (EngineGhostInfo.currentPower etc.), while
        /// PRESERVING the snapshot-derived dictionary references, the ghost
        /// GameObject, the reentry FX info, and the reentry-FX pending-build
        /// flag (#450 B3 — clearing the flag would re-pay the ~7 ms build
        /// every cycle). See <c>docs/dev/plan-406-ghost-reuse-loop-cycles.md</c>
        /// for the field-by-field preservation table.
        ///
        /// Unity-touching cleanup (RCS emission restore, fake canopy GameObject
        /// destroy) is deliberately NOT invoked here — the engine's
        /// <c>ReusePrimaryGhostAcrossCycle</c> orchestrator calls those helpers
        /// separately immediately after this one. Pulling them into this method
        /// would trip <c>System.Security.SecurityException</c> in xUnit runs
        /// because the JIT loads Unity type references at method-verify time,
        /// even if the early-returns prevent their execution.
        /// </summary>
        internal static void ResetForLoopCycle(GhostPlaybackState state, long newCycleIndex)
        {
            if (state == null) return;

            // Playback iterators rewind to cycle start.
            state.playbackIndex = 0;
            state.partEventIndex = 0;
            state.flagEventIndex = 0;
            state.appearanceCount = 0;
            state.hadVisibleRenderersLastFrame = false;
            state.loopCycleIndex = newCycleIndex;

            // Per-cycle flags reset to spawn baseline — the new cycle re-decides.
            state.explosionFired = false;
            state.pauseHidden = false;
            state.rcsSuppressed = false;

            // Audio state machine: next frame's atmosphere/mute pipeline
            // re-decides. Atmosphere factor resets to 1 (matches spawn).
            state.audioMuted = false;
            state.atmosphereFactor = 1f;

            // Per-part runtime state accrued from events (light blink state,
            // logical-pid presence set). Events on the new cycle repopulate
            // these; `logicalPartIds` is restored by the reuse orchestrator
            // via BuildSnapshotPartIdSet because that helper requires the
            // snapshot ConfigNode which this pure static doesn't have access
            // to. `fakeCanopies` entries must be destroyed — the engine
            // orchestrator calls DestroyAllFakeCanopies() separately because
            // it invokes Unity's Object.Destroy.
            state.lightPlaybackStates?.Clear();

            // Mutable playback fields INSIDE the preserved module dictionaries:
            // a fresh spawn constructs new info objects with these fields at
            // their default (zero). Reuse must match that baseline or the
            // first-visible frame can reapply stale engine throttle / robotic
            // servo / color-charge / reentry-intensity state from the previous
            // cycle before the new cycle's events have fired. Nullable-safe
            // loops — any of these dictionaries can legitimately be null
            // (trajectory had no engines, no RCS, no robotic parts, etc.).
            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                    if (info != null) info.currentPower = 0f;
            }
            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                    if (info != null) info.currentPower = 0f;
            }
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                    if (info != null) info.currentPower = 0f;
            }
            if (state.roboticInfos != null)
            {
                foreach (var info in state.roboticInfos.Values)
                {
                    if (info == null) continue;
                    info.currentValue = 0f;
                    info.active = false;
                    info.lastUpdateUT = double.NaN;
                }
            }
            if (state.colorChangerInfos != null)
            {
                foreach (var list in state.colorChangerInfos.Values)
                {
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] != null)
                            list[i].peakCharIntensity = 0f;
                    }
                }
            }
            if (state.reentryFxInfo != null)
                state.reentryFxInfo.lastIntensity = 0f;

            // Reset fresh-spawn visibility deferral so the first positioned
            // frame activates the ghost the same way a fresh spawn would.
            state.deferVisibilityUntilPlaybackSync = true;

            // Deliberately NOT reset: `fidelityReduced`, `distanceLodReduced`,
            // `fidelityDisabledRenderers`, `simplified`. These track
            // distance-LOD state that is re-evaluated every frame by
            // `ApplyDistanceLodFidelity` + `ApplyZonePolicy`. If a
            // prior-cycle-decoupled part that was on the disabled-renderers
            // list is now reactivated by ReactivateGhostPartHierarchyForLoopRewind,
            // the list holds a reference to a renderer that is briefly
            // visible again — but the next ApplyDistanceLodFidelity pass
            // re-walks active renderers and re-disables anything still out
            // of range, so the list self-corrects within one frame. Pre-#406
            // behaviour discarded the list with DestroyGhost; the reuse
            // path preserves it because rebuilding would churn the LOD
            // state machine without gameplay benefit.
        }

        /// <summary>
        /// #406 follow-up: re-apply spawn-time Unity-touching module baselines
        /// after a loop-cycle reuse. Fresh spawn does three things that
        /// <see cref="ResetForLoopCycle"/> cannot (because they touch Unity):
        ///  1. Heat parts: reset every <c>HeatGhostInfo</c> to
        ///     <see cref="HeatLevel.Cold"/> via <c>ApplyHeatState</c>, so a
        ///     cycle whose prior pass went hot does not carry that emission
        ///     into the new cycle's pre-reentry frames.
        ///  2. Deployable parts: reset every transform in
        ///     <c>DeployableGhostInfo</c> to its stowed pose, so a solar
        ///     panel that deployed mid-cycle is folded again before the new
        ///     cycle's events re-deploy it on schedule.
        ///  3. Jettison panels: reactivate jettisoned panels (SetActive true)
        ///     so the new cycle's jettison events can re-fire.
        ///  4. Orphan engine/audio auto-start: for recordings with ZERO
        ///     engine events (typical of pure debris boosters that were
        ///     running at breakup), re-fire the fresh-spawn auto-start logic
        ///     so plume/audio come back on the second cycle onward. Without
        ///     this, the first cycle has orphan FX but the second cycle
        ///     loses them silently.
        /// Must be called from the engine orchestrator AFTER
        /// <see cref="ResetForLoopCycle"/> and
        /// <see cref="ReactivateGhostPartHierarchyForLoopRewind"/> and BEFORE
        /// the next <c>PrimeLoadedGhostForPlaybackUT</c> / <c>ApplyFrameVisuals</c>
        /// call. All three branches no-op on null inputs.
        /// </summary>
        internal static void ReapplySpawnTimeModuleBaselinesForLoopCycle(
            GhostPlaybackState state, IPlaybackTrajectory traj)
        {
            if (state == null || state.ghost == null) return;

            // 1. Heat: reset every part to cold.
            if (state.heatInfos != null)
            {
                foreach (var kvp in state.heatInfos)
                {
                    var coldEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyHeatState(state, coldEvt, HeatLevel.Cold);
                }
            }

            // 2. Deployables: re-stow every panel. Events during the new
            //    cycle re-deploy on their original UT.
            if (state.deployableInfos != null)
            {
                foreach (var kvp in state.deployableInfos)
                {
                    var stowedEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyDeployableState(state, stowedEvt, deployed: false);
                }
            }

            // 3. Jettison panels: re-attach every panel. Jettison events
            //    during the new cycle hide them again on their original UT.
            if (state.jettisonInfos != null)
            {
                foreach (var kvp in state.jettisonInfos)
                {
                    var attachedEvt = new PartEvent { partPersistentId = kvp.Key };
                    ApplyJettisonPanelState(state, attachedEvt, jettisoned: false);
                }
            }

            // 3b. Parachutes: re-stow canopies (localScale = Vector3.zero, the
            //     spawn-time baseline from TryBuildParachuteInfo at
            //     GhostVisualBuilder.cs line ~4539) and re-activate caps so
            //     packs that cut / destroyed / deployed in the prior cycle
            //     are back to their pre-launch pose. Destroy any fake canopy
            //     left over from a prior ParachuteDeployed event so the new
            //     cycle's event can re-create it fresh.
            if (state.parachuteInfos != null)
            {
                foreach (var kvp in state.parachuteInfos)
                {
                    ParachuteGhostInfo info = kvp.Value;
                    if (info == null) continue;
                    if (info.canopyTransform != null)
                        info.canopyTransform.localScale = UnityEngine.Vector3.zero;
                    if (info.capTransform != null)
                        info.capTransform.gameObject.SetActive(true);
                }
            }
            DestroyAllFakeCanopies(state);

            // 3c. Fairings: re-activate fairing mesh so a FairingJettisoned
            //     event from the prior cycle is undone. Events during the
            //     new cycle re-hide on their original UT.
            if (state.fairingInfos != null)
            {
                foreach (var kvp in state.fairingInfos)
                {
                    FairingGhostInfo info = kvp.Value;
                    if (info == null || info.fairingMeshObject == null) continue;
                    info.fairingMeshObject.SetActive(true);
                }
            }

            // 3d. Lights: force every Light component to disabled so a lamp
            //     that was ON at cycle-end does not stay on during the new
            //     cycle's pre-event window. ResetForLoopCycle already cleared
            //     lightPlaybackStates, so UpdateBlinkingLights would not
            //     iterate until events repopulate the dict — without this
            //     explicit SetLightState(false), the Unity Light.enabled flag
            //     stays at its prior value. The fresh-spawn path converges on
            //     "all off" only after UpdateBlinkingLights runs once with
            //     lightPlaybackStates populated; this short-circuits the
            //     transient window where lamps appear stuck on.
            if (state.lightInfos != null)
            {
                foreach (var kvp in state.lightInfos)
                    SetLightState(state, kvp.Key, false);
            }

            // 4. Orphan engine/audio auto-start: duplicates the zero-engine-event
            //    branch of TryPopulateGhostVisuals so a debris-booster recording
            //    with no engine events keeps its plume + audio across loop cycles.
            bool hasEngineOrAudioInfos =
                (state.audioInfos != null && state.audioInfos.Count > 0)
                || (state.engineInfos != null && state.engineInfos.Count > 0);
            if (!hasEngineOrAudioInfos || traj == null || traj.PartEvents == null) return;
            HashSet<ulong> engineKeysWithEvents = BuildEngineEventKeySet(traj.PartEvents);
            if (engineKeysWithEvents.Count != 0) return;

            if (state.audioInfos != null)
            {
                foreach (var kvp in state.audioInfos)
                {
                    kvp.Value.currentPower = 1f;
                    if (kvp.Value.audioSource != null)
                    {
                        kvp.Value.audioSource.volume = 0f;
                        kvp.Value.audioSource.loop = true;
                    }
                }

                EnforceLoopedAudioPlaybackCap(state);
            }

            if (state.engineInfos != null)
            {
                foreach (var kvp in state.engineInfos)
                {
                    uint pid; int midx;
                    FlightRecorder.DecodeEngineKey(kvp.Key, out pid, out midx);
                    var syntheticEvt = new PartEvent { partPersistentId = pid, moduleIndex = midx };
                    SetEngineEmission(state, syntheticEvt, 1f);
                }
            }
        }

        internal static bool RefreshCompoundPartVisibility(GhostPlaybackState state)
        {
            if (state == null || state.ghost == null || state.compoundPartInfos == null
                || state.compoundPartInfos.Count == 0)
                return false;

            bool changed = false;
            var logicalPartIds = state.logicalPartIds;
            for (int i = 0; i < state.compoundPartInfos.Count; i++)
            {
                CompoundPartGhostInfo info = state.compoundPartInfos[i];
                if (info == null || info.partTransform == null || info.targetPersistentId == 0)
                    continue;

                GameObject partObject = info.partTransform.gameObject;
                if (partObject == null)
                    continue;

                if (logicalPartIds != null && logicalPartIds.Count > 0
                    && !logicalPartIds.Contains(info.partPersistentId))
                    continue;

                if (!partObject.activeSelf)
                    continue;

                Transform targetTransform = GhostVisualBuilder.FindGhostPartTransform(
                    state.ghost, info.targetPersistentId);
                bool hidePart = ShouldHideCompoundPart(
                    info.targetPersistentId,
                    logicalPartIds,
                    targetTransform != null,
                    targetTransform != null && targetTransform.gameObject.activeSelf);

                if (!hidePart)
                    continue;

                partObject.SetActive(false);
                changed = true;
            }

            return changed;
        }

        internal static bool ShouldHideCompoundPart(
            uint targetPersistentId,
            HashSet<uint> logicalPartIds,
            bool targetVisualExists,
            bool targetVisualActive)
        {
            if (targetPersistentId == 0)
                return false;

            if (logicalPartIds != null && logicalPartIds.Count > 0
                && !logicalPartIds.Contains(targetPersistentId))
                return true;

            return targetVisualExists && !targetVisualActive;
        }

        internal static bool ShouldRestoreCompoundPart(
            uint sourcePersistentId,
            uint targetPersistentId,
            HashSet<uint> logicalPartIds,
            bool targetVisualExists,
            bool targetVisualActive)
        {
            if (logicalPartIds != null && logicalPartIds.Count > 0
                && !logicalPartIds.Contains(sourcePersistentId))
                return false;

            return !ShouldHideCompoundPart(
                targetPersistentId,
                logicalPartIds,
                targetVisualExists,
                targetVisualActive);
        }

        internal static void RemovePartSubtreeFromLogicalPresence(
            HashSet<uint> logicalPartIds,
            uint rootPid,
            Dictionary<uint, List<uint>> tree)
        {
            if (logicalPartIds == null || logicalPartIds.Count == 0)
                return;

            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                logicalPartIds.Remove(pid);

                if (tree == null)
                    continue;

                List<uint> children;
                if (!tree.TryGetValue(pid, out children))
                    continue;

                for (int i = 0; i < children.Count; i++)
                    stack.Push(children[i]);
            }
        }

        internal static bool RestoreCompoundPartsForPlacedTargets(
            GhostPlaybackState state,
            HashSet<uint> placedTargetPartIds)
        {
            if (state == null || state.ghost == null || state.compoundPartInfos == null
                || state.compoundPartInfos.Count == 0 || placedTargetPartIds == null
                || placedTargetPartIds.Count == 0)
                return false;

            bool changed = false;
            var logicalPartIds = state.logicalPartIds;
            for (int i = 0; i < state.compoundPartInfos.Count; i++)
            {
                CompoundPartGhostInfo info = state.compoundPartInfos[i];
                if (info == null || info.partTransform == null
                    || !placedTargetPartIds.Contains(info.targetPersistentId))
                    continue;

                GameObject partObject = info.partTransform.gameObject;
                if (partObject == null || partObject.activeSelf)
                    continue;

                Transform targetTransform = GhostVisualBuilder.FindGhostPartTransform(
                    state.ghost, info.targetPersistentId);
                if (!ShouldRestoreCompoundPart(
                    info.partPersistentId,
                    info.targetPersistentId,
                    logicalPartIds,
                    targetTransform != null,
                    targetTransform != null && targetTransform.gameObject.activeSelf))
                    continue;

                partObject.SetActive(true);
                changed = true;
            }

            return changed;
        }

        #endregion

        #region Part Events

        internal static void ApplyPartEvents(
            int recIdx, IPlaybackTrajectory rec, double currentUT, GhostPlaybackState state,
            bool allowTransientEffects = true)
        {
            if (rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state.ghost == null)
            {
                ParsekLog.VerboseRateLimited("Flight", $"apply-part-events-null-ghost-{recIdx}",
                    $"ApplyPartEvents: ghost is null for recording #{recIdx}");
                return;
            }

            int evtIdx = state.partEventIndex;
            var tree = state.partTree;
            var ghost = state.ghost;
            var logicalPartIds = state.logicalPartIds;
            bool visibilityChanged = false;
            bool needsReentryMeshRebuild = false;
            HashSet<uint> placedTargetPartIds = null;

            while (evtIdx < rec.PartEvents.Count && rec.PartEvents[evtIdx].ut <= currentUT)
            {
                var evt = rec.PartEvents[evtIdx];
                switch (evt.eventType)
                {
                    case PartEventType.Decoupled:
                        ApplyDecoupledPartEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            tree,
                            evt,
                            allowTransientEffects,
                            ref visibilityChanged,
                            ref needsReentryMeshRebuild);
                        break;
                    case PartEventType.Destroyed:
                        ApplyDestroyedPartEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            evt,
                            allowTransientEffects,
                            ref visibilityChanged,
                            ref needsReentryMeshRebuild);
                        break;
                    case PartEventType.ParachuteCut:
                        ApplyParachuteCutEvent(state, evt.partPersistentId);
                        break;
                    case PartEventType.ShroudJettisoned:
                        ApplyJettisonPanelState(state, evt, jettisoned: true);
                        break;
                    case PartEventType.ParachuteDestroyed:
                        ApplyParachuteDestroyedEvent(
                            state,
                            ghost,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref visibilityChanged);
                        break;
                    case PartEventType.ParachuteSemiDeployed:
                        if (state.parachuteInfos != null)
                        {
                            ParachuteGhostInfo semiInfo;
                            if (state.parachuteInfos.TryGetValue(evt.partPersistentId, out semiInfo) &&
                                semiInfo.canopyTransform != null && semiInfo.semiDeployedSampled)
                            {
                                semiInfo.canopyTransform.localScale = semiInfo.semiDeployedCanopyScale;
                                semiInfo.canopyTransform.localPosition = semiInfo.semiDeployedCanopyPos;
                                semiInfo.canopyTransform.localRotation = semiInfo.semiDeployedCanopyRot;
                                if (semiInfo.capTransform != null)
                                    semiInfo.capTransform.gameObject.SetActive(false);
                            }
                        }
                        break;
                    case PartEventType.ParachuteDeployed:
                        ApplyParachuteDeployedEvent(state, ghost, evt.partPersistentId);
                        break;
                    case PartEventType.EngineIgnited:
                        // Use at least a minimum emission on ignition (#165) — older
                        // recordings may contain seed events with throttle=0 from before
                        // the recording-side fix. The 0.01 floor ensures plume visibility
                        // for backward compatibility. New recordings skip zero-throttle
                        // engine seeds entirely (PartStateSeeder.EmitEngineSeedEvents).
                        SetEngineEmission(state, evt, System.Math.Max(evt.value, 0.01f));
                        SetEngineAudio(state, evt, System.Math.Max(evt.value, 0.01f));
                        break;
                    case PartEventType.EngineShutdown:
                        SetEngineEmission(state, evt, 0f);
                        SetEngineAudio(state, evt, 0f);
                        break;
                    case PartEventType.EngineThrottle:
                        SetEngineEmission(state, evt, evt.value);
                        SetEngineAudio(state, evt, evt.value);
                        break;
                    case PartEventType.DeployableExtended:
                        ApplyDeployableState(state, evt, deployed: true);
                        break;
                    case PartEventType.DeployableRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        break;
                    case PartEventType.ThermalAnimationHot:
                        ApplyHeatState(state, evt, HeatLevel.Hot);
                        break;
                    case PartEventType.ThermalAnimationMedium:
                        ApplyHeatState(state, evt, HeatLevel.Medium);
                        break;
                    case PartEventType.ThermalAnimationCold:
                        ApplyHeatState(state, evt, HeatLevel.Cold);
                        break;
                    case PartEventType.LightOn:
                        ApplyLightPowerEvent(state, evt.partPersistentId, true);
                        break;
                    case PartEventType.LightOff:
                        ApplyLightPowerEvent(state, evt.partPersistentId, false);
                        break;
                    case PartEventType.LightBlinkEnabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: true, evt.value);
                        break;
                    case PartEventType.LightBlinkDisabled:
                        ApplyLightBlinkModeEvent(state, evt.partPersistentId, enabled: false, evt.value);
                        break;
                    case PartEventType.LightBlinkRate:
                        ApplyLightBlinkRateEvent(state, evt.partPersistentId, evt.value);
                        break;
                    case PartEventType.GearDeployed:
                        ApplyDeployableState(state, evt, deployed: true);
                        break;
                    case PartEventType.GearRetracted:
                        ApplyDeployableState(state, evt, deployed: false);
                        break;
                    case PartEventType.CargoBayOpened:
                        if (!ApplyDeployableState(state, evt, deployed: true))
                            ApplyJettisonPanelState(state, evt, jettisoned: true);
                        break;
                    case PartEventType.CargoBayClosed:
                        if (!ApplyDeployableState(state, evt, deployed: false))
                            ApplyJettisonPanelState(state, evt, jettisoned: false);
                        break;
                    case PartEventType.FairingJettisoned:
                        if (state.fairingInfos != null)
                        {
                            FairingGhostInfo fInfo;
                            if (state.fairingInfos.TryGetValue(evt.partPersistentId, out fInfo)
                                && fInfo.fairingMeshObject != null)
                            {
                                fInfo.fairingMeshObject.SetActive(false);
                            }
                        }
                        break;
                    case PartEventType.RCSActivated:
                        SetRcsEmission(state, evt, evt.value);
                        break;
                    case PartEventType.RCSStopped:
                        SetRcsEmission(state, evt, 0f);
                        break;
                    case PartEventType.RCSThrottle:
                        SetRcsEmission(state, evt, evt.value);
                        break;
                    case PartEventType.RoboticMotionStarted:
                    case PartEventType.RoboticPositionSample:
                    case PartEventType.RoboticMotionStopped:
                        ApplyRoboticEvent(state, evt, currentUT);
                        break;
                    case PartEventType.InventoryPartPlaced:
                        ApplyInventoryPartPlacedEvent(
                            state,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref placedTargetPartIds,
                            ref visibilityChanged);
                        break;
                    case PartEventType.InventoryPartRemoved:
                        ApplyInventoryPartRemovedEvent(
                            state,
                            logicalPartIds,
                            evt.partPersistentId,
                            ref visibilityChanged);
                        break;
                }
                evtIdx++;
            }

            int appliedCount = evtIdx - state.partEventIndex;
            state.partEventIndex = evtIdx;
            if (appliedCount > 0)
                ParsekLog.VerboseRateLimited("Flight", $"part-events-{recIdx}",
                    $"Applied {appliedCount} part events for ghost #{recIdx} (evtIdx now {evtIdx})");
            if (visibilityChanged)
            {
                if (RefreshCompoundPartVisibility(state))
                    needsReentryMeshRebuild = true;
                if (RestoreCompoundPartsForPlacedTargets(state, placedTargetPartIds))
                    needsReentryMeshRebuild = true;
                if (needsReentryMeshRebuild)
                    GhostVisualBuilder.RebuildReentryMeshes(ghost, state.reentryFxInfo);
                RecalculateCameraPivot(state);
            }
            UpdateBlinkingLights(state, currentUT);
            UpdateActiveRobotics(state, currentUT);
        }

        private static void ApplyDecoupledPartEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            Dictionary<uint, List<uint>> tree,
            PartEvent evt,
            bool allowTransientEffects,
            ref bool visibilityChanged,
            ref bool needsReentryMeshRebuild)
        {
            StopEngineFxForPart(state, evt.partPersistentId);
            StopRcsFxForPart(state, evt.partPersistentId);
            StopAudioForPart(state, evt.partPersistentId);
            ApplyHeatState(state, evt, HeatLevel.Cold);
            if (allowTransientEffects)
                SpawnPartPuffAtPart(ghost, evt.partPersistentId);
            if (tree != null)
            {
                HidePartSubtree(ghost, evt.partPersistentId, tree);
                RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, tree);
            }
            else
            {
                HideGhostPart(ghost, evt.partPersistentId);
                RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, null);
            }
            visibilityChanged = true;
            needsReentryMeshRebuild = true;
        }

        private static void ApplyDestroyedPartEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            PartEvent evt,
            bool allowTransientEffects,
            ref bool visibilityChanged,
            ref bool needsReentryMeshRebuild)
        {
            StopEngineFxForPart(state, evt.partPersistentId);
            StopRcsFxForPart(state, evt.partPersistentId);
            StopAudioForPart(state, evt.partPersistentId);
            if (allowTransientEffects)
                PlayOneShotAtGhost(state, evt.eventType);
            ApplyHeatState(state, evt, HeatLevel.Cold);
            if (allowTransientEffects)
                SpawnPartPuffAtPart(ghost, evt.partPersistentId);
            HideGhostPart(ghost, evt.partPersistentId);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, evt.partPersistentId, null);
            visibilityChanged = true;
            needsReentryMeshRebuild = true;
        }

        private static void ApplyParachuteCutEvent(
            GhostPlaybackState state,
            uint partPersistentId)
        {
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo cutInfo;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out cutInfo))
                {
                    if (cutInfo.canopyTransform != null)
                        cutInfo.canopyTransform.localScale = Vector3.zero;
                    if (cutInfo.capTransform != null)
                        cutInfo.capTransform.gameObject.SetActive(false);
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
        }

        private static void ApplyParachuteDestroyedEvent(
            GhostPlaybackState state,
            GameObject ghost,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref bool visibilityChanged)
        {
            // Clean up canopy visuals before hiding the part
            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo destroyedInfo;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out destroyedInfo))
                {
                    if (destroyedInfo.canopyTransform != null)
                        destroyedInfo.canopyTransform.localScale = Vector3.zero;
                }
            }
            DestroyFakeCanopy(state, partPersistentId);
            HideGhostPart(ghost, partPersistentId);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, partPersistentId, null);
            visibilityChanged = true;
        }

        private static void ApplyInventoryPartPlacedEvent(
            GhostPlaybackState state,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref HashSet<uint> placedTargetPartIds,
            ref bool visibilityChanged)
        {
            SetGhostPartActive(state, partPersistentId, true);
            if (logicalPartIds != null)
                logicalPartIds.Add(partPersistentId);
            if (placedTargetPartIds == null)
                placedTargetPartIds = new HashSet<uint>();
            placedTargetPartIds.Add(partPersistentId);
            visibilityChanged = true;
        }

        private static void ApplyInventoryPartRemovedEvent(
            GhostPlaybackState state,
            HashSet<uint> logicalPartIds,
            uint partPersistentId,
            ref bool visibilityChanged)
        {
            SetGhostPartActive(state, partPersistentId, false);
            RemovePartSubtreeFromLogicalPresence(logicalPartIds, partPersistentId, null);
            visibilityChanged = true;
        }

        /// <summary>
        /// Spawns a small smoke puff + spark FX at a ghost part's world position.
        /// Called before hiding the part on Decoupled/Destroyed events.
        /// </summary>
        internal static void SpawnPartPuffAtPart(GameObject ghost, uint persistentId)
        {
            if (ghost == null) return;
            if (ShouldSuppressVisualFx(TimeWarp.CurrentRate)) return;
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t == null)
            {
                return;
            }
            if (!t.gameObject.activeSelf)
            {
                return;
            }

            // Estimate part scale from its renderer bounds
            float partScale = 1f;
            var renderer = t.GetComponentInChildren<Renderer>();
            if (renderer != null)
                partScale = renderer.bounds.size.magnitude * 0.5f;

            var pos = t.position;
            GhostVisualBuilder.SpawnPartPuffFx(pos, partScale);
        }

        internal static void HideGhostPart(GameObject ghost, uint persistentId)
        {
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t != null) t.gameObject.SetActive(false);
        }

        internal static void SetGhostPartActive(GameObject ghost, uint persistentId, bool active)
        {
            if (ghost == null) return;
            var t = GhostVisualBuilder.FindGhostPartTransform(ghost, persistentId);
            if (t != null) t.gameObject.SetActive(active);
        }

        internal static void SetGhostPartActive(GhostPlaybackState state, uint persistentId, bool active)
        {
            if (state == null)
                return;

            SetGhostPartActive(state.ghost, persistentId, active);

            if (state.audioInfos == null)
                return;

            var restores = active ? new List<(int moduleIndex, float power)>() : null;
            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || info.partPersistentId != persistentId || info.audioSource == null)
                    continue;

                if (!active && info.audioSource.isPlaying)
                    StopLoopedGhostAudio(info, "part-inactive");

                info.audioSource.gameObject.SetActive(active);

                if (active && info.currentPower > 0f)
                    restores.Add((info.moduleIndex, info.currentPower));
            }

            if (restores == null)
                return;

            for (int i = 0; i < restores.Count; i++)
            {
                SetEngineAudio(state, new PartEvent
                {
                    partPersistentId = persistentId,
                    moduleIndex = restores[i].moduleIndex
                }, restores[i].power);
            }
        }

        internal static void InitializeInventoryPlacementVisibility(
            IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (rec == null || rec.PartEvents == null || rec.PartEvents.Count == 0) return;
            if (state == null || state.ghost == null) return;

            // If a part's first placement-related event is "placed", start hidden so it
            // visibly appears only when the event fires.
            var initialized = new HashSet<uint>();
            int hidden = 0;
            for (int i = 0; i < rec.PartEvents.Count; i++)
            {
                var evt = rec.PartEvents[i];
                if (initialized.Contains(evt.partPersistentId)) continue;

                if (evt.eventType == PartEventType.InventoryPartPlaced)
                {
                    SetGhostPartActive(state, evt.partPersistentId, false);
                    initialized.Add(evt.partPersistentId);
                    hidden++;
                }
                else if (evt.eventType == PartEventType.InventoryPartRemoved)
                {
                    SetGhostPartActive(state, evt.partPersistentId, true);
                    initialized.Add(evt.partPersistentId);
                }
            }
        }

        /// <summary>
        /// Initializes flag ghost visibility — all flags start hidden and appear when their event fires.
        /// </summary>
        internal static void InitializeFlagVisibility(IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (rec == null || rec.FlagEvents == null || rec.FlagEvents.Count == 0) return;
            if (state == null) return;
            state.flagEventIndex = 0;
        }

        /// <summary>
        /// Spawns flag vessels when their UT is reached. Flags are permanent world objects —
        /// they are never destroyed by Parsek. Duplicate check prevents re-spawning on loop wrap.
        /// The FlagEvent in the recording tracks which flag was planted (name, position, texture, plaque).
        /// </summary>
        internal static void ApplyFlagEvents(GhostPlaybackState state, IPlaybackTrajectory rec, double currentUT)
        {
            if (rec == null || rec.FlagEvents == null || rec.FlagEvents.Count == 0) return;

            if (state != null)
            {
                // Fast path: cursor-driven walk advances the per-state index monotonically.
                while (state.flagEventIndex < rec.FlagEvents.Count)
                {
                    var evt = rec.FlagEvents[state.flagEventIndex];
                    if (evt.ut > currentUT) break;

                    TrySpawnFlagVessel(evt);
                    state.flagEventIndex++;
                }
                return;
            }

            // Bug #414: state-less fallback. Reached when the caller cannot yet produce a
            // GhostPlaybackState (e.g. first-spawn visual build is throttled for a frame) but
            // we still want flag vessels — which are independent permanent world objects — to
            // be placed on schedule. `FlagExistsAtPosition` dedups, so a follow-up state-aware
            // walk starting from `flagEventIndex = 0` on the next frame is cheap and correct.
            SpawnFlagVesselsUpToUT(rec, currentUT);
        }

        /// <summary>
        /// Replays all flag events whose UT is in the past for callers that do not carry
        /// a per-recording flag cursor (for example deferred spawn flushes at warp end).
        /// Returns how many flag events were eligible at the requested UT, and how many
        /// actually spawned new flag vessels after dedup, were already present, or failed.
        /// </summary>
        internal static (int eligibleCount, int spawnedCount, int alreadyPresentCount, int failedCount)
            SpawnFlagVesselsUpToUT(
            IPlaybackTrajectory rec, double currentUT)
        {
            if (rec == null || rec.FlagEvents == null || rec.FlagEvents.Count == 0)
                return (0, 0, 0, 0);

            int eligibleCount = 0;
            int spawnedCount = 0;
            int alreadyPresentCount = 0;
            int failedCount = 0;
            for (int i = 0; i < rec.FlagEvents.Count; i++)
            {
                var evt = rec.FlagEvents[i];
                if (evt.ut > currentUT)
                    break;

                eligibleCount++;
                switch (TrySpawnFlagVessel(evt))
                {
                    case FlagReplayOutcome.Spawned:
                        spawnedCount++;
                        break;
                    case FlagReplayOutcome.AlreadyPresent:
                        alreadyPresentCount++;
                        break;
                    default:
                        failedCount++;
                        break;
                }
            }

            return (eligibleCount, spawnedCount, alreadyPresentCount, failedCount);
        }

        private enum FlagReplayOutcome
        {
            Spawned,
            AlreadyPresent,
            Failed
        }

        private static FlagReplayOutcome TrySpawnFlagVessel(FlagEvent evt)
        {
            if (FlagExistsAtPosition(evt))
                return FlagReplayOutcome.AlreadyPresent;

            if (spawnFlagOverrideForTesting != null)
                return spawnFlagOverrideForTesting(evt)
                    ? FlagReplayOutcome.Spawned
                    : FlagReplayOutcome.Failed;

            return GhostVisualBuilder.SpawnFlagVessel(evt) != null
                ? FlagReplayOutcome.Spawned
                : FlagReplayOutcome.Failed;
        }

        /// <summary>
        /// Checks if a flag vessel already exists within 1m of the event position (prevents duplicates on loop).
        /// Uses world-space 3D distance rather than lat/lon to handle high-latitude and small-body cases correctly.
        /// </summary>
        private static bool FlagExistsAtPosition(FlagEvent evt)
        {
            if (flagExistsOverrideForTesting != null)
                return flagExistsOverrideForTesting(evt);

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == evt.bodyName);
            if (body == null || FlightGlobals.Vessels == null) return false;

            Vector3d eventPos = body.GetWorldSurfacePosition(evt.latitude, evt.longitude, evt.altitude);

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel v = FlightGlobals.Vessels[i];
                if (v == null || v.vesselType != VesselType.Flag) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                if (v.mainBody != body) continue;

                Vector3d flagPos = body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
                double dx = flagPos.x - eventPos.x;
                double dy = flagPos.y - eventPos.y;
                double dz = flagPos.z - eventPos.z;
                if (dx * dx + dy * dy + dz * dz < 1.0) // within 1m
                    return true;
            }
            return false;
        }

        internal static void HidePartSubtree(GameObject ghost, uint rootPid, Dictionary<uint, List<uint>> tree)
        {
            int hidden = 0;
            int notFound = 0;
            var stack = new Stack<uint>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                uint pid = stack.Pop();
                var t = GhostVisualBuilder.FindGhostPartTransform(ghost, pid);
                if (t != null)
                {
                    t.gameObject.SetActive(false);
                    hidden++;
                }
                else
                    notFound++;
                List<uint> children;
                if (tree.TryGetValue(pid, out children))
                    for (int c = 0; c < children.Count; c++)
                        stack.Push(children[c]);
            }
        }

        /// <summary>
        /// Recalculate cameraPivot position after a visibility change (decouple/destroy).
        /// Sets localPosition to midpoint of remaining active parts' bounding extent.
        /// </summary>
        internal static void RecalculateCameraPivot(GhostPlaybackState state)
        {
            if (state.ghost == null || state.cameraPivot == null) return;
            var ghostTransform = state.ghost.transform;
            var partContainer = GhostVisualBuilder.GetGhostPartContainer(ghostTransform);
            if (partContainer == null)
            {
                state.cameraPivot.localPosition = Vector3.zero;
                return;
            }
            int count = 0;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int i = 0; i < partContainer.childCount; i++)
            {
                var child = partContainer.GetChild(i);
                if (!child.gameObject.activeSelf || !child.name.StartsWith("ghost_part_"))
                    continue;
                var pos = ghostTransform.InverseTransformPoint(child.position);
                if (count == 0) { min = max = pos; }
                else { min = Vector3.Min(min, pos); max = Vector3.Max(max, pos); }
                count++;
            }
            state.cameraPivot.localPosition = count > 0 ? (min + max) * 0.5f : Vector3.zero;
            ParsekLog.VerboseRateLimited("CameraFollow", $"pivot-{state.ghost.name}",
                $"Camera pivot recalculated: localPos=({state.cameraPivot.localPosition.x:F2},{state.cameraPivot.localPosition.y:F2},{state.cameraPivot.localPosition.z:F2})" +
                $" activeParts={count}", 1.0);
        }

        #endregion

        #region Canopy Management

        /// <summary>
        /// Applies a ParachuteDeployed event: sets the real canopy to deployed pose if available,
        /// otherwise creates a fake canopy sphere as fallback. Hides the cap in both cases.
        /// </summary>
        private static void ApplyParachuteDeployedEvent(GhostPlaybackState state, GameObject ghost, uint partPersistentId)
        {
            bool usedRealCanopy = false;

            if (state.parachuteInfos != null)
            {
                ParachuteGhostInfo info;
                if (state.parachuteInfos.TryGetValue(partPersistentId, out info) && info.canopyTransform != null)
                {
                    info.canopyTransform.localScale = info.deployedCanopyScale;
                    info.canopyTransform.localPosition = info.deployedCanopyPos;
                    info.canopyTransform.localRotation = info.deployedCanopyRot;
                    if (info.capTransform != null)
                        info.capTransform.gameObject.SetActive(false);
                    usedRealCanopy = true;
                }
            }

            if (!usedRealCanopy)
            {
                var canopy = GhostVisualBuilder.CreateFakeCanopy(ghost, partPersistentId);
                if (canopy != null)
                {
                    TrackFakeCanopy(state, partPersistentId, canopy);
                }
            }
        }

        internal static void TrackFakeCanopy(GhostPlaybackState state, uint partPid, GameObject canopy)
        {
            if (state.fakeCanopies == null)
                state.fakeCanopies = new Dictionary<uint, GameObject>();
            // Destroy previous canopy for this part if one exists (prevents leak)
            GameObject existing;
            if (state.fakeCanopies.TryGetValue(partPid, out existing) && existing != null)
                DestroyCanopyAndMaterial(existing);
            state.fakeCanopies[partPid] = canopy;
        }

        internal static void DestroyFakeCanopy(GhostPlaybackState state, uint partPid)
        {
            if (state.fakeCanopies == null) return;
            GameObject canopy;
            if (state.fakeCanopies.TryGetValue(partPid, out canopy) && canopy != null)
                DestroyCanopyAndMaterial(canopy);
            state.fakeCanopies.Remove(partPid);
        }

        internal static void DestroyAllFakeCanopies(GhostPlaybackState state)
        {
            if (state.fakeCanopies == null) return;
            foreach (var kv in state.fakeCanopies)
                if (kv.Value != null) DestroyCanopyAndMaterial(kv.Value);
            state.fakeCanopies = null;
        }

        internal static void DestroyCanopyAndMaterial(GameObject canopy)
        {
            var renderer = canopy.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
                UnityEngine.Object.Destroy(renderer.material);
            UnityEngine.Object.Destroy(canopy);
        }

        #endregion

        #region Engine FX

        internal static string BuildEngineFxEmissionDiagnostic(
            string partName,
            uint partPersistentId,
            int moduleIndex,
            float power,
            string particleName,
            string parentName,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 worldPosition,
            Vector3 worldForward,
            Vector3 worldUp,
            float emissionRate,
            float startSpeed,
            bool isPlaying)
        {
            string safePartName = string.IsNullOrEmpty(partName) ? "<unknown>" : partName;
            string safeParticleName = string.IsNullOrEmpty(particleName) ? "<unknown>" : particleName;
            string safeParentName = string.IsNullOrEmpty(parentName) ? "<none>" : parentName;
            string localRotationRaw =
                $"({localRotation.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.z.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{localRotation.w.ToString("F4", CultureInfo.InvariantCulture)})";

            return $"Engine FX emission diag: part='{safePartName}' pid={partPersistentId} midx={moduleIndex} " +
                $"power={power.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"ps='{safeParticleName}' parent='{safeParentName}' " +
                $"localPos={FormatVector3Invariant(localPosition)} localRot={localRotationRaw} " +
                $"worldPos={FormatVector3Invariant(worldPosition)} " +
                $"worldFwd={FormatVector3Invariant(worldForward)} " +
                $"worldUp={FormatVector3Invariant(worldUp)} " +
                $"rate={emissionRate.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"speed={startSpeed.ToString("F2", CultureInfo.InvariantCulture)} playing={isPlaying}";
        }

        internal static string FormatVector3Invariant(Vector3 value)
        {
            return $"({value.x.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.y.ToString("F4", CultureInfo.InvariantCulture)}," +
                $"{value.z.ToString("F4", CultureInfo.InvariantCulture)})";
        }

        internal static void SetEngineEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.engineInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            EngineGhostInfo info;
            if (!state.engineInfos.TryGetValue(key, out info)) return;

            info.currentPower = power;

            // Control KSPParticleEmitter.emit via reflection — this is the ONLY particle
            // creation source. Unity's emission module is permanently disabled (bug #105).
            SetKspEmittersEnabled(info.kspEmitters, power > 0f);

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                if (power > 0f)
                {
                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();
                }
                else
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

            }
        }

        /// <summary>
        /// Stop all engine FX particle systems for a given part (by PID).
        /// Used defensively on decouple/destroy to ensure no orphaned engine glow.
        /// </summary>
        internal static void StopEngineFxForPart(GhostPlaybackState state, uint partPersistentId)
        {
            ClearTrackedEnginePowerForPart(state, partPersistentId);
            if (state?.engineInfos == null) return;
            foreach (var info in state.engineInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    var ps = info.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Stop all RCS FX particle systems for a given part (by PID).
        /// Used defensively on decouple/destroy to ensure no orphaned RCS glow.
        /// </summary>
        internal static void StopRcsFxForPart(GhostPlaybackState state, uint partPersistentId)
        {
            ClearTrackedRcsPowerForPart(state, partPersistentId);
            if (state?.rcsInfos == null) return;
            foreach (var info in state.rcsInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    var ps = info.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        #region Ghost Audio Control

        internal static double ResolveAudioPriorityDistance(GhostPlaybackState state)
        {
            if (state == null)
                return 0.0;

            double distanceMeters = state.lastRenderDistance;
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0.0)
                distanceMeters = state.lastDistance;
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0.0)
                return 0.0;

            return distanceMeters;
        }

        internal static void UpdateLoopedAudioPriority(GhostPlaybackState state, AudioGhostInfo info)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;

            int priority = GhostAudioPresets.ComputeRuntimePriority(
                info.priorityClass,
                ResolveAudioPriorityDistance(state));
            if (info.audioSource.priority != priority)
                info.audioSource.priority = priority;
        }

        internal static List<AudioGhostInfo> SelectHighestPriorityActiveLoopedGhostAudioSources(
            IList<AudioGhostInfo> audioInfos, int maxSources)
        {
            var result = new List<AudioGhostInfo>();
            if (audioInfos == null || maxSources <= 0)
                return result;

            AudioGhostInfo first = null;
            AudioGhostInfo second = null;
            AudioGhostInfo third = null;
            AudioGhostInfo fourth = null;
            int firstOrder = int.MaxValue;
            int secondOrder = int.MaxValue;
            int thirdOrder = int.MaxValue;
            int fourthOrder = int.MaxValue;
            for (int i = 0; i < audioInfos.Count; i++)
            {
                AudioGhostInfo info = audioInfos[i];
                if (info == null || info.currentPower <= 0f)
                    continue;

                InsertLoopedAudioSelectionCandidate(
                    info,
                    i,
                    maxSources,
                    ref first,
                    ref firstOrder,
                    ref second,
                    ref secondOrder,
                    ref third,
                    ref thirdOrder,
                    ref fourth,
                    ref fourthOrder);
            }

            AppendIfNotNull(result, first);
            if (maxSources > 1) AppendIfNotNull(result, second);
            if (maxSources > 2) AppendIfNotNull(result, third);
            if (maxSources > 3) AppendIfNotNull(result, fourth);

            return result;
        }

        internal static void EnforceLoopedAudioPlaybackCap(GhostPlaybackState state)
        {
            if (state?.audioInfos == null)
                return;

            if (state.audioMuted || state.atmosphereFactor < 0.001f)
            {
                string stopReason = state.audioMuted ? "muted" : "vacuum";
                foreach (var info in state.audioInfos.Values)
                {
                    if (info != null && !ReferenceEquals(info.audioSource, null) && info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, stopReason);
                }
                return;
            }

            AudioGhostInfo first = null;
            AudioGhostInfo second = null;
            AudioGhostInfo third = null;
            AudioGhostInfo fourth = null;
            int firstOrder = int.MaxValue;
            int secondOrder = int.MaxValue;
            int thirdOrder = int.MaxValue;
            int fourthOrder = int.MaxValue;
            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || ReferenceEquals(info.audioSource, null))
                    continue;

                UpdateLoopedAudioPriority(state, info);

                if (info.currentPower > 0f)
                {
                    InsertLoopedAudioSelectionCandidate(
                        info,
                        info.selectionOrder,
                        GhostAudioPresets.MaxAudioSourcesPerGhost,
                        ref first,
                        ref firstOrder,
                        ref second,
                        ref secondOrder,
                        ref third,
                        ref thirdOrder,
                        ref fourth,
                        ref fourthOrder);
                }
                else if (info.audioSource.isPlaying)
                {
                    StopLoopedGhostAudio(info, "power=0");
                }
            }

            foreach (var info in state.audioInfos.Values)
            {
                if (info == null || ReferenceEquals(info.audioSource, null) || info.currentPower <= 0f)
                    continue;

                float volume = ComputeGhostAudioVolume(
                    info.volumeCurve.Evaluate(info.currentPower),
                    state.atmosphereFactor);
                if (!IsLoopedAudioSelectedForPlayback(info, first, second, third, fourth))
                {
                    if (info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "capped");
                    continue;
                }
                if (volume <= 0f)
                {
                    if (info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "volume=0");
                    continue;
                }

                if (info.audioSource.volume != volume)
                    info.audioSource.volume = volume;

                float pitch = info.pitchCurve.Evaluate(info.currentPower);
                if (info.audioSource.pitch != pitch)
                    info.audioSource.pitch = pitch;

                if (!ReferenceEquals(info.audioSource.clip, info.clip))
                    info.audioSource.clip = info.clip;

                if (!info.audioSource.isPlaying && CanStartLoopedGhostAudio(info.audioSource))
                    StartLoopedGhostAudio(info, volume, pitch);
            }
        }

        /// <summary>
        /// Set engine audio volume/pitch from recorded throttle power.
        /// Called alongside SetEngineEmission for EngineIgnited/Throttle/Shutdown events.
        /// </summary>
        internal static void SetEngineAudio(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.audioInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            AudioGhostInfo info;
            if (!state.audioInfos.TryGetValue(key, out info)) return;

            info.currentPower = power;
            if (state.audioMuted)
            {
                // Keep tracked power in sync during warp so unmute resumes the correct state.
                if (!ReferenceEquals(info.audioSource, null))
                    StopLoopedGhostAudio(info, "muted");
                return;
            }
            if (ReferenceEquals(info.audioSource, null)) return;

            EnforceLoopedAudioPlaybackCap(state);
        }

        internal static bool CanStartLoopedGhostAudio(bool sourceExists, bool sourceIsActiveAndEnabled)
        {
            return sourceExists && sourceIsActiveAndEnabled;
        }

        internal static bool CanStartLoopedGhostAudio(AudioSource audioSource)
        {
            return CanStartLoopedGhostAudio(
                sourceExists: !ReferenceEquals(audioSource, null),
                sourceIsActiveAndEnabled: !ReferenceEquals(audioSource, null) && audioSource.isActiveAndEnabled);
        }

        /// <summary>
        /// Stop all audio sources for a given part (by PID).
        /// Used defensively on decouple/destroy.
        /// </summary>
        internal static void StopAudioForPart(GhostPlaybackState state, uint partPersistentId)
        {
            ClearTrackedAudioPowerForPart(state, partPersistentId);
            if (state?.audioInfos == null) return;
            foreach (var info in state.audioInfos.Values)
            {
                if (info.partPersistentId != partPersistentId) continue;
                if (info.audioSource != null && info.audioSource.isPlaying)
                    StopLoopedGhostAudio(info, "part-removed");
            }
        }

        /// <summary>
        /// Play a one-shot sound effect at the ghost root position.
        /// Used for decouple and explosion events.
        /// </summary>
        internal static void PlayOneShotAtGhost(GhostPlaybackState state, PartEventType eventType)
        {
            if (state.oneShotAudio?.audioSource == null) return;
            // One-shot events (explosions) bypass audioMuted — they're dramatic moments
            // that should always be audible, even for overlap ghosts about to expire.
            if (state.atmosphereFactor < 0.001f) return; // no sound in vacuum

            string clipPath = GhostAudioPresets.ResolveOneShotClip(eventType);
            if (clipPath == null) return;

            var clip = GameDatabase.Instance.GetAudioClip(clipPath);
            if (clip == null) return;

            float vol = ComputeGhostAudioVolume(GhostAudioPresets.OneShotVolumeScale, state.atmosphereFactor);
            if (vol <= 0f) return;

            state.oneShotAudio.audioSource.priority = GhostAudioPresets.ComputeRuntimePriority(
                GhostAudioPresets.ClassifyOneShotPriority(eventType),
                ResolveAudioPriorityDistance(state));
            state.oneShotAudio.audioSource.PlayOneShot(clip, vol);
            ParsekLog.Verbose("GhostAudio",
                $"One-shot played: {eventType} clip='{clipPath}' vol={vol:F2}");
        }

        /// <summary>
        /// Mute all ghost audio sources (during high warp or ghost hidden).
        /// </summary>
        internal static void MuteAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (state.audioMuted) return;
            state.audioMuted = true;

            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null && info.audioSource.isPlaying)
                        StopLoopedGhostAudio(info, "muted");
                }
            }
            if (state.oneShotAudio?.audioSource != null)
                state.oneShotAudio.audioSource.Stop();
        }

        /// <summary>
        /// Unmute ghost audio. Active engines will resume on next throttle event.
        /// </summary>
        internal static void UnmuteAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (!state.audioMuted) return;
            state.audioMuted = false;
            // Audio restores naturally via next ApplyPartEvents cycle.
        }

        /// <summary>
        /// Pause all ghost audio sources for this state, preserving playback position.
        /// Used by the game pause handler so ESC menu mutes ghost audio.
        /// Unlike MuteAllAudio which calls Stop() (resetting position), this calls
        /// Pause() so UnPauseAllAudio can resume exactly where it left off.
        /// </summary>
        internal static void PauseAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null && info.audioSource.isPlaying)
                        info.audioSource.Pause();
                }
            }
            if (state.oneShotAudio?.audioSource != null && state.oneShotAudio.audioSource.isPlaying)
                state.oneShotAudio.audioSource.Pause();
        }

        /// <summary>
        /// Resume all ghost audio sources paused by PauseAllAudio.
        /// </summary>
        internal static void UnpauseAllAudio(GhostPlaybackState state)
        {
            if (state == null) return;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null)
                        info.audioSource.UnPause();
                }
            }
            if (state.oneShotAudio?.audioSource != null)
                state.oneShotAudio.audioSource.UnPause();
        }

        /// <summary>
        /// Compute the ghost audio volume for a given power level and atmosphere state.
        /// Centralizes the volume formula so SetEngineAudio, PlayOneShotAtGhost, and
        /// UpdateAudioAtmosphere all use the same calculation.
        /// </summary>
        internal static float ComputeGhostAudioVolume(float curveValue, float atmosphereFactor)
        {
            float settingsVolume = ParsekSettings.Current?.ghostAudioVolume ?? 1.0f;
            return curveValue * settingsVolume * GameSettings.SHIP_VOLUME * atmosphereFactor;
        }

        /// <summary>
        /// Compute atmosphere attenuation factor for ghost audio.
        /// Returns 0 in vacuum (no atmosphere or above atmosphere depth), 1 at sea level,
        /// with smooth quadratic falloff at high altitude.
        /// Uses cached CelestialBody on state to avoid per-frame linear search.
        /// </summary>
        internal static float ComputeAtmosphereFactor(GhostPlaybackState state)
        {
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName)) return 0f;

            // Cache the CelestialBody lookup — body only changes on SOI transitions.
            CelestialBody body = state.cachedAudioBody;
            if (body == null || state.cachedAudioBodyName != bodyName)
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                state.cachedAudioBody = body;
                state.cachedAudioBodyName = bodyName;
            }

            if (body == null || !body.atmosphere) return 0f;
            if (altitude >= body.atmosphereDepth) return 0f;
            if (altitude <= 0) return 1f;

            // Quadratic falloff: factor = (1 - alt/depth)^2
            // Sea level: 1.0. Half depth: 0.25. Edge: 0.0.
            float ratio = (float)(altitude / body.atmosphereDepth);
            float factor = (1f - ratio) * (1f - ratio);
            return factor;
        }

        /// <summary>
        /// Per-frame update of atmosphere factor and volume adjustment for all playing audio sources.
        /// Ensures smooth fade as ghost ascends/descends through atmosphere.
        /// </summary>
        internal static void UpdateAudioAtmosphere(GhostPlaybackState state)
        {
            if (state == null || state.audioInfos == null || state.audioMuted) return;

            float newFactor = ComputeAtmosphereFactor(state);

            // Log transitions (vacuum / atmosphere)
            bool wasInVacuum = state.atmosphereFactor < 0.001f;
            bool nowInVacuum = newFactor < 0.001f;
            if (wasInVacuum != nowInVacuum)
            {
                ParsekLog.VerboseRateLimited("GhostAudio", $"atm-transition-{state.vesselName}",
                    nowInVacuum
                        ? $"Ghost '{state.vesselName}' entered vacuum — audio silent"
                        : $"Ghost '{state.vesselName}' entered atmosphere — audio enabled (factor={newFactor:F3})",
                    2.0);
            }

            state.atmosphereFactor = newFactor;

            EnforceLoopedAudioPlaybackCap(state);
        }

        private static void AppendIfNotNull(List<AudioGhostInfo> result, AudioGhostInfo info)
        {
            if (info != null)
                result.Add(info);
        }

        private static bool ShouldLoopedAudioCandidatePrecede(
            AudioGhostInfo candidate, int candidateOrder, AudioGhostInfo current, int currentOrder)
        {
            if (candidate == null)
                return false;
            if (current == null)
                return true;

            int priorityCompare = GhostAudioPresets.GetBasePriority(candidate.priorityClass)
                .CompareTo(GhostAudioPresets.GetBasePriority(current.priorityClass));
            if (priorityCompare != 0)
                return priorityCompare < 0;

            int powerCompare = candidate.currentPower.CompareTo(current.currentPower);
            if (powerCompare != 0)
                return powerCompare > 0;

            return candidateOrder < currentOrder;
        }

        private static void InsertLoopedAudioSelectionCandidate(
            AudioGhostInfo candidate,
            int candidateOrder,
            int maxSources,
            ref AudioGhostInfo first,
            ref int firstOrder,
            ref AudioGhostInfo second,
            ref int secondOrder,
            ref AudioGhostInfo third,
            ref int thirdOrder,
            ref AudioGhostInfo fourth,
            ref int fourthOrder)
        {
            if (candidate == null || maxSources <= 0)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, first, firstOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                if (maxSources > 2)
                {
                    third = second;
                    thirdOrder = secondOrder;
                }
                if (maxSources > 1)
                {
                    second = first;
                    secondOrder = firstOrder;
                }
                first = candidate;
                firstOrder = candidateOrder;
                return;
            }

            if (maxSources <= 1)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, second, secondOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                if (maxSources > 2)
                {
                    third = second;
                    thirdOrder = secondOrder;
                }
                second = candidate;
                secondOrder = candidateOrder;
                return;
            }

            if (maxSources <= 2)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, third, thirdOrder))
            {
                if (maxSources > 3)
                {
                    fourth = third;
                    fourthOrder = thirdOrder;
                }
                third = candidate;
                thirdOrder = candidateOrder;
                return;
            }

            if (maxSources <= 3)
                return;

            if (ShouldLoopedAudioCandidatePrecede(candidate, candidateOrder, fourth, fourthOrder))
            {
                fourth = candidate;
                fourthOrder = candidateOrder;
            }
        }

        private static bool IsLoopedAudioSelectedForPlayback(
            AudioGhostInfo info,
            AudioGhostInfo first,
            AudioGhostInfo second,
            AudioGhostInfo third,
            AudioGhostInfo fourth)
        {
            return ReferenceEquals(info, first)
                || ReferenceEquals(info, second)
                || ReferenceEquals(info, third)
                || ReferenceEquals(info, fourth);
        }

        private static void StartLoopedGhostAudio(AudioGhostInfo info, float volume, float pitch)
        {
            if (info == null || ReferenceEquals(info.audioSource, null))
                return;

            info.audioSource.Play();
            ParsekLog.VerboseRateLimited("GhostAudio",
                $"audio-start-{info.partPersistentId}-{info.moduleIndex}",
                $"Engine audio started: pid={info.partPersistentId} midx={info.moduleIndex} " +
                $"power={info.currentPower:F2} vol={volume:F2} pitch={pitch:F2}",
                5.0);
        }

        private static void StopLoopedGhostAudio(AudioGhostInfo info, string reason)
        {
            if (info == null || ReferenceEquals(info.audioSource, null) || !info.audioSource.isPlaying)
                return;

            info.audioSource.Stop();
            ParsekLog.VerboseRateLimited("GhostAudio",
                $"audio-stop-{info.partPersistentId}-{info.moduleIndex}",
                $"Engine audio stopped: pid={info.partPersistentId} midx={info.moduleIndex} reason={reason}",
                5.0);
        }

        #endregion

        /// <summary>
        /// Stop and clear all engine FX particle systems across every engine info in the state.
        /// Used during ghost teardown to ensure no orphaned particle effects remain.
        /// </summary>
        internal static void StopAllEngineFx(GhostPlaybackState state)
        {
            if (state?.engineInfos == null) return;
            foreach (var kv in state.engineInfos)
            {
                SetKspEmittersEnabled(kv.Value.kspEmitters, false);
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Stop and clear all RCS FX particle systems across every RCS info in the state.
        /// Used during ghost teardown to ensure no orphaned particle effects remain.
        /// </summary>
        internal static void StopAllRcsFx(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            foreach (var kv in state.rcsInfos)
            {
                SetKspEmittersEnabled(kv.Value.kspEmitters, false);
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps == null) continue;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }
            }
        }

        /// <summary>
        /// Detaches active particle systems from the ghost hierarchy so they can linger
        /// and fade out naturally after the ghost is destroyed (#107). Stops emission,
        /// unparents, and schedules delayed destruction.
        /// </summary>
        internal static void DetachAndLingerParticleSystems(
            List<ParticleSystem> particleSystems, List<KspEmitterRef> kspEmitters, float lingerSeconds = 8f)
        {
            if (kspEmitters != null)
                SetKspEmittersEnabled(kspEmitters, false);
            if (particleSystems == null) return;

            for (int i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;

                // Only detach if particles are alive (no point lingering an empty system)
                if (ps.particleCount == 0)
                {
                    UnityEngine.Object.Destroy(ps.gameObject);
                    continue;
                }

                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                ps.transform.SetParent(null, true);
                UnityEngine.Object.Destroy(ps.gameObject, lingerSeconds);
            }
            particleSystems.Clear();
        }

        internal static void StopAndClearParticleSystems(
            List<ParticleSystem> particleSystems, List<KspEmitterRef> kspEmitters)
        {
            if (kspEmitters != null)
                SetKspEmittersEnabled(kspEmitters, false);
            if (particleSystems == null) return;

            for (int i = 0; i < particleSystems.Count; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(true);
                SetParticleRenderersEnabled(ps, false);
            }
            particleSystems.Clear();
        }

        internal static void SetParticleRenderersEnabled(ParticleSystem ps, bool enabled)
        {
            if (ps == null)
                return;

            ParticleSystemRenderer[] renderers = ps.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = enabled;
            }
        }

        /// <summary>
        /// Enable or disable KSPParticleEmitter.emit on all captured emitters via reflection.
        /// KSPParticleEmitter is the ONLY particle creation source on ghost FX objects —
        /// Unity's emission module is permanently disabled to prevent bubble artifacts (bug #105).
        /// </summary>
        private static void SetKspEmittersEnabled(List<KspEmitterRef> kspEmitters, bool enabled)
        {
            if (kspEmitters == null) return;
            for (int i = 0; i < kspEmitters.Count; i++)
            {
                var r = kspEmitters[i];
                if (r.emitter == null || r.emitField == null) continue;
                r.emitField.SetValue(r.emitter, enabled);
            }
        }

        #endregion

        #region RCS FX

        internal static void SetRcsEmission(GhostPlaybackState state, PartEvent evt, float power)
        {
            if (state.rcsInfos == null) return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            RcsGhostInfo info;
            if (!state.rcsInfos.TryGetValue(key, out info)) return;

            info.currentPower = power;

            // Control KSPParticleEmitter.emit via reflection — this is the ONLY particle
            // creation source. Unity's emission module is permanently disabled (bug #105).
            SetKspEmittersEnabled(info.kspEmitters, power > 0f);

            int configuredSystems = 0;
            int enabledRenderers = 0;
            int playingSystems = 0;
            float sampleSpeed = 0f;
            float sampleSize = 0f;
            float sampleLifetime = 0f;

            for (int i = 0; i < info.particleSystems.Count; i++)
            {
                var ps = info.particleSystems[i];
                if (ps == null) continue;

                configuredSystems++;
                if (power > 0f)
                {
                    SetParticleRenderersEnabled(ps, true);
                    if (!ps.isPlaying) ps.Play();

                    if (sampleSpeed <= 0f)
                    {
                        var main = ps.main;
                        sampleSpeed = main.startSpeedMultiplier;
                        sampleSize = main.startSizeMultiplier;
                        sampleLifetime = main.startLifetimeMultiplier;
                    }
                }
                else
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                    SetParticleRenderersEnabled(ps, false);
                }

                if (ps.isPlaying) playingSystems++;
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.enabled) enabledRenderers++;
            }

        }

        internal static float ComputeScaledRcsEmissionRate(
            FloatCurve emissionCurve, float power, float emissionScale)
        {
            if (power <= 0f) return 0f;

            float emRate = emissionCurve != null ? emissionCurve.Evaluate(power) : power * 100f;
            emRate *= emissionScale > 0f ? emissionScale : 1f;
            if (emissionScale > 1f)
                emRate = Math.Max(emRate, 60f);

            return emRate;
        }

        internal static float ComputeScaledRcsSpeed(
            FloatCurve speedCurve, float power, float speedScale)
        {
            if (power <= 0f) return 0f;

            float spd = speedCurve != null ? speedCurve.Evaluate(power) : power * 10f;
            spd *= speedScale > 0f ? speedScale : 1f;
            if (speedScale > 1f)
                spd = Math.Max(spd, 4f);

            return spd;
        }

        internal static void StopAllRcsEmissions(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            if (state.rcsSuppressed) return;
            state.rcsSuppressed = true;
            int suppressedCount = 0;
            foreach (var info in state.rcsInfos.Values)
            {
                SetKspEmittersEnabled(info.kspEmitters, false);
                for (int j = 0; j < info.particleSystems.Count; j++)
                {
                    var ps = info.particleSystems[j];
                    if (ps != null)
                    {
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(true);
                    }
                }
                suppressedCount++;
            }
        }

        internal static void RestoreAllRcsEmissions(GhostPlaybackState state)
        {
            if (state?.rcsInfos == null) return;
            if (!state.rcsSuppressed) return;
            state.rcsSuppressed = false;
            int restoredCount = 0;
            foreach (var info in state.rcsInfos.Values)
            {
                // Only restore emission for RCS that had active KSP emitters.
                // Check if any KSPParticleEmitter was playing before suppression
                // by looking at whether the particle system was playing (ps.isPlaying
                // stays true after Stop with StopEmittingAndClear until particles expire).
                // Since we call Clear(), isPlaying is false after suppress. Instead, check
                // if any renderers are enabled — SetRcsEmission enables renderers when active.
                bool wasActive = false;
                for (int j = 0; j < info.particleSystems.Count; j++)
                {
                    var ps = info.particleSystems[j];
                    if (ps == null) continue;
                    var renderer = ps.GetComponent<ParticleSystemRenderer>();
                    if (renderer != null && renderer.enabled)
                    {
                        wasActive = true;
                        break;
                    }
                }
                if (wasActive)
                {
                    SetKspEmittersEnabled(info.kspEmitters, true);
                    for (int j = 0; j < info.particleSystems.Count; j++)
                    {
                        var ps = info.particleSystems[j];
                        if (ps != null && !ps.isPlaying)
                            ps.Play();
                    }
                    restoredCount++;
                }
            }
            ParsekLog.Info("Flight", $"Restored RCS emissions for {restoredCount} modules");
        }

        internal static List<(ulong key, float power)> CollectDeferredEnginePowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.engineInfos == null)
                return restores;

            foreach (var kvp in state.engineInfos)
            {
                EngineGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedEnginePowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.engineInfos == null)
                return;

            foreach (var info in state.engineInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static List<(ulong key, float power)> CollectDeferredRcsPowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.rcsInfos == null)
                return restores;

            foreach (var kvp in state.rcsInfos)
            {
                RcsGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedRcsPowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.rcsInfos == null)
                return;

            foreach (var info in state.rcsInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static List<(ulong key, float power)> CollectDeferredAudioPowerRestores(
            GhostPlaybackState state)
        {
            var restores = new List<(ulong key, float power)>();
            if (state?.audioInfos == null || state.audioMuted)
                return restores;

            foreach (var kvp in state.audioInfos)
            {
                AudioGhostInfo info = kvp.Value;
                if (info == null || info.currentPower <= 0f)
                    continue;

                restores.Add((kvp.Key, info.currentPower));
            }

            return restores;
        }

        internal static void ClearTrackedAudioPowerForPart(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state?.audioInfos == null)
                return;

            foreach (var info in state.audioInfos.Values)
            {
                if (info != null && info.partPersistentId == partPersistentId)
                    info.currentPower = 0f;
            }
        }

        internal static void RestoreDeferredRuntimeFxState(GhostPlaybackState state)
        {
            if (state == null)
                return;

            foreach (var restore in CollectDeferredEnginePowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetEngineEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
            }

            foreach (var restore in CollectDeferredRcsPowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetRcsEmission(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
            }

            foreach (var restore in CollectDeferredAudioPowerRestores(state))
            {
                uint partPersistentId;
                int moduleIndex;
                FlightRecorder.DecodeEngineKey(restore.key, out partPersistentId, out moduleIndex);
                SetEngineAudio(state, new PartEvent
                {
                    partPersistentId = partPersistentId,
                    moduleIndex = moduleIndex
                }, restore.power);
            }
        }

        #endregion

        #region Robotic

        internal static float ComputeRotorDeltaDegrees(float rpm, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0)
                return 0f;
            if (float.IsNaN(rpm) || float.IsInfinity(rpm) || Mathf.Abs(rpm) <= 0.0001f)
                return 0f;

            // RPM * 360deg / 60s
            return rpm * 6f * (float)deltaSeconds;
        }

        private static void ApplyRoboticPose(RoboticGhostInfo info, float value)
        {
            if (info == null || info.servoTransform == null)
                return;

            Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                ? info.axisLocal.normalized
                : Vector3.up;

            if (info.visualMode == RoboticVisualMode.Linear)
            {
                info.servoTransform.localPosition = info.stowedPos + (axis * value);
            }
            else if (info.visualMode == RoboticVisualMode.Rotational)
            {
                info.servoTransform.localRotation =
                    info.stowedRot * Quaternion.AngleAxis(value, axis);
            }
        }

        private static void ApplyRoboticEvent(
            GhostPlaybackState state, PartEvent evt, double currentUT)
        {
            if (state == null || state.roboticInfos == null)
                return;

            ulong key = FlightRecorder.EncodeEngineKey(evt.partPersistentId, evt.moduleIndex);
            if (!state.roboticInfos.TryGetValue(key, out RoboticGhostInfo info) || info == null)
                return;

            info.currentValue = evt.value;

            if (info.visualMode == RoboticVisualMode.RotorRpm)
            {
                info.active = evt.eventType != PartEventType.RoboticMotionStopped &&
                    Mathf.Abs(evt.value) > 0.0001f;
                info.lastUpdateUT = currentUT;
            }
            else
            {
                ApplyRoboticPose(info, evt.value);
                info.active = evt.eventType != PartEventType.RoboticMotionStopped;
                info.lastUpdateUT = currentUT;
            }
        }

        internal static void UpdateActiveRobotics(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.roboticInfos == null || state.roboticInfos.Count == 0)
                return;

            foreach (var kv in state.roboticInfos)
            {
                RoboticGhostInfo info = kv.Value;
                if (info == null || info.servoTransform == null)
                    continue;

                if (double.IsNaN(info.lastUpdateUT) || double.IsInfinity(info.lastUpdateUT))
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                double deltaSeconds = currentUT - info.lastUpdateUT;
                if (deltaSeconds <= 0)
                {
                    info.lastUpdateUT = currentUT;
                    continue;
                }

                // Timeline jumps/loop boundaries rebuild ghosts, but guard large UT gaps anyway.
                deltaSeconds = Math.Min(deltaSeconds, 1.0);

                if (info.visualMode == RoboticVisualMode.RotorRpm && info.active)
                {
                    float deltaDegrees = ComputeRotorDeltaDegrees(info.currentValue, deltaSeconds);
                    if (Mathf.Abs(deltaDegrees) > 0.0001f)
                    {
                        Vector3 axis = info.axisLocal.sqrMagnitude > 0.0001f
                            ? info.axisLocal.normalized
                            : Vector3.up;
                        info.servoTransform.localRotation =
                            info.servoTransform.localRotation * Quaternion.AngleAxis(deltaDegrees, axis);
                    }
                }

                info.lastUpdateUT = currentUT;
            }
        }

        #endregion

        #region Heat / Reentry

        internal static bool ApplyHeatState(GhostPlaybackState state, PartEvent evt, HeatLevel level)
        {
            if (state == null || state.heatInfos == null) return false;

            if (!state.heatInfos.TryGetValue(evt.partPersistentId, out HeatGhostInfo info) || info == null)
                return false;

            bool applied = false;

            if (info.transforms != null)
            {
                for (int i = 0; i < info.transforms.Count; i++)
                {
                    var ts = info.transforms[i];
                    if (ts.t == null) continue;

                    switch (level)
                    {
                        case HeatLevel.Hot:
                            ts.t.localPosition = ts.hotPos;
                            ts.t.localRotation = ts.hotRot;
                            ts.t.localScale = ts.hotScale;
                            break;
                        case HeatLevel.Medium:
                            ts.t.localPosition = ts.mediumPos;
                            ts.t.localRotation = ts.mediumRot;
                            ts.t.localScale = ts.mediumScale;
                            break;
                        default:
                            ts.t.localPosition = ts.coldPos;
                            ts.t.localRotation = ts.coldRot;
                            ts.t.localScale = ts.coldScale;
                            break;
                    }
                    applied = true;
                }
            }

            if (info.materialStates != null)
            {
                for (int i = 0; i < info.materialStates.Count; i++)
                {
                    HeatMaterialState materialState = info.materialStates[i];
                    if (materialState.material == null) continue;

                    Color color, emission;
                    switch (level)
                    {
                        case HeatLevel.Hot:
                            color = materialState.hotColor;
                            emission = materialState.hotEmission;
                            break;
                        case HeatLevel.Medium:
                            color = materialState.mediumColor;
                            emission = materialState.mediumEmission;
                            break;
                        default:
                            color = materialState.coldColor;
                            emission = materialState.coldEmission;
                            break;
                    }

                    if (!string.IsNullOrEmpty(materialState.colorProperty))
                        materialState.material.SetColor(materialState.colorProperty, color);

                    if (!string.IsNullOrEmpty(materialState.emissiveProperty))
                        materialState.material.SetColor(materialState.emissiveProperty, emission);

                    applied = true;
                }
            }

            if (applied)
                ParsekLog.VerboseRateLimited("Flight", $"heat-{evt.partPersistentId}",
                    $"Part pid={evt.partPersistentId}: applied heat level {level}", 5.0);

            return applied;
        }

        internal static void ResetReentryFx(GhostPlaybackState state, int recIdx)
        {
            var info = state.reentryFxInfo;
            if (info == null) return;

            info.lastIntensity = 0f;

            if (info.fireParticles != null)
            {
                info.fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                info.fireParticles.Clear(true);
            }

            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;
                    if (!string.IsNullOrEmpty(ms.emissiveProperty))
                        ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                    if (!string.IsNullOrEmpty(ms.colorProperty))
                        ms.material.SetColor(ms.colorProperty, ms.coldColor);
                }
            }

        }

        #endregion

        #region Deployables / Jettison

        internal static bool ApplyDeployableState(GhostPlaybackState state, PartEvent evt, bool deployed)
        {
            if (state.deployableInfos == null) return false;

            DeployableGhostInfo info;
            if (!state.deployableInfos.TryGetValue(evt.partPersistentId, out info)) return false;

            bool applied = false;

            for (int i = 0; i < info.transforms.Count; i++)
            {
                var ts = info.transforms[i];
                if (ts.t == null) continue;
                applied = true;
                if (deployed)
                {
                    ts.t.localPosition = ts.deployedPos;
                    ts.t.localRotation = ts.deployedRot;
                    ts.t.localScale = ts.deployedScale;
                }
                else
                {
                    ts.t.localPosition = ts.stowedPos;
                    ts.t.localRotation = ts.stowedRot;
                    ts.t.localScale = ts.stowedScale;
                }
            }

            return applied;
        }

        internal static bool ApplyJettisonPanelState(GhostPlaybackState state, PartEvent evt, bool jettisoned)
        {
            if (state.jettisonInfos == null) return false;

            JettisonGhostInfo jetInfo;
            if (!state.jettisonInfos.TryGetValue(evt.partPersistentId, out jetInfo) ||
                jetInfo.jettisonTransforms == null ||
                jetInfo.jettisonTransforms.Count == 0)
                return false;

            bool applied = false;
            for (int i = 0; i < jetInfo.jettisonTransforms.Count; i++)
            {
                Transform jettisonTransform = jetInfo.jettisonTransforms[i];
                if (jettisonTransform == null) continue;
                jettisonTransform.gameObject.SetActive(!jettisoned);
                applied = true;
            }

            return applied;
        }

        #endregion

        #region Lights

        internal static LightPlaybackState GetOrCreateLightPlaybackState(
            GhostPlaybackState state, uint partPersistentId)
        {
            if (state.lightPlaybackStates == null)
                state.lightPlaybackStates = new Dictionary<uint, LightPlaybackState>();

            LightPlaybackState playbackState;
            if (!state.lightPlaybackStates.TryGetValue(partPersistentId, out playbackState))
            {
                playbackState = new LightPlaybackState();
                state.lightPlaybackStates[partPersistentId] = playbackState;
            }

            return playbackState;
        }

        internal static void ApplyLightPowerEvent(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.isOn = on;
            if (!on)
                SetLightState(state, partPersistentId, false);
            else if (!playbackState.blinkEnabled)
                SetLightState(state, partPersistentId, true);
        }

        internal static void ApplyLightBlinkModeEvent(
            GhostPlaybackState state, uint partPersistentId, bool enabled, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            playbackState.blinkEnabled = enabled;
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        internal static void ApplyLightBlinkRateEvent(GhostPlaybackState state, uint partPersistentId, float blinkRateHz)
        {
            if (state == null) return;
            LightPlaybackState playbackState = GetOrCreateLightPlaybackState(state, partPersistentId);
            if (blinkRateHz > 0f)
                playbackState.blinkRateHz = blinkRateHz;
        }

        internal static void UpdateBlinkingLights(GhostPlaybackState state, double currentUT)
        {
            if (state == null || state.lightPlaybackStates == null || state.lightPlaybackStates.Count == 0)
                return;

            foreach (var kv in state.lightPlaybackStates)
            {
                uint partPersistentId = kv.Key;
                LightPlaybackState playbackState = kv.Value;
                if (playbackState == null)
                    continue;

                bool shouldEnable = playbackState.isOn;
                if (shouldEnable && playbackState.blinkEnabled)
                {
                    float rateHz = playbackState.blinkRateHz > 0f ? playbackState.blinkRateHz : 1f;
                    double cycle = currentUT * rateHz;
                    double frac = cycle - Math.Floor(cycle);
                    shouldEnable = frac < 0.5;
                }

                SetLightState(state, partPersistentId, shouldEnable);
            }
        }

        internal static void SetLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            // Toggle Unity Light components (existing behavior)
            if (state.lightInfos != null)
            {
                LightGhostInfo info;
                if (state.lightInfos.TryGetValue(partPersistentId, out info))
                {
                    for (int i = 0; i < info.lights.Count; i++)
                    {
                        if (info.lights[i] != null)
                            info.lights[i].enabled = on;
                    }
                }
            }

            // Toggle ColorChanger emissive materials (Pattern A: cabin lights)
            ApplyColorChangerLightState(state, partPersistentId, on);
        }

        internal static void ApplyColorChangerLightState(GhostPlaybackState state, uint partPersistentId, bool on)
        {
            if (state.colorChangerInfos == null) return;

            List<ColorChangerGhostInfo> infos;
            if (!state.colorChangerInfos.TryGetValue(partPersistentId, out infos)) return;

            for (int c = 0; c < infos.Count; c++)
            {
                var ccInfo = infos[c];
                if (!ccInfo.isCabinLight) continue; // Only Pattern A responds to light events

                for (int i = 0; i < ccInfo.materials.Count; i++)
                {
                    if (ccInfo.materials[i].material != null)
                    {
                        ccInfo.materials[i].material.SetColor(
                            ccInfo.shaderProperty,
                            on ? ccInfo.materials[i].onColor : ccInfo.materials[i].offColor);
                    }
                }

                ParsekLog.VerboseRateLimited("Flight", $"cc-light-{partPersistentId}",
                    $"Part pid={partPersistentId}: applied color changer cabin light state={on}");
            }
        }

        /// <summary>
        /// Applies ablation char color to heat shield parts (Pattern B) based on reentry intensity.
        /// Called from DriveReentryLayers when reentry glow is active.
        /// </summary>
        internal static void ApplyColorChangerCharState(GhostPlaybackState state, float intensity)
        {
            if (state == null || state.colorChangerInfos == null) return;

            foreach (var kvp in state.colorChangerInfos)
            {
                var infos = kvp.Value;
                for (int c = 0; c < infos.Count; c++)
                {
                    var ccInfo = infos[c];
                    if (ccInfo.isCabinLight) continue; // Only Pattern B responds to reentry

                    // Char is permanent — only increase, never fade back
                    float fraction = Mathf.Clamp01(intensity);
                    if (fraction <= ccInfo.peakCharIntensity) continue;
                    ccInfo.peakCharIntensity = fraction;

                    for (int i = 0; i < ccInfo.materials.Count; i++)
                    {
                        if (ccInfo.materials[i].material != null)
                        {
                            Color lerped = Color.Lerp(
                                ccInfo.materials[i].offColor,
                                ccInfo.materials[i].onColor,
                                fraction);
                            ccInfo.materials[i].material.SetColor(ccInfo.shaderProperty, lerped);
                        }
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Bug #433: decide whether a skipped-ghost trajectory should still fire
        /// PlaybackCompleted at past-end. Only the PlaybackEnabled=false cause is
        /// career-neutral (visibility toggle) and must drive the policy's spawn
        /// branch; !hasData / externalVesselSuppressed are structural and must
        /// silently skip as before.
        ///
        /// Mirrors the visible-path contract in GhostPlaybackEngine at two points:
        ///   - "has renderable data" matches HasRenderableGhostData (Points OR
        ///     OrbitSegments OR SurfacePos) so orbit-only and surface-only
        ///     recordings still complete when hidden.
        ///   - past-end comparisons are strict (`&gt;`), same as the visible path
        ///     at GhostPlaybackEngine.cs `pastEnd = ctx.currentUT &gt; traj.EndUT`
        ///     and `pastEffectiveEnd = ctx.currentUT &gt; f.chainEndUT`, so the
        ///     toggle does not shift completion timing by a frame.
        ///
        /// Pure predicate — accepts pre-collected set-membership booleans so the
        /// engine can pass `HashSet.Contains` results without exposing the set.
        /// </summary>
        internal static bool ShouldFireHiddenPastEndCompletion(
            IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags,
            double currentUT,
            bool completionAlreadyFired,
            bool earlyDebrisCompletion)
        {
            if (traj == null) return false;
            if (completionAlreadyFired || earlyDebrisCompletion) return false;
            if (traj.PlaybackEnabled) return false; // only the visibility-hidden cause
            if (!GhostPlaybackEngine.HasRenderableGhostData(traj)) return false;
            bool pastEnd = currentUT > traj.EndUT;
            bool pastEffectiveEnd = currentUT > flags.chainEndUT;
            return pastEnd || pastEffectiveEnd;
        }

        #region Spawn-at-Recording-End Decision

        /// <summary>
        /// Determines whether spawn should be suppressed because the recording
        /// is an intermediate link in a ghost chain (not the chain tip), or
        /// because the chain is terminated (vessel destroyed/recovered).
        /// Returns (suppressed, reason). Called BEFORE ShouldSpawnAtRecordingEnd
        /// by the playback controller (Phase 6b).
        /// </summary>
        internal static (bool suppressed, string reason) ShouldSuppressSpawnForChain(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0)
                return (false, "");

            if (GhostChainWalker.IsIntermediateChainLink(chains, rec))
            {
                // Per-frame per-recording — rate-limit to avoid log spam
                ParsekLog.VerboseRateLimited("ChainWalker", $"chain-suppress-{rec.RecordingId}",
                    $"Intermediate spawn suppressed: rec={rec.RecordingId} vessel={rec.VesselName}");
                return (true, "intermediate ghost chain link");
            }

            var chain = GhostChainWalker.FindChainForVessel(chains, rec.VesselPersistentId);
            if (chain != null && chain.IsTerminated && chain.TipRecordingId == rec.RecordingId)
            {
                ParsekLog.VerboseRateLimited("ChainWalker",
                    "terminated-spawn-" + rec.VesselPersistentId,
                    string.Format(CultureInfo.InvariantCulture,
                        "Terminated chain spawn suppressed: rec={0} vessel={1} vesselPid={2}",
                        rec.RecordingId, rec.VesselName, rec.VesselPersistentId));
                return (true, "terminated ghost chain");
            }

            return (false, "");
        }

        /// <summary>
        /// Pure decision logic for whether a recording's vessel should be spawned
        /// at the end of its ghost playback (the "spawn-at-recording-end" feature).
        /// Extracted from ParsekFlight.UpdateTimelinePlayback for testability.
        ///
        /// Returns (needsSpawn, reason) where reason explains why spawn was suppressed.
        /// Empty reason means spawn is allowed.
        /// </summary>
        /// <param name="rec">The recording to evaluate.</param>
        /// <param name="isActiveChainMember">True if the recording belongs to the chain currently being built.</param>
        /// <param name="isChainLooping">True if the recording's chain has at least one branch-0 looping segment.</param>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtRecordingEnd(
            Recording rec,
            bool isActiveChainMember,
            bool isChainLooping)
        {
            return ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember,
                isChainLooping,
                treeContext: null);
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtRecordingEnd(
            Recording rec,
            bool isActiveChainMember,
            bool isChainLooping,
            RecordingTree treeContext)
        {
            if (!string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId))
            {
                return (false,
                    "terminal spawn superseded by recording " +
                    rec.TerminalSpawnSupersededByRecordingId);
            }

            // Plain Rewind-to-Launch source protection (#573) remains an absolute
            // block for the active/source recording that was stripped during rewind.
            // Legacy broad tree markers from the old implementation are consumed and
            // ignored here so future same-tree recordings can materialize normally
            // when playback reaches their terminal EndUT (#589).
            if (ShouldBlockSpawnForRewindSuppression(rec, out string rewindSuppressionReason))
            {
                return (false, rewindSuppressionReason);
            }

            // Preserve the existing "already spawned" precedence, but make destroyed
            // recordings win over the generic missing-snapshot diagnostic.
            if (rec.VesselSpawned)
            {
                return (false, "already spawned (VesselSpawned=true)");
            }
            if (rec.VesselDestroyed)
            {
                return (false, "vessel destroyed");
            }
            // Base condition: must have a snapshot
            if (rec.VesselSnapshot == null)
            {
                return (false, "no vessel snapshot");
            }

            // Gloops Flight Recorder recordings are ghost-only — never spawn a real vessel
            if (rec.IsGhostOnly)
            {
                return (false, "ghost-only recording (Gloops)");
            }

            // Branch > 0 recordings are ghost-only (undock continuations) — never spawn
            if (rec.ChainBranch > 0)
            {
                return (false, "branch > 0 (ghost-only)");
            }

            // Suppress spawning for recordings belonging to a chain currently being built
            if (isActiveChainMember)
            {
                return (false, "active chain being built");
            }

            // Looping recordings: first playthrough spawns the vessel (so it exists in the world),
            // subsequent loops are visual-only. The VesselSpawned/SpawnedVesselPersistentId checks
            // above handle this — after first spawn, VesselSpawned=true prevents re-spawning.
            // No blanket LoopPlayback suppression needed here.

            // Suppress spawn for looping chains (ghost loops forever, never reaches a "final" state).
            // Note: fully-disabled chains used to suppress here too, but that gated career state on
            // a visual toggle (bug #433). A fully-disabled chain still spawns its vessel at tip.
            if (isChainLooping)
            {
                return (false, "chain looping");
            }

            // Breakup-continuous check: the foreground recording continued past a breakup
            // (ProcessBreakupEvent sets ChildBranchPointId without creating a same-PID
            // continuation). If no child shares this vessel's PID, the recording IS the
            // effective leaf and should be spawnable. Only applies to non-debris recordings
            // with a spawnable terminal state (Landed/Splashed/Orbiting). (#224)
            bool hasSpawnableTerminal = rec.TerminalStateValue.HasValue &&
                (rec.TerminalStateValue.Value == TerminalState.Landed ||
                 rec.TerminalStateValue.Value == TerminalState.Splashed ||
                 rec.TerminalStateValue.Value == TerminalState.Orbiting);
            bool effectiveLeaf = rec.ChildBranchPointId != null
                && !rec.IsDebris
                && hasSpawnableTerminal
                && IsEffectiveLeafForVessel(rec, treeContext);

            // Non-leaf tree recordings should never spawn — they branched into a
            // same-vessel continuation that carries the correct snapshot.
            if (rec.ChildBranchPointId != null && !effectiveLeaf)
            {
                return (false, "non-leaf tree recording");
            }

            // Safety net: even if ChildBranchPointId is null, check the resolved tree
            // for recordings that are parents of a branch point. Covers edge cases where
            // ChildBranchPointId was not set (e.g., serialization gaps). (#114)
            // Skip for effective-leaf recordings — the branch point exists but the recording
            // is still the leaf for its vessel.
            if (!effectiveLeaf && IsNonLeafInTree(rec, treeContext))
            {
                return (false, "non-leaf in tree (safety net)");
            }

            // Debris recordings are visual-only (short TTL, no meaningful vessel to persist)
            if (rec.IsDebris)
            {
                return (false, "debris recording (visual-only)");
            }

            // Terminal states: destroyed/recovered/docked/boarded/suborbital should not spawn
            // SubOrbital includes FLYING and ESCAPING — vessel would materialize mid-air and crash (#45)
            if (rec.TerminalStateValue.HasValue)
            {
                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered
                    || ts == TerminalState.Docked || ts == TerminalState.Boarded
                    || ts == TerminalState.SubOrbital)
                {
                    return (false, $"terminal state {ts}");
                }
            }

            // Snapshot situation check: if the snapshot's sit field is FLYING or SUB_ORBITAL,
            // KSP's on-rails aero check (101.3 kPa) immediately destroys spawned vessels.
            // This catches cases where TerminalState is null/Landed but the snapshot was
            // captured mid-flight. (#114)
            // Override: if terminal state is Landed/Splashed/Orbiting, the vessel DID reach
            // a safe state — the snapshot's sit field may be stale from recording start.
            // Orbiting: vessel captured during ascent (FLYING) but achieved orbit. The spawn
            // path corrects the snapshot situation before spawning. (#169, #EVA-spawn)
            bool terminalOverridesUnsafe = rec.TerminalStateValue == TerminalState.Landed ||
                rec.TerminalStateValue == TerminalState.Splashed ||
                rec.TerminalStateValue == TerminalState.Orbiting;
            if (!terminalOverridesUnsafe && IsSnapshotSituationUnsafe(rec.VesselSnapshot))
            {
                return (false, "snapshot situation unsafe (FLYING/SUB_ORBITAL)");
            }

            // PID dedup: if vessel was already spawned (PID recorded), never re-spawn.
            // On revert, SpawnedVesselPersistentId resets to 0 from quicksave so reverts still work.
            if (rec.SpawnedVesselPersistentId != 0)
            {
                return (false, $"already spawned (pid={rec.SpawnedVesselPersistentId})");
            }

            return (true, "");
        }

        private static bool ShouldBlockSpawnForRewindSuppression(
            Recording rec,
            out string reason)
        {
            reason = "";
            if (rec == null || !rec.SpawnSuppressedByRewind)
                return false;

            string markerReason = rec.SpawnSuppressedByRewindReason;
            if (string.Equals(markerReason,
                    ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                    StringComparison.Ordinal))
            {
                reason = "spawn suppressed post-rewind (same-recording active/source protection, #573)";
                return true;
            }

            if (string.IsNullOrEmpty(markerReason))
                markerReason = ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped;

            ParsekScenario.ClearRewindSpawnSuppression(
                rec,
                $"reason={markerReason} endpointUT={ParsekScenario.FormatRewindUT(rec.EndUT)}",
                "spawn allowed despite same-tree rewind because marker is not same-recording");
            ParsekLog.Info("Rewind",
                $"Spawn allowed despite same-tree rewind: \"{rec.VesselName}\" id={rec.RecordingId} " +
                $"endpointUT={ParsekScenario.FormatRewindUT(rec.EndUT)} markerReason={markerReason}");
            return false;
        }

        /// <summary>
        /// KSC-specific spawn eligibility check. Simplified version of the Flight scene's
        /// spawn decision: at KSC there is no active chain being built, so isActiveChainMember
        /// is always false. Chain looping/disabled state is derived from RecordingStore.
        /// Returns (needsSpawn, reason) — same semantics as ShouldSpawnAtRecordingEnd.
        /// </summary>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtKscEnd(Recording rec)
        {
            // During rewind, Planetarium UT is still the pre-rewind future value until
            // the deferred coroutine fires. Block all spawns to prevent future vessels
            // from being re-created before the clock is wound back.
            if (RecordingStore.RewindUTAdjustmentPending)
                return (false, "rewind UT adjustment pending — Planetarium UT not yet corrected");

            return ShouldSpawnAtKscEnd(rec, Planetarium.GetUniversalTime());
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtKscEnd(Recording rec, double currentUT)
        {
            // Don't spawn vessels whose recording hasn't finished yet at the current UT (#rewind-persistence)
            if (currentUT < rec.EndUT)
                return (false, $"current UT {currentUT:F0} before recording end {rec.EndUT:F0}");

            // Orbiting/Docked vessels cannot survive pv.Load() in the Space Center scene —
            // KSP crashes them through terrain within frames. Defer to flight scene spawn
            // where SpawnAtPosition can place them correctly. (#171)
            if (rec.TerminalStateValue == TerminalState.Orbiting
                || rec.TerminalStateValue == TerminalState.Docked)
                return (false, $"orbital vessel deferred to flight scene (terminal={rec.TerminalStateValue})");

            // At KSC, no chain is being built → isActiveChainMember = false
            bool isChainLooping = !string.IsNullOrEmpty(rec.ChainId) &&
                RecordingStore.IsChainLooping(rec.ChainId);

            // Intermediate chain segments should not spawn — only the chain tip spawns.
            // In Flight, ShouldSuppressSpawnForChain handles this via runtime GhostChain
            // state, but at KSC there are no GhostChain objects. Use the committed data.
            if (RecordingStore.IsChainMidSegment(rec))
                return (false, "intermediate chain segment (not tip)");

            return ShouldSpawnAtRecordingEnd(rec, false, isChainLooping);
        }

        /// <summary>
        /// Safety-net check: determines whether a recording is a non-leaf node in a
        /// committed tree by scanning the tree's branch points for parent references.
        /// This catches cases where ChildBranchPointId was not set on the recording
        /// (e.g., serialization gaps, edge-case commit paths) but the tree structure
        /// shows the recording has children. (#114)
        /// Static method, testable via RecordingStore.CommittedTrees setup.
        /// </summary>
        internal static bool IsNonLeafInCommittedTree(Recording rec)
        {
            return IsNonLeafInTree(rec, treeContext: null);
        }

        internal static bool IsNonLeafInTree(Recording rec, RecordingTree treeContext)
        {
            RecordingTree tree;
            if (!TryResolveTreeContext(rec, treeContext, out tree))
                return false;

            // Check if any branch point lists this recording as a parent.
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                var bp = tree.BranchPoints[b];
                if (bp.ParentRecordingIds != null && bp.ParentRecordingIds.Contains(rec.RecordingId))
                {
                    string treeLabel = !string.IsNullOrEmpty(tree.Id) ? tree.Id : (tree.TreeName ?? "(pending)");
                    ParsekLog.VerboseRateLimited("Spawner",
                        $"safety-net-{rec.RecordingId}",
                        string.Format(CultureInfo.InvariantCulture,
                            "IsNonLeafInTree: recording {0} is parent of branch point {1} " +
                            "in tree {2} (ChildBranchPointId was null — safety net triggered)",
                            rec.RecordingId, bp.Id, treeLabel), 30.0);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true when a recording with ChildBranchPointId is the effective leaf
        /// for its vessel — no child recording of that branch point shares the same
        /// VesselPersistentId. This happens for breakup-continuous foreground recordings
        /// where ProcessBreakupEvent sets ChildBranchPointId without creating a same-PID
        /// continuation (debris-only breakups on splashdown/landing). (#224)
        /// </summary>
        internal static bool IsEffectiveLeafForVessel(Recording rec)
        {
            return IsEffectiveLeafForVessel(rec, treeContext: null);
        }

        internal static bool IsEffectiveLeafForVessel(Recording rec, RecordingTree treeContext)
        {
            if (string.IsNullOrEmpty(rec.ChildBranchPointId))
                return false;

            RecordingTree tree;
            if (!TryResolveTreeContext(rec, treeContext, out tree))
                return false;

            // Find the branch point
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                var bp = tree.BranchPoints[b];
                if (bp.Id != rec.ChildBranchPointId) continue;

                // Check if any child recording shares the same vessel PID
                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                {
                    Recording childRec;
                    if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out childRec))
                    {
                        if (childRec.VesselPersistentId == rec.VesselPersistentId)
                            return false; // Same-PID continuation exists — NOT effective leaf
                    }
                }

                // No child shares this vessel PID — recording IS the effective leaf
                ParsekLog.VerboseRateLimited("Spawner",
                    rec.RecordingId,
                    string.Format(CultureInfo.InvariantCulture,
                        "IsEffectiveLeafForVessel: recording {0} vessel={1} is effective leaf " +
                        "(breakup-continuous, no same-PID continuation child)",
                        rec.RecordingId, rec.VesselPersistentId));
                return true;
            }
            return false;
        }

        private static bool TryResolveTreeContext(
            Recording rec,
            RecordingTree treeContext,
            out RecordingTree tree)
        {
            tree = null;
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId) || !rec.IsTreeRecording)
                return false;

            if (treeContext != null)
            {
                bool sameTreeId = !string.IsNullOrEmpty(treeContext.Id) && treeContext.Id == rec.TreeId;
                bool containsRecording = treeContext.Recordings != null
                    && treeContext.Recordings.ContainsKey(rec.RecordingId);
                if (sameTreeId || containsRecording)
                {
                    tree = treeContext;
                    return true;
                }
            }

            var trees = RecordingStore.CommittedTrees;
            for (int t = 0; t < trees.Count; t++)
            {
                if (trees[t].Id == rec.TreeId)
                {
                    tree = trees[t];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a vessel snapshot's situation is unsafe for spawning.
        /// FLYING and SUB_ORBITAL vessels are immediately killed by KSP's on-rails
        /// atmospheric pressure check (101.3 kPa at sea level). (#114)
        /// Pure static method for testability.
        /// </summary>
        internal static bool IsSnapshotSituationUnsafe(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return false;

            string sit = vesselSnapshot.GetValue("sit");
            if (string.IsNullOrEmpty(sit)) return false;

            // KSP situation strings: LANDED, SPLASHED, PRELAUNCH, FLYING,
            // SUB_ORBITAL, ORBITING, ESCAPING, DOCKED
            return sit.Equals("FLYING", System.StringComparison.OrdinalIgnoreCase)
                || sit.Equals("SUB_ORBITAL", System.StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Zone-Based Rendering

        /// <summary>
         /// Determines the rendering actions to take when a ghost transitions between zones.
         /// Returns (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning).
         /// </summary>
        internal static (bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning)
            GetZoneRenderingPolicy(RenderingZone zone)
        {
            switch (zone)
            {
                case RenderingZone.Beyond:
                    return (true, true, true);
                case RenderingZone.Visual:
                    return (false, false, false); // part events apply in Visual zone
                case RenderingZone.Physics:
                default:
                    return (false, false, false);
            }
        }

        /// <summary>
        /// Returns true when the watched ghost should ignore distance-based LOD suppression
        /// and stay at full fidelity for the current frame.
        /// </summary>
        internal static bool ShouldForceWatchedFullFidelity(
            bool isWatchedGhost, double ghostDistanceMeters, float cutoffKm)
        {
            return isWatchedGhost && !ShouldExitWatchForCutoff(ghostDistanceMeters, cutoffKm);
        }

        /// <summary>
        /// Applies the watched-ghost full-fidelity override to a zone policy tuple.
        /// Distance-based LOD should not suppress a watched ghost that is still within cutoff.
        /// </summary>
        internal static (bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning)
            ApplyWatchedFullFidelityOverride(
                bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning,
                bool forceFullFidelity)
        {
            if (!forceFullFidelity)
                return (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning);

            return (false, false, false);
        }

        /// <summary>
        /// Applies the distance-based LOD tiers for unwatched ghosts on top of the base zone policy.
        /// The thresholds intentionally reuse the shared distance constants rather than adding
        /// another set of rendering knobs.
        /// </summary>
        internal static (bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning,
            bool shouldSuppressVisualFx, bool shouldReduceFidelity)
            ApplyDistanceLodPolicy(
                bool shouldHideMesh, bool shouldSkipPartEvents, bool shouldSkipPositioning,
                double ghostDistanceMeters, bool forceFullFidelity)
        {
            if (forceFullFidelity)
                return (false, false, false, false, false);

            if (shouldHideMesh)
                return (true, true, true, true, false);

            if (ghostDistanceMeters >= DistanceThresholds.GhostFlight.LoopSimplifiedMeters)
                return (true, true, true, true, false);

            if (ghostDistanceMeters >= DistanceThresholds.PhysicsBubbleMeters)
                return (false, true, false, true, true);

            return (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning, false, false);
        }

        /// <summary>
        /// Detects a zone transition and returns whether the zone changed.
        /// Pure decision method — does not mutate state or log.
        /// </summary>
        internal static bool DetectZoneTransition(
            RenderingZone previousZone, RenderingZone newZone,
            out string transitionDescription)
        {
            if (previousZone == newZone)
            {
                transitionDescription = null;
                return false;
            }

            // Describe the transition direction
            bool movingOutward = (int)newZone > (int)previousZone;
            transitionDescription = movingOutward ? "outward" : "inward";
            return true;
        }

        /// <summary>
        /// Determines whether a looped ghost should be spawned at the given distance,
        /// and whether it should use simplified rendering (no part events).
        /// Wraps RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance for consistency.
        /// </summary>
        internal static (bool shouldSpawn, bool simplified) EvaluateLoopedGhostSpawn(
            double distanceMeters)
        {
            return RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(distanceMeters);
        }

        #endregion

        #region Soft Cap Fidelity

        /// <summary>
        /// Reduces ghost visual fidelity by disabling a fraction of renderers.
        /// Keeps approximately 1 in 4 renderers to maintain recognizable shape
        /// while significantly reducing draw calls.
        /// </summary>
        internal static void ReduceGhostFidelity(GhostPlaybackState state)
        {
            if (state.ghost == null) return;
            var renderers = state.ghost.GetComponentsInChildren<Renderer>(true);
            state.fidelityDisabledRenderers = new List<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                // Keep every 4th renderer for a coarse silhouette
                if (i % 4 == 0) continue;
                if (!renderers[i].enabled) continue;
                renderers[i].enabled = false;
                state.fidelityDisabledRenderers.Add(renderers[i]);
            }
            state.fidelityReduced = true;
            ParsekLog.Verbose("Visual",
                $"ReduceFidelity: disabled {state.fidelityDisabledRenderers.Count}/{renderers.Length} renderers");
        }

        /// <summary>
        /// Restores ghost visual fidelity by re-enabling only the renderers that
        /// ReduceGhostFidelity disabled. Preserves part-event visibility state
        /// (e.g. decoupled/destroyed parts stay hidden).
        /// </summary>
        internal static void RestoreGhostFidelity(GhostPlaybackState state)
        {
            if (state.fidelityDisabledRenderers != null)
            {
                int restored = 0;
                for (int i = 0; i < state.fidelityDisabledRenderers.Count; i++)
                {
                    if (state.fidelityDisabledRenderers[i] != null)
                    {
                        state.fidelityDisabledRenderers[i].enabled = true;
                        restored++;
                    }
                }
                ParsekLog.Verbose("Visual", $"RestoreGhostFidelity: re-enabled {restored} renderers");
                state.fidelityDisabledRenderers = null;
            }
            state.fidelityReduced = false;
        }

        /// <summary>
        /// Restores any runtime suppression state that would prevent a watched ghost from
        /// rendering at full fidelity. Used when watch mode overrides distance-based LOD.
        /// </summary>
        internal static void RestoreWatchedFullFidelityState(GhostPlaybackState state)
        {
            if (state == null) return;

            if (state.fidelityReduced)
                RestoreGhostFidelity(state);
            state.distanceLodReduced = false;

            if (state.simplified)
            {
                if (state.ghost != null && !state.ghost.activeSelf)
                    state.ghost.SetActive(true);
                state.simplified = false;
            }
        }

        /// <summary>
        /// Applies or removes the distance-based reduced-fidelity renderer mode without
        /// interfering with the soft-cap ownership of the same visual primitive.
        /// </summary>
        internal static void ApplyDistanceLodFidelity(GhostPlaybackState state, bool shouldReduceFidelity)
        {
            if (state == null || state.ghost == null) return;

            if (shouldReduceFidelity)
            {
                if (!state.distanceLodReduced && !state.fidelityReduced)
                {
                    ReduceGhostFidelity(state);
                    state.distanceLodReduced = true;
                }
                return;
            }

            if (state.distanceLodReduced)
            {
                RestoreGhostFidelity(state);
                state.distanceLodReduced = false;
            }
        }

        /// <summary>
        /// Protected ghosts (currently watched) should ignore runtime suppression that
        /// would reduce or hide them.
        /// </summary>
        internal static bool IsProtectedGhost(int protectedIndex, int currentIndex)
        {
            return protectedIndex == currentIndex;
        }

        internal static bool IsProtectedGhost(
            int protectedIndex, long protectedLoopCycleIndex,
            int currentIndex, long currentLoopCycleIndex)
        {
            return protectedIndex == currentIndex
                && protectedLoopCycleIndex == currentLoopCycleIndex;
        }

        /// <summary>
        /// Returns true when the current recording should inherit watch-mode protection.
        /// This is broader than the exact watched ghost: breakup debris linked to the
        /// watched vessel's same-tree lineage should stay visible while that vessel is watched.
        /// </summary>
        internal static bool IsWatchProtectedRecording(
            IReadOnlyList<Recording> committed, int watchedRecordingIndex, int currentIndex)
        {
            return IsWatchProtectedRecording(
                committed, RecordingStore.CommittedTrees, watchedRecordingIndex, currentIndex);
        }

        internal static bool IsWatchProtectedRecording(
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> committedTrees,
            int watchedRecordingIndex, int currentIndex)
        {
            if (committed == null
                || watchedRecordingIndex < 0
                || currentIndex < 0
                || watchedRecordingIndex >= committed.Count
                || currentIndex >= committed.Count)
                return false;

            if (watchedRecordingIndex == currentIndex)
                return true;

            Recording watched = committed[watchedRecordingIndex];
            Recording current = committed[currentIndex];
            if (watched == null || current == null || !current.IsDebris)
                return false;

            if (string.IsNullOrEmpty(watched.TreeId)
                || string.IsNullOrEmpty(current.TreeId)
                || watched.TreeId != current.TreeId)
                return false;

            if (IsLoopSyncedDebrisOfWatchedLineage(committed, watched, current))
                return true;

            RecordingTree tree = FindTreeById(committedTrees, watched.TreeId);
            return IsDebrisDescendedFromWatchedLineage(watched, current, tree);
        }

        internal static double ComputeWatchLineageProtectionUntilUT(
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> committedTrees,
            int watchedRecordingIndex,
            double currentUT)
        {
            if (committed == null
                || watchedRecordingIndex < 0
                || watchedRecordingIndex >= committed.Count)
            {
                return double.NaN;
            }

            bool hasCurrentUT = !double.IsNaN(currentUT) && !double.IsInfinity(currentUT);
            double protectionUntilUT = double.NaN;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate == null || !candidate.IsDebris)
                    continue;
                if (hasCurrentUT && candidate.EndUT < currentUT)
                    continue;
                if (!IsWatchProtectedRecording(committed, committedTrees, watchedRecordingIndex, i))
                    continue;

                if (double.IsNaN(protectionUntilUT) || candidate.EndUT > protectionUntilUT)
                    protectionUntilUT = candidate.EndUT;
            }

            return protectionUntilUT;
        }

        private static bool IsLoopSyncedDebrisOfWatchedLineage(
            IReadOnlyList<Recording> committed, Recording watched, Recording current)
        {
            int parentIdx = current.LoopSyncParentIdx;
            if (committed == null || parentIdx < 0 || parentIdx >= committed.Count)
                return false;

            Recording parent = committed[parentIdx];
            if (parent == null || parent.TreeId != watched.TreeId)
                return false;

            if (parent.RecordingId == watched.RecordingId)
                return true;

            if (watched.VesselPersistentId == 0 || parent.VesselPersistentId == 0)
                return false;

            return parent.VesselPersistentId == watched.VesselPersistentId;
        }

        private static bool IsDebrisDescendedFromWatchedLineage(
            Recording watched, Recording current, RecordingTree tree)
        {
            if (watched == null || current == null || tree == null)
                return false;

            var pendingBranchPoints = new Queue<string>();
            var visitedBranchPoints = new HashSet<string>();
            var visitedRecordings = new HashSet<string>();

            if (!string.IsNullOrEmpty(current.ParentBranchPointId))
                pendingBranchPoints.Enqueue(current.ParentBranchPointId);

            while (pendingBranchPoints.Count > 0)
            {
                string branchPointId = pendingBranchPoints.Dequeue();
                if (string.IsNullOrEmpty(branchPointId) || !visitedBranchPoints.Add(branchPointId))
                    continue;

                BranchPoint branchPoint = FindBranchPointById(tree, branchPointId);
                if (branchPoint?.ParentRecordingIds == null)
                    continue;

                for (int i = 0; i < branchPoint.ParentRecordingIds.Count; i++)
                {
                    string parentRecordingId = branchPoint.ParentRecordingIds[i];
                    if (string.IsNullOrEmpty(parentRecordingId) || !visitedRecordings.Add(parentRecordingId))
                        continue;

                    Recording parent;
                    if (!tree.Recordings.TryGetValue(parentRecordingId, out parent) || parent == null)
                        continue;

                    if (parent.RecordingId == watched.RecordingId)
                        return true;

                    if (watched.VesselPersistentId != 0
                        && parent.VesselPersistentId != 0
                        && parent.VesselPersistentId == watched.VesselPersistentId)
                    {
                        return true;
                    }

                    if (!string.IsNullOrEmpty(parent.ParentBranchPointId))
                        pendingBranchPoints.Enqueue(parent.ParentBranchPointId);
                }
            }

            return false;
        }

        private static RecordingTree FindTreeById(
            IReadOnlyList<RecordingTree> committedTrees, string treeId)
        {
            if (committedTrees == null || string.IsNullOrEmpty(treeId))
                return null;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                RecordingTree tree = committedTrees[i];
                if (tree != null && tree.Id == treeId)
                    return tree;
            }

            return null;
        }

        private static BranchPoint FindBranchPointById(RecordingTree tree, string branchPointId)
        {
            if (tree?.BranchPoints == null || string.IsNullOrEmpty(branchPointId))
                return null;

            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint branchPoint = tree.BranchPoints[i];
                if (branchPoint != null && branchPoint.Id == branchPointId)
                    return branchPoint;
            }

            return null;
        }

        #endregion

        #region Watch Mode Decisions

        /// <summary>
        /// Determines whether the watch mode should auto-exit because the ghost
        /// exceeded the camera cutoff distance.
        /// </summary>
        internal static bool ShouldExitWatchForCutoff(double ghostDistanceMeters, float cutoffKm)
        {
            return ghostDistanceMeters >= cutoffKm * 1000.0;
        }

        /// <summary>
        /// Determines whether a ghost is within the camera cutoff distance
        /// (eligible for Watch button). Only checks distance against the
        /// user-configurable cutoff — zone is irrelevant because watch mode
        /// moves the camera to the ghost (T39).
        /// </summary>
        internal static bool IsWithinWatchRange(double distanceMeters, float cutoffKm)
        {
            return distanceMeters < cutoffKm * 1000.0;
        }

        /// <summary>
        /// Resolves the effective watch target for a source recording by walking the
        /// preferred continuation lineage until an active ghost is found.
        /// Returns the source index itself when its ghost is still active, otherwise
        /// follows chain/tree continuation rules through inactive intermediates.
        /// Used by aggregate UI affordances that should continue tracking the live
        /// vessel after watch auto-follow hands off to a descendant segment.
        /// </summary>
        internal static int ResolveEffectiveWatchTargetIndex(
            int sourceIndex,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive)
        {
            return ResolveEffectiveWatchTargetIndex(
                sourceIndex,
                committed,
                trees,
                isGhostActive,
                new HashSet<int>(),
                depth: 0);
        }

        private static int ResolveEffectiveWatchTargetIndex(
            int sourceIndex,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            HashSet<int> visited,
            int depth)
        {
            const int MaxRecursionDepth = 16;
            if (committed == null
                || visited == null
                || sourceIndex < 0
                || sourceIndex >= committed.Count
                || depth > MaxRecursionDepth
                || !visited.Add(sourceIndex))
            {
                return -1;
            }

            if (isGhostActive != null && isGhostActive(sourceIndex))
                return sourceIndex;

            Recording currentRec = committed[sourceIndex];
            if (currentRec == null)
                return -1;

            int nextChainIndex = FindImmediateChainContinuationIndex(currentRec, committed);
            if (nextChainIndex >= 0)
            {
                int resolvedChainIndex = ResolveEffectiveWatchTargetIndex(
                    nextChainIndex,
                    committed,
                    trees,
                    isGhostActive,
                    visited,
                    depth + 1);
                if (resolvedChainIndex >= 0)
                    return resolvedChainIndex;
            }

            return ResolveEffectiveTreeWatchTargetIndex(
                currentRec,
                committed,
                trees,
                isGhostActive,
                visited,
                depth);
        }

        private static int FindImmediateChainContinuationIndex(
            Recording currentRec,
            IReadOnlyList<Recording> committed)
        {
            if (currentRec == null
                || committed == null
                || string.IsNullOrEmpty(currentRec.ChainId)
                || currentRec.ChainIndex < 0
                || currentRec.ChainBranch != 0)
            {
                return -1;
            }

            int nextChainIndex = currentRec.ChainIndex + 1;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate == null)
                    continue;

                if (candidate.ChainId == currentRec.ChainId
                    && candidate.ChainBranch == 0
                    && candidate.ChainIndex == nextChainIndex)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int ResolveEffectiveTreeWatchTargetIndex(
            Recording currentRec,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            HashSet<int> visited,
            int depth)
        {
            if (currentRec == null
                || committed == null
                || trees == null
                || string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                || !currentRec.IsTreeRecording)
            {
                return -1;
            }

            RecordingTree tree = FindTreeById(trees, currentRec.TreeId);
            BranchPoint branchPoint = FindBranchPointById(tree, currentRec.ChildBranchPointId);
            if (branchPoint == null)
                return -1;

            bool pidMatchFound = false;
            for (int i = 0; i < branchPoint.ChildRecordingIds.Count; i++)
            {
                int childIndex = FindRecordingIndexById(committed, branchPoint.ChildRecordingIds[i]);
                if (childIndex < 0)
                    continue;

                Recording child = committed[childIndex];
                if (child == null || child.VesselPersistentId != currentRec.VesselPersistentId)
                    continue;

                pidMatchFound = true;
                int resolvedChildIndex = ResolveEffectiveWatchTargetIndex(
                    childIndex,
                    committed,
                    trees,
                    isGhostActive,
                    visited,
                    depth + 1);
                if (resolvedChildIndex >= 0)
                    return resolvedChildIndex;
            }

            // Mirror watch auto-follow: once a same-PID continuation exists, do not
            // fall through to other branches if that lineage has no active target yet.
            if (pidMatchFound || branchPoint.Type == BranchPointType.Breakup)
                return -1;

            for (int i = 0; i < branchPoint.ChildRecordingIds.Count; i++)
            {
                int childIndex = FindRecordingIndexById(committed, branchPoint.ChildRecordingIds[i]);
                if (childIndex < 0)
                    continue;

                Recording child = committed[childIndex];
                if (child == null || child.IsDebris)
                    continue;

                // Mirror FindNextWatchTarget exactly for non-PID fallback:
                // only an immediate active non-debris child is a valid target.
                if (isGhostActive != null && isGhostActive(childIndex))
                    return childIndex;
            }

            return -1;
        }

        /// <summary>
        /// Bug #382: result of advancing a group's watch-rotation cursor. Returned by
        /// <see cref="AdvanceGroupWatchCursor"/>. When <see cref="NextRecordingId"/> is
        /// null there are no eligible descendants in the group (button should be
        /// disabled). When <see cref="IsToggleOff"/> is true the only eligible
        /// descendant is the one currently being watched, and the caller should call
        /// ExitWatchMode rather than re-enter the same target.
        /// </summary>
        internal readonly struct GroupWatchAdvanceResult
        {
            public readonly string NextRecordingId;   // null == empty eligible set
            public readonly int Position;             // 1-based index in eligible list (0 when NextRecordingId is null)
            public readonly int TotalEligible;        // count of eligible descendants
            public readonly bool IsToggleOff;         // single-entry rotation where that entry IS currently watched
            public readonly bool IsWrap;              // advance wrapped past the end of the eligible list

            public GroupWatchAdvanceResult(string nextId, int pos, int total, bool toggleOff, bool wrap)
            {
                NextRecordingId = nextId;
                Position = pos;
                TotalEligible = total;
                IsToggleOff = toggleOff;
                IsWrap = wrap;
            }

            public static GroupWatchAdvanceResult Empty => new GroupWatchAdvanceResult(null, 0, 0, false, false);
        }

        /// <summary>
        /// Bug #382: advances the rotation cursor for a group's W button. Builds a
        /// stable eligible list from <paramref name="descendants"/> (sorted by
        /// <c>StartUT</c> ascending, with <c>RecordingId</c> ordinal ascending as
        /// a deterministic tiebreaker), locates <paramref name="cursorRecordingId"/>
        /// in that list, and advances one step forward (wrapping) to the first entry
        /// whose <c>RecordingId</c> differs from <paramref name="currentlyWatchedRecId"/>.
        ///
        /// The <paramref name="isEligible"/> predicate is the single source of truth
        /// for "watchable" — callers are expected to fold
        /// <c>hasGhost &amp;&amp; sameBody &amp;&amp; inRange &amp;&amp; !IsDebris</c>
        /// (plus any non-null RecordingId filter they need) into it.
        ///
        /// If every eligible entry equals <paramref name="currentlyWatchedRecId"/>
        /// (only possible when the rotation reduces to a single entry and that entry
        /// is already the watched one), the result has
        /// <see cref="GroupWatchAdvanceResult.IsToggleOff"/> = true and
        /// <see cref="GroupWatchAdvanceResult.NextRecordingId"/> set to the watched
        /// id, so the caller can detect the identity and invoke ExitWatchMode.
        /// </summary>
        internal static GroupWatchAdvanceResult AdvanceGroupWatchCursor(
            HashSet<int> descendants,
            IReadOnlyList<Recording> committed,
            Func<int, bool> isEligible,
            string cursorRecordingId,
            string currentlyWatchedRecId)
        {
            if (descendants == null || committed == null || isEligible == null || descendants.Count == 0)
                return GroupWatchAdvanceResult.Empty;

            // 1. Build eligible list. Only Recording refs are needed from here on;
            // the UI re-resolves index by RecordingId after the call.
            var eligible = new List<Recording>(descendants.Count);
            foreach (int idx in descendants)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var rec = committed[idx];
                if (rec == null) continue;
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (!isEligible(idx)) continue;
                eligible.Add(rec);
            }

            if (eligible.Count == 0)
                return GroupWatchAdvanceResult.Empty;

            // 2. Stable sort: StartUT asc, then RecordingId ordinal asc.
            eligible.Sort((a, b) =>
            {
                int c = a.StartUT.CompareTo(b.StartUT);
                return c != 0 ? c : string.CompareOrdinal(a.RecordingId, b.RecordingId);
            });
            int count = eligible.Count;

            // 3. Locate cursor by RecordingId. -1 means "before-first".
            int cursorPos = -1;
            if (!string.IsNullOrEmpty(cursorRecordingId))
            {
                for (int i = 0; i < count; i++)
                {
                    if (eligible[i].RecordingId == cursorRecordingId)
                    {
                        cursorPos = i;
                        break;
                    }
                }
            }

            // 4. Walk forward, wrapping, skipping the currently-watched id.
            for (int step = 1; step <= count; step++)
            {
                // (((cursorPos + step) % count) + count) % count handles the
                // cursorPos=-1 "before-first" sentinel: with step=1 the first
                // probe is 0 (first eligible entry). The double-mod is
                // defensive against any future negative inputs.
                int probe = ((cursorPos + step) % count + count) % count;
                var candidate = eligible[probe];
                if (candidate.RecordingId != currentlyWatchedRecId)
                {
                    // stepping wrapped past end: probe index is at or before cursorPos.
                    bool wrap = cursorPos >= 0 && probe <= cursorPos;
                    return new GroupWatchAdvanceResult(candidate.RecordingId, probe + 1, count, toggleOff: false, wrap: wrap);
                }
            }

            // 5. All eligible entries equal currentlyWatchedRecId → single-entry rotation
            //    that IS watched. Return the watched id with IsToggleOff = true.
            //    Because step 1's filter rejects rows with null/empty RecordingId, every
            //    candidate.RecordingId in the loop is a non-null string. If
            //    currentlyWatchedRecId is null, every iteration's inequality check is true
            //    and the loop returns on the first probe. So this trailing return only
            //    fires when currentlyWatchedRecId is a non-null string equal to every
            //    eligible entry — guaranteed-safe toggle-off.
            return new GroupWatchAdvanceResult(currentlyWatchedRecId, 1, count, toggleOff: true, wrap: false);
        }

        internal static int FindRecordingIndexById(
            IReadOnlyList<Recording> committed,
            string recordingId)
        {
            if (committed == null || string.IsNullOrEmpty(recordingId))
                return -1;

            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate != null && candidate.RecordingId == recordingId)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Searches committed recordings for the next watch target after the current
        /// recording completes. Handles chain continuation (same chainId, next index)
        /// and tree branching (childBranchPointId → child with same vessel PID).
        /// </summary>
        /// <param name="currentRec">The recording that just completed.</param>
        /// <param name="committed">All committed recordings.</param>
        /// <param name="trees">All committed trees (for branch point lookup).</param>
        /// <param name="isGhostActive">Predicate: is there an active ghost at index j?</param>
        /// <returns>Index into committed, or -1 if no target found.</returns>
        internal static int FindNextWatchTarget(
            Recording currentRec,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            int depth = 0)
        {
            const int MaxRecursionDepth = 10;
            if (currentRec == null || committed == null || depth > MaxRecursionDepth) return -1;

            // Case 1: Chain continuation (same chainId, next chainIndex, branch 0)
            if (!string.IsNullOrEmpty(currentRec.ChainId) && currentRec.ChainIndex >= 0
                && currentRec.ChainBranch == 0)
            {
                int nextChainIndex = currentRec.ChainIndex + 1;
                for (int j = 0; j < committed.Count; j++)
                {
                    var candidate = committed[j];
                    if (candidate.ChainId == currentRec.ChainId
                        && candidate.ChainBranch == 0
                        && candidate.ChainIndex == nextChainIndex
                        && isGhostActive(j))
                    {
                        return j;
                    }
                }
            }

            // Case 2: Tree branching via ChildBranchPointId
            if (!string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                && currentRec.IsTreeRecording
                && trees != null)
            {
                BranchPoint bp = null;
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree.Id != currentRec.TreeId) continue;
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        if (tree.BranchPoints[b].Id == currentRec.ChildBranchPointId)
                        {
                            bp = tree.BranchPoints[b];
                            break;
                        }
                    }
                    break;
                }

                if (bp != null)
                {
                    int fallbackIdx = -1;
                    bool pidMatchFound = false;
                    bool allowDifferentPidFallback = bp.Type != BranchPointType.Breakup;
                    bool blockedDifferentPidActiveChildFound = false;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        string childId = bp.ChildRecordingIds[c];
                        for (int j = 0; j < committed.Count; j++)
                        {
                            if (committed[j].RecordingId != childId) continue;

                            bool isPidMatch = committed[j].VesselPersistentId == currentRec.VesselPersistentId;

                            if (isGhostActive(j))
                            {
                                // Prefer child with same vessel PID (same vessel continues)
                                if (isPidMatch)
                                    return j;

                                // Bug #321: breakup/crash watch recovery should stay with
                                // the preserved live vessel context unless the same vessel
                                // actually continues. Non-breakup branches may still fall
                                // back to the first active non-debris child.
                                if (allowDifferentPidFallback
                                    && !committed[j].IsDebris
                                    && fallbackIdx < 0)
                                {
                                    fallbackIdx = j;
                                }
                                else if (!allowDifferentPidFallback)
                                {
                                    blockedDifferentPidActiveChildFound = true;
                                }
                            }
                            else if (isPidMatch)
                            {
                                // #158: PID-matched continuation has no ghost (boundary seed
                                // with insufficient data). Recursively descend through its
                                // children to find a deeper target with an active ghost.
                                pidMatchFound = true;
                                int deeper = FindNextWatchTarget(
                                    committed[j], committed, trees, isGhostActive, depth + 1);
                                if (deeper >= 0)
                                    return deeper;
                            }
                        }
                    }
                    // #158: If we found the PID-matched continuation but it (and its
                    // descendants) have no ghost, don't fall through to debris — there's
                    // no good target. The watch hold timer will expire naturally.
                    if (pidMatchFound)
                        return -1;
                    if (!allowDifferentPidFallback && blockedDifferentPidActiveChildFound)
                    {
                        ParsekLog.VerboseRateLimited("Watch",
                            $"breakup-watch-no-fallback-{currentRec.RecordingId}",
                            $"FindNextWatchTarget: breakup branch {bp.Id} for rec '{currentRec.VesselName}' " +
                            "has no same-PID continuation — preserving live vessel context");
                    }
                    if (fallbackIdx >= 0)
                        return fallbackIdx;
                }
            }

            return -1;
        }

        /// <summary>
        /// Returns the earliest UT at which a preferred watch continuation could become
        /// ghost-active, even if it is not active yet. This is used to extend the
        /// watch-end hold timer for quickload-resumed branches whose continuation data
        /// starts later than the parent branch boundary.
        /// </summary>
        internal static bool TryGetPendingWatchActivationUT(
            Recording currentRec,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            out double activationUT,
            int depth = 0)
        {
            activationUT = double.NaN;
            const int MaxRecursionDepth = 10;
            if (currentRec == null || committed == null || depth > MaxRecursionDepth)
                return false;

            // Case 1: Chain continuation.
            if (!string.IsNullOrEmpty(currentRec.ChainId)
                && currentRec.ChainIndex >= 0
                && currentRec.ChainBranch == 0)
            {
                int nextChainIndex = currentRec.ChainIndex + 1;
                for (int j = 0; j < committed.Count; j++)
                {
                    var candidate = committed[j];
                    if (candidate.ChainId != currentRec.ChainId
                        || candidate.ChainBranch != 0
                        || candidate.ChainIndex != nextChainIndex)
                    {
                        continue;
                    }

                    if (isGhostActive != null && isGhostActive(j))
                        return false;

                    return candidate.TryGetGhostActivationStartUT(out activationUT);
                }
            }

            // Case 2: Tree branching via ChildBranchPointId. Mirror FindNextWatchTarget:
            // prefer same-PID continuation, otherwise allow non-debris fallback on
            // non-breakup branches.
            if (!string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                && currentRec.IsTreeRecording
                && trees != null)
            {
                BranchPoint bp = null;
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree.Id != currentRec.TreeId)
                        continue;

                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        if (tree.BranchPoints[b].Id == currentRec.ChildBranchPointId)
                        {
                            bp = tree.BranchPoints[b];
                            break;
                        }
                    }
                    break;
                }

                if (bp != null)
                {
                    double samePidActivationUT = double.NaN;
                    double fallbackActivationUT = double.NaN;
                    bool sawSamePidContinuation = false;
                    bool sawActiveFallback = false;
                    bool allowDifferentPidFallback = bp.Type != BranchPointType.Breakup;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        string childId = bp.ChildRecordingIds[c];
                        for (int j = 0; j < committed.Count; j++)
                        {
                            var candidate = committed[j];
                            if (candidate.RecordingId != childId)
                            {
                                continue;
                            }

                            bool isPidMatch = candidate.VesselPersistentId == currentRec.VesselPersistentId;
                            bool isAllowedFallback = allowDifferentPidFallback && !candidate.IsDebris;
                            if (!isPidMatch && !isAllowedFallback)
                                continue;

                            if (isPidMatch)
                            {
                                sawSamePidContinuation = true;

                                if (isGhostActive != null && isGhostActive(j))
                                    return false;

                                if (candidate.TryGetGhostActivationStartUT(out double candidateActivationUT))
                                    samePidActivationUT = MinPendingActivationUT(samePidActivationUT, candidateActivationUT);

                                if (TryGetPendingWatchActivationUT(
                                        candidate, committed, trees, isGhostActive, out double deeperActivationUT, depth + 1))
                                {
                                    samePidActivationUT = MinPendingActivationUT(samePidActivationUT, deeperActivationUT);
                                }
                                continue;
                            }

                            if (isGhostActive != null && isGhostActive(j))
                            {
                                sawActiveFallback = true;
                                continue;
                            }

                            if (candidate.TryGetGhostActivationStartUT(out double candidateActivationFallbackUT))
                                fallbackActivationUT = MinPendingActivationUT(fallbackActivationUT, candidateActivationFallbackUT);
                        }
                    }

                    if (sawSamePidContinuation && !double.IsNaN(samePidActivationUT))
                    {
                        activationUT = samePidActivationUT;
                        return true;
                    }
                    if (sawSamePidContinuation)
                        return false;
                    if (sawActiveFallback)
                        return false;
                    if (!double.IsNaN(fallbackActivationUT))
                    {
                        activationUT = fallbackActivationUT;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static float ComputePendingWatchHoldSeconds(
            float baseHoldSeconds,
            double currentUT,
            double continuationActivationUT,
            float warpRate)
        {
            if (baseHoldSeconds < 0f)
                baseHoldSeconds = 0f;

            if (double.IsNaN(continuationActivationUT) || continuationActivationUT <= currentUT)
                return baseHoldSeconds;

            // #369: harden against NaN warp rate — Mathf.Ceil((x / NaN) + grace) is
            // NaN and Mathf.Clamp on NaN silently falls through to the base hold.
            float effectiveWarpRate = (!float.IsNaN(warpRate) && warpRate > 0.01f) ? warpRate : 1f;
            float requiredSeconds = Mathf.Ceil((float)((continuationActivationUT - currentUT) / effectiveWarpRate)
                + WatchMode.PendingPostActivationGraceSeconds);
            return Mathf.Clamp(Mathf.Max(baseHoldSeconds, requiredSeconds), baseHoldSeconds, WatchMode.MaxPendingHoldSeconds);
        }

        internal static void ComputePendingWatchHoldWindow(
            float baseHoldSeconds,
            float currentRealtime,
            double currentUT,
            double continuationActivationUT,
            float warpRate,
            out float holdUntilRealTime,
            out float holdMaxRealTime)
        {
            float holdSeconds = ComputePendingWatchHoldSeconds(
                baseHoldSeconds,
                currentUT,
                continuationActivationUT,
                warpRate);

            holdUntilRealTime = currentRealtime + holdSeconds;
            if (!double.IsNaN(continuationActivationUT) && continuationActivationUT > currentUT)
            {
                holdMaxRealTime = currentRealtime + WatchMode.MaxPendingHoldSeconds;
                holdUntilRealTime = Mathf.Min(holdUntilRealTime, holdMaxRealTime);
            }
            else
            {
                holdMaxRealTime = holdUntilRealTime;
            }
        }

        private static double MinPendingActivationUT(double currentBest, double candidate)
        {
            if (double.IsNaN(candidate))
                return currentBest;
            if (double.IsNaN(currentBest))
                return candidate;
            return Math.Min(currentBest, candidate);
        }

        #endregion

        #region Auto Loop Range

        /// <summary>
        /// Returns true if the given environment is visually uninteresting for looping purposes.
        /// ExoBallistic (orbital coasting) and SurfaceStationary (sitting on ground) are trimmed
        /// from the loop range because they contain no visible action.
        /// </summary>
        internal static bool IsBoringEnvironment(SegmentEnvironment env)
        {
            return env == SegmentEnvironment.ExoBallistic || env == SegmentEnvironment.SurfaceStationary;
        }

        /// <summary>
        /// Computes the automatic loop range for a recording by trimming leading and trailing
        /// "boring" TrackSections (ExoBallistic, SurfaceStationary). Returns (NaN, NaN) if no
        /// trimming is possible (recording has fewer than 2 sections, all sections are interesting,
        /// or all sections are boring).
        /// </summary>
        internal static (double startUT, double endUT) ComputeAutoLoopRange(List<TrackSection> sections)
        {
            if (sections == null || sections.Count < 2)
                return (double.NaN, double.NaN);

            // Find first non-boring section
            int first = -1;
            for (int i = 0; i < sections.Count; i++)
            {
                if (!IsBoringEnvironment(sections[i].environment))
                {
                    first = i;
                    break;
                }
            }

            if (first < 0)
                return (double.NaN, double.NaN); // all boring — loop the whole thing

            // Find last non-boring section
            int last = first;
            for (int i = sections.Count - 1; i >= first; i--)
            {
                if (!IsBoringEnvironment(sections[i].environment))
                {
                    last = i;
                    break;
                }
            }

            // If nothing was trimmed, no range narrowing needed
            if (first == 0 && last == sections.Count - 1)
                return (double.NaN, double.NaN);

            return (sections[first].startUT, sections[last].endUT);
        }

        #endregion
    }
}
