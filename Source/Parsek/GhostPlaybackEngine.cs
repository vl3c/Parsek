using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Core ghost playback engine.
    /// Consumes <see cref="IPlaybackTrajectory"/> data plus rendering collaborators to manage
    /// ghost visuals, per-frame positioning, part-event playback, loop/overlap playback,
    /// zone transitions, and soft caps.
    ///
    /// Concrete recording/tree policy stays outside this class; trajectory knowledge crosses
    /// this boundary only through the interface surface.
    /// </summary>
    internal class GhostPlaybackEngine
    {
        private readonly IGhostPositioner positioner;

        #region Ghost state

        // Primary ghost state: one GhostPlaybackState per active timeline ghost, keyed by recording index.
        internal readonly Dictionary<int, GhostPlaybackState> ghostStates = new Dictionary<int, GhostPlaybackState>();

        // Overlap ghosts: older cycle ghosts still alive due to negative loop interval.
        // NOTE: Overlap playback paths use raw StartUT/EndUT, not EffectiveLoop bounds.
        // Loop range narrowing (LoopStartUT/LoopEndUT) only affects the primary loop path.
        internal readonly Dictionary<int, List<GhostPlaybackState>> overlapGhosts = new Dictionary<int, List<GhostPlaybackState>>();

        // Loop phase offsets: shifted loop phase for Watch mode targeting.
        internal readonly Dictionary<int, double> loopPhaseOffsets = new Dictionary<int, double>();

        // Anchor vessel tracking: which anchor vessels are loaded (for looped ghost lifecycle).
        internal readonly HashSet<uint> loadedAnchorVessels = new HashSet<uint>();

        // Diagnostic logging guards (log once per state transition, not per frame).
        internal readonly HashSet<int> loggedGhostEnter = new HashSet<int>();
        internal readonly HashSet<int> loggedReshow = new HashSet<int>();

        // Non-spamming observability for overlap-cadence adjustment. One log
        // line per (recordingIndex, userPeriod, effectiveCadence, duration)
        // tuple, re-emitted only when any input changes. Steady-state silent.
        private readonly Dictionary<int, (double userPeriod, double effectiveCadence, double duration)>
            lastLoggedCadence = new Dictionary<int, (double, double, double)>();
        private readonly Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>
            autoLoopLaunchSchedules = new Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>();
        private readonly List<AutoLoopQueueCandidate> autoLoopQueueScratch = new List<AutoLoopQueueCandidate>();

        // Constants live in ParsekConfig.cs — see GhostPlayback.* for the
        // concurrency caps, per-frame throttles, hold windows, and prewarm
        // buffers used below.
        private static readonly long MaxSpawnTimelineBuildTicksPerAdvance =
            (long)(Stopwatch.Frequency * (GhostPlayback.MaxSpawnBuildMillisecondsPerAdvance / 1000.0));
        private const double DefaultPendingOrbitBodyRadiusMeters = 600000.0;

        // Per-frame batch counters (avoid per-ghost log spam)
        private int frameSpawnCount;
        private int frameDestroyCount;
        // Bug #450 B3: per-frame counters for deferred-reentry-build throttling.
        // Reset each frame alongside the #414 counters.
        private int frameLazyReentryBuildCount;
        private int frameLazyReentryBuildDeferred;
        // Bug #414: per-frame counter of throttle-eligible spawns that were deferred because
        // the cap was already exhausted. Reset each frame alongside frameSpawnCount.
        private int frameSpawnDeferred;
        private int frameSkipBeforeActivation;
        private int frameSkipAnchorMissing;
        private int frameSkipLoopSyncFailed;
        private int frameSkipParentLoopPaused;
        private int frameSkipWarpHidden;
        private int frameSkipVisualLoadFailed;
        private int frameSkipNoRenderableData;
        private int frameSkipPlaybackDisabled;
        private int frameSkipExternalVesselSuppressed;
        private int frameSkipSessionSuppressed;
        // Bug #460: per-frame counter of overlap-ghost iterations. Incremented once per
        // iteration of the inner `for` loop in `UpdateExpireAndPositionOverlaps` (before any
        // continue / remove), so it reflects total overlap dispatch work regardless of whether
        // the iteration expired the ghost, repositioned it, or removed a null entry. Reset each
        // frame alongside the other frame counters so the mainLoop breakdown WARN (#460 latch)
        // can report `meanPerDispatch = mainLoop / (trajectoriesIterated + overlapIterations)`
        // alongside `meanPerTraj`, distinguishing "slow per-trajectory dispatch" from "many
        // overlap ghosts per trajectory" when the WARN fires on the next qualifying spike.
        private int frameOverlapGhostIterationCount;
        // Bug #414: largest single BuildGhostVisualsWithMetrics cost (in Stopwatch ticks) seen
        // this frame. Reset each frame; surfaced via PlaybackBudgetPhases.spawnMaxMicroseconds
        // so the breakdown WARN reveals whether one ghost alone is blowing the budget.
        private long frameMaxSpawnTicks;

        // Bug #450: per-sub-phase tick accumulators captured while processing the single
        // spawn that is the new heaviest of this frame. Latched inside the finally block of
        // BuildGhostVisualsWithMetrics when deltaTicks > frameMaxSpawnTicks. Reset each frame.
        // Surfaced via PlaybackBudgetPhases.heaviestSpawn* so the one-shot breakdown WARN can
        // attribute a single heavy spawn's cost across snapshot / timeline / dicts / reentry.
        private long frameHeaviestSpawnSnapshotResolveTicks;
        private long frameHeaviestSpawnTimelineTicks;
        private long frameHeaviestSpawnDictionariesTicks;
        private long frameHeaviestSpawnReentryTicks;
        private long frameHeaviestSpawnOtherTicks;
        private HeaviestSpawnBuildType frameHeaviestSpawnBuildType;

        // Bug #450: "last spawn" per-sub-phase tick deltas written by TryPopulateGhostVisuals
        // and read by BuildGhostVisualsWithMetrics.finally when deciding whether this call is
        // the new frame heaviest. Reset at the start of TryPopulateGhostVisuals to guarantee
        // stale values from a prior call cannot leak into a later spawn's attribution.
        private long lastSpawnSnapshotResolveTicks;
        private long lastSpawnTimelineTicks;
        private long lastSpawnDictionariesTicks;
        private long lastSpawnReentryTicks;

        // Diagnostics: reusable Stopwatches (allocated once, no per-frame GC)
        private readonly Stopwatch updateStopwatch = new Stopwatch();
        private readonly Stopwatch spawnStopwatch = new Stopwatch();
        private readonly Stopwatch destroyStopwatch = new Stopwatch();
        // Bug #414: per-phase stopwatches used only to populate the one-shot PlaybackBudgetPhases
        // breakdown that DiagnosticsComputation logs the first time a budget-exceeded frame fires.
        // Allocated once, Reset()+Start()/Stop() each frame — no per-frame GC.
        private readonly Stopwatch deferredCreatedStopwatch = new Stopwatch();
        private readonly Stopwatch deferredCompletedStopwatch = new Stopwatch();
        private readonly Stopwatch observabilityStopwatch = new Stopwatch();
        // Bug #450: per-sub-phase spawn stopwatches. Accumulate across every spawn in the
        // frame; ElapsedTicks at populate time gives the aggregate surfaced via
        // PlaybackBudgetPhases.build*Microseconds. Per-call deltas (captured via pre-call
        // ElapsedTicks + post-call subtraction, identical to the #414 spawnStopwatch pattern)
        // feed the heaviest-spawn latch. Zero per-frame GC.
        private readonly Stopwatch buildSnapshotResolveStopwatch = new Stopwatch();
        private readonly Stopwatch buildTimelineStopwatch = new Stopwatch();
        private readonly Stopwatch buildDictionariesStopwatch = new Stopwatch();
        private readonly Stopwatch buildReentryFxStopwatch = new Stopwatch();

        // Deferred event lists (reused per frame to avoid GC allocation)
        private readonly List<PlaybackCompletedEvent> deferredCompletedEvents = new List<PlaybackCompletedEvent>();
        private readonly List<GhostLifecycleEvent> deferredCreatedEvents = new List<GhostLifecycleEvent>();

        // Dedup: prevent completed events from firing every frame for past-end recordings.
        // Rewind safety: DestroyAllGhosts() clears this set, and rewind always calls
        // DestroyAllGhosts (via ParsekFlight cleanup path), so completedEventFired
        // is guaranteed to be empty when playback restarts after a rewind.
        private readonly HashSet<int> completedEventFired = new HashSet<int>();
        // Destroyed debris can complete before EndUT when the recording captured a clear
        // destructive part event. Keep those indices suppressed until a rewind/reset so
        // they do not respawn while the original recording window is still in range.
        private readonly HashSet<int> earlyDestroyedDebrisCompleted = new HashSet<int>();

        private enum GhostVisualLoadStatus : byte
        {
            Failed = 0,
            Pending = 1,
            Ready = 2,
            CompletedThisCall = 3
        }

        #endregion

        #region Lifecycle events

        internal event Action<GhostLifecycleEvent> OnGhostCreated;
        internal event Action<GhostLifecycleEvent> OnGhostDestroyed;
        internal event Action<PlaybackCompletedEvent> OnPlaybackCompleted;
        internal event Action<LoopRestartedEvent> OnLoopRestarted;
        internal event Action<OverlapExpiredEvent> OnOverlapExpired;
        internal event Action OnAllGhostsDestroying;

        /// <summary>
        /// Delegate set by the policy to check if a ghost index is being held
        /// (pending spawn, watched, etc.). Used by stale past-end cleanup to avoid
        /// destroying ghosts that the policy is intentionally keeping alive.
        /// </summary>
        internal System.Func<int, bool> IsGhostHeld;

        /// <summary>
        /// Optional host-supplied exact watched-state predicate. Needed so overlap cycles
        /// don't inherit watched-only exemptions from the recording index alone.
        /// </summary>
        internal System.Func<int, GhostPlaybackState, bool> IsWatchedGhostStateResolver;

        /// <summary>
        /// Optional host-supplied render-distance resolver. Lets the engine classify hidden
        /// or inactive ghosts against their current playback position rather than a stale transform.
        /// </summary>
        internal System.Func<int, IPlaybackTrajectory, GhostPlaybackState, double, double> ResolvePlaybackDistanceOverride;

        /// <summary>
        /// Optional host-supplied active-vessel-distance resolver. Watch cutoff, watch UI,
        /// and other safety policies stay anchored to the active vessel even when render
        /// LOD follows the live scene camera.
        /// </summary>
        internal System.Func<int, IPlaybackTrajectory, GhostPlaybackState, double, double> ResolvePlaybackActiveVesselDistanceOverride;

        // Camera events (engine detects cycle changes, host handles FlightCamera).
        internal event Action<CameraActionEvent> OnLoopCameraAction;
        internal event Action<CameraActionEvent> OnOverlapCameraAction;

        #endregion

        internal static Func<string, double?> PendingOrbitBodyRadiusResolverForTesting;

        internal static bool HasRenderableGhostData(IPlaybackTrajectory traj)
        {
            return traj != null
                && ((traj.Points != null && traj.Points.Count > 0)
                    || traj.HasOrbitSegments
                    || traj.SurfacePos.HasValue);
        }

        internal GhostPlaybackEngine(IGhostPositioner positioner)
        {
            this.positioner = positioner;
            ParsekLog.Info("Engine", "GhostPlaybackEngine created");
        }

        private readonly struct AutoLoopQueueCandidate
        {
            internal AutoLoopQueueCandidate(
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

        internal static bool HasLoadedGhostVisuals(GhostPlaybackState state)
        {
            return state != null && state.ghost != null;
        }

        internal static bool HasLoopCycleChanged(GhostPlaybackState state, long cycleIndex)
        {
            return state == null || state.loopCycleIndex != cycleIndex;
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
                || counters.sessionSuppressed > 0;
        }

        internal static string BuildFrameSummaryMessage(GhostPlaybackFrameCounters counters)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Frame: spawned={0} destroyed={1} deferred={2} " +
                "skips[beforeActivation={3} anchorMissing={4} loopSyncFailed={5} " +
                "parentLoopPaused={6} warpHidden={7} visualLoadFailed={8} " +
                "noRenderableData={9} playbackDisabled={10} externalVesselSuppressed={11} " +
                "sessionSuppressed={12}] active={13}",
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

        internal static bool ShouldPrewarmHiddenGhostForPartEvent(PartEventType type)
        {
            switch (type)
            {
                case PartEventType.Decoupled:
                case PartEventType.Destroyed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteDestroyed:
                case PartEventType.ShroudJettisoned:
                case PartEventType.DeployableExtended:
                case PartEventType.DeployableRetracted:
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                case PartEventType.FairingJettisoned:
                case PartEventType.Docked:
                case PartEventType.Undocked:
                case PartEventType.InventoryPartPlaced:
                case PartEventType.InventoryPartRemoved:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool ShouldPrewarmHiddenGhost(
            IPlaybackTrajectory traj, GhostPlaybackState state, double ghostDistance, double currentUT)
        {
            if (traj == null || state == null)
                return false;

            if (ghostDistance <= DistanceThresholds.GhostFlight.LoopSimplifiedMeters
                + GhostPlayback.HiddenGhostVisibleTierPrewarmBufferMeters)
            {
                return true;
            }

            if (traj.PartEvents == null || traj.PartEvents.Count == 0)
                return false;

            double lookaheadDeadline = currentUT + GhostPlayback.HiddenGhostEventPrewarmLookaheadSeconds;
            int startIndex = Math.Max(0, state.partEventIndex);
            for (int i = startIndex; i < traj.PartEvents.Count; i++)
            {
                var evt = traj.PartEvents[i];
                if (evt.ut > lookaheadDeadline)
                    break;
                if (evt.ut < currentUT)
                    continue;
                if (ShouldPrewarmHiddenGhostForPartEvent(evt.eventType))
                    return true;
            }

            return false;
        }

        #region Per-frame update

        /// <summary>
        /// Main per-frame update. Iterates all active trajectories, spawns/positions/destroys
        /// ghosts, fires lifecycle events. Called from host's Update().
        /// </summary>
        internal void UpdatePlayback(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            TrajectoryPlaybackFlags[] flags,
            FrameContext ctx)
        {
            if (trajectories == null || trajectories.Count == 0)
            {
                DiagnosticsState.playbackBudget = default;
                return;
            }
            if (positioner == null)
            {
                DiagnosticsState.playbackBudget = default;
                return; // Not yet wired (Phase 7)
            }
            if (flags == null || flags.Length < trajectories.Count)
            {
                DiagnosticsState.playbackBudget = default;
                ParsekLog.Warn("Engine", $"UpdatePlayback: flags array mismatch (flags={flags?.Length ?? 0} trajectories={trajectories.Count})");
                return;
            }

            // In map view, keep ghosts positioned so map markers draw at the correct
            // location even during high warp (#290). The mesh is invisible at orbital
            // distances anyway — only the icon+text matters.
            bool suppressGhosts = !ctx.mapViewEnabled
                && GhostPlaybackLogic.ShouldSuppressGhosts(ctx.warpRate);
            bool suppressVisualFx = GhostPlaybackLogic.ShouldSuppressVisualFx(ctx.warpRate);
            RebuildAutoLoopLaunchScheduleCache(trajectories, ctx.autoLoopIntervalSeconds);

            ResetPerFramePlaybackCounters(suppressGhosts);
            long spawnMicroseconds = 0;
            int ghostsProcessed = 0;
            int trajectoriesIterated = 0;

            for (int i = 0; i < trajectories.Count; i++)
            {
                trajectoriesIterated++;
                var traj = trajectories[i];
                var f = flags[i];

                // Disabled/suppressed: destroy any active ghost before skipping.
                // Bug #433: only the PlaybackEnabled=false cause is career-neutral, so
                // only that cause must still fire PlaybackCompleted at past-end — the
                // policy reads the event to spawn the persistent vessel, which must
                // happen regardless of the visual toggle. !hasData and
                // externalVesselSuppressed are structural (no valid trajectory / no
                // spawn path) and keep their silent-skip behaviour.
                if (f.skipGhost)
                {
                    CountFrameSkip(f.skipReason);
                    if (ghostStates.ContainsKey(i))
                    {
                        DestroyAllOverlapGhosts(i);
                        DestroyGhost(i, traj, f, reason: f.skipReason.ToLogToken());
                    }

                    if (GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                            traj, f, ctx.currentUT,
                            completedEventFired.Contains(i),
                            earlyDestroyedDebrisCompleted.Contains(i)))
                    {
                        bool skipHasPointData = traj.Points != null && traj.Points.Count > 0;
                        ParsekLog.Verbose("Engine",
                            $"Disabled past-end completion: #{i} \"{traj.VesselName}\" " +
                            $"UT={ctx.currentUT:F2} endUT={traj.EndUT:F2} ghostActive=false");
                        HandlePastEndGhost(i, traj, f, ctx, state: null, ghostActive: false, skipHasPointData);
                    }
                    continue;
                }

                bool hasPointData = traj.Points != null && traj.Points.Count > 0;
                bool hasInterpolatedPoints = traj.Points != null && traj.Points.Count >= 2;
                bool hasOrbitData = traj.HasOrbitSegments;
                bool hasSurfaceData = traj.SurfacePos.HasValue;
                if (!HasRenderableGhostData(traj))
                {
                    CountFrameSkip(GhostPlaybackSkipReason.NoRenderableData);
                    continue;
                }

                // Phase 7 of Rewind-to-Staging (design §3.3): during an active
                // re-fly session, skip ghosts whose source recording is in the
                // SessionSuppressedSubtree. Destroy any leftover ghost visuals
                // first so the player doesn't see stale meshes from a recording
                // that just got superseded mid-flight.
                if (SessionSuppressionState.IsActive
                    && SessionSuppressionState.IsSuppressedRecordingIndex(i))
                {
                    if (ghostStates.ContainsKey(i))
                    {
                        DestroyAllOverlapGhosts(i);
                        DestroyGhost(i, traj, f, reason: "session-suppressed subtree");
                    }
                    CountFrameSkip(GhostPlaybackSkipReason.SessionSuppressed);
                    continue;
                }

                GhostPlaybackState state;
                ghostStates.TryGetValue(i, out state);
                bool ghostActive = HasLoadedGhostVisuals(state);
                if (ghostActive) ghostsProcessed++;

                double activationStartUT = ResolveGhostActivationStartUT(traj);

                if (ctx.currentUT < activationStartUT)
                {
                    completedEventFired.Remove(i);
                    earlyDestroyedDebrisCompleted.Remove(i);
                    if (ghostActive)
                    {
                        DestroyAllOverlapGhosts(i);
                        DestroyGhost(i, traj, f, reason: "before activation start UT");
                    }
                    CountFrameSkip(GhostPlaybackSkipReason.BeforeActivation);
                    continue;
                }

                bool inRange = ctx.currentUT <= traj.EndUT;
                bool pastEnd = ctx.currentUT > traj.EndUT;
                bool pastEffectiveEnd = ctx.currentUT > f.chainEndUT;

                // === Loop dispatch (before main rendering) ===
                if (ShouldLoopPlayback(traj))
                {
                    // Anchor gating: if anchor configured but not loaded, skip ghost
                    if (traj.LoopAnchorVesselId != 0 && !loadedAnchorVessels.Contains(traj.LoopAnchorVesselId))
                    {
                        if (ghostActive)
                        {
                            DestroyGhost(i, traj, f, reason: $"anchor {traj.LoopAnchorVesselId} unloaded");
                            DestroyAllOverlapGhosts(i);
                        }
                        CountFrameSkip(GhostPlaybackSkipReason.AnchorMissing);
                        continue;
                    }

                    UpdateLoopingPlayback(i, traj, f, ctx, suppressGhosts, suppressVisualFx);

                    // Clean up leftover overlap ghosts from loop->non-loop transition
                    List<GhostPlaybackState> leftoverOverlaps;
                    if (overlapGhosts.TryGetValue(i, out leftoverOverlaps) && leftoverOverlaps.Count > 0)
                    {
                        // Overlap cleanup handled in UpdateLoopingPlayback/UpdateOverlapPlayback
                    }

                    continue;
                }

                // Clean up overlap ghosts if recording switched from looping to non-looping
                if (overlapGhosts.ContainsKey(i))
                {
                    DestroyAllOverlapGhosts(i);
                    overlapGhosts.Remove(i);
                }

                // === Loop-synced debris: use parent's loop clock ===
                if (traj.LoopSyncParentIdx >= 0 && traj.LoopSyncParentIdx < trajectories.Count)
                {
                    var parent = trajectories[traj.LoopSyncParentIdx];
                    if (ShouldLoopPlayback(parent))
                    {
                        double parentLoopUT;
                        long parentCycle;
                        bool parentPaused;
                        if (!TryComputeLoopPlaybackUT(parent, ctx.currentUT, ctx.autoLoopIntervalSeconds,
                                out parentLoopUT, out parentCycle, out parentPaused, traj.LoopSyncParentIdx))
                        {
                            if (ghostActive)
                                DestroyGhost(i, traj, f, reason: "parent loop sync failed");
                            CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                            continue;
                        }

                        bool suppressLoopSyncGhost = suppressGhosts
                            && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                                ctx.warpRate, traj, parentLoopUT);
                        if (parentPaused || suppressLoopSyncGhost)
                        {
                            if (state != null)
                                DestroyGhost(i, traj, f, reason: "parent loop paused/warp");
                            CountFrameSkip(parentPaused
                                ? GhostPlaybackSkipReason.ParentLoopPaused
                                : GhostPlaybackSkipReason.WarpHidden);
                            continue;
                        }

                        // Cycle change: rebuild ghost for clean visual state
                        if (state != null && state.loopCycleIndex != parentCycle)
                        {
                            DestroyGhost(i, traj, f, reason: "parent loop cycle change");
                            ghostActive = false;
                            state = null;
                            // Clear completed-event flag so the debris can play again
                            completedEventFired.Remove(i);
                        }

                        bool debrisInRange = parentLoopUT >= activationStartUT && parentLoopUT <= traj.EndUT;
                        if (debrisInRange)
                        {
                            // Override UT for positioning — use parent's loop clock
                            var syncCtx = ctx;
                            syncCtx.currentUT = parentLoopUT;
                            if (RenderInRangeGhost(i, traj, f, syncCtx, suppressVisualFx,
                                    hasPointData, hasInterpolatedPoints, hasSurfaceData, hasOrbitData,
                                    allowEarlyDestroyedDebrisCompletion: false,
                                    ref state, ref ghostActive))
                            {
                                if (state != null)
                                    state.loopCycleIndex = parentCycle;
                            }
                        }
                        else if (state != null)
                        {
                            DestroyGhost(i, traj, f, reason: "outside debris UT range in parent loop");
                        }
                        continue;
                    }
                }

                // === Warp suppression: hide moving ghosts during high warp ===
                if (suppressGhosts && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                        ctx.warpRate, traj, ctx.currentUT))
                {
                    if (ghostActive && state.ghost.activeSelf)
                    {
                        state.ghost.SetActive(false);
                        ResetGhostAppearanceTracking(state);
                    }
                    DestroyAllOverlapGhosts(i);
                    CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                    continue;
                }

                // === In-range rendering ===
                if (inRange)
                {
                    if (RenderInRangeGhost(i, traj, f, ctx, suppressVisualFx,
                            hasPointData, hasInterpolatedPoints, hasSurfaceData, hasOrbitData,
                            allowEarlyDestroyedDebrisCompletion: true,
                            ref state, ref ghostActive))
                        continue;
                }

                // === Past end: fire completed event, optionally destroy ===
                if ((pastEnd || pastEffectiveEnd)
                    && !completedEventFired.Contains(i)
                    && !earlyDestroyedDebrisCompleted.Contains(i))
                    HandlePastEndGhost(i, traj, f, ctx, state, ghostActive, hasPointData);

                // === Stale past-end ghost cleanup ===
                // Ghost survived past-end (e.g. watch hold), completed event already fired,
                // and not being held by the policy — destroy it. Prevents debris ghosts
                // from freezing at their last trajectory point indefinitely.
                if (state != null && completedEventFired.Contains(i)
                    && (IsGhostHeld == null || !IsGhostHeld(i)))
                {
                    DestroyGhost(i, traj, f, reason: "stale past-end ghost (no longer held)");
                }
            }

            // Post-loop: batch summary
            GhostPlaybackFrameCounters frameCounters = BuildCurrentFrameCounters();
            if (ShouldEmitFrameSummary(frameCounters))
                ParsekLog.VerboseRateLimited("Engine", "frame-summary",
                    BuildFrameSummaryMessage(frameCounters));

            // Bug #414: capture elapsed time at loop end so the "main loop" phase (pure
            // dispatch cost, excluding spawn/destroy which already accumulate into their
            // own stopwatches inside SpawnGhost/DestroyGhost) can be computed below.
            long elapsedTicksAtLoopEnd = updateStopwatch.ElapsedTicks;

            // Fire deferred events AFTER loop completes
            int createdEventsFired = deferredCreatedEvents.Count;
            deferredCreatedStopwatch.Start();
            for (int i = 0; i < deferredCreatedEvents.Count; i++)
                OnGhostCreated?.Invoke(deferredCreatedEvents[i]);
            deferredCreatedStopwatch.Stop();

            int completedEventsFired = deferredCompletedEvents.Count;
            deferredCompletedStopwatch.Start();
            for (int i = 0; i < deferredCompletedEvents.Count; i++)
                OnPlaybackCompleted?.Invoke(deferredCompletedEvents[i]);
            deferredCompletedStopwatch.Stop();

            // Observability capture is measured as a phase and is now inside the updateStopwatch
            // window — totalMicroseconds includes it, so the #414 breakdown's phase sum matches
            // the budget total (pre-#414 it sat outside the window and was silently untracked).
            observabilityStopwatch.Start();
            GhostObservability ghostObservability = CaptureGhostObservability();
            observabilityStopwatch.Stop();

            updateStopwatch.Stop();
            long totalMicroseconds = updateStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            spawnMicroseconds = spawnStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            long destroyMicroseconds = destroyStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;

            DiagnosticsState.playbackBudget.totalMicroseconds = totalMicroseconds;
            DiagnosticsState.playbackBudget.spawnMicroseconds = spawnMicroseconds;
            DiagnosticsState.playbackBudget.destroyMicroseconds = destroyMicroseconds;
            DiagnosticsState.playbackBudget.ghostsProcessed = ghostsProcessed;
            DiagnosticsState.playbackBudget.warpRate = ctx.warpRate;
            DiagnosticsState.playbackBudget.ghostObservability = ghostObservability;

            DiagnosticsState.playbackFrameHistory.Append(
                Time.realtimeSinceStartup, totalMicroseconds);

            // Bug #414: build the one-shot per-phase breakdown struct. Cheap — a handful of
            // tick-to-microsecond divisions even on healthy frames. DiagnosticsComputation
            // itself only logs when the budget is actually exceeded AND only once per session,
            // so steady-state cost of the breakdown path is exactly the struct population
            // below; nothing is written unless the warn fires.
            long elapsedMicrosecondsAtLoopEnd = elapsedTicksAtLoopEnd * 1000000L / Stopwatch.Frequency;
            long mainLoopMicroseconds = elapsedMicrosecondsAtLoopEnd - spawnMicroseconds - destroyMicroseconds;
            if (mainLoopMicroseconds < 0) mainLoopMicroseconds = 0;
            // Bug #450: per-sub-phase aggregate = sum across all spawns in the frame.
            long buildSnapshotResolveMicroseconds = buildSnapshotResolveStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            long buildTimelineFromSnapshotMicroseconds = buildTimelineStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            long buildDictionariesMicroseconds = buildDictionariesStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            long buildReentryFxMicroseconds = buildReentryFxStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
            // Residual = outer spawn time − attributed sub-phases (covers camera pivot,
            // materials, MaterialPropertyBlock, SetActive, appearance reset). Floors at 0 so
            // sub-microsecond rounding between the four sub-phase ticks and spawnMicroseconds
            // cannot produce a negative.
            long buildOtherMicroseconds = spawnMicroseconds
                - buildSnapshotResolveMicroseconds
                - buildTimelineFromSnapshotMicroseconds
                - buildDictionariesMicroseconds
                - buildReentryFxMicroseconds;
            if (buildOtherMicroseconds < 0) buildOtherMicroseconds = 0;

            var phases = new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = mainLoopMicroseconds,
                spawnMicroseconds = spawnMicroseconds,
                destroyMicroseconds = destroyMicroseconds,
                explosionCleanupMicroseconds = 0, // FXMonger owns explosion-side cleanup; nothing engine-local to measure
                deferredCreatedEventsMicroseconds = deferredCreatedStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency,
                deferredCompletedEventsMicroseconds = deferredCompletedStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency,
                observabilityCaptureMicroseconds = observabilityStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency,
                trajectoriesIterated = trajectoriesIterated,
                // Bug #460: total overlap-ghost iterations this frame so the mainLoop
                // breakdown WARN can compute `meanPerDispatch` alongside `meanPerTraj`.
                overlapGhostIterationCount = frameOverlapGhostIterationCount,
                createdEventsFired = createdEventsFired,
                completedEventsFired = completedEventsFired,
                spawnsAttempted = frameSpawnCount,
                spawnsThrottled = frameSpawnDeferred,
                spawnMaxMicroseconds = frameMaxSpawnTicks * 1000000L / Stopwatch.Frequency,
                // Bug #450: per-sub-phase aggregate across every spawn this frame.
                buildSnapshotResolveMicroseconds = buildSnapshotResolveMicroseconds,
                buildTimelineFromSnapshotMicroseconds = buildTimelineFromSnapshotMicroseconds,
                buildDictionariesMicroseconds = buildDictionariesMicroseconds,
                buildReentryFxMicroseconds = buildReentryFxMicroseconds,
                buildOtherMicroseconds = buildOtherMicroseconds,
                // Bug #450: heaviest-spawn breakdown (latched on whichever single
                // BuildGhostVisualsWithMetrics call produced the largest delta).
                heaviestSpawnSnapshotResolveMicroseconds = frameHeaviestSpawnSnapshotResolveTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnTimelineFromSnapshotMicroseconds = frameHeaviestSpawnTimelineTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnDictionariesMicroseconds = frameHeaviestSpawnDictionariesTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnReentryFxMicroseconds = frameHeaviestSpawnReentryTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnOtherMicroseconds = frameHeaviestSpawnOtherTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnBuildType = frameHeaviestSpawnBuildType,
            };

            // Budget threshold warning (8ms = half of 16.6ms frame budget at 60fps).
            // WithBreakdown variant: also emits a one-shot WARN with phase breakdown the
            // first time a spike is seen, to localize the responsible sub-phase (bug #414).
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                totalMicroseconds, ghostsProcessed, ctx.warpRate, phases);
        }

        private void ResetPerFramePlaybackCounters(bool suppressGhosts)
        {
            // Reset reshow dedup when entering warp suppression
            if (suppressGhosts)
                loggedReshow.Clear();

            deferredCompletedEvents.Clear();
            deferredCreatedEvents.Clear();
            frameSpawnCount = 0;
            frameDestroyCount = 0;
            frameSpawnDeferred = 0;
            frameSkipBeforeActivation = 0;
            frameSkipAnchorMissing = 0;
            frameSkipLoopSyncFailed = 0;
            frameSkipParentLoopPaused = 0;
            frameSkipWarpHidden = 0;
            frameSkipVisualLoadFailed = 0;
            frameSkipNoRenderableData = 0;
            frameSkipPlaybackDisabled = 0;
            frameSkipExternalVesselSuppressed = 0;
            frameSkipSessionSuppressed = 0;
            frameMaxSpawnTicks = 0;
            // Bug #460: reset overlap-iteration counter so the mainLoop breakdown's
            // `meanPerDispatch` denominator reflects only this frame's overlap dispatch work.
            frameOverlapGhostIterationCount = 0;
            // Bug #450 B3: reset lazy-reentry-build per-frame counters each tick so the
            // cap applies within a single UpdatePlayback call.
            frameLazyReentryBuildCount = 0;
            frameLazyReentryBuildDeferred = 0;
            // Bug #450: reset per-frame heaviest-spawn breakdown fields so the latch starts
            // empty each frame. No heaviest spawn yet -> HeaviestSpawnBuildType.None. The
            // lastSpawn*Ticks fields are NOT reset here because TryPopulateGhostVisuals
            // resets them itself at its head every call — any read of frameHeaviestSpawn*
            // happens only inside the PlaybackBudgetPhases populate block below, strictly
            // after all BuildGhostVisualsWithMetrics calls this frame have run. Scene-
            // cleanup paths (DestroyAllGhosts, ReindexAfterDelete) do not need to clear
            // these fields either — they run outside UpdatePlayback, and the next frame's
            // reset at the head of UpdatePlayback zeroes everything before any producer
            // touches it. Same invariant #414's frameMaxSpawnTicks already relies on.
            frameHeaviestSpawnSnapshotResolveTicks = 0;
            frameHeaviestSpawnTimelineTicks = 0;
            frameHeaviestSpawnDictionariesTicks = 0;
            frameHeaviestSpawnReentryTicks = 0;
            frameHeaviestSpawnOtherTicks = 0;
            frameHeaviestSpawnBuildType = HeaviestSpawnBuildType.None;

            // Diagnostics: start total frame timing
            updateStopwatch.Restart();
            // Reset spawn timer to zero — Start()/Stop() pairs inside SpawnGhost
            // accumulate across multiple spawns per frame (Start resumes, not resets)
            spawnStopwatch.Reset();
            destroyStopwatch.Reset();
            // Bug #414: reset per-phase stopwatches used for the one-shot breakdown.
            // Cheap (Reset on a stopped Stopwatch is a single field write).
            deferredCreatedStopwatch.Reset();
            deferredCompletedStopwatch.Reset();
            observabilityStopwatch.Reset();
            // Bug #450: reset per-sub-phase spawn stopwatches (same pattern as #414).
            buildSnapshotResolveStopwatch.Reset();
            buildTimelineStopwatch.Reset();
            buildDictionariesStopwatch.Reset();
            buildReentryFxStopwatch.Reset();
        }

        /// <summary>
        /// Handles in-range ghost rendering: spawn if needed, position, apply visual events.
        /// Returns true if the ghost was processed (caller should continue to next iteration).
        /// </summary>
        private bool RenderInRangeGhost(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f,
            FrameContext ctx, bool suppressVisualFx,
            bool hasPointData, bool hasInterpolatedPoints, bool hasSurfaceData, bool hasOrbitData,
            bool allowEarlyDestroyedDebrisCompletion,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            if (allowEarlyDestroyedDebrisCompletion && earlyDestroyedDebrisCompleted.Contains(i))
                return true;

            // Flag events spawn permanent world vessels — apply regardless of ghost state,
            // zone distance, or spawn-throttle (#249, #414). Unlike visual part events (mesh
            // toggles), flags are independent entities that must exist whether or not the
            // ghost is visible AND whether or not the ghost's visual build has happened yet.
            // When `state` is null (first-spawn throttled or not yet run this frame),
            // ApplyFlagEvents falls back to a state-less walk with FlagExistsAtPosition dedup.
            GhostPlaybackLogic.ApplyFlagEvents(state, traj, ctx.currentUT);

            if (state == null)
            {
                // Bug #414: throttle first-ever spawns so scene-load warm-up bursts don't
                // land every eligible ghost's visual build on a single frame.
                if (!TryReserveSpawnSlot(i, "first-spawn"))
                    return false;

                state = CreatePendingSpawnState(
                    traj, ctx.currentUT, PendingSpawnLifecycle.StandardEnter, f);
                ghostStates[i] = state;
                GhostVisualLoadStatus firstSpawnStatus = EnsureGhostVisualsLoaded(
                    i, traj, state, ctx.currentUT, "first spawn",
                    resetCompletedEventDedup: true);
                if (firstSpawnStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    ghostStates.Remove(i);
                    ghostActive = false;
                    return false;
                }
                if (firstSpawnStatus == GhostVisualLoadStatus.Pending)
                {
                    ghostActive = false;
                    return true;
                }
            }

            if (state == null) return false;

            // Zone-based rendering
            double renderDistance = ResolvePlaybackDistance(i, traj, state, ctx.currentUT, ctx.activeVesselPos);
            double activeVesselDistance = ResolvePlaybackActiveVesselDistance(
                i, traj, state, ctx.currentUT, ctx.activeVesselPos);
            CachePlaybackDistances(state, activeVesselDistance, renderDistance);
            var zoneResult = positioner.ApplyZoneRendering(i, state, traj, renderDistance, ctx.protectedIndex);

            if (zoneResult.hiddenByZone)
            {
                ghostActive = HandleHiddenGhostVisualState(
                    i, traj, state, ctx.currentUT, ctx.warpRate, renderDistance, overlapGhost: false,
                    hiddenReason: $"hidden by distance LOD at {renderDistance:F0}m");
                return true;
            }

            if (!HasLoadedGhostVisuals(state))
            {
                // Bug #414: throttle distance-tier rehydration — a 1-frame delay is invisible
                // when a ghost comes back into LOD range.
                string reloadSite = state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                    ? "first-spawn-continue"
                    : "distance-tier-rehydrate";
                if (!TryReserveSpawnSlot(i, reloadSite))
                {
                    ghostActive = false;
                    return false;
                }
                GhostVisualLoadStatus loadStatus = EnsureGhostVisualsLoaded(
                    i, traj, state, ctx.currentUT,
                    state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                        ? "continuing first spawn"
                        : "entered visible distance tier");
                if (loadStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    ghostActive = false;
                    return false;
                }
                if (loadStatus == GhostVisualLoadStatus.Pending)
                {
                    ghostActive = false;
                    return true;
                }
            }

            ghostActive = HasLoadedGhostVisuals(state);
            if (!ghostActive) return false;

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, zoneResult.reduceFidelity);

            double visiblePlaybackUT = ResolveVisiblePlaybackUT(traj, state, ctx.currentUT);

            // Bug #613 (PR #594 P1): clear the per-frame retire signal before
            // positioning so a stale value from a previous frame's relative
            // section can't leak into the current frame's pipeline. The
            // relative positioner sets it back to true if the recorded anchor
            // is unresolvable.
            state.anchorRetiredThisFrame = false;

            // Position the ghost
            if (hasInterpolatedPoints)
            {
                // Check for relative frame positioning (Phase 3b: docking/rendezvous)
                bool usedRelative = false;
                if (traj.TrackSections != null && traj.TrackSections.Count > 0)
                {
                    int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, visiblePlaybackUT);
                    if (sectionIdx >= 0 && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
                    {
                        positioner.InterpolateAndPositionRelative(i, traj, state,
                            visiblePlaybackUT, suppressVisualFx,
                            traj.TrackSections[sectionIdx].anchorVesselId);
                        usedRelative = true;
                    }
                }
                if (!usedRelative)
                {
                    positioner.InterpolateAndPosition(i, traj, state, visiblePlaybackUT, suppressVisualFx);
                }
            }
            else if (hasPointData)
            {
                if (ShouldPrimeSinglePointGhostFromOrbit(traj, visiblePlaybackUT))
                    positioner.PositionFromOrbit(i, traj, state, visiblePlaybackUT);
                else
                    positioner.PositionAtPoint(i, traj, state, traj.Points[0]);
            }
            else if (hasSurfaceData)
            {
                positioner.PositionAtSurface(i, traj, state);
            }
            else if (hasOrbitData)
            {
                positioner.PositionFromOrbit(i, traj, state, visiblePlaybackUT);
            }

            // Bug #613 (PR #594 P1): when the relative positioner retired the
            // ghost (anchor unresolvable post-rewind), call ApplyFrameVisuals
            // with skipPartEvents=true and suppressVisualFx=true so any
            // previously-emitting plumes/audio get cleanly stopped, but no
            // new transient events fire from the stale (0,0,0) transform.
            // Skip ActivateGhostVisualsIfNeeded -- otherwise it would
            // unconditionally re-show the ghost the same frame we hid it.
            // Skip TrackGhostAppearance -- logging a root=(0,0,0) appearance
            // for a retired ghost was the original misleading symptom.
            bool retired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame);
            if (retired)
            {
                ApplyFrameVisuals(i, traj, state, visiblePlaybackUT, ctx.warpRate,
                    skipPartEvents: true, suppressVisualFx: true);
            }
            else
            {
                ApplyFrameVisuals(i, traj, state, visiblePlaybackUT, ctx.warpRate,
                    zoneResult.skipPartEvents, suppressVisualFx || zoneResult.suppressVisualFx);
                bool activatedDeferredState = ActivateGhostVisualsIfNeeded(state);
                if (ShouldRestoreDeferredRuntimeFxState(
                        activatedDeferredState,
                        suppressVisualFx || zoneResult.suppressVisualFx))
                    GhostPlaybackLogic.RestoreDeferredRuntimeFxState(state);
                TrackGhostAppearance(index: i, traj: traj, state: state, playbackUT: visiblePlaybackUT,
                    reason: "playback", requestedPlaybackUT: ctx.currentUT);
            }

            // Run early-destroyed-debris completion for its side effects (event
            // emission, explosion FX). The helper's return value doesn't gate
            // the outer result: the ghost has already been rendered above this
            // line, so RenderInRangeGhost must report true regardless of
            // whether the completion fired or was skipped. #369 cosmetic.
            if (allowEarlyDestroyedDebrisCompletion)
                TryHandleEarlyDestroyedDebrisCompletion(i, traj, f, ctx, state, ghostActive, hasPointData);

            return true;
        }

        private bool TryHandleEarlyDestroyedDebrisCompletion(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            FrameContext ctx, GhostPlaybackState state, bool ghostActive, bool hasPointData)
        {
            if (completedEventFired.Contains(index))
                return true;

            if (!GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(traj, out double explosionUT))
                return false;

            if (ctx.currentUT + 1e-6 < explosionUT)
                return false;

            completedEventFired.Add(index);
            earlyDestroyedDebrisCompleted.Add(index);

            if (ghostActive)
                TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

            deferredCompletedEvents.Add(new PlaybackCompletedEvent
            {
                Index = index,
                Trajectory = traj,
                State = state,
                Flags = flags,
                GhostWasActive = ghostActive,
                PastEffectiveEnd = ctx.currentUT > flags.chainEndUT,
                LastPoint = hasPointData ? traj.Points[traj.Points.Count - 1] : default,
                CurrentUT = ctx.currentUT
            });

            ParsekLog.Info("Engine",
                $"Early debris completion: ghost #{index} \"{traj.VesselName}\" " +
                $"explosionUT={explosionUT:F2} currentUT={ctx.currentUT:F2} endUT={traj.EndUT:F2}");
            return true;
        }

        /// <summary>
        /// Handles past-end ghost: positions at final point, triggers explosion if destroyed,
        /// fires completed event. Works for both active and inactive ghost cases.
        /// </summary>
        private void HandlePastEndGhost(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f,
            FrameContext ctx, GhostPlaybackState state, bool ghostActive, bool hasPointData)
        {
            completedEventFired.Add(i);

            if (ghostActive)
            {
                // Position ghost at the true recording endpoint.
                PositionGhostAtRecordingEndpoint(i, traj, state);

                // Trigger explosion if destroyed
                TriggerExplosionIfDestroyed(state, traj, i, ctx.warpRate);
            }

            // Fire completed event (policy handles spawn/resources/camera).
            // Ghost stays alive — policy decides when to destroy
            // (may hold for watch-mode camera, or destroy immediately).
            deferredCompletedEvents.Add(new PlaybackCompletedEvent
            {
                Index = i,
                Trajectory = traj,
                State = state,
                Flags = f,
                GhostWasActive = ghostActive,
                PastEffectiveEnd = ctx.currentUT > f.chainEndUT,
                LastPoint = hasPointData ? traj.Points[traj.Points.Count - 1] : default,
                CurrentUT = ctx.currentUT
            });
        }

        /// <summary>
        /// Applies per-frame visual events to a ghost: part events, flag events,
        /// reentry FX, and RCS emission state. Called after positioning.
        /// When skipPartEvents is true, only reentry FX and RCS are applied.
        /// </summary>
        private void ApplyFrameVisuals(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, float warpRate,
            bool skipPartEvents, bool suppressVisualFx, bool allowTransientEffects = true)
        {
            if (!skipPartEvents)
            {
                GhostPlaybackLogic.ApplyPartEvents(index, traj, ut, state, allowTransientEffects);
                // Flag events are applied earlier in RenderInRangeGhost, before zone check (#249).
            }

            if (suppressVisualFx)
            {
                GhostPlaybackLogic.StopAllEngineFx(state);
                GhostPlaybackLogic.StopAllRcsFx(state);
                GhostPlaybackLogic.StopAllRcsEmissions(state);
                GhostPlaybackLogic.ResetReentryFx(state, index);
                GhostPlaybackLogic.MuteAllAudio(state);
            }
            else
            {
                UpdateReentryFx(index, state, traj.VesselName, warpRate);
                GhostPlaybackLogic.RestoreAllRcsEmissions(state);
                GhostPlaybackLogic.UnmuteAllAudio(state);
            }

            // Per-frame atmosphere attenuation — smoothly fade audio as ghost ascends/descends.
            // Runs after part events (which may start/stop audio) and after mute/unmute.
            GhostPlaybackLogic.UpdateAudioAtmosphere(state);
        }

        /// <summary>
        /// Handles looping ghost playback for a single trajectory.
        /// Manages cycle changes, ghost spawn/destroy, pause windows.
        /// Fires CameraActionEvents for watch mode interactions (engine does not
        /// know about watch mode — ParsekFlight handles camera in event handlers).
        /// </summary>
        private void UpdateLoopingPlayback(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            bool suppressGhosts, bool suppressVisualFx)
        {
            GhostPlaybackState state;
            ghostStates.TryGetValue(index, out state);
            bool ghostActive = HasLoadedGhostVisuals(state);

            if (!TryResolveLoopSchedule(
                    traj, ctx.autoLoopIntervalSeconds, index,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
            {
                if (ghostActive)
                    DestroyGhost(index, traj, flags, reason: "loop schedule resolution failed");
                CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                return;
            }

            // #381: If the period is shorter than the recording duration, successive
            // launches overlap — use multi-cycle path.
            if (GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, duration))
            {
                bool suppressOverlapGhosts = false;
                if (suppressGhosts
                    && (!GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                            ctx.currentUT,
                            intervalSeconds,
                            duration,
                            playbackStartUT,
                            scheduleStartUT,
                            out double overlapLoopUT,
                            out _)
                        || GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                            ctx.warpRate, traj, overlapLoopUT)))
                {
                    if (ghostActive && state.ghost.activeSelf)
                    {
                        state.ghost.SetActive(false);
                        ParsekLog.Info("Engine",
                            $"Ghost #{index} \"{traj.VesselName}\" (loop) hidden: warp > {WarpThresholds.GhostHide}x");
                        OnLoopCameraAction?.Invoke(new CameraActionEvent
                        {
                            Index = index, Action = CameraActionType.ExitWatch,
                            Trajectory = traj, Flags = flags
                        });
                    }
                    DestroyAllOverlapGhosts(index);
                    CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                    return;
                }
                else if (suppressGhosts)
                {
                    // Keep the newest stationary primary mesh visible, but continue
                    // culling overlap clones at high warp for the original perf reason.
                    suppressOverlapGhosts = true;
                }

                UpdateOverlapPlayback(index, traj, flags, ctx, state,
                    intervalSeconds, duration, playbackStartUT, scheduleStartUT,
                    suppressVisualFx, suppressOverlapGhosts);
                return;
            }

            // --- Period >= duration: single ghost path (pause window may apply) ---
            DestroyAllOverlapGhosts(index);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;
            if (!TryComputeLoopPlaybackUT(traj, ctx.currentUT, ctx.autoLoopIntervalSeconds,
                    out loopUT, out cycleIndex, out inPauseWindow, index))
            {
                if (ghostActive)
                    DestroyGhost(index, traj, flags, reason: "loop UT computation failed");
                CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                return;
            }

            // High time warp: hide moving loop ghosts, but keep stationary surface
            // segments visible because their mesh is not chasing a high-rate trajectory.
            if (suppressGhosts && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                    ctx.warpRate, traj, loopUT))
            {
                if (ghostActive && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.Info("Engine",
                        $"Ghost #{index} \"{traj.VesselName}\" (loop) hidden: warp > {WarpThresholds.GhostHide}x");
                    // Fire camera event so host can exit watch mode
                    OnLoopCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index, Action = CameraActionType.ExitWatch,
                        Trajectory = traj, Flags = flags
                    });
                }
                DestroyAllOverlapGhosts(index);
                CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                return;
            }

            // Rebuild once per loop cycle to guarantee clean visual state and event indices.
            bool cycleChanged = HasLoopCycleChanged(state, cycleIndex);
            if (cycleChanged && state != null)
            {
                if (!HasLoadedGhostVisuals(state))
                {
                    // Bug #450 B2: split first-spawn builds can survive across a loop
                    // boundary before any ghost GameObject exists. Advance the cycle index
                    // so the pending spawn eventually finalizes against the current cycle,
                    // but do NOT emit restart / explosion-hold events for a ghost that
                    // never actually spawned.
                    ReusePrimaryGhostAcrossCycle(index, traj, flags, state, loopUT, cycleIndex);
                }
                else
                {
                // Position at the loop endpoint so explosion appears at the real loop end.
                    PositionGhostAtLoopEndpoint(index, traj, state);

                    bool needsExplosion = !state.explosionFired
                    && GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                        traj, ResolveLoopPlaybackEndpointUT(traj));

                    if (needsExplosion)
                        TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

                    // Fire camera event for cycle change (host handles camera anchor/hold/retarget)
                    OnLoopCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index,
                        Action = needsExplosion ? CameraActionType.ExplosionHoldStart : CameraActionType.ExplosionHoldEnd,
                        AnchorPosition = state.ghost.transform.position,
                        HoldUntilUT = ctx.currentUT + GhostPlayback.OverlapExplosionHoldSeconds,
                        Trajectory = traj, Flags = flags
                    });

                    // Fire loop restarted event
                    OnLoopRestarted?.Invoke(new LoopRestartedEvent
                    {
                        Index = index, Trajectory = traj, State = state, Flags = flags,
                        PreviousCycleIndex = state.loopCycleIndex,
                        NewCycleIndex = cycleIndex,
                        ExplosionFired = needsExplosion,
                        ExplosionPosition = state.ghost.transform.position
                    });

                    GhostPlaybackLogic.ResetReentryFx(state, index);

                    // #406 follow-up: reuse the ghost GameObject across the loop
                    // cycle boundary instead of destroy+spawn. The reuse preserves
                    // the ghost hierarchy, reentryFxInfo, reentryFxPendingBuild
                    // (#450 B3), all info dictionaries, cameraPivot identity,
                    // and horizonProxy; it resets playback iterators, explosionFired,
                    // pauseHidden, rcsSuppressed, lightPlaybackStates, fakeCanopies,
                    // logicalPartIds (re-materialised from snapshot), and part
                    // visibility (previous-cycle decoupled parts get reactivated).
                    // This path does NOT consume a #414 spawn slot and does NOT
                    // increment frameLazyReentryBuildCount. See
                    // docs/dev/plan-406-ghost-reuse-loop-cycles.md for the full
                    // preservation table.
                    //
                    // Suppress the retarget camera event when an explosion was just
                    // emitted: ExplosionHoldStart already set the watch handler into
                    // hold (watchedOverlapCycleIndex == -2) and a follow-up
                    // RetargetToNewGhost would be ignored anyway. The contract is
                    // "one OnLoopCameraAction per cycle boundary" — see
                    // RuntimeTests.ExplosionAnchorPosition_BelowTerrain_ClampsBeforeWatchHold.
                    // Non-destroyed boundaries (ExplosionHoldEnd) still need the
                    // retarget so the watch handler swaps the bridge anchor for
                    // the new cycle's pivot.
                    ReusePrimaryGhostAcrossCycle(
                        index, traj, flags, state, loopUT, cycleIndex,
                        emitRetargetEvent: !needsExplosion);
                    // state is the same object; loaded-ghost path keeps visuals live, and
                    // pending-build path above just advances the cycle index without
                    // emitting restart side effects.
                }
            }

            // Looped ghost distance gating
            double loopGhostDistance = ResolvePlaybackDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);

            var (_, loopSimplified) =
                GhostPlaybackLogic.EvaluateLoopedGhostSpawn(loopGhostDistance);

            if (state == null)
            {
                // True first-spawn path: no ghost has ever existed this session
                // for this recording. Safe to throttle via the #414 spawn slot
                // cap because nothing visible is being replaced. Post-#406 the
                // cycle-rebuild branch above never falls through here — it
                // always reuses the existing ghost — so the throttle is always
                // safe on this line.
                if (!TryReserveSpawnSlot(index, "loop-first-spawn"))
                    return;
                state = CreatePendingSpawnState(
                    traj, loopUT, PendingSpawnLifecycle.LoopEnter, flags);
                state.loopCycleIndex = cycleIndex;
                ghostStates[index] = state;
                GhostVisualLoadStatus loopSpawnStatus = EnsureGhostVisualsLoaded(
                    index, traj, state, loopUT, "loop first spawn",
                    resetCompletedEventDedup: true);
                if (loopSpawnStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    ghostStates.Remove(index);
                    return;
                }
                if (loopSpawnStatus == GhostVisualLoadStatus.Pending)
                {
                    ghostActive = false;
                    return;
                }
                ghostActive = true;
            }

            if (state == null)
                return;

            // Zone-based rendering
            double loopZoneDistance = ResolvePlaybackDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);
            double loopActiveVesselDistance = ResolvePlaybackActiveVesselDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);
            CachePlaybackDistances(state, loopActiveVesselDistance, loopZoneDistance);
            var zoneResult = positioner.ApplyZoneRendering(index, state, traj, loopZoneDistance, ctx.protectedIndex);
            if (zoneResult.hiddenByZone)
            {
                ghostActive = HandleHiddenGhostVisualState(
                    index, traj, state, loopUT, ctx.warpRate, loopZoneDistance, overlapGhost: false,
                    hiddenReason: $"loop hidden by distance LOD at {loopZoneDistance:F0}m");
                return;
            }

            if (!HasLoadedGhostVisuals(state))
            {
                // Bug #414: throttle loop distance-tier rehydration.
                string loopReloadSite = state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                    ? "loop-first-spawn-continue"
                    : "loop-distance-tier-rehydrate";
                if (!TryReserveSpawnSlot(index, loopReloadSite))
                    return;
                GhostVisualLoadStatus loopLoadStatus = EnsureGhostVisualsLoaded(
                    index, traj, state, loopUT,
                    state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                        ? "continuing loop first spawn"
                        : "loop re-entered visible distance tier");
                if (loopLoadStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    return;
                }
                if (loopLoadStatus == GhostVisualLoadStatus.Pending)
                    return;
            }

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, zoneResult.reduceFidelity);
            bool forceWatchedFullFidelity = IsWatchedGhostState(index, state, ctx);
            bool skipLoopPartEvents = zoneResult.skipPartEvents || (loopSimplified && !forceWatchedFullFidelity);
            bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;

            // Pause window: position at end, hide parts, zero velocity for reentry decay
            if (inPauseWindow)
            {
                HandleLoopPauseWindow(index, traj, state, ctx.warpRate);
                return;
            }

            // Bug #613 (PR #594 P1): clear the per-frame retire signal before
            // positioning. The relative loop positioner sets it back to true
            // if the recorded anchor is unresolvable.
            state.anchorRetiredThisFrame = false;

            // Position the loop ghost
            positioner.PositionLoop(index, traj, state, loopUT, effectiveSuppressVisualFx);

            // Apply visual events
            if (RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                    state.anchorRetiredThisFrame))
            {
                // Retired loop ghost: stop FX cleanly, do NOT re-activate, do
                // NOT log appearance. See the matching gate in RenderInRangeGhost.
                ApplyFrameVisuals(index, traj, state, loopUT, ctx.warpRate,
                    skipPartEvents: true, suppressVisualFx: true);
            }
            else
            {
                if (!skipLoopPartEvents)
                    ApplyFrameVisuals(index, traj, state, loopUT, ctx.warpRate, false, effectiveSuppressVisualFx);
                if (ShouldRestoreDeferredRuntimeFxState(
                        ActivateGhostVisualsIfNeeded(state),
                        effectiveSuppressVisualFx))
                    GhostPlaybackLogic.RestoreDeferredRuntimeFxState(state);
            }
        }

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        private void UpdateOverlapPlayback(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            GhostPlaybackState primaryState,
            double intervalSeconds, double duration,
            double playbackStartUT, double scheduleStartUT,
            bool suppressVisualFx, bool suppressOverlapGhosts = false,
            bool stopAfterSuppressOverlapGhostsForTesting = false)
        {
            if (ctx.currentUT < scheduleStartUT)
            {
                if (primaryState != null) DestroyGhost(index, traj, flags, reason: "before activation start UT");
                DestroyAllOverlapGhosts(index);
                CountFrameSkip(GhostPlaybackSkipReason.BeforeActivation);
                return;
            }

            // #443: Compute the effective launch cadence. If the user-configured
            // period would produce more simultaneously-live cycles than the cap,
            // cadence is raised to the minimum value that makes
            // ceil(duration/cadence) <= cap. The user's stored period is
            // unchanged — only the runtime spawn rate adjusts. This replaces
            // the pre-fix behaviour of silently culling older cycles via
            // GetActiveCycles's newest-cycle clamp (which stacked ghosts near
            // launch under short user periods).
            double effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                intervalSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            LogOverlapCadenceIfChanged(index, traj, intervalSeconds, effectiveCadence, duration);

            double cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(
                ctx.currentUT,
                scheduleStartUT,
                scheduleStartUT + duration,
                effectiveCadence,
                GhostPlayback.MaxOverlapGhostsPerRecording, out firstCycle, out lastCycle);

            List<GhostPlaybackState> overlaps;
            if (!overlapGhosts.TryGetValue(index, out overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                overlapGhosts[index] = overlaps;
            }
            else if (suppressOverlapGhosts)
            {
                DestroyAllOverlapGhosts(index);
            }
            if (suppressOverlapGhosts && stopAfterSuppressOverlapGhostsForTesting)
                return;

            // Primary ghost represents the newest (lastCycle)
            bool primaryCycleChanged = HasLoopCycleChanged(primaryState, lastCycle);
            double primaryLoopUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                ctx.currentUT,
                scheduleStartUT,
                playbackStartUT,
                duration,
                cycleDuration,
                lastCycle);
            bool primaryAdvanceDeferredThisFrame = false;

            if (primaryCycleChanged)
            {
                // Move old primary to overlap list if still alive
                if (primaryState != null)
                {
                    if (suppressOverlapGhosts)
                    {
                        DestroyGhost(index, traj, flags,
                            reason: "stationary high-warp overlap primary advanced");
                    }
                    else
                    {
                        ghostStates.Remove(index);
                        if (primaryState.pendingSpawnLifecycle != PendingSpawnLifecycle.None)
                        {
                            // Bug #450 B2: a primary that gets demoted to the overlap list before
                            // its split build completes must NOT later finalize as a brand-new
                            // overlap-primary enter. The old cycle should quietly finish as an
                            // overlap shell with no ghost-created / camera-retarget side effects.
                            primaryState.pendingSpawnLifecycle = PendingSpawnLifecycle.None;
                            primaryState.pendingSpawnFlags = default(TrajectoryPlaybackFlags);
                        }
                        if (primaryState.ghost != null)
                            GhostPlaybackLogic.MuteAllAudio(primaryState); // overlap ghosts get no audio
                        overlaps.Add(primaryState);
                        ParsekLog.VerboseRateLimited("Engine", "overlap-move",
                            $"Ghost #{index} cycle={primaryState.loopCycleIndex} moved to overlap list (audio muted)");
                    }
                }

                // Spawn new primary for lastCycle
                primaryState = CreatePendingSpawnState(
                    traj, primaryLoopUT, PendingSpawnLifecycle.OverlapPrimaryEnter, flags);
                primaryState.loopCycleIndex = lastCycle;
                ghostStates[index] = primaryState;
                GhostVisualLoadStatus overlapPrimarySpawnStatus = EnsureGhostVisualsLoaded(
                    index, traj, primaryState, primaryLoopUT, "overlap primary first spawn",
                    resetCompletedEventDedup: true);
                if (overlapPrimarySpawnStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    ghostStates.Remove(index);
                    ParsekLog.Warn("Engine",
                        $"Overlap: SpawnGhost failed for #{index} cycle={lastCycle}");
                    return;
                }
                primaryAdvanceDeferredThisFrame =
                    overlapPrimarySpawnStatus == GhostVisualLoadStatus.Pending;
            }

            // Position primary ghost
            // Note: anchor-relative positioning is handled internally by positioner.PositionLoop,
            // which calls ShouldUseLoopAnchor itself. No need to pre-compute here.
            if (primaryState != null)
            {
                bool primaryReady = true;
                double primaryDistance = ResolvePlaybackDistance(
                    index, traj, primaryState, primaryLoopUT, ctx.activeVesselPos);
                double primaryActiveVesselDistance = ResolvePlaybackActiveVesselDistance(
                    index, traj, primaryState, primaryLoopUT, ctx.activeVesselPos);
                CachePlaybackDistances(primaryState, primaryActiveVesselDistance, primaryDistance);
                var zoneResult = positioner.ApplyZoneRendering(
                    index, primaryState, traj, primaryDistance, ctx.protectedIndex);
                if (zoneResult.hiddenByZone)
                {
                    // Bug #450 B2: a newly-started overlap-primary build may already have
                    // consumed its one allowed timeline advance this frame above. Do not let
                    // hidden-tier prewarm immediately take a second advance through
                    // HandleHiddenGhostVisualState's EnsureGhostVisualsLoaded path.
                    if (!(primaryAdvanceDeferredThisFrame && !HasLoadedGhostVisuals(primaryState)))
                    {
                        HandleHiddenGhostVisualState(
                            index, traj, primaryState, primaryLoopUT, ctx.warpRate, primaryDistance, overlapGhost: false,
                            hiddenReason: $"overlap primary hidden by distance LOD at {primaryDistance:F0}m");
                    }
                }
                else
                {
                    if (!HasLoadedGhostVisuals(primaryState))
                    {
                        if (primaryAdvanceDeferredThisFrame)
                            primaryReady = false;
                        else
                        {
                            // Bug #414: throttle overlap-primary distance-tier rehydration.
                            string overlapPrimaryReloadSite = primaryState.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                                ? "overlap-primary-first-spawn-continue"
                                : "overlap-primary-rehydrate";
                            if (!TryReserveSpawnSlot(index, overlapPrimaryReloadSite))
                                primaryReady = false;
                            else
                            {
                                GhostVisualLoadStatus overlapPrimaryLoadStatus = EnsureGhostVisualsLoaded(
                                    index, traj, primaryState, primaryLoopUT,
                                    primaryState.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                                        ? "continuing overlap primary first spawn"
                                        : "overlap primary re-entered visible distance tier");
                                if (overlapPrimaryLoadStatus != GhostVisualLoadStatus.CompletedThisCall
                                    && overlapPrimaryLoadStatus != GhostVisualLoadStatus.Ready)
                                {
                                    if (overlapPrimaryLoadStatus == GhostVisualLoadStatus.Failed)
                                        CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                                    primaryReady = false;
                                }
                            }
                        }
                    }

                    if (primaryReady)
                    {
                        GhostPlaybackLogic.ApplyDistanceLodFidelity(primaryState, zoneResult.reduceFidelity);
                        bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
                        // Bug #613 (PR #594 P1): clear retire signal before
                        // positioning; gate visuals/activation/appearance on
                        // it after.
                        primaryState.anchorRetiredThisFrame = false;
                        positioner.PositionLoop(index, traj, primaryState, primaryLoopUT, effectiveSuppressVisualFx);
                        if (RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                                primaryState.anchorRetiredThisFrame))
                        {
                            ApplyFrameVisuals(index, traj, primaryState, primaryLoopUT, ctx.warpRate,
                                skipPartEvents: true, suppressVisualFx: true);
                        }
                        else
                        {
                            ApplyFrameVisuals(index, traj, primaryState, primaryLoopUT, ctx.warpRate,
                                zoneResult.skipPartEvents, effectiveSuppressVisualFx);
                            if (ShouldRestoreDeferredRuntimeFxState(
                                    ActivateGhostVisualsIfNeeded(primaryState),
                                    effectiveSuppressVisualFx))
                                GhostPlaybackLogic.RestoreDeferredRuntimeFxState(primaryState);
                            TrackGhostAppearance(index, traj, primaryState, primaryLoopUT, "loop-primary");
                        }
                    }
                }
            }

            if (suppressOverlapGhosts)
                return;

            // Update overlap ghosts (older cycles)
            UpdateExpireAndPositionOverlaps(index, traj, flags, ctx, overlaps,
                duration, cycleDuration, playbackStartUT, scheduleStartUT, suppressVisualFx);
        }

        /// <summary>
        /// Iterates overlap ghosts (older cycles) in reverse. Expires cycles whose phase
        /// exceeds duration (triggers explosion + camera event), removes null entries,
        /// and positions remaining overlaps at their current loop UT.
        /// </summary>
        private void UpdateExpireAndPositionOverlaps(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            List<GhostPlaybackState> overlaps,
            double duration, double cycleDuration,
            double playbackStartUT, double scheduleStartUT,
            bool suppressVisualFx)
        {
            for (int i = overlaps.Count - 1; i >= 0; i--)
            {
                // Bug #460: count every overlap-ghost iteration (including null-entry
                // cleanup) before any continue / remove, so the mainLoop breakdown WARN's
                // `overlapGhostIterationCount` reflects the full overlap dispatch work
                // that `mainLoopMicroseconds` covers. Matters for distinguishing
                // "slow per-trajectory dispatch" from "many overlap ghosts per trajectory"
                // on the next post-#460 playtest spike.
                frameOverlapGhostIterationCount++;

                var ovState = overlaps[i];
                if (ovState == null)
                {
                    overlaps.RemoveAt(i);
                    continue;
                }

                long cycle = ovState.loopCycleIndex;
                double phase = ctx.currentUT - (scheduleStartUT + cycle * cycleDuration);

                // Expired cycle
                if (phase > duration)
                {
                    if (ovState.ghost != null)
                        PositionGhostAtLoopEndpoint(index, traj, ovState);
                    bool triggerExplosionAtExpiry = !ovState.explosionFired
                        && GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                            traj, ResolveLoopPlaybackEndpointUT(traj));
                    if (triggerExplosionAtExpiry)
                    {
                        TriggerExplosionIfDestroyed(ovState, traj, index, ctx.warpRate);
                    }

                    // Fire camera event for overlap expiry
                    OnOverlapCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index,
                        Action = triggerExplosionAtExpiry
                            ? CameraActionType.ExplosionHoldStart
                            : CameraActionType.ExplosionHoldEnd,
                        NewCycleIndex = cycle,
                        AnchorPosition = ovState.ghost != null ? ovState.ghost.transform.position : Vector3.zero,
                        HoldUntilUT = ctx.currentUT + GhostPlayback.OverlapExplosionHoldSeconds,
                        Trajectory = traj, Flags = flags
                    });

                    // Fire overlap expired event
                    OnOverlapExpired?.Invoke(new OverlapExpiredEvent
                    {
                        Index = index, Trajectory = traj, State = ovState, Flags = flags,
                        CycleIndex = cycle,
                        ExplosionFired = triggerExplosionAtExpiry,
                        ExplosionPosition = ovState.ghost != null ? ovState.ghost.transform.position : Vector3.zero
                    });

                    ParsekLog.VerboseRateLimited("Engine", "overlap-expired",
                        $"Ghost EXITED range: #{index} \"{traj.VesselName}\" cycle={cycle} (overlap expired)");
                    DestroyOverlapGhostState(ovState);
                    overlaps.RemoveAt(i);
                    continue;
                }

                phase = Math.Max(0, Math.Min(phase, duration));
                double loopUT = playbackStartUT + phase;
                double overlapDistance = ResolvePlaybackDistance(
                    index, traj, ovState, loopUT, ctx.activeVesselPos);
                double overlapActiveVesselDistance = ResolvePlaybackActiveVesselDistance(
                    index, traj, ovState, loopUT, ctx.activeVesselPos);
                CachePlaybackDistances(ovState, overlapActiveVesselDistance, overlapDistance);
                var zoneResult = positioner.ApplyZoneRendering(
                    index, ovState, traj, overlapDistance, ctx.protectedIndex);
                if (zoneResult.hiddenByZone)
                {
                    HandleHiddenGhostVisualState(
                        index, traj, ovState, loopUT, ctx.warpRate, overlapDistance, overlapGhost: true,
                        hiddenReason: $"overlap hidden by distance LOD at {overlapDistance:F0}m");
                    continue;
                }

                if (!HasLoadedGhostVisuals(ovState))
                {
                    // Bug #414: throttle loop-overlap distance-tier rehydration.
                    if (!TryReserveSpawnSlot(index, "loop-overlap-rehydrate"))
                        continue;
                    GhostVisualLoadStatus overlapLoadStatus = EnsureGhostVisualsLoaded(
                        index, traj, ovState, loopUT, "overlap re-entered visible distance tier");
                    if (overlapLoadStatus == GhostVisualLoadStatus.Failed
                        || overlapLoadStatus == GhostVisualLoadStatus.Pending)
                        continue;
                }

                GhostPlaybackLogic.ApplyDistanceLodFidelity(ovState, zoneResult.reduceFidelity);
                bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
                // Bug #613 (PR #594 P1): clear retire signal before
                // positioning; gate visuals/activation/appearance on it after.
                ovState.anchorRetiredThisFrame = false;
                positioner.PositionLoop(index, traj, ovState, loopUT, effectiveSuppressVisualFx);
                if (RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                        ovState.anchorRetiredThisFrame))
                {
                    ApplyFrameVisuals(index, traj, ovState, loopUT, ctx.warpRate,
                        skipPartEvents: true, suppressVisualFx: true);
                }
                else
                {
                    ApplyFrameVisuals(index, traj, ovState, loopUT, ctx.warpRate,
                        zoneResult.skipPartEvents, effectiveSuppressVisualFx);
                    if (ShouldRestoreDeferredRuntimeFxState(
                            ActivateGhostVisualsIfNeeded(ovState),
                            effectiveSuppressVisualFx))
                        GhostPlaybackLogic.RestoreDeferredRuntimeFxState(ovState);
                    TrackGhostAppearance(index, traj, ovState, loopUT, "loop-overlap");
                }
            }
        }

        /// <summary>
        /// Handles the loop pause window: positions ghost at the final loop point,
        /// hides all parts (crash-site hold), zeroes velocity for reentry FX decay,
        /// and triggers explosion if the recording ended in destruction.
        /// </summary>
        private void HandleLoopPauseWindow(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, float warpRate)
        {
            // Bug #613 (PR #594 P1): clear retire signal before positioning;
            // gate visuals/activation/appearance on it after. The loop-pause
            // window calls PositionGhostAtLoopEndpoint -> positioner.PositionLoop,
            // which routes through the relative-frame retire branch when
            // traj.LoopAnchorVesselId is unresolvable (same Re-Fly rewind
            // failure mode covered by the per-frame gate above).
            state.anchorRetiredThisFrame = false;
            PositionGhostAtLoopEndpoint(index, traj, state);
            // PositionAtPoint now sets state.lastInterpolatedAltitude to the
            // clamped altitude (#282). Don't overwrite it here with the raw
            // recording-end altitude on the first pause-window frame, or the
            // watch overlay reports the buried recorded value for that one frame.
            // Still seed the body name (PositionAtPoint doesn't touch it).
            if (string.IsNullOrEmpty(state.lastInterpolatedBodyName))
            {
                state.lastInterpolatedBodyName = traj?.Points != null && traj.Points.Count > 0
                    ? traj.Points[traj.Points.Count - 1].bodyName
                    : null;
            }
            if (GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                    traj, ResolveLoopPlaybackEndpointUT(traj)))
            {
                TriggerExplosionIfDestroyed(state, traj, index, warpRate);
            }
            if (!state.pauseHidden)
            {
                state.pauseHidden = true;
                GhostPlaybackLogic.HideAllGhostParts(state);
            }
            // Bug #450 B3: widened guard — pending-but-not-yet-built ghosts also need
            // UpdateReentryFx to run so the lazy build can fire if the loop-pause frame
            // happens to be the first one in atmosphere.
            if (state.reentryFxInfo != null || state.reentryFxPendingBuild)
            {
                state.lastInterpolatedVelocity = Vector3.zero;
                UpdateReentryFx(index, state, traj.VesselName, warpRate);
            }
            // Bug #613 (PR #594 P1): if the loop-endpoint position landed in a
            // relative section whose anchor pid is unresolvable, the retire
            // branch already called SetActive(false). Skip the
            // ActivateGhostVisualsIfNeeded + TrackGhostAppearance pair here so
            // the same-frame reactivation race can't undo the hide.
            if (RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                    state.anchorRetiredThisFrame))
                return;
            ActivateGhostVisualsIfNeeded(state);
            double appearanceUT = ResolveLoopPlaybackEndpointUT(traj);
            TrackGhostAppearance(index, traj, state, appearanceUT, "loop-pause");
        }

        private void PositionGhostAtLoopEndpoint(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state)
        {
            if (state?.ghost == null || traj == null || positioner == null)
                return;

            positioner.PositionLoop(index, traj, state, ResolveLoopPlaybackEndpointUT(traj), true);
        }

        private void PositionGhostAtRecordingEndpoint(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state)
        {
            if (state?.ghost == null || traj == null || positioner == null)
                return;

            if (RecordingEndpointResolver.TryGetOrbitEndpointUT(traj, out double orbitEndpointUT))
            {
                positioner.PositionFromOrbit(index, traj, state, orbitEndpointUT);
                if (RecordingEndpointResolver.TryGetOrbitEndpointCoordinates(
                    traj, out string bodyName, out _, out _, out double altitude))
                {
                    state.lastInterpolatedBodyName = bodyName;
                    state.lastInterpolatedAltitude = altitude;
                    state.lastInterpolatedVelocity = Vector3.zero;
                }
                return;
            }

            if (traj.Points != null && traj.Points.Count > 0)
            {
                positioner.PositionAtPoint(index, traj, state, traj.Points[traj.Points.Count - 1]);
                return;
            }

            if (traj.SurfacePos.HasValue)
            {
                positioner.PositionAtSurface(index, traj, state);
                state.lastInterpolatedBodyName = traj.SurfacePos.Value.body;
                state.lastInterpolatedAltitude = traj.SurfacePos.Value.altitude;
                state.lastInterpolatedVelocity = Vector3.zero;
                return;
            }

            if (traj.HasOrbitSegments)
                positioner.PositionFromOrbit(index, traj, state, traj.OrbitSegments[traj.OrbitSegments.Count - 1].endUT);
        }

        #endregion

        #region Loop utilities

        /// <summary>
        /// Returns the effective loop start UT, falling back to the first playable
        /// ghost-activation UT when LoopStartUT is NaN or out of range.
        /// </summary>
        internal static double EffectiveLoopStartUT(IPlaybackTrajectory traj)
        {
            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double loopStart = traj.LoopStartUT;
            if (!double.IsNaN(loopStart) && loopStart >= activationStartUT && loopStart < traj.EndUT)
            {
                // Cross-validate: effective start must be less than effective end
                double loopEnd = traj.LoopEndUT;
                double effectiveEnd = (!double.IsNaN(loopEnd) && loopEnd <= traj.EndUT && loopEnd > activationStartUT)
                    ? loopEnd : traj.EndUT;
                if (loopStart >= effectiveEnd)
                    return activationStartUT;
                return loopStart;
            }
            return activationStartUT;
        }

        /// <summary>
        /// Returns the effective loop end UT, falling back to traj.EndUT when
        /// LoopEndUT is NaN or out of range.
        /// </summary>
        internal static double EffectiveLoopEndUT(IPlaybackTrajectory traj)
        {
            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double loopEnd = traj.LoopEndUT;
            if (!double.IsNaN(loopEnd) && loopEnd <= traj.EndUT && loopEnd > activationStartUT)
            {
                // Cross-validate: effective end must be greater than effective start
                double loopStart = traj.LoopStartUT;
                double effectiveStart = (!double.IsNaN(loopStart) && loopStart >= activationStartUT && loopStart < traj.EndUT)
                    ? loopStart : activationStartUT;
                if (loopEnd <= effectiveStart)
                    return traj.EndUT;
                return loopEnd;
            }
            return traj.EndUT;
        }

        /// <summary>
        /// Returns the effective loop duration (EffectiveLoopEndUT - EffectiveLoopStartUT).
        /// All loop-dispatch decisions that care about "one cycle length" — IsOverlapLoop,
        /// overlap phase clamping, watch-mode single-vs-overlap choice — should use this
        /// instead of `traj.EndUT - traj.StartUT` or the half-hybrid `traj.EndUT - effStart`,
        /// so recordings with a custom loop subrange get consistent duration everywhere.
        /// #409: was duplicated inline at the watch-mode sites with inconsistent formulas.
        /// </summary>
        internal static double EffectiveLoopDuration(IPlaybackTrajectory traj)
        {
            return EffectiveLoopEndUT(traj) - EffectiveLoopStartUT(traj);
        }

        /// <summary>
        /// Converts a pre-#381 legacy "gap after cycle" value into the current launch-to-launch
        /// period. If the reconstructed period underflows or the trajectory bounds are not
        /// available, clamps defensively to MinCycleDuration.
        /// </summary>
        internal static bool TryConvertLegacyGapToLoopPeriodSeconds(
            IPlaybackTrajectory traj,
            double legacyGapSeconds,
            out double migratedPeriod,
            out double effectiveLoopDuration)
        {
            effectiveLoopDuration = EffectiveLoopDuration(traj);
            migratedPeriod = legacyGapSeconds;
            if (double.IsNaN(effectiveLoopDuration) || double.IsInfinity(effectiveLoopDuration)
                || effectiveLoopDuration <= 0.0)
                return false;
            // #411 follow-up: reject NaN/Inf gap defensively so the caller doesn't store a
            // poisoned period on the recording. All real load paths parse via double.TryParse
            // and never hand NaN in, but hand-edited saves can.
            if (double.IsNaN(legacyGapSeconds) || double.IsInfinity(legacyGapSeconds))
                return false;

            migratedPeriod = effectiveLoopDuration + legacyGapSeconds;
            if (double.IsNaN(migratedPeriod) || double.IsInfinity(migratedPeriod)
                || migratedPeriod < LoopTiming.MinCycleDuration)
                migratedPeriod = LoopTiming.MinCycleDuration;
            return true;
        }

        /// <summary>
        /// Returns the UT where loop playback should hold/teardown. For custom loop ranges this
        /// is the effective loop end, not the recording's raw final timestamp.
        /// </summary>
        internal static double ResolveLoopPlaybackEndpointUT(IPlaybackTrajectory traj)
        {
            return EffectiveLoopEndUT(traj);
        }

        /// <summary>Whether the trajectory should loop (has enough points and duration).</summary>
        internal static bool ShouldLoopPlayback(IPlaybackTrajectory traj)
        {
            if (traj == null || !traj.LoopPlayback || traj.Points == null || traj.Points.Count < 2)
                return false;
            double start = EffectiveLoopStartUT(traj);
            double end = EffectiveLoopEndUT(traj);
            return end - start > LoopTiming.MinLoopDurationSeconds;
        }

        private static int CompareAutoLoopQueueCandidates(AutoLoopQueueCandidate a, AutoLoopQueueCandidate b)
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

        private void RebuildAutoLoopLaunchScheduleCache(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            double autoLoopIntervalSeconds)
        {
            autoLoopLaunchSchedules.Clear();
            autoLoopQueueScratch.Clear();
            if (trajectories == null || trajectories.Count == 0)
                return;

            for (int i = 0; i < trajectories.Count; i++)
            {
                var traj = trajectories[i];
                if (!GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(traj))
                    continue;

                autoLoopQueueScratch.Add(new AutoLoopQueueCandidate(
                    i,
                    EffectiveLoopStartUT(traj),
                    EffectiveLoopEndUT(traj),
                    traj.RecordingId));
            }

            if (autoLoopQueueScratch.Count == 0)
                return;

            autoLoopQueueScratch.Sort(CompareAutoLoopQueueCandidates);
            double launchGapSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                trajectories[autoLoopQueueScratch[0].RecordingIndex],
                autoLoopIntervalSeconds,
                LoopTiming.DefaultLoopIntervalSeconds,
                LoopTiming.MinCycleDuration);
            double anchorUT = autoLoopQueueScratch[0].PlaybackStartUT;
            double cadenceSeconds = launchGapSeconds * autoLoopQueueScratch.Count;
            ParsekLog.Verbose("Engine",
                $"Auto loop queue rebuilt: count={autoLoopQueueScratch.Count} " +
                $"anchorUT={anchorUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"cadence={cadenceSeconds.ToString("R", CultureInfo.InvariantCulture)}s");
            for (int slot = 0; slot < autoLoopQueueScratch.Count; slot++)
            {
                var candidate = autoLoopQueueScratch[slot];
                autoLoopLaunchSchedules[candidate.RecordingIndex] =
                    new GhostPlaybackLogic.AutoLoopLaunchSchedule(
                        anchorUT + (slot * launchGapSeconds),
                        cadenceSeconds,
                        slot,
                        autoLoopQueueScratch.Count);
            }
        }

        private bool TryResolveLoopSchedule(
            IPlaybackTrajectory traj,
            double autoLoopIntervalSeconds,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            intervalSeconds = LoopTiming.DefaultLoopIntervalSeconds;
            if (traj == null || traj.Points == null || traj.Points.Count < 2)
                return false;

            playbackStartUT = EffectiveLoopStartUT(traj);
            double playbackEndUT = EffectiveLoopEndUT(traj);
            duration = playbackEndUT - playbackStartUT;
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double baseIntervalSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                traj, autoLoopIntervalSeconds,
                LoopTiming.DefaultLoopIntervalSeconds,
                LoopTiming.MinCycleDuration);
            if (recIdx >= 0 && autoLoopLaunchSchedules.TryGetValue(recIdx, out var autoSchedule))
            {
                scheduleStartUT = autoSchedule.LaunchStartUT;
                intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                return true;
            }

            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;
            return true;
        }

        /// <summary>Resolve the effective loop interval for a trajectory.</summary>
        internal double GetLoopIntervalSeconds(IPlaybackTrajectory traj, double autoLoopIntervalSeconds)
        {
            return GhostPlaybackLogic.ResolveLoopInterval(
                traj, autoLoopIntervalSeconds,
                LoopTiming.DefaultLoopIntervalSeconds,
                LoopTiming.MinCycleDuration);
        }

        internal double GetLoopIntervalSeconds(
            IPlaybackTrajectory traj,
            double autoLoopIntervalSeconds,
            int recIdx)
        {
            if (recIdx >= 0 && autoLoopLaunchSchedules.TryGetValue(recIdx, out var autoSchedule))
                return autoSchedule.LaunchCadenceSeconds;

            return GetLoopIntervalSeconds(traj, autoLoopIntervalSeconds);
        }

        /// <summary>
        /// Compute the effective UT within a looping trajectory, accounting for cycle index,
        /// pause windows, and loop phase offsets. Returns false if the trajectory is not loopable.
        /// </summary>
        internal bool TryComputeLoopPlaybackUT(
            IPlaybackTrajectory traj,
            double currentUT,
            double autoLoopIntervalSeconds,
            out double loopUT,
            out long cycleIndex,
            out bool inPauseWindow,
            int recIdx = -1)
        {
            cycleIndex = 0;
            inPauseWindow = false;
            loopUT = 0;
            if (!TryResolveLoopSchedule(
                    traj, autoLoopIntervalSeconds, recIdx,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
                return false;

            // Apply loop phase offset (set by Watch mode to reset ghost to recording start)
            double phaseOffset;
            if (recIdx >= 0 && loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
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
            if (inPauseWindow && ParsekLog.IsVerboseEnabled)
            {
                ParsekLog.VerboseRateLimited("Engine", "loop_pause_" + recIdx,
                    $"TryComputeLoopPlaybackUT: in pause window for recIdx={recIdx}, cycle={cycleIndex}");
            }
            return true;
        }

        /// <summary>Whether any time warp is active (using FrameContext values, no KSP globals).</summary>
        internal static bool IsAnyWarpActive(FrameContext ctx)
        {
            return GhostPlaybackLogic.IsAnyWarpActive(ctx.warpRateIndex, ctx.warpRate);
        }

        private bool IsWatchedGhostState(int recordingIndex, GhostPlaybackState state, FrameContext ctx)
        {
            if (state == null)
                return false;

            if (IsWatchedGhostStateResolver != null)
                return IsWatchedGhostStateResolver(recordingIndex, state);

            return GhostPlaybackLogic.IsProtectedGhost(
                ctx.protectedIndex, ctx.protectedLoopCycleIndex,
                recordingIndex, state.loopCycleIndex);
        }

        private double ResolvePlaybackDistance(
            int recordingIndex, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d activeVesselPos)
        {
            if (ResolvePlaybackDistanceOverride != null)
            {
                double resolved = ResolvePlaybackDistanceOverride(
                    recordingIndex, traj, state, playbackUT);
                if (!double.IsNaN(resolved) && !double.IsInfinity(resolved) && resolved >= 0)
                    return resolved;
            }

            if (state != null && state.ghost != null)
                return Vector3d.Distance((Vector3d)state.ghost.transform.position, activeVesselPos);

            return double.MaxValue;
        }

        private double ResolvePlaybackActiveVesselDistance(
            int recordingIndex, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d activeVesselPos)
        {
            if (ResolvePlaybackActiveVesselDistanceOverride != null)
            {
                double resolved = ResolvePlaybackActiveVesselDistanceOverride(
                    recordingIndex, traj, state, playbackUT);
                if (!double.IsNaN(resolved) && !double.IsInfinity(resolved) && resolved >= 0)
                    return resolved;
            }

            if (state != null && state.ghost != null)
                return Vector3d.Distance((Vector3d)state.ghost.transform.position, activeVesselPos);

            return double.MaxValue;
        }

        internal static void CachePlaybackDistances(
            GhostPlaybackState state, double activeVesselDistance, double renderDistance)
        {
            if (state == null)
                return;

            state.lastDistance = activeVesselDistance;
            state.lastRenderDistance = renderDistance;
        }

        /// <summary>Whether any time warp is active (reads KSP globals directly — host convenience wrapper).</summary>
        internal static bool IsAnyWarpActiveFromGlobals()
        {
            return GhostPlaybackLogic.IsAnyWarpActive(TimeWarp.CurrentRateIndex, TimeWarp.CurrentRate);
        }

        #endregion

        #region Reentry FX

        /// <summary>
        /// Update reentry visual effects for a ghost. Computes atmospheric density,
        /// Mach number, and intensity, then drives glow/fire/shell layers.
        /// </summary>
        /// <remarks>
        /// Bug #450 B3: entry condition widened to allow ghosts whose reentry build
        /// was deferred at spawn (<c>state.reentryFxPendingBuild == true</c>) to
        /// reach the body/atmosphere lookup below. The lazy build fires inside this
        /// method after the body is resolved so there is no duplicate
        /// <c>FlightGlobals.Bodies.Find</c> on the hot path. Drive-to-zero paths
        /// short-circuit to a plain return when <c>reentryFxInfo</c> is still null
        /// (nothing to drive, we're just waiting for in-atmosphere conditions).
        /// </remarks>
        internal void UpdateReentryFx(int recIdx, GhostPlaybackState state, string vesselName, float warpRate)
        {
            if (state == null || state.ghost == null) return;
            if (state.reentryFxInfo == null && !state.reentryFxPendingBuild) return;

            var info = state.reentryFxInfo;
            if (info != null && GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
            {
                DriveReentryToZero(info, recIdx, state.lastInterpolatedBodyName,
                    state.lastInterpolatedAltitude, vesselName);
                return;
            }

            Vector3 interpolatedVel = state.lastInterpolatedVelocity;
            string bodyName = state.lastInterpolatedBodyName;
            double altitude = state.lastInterpolatedAltitude;

            if (string.IsNullOrEmpty(bodyName))
            {
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, 0.0, vesselName, state);
                return;
            }

            // TODO(standalone): FlightGlobals.Bodies is a KSP global dependency.
            // For standalone extraction, inject a body-lookup delegate or interface
            // (e.g., Func<string, CelestialBody> bodyLookup) via the constructor.
            // CelestialBody is needed here for atmosphere physics (pressure, temperature,
            // density, speed of sound), so this cannot be easily abstracted away.
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Engine", $"ghost-{recIdx}-nobody",
                    $"ReentryFx: body '{bodyName}' not found — skipping");
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            if (!body.atmosphere)
            {
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            if (altitude >= body.atmosphereDepth)
            {
                if (info != null)
                    DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            // Compute surface velocity BEFORE the lazy-build decision so the speed
            // gate can fire. `ReentryPotentialSpeedFloor` (400 m/s ≈ Mach 1.2 at
            // sea-level Kerbin) is shared with `HasReentryPotential` and gates out
            // pad launches where the ghost starts at 0 m/s — without this gate,
            // ghosts whose first playback point is already in-atmosphere (every
            // KSC launch recording) would still pay the full 7 ms build on frame 1,
            // just attributed to mainLoop instead of spawn.
            Vector3 surfaceVel = interpolatedVel - (Vector3)body.getRFrmVel(state.ghost.transform.position);
            float speed = surfaceVel.magnitude;

            // Bug #450 B3: lazy-build seam. Reuses the body + atmosphere + altitude
            // + speed checks above. If the build is throttled or the gate fails,
            // return (for null info) — next frame will retry while still in
            // atmosphere. Flag cleared only when the build actually fires, see
            // TryPerformLazyReentryBuild.
            //
            // Under warp-suppression the FX subsystem would drive-to-zero anyway
            // (see GhostPlaybackLogic.ShouldSuppressVisualFx); doing a ~7 ms build
            // of FX we immediately hide contradicts the suppression policy. Defer
            // one more frame past the warp drop-out — the ghost hasn't moved
            // meaningfully during warp anyway.
            if (state.reentryFxPendingBuild)
            {
                if (GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
                    return;
                if (!GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                        state.reentryFxPendingBuild, bodyName, body.atmosphere,
                        altitude, body.atmosphereDepth,
                        speed, TrajectoryMath.ReentryPotentialSpeedFloor))
                {
                    // In-atmosphere but still too slow for FX to be imminent
                    // (typical KSC launch below ~Mach 1.2). Return — info is null,
                    // nothing to drive. Next frame will re-check.
                    return;
                }
                TryPerformLazyReentryBuild(recIdx, state, vesselName, bodyName, altitude);
                info = state.reentryFxInfo;
                if (info == null) return;
            }

            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure < 0 || double.IsNaN(temperature) || temperature < 0)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0)
            {
                DriveReentryToZero(info, recIdx, bodyName, altitude, vesselName, state);
                return;
            }

            double speedOfSound = body.GetSpeedOfSound(pressure, density);
            float machNumber = (speedOfSound > 0) ? (float)(speed / speedOfSound) : 0f;
            float rawIntensity = GhostVisualBuilder.ComputeReentryIntensity(speed, (float)density, machNumber);

            float smoothedIntensity = Mathf.Lerp(info.lastIntensity, rawIntensity,
                1f - Mathf.Exp(-GhostVisualBuilder.ReentrySmoothingRate * Time.deltaTime));
            if (smoothedIntensity < 0.001f && rawIntensity == 0f)
                smoothedIntensity = 0f;

            DriveReentryLayers(info, smoothedIntensity, surfaceVel, recIdx, bodyName, altitude, machNumber, vesselName, state);
            info.lastIntensity = smoothedIntensity;
        }

        /// <summary>
        /// Bug #450 B3: perform the deferred reentry-FX build. Called from inside
        /// <see cref="UpdateReentryFx"/> after its body/atmosphere/altitude guards have
        /// already confirmed the ghost is in atmosphere, so there is no duplicate
        /// <c>FlightGlobals.Bodies.Find</c> lookup. Respects
        /// <see cref="GhostPlayback.MaxLazyReentryBuildsPerFrame"/> to prevent a burst of simultaneous
        /// atmosphere-entries from producing the same bimodal hitch #450 is trying to
        /// eliminate.
        /// </summary>
        private void TryPerformLazyReentryBuild(
            int recIdx, GhostPlaybackState state, string vesselName,
            string bodyName, double altitude)
        {
            // Defensive idempotency: the production call site in UpdateReentryFx guards
            // on state.reentryFxPendingBuild, but this method is also exposed as a test
            // seam. A re-entrant call with the flag already cleared must be a no-op.
            if (state == null || !state.reentryFxPendingBuild) return;

            if (frameLazyReentryBuildCount >= GhostPlayback.MaxLazyReentryBuildsPerFrame)
            {
                frameLazyReentryBuildDeferred++;
                // Per-index rate-limit key so a burst of same-frame throttles does
                // not collapse into a single shared-key log line — the counter
                // captures the count, but the per-ghost identity matters for
                // post-playtest diagnosis. 1-second window still bounds volume per
                // ghost.
                ParsekLog.VerboseRateLimited("ReentryFx", $"lazy-throttle-{recIdx}",
                    $"Lazy reentry build throttled: #{recIdx} deferred to next frame " +
                    $"(used {frameLazyReentryBuildCount}/{GhostPlayback.MaxLazyReentryBuildsPerFrame})", 1.0);
                return;  // Flag stays true — retry next frame while still in atmosphere.
            }

            // Defensive: heatInfos is only nulled by ClearLoadedVisualReferences (which
            // also clears our pending flag) or by a full rebuild path. If we somehow
            // land here with a null it, clear the flag so we don't burn CPU every
            // frame on an impossible build, and let the subsystem self-heal on the
            // next rebuild.
            if (state.heatInfos == null)
            {
                state.reentryFxPendingBuild = false;
                ParsekLog.VerboseRateLimited("ReentryFx", $"lazy-noheat-{recIdx}",
                    $"Lazy reentry build skipped for #{recIdx} \"{vesselName}\" — " +
                    $"state.heatInfos is null (rebuild likely in flight); clearing flag", 5.0);
                return;
            }

            frameLazyReentryBuildCount++;
            state.reentryFxPendingBuild = false;  // One-shot: clear even if build fails.
            var built = GhostVisualBuilder.TryBuildReentryFx(
                state.ghost, state.heatInfos, recIdx, vesselName);
            state.reentryFxInfo = built;
            // Count actual builds only — the counter semantic is "FX objects produced",
            // used by the diagnostics `reentryFx built X` line. A failed TryBuildReentryFx
            // (returns null) is observable via `deferred - built > expected` and is NOT
            // a build.
            if (built != null)
            {
                DiagnosticsState.health.reentryFxBuildsThisSession++;
                ParsekLog.Verbose("ReentryFx",
                    $"Lazy reentry build fired for ghost #{recIdx} \"{vesselName}\" — " +
                    $"body={bodyName} alt={altitude:F0}m " +
                    $"(deferred at spawn, built on first atmospheric frame)");
            }
            else
            {
                ParsekLog.VerboseRateLimited("ReentryFx", $"lazy-buildnull-{recIdx}",
                    $"Lazy reentry build returned null for #{recIdx} \"{vesselName}\" — " +
                    $"body={bodyName} alt={altitude:F0}m (flag cleared, no retry)", 5.0);
            }
        }

        private void DriveReentryToZero(ReentryFxInfo info, int recIdx, string bodyName, double altitude,
            string vesselName, GhostPlaybackState state = null)
        {
            DriveReentryLayers(info, 0f, Vector3.zero, recIdx, bodyName, altitude, 0f, vesselName, state);
            info.lastIntensity = 0f;
        }

        private void DriveReentryLayers(ReentryFxInfo info, float intensity, Vector3 surfaceVel,
            int recIdx, string bodyName, double altitude, float machNumber, string vesselName,
            GhostPlaybackState state = null)
        {
            bool wasActive = info.lastIntensity > 0f;
            bool isActive = intensity > 0f;

            if (isActive && !wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Engine",
                    $"ReentryFx: Activated for ghost #{recIdx} \"{vesselName}\" — intensity={intensity:F2}, Mach={machNumber:F2}, speed={speed:F0} m/s, alt={altitude:F0} m, body={bodyName}");
            }
            else if (!isActive && wasActive)
            {
                float speed = surfaceVel.magnitude;
                ParsekLog.Verbose("Engine",
                    $"ReentryFx: Deactivated for ghost #{recIdx} — intensity dropped to 0 (speed={speed:F0} m/s, alt={altitude:F0} m)");
            }

            if (isActive)
            {
                ParsekLog.VerboseRateLimited("Engine", $"ghost-{recIdx}-intensity",
                    $"ReentryFx: ghost #{recIdx} intensity={intensity:F2} speed={surfaceVel.magnitude:F0} alt={altitude:F0}");
            }

            // Layer A: Heat glow (material emission)
            if (info.glowMaterials != null)
            {
                for (int i = 0; i < info.glowMaterials.Count; i++)
                {
                    HeatMaterialState ms = info.glowMaterials[i];
                    if (ms.material == null) continue;

                    if (intensity <= GhostVisualBuilder.ReentryLayerAThreshold)
                    {
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty, ms.coldEmission);
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty, ms.coldColor);
                    }
                    else
                    {
                        float glowFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryLayerAThreshold, 1f, intensity);
                        Color targetEmission = Color.Lerp(GhostVisualBuilder.ReentryHotEmissionLow,
                            GhostVisualBuilder.ReentryHotEmissionHigh, glowFraction);
                        if (!string.IsNullOrEmpty(ms.emissiveProperty))
                            ms.material.SetColor(ms.emissiveProperty,
                                Color.Lerp(ms.coldEmission, ms.coldEmission + targetEmission, glowFraction));
                        if (!string.IsNullOrEmpty(ms.colorProperty))
                            ms.material.SetColor(ms.colorProperty,
                                Color.Lerp(ms.coldColor, ms.hotColor, glowFraction));
                    }
                }
            }

            // Apply ablation char to heat shield parts
            if (state != null)
                GhostPlaybackLogic.ApplyColorChangerCharState(state, intensity);

            // Fire envelope particles. Visible density is controlled primarily by the
            // emission-rate range; maxParticles only supplies headroom for the denser stream.
            if (info.fireParticles != null)
            {
                if (intensity > GhostVisualBuilder.ReentryFireThreshold)
                {
                    float fireFraction = Mathf.InverseLerp(GhostVisualBuilder.ReentryFireThreshold, 1f, intensity);

                    var emissionMod = info.fireParticles.emission;
                    emissionMod.rateOverTimeMultiplier = Mathf.Lerp(
                        GhostVisualBuilder.ReentryFireEmissionMin,
                        GhostVisualBuilder.ReentryFireEmissionMax, fireFraction);

                    var mainMod = info.fireParticles.main;
                    mainMod.startSizeMultiplier = Mathf.Lerp(0.8f, 2.0f, fireFraction);

                    if (!info.fireParticles.isPlaying)
                        info.fireParticles.Play();
                }
                else
                {
                    if (info.fireParticles.isPlaying)
                        info.fireParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            // Fire shell overlay
            if (info.fireShellMeshes != null && info.fireShellMaterial != null
                && intensity > GhostVisualBuilder.ReentryFireThreshold)
            {
                Vector3 velDir = surfaceVel.normalized;
                float maxOffset = info.vesselLength * GhostVisualBuilder.ReentryFireShellMaxOffset * intensity;
                float baseTint = Mathf.Lerp(0.026f, 0.156f, intensity);

                for (int pass = 0; pass < GhostVisualBuilder.ReentryFireShellPasses; pass++)
                {
                    float t = (pass + 1f) / GhostVisualBuilder.ReentryFireShellPasses;
                    Vector3 offset = -velDir * (maxOffset * t);
                    float alpha = baseTint * (1f - t * 0.7f);
                    Color tint = GhostVisualBuilder.ReentryFireShellColor * alpha;

                    var mpb = state?.reentryMpb ?? new MaterialPropertyBlock();
                    mpb.SetColor("_TintColor", tint);

                    for (int m = 0; m < info.fireShellMeshes.Count; m++)
                    {
                        FireShellMesh fsm = info.fireShellMeshes[m];
                        if (fsm.mesh == null || fsm.transform == null) continue;
                        if (!fsm.transform.gameObject.activeInHierarchy) continue;

                        Matrix4x4 matrix = Matrix4x4.Translate(offset) * fsm.transform.localToWorldMatrix;
                        Graphics.DrawMesh(fsm.mesh, matrix, info.fireShellMaterial, 0, null, 0, mpb);
                    }
                }
            }
        }

        #endregion

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

        private static void CountFxForObservability(
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

        private static int CountModulesAndParticleSystems<TGhostInfo>(
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

        #region Query API

        /// <summary>Number of primary timeline ghosts with loaded visuals.</summary>
        internal int GhostCount
        {
            get
            {
                int count = 0;
                foreach (var state in ghostStates.Values)
                {
                    if (HasLoadedGhostVisuals(state))
                        count++;
                }
                return count;
            }
        }

        /// <summary>Whether a ghost exists for the given recording index.</summary>
        internal bool HasGhost(int index) => ghostStates.ContainsKey(index);

        /// <summary>Whether a ghost exists with a non-null GameObject.</summary>
        internal bool HasActiveGhost(int index)
        {
            return ghostStates.TryGetValue(index, out var state) && HasLoadedGhostVisuals(state);
        }

        /// <summary>Get the ghost state for a recording index.</summary>
        internal bool TryGetGhostState(int index, out GhostPlaybackState state)
        {
            return ghostStates.TryGetValue(index, out state);
        }

        internal bool EnsureGhostVisualsLoadedForWatch(
            int index, IPlaybackTrajectory traj, double playbackUT,
            bool forceRebuildLoadedVisuals = false)
        {
            if (!ghostStates.TryGetValue(index, out var state) || state == null)
                return false;
            if (forceRebuildLoadedVisuals && HasLoadedGhostVisuals(state))
            {
                DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
                state.ClearLoadedVisualReferences();
            }
            GhostVisualLoadStatus loadStatus = EnsureGhostVisualsLoaded(
                index, traj, state, playbackUT, "watch mode requested",
                forceImmediateBuild: true);
            if (loadStatus != GhostVisualLoadStatus.CompletedThisCall
                && loadStatus != GhostVisualLoadStatus.Ready)
                return false;

            SynchronizeLoadedGhostForWatch(index, traj, state, playbackUT);
            return true;
        }

        /// <summary>Get the camera pivot transform for a ghost (for watch mode targeting).</summary>
        internal bool TryGetGhostPivot(int index, out Transform pivot)
        {
            pivot = null;
            if (ghostStates.TryGetValue(index, out var state) && state?.cameraPivot != null)
            {
                pivot = state.cameraPivot;
                return true;
            }
            return false;
        }

        /// <summary>Whether the ghost is within visual rendering range (not in Beyond zone).</summary>
        internal bool IsGhostWithinVisualRange(int index)
        {
            return ghostStates.TryGetValue(index, out var state)
                && state != null
                && state.currentZone != RenderingZone.Beyond;
        }

        /// <summary>Whether the ghost is on the specified celestial body.</summary>
        internal bool IsGhostOnBody(int index, string bodyName)
        {
            if (!ghostStates.TryGetValue(index, out var state) || state == null)
                return false;
            if (string.IsNullOrEmpty(bodyName))
                return false;
            return state.lastInterpolatedBodyName == bodyName;
        }

        /// <summary>Get the body name the ghost was last positioned on.</summary>
        internal string GetGhostBodyName(int index)
        {
            return ghostStates.TryGetValue(index, out var state) ? state?.lastInterpolatedBodyName : null;
        }

        /// <summary>Build a dictionary of recording index to ghost GameObject (for UI).</summary>
        internal Dictionary<int, GameObject> GetGhostGameObjects()
        {
            var result = new Dictionary<int, GameObject>(ghostStates.Count);
            foreach (var kv in ghostStates)
            {
                if (kv.Value?.ghost != null)
                    result[kv.Key] = kv.Value.ghost;
            }
            return result;
        }

        /// <summary>Get overlap ghosts for a recording index.</summary>
        internal bool TryGetOverlapGhosts(int index, out List<GhostPlaybackState> overlaps)
        {
            return overlapGhosts.TryGetValue(index, out overlaps);
        }

        /// <summary>Get active ghost positions for proximity checking (Real Spawn Control).</summary>
        internal IEnumerable<(int index, Vector3 position)> GetActiveGhostPositions()
        {
            foreach (var kv in ghostStates)
            {
                if (kv.Value?.ghost != null && kv.Value.ghost.activeSelf)
                    yield return (kv.Key, kv.Value.ghost.transform.position);
            }
        }

        #endregion

        #region Anchor vessel lifecycle

        /// <summary>Notify the engine that an anchor vessel was loaded.</summary>
        internal void OnAnchorVesselLoaded(uint vesselPid)
        {
            loadedAnchorVessels.Add(vesselPid);
        }

        /// <summary>Notify the engine that an anchor vessel was unloaded.</summary>
        internal void OnAnchorVesselUnloaded(uint vesselPid)
        {
            loadedAnchorVessels.Remove(vesselPid);
        }

        #endregion

        #region Ghost lifecycle

        /// <summary>
        /// #406 follow-up: reuse the existing ghost GameObject across a
        /// loop-cycle boundary instead of destroy+spawn. The ghost hierarchy
        /// (parts, particle systems, audio sources, cloned materials) and
        /// reentry FX resources (mesh, particle system, glow materials) survive;
        /// only playback iterators, per-cycle flags, and transient per-cycle
        /// state are reset. Visibility of parts hidden by prior-cycle events
        /// (decouple/destroy/inventory placed) is restored via
        /// <see cref="GhostPlaybackLogic.ReactivateGhostPartHierarchyForLoopRewind"/>
        /// and the initial-spawn visibility baseline re-applied. Does NOT
        /// consume a #414 spawn slot, does NOT increment
        /// <c>frameSpawnCount</c>, does NOT fire <c>OnGhostCreated</c> (the
        /// ghost was never destroyed). Fires
        /// <c>OnLoopCameraAction(RetargetToNewGhost)</c> at the end so the
        /// host (WatchModeController / ParsekFlight) can re-snap the camera
        /// to the ghost's post-wrap position — the <c>cameraPivot</c>
        /// Transform identity is preserved across the reuse so any hard
        /// references held by the camera code remain valid.
        ///
        /// Set <paramref name="emitRetargetEvent"/> to <c>false</c> when the
        /// caller has already emitted a primary <c>OnLoopCameraAction</c> for
        /// this cycle boundary (e.g. <c>ExplosionHoldStart</c>) and a follow-up
        /// <c>RetargetToNewGhost</c> would either be ignored by the watch
        /// handler (already in explosion hold) or override an explicit
        /// camera-state decision. The destroyed-loop-cycle path in
        /// <c>UpdateLoopingPlayback</c> uses this to keep the camera held at
        /// the explosion site without firing a redundant retarget event
        /// immediately after.
        /// </summary>
        internal void ReusePrimaryGhostAcrossCycle(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            GhostPlaybackState state, double playbackUT, long newCycleIndex,
            bool emitRetargetEvent = true)
        {
            if (state == null || state.ghost == null)
            {
                // Advance loopCycleIndex so HasLoopCycleChanged returns false next
                // frame — otherwise a state with a null/Unity-destroyed ghost
                // re-enters this path every frame and spams the log. The ghost
                // either never built (no snapshot) or was externally destroyed;
                // the spawn path below is responsible for re-creating it if and
                // when a snapshot becomes available. Rate-limited so a genuine
                // regression still leaves a breadcrumb without 85/sec spam.
                if (state != null)
                    state.loopCycleIndex = newCycleIndex;
                ParsekLog.VerboseRateLimited("Engine", $"reuse-skip-null-{index}",
                    $"ReusePrimaryGhostAcrossCycle: #{index} skipped — state.ghost is null (advanced cycle={newCycleIndex})", 1.0);
                return;
            }

            long previousCycle = state.loopCycleIndex;

            // Step 0: parity with SpawnGhost + DestroyGhost dedupe hygiene —
            // the prior-cycle end may have fired a trajectory completion event
            // (added to completedEventFired) and emitted a missing-audio-clip
            // warning (tracked in GhostVisualBuilder.ClearMissingAudioClipWarnings).
            // Today's destroy+spawn path clears both. Mirroring here so reuse
            // does not suppress the new cycle's legitimate completion event
            // nor over-dedupe the audio warning across cycles.
            completedEventFired.Remove(index);
            GhostVisualBuilder.ClearMissingAudioClipWarnings(state.ghost.name);

            // Step 1: pure-static per-cycle state reset. See
            // GhostPlaybackLogic.ResetForLoopCycle for the field-by-field
            // preservation table in
            // docs/dev/plan-406-ghost-reuse-loop-cycles.md.
            GhostPlaybackLogic.ResetForLoopCycle(state, newCycleIndex);

            // Step 1b: Unity-touching cleanup that cannot live inside the
            // pure-logic ResetForLoopCycle (xUnit JIT-verify would trip
            // SecurityException on Unity type references). These were
            // originally called from inside ResetForLoopCycle and moved out
            // after a review finding — ordering is preserved: restore RCS
            // emitters before downstream code re-inspects `rcsSuppressed`,
            // destroy fake canopy GameObjects so stale decouple stubs from
            // the prior cycle do not linger in the scene.
            GhostPlaybackLogic.RestoreAllRcsEmissions(state);
            GhostPlaybackLogic.DestroyAllFakeCanopies(state);

            // Step 2: re-activate any part GameObjects hidden by prior-cycle
            // events (decouple, destroy, inventory placed, pause-window
            // HideAllGhostParts). Without this, a Learstar-class ghost that
            // jettisoned stages on the previous cycle would start the new
            // cycle with those stages invisible.
            int reactivated = GhostPlaybackLogic.ReactivateGhostPartHierarchyForLoopRewind(state);

            // Step 3: restore the logical-part-id set to the full snapshot
            // baseline — prior-cycle decouple/destroy events pruned pids out
            // of this set. The same helper is used by the spawn path, so
            // semantics match identically.
            ConfigNode snapshot = GhostVisualBuilder.GetGhostSnapshot(traj);
            if (snapshot != null)
                state.logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot);

            // Step 4: re-apply the spawn-time visibility baseline for
            // inventory placements and compound parts. Inventory parts
            // whose first event is InventoryPartPlaced start hidden;
            // compound parts whose target is missing start hidden. Both
            // helpers are idempotent and safe to re-run.
            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(traj, state);
            GhostPlaybackLogic.RefreshCompoundPartVisibility(state);

            // Step 5: rebuild the reentry emission mesh to include the
            // now-active parts. RebuildReentryMeshes uses GetComponentsInChildren
            // (active only), so without this rebuild the mesh would stay
            // stale (only the post-decouple subset from the prior cycle)
            // until the new cycle fires its own decouple event. For the
            // Learstar A1 class (decouples precede reentry), the
            // user-visible impact is nil — but on recordings where the
            // new cycle re-enters atmosphere BEFORE the first decouple,
            // the stale mesh would make reentry FX emit from too few
            // verts. Skipped when reentryFxInfo is null (showcase/EVA or
            // #450 B3 still pending build) — RebuildReentryMeshes itself
            // early-returns on null anyway, the null check here just
            // avoids the MeshFilter scan.
            if (state.reentryFxInfo != null)
                GhostVisualBuilder.RebuildReentryMeshes(state.ghost, state.reentryFxInfo);

            // Step 5b: re-apply spawn-time module baselines that ResetForLoopCycle
            // cannot touch because they are Unity-touching: heat parts back to
            // cold, deployables re-stowed, jettison panels re-attached, and —
            // for zero-engine-event recordings (pure debris boosters) — the
            // orphan engine/audio auto-start. Fresh-spawn behaviour in
            // TryPopulateGhostVisuals does each of these; without this step the
            // second cycle onward would inherit the prior cycle's end-state
            // (hot + deployed + jettisoned + silent) instead of replaying from
            // the same baseline the first cycle did. See the helper's XML doc
            // for the per-branch rationale.
            GhostPlaybackLogic.ReapplySpawnTimeModuleBaselinesForLoopCycle(state, traj);

            // Step 6: re-prime the ghost at the new cycle's playbackUT.
            // PrimeLoadedGhostForPlaybackUT resets playbackIndex+partEventIndex
            // (already 0 post-reset, but it's cheap and idempotent),
            // positions the ghost, applies ApplyFrameVisuals with
            // allowTransientEffects: false so decouple puffs do not re-fire,
            // hides the ghost GO, and resets appearance tracking. Same call
            // the spawn path uses, so reuse and spawn converge on identical
            // post-condition state.
            PrimeLoadedGhostForPlaybackUT(index, traj, state, playbackUT);

            DiagnosticsState.health.ghostReusedAcrossCycleThisSession++;

            // Distinctive UTF-16 string for the DLL verification recipe.
            ParsekLog.VerboseRateLimited("Engine", $"reuse-{index}",
                $"Ghost #{index} \"{state.vesselName}\" ghost reused across loop cycle: " +
                $"from cycle={previousCycle} to cycle={newCycleIndex} " +
                $"(reactivated={reactivated} parts, " +
                $"reentryFxPendingBuild={state.reentryFxPendingBuild}, " +
                $"reentryFxInfo={(state.reentryFxInfo != null ? "present" : "null")})", 1.0);

            // Camera handshake: same event the spawn path fires so the host
            // can re-snap to the ghost's post-wrap pivot position. The pivot
            // Transform identity is preserved, but the underlying GameObject
            // has moved (PrimeLoadedGhostForPlaybackUT -> positioner.PositionLoop
            // placed it at loopUT on the new cycle), so the retarget is
            // essential for watch-mode follow.
            //
            // Suppressed when the caller already emitted a primary camera event
            // for this cycle boundary (destroyed-loop ExplosionHoldStart). The
            // watch handler ignores RetargetToNewGhost while in explosion hold
            // (watchedOverlapCycleIndex == -2), so firing it adds API noise
            // without changing camera behaviour and breaks the
            // "exactly one OnLoopCameraAction per cycle boundary" contract that
            // RuntimeTests.ExplosionAnchorPosition_BelowTerrain_ClampsBeforeWatchHold
            // exercises. The non-destroyed branch (ExplosionHoldEnd) DOES need
            // the retarget event so the watch handler swaps the bridge anchor
            // for the new cycle's pivot, so the suppression only applies when
            // the boundary fired a real explosion.
            if (emitRetargetEvent)
            {
                OnLoopCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = index,
                    Action = CameraActionType.RetargetToNewGhost,
                    NewCycleIndex = newCycleIndex,
                    GhostPivot = state.cameraPivot,
                    Trajectory = traj,
                    Flags = flags
                });
            }
            else
            {
                ParsekLog.VerboseRateLimited("Engine", $"reuse-suppress-retarget-{index}",
                    $"Ghost #{index} \"{state.vesselName}\" loop-cycle reuse: " +
                    $"RetargetToNewGhost suppressed (caller emitted primary boundary event; cycle={newCycleIndex})",
                    1.0);
            }
        }

        /// <summary>
        /// Spawns a timeline ghost for the given trajectory at the specified index.
        /// Builds the ghost mesh from the snapshot, or falls back to a sphere.
        /// Populates all ghost info dictionaries and reentry FX. Called for the
        /// first appearance of a ghost in a session; subsequent loop cycles go
        /// through <see cref="ReusePrimaryGhostAcrossCycle"/> instead.
        /// </summary>
        internal void SpawnGhost(int index, IPlaybackTrajectory traj, double playbackUT)
        {
            // Debris with no snapshot would produce a distracting green sphere — skip entirely (#232)
            if (traj.IsDebris && GhostVisualBuilder.GetGhostSnapshot(traj) == null)
            {
                ParsekLog.Verbose("Engine",
                    $"Ghost #{index} \"{traj.VesselName}\": debris with no snapshot, skipping");
                return;
            }

            var state = CreatePendingSpawnState(
                traj, playbackUT, PendingSpawnLifecycle.StandardEnter, default(TrajectoryPlaybackFlags));
            ghostStates[index] = state;

            GhostVisualLoadStatus status = EnsureGhostVisualsLoaded(
                index, traj, state, playbackUT, "direct spawn",
                forceImmediateBuild: true, resetCompletedEventDedup: true);
            if (status == GhostVisualLoadStatus.Failed)
                ghostStates.Remove(index);
        }

        private GhostPlaybackState CreatePendingSpawnState(
            IPlaybackTrajectory traj, double playbackUT,
            PendingSpawnLifecycle lifecycle, TrajectoryPlaybackFlags flags)
        {
            var state = new GhostPlaybackState
            {
                vesselName = traj?.VesselName ?? "Unknown",
                playbackIndex = 0,
                partEventIndex = 0,
                flagEventIndex = 0,
                pendingSpawnLifecycle = lifecycle,
                pendingSpawnFlags = flags
            };

            if (TryResolvePendingPlaybackInterpolation(traj, playbackUT, out InterpolationResult initialPlayback))
            {
                state.SetInterpolated(initialPlayback);
                string seededBodyName = initialPlayback.bodyName ?? "(null)";
                ParsekLog.Verbose("Engine", FormattableString.Invariant(
                    $"Pending spawn interpolation seed: vessel='{state.vesselName}' lifecycle={lifecycle} UT={playbackUT:F1} body='{seededBodyName}' altitude={initialPlayback.altitude:F1}"));
            }
            else
            {
                ParsekLog.Verbose("Engine", FormattableString.Invariant(
                    $"Pending spawn interpolation seed unavailable: vessel='{state.vesselName}' lifecycle={lifecycle} UT={playbackUT:F1}"));
            }

            return state;
        }

        private void QueueOrEmitGhostCreated(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPlaybackFlags flags)
        {
            if (OnGhostCreated == null)
                return;

            var evt = new GhostLifecycleEvent
            {
                Index = index,
                Trajectory = traj,
                State = state,
                Flags = flags
            };

            if (updateStopwatch.IsRunning)
                deferredCreatedEvents.Add(evt);
            else
                OnGhostCreated(evt);
        }

        private void FinalizePendingSpawnLifecycle(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, HeaviestSpawnBuildType buildType)
        {
            if (state == null)
                return;

            PendingSpawnLifecycle lifecycle = state.pendingSpawnLifecycle;
            if (lifecycle == PendingSpawnLifecycle.None)
                return;

            TrajectoryPlaybackFlags flags = state.pendingSpawnFlags;
            state.pendingSpawnLifecycle = PendingSpawnLifecycle.None;
            state.pendingSpawnFlags = default(TrajectoryPlaybackFlags);

            GhostPlaybackLogic.InitializeFlagVisibility(traj, state);
            loggedGhostEnter.Add(index);

            ParsekLog.VerboseRateLimited("Engine", $"spawn-{index}",
                $"Ghost #{index} \"{traj?.VesselName}\" spawned ({buildType.ToLogToken()}, " +
                $"parts={state.partTree?.Count ?? 0} engines={state.engineInfos?.Count ?? 0} " +
                $"rcs={state.rcsInfos?.Count ?? 0})", 1.0);

            QueueOrEmitGhostCreated(index, traj, state, flags);

            switch (lifecycle)
            {
                case PendingSpawnLifecycle.LoopEnter:
                    ParsekLog.VerboseRateLimited("Engine", $"enter-{index}",
                        $"Ghost ENTERED range: #{index} \"{traj.VesselName}\" at UT {playbackUT:F1} " +
                        $"(loop cycle={state.loopCycleIndex})");
                    OnLoopCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index,
                        Action = CameraActionType.RetargetToNewGhost,
                        NewCycleIndex = state.loopCycleIndex,
                        GhostPivot = state.cameraPivot,
                        Trajectory = traj,
                        Flags = flags
                    });
                    break;

                case PendingSpawnLifecycle.OverlapPrimaryEnter:
                    ParsekLog.VerboseRateLimited("Engine", $"enter-{index}",
                        $"Ghost ENTERED range: #{index} \"{traj.VesselName}\" cycle={state.loopCycleIndex} " +
                        $"at UT {playbackUT:F1} (overlap)");
                    OnOverlapCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index,
                        Action = CameraActionType.RetargetToNewGhost,
                        NewCycleIndex = state.loopCycleIndex,
                        GhostPivot = state.cameraPivot,
                        Trajectory = traj,
                        Flags = flags
                    });
                    break;
            }
        }

        private GhostVisualLoadStatus BuildGhostVisualsWithMetrics(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            bool resetCompletedEventDedup, bool forceImmediateBuild,
            out HeaviestSpawnBuildType buildType)
        {
            GhostVisualLoadStatus status = GhostVisualLoadStatus.Failed;
            buildType = HeaviestSpawnBuildType.None;
            // Bug #414: record per-spawn cost so the breakdown WARN surfaces whether a single
            // heavy ghost is the culprit vs many light ones. Take the pre-call tick count
            // before Start() resumes the stopwatch so the delta is this call only.
            long preCallTicks = spawnStopwatch.ElapsedTicks;
            spawnStopwatch.Start();
            frameSpawnCount++;
            if (resetCompletedEventDedup)
                completedEventFired.Remove(index);

            try
            {
                status = TryPopulateGhostVisuals(index, traj, state, forceImmediateBuild, out buildType);
                return status;
            }
            finally
            {
                spawnStopwatch.Stop();
                long deltaTicks = spawnStopwatch.ElapsedTicks - preCallTicks;
                if (deltaTicks > frameMaxSpawnTicks)
                {
                    frameMaxSpawnTicks = deltaTicks;
                    // Bug #450: this call is the new frame-heaviest — latch its sub-phase
                    // breakdown so the one-shot WARN can attribute whichever single spawn
                    // dominates. Copy the per-call deltas set by TryPopulateGhostVisuals;
                    // "other" is the residual that preserves the sum+residual = delta
                    // invariant the tests assert. The enum arrives directly through the
                    // out-param — no string-classification coupling between engine and
                    // diagnostics layer (prior revision had that brittleness).
                    frameHeaviestSpawnSnapshotResolveTicks = lastSpawnSnapshotResolveTicks;
                    frameHeaviestSpawnTimelineTicks = lastSpawnTimelineTicks;
                    frameHeaviestSpawnDictionariesTicks = lastSpawnDictionariesTicks;
                    frameHeaviestSpawnReentryTicks = lastSpawnReentryTicks;
                    long attributedTicks =
                        lastSpawnSnapshotResolveTicks
                        + lastSpawnTimelineTicks
                        + lastSpawnDictionariesTicks
                        + lastSpawnReentryTicks;
                    long otherTicks = deltaTicks - attributedTicks;
                    frameHeaviestSpawnOtherTicks = otherTicks < 0 ? 0 : otherTicks;
                    frameHeaviestSpawnBuildType = buildType;
                }
                if (status == GhostVisualLoadStatus.CompletedThisCall)
                    DiagnosticsState.health.ghostBuildsThisSession++;
            }
        }

        /// <summary>
        /// Bug #414: per-frame ghost spawn throttle. Returns true if the caller is allowed
        /// to spawn this frame; false if the cap is exhausted and the spawn must be deferred
        /// to a later frame. Increments the deferred counter and emits a rate-limited Verbose
        /// log on the deferred path. Watch-mode and loop-cycle-rebuild spawns do NOT call this
        /// — see plan-414-spawn-throttle.md for the call-site taxonomy.
        /// </summary>
        private bool TryReserveSpawnSlot(int index, string site)
        {
            if (GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame))
            {
                frameSpawnDeferred++;
                ParsekLog.VerboseRateLimited("Engine", "spawn-throttle",
                    $"Spawn throttled ({site}): #{index} deferred to next frame " +
                    $"(used {frameSpawnCount}/{GhostPlayback.MaxSpawnsPerFrame})", 1.0);
                return false;
            }
            return true;
        }

        // Bug #414 test hooks. The throttle gate and counters are private so UpdatePlayback
        // remains the only production path that touches them; tests need a narrow seam to
        // exercise the decision directly without constructing a full FrameContext.
        internal bool TryReserveSpawnSlotForTesting(int index, string site)
            => TryReserveSpawnSlot(index, site);
        internal void IncrementFrameSpawnCountForTesting()
            => frameSpawnCount++;
        internal int FrameSpawnCountForTesting => frameSpawnCount;
        internal int FrameSpawnDeferredForTesting => frameSpawnDeferred;
        // Bug #450 B3 test hooks. Narrow seam that lets tests drive the lazy-build
        // decision + throttle without constructing a full Unity scene (TryBuildReentryFx
        // needs a live GameObject, so tests here will stay on the counter/flag observation
        // side and defer the actual-build assertion to the in-game RuntimeTests fixture).
        internal void TryPerformLazyReentryBuildForTesting(
            int recIdx, GhostPlaybackState state, string vesselName,
            string bodyName, double altitude)
            => TryPerformLazyReentryBuild(recIdx, state, vesselName, bodyName, altitude);
        internal int FrameLazyReentryBuildCountForTesting => frameLazyReentryBuildCount;
        internal int FrameLazyReentryBuildDeferredForTesting => frameLazyReentryBuildDeferred;
        internal void SetFrameLazyReentryBuildCountForTesting(int value)
            => frameLazyReentryBuildCount = value;
        internal void ResetPerFrameCountersForTesting()
        {
            frameSpawnCount = 0;
            frameDestroyCount = 0;
            frameSpawnDeferred = 0;
            frameSkipBeforeActivation = 0;
            frameSkipAnchorMissing = 0;
            frameSkipLoopSyncFailed = 0;
            frameSkipParentLoopPaused = 0;
            frameSkipWarpHidden = 0;
            frameSkipVisualLoadFailed = 0;
            frameSkipNoRenderableData = 0;
            frameSkipPlaybackDisabled = 0;
            frameSkipExternalVesselSuppressed = 0;
            frameSkipSessionSuppressed = 0;
            frameMaxSpawnTicks = 0;
            // Bug #450: mirror the production per-frame reset at UpdatePlayback's head so
            // test seams see a clean heaviest-spawn latch.
            frameHeaviestSpawnSnapshotResolveTicks = 0;
            frameHeaviestSpawnTimelineTicks = 0;
            frameHeaviestSpawnDictionariesTicks = 0;
            frameHeaviestSpawnReentryTicks = 0;
            frameHeaviestSpawnOtherTicks = 0;
            frameHeaviestSpawnBuildType = HeaviestSpawnBuildType.None;
            lastSpawnSnapshotResolveTicks = 0;
            lastSpawnTimelineTicks = 0;
            lastSpawnDictionariesTicks = 0;
            lastSpawnReentryTicks = 0;
            buildSnapshotResolveStopwatch.Reset();
            buildTimelineStopwatch.Reset();
            buildDictionariesStopwatch.Reset();
            buildReentryFxStopwatch.Reset();
            // Bug #450 B3: mirror the production reset so tests see a clean cap each call.
            frameLazyReentryBuildCount = 0;
            frameLazyReentryBuildDeferred = 0;
            // Bug #460: mirror the production reset for the overlap-iteration counter so
            // tests that read FrameOverlapGhostIterationCountForTesting (or otherwise drive
            // multiple synthetic frames) see a clean counter each call.
            frameOverlapGhostIterationCount = 0;
        }

        internal int FrameOverlapGhostIterationCountForTesting => frameOverlapGhostIterationCount;

        // Bug #450 B2 test seams. These drive the exact loop/overlap lifecycle branches
        // for pending split-build states using MockTrajectory in xUnit, without requiring
        // a full UpdatePlayback pass or live KSP scene objects.
        internal void UpdateLoopingPlaybackForTesting(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            FrameContext ctx, bool suppressGhosts, bool suppressVisualFx)
            => UpdateLoopingPlayback(index, traj, flags, ctx, suppressGhosts, suppressVisualFx);

        internal void UpdateOverlapPlaybackForTesting(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            FrameContext ctx, GhostPlaybackState primaryState, bool suppressVisualFx,
            bool suppressOverlapGhosts = false,
            bool stopAfterSuppressOverlapGhosts = false)
        {
            if (!TryResolveLoopSchedule(
                    traj, ctx.autoLoopIntervalSeconds, index,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
                return;

            UpdateOverlapPlayback(index, traj, flags, ctx, primaryState,
                intervalSeconds, duration, playbackStartUT, scheduleStartUT,
                suppressVisualFx, suppressOverlapGhosts, stopAfterSuppressOverlapGhosts);
        }

        private GhostVisualLoadStatus TryPopulateGhostVisuals(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            bool forceImmediateBuild, out HeaviestSpawnBuildType buildType)
        {
            buildType = HeaviestSpawnBuildType.None;
            // Bug #450: reset per-call sub-phase deltas so a prior (shorter-lived) spawn's
            // attribution cannot leak into this call's heaviest-spawn latch. Each delta is
            // captured via pre/post Start-Stop subtraction below, matching the #414
            // spawnStopwatch pattern.
            lastSpawnSnapshotResolveTicks = 0;
            lastSpawnTimelineTicks = 0;
            lastSpawnDictionariesTicks = 0;
            lastSpawnReentryTicks = 0;

            if (state == null)
                return GhostVisualLoadStatus.Failed;

            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            GhostBuildResult buildResult = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;
            PendingGhostVisualBuild pendingBuild = state.pendingVisualBuild;
            // Retain the exact snapshot used to begin the split build so the final
            // dictionaries/logical-part reconstruction runs against the same node after
            // several yielded frames instead of re-resolving from traj mid-build.
            ConfigNode snapshot = pendingBuild?.snapshotNode;

            // Bug #450 sub-phase 1: snapshot resolve. Included even though it's trivial so
            // the breakdown reconciles — callers reading the log can sanity-check that
            // snapshot+timeline+dicts+reentry+other ≈ spawnMax.
            if (pendingBuild == null)
            {
                long snapshotResolvePre = buildSnapshotResolveStopwatch.ElapsedTicks;
                buildSnapshotResolveStopwatch.Start();
                snapshot = GhostVisualBuilder.GetGhostSnapshot(traj);
                buildSnapshotResolveStopwatch.Stop();
                lastSpawnSnapshotResolveTicks = buildSnapshotResolveStopwatch.ElapsedTicks - snapshotResolvePre;
            }

            // Bug #450 sub-phase 2: timeline build (the dominant suspect). Covers both the
            // snapshot path (BuildTimelineGhostFromSnapshot — part instantiation, engine FX
            // size-boost, audio wiring) and the sphere fallback (CreateGhostSphere). Bug
            // #450 B2 advances the snapshot path in bounded chunks across frames.
            long timelinePre = buildTimelineStopwatch.ElapsedTicks;
            buildTimelineStopwatch.Start();
            if (pendingBuild != null)
            {
                buildType = pendingBuild.buildType;
                long timelineBudgetTicks = forceImmediateBuild
                    ? long.MaxValue
                    : MaxSpawnTimelineBuildTicksPerAdvance;
                if (!GhostVisualBuilder.AdvanceTimelineGhostBuild(pendingBuild, timelineBudgetTicks))
                {
                    buildTimelineStopwatch.Stop();
                    lastSpawnTimelineTicks = buildTimelineStopwatch.ElapsedTicks - timelinePre;
                    return GhostVisualLoadStatus.Pending;
                }

                buildResult = GhostVisualBuilder.CompleteTimelineGhostBuild(pendingBuild, traj);
                state.pendingVisualBuild = null;
                if (buildResult != null)
                {
                    ghost = buildResult.root;
                    builtFromSnapshot = true;
                }
            }
            else if (snapshot != null)
            {
                HeaviestSpawnBuildType snapshotBuildType = traj.GhostVisualSnapshot != null
                    ? HeaviestSpawnBuildType.RecordingStartSnapshot
                    : HeaviestSpawnBuildType.VesselSnapshot;
                pendingBuild = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                    traj, snapshot, $"Parsek_Timeline_{index}", snapshotBuildType);
                if (pendingBuild != null)
                {
                    state.pendingVisualBuild = pendingBuild;
                    buildType = snapshotBuildType;
                    long timelineBudgetTicks = forceImmediateBuild
                        ? long.MaxValue
                        : MaxSpawnTimelineBuildTicksPerAdvance;
                    if (!GhostVisualBuilder.AdvanceTimelineGhostBuild(pendingBuild, timelineBudgetTicks))
                    {
                        buildTimelineStopwatch.Stop();
                        lastSpawnTimelineTicks = buildTimelineStopwatch.ElapsedTicks - timelinePre;
                        return GhostVisualLoadStatus.Pending;
                    }

                    buildResult = GhostVisualBuilder.CompleteTimelineGhostBuild(pendingBuild, traj);
                    state.pendingVisualBuild = null;
                    if (buildResult != null)
                    {
                        ghost = buildResult.root;
                        builtFromSnapshot = true;
                    }
                }
            }

            if (ghost == null)
            {
                ghost = GhostVisualBuilder.CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);
                buildType = HeaviestSpawnBuildType.SphereFallback;
            }
            buildTimelineStopwatch.Stop();
            lastSpawnTimelineTicks = buildTimelineStopwatch.ElapsedTicks - timelinePre;

            if (ghost == null)
                return GhostVisualLoadStatus.Failed;

            var cameraPivotObj = new GameObject("cameraPivot");
            cameraPivotObj.transform.SetParent(ghost.transform, false);

            var horizonProxyObj = new GameObject("horizonProxy");
            horizonProxyObj.transform.SetParent(cameraPivotObj.transform, false);

            state.vesselName = traj?.VesselName ?? state.vesselName ?? "Unknown";
            state.ghost = ghost;
            state.cameraPivot = cameraPivotObj.transform;
            state.horizonProxy = horizonProxyObj.transform;

            // Bug #450 sub-phase 3: dictionaries. Subtree map + logical-id set +
            // PopulateGhostInfoDictionaries + inventory/compound visibility are all
            // snapshot-walk consumers; grouped as one bucket because splitting at this
            // granularity isn't actionable for a Phase B fix.
            long dictionariesPre = buildDictionariesStopwatch.ElapsedTicks;
            buildDictionariesStopwatch.Start();
            state.partTree = GhostVisualBuilder.BuildPartSubtreeMap(snapshot);
            state.logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot);

            if (builtFromSnapshot)
            {
                state.materials = new List<Material>();
            }
            else
            {
                var m = ghost.GetComponent<Renderer>()?.material;
                state.materials = m != null ? new List<Material> { m } : new List<Material>();
            }

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, buildResult, traj);
            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(traj, state);
            GhostPlaybackLogic.RefreshCompoundPartVisibility(state);
            GhostPlaybackLogic.RecalculateCameraPivot(state);
            GhostVisualBuilder.AttachGhostAudioToWatchPivot(buildResult, state.cameraPivot);
            buildDictionariesStopwatch.Stop();
            lastSpawnDictionariesTicks = buildDictionariesStopwatch.ElapsedTicks - dictionariesPre;

            // Bug #450 sub-phase 4: reentry FX. Bracket the ENTIRE reentry decision
            // (classification + build or skip) in a single window so the O(n)
            // HasReentryPotential scan over trajectory points on non-orbital recordings
            // is attributed to the reentry bucket rather than leaking into "other". If
            // the scan dominated and we lumped it into "other", Phase B would see a big
            // residual and pick the wrong fix branch.
            // Gate reentry FX build: stationary part-showcase ghosts, EVA walks, and slow
            // suborbital hops below Mach 1.5 can never produce reentry visuals, so we skip
            // the mesh-combine + ParticleSystem + glow-material clone work entirely. With
            // hundreds of ghosts looping, rebuilding reentry FX on every loop-cycle
            // boundary was the dominant cost in the map-view perf tank (#406).
            long reentryPre = buildReentryFxStopwatch.ElapsedTicks;
            buildReentryFxStopwatch.Start();
            if (TrajectoryMath.HasReentryPotential(traj))
            {
                // Bug #450 B3: defer the ~7 ms TryBuildReentryFx work to the first
                // in-atmosphere frame. Ghosts that never enter atmosphere (orbital-only
                // fly-bys, pad ghosts that never launch, sub-400 m/s suborbital hops
                // that stay above the boundary) save the entire build cost. The lazy
                // build fires from inside UpdateReentryFx after its body lookup so
                // there is no duplicate FlightGlobals.Bodies.Find.
                state.reentryFxInfo = null;
                state.reentryFxPendingBuild = true;
                DiagnosticsState.health.reentryFxDeferredThisSession++;
            }
            else
            {
                state.reentryFxInfo = null;
                state.reentryFxPendingBuild = false;
                DiagnosticsState.health.reentryFxSkippedThisSession++;
                ParsekLog.VerboseRateLimited("ReentryFx", $"skip-{index}",
                    $"Skipped reentry FX build for ghost #{index} \"{traj.VesselName}\" " +
                    $"— trajectory peak speed below {TrajectoryMath.ReentryPotentialSpeedFloor:F0} m/s " +
                    $"and no orbit segments (cannot produce reentry visuals)", 5.0);
            }
            buildReentryFxStopwatch.Stop();
            lastSpawnReentryTicks = buildReentryFxStopwatch.ElapsedTicks - reentryPre;
            state.reentryMpb = new MaterialPropertyBlock();

            // Keep fresh builds hidden until the playback loop has positioned them at the
            // current UT. This prevents one-frame flashes at the recording-start snapshot.
            if (ghost.activeSelf)
                ghost.SetActive(false);
            state.deferVisibilityUntilPlaybackSync = true;
            ResetGhostAppearanceTracking(state);

            if (builtFromSnapshot && buildType == HeaviestSpawnBuildType.None)
            {
                buildType = traj.GhostVisualSnapshot != null
                    ? HeaviestSpawnBuildType.RecordingStartSnapshot
                    : HeaviestSpawnBuildType.VesselSnapshot;
            }
            return GhostVisualLoadStatus.CompletedThisCall;
        }

        private bool HandleHiddenGhostVisualState(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, float warpRate, double ghostDistance,
            bool overlapGhost, string hiddenReason)
        {
            if (state == null)
                return false;

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, shouldReduceFidelity: false);

            if (ShouldPrewarmHiddenGhost(traj, state, ghostDistance, playbackUT))
            {
                if (!HasLoadedGhostVisuals(state))
                {
                    // Bug #414: throttle prewarm spawns — the safest throttle victim since the
                    // ghost is hidden by distance LOD anyway. A 1-frame prewarm delay is invisible.
                    if (!TryReserveSpawnSlot(index, "hidden-tier-prewarm"))
                        return false;
                    GhostVisualLoadStatus prewarmStatus = EnsureGhostVisualsLoaded(
                        index, traj, state, playbackUT,
                        $"hidden-tier prewarm ({hiddenReason})");
                    if (prewarmStatus == GhostVisualLoadStatus.Failed)
                    {
                        CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                        return false;
                    }
                    if (prewarmStatus == GhostVisualLoadStatus.Pending)
                        return true;
                }

                if (HasLoadedGhostVisuals(state))
                {
                    PositionLoadedGhostAtPlaybackUT(index, traj, state, playbackUT);
                    if (state.ghost.activeSelf)
                        state.ghost.SetActive(false);
                    ResetGhostAppearanceTracking(state);
                    ApplyFrameVisuals(index, traj, state, playbackUT, warpRate,
                        skipPartEvents: false, suppressVisualFx: true, allowTransientEffects: false);
                    ParsekLog.VerboseRateLimited("Engine", $"prewarm-hidden-{index}",
                        $"Ghost #{index} \"{state.vesselName}\" hidden-tier prewarm active " +
                        $"({hiddenReason})", 1.0);
                    return true;
                }
            }

            if (HasLoadedGhostVisuals(state))
            {
                if (overlapGhost)
                    UnloadOverlapGhostVisuals(index, state, hiddenReason);
                else
                    UnloadGhostVisuals(index, state, hiddenReason);
            }

            ResetGhostAppearanceTracking(state);

            return false;
        }

        private GhostVisualLoadStatus EnsureGhostVisualsLoaded(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, string reason, bool forceImmediateBuild = false,
            bool resetCompletedEventDedup = false)
        {
            if (state == null)
                return GhostVisualLoadStatus.Failed;
            if (HasLoadedGhostVisuals(state))
                return GhostVisualLoadStatus.Ready;
            if (traj.IsDebris && GhostVisualBuilder.GetGhostSnapshot(traj) == null)
                return GhostVisualLoadStatus.Failed;

            GhostVisualLoadStatus status = BuildGhostVisualsWithMetrics(
                index, traj, state, resetCompletedEventDedup, forceImmediateBuild,
                out HeaviestSpawnBuildType buildType);
            if (status == GhostVisualLoadStatus.Pending)
            {
                if (state.pendingVisualBuild != null && !state.pendingVisualBuild.hasLoggedSplitYield)
                {
                    state.pendingVisualBuild.hasLoggedSplitYield = true;
                    ParsekLog.VerboseRateLimited("Engine", $"spawn-split-{index}",
                        $"Ghost #{index} \"{state.vesselName}\" build split across frames: " +
                        $"{state.pendingVisualBuild.nextPartIndex}/{state.pendingVisualBuild.partNodes.Length} " +
                        $"snapshot parts built ({reason}, budget={GhostPlayback.MaxSpawnBuildMillisecondsPerAdvance:F1}ms)",
                        1.0);
                }
                return status;
            }

            if (status != GhostVisualLoadStatus.CompletedThisCall)
                return status;

            PrimeLoadedGhostForPlaybackUT(index, traj, state, playbackUT);
            if (state.pendingSpawnLifecycle != PendingSpawnLifecycle.None)
                FinalizePendingSpawnLifecycle(index, traj, state, playbackUT, buildType);
            else
                ParsekLog.VerboseRateLimited("Engine", $"rebuild-{index}",
                    $"Ghost #{index} \"{state.vesselName}\" visuals rebuilt ({buildType.ToLogToken()}, {reason})", 1.0);

            return status;
        }

        private void PrimeLoadedGhostForPlaybackUT(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (state?.ghost == null)
                return;

            state.playbackIndex = 0;
            state.partEventIndex = 0;
            PositionLoadedGhostAtPlaybackUT(index, traj, state, playbackUT);
            ApplyFrameVisuals(index, traj, state, playbackUT, TimeWarp.CurrentRate,
                skipPartEvents: false, suppressVisualFx: false, allowTransientEffects: false);
            if (state.ghost.activeSelf)
                state.ghost.SetActive(false);
            ResetGhostAppearanceTracking(state);
        }

        private void SynchronizeLoadedGhostForWatch(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            state.playbackIndex = 0;
            state.partEventIndex = 0;
            // Bug #613 (PR #594 P1): clear retire signal before positioning;
            // gate visuals/activation/appearance on it after. Watch-sync runs
            // through the same relative-frame positioner as the per-frame
            // path, so it has the same vulnerability to a stale anchor pid.
            state.anchorRetiredThisFrame = false;
            PositionLoadedGhostAtPlaybackUT(index, traj, state, playbackUT);
            if (RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                    state.anchorRetiredThisFrame))
            {
                ApplyFrameVisuals(index, traj, state, playbackUT, TimeWarp.CurrentRate,
                    skipPartEvents: true, suppressVisualFx: true,
                    allowTransientEffects: false);
            }
            else
            {
                ApplyFrameVisuals(index, traj, state, playbackUT, TimeWarp.CurrentRate,
                    skipPartEvents: false, suppressVisualFx: false, allowTransientEffects: false);
                if (ShouldRestoreDeferredRuntimeFxState(
                        ActivateGhostVisualsIfNeeded(state),
                        suppressVisualFx: false))
                    GhostPlaybackLogic.RestoreDeferredRuntimeFxState(state);
                TrackGhostAppearance(index, traj, state, playbackUT, "watch-sync");
            }
        }

        private void PositionLoadedGhostAtPlaybackUT(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!HasLoadedGhostVisuals(state) || traj == null || positioner == null)
                return;

            bool hasPoints = traj.Points != null && traj.Points.Count >= 2;
            bool hasSurfaceData = traj.SurfacePos.HasValue;
            bool hasOrbitData = traj.HasOrbitSegments;

            if (hasPoints)
            {
                bool usedRelative = false;
                if (traj.TrackSections != null && traj.TrackSections.Count > 0)
                {
                    int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
                    if (sectionIdx >= 0 && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
                    {
                        positioner.InterpolateAndPositionRelative(index, traj, state,
                            playbackUT, suppressFx: true, traj.TrackSections[sectionIdx].anchorVesselId);
                        usedRelative = true;
                    }
                }

                if (!usedRelative)
                    positioner.InterpolateAndPosition(index, traj, state, playbackUT, suppressFx: true);
                return;
            }

            if (hasSurfaceData)
            {
                positioner.PositionAtSurface(index, traj, state);
                return;
            }

            if (traj.Points != null && traj.Points.Count > 0)
            {
                var firstPoint = traj.Points[0];

                if (ShouldPrimeSinglePointGhostFromOrbit(traj, playbackUT))
                {
                    positioner.PositionFromOrbit(index, traj, state, playbackUT);
                    return;
                }

                positioner.PositionAtPoint(index, traj, state, firstPoint);
                return;
            }

            if (hasOrbitData)
                positioner.PositionFromOrbit(index, traj, state, playbackUT);
        }

        internal static bool ShouldPrimeSinglePointGhostFromOrbit(
            IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj?.Points == null || traj.Points.Count != 1 || !traj.HasOrbitSegments)
                return false;

            // A single branch-boundary seed point should anchor the ghost only at the
            // exact recording start; once playback advances and an orbit segment is live,
            // orbit propagation should take over.
            return playbackUT > traj.Points[0].ut + 1e-6
                && TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, playbackUT).HasValue;
        }

        private static bool ActivateGhostVisualsIfNeeded(GhostPlaybackState state)
        {
            if (state?.ghost == null)
                return false;

            bool restoredDeferredState = state.deferVisibilityUntilPlaybackSync;

            if (!state.ghost.activeSelf)
                state.ghost.SetActive(true);

            state.deferVisibilityUntilPlaybackSync = false;
            return restoredDeferredState;
        }

        internal static bool ShouldRestoreDeferredRuntimeFxState(
            bool activatedDeferredState, bool suppressVisualFx)
        {
            return activatedDeferredState && !suppressVisualFx;
        }

        private static void ResetGhostAppearanceTracking(GhostPlaybackState state)
        {
            if (state == null)
                return;

            state.hadVisibleRenderersLastFrame = false;
        }

        internal static double ResolveGhostActivationStartUT(IPlaybackTrajectory traj)
        {
            return PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj);
        }

        internal static double ResolveVisiblePlaybackUT(
            IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (traj == null || state == null)
                return playbackUT;

            if (!state.deferVisibilityUntilPlaybackSync || state.appearanceCount != 0)
                return playbackUT;

            double activationStartUT = ResolveGhostActivationStartUT(traj);
            double activationLead = playbackUT - activationStartUT;
            if (activationLead <= 0.0 || activationLead > GhostPlayback.InitialVisibleFrameClampWindowSeconds)
                return playbackUT;

            return activationStartUT;
        }

        internal static bool TryResolvePendingPlaybackInterpolation(
            IPlaybackTrajectory traj, double playbackUT, out InterpolationResult result)
        {
            result = InterpolationResult.Zero;
            if (traj == null)
                return LogPendingPlaybackInterpolationUnresolved(
                    null, playbackUT, "null trajectory");

            if (traj.Points != null && traj.Points.Count >= 2)
            {
                bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, playbackUT);
                bool canUseOrbitPrecedence = TryResolvePendingOrbitSegmentInterpolation(
                    traj, playbackUT, applySubSurfaceGuard: true, out InterpolationResult orbitSegmentResult);
                if (surfaceSkip && canUseOrbitPrecedence)
                {
                    string vesselName = traj.VesselName ?? "Unknown";
                    ParsekLog.Verbose("Engine", FormattableString.Invariant(
                        $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} surface track section active, skipping orbit precedence"));
                }

                if (!surfaceSkip && canUseOrbitPrecedence)
                {
                    result = orbitSegmentResult;
                    return LogPendingPlaybackInterpolationResolved(
                        traj, playbackUT, result, "active orbit segment");
                }

                int cachedIndex = 0;
                if (!TrajectoryMath.InterpolatePoints(
                    traj.Points, ref cachedIndex, playbackUT,
                    out TrajectoryPoint before, out TrajectoryPoint after, out float t))
                {
                    if (!string.IsNullOrEmpty(before.bodyName))
                    {
                        result = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                        return LogPendingPlaybackInterpolationResolved(
                            traj, playbackUT, result, "before-start point fallback");
                    }
                }
                else
                {
                    bool useBeforePoint = t == 0f && before.ut == after.ut;
                    bool afterEndClamp = playbackUT > after.ut;
                    string bodyName = useBeforePoint ? before.bodyName : after.bodyName;
                    if (!string.IsNullOrEmpty(bodyName))
                    {
                        result = new InterpolationResult(
                            useBeforePoint ? before.velocity : Vector3.Lerp(before.velocity, after.velocity, t),
                            bodyName,
                            useBeforePoint
                                ? before.altitude
                                : TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
                        bool crossBodyTransition =
                            !useBeforePoint
                            && !string.Equals(before.bodyName, after.bodyName, StringComparison.Ordinal);
                        string pointSource = useBeforePoint
                            ? "same-UT point segment"
                            : afterEndClamp
                                ? "point after-end clamp"
                                : crossBodyTransition
                                    ? FormattableString.Invariant(
                                        $"cross-body point transition {before.bodyName ?? "(null)"}->{after.bodyName ?? "(null)"} (using upper-point body)")
                                : "point interpolation";
                        return LogPendingPlaybackInterpolationResolved(
                            traj, playbackUT, result, pointSource);
                    }
                }
            }

            if (traj.SurfacePos.HasValue && !string.IsNullOrEmpty(traj.SurfacePos.Value.body))
            {
                SurfacePosition surface = traj.SurfacePos.Value;
                result = new InterpolationResult(Vector3.zero, surface.body, surface.altitude);
                return LogPendingPlaybackInterpolationResolved(
                    traj, playbackUT, result, "surface metadata");
            }

            if (traj.Points != null && traj.Points.Count == 1)
            {
                if (ShouldPrimeSinglePointGhostFromOrbit(traj, playbackUT)
                    && TryResolvePendingOrbitSegmentInterpolation(
                        traj, playbackUT, applySubSurfaceGuard: false, out result))
                {
                    return LogPendingPlaybackInterpolationResolved(
                        traj, playbackUT, result, "single-point orbit segment");
                }

                TrajectoryPoint point = traj.Points[0];
                if (!string.IsNullOrEmpty(point.bodyName))
                {
                    result = new InterpolationResult(point.velocity, point.bodyName, point.altitude);
                    return LogPendingPlaybackInterpolationResolved(
                        traj, playbackUT, result, "single-point fallback");
                }
            }

            if (TryResolvePendingOrbitSegmentInterpolation(
                traj, playbackUT, applySubSurfaceGuard: false, out result))
            {
                return LogPendingPlaybackInterpolationResolved(
                    traj, playbackUT, result, "fallback orbit segment");
            }

            if (!string.IsNullOrEmpty(traj.EndpointBodyName))
            {
                result = new InterpolationResult(Vector3.zero, traj.EndpointBodyName, 0.0);
                return LogPendingPlaybackInterpolationResolved(
                    traj, playbackUT, result, "endpoint body fallback");
            }

            return LogPendingPlaybackInterpolationUnresolved(
                traj, playbackUT, "no points, surface metadata, orbit segment, or endpoint body");
        }

        private static bool LogPendingPlaybackInterpolationResolved(
            IPlaybackTrajectory traj, double playbackUT, InterpolationResult result, string source)
        {
            string vesselName = traj?.VesselName ?? "Unknown";
            string bodyName = result.bodyName ?? "(null)";
            ParsekLog.Verbose("Engine", FormattableString.Invariant(
                $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} resolved from {source} body='{bodyName}' altitude={result.altitude:F1}"));
            return true;
        }

        private static bool LogPendingPlaybackInterpolationUnresolved(
            IPlaybackTrajectory traj, double playbackUT, string reason)
        {
            string vesselName = traj?.VesselName ?? "Unknown";
            ParsekLog.Verbose("Engine", FormattableString.Invariant(
                $"Pending playback interpolation: vessel='{vesselName}' UT={playbackUT:F1} unresolved ({reason})"));
            return false;
        }

        private static bool TryResolvePendingOrbitSegmentInterpolation(
            IPlaybackTrajectory traj, double playbackUT, bool applySubSurfaceGuard,
            out InterpolationResult result)
        {
            result = InterpolationResult.Zero;
            if (traj?.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(traj.OrbitSegments, playbackUT);
            if (!seg.HasValue || string.IsNullOrEmpty(seg.Value.bodyName))
                return false;

            if (applySubSurfaceGuard)
            {
                double bodyRadius = ResolvePendingOrbitBodyRadius(seg.Value.bodyName);
                double absSma = System.Math.Abs(seg.Value.semiMajorAxis);
                if (absSma < bodyRadius * 0.9)
                    return false;
            }

            result = new InterpolationResult(Vector3.zero, seg.Value.bodyName, 0.0);
            return true;
        }

        private static double ResolvePendingOrbitBodyRadius(string bodyName)
        {
            Func<string, double?> resolver = PendingOrbitBodyRadiusResolverForTesting;
            if (resolver != null)
            {
                double? resolved = resolver(bodyName);
                if (resolved.HasValue && !double.IsNaN(resolved.Value) && !double.IsInfinity(resolved.Value) && resolved.Value > 0)
                    return resolved.Value;
            }

            try
            {
                CelestialBody segBody = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (segBody != null)
                    return segBody.Radius;
            }
            catch (TypeInitializationException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }

            return DefaultPendingOrbitBodyRadiusMeters;
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
            ParsekLog.Verbose("GhostAppearance",
                $"Ghost #{index} \"{traj?.VesselName ?? state.vesselName ?? "unknown"}\" " +
                $"appearance#{state.appearanceCount} reason={reason} " +
                $"ut={playbackUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"requestedUT={requestedUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"firstFrameClamped={(firstFrameClamped ? "T" : "F")} " +
                $"activationStart={activationStartUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"activationLead={(playbackUT - activationStartUT).ToString("F2", CultureInfo.InvariantCulture)} " +
                $"zone={state.currentZone} dist={state.lastDistance.ToString("F0", CultureInfo.InvariantCulture)}m " +
                $"{activeSectionSummary} " +
                $"root={FormatVector3d(rootPos)} rootRot={FormatQuaternion(rootRot)} " +
                $"boundsCenter={FormatVector3d(boundsCenter)} bounds-root={FormatVector3d(boundsRootDelta)} " +
                $"firstVisiblePart={firstVisiblePartLabel}:{FormatVector3d(firstVisiblePartPos)} " +
                $"part-root={FormatVector3d(firstVisiblePartRootDelta)} " +
                $"{recordingStartSummary} " +
                $"snapshotCoM={(hasSnapshotCoM ? FormatVector3(snapshotCoM) : "none")} " +
                $"{rootPartSummary}{rootPartWorldSummary} visibleRenderers={rendererCount}");
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
                ? $" anchorPid={section.anchorVesselId}"
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
            uint anchorVesselId = 0;

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, firstPoint.ut);
                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                {
                    TrackSection section = traj.TrackSections[sectionIdx];
                    frame = section.referenceFrame;
                    anchorVesselId = section.anchorVesselId;
                }
            }

            if (frame == ReferenceFrame.Relative)
            {
                Vector3d offset = new Vector3d(firstPoint.latitude, firstPoint.longitude, firstPoint.altitude);
                return
                    $"recordingStart@{firstPoint.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"frame=Relative offset={FormatVector3d(offset)} anchorPid={anchorVesselId}";
            }

            string rawSummary =
                $"recordingStart@{firstPoint.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"frame={frame} body={firstPoint.bodyName ?? "unknown"} " +
                $"lla={FormatVector3d(new Vector3d(firstPoint.latitude, firstPoint.longitude, firstPoint.altitude))}";

            CelestialBody body = FlightGlobals.GetBodyByName(firstPoint.bodyName);
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

        private void UnloadGhostVisuals(int index, GhostPlaybackState state, string reason)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            ParsekLog.VerboseRateLimited("Engine", $"unload-{index}",
                $"Ghost #{index} \"{state.vesselName}\" visuals unloaded ({reason})", 1.0);
            DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
            state.ClearLoadedVisualReferences();
        }

        private void UnloadOverlapGhostVisuals(int index, GhostPlaybackState state, string reason)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            ParsekLog.VerboseRateLimited("Engine", $"overlap-unload-{index}-{state.loopCycleIndex}",
                $"Ghost #{index} cycle={state.loopCycleIndex} visuals unloaded ({reason})", 1.0);
            DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
            state.ClearLoadedVisualReferences();
        }

        /// <summary>
        /// Destroys materials, engine/RCS particle systems, reentry FX, ghost GameObject,
        /// and fake canopies for a single ghost playback state.
        /// Does NOT remove from any dictionary — caller handles collection bookkeeping.
        /// </summary>
        internal void DestroyGhostResources(GhostPlaybackState state, bool lingerParticleSystems = true)
        {
            if (state.pendingVisualBuild != null)
            {
                GhostVisualBuilder.DestroyPendingTimelineGhostBuild(state.pendingVisualBuild);
                state.pendingVisualBuild = null;
            }

            if (state.materials != null)
            {
                for (int i = 0; i < state.materials.Count; i++)
                {
                    if (state.materials[i] != null)
                        UnityEngine.Object.Destroy(state.materials[i]);
                }
            }

            // Detach active engine/RCS particle systems so smoke trails linger (#107).
            // Stopped systems with no live particles are destroyed immediately.
            if (state.engineInfos != null)
            {
                foreach (var info in state.engineInfos.Values)
                {
                    if (lingerParticleSystems)
                        GhostPlaybackLogic.DetachAndLingerParticleSystems(info.particleSystems, info.kspEmitters);
                    else
                        GhostPlaybackLogic.StopAndClearParticleSystems(info.particleSystems, info.kspEmitters);
                }
            }

            if (state.rcsInfos != null)
            {
                foreach (var info in state.rcsInfos.Values)
                {
                    if (lingerParticleSystems)
                        GhostPlaybackLogic.DetachAndLingerParticleSystems(info.particleSystems, info.kspEmitters);
                    else
                        GhostPlaybackLogic.StopAndClearParticleSystems(info.particleSystems, info.kspEmitters);
                }
            }

            // Stop all ghost audio sources before destroying the GO hierarchy.
            int audioStopped = 0;
            if (state.audioInfos != null)
            {
                foreach (var info in state.audioInfos.Values)
                {
                    if (info.audioSource != null && info.audioSource.isPlaying)
                    {
                        info.audioSource.Stop();
                        audioStopped++;
                    }
                }
            }
            if (state.oneShotAudio?.audioSource != null && state.oneShotAudio.audioSource.isPlaying)
            {
                state.oneShotAudio.audioSource.Stop();
                audioStopped++;
            }
            if (audioStopped > 0)
                ParsekLog.Verbose("GhostAudio", $"Cleanup: stopped {audioStopped} audio source(s) for '{state.vesselName}'");

            DestroyReentryFxResources(state.reentryFxInfo);

            if (state.ghost != null)
                UnityEngine.Object.Destroy(state.ghost);

            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
        }

        private void DestroyGhostResourcesWithMetrics(GhostPlaybackState state, bool lingerParticleSystems = true)
        {
            if (state == null)
                return;

            if (!updateStopwatch.IsRunning)
            {
                DestroyGhostResources(state, lingerParticleSystems);
                return;
            }

            destroyStopwatch.Start();
            try
            {
                DestroyGhostResources(state, lingerParticleSystems);
            }
            finally
            {
                destroyStopwatch.Stop();
            }
        }

        /// <summary>
        /// Destroys reentry FX resources (cloned materials, generated texture, emission mesh).
        /// </summary>
        internal void DestroyReentryFxResources(ReentryFxInfo info)
        {
            if (info == null) return;
            if (info.allClonedMaterials != null)
                for (int i = 0; i < info.allClonedMaterials.Count; i++)
                    if (info.allClonedMaterials[i] != null)
                        UnityEngine.Object.Destroy(info.allClonedMaterials[i]);
            if (info.generatedTexture != null)
                UnityEngine.Object.Destroy(info.generatedTexture);
            if (info.combinedEmissionMesh != null)
                UnityEngine.Object.Destroy(info.combinedEmissionMesh);
        }

        /// <summary>
        /// Despawns a single primary timeline ghost. Destroys its resources and
        /// removes it from ghostStates and loopPhaseOffsets.
        /// </summary>
        internal void DestroyGhost(int index, IPlaybackTrajectory traj = null,
            TrajectoryPlaybackFlags flags = default, string reason = null)
        {
            frameDestroyCount++;

            GhostPlaybackState state;
            if (!ghostStates.TryGetValue(index, out state))
                return;

            string name = state?.vesselName ?? traj?.VesselName ?? "Unknown";
            ParsekLog.VerboseRateLimited("Engine", $"destroy-{index}",
                $"Ghost #{index} \"{name}\" destroyed ({reason ?? "unknown"})", 1.0);

            // Capture ghost root name before the GO is destroyed so we can clear the
            // per-ghost "AudioClip not found" dedupe set (#421). A fresh spawn of this
            // index can then warn once more if the clip is still missing.
            string ghostRootName = state?.ghost != null ? state.ghost.name : null;

            // Fire before destroy so subscribers can read state
            OnGhostDestroyed?.Invoke(new GhostLifecycleEvent
            {
                Index = index, Trajectory = traj, State = state, Flags = flags
            });

            DestroyGhostResourcesWithMetrics(state);

            ghostStates.Remove(index);
            loopPhaseOffsets.Remove(index);
            completedEventFired.Remove(index);
            GhostVisualBuilder.ClearMissingAudioClipWarnings(ghostRootName);
            DiagnosticsState.health.ghostDestroysThisSession++;
        }

        /// <summary>
        /// Destroys a single overlap ghost's resources. Does NOT remove from any collection.
        /// </summary>
        internal void DestroyOverlapGhostState(GhostPlaybackState state)
        {
            if (state == null) return;
            ParsekLog.VerboseRateLimited("Engine", "destroy-overlap",
                $"Destroying overlap ghost cycle={state.loopCycleIndex}", 2.0);
            DestroyGhostResourcesWithMetrics(state);
        }

        /// <summary>
        /// Destroys all overlap ghosts for a single recording index.
        /// Returns true if the given recIdx matched the watched recording's overlap tracking
        /// (caller should reset camera state).
        /// </summary>
        internal bool DestroyAllOverlapGhosts(int recIdx)
        {
            List<GhostPlaybackState> list;
            if (!overlapGhosts.TryGetValue(recIdx, out list)) return false;
            if (list.Count > 0)
                ParsekLog.Verbose("Engine",
                    $"Destroying all {list.Count} overlap ghost(s) for recording #{recIdx}");

            for (int i = 0; i < list.Count; i++)
                DestroyOverlapGhostState(list[i]);
            list.Clear();

            // Return true so the caller (ParsekFlight) can reset watch mode camera state
            // if this recording was being watched. Engine does not know about watch mode.
            return true;
        }

        /// <summary>
        /// Pauses audio on all active ghost states (primary + overlap). Called when the KSP
        /// pause menu opens (ESC). Preserves playback position via AudioSource.Pause() so
        /// UnpauseAllGhostAudio can resume exactly where it left off.
        /// </summary>
        internal void PauseAllGhostAudio()
        {
            int pausedPrimary = 0, pausedOverlap = 0;
            foreach (var kvp in ghostStates)
            {
                GhostPlaybackLogic.PauseAllAudio(kvp.Value);
                pausedPrimary++;
            }
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    GhostPlaybackLogic.PauseAllAudio(kvp.Value[i]);
                    pausedOverlap++;
                }
            }
            ParsekLog.Verbose("GhostAudio",
                $"PauseAllGhostAudio: paused {pausedPrimary} primary + {pausedOverlap} overlap ghost(s)");
        }

        /// <summary>
        /// Resumes audio on all active ghost states. Called when the KSP pause menu closes.
        /// </summary>
        internal void UnpauseAllGhostAudio()
        {
            int resumedPrimary = 0, resumedOverlap = 0;
            foreach (var kvp in ghostStates)
            {
                GhostPlaybackLogic.UnpauseAllAudio(kvp.Value);
                resumedPrimary++;
            }
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    GhostPlaybackLogic.UnpauseAllAudio(kvp.Value[i]);
                    resumedOverlap++;
                }
            }
            ParsekLog.Verbose("GhostAudio",
                $"UnpauseAllGhostAudio: resumed {resumedPrimary} primary + {resumedOverlap} overlap ghost(s)");
        }

        /// <summary>
        /// Checks whether the recording ended with destruction and spawns an explosion FX if so.
        /// Takes warpRate as parameter (engine does not read KSP globals directly).
        /// </summary>
        internal void TriggerExplosionIfDestroyed(GhostPlaybackState state, IPlaybackTrajectory traj,
            int recIdx, float warpRate)
        {
            if (state == null)
            {
                ParsekLog.Verbose("Engine", $"TriggerExplosionIfDestroyed: ghost #{recIdx} — skipped (state is null)");
                return;
            }
            if (!GhostPlaybackLogic.ShouldTriggerExplosion(state.explosionFired, traj.TerminalStateValue,
                    state.ghost != null, traj.VesselName, recIdx))
                return;

            if (GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate))
            {
                state.explosionFired = true;
                GhostPlaybackLogic.HideAllGhostParts(state);
                ParsekLog.VerboseRateLimited("Engine", $"explosion-suppress-{recIdx}",
                    $"Explosion suppressed for ghost #{recIdx} \"{traj.VesselName}\": " +
                    $"warp rate {warpRate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}x > " +
                    $"{WarpThresholds.FxSuppress}x");
                return;
            }

            state.explosionFired = true;

            Vector3 worldPos = state.ghost.transform.position;
            if (positioner != null
                && !positioner.TryResolveExplosionAnchorPosition(recIdx, traj, state, out worldPos))
            {
                worldPos = state.ghost.transform.position;
            }
            // Write the shared explosion anchor back onto the ghost root before the
            // loop/overlap boundary event emitters run; the subsequent camera-hold
            // and restart payloads both read this transform as their explosion site.
            state.ghost.transform.position = worldPos;
            float vesselLength = state.reentryFxInfo != null
                ? state.reentryFxInfo.vesselLength
                : GhostVisualBuilder.ComputeGhostLength(state.ghost);
            double power = Mathf.Clamp01(vesselLength / 20f);
            ParsekLog.VerboseRateLimited("ExplosionFx", $"stock-explode-{recIdx}",
                $"Stock FXMonger.Explode for ghost #{recIdx} \"{traj.VesselName}\" " +
                $"at ({worldPos.x:F1},{worldPos.y:F1},{worldPos.z:F1}) " +
                $"vesselLength={vesselLength:F1}m power={power.ToString("F2", CultureInfo.InvariantCulture)}",
                10.0);

            if (!GhostVisualBuilder.TryTriggerStockExplosionFx(worldPos, power, out string stockFxFailure))
            {
                ParsekLog.Warn("ExplosionFx",
                    $"FXMonger.Explode did not queue stock FX for ghost #{recIdx} \"{traj.VesselName}\"; " +
                    $"falling back to custom FX: {stockFxFailure}");
                GhostVisualBuilder.SpawnExplosionFx(worldPos, vesselLength);
            }

            GhostPlaybackLogic.HideAllGhostParts(state);
            ParsekLog.VerboseRateLimited("Engine", "parts-hidden-explosion",
                $"Ghost #{recIdx} parts hidden after explosion");
        }

        /// <summary>
        /// Clean up all engine-owned ghost state. Destroys all ghost GOs first,
        /// then clears all collections. Fires OnAllGhostsDestroying so policy
        /// and host can clear their own state.
        /// </summary>
        internal void DestroyAllGhosts()
        {
            ParsekLog.Info("Engine", $"DestroyAllGhosts: clearing {ghostStates.Count} primary + {overlapGhosts.Count} overlap entries");

            // Fire event BEFORE clearing so subscribers can inspect ghost state if needed
            OnAllGhostsDestroying?.Invoke();

            // Destroy all primary ghost GOs
            var keys = new List<int>(ghostStates.Keys);
            foreach (int key in keys)
            {
                if (ghostStates.TryGetValue(key, out var state))
                    DestroyGhostResources(state);
            }

            // Destroy all overlap ghost GOs
            foreach (var kvp in overlapGhosts)
            {
                for (int i = 0; i < kvp.Value.Count; i++)
                    DestroyOverlapGhostState(kvp.Value[i]);
            }

            // Clear all engine state
            ghostStates.Clear();
            overlapGhosts.Clear();
            autoLoopLaunchSchedules.Clear();
            autoLoopQueueScratch.Clear();
            loopPhaseOffsets.Clear();
            loadedAnchorVessels.Clear();
            loggedGhostEnter.Clear();
            loggedReshow.Clear();
            completedEventFired.Clear();
            earlyDestroyedDebrisCompleted.Clear();

        }

        /// <summary>
        /// Reindex all engine dictionaries after a recording is deleted.
        /// Keys above the removed index shift down by 1.
        /// </summary>
        internal void ReindexAfterDelete(int removedIndex)
        {
            autoLoopLaunchSchedules.Clear();
            autoLoopQueueScratch.Clear();
            ReindexDict(ghostStates, removedIndex);
            ReindexDict(overlapGhosts, removedIndex);
            ReindexDict(loopPhaseOffsets, removedIndex);
            ReindexSet(loggedGhostEnter, removedIndex);
            ReindexSet(loggedReshow, removedIndex);
            ReindexSet(completedEventFired, removedIndex);
            ReindexSet(earlyDestroyedDebrisCompleted, removedIndex);
        }

        private static void ReindexDict<T>(Dictionary<int, T> dict, int removedIndex)
        {
            var keys = new List<int>(dict.Keys);
            keys.Sort();
            foreach (int key in keys)
            {
                if (key > removedIndex)
                {
                    var value = dict[key];
                    dict.Remove(key);
                    dict[key - 1] = value;
                }
            }
        }

        private static void ReindexSet(HashSet<int> set, int removedIndex)
        {
            var items = new List<int>(set);
            set.Clear();
            foreach (int item in items)
            {
                if (item > removedIndex)
                    set.Add(item - 1);
                else if (item < removedIndex)
                    set.Add(item);
                // item == removedIndex is dropped
            }
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Release all resources. Called from host's OnDestroy().
        /// </summary>
        internal void Dispose()
        {
            DestroyAllGhosts();
            ParsekLog.Info("Engine", "GhostPlaybackEngine disposed");
        }

        #endregion
    }
}
