using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostPlaybackLogic
    {
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

        internal static bool ShouldSuppressGhostMeshAtWarp(
            float currentWarpRate, IPlaybackTrajectory traj, double playbackUT)
        {
            return ShouldSuppressGhosts(currentWarpRate)
                && !IsSurfaceStationaryAtPlaybackUT(traj, playbackUT);
        }

        internal static bool IsSurfaceStationaryAtPlaybackUT(
            IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj == null)
                return false;

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIdx >= 0)
                return traj.TrackSections[sectionIdx].environment == SegmentEnvironment.SurfaceStationary;

            // Surface-only trajectories have no moving payload to replay; keep their
            // already-static mesh visible even when no TrackSections are available.
            return traj.SurfacePos.HasValue
                && (traj.Points == null || traj.Points.Count == 0)
                && !traj.HasOrbitSegments;
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

        internal static bool ShouldAllowWarpZoneHideExemption(
            bool isWatchProtectedRecording, bool isOrbitTailPlayback)
        {
            return !(isWatchProtectedRecording && isOrbitTailPlayback);
        }

        internal static bool ShouldForceWatchProtectedFullFidelity(
            bool isWatchProtectedRecording, bool isOrbitTailPlayback)
        {
            return isWatchProtectedRecording && !isOrbitTailPlayback;
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
        /// Returns true when a Mission loop unit should SELF-OVERLAP: its launch-to-launch overlap
        /// cadence is shorter than the whole-mission span, so the mission relaunches before the
        /// prior instance finishes and several staggered instances play at once (a launch every
        /// period). False (the span is degenerate or the cadence is >= the span) means a single
        /// span-clock instance, one replay at a time. Used identically by the flight engine and the
        /// Space Center so a looped mission overlaps in both, exactly like a single recording with a
        /// period shorter than its duration.
        /// </summary>
        internal static bool UnitMemberOverlaps(LoopUnit unit)
        {
            double span = unit.SpanEndUT - unit.SpanStartUT;
            return span > 0 && unit.OverlapCadenceSeconds < span;
        }

        /// <summary>
        /// The schedule-start UT for ONE member inside a self-overlapping mission: the instant THIS
        /// member would launch in each mission instance. Each member overlaps on the shared launch
        /// cadence but staggered by its offset within the span (memberStart - spanStart) from the
        /// mission's phase anchor, so the whole mission relaunches as a unit every overlap cadence.
        /// </summary>
        internal static double ComputeMemberOverlapScheduleStartUT(
            double phaseAnchorUT, double spanStartUT, double memberStartUT)
        {
            return phaseAnchorUT + (memberStartUT - spanStartUT);
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

        internal static bool TryComputeNewestOverlapPlaybackUT(
            double currentUT,
            double intervalSeconds,
            double duration,
            double playbackStartUT,
            double scheduleStartUT,
            out double playbackUT,
            out long cycleIndex)
        {
            playbackUT = playbackStartUT;
            cycleIndex = 0;

            if (currentUT < scheduleStartUT)
                return false;

            double effectiveCadence = ComputeEffectiveLaunchCadence(
                intervalSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            double cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);

            long firstCycle;
            long lastCycle;
            GetActiveCycles(
                currentUT,
                scheduleStartUT,
                scheduleStartUT + duration,
                effectiveCadence,
                GhostPlayback.MaxOverlapGhostsPerRecording,
                out firstCycle,
                out lastCycle);

            cycleIndex = lastCycle;
            playbackUT = ComputeOverlapCyclePlaybackUT(
                currentUT,
                scheduleStartUT,
                playbackStartUT,
                duration,
                cycleDuration,
                lastCycle);
            return true;
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
    }
}
