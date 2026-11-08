using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    internal readonly struct GhostAnchorCandidate
    {
        public readonly int Index;
        public readonly string RecordingId;
        public readonly bool PositionedThisFrame;

        internal GhostAnchorCandidate(int index, string recordingId, bool positionedThisFrame)
        {
            Index = index;
            RecordingId = recordingId ?? string.Empty;
            PositionedThisFrame = positionedThisFrame;
        }
    }

    /// <summary>
    /// Core ghost playback engine.
    /// Consumes <see cref="IPlaybackTrajectory"/> data plus rendering collaborators to manage
    /// ghost visuals, per-frame positioning, part-event playback, loop/overlap playback,
    /// zone transitions, and soft caps.
    ///
    /// Concrete recording/tree policy stays outside this class; trajectory knowledge crosses
    /// this boundary only through the interface surface.
    /// </summary>
    internal partial class GhostPlaybackEngine
    {
        private readonly IGhostPositioner positioner;
        private bool ghostAudioPaused;

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

        // Chain-loop unit descriptors for THIS frame (host-detected, opaque to the engine).
        // Pushed once per frame via SetLoopUnits before UpdatePlayback, exactly the lifetime of
        // autoLoopLaunchSchedules. Empty (LoopUnitSet.Empty) means no consecutive auto-loop chain
        // members exist, which keeps the whole chain-loop-unit feature dormant. The engine never
        // reads Recording / ChainId — it consumes the index-keyed descriptors as opaque sets.
        private GhostPlaybackLogic.LoopUnitSet currentLoopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;

        // Last (cycleIndex, liveMemberIdx, watchedRendering) the shared mission clock resolved for
        // each unit owner. Under the concurrent model every member renders independently when the
        // clock is in its own window (no shared mutable state per design D3); this is purely for the
        // cycle-wrap / camera-handoff diagnostic + watch retarget: the FIRST member of a unit to run
        // this frame observes the transition and acts once. liveMemberIdx = the in-window member with
        // the highest StartUT (the camera-follow target, newest segment); -1 = nothing live (tail /
        // before span). Keyed by owner index; self-heals each frame since only members write it.
        private readonly Dictionary<int, (long cycle, int liveMemberIdx, bool watchedRendering)> lastUnitSelection
            = new Dictionary<int, (long, int, bool)>();

        // Constants live in ParsekConfig.cs — see GhostPlayback.* for the
        // concurrency caps, per-frame throttles, hold windows, and prewarm
        // buffers used below.
        private static readonly long MaxSpawnTimelineBuildTicksPerAdvance =
            (long)(Stopwatch.Frequency * (GhostPlayback.MaxSpawnBuildMillisecondsPerAdvance / 1000.0));

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
        private int spawnReserveAttemptCountForTesting;
        private int visualLoadAttemptCountForTesting;
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
        private int frameSkipSupersededByRelation;
        private int frameSkipRewindRetired;
        private int frameSkipSpawnSuppressedDeadOnArrival;
        private int frameSkipAnchorRotationUnreliable;
        private int frameSkipAnchorReFlyUnstable;
        private int frameSkipChainShadowed;
        private int frameSkipChainBridgeHeld;
        // Chain-loop unit follower: per-frame count of unit members hidden because the shared span
        // clock selected a different member (or a gap, or the clock failed). See UpdateUnitMemberPlayback.
        private int frameSkipMissionLoopUnitInactive;
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
        private readonly List<GhostLifecycleEvent> deferredSpawnPendingEvents = new List<GhostLifecycleEvent>();
        // Stale past-end cleanups postponed until the slot's completion event has reached the
        // policy (run in the same frame, right after FireDeferredFrameEvents). Reused per frame.
        private readonly List<DeferredStaleCleanup> staleCleanupsAwaitingCompletionDelivery =
            new List<DeferredStaleCleanup>();

        /// <summary>
        /// One postponed stale cleanup. The chain inputs are the ones the loop computed for the
        /// slot, so the chain bridge-hold decision sees the same continuation state it would
        /// have seen in the loop (a continuation spawned later in the same loop pass must not
        /// release the head a frame early).
        /// </summary>
        private struct DeferredStaleCleanup
        {
            public PlaybackCompletedEvent Completion;
            public int ChainNextIndex;
            public bool ContinuationHasActiveGhost;
        }

        // Dedup: prevent completed events from firing every frame for past-end recordings.
        // Rewind safety: DestroyAllGhosts() clears this set, and rewind always calls
        // DestroyAllGhosts (via ParsekFlight cleanup path), so completedEventFired
        // is guaranteed to be empty when playback restarts after a rewind.
        private readonly HashSet<int> completedEventFired = new HashSet<int>();
        // One-shot latch for the loop first-run spawn (TryQueueLoopFirstRunSpawn). Kept apart
        // from completedEventFired because a loop member's ghost is destroyed and rebuilt every
        // cycle, and every DestroyGhost clears completedEventFired. Cleared only when the
        // playhead returns before the recording (a rewind), on full teardown and never per cycle.
        private readonly HashSet<int> loopFirstRunSpawnFired = new HashSet<int>();
        // Destroyed debris can complete before EndUT when the recording captured a clear
        // destructive part event. Keep those indices suppressed until a rewind/reset so
        // they do not respawn while the original recording window is still in range.
        private readonly HashSet<int> earlyDestroyedDebrisCompleted = new HashSet<int>();

        /// <summary>
        /// Per-unit-member seam-marker cursor + dedup (design-dock-event-graph.md 7.3 / 14). The
        /// rules live in the pure <see cref="LoopSeamMarkerRuntime"/> so they stay headlessly
        /// testable (this class cannot be constructed without a live host). Reset on a unit-set swap
        /// (<see cref="SetLoopUnits"/>) and on full teardown (<see cref="DestroyAllGhosts"/>), so no
        /// cursor or dedup key ever outlives the committed-index space it was keyed in.
        /// </summary>
        private readonly LoopSeamMarkerRuntime seamMarkerRuntime = new LoopSeamMarkerRuntime();

        private enum GhostVisualLoadStatus : byte
        {
            Failed = 0,
            Pending = 1,
            Ready = 2,
            CompletedThisCall = 3
        }

        // Outcome of the first-spawn block (R3) extracted from RenderInRangeGhost.
        // The helper owns the block body; the caller maps the outcome to the
        // corresponding return / fall-through so no return is buried in the helper.
        private enum FirstSpawnOutcome : byte
        {
            // Spawn produced a ready state — fall through to the defensive
            // null-check and the zone-rendering pipeline (original fall-out-of-block).
            Proceed = 0,
            // Caller should `return true;` (original Pending path).
            ReturnTrue = 1,
            // Caller should `return false;` (original dead-on-arrival / throttle /
            // visual-load-Failed paths).
            ReturnFalse = 2
        }

        #endregion

        #region Lifecycle events

        /// <summary>
        /// Fired when a PRIMARY ghost playback state REGISTERS in <c>ghostStates</c> (pending spawn
        /// seeded, build not Failed), BEFORE the time-sliced mesh build completes.
        /// <see cref="OnGhostCreated"/> keeps firing at spawn FINALIZE (mesh ready) for the
        /// mesh-dependent concerns (camera auto-follow); this earlier signal exists for
        /// mesh-INDEPENDENT consumers - the flight map presence (ProtoVessel + orbit line) used to
        /// ride OnGhostCreated, so a far-distance-tier multi-part ghost's ~4ms/frame split build
        /// left its map orbit line missing for tens of seconds after a vessel switch (2026-07-04
        /// Duna playtest: 24s for a 75-part ghost). Same mid-update deferral contract as
        /// OnGhostCreated (fired from the frame tail, pending before created). Caveat: on the
        /// force-immediate direct-spawn path invoked OUTSIDE UpdatePlayback (in-game tests), the
        /// build finalizes synchronously, so created fires before this event - both converge on
        /// the idempotent map-presence body, so order only matters for the deferred path. NOT fired for
        /// demoted boundary-overlap secondaries (no lifecycle side effects by design) and NOT
        /// fired when the visual build fails at the registration site (state is removed again;
        /// behavior matches the old no-GhostCreated outcome).
        /// </summary>
        internal event Action<GhostLifecycleEvent> OnGhostSpawnPending;

        internal event Action<GhostLifecycleEvent> OnGhostCreated;
        internal event Action<GhostLifecycleEvent> OnGhostDestroyed;
        internal event Action<PlaybackCompletedEvent> OnPlaybackCompleted;
        internal event Action<LoopRestartedEvent> OnLoopRestarted;
        internal event Action<OverlapExpiredEvent> OnOverlapExpired;

        /// <summary>
        /// Raised once per (seam marker, unit cycle) when a looping mission's shared span clock
        /// enters a precomputed seam window (design-dock-event-graph.md 7.3). The engine only
        /// detects the clock crossing and passes the marker's preformatted string through; the
        /// policy layer decides how to show it.
        /// </summary>
        internal event Action<SeamMarkerEvent> OnSeamMarker;

        internal event Action OnAllGhostsDestroying;

        /// <summary>
        /// Delegate set by the policy to check if a ghost index is being held
        /// (pending spawn, watched, etc.). Used by stale past-end cleanup to avoid
        /// destroying ghosts that the policy is intentionally keeping alive.
        /// </summary>
        internal System.Func<int, bool> IsGhostHeld;

        /// <summary>
        /// Delegate set by the policy to resolve the slot index of the chain
        /// continuation for a given slot. Returns -1 when the slot has no
        /// chain continuation (no chain id, branch &gt; 0, no committed
        /// recording at the next chainIndex, or supersede resolution failed).
        /// Used by <see cref="ChainHandoffLogic"/> to coordinate the chain-seam
        /// handoff: shadow the head when the continuation is rendering,
        /// bridge-hold the head when the continuation has not yet activated.
        /// Null is treated as "no chain continuation for any slot" — engine
        /// behaviour falls back to the pre-handoff destroy-and-respawn shape.
        /// </summary>
        internal System.Func<int, int> ResolveChainNextIndex;

        /// <summary>
        /// The current per-frame Mission loop unit set (never null; <see
        /// cref="GhostPlaybackLogic.LoopUnitSet.Empty"/> when no Mission loops). Exposed so the
        /// flight-scene map-presence driver (<see cref="ParsekPlaybackPolicy.CheckPendingMapVessels"/>)
        /// can remap the live UT through the shared span clock exactly like the Tracking Station
        /// and KSC drivers do, instead of sampling orbit lines / icons at the raw live UT (which is
        /// far outside a looped member's recorded UT range).
        /// </summary>
        internal GhostPlaybackLogic.LoopUnitSet CurrentLoopUnits => currentLoopUnits;

        /// <summary>
        /// Per-slot bookkeeping for the chain-bridge hold: the playback UT at
        /// which the bridge first opened for a slot waiting on its chain
        /// continuation. Bounded by
        /// <see cref="ChainHandoffLogic.DefaultBridgeMaxSeconds"/> so a
        /// continuation that genuinely never spawns still tears down the head.
        /// Entries are removed on bridge close (continuation activated,
        /// bridge expired, or slot destroyed).
        /// </summary>
        private readonly Dictionary<int, double> chainBridgeOpenedUT
            = new Dictionary<int, double>();

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

        // The literal `engine-frame-iter` token must appear in the rendered
        // message body, not just the rate-limit key. ParsekLog.VerboseRateLimited
        // uses the key purely for compositeKey lookup (ParsekLog.cs:176) — only
        // the message argument is written via Write (ParsekLog.cs:194,
        // ParsekLog.cs:406). The next-repro grep `[Engine] engine-frame-iter`
        // documented in CHANGELOG.md and docs/dev/todo-and-known-bugs.md
        // depends on this token being in the rendered line, not just the
        // hidden rate-limit composite key.
        internal const string EngineFrameIterMessagePrefix = "engine-frame-iter";

        internal GhostPlaybackEngine(IGhostPositioner positioner)
        {
            this.positioner = positioner;
            ParsekLog.Info("Engine", "GhostPlaybackEngine created");
        }

        internal static bool HasLoadedGhostVisuals(GhostPlaybackState state)
        {
            return state != null && state.ghost != null;
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
            ClearPrimaryGhostPositionedThisFrame();

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
            bool suppressGhosts = GhostPlaybackLogic.ShouldSuppressGhostsInView(
                ctx.mapViewEnabled, ctx.warpRate);
            bool suppressVisualFx = GhostPlaybackLogic.ShouldSuppressVisualFx(ctx.warpRate);
            RebuildAutoLoopLaunchScheduleCache(trajectories, ctx.autoLoopIntervalSeconds);

            ResetPerFramePlaybackCounters(suppressGhosts);
            int ghostsProcessed = 0;
            int trajectoriesIterated = 0;

            // Engine-iteration trace gate. The line itself is built once per
            // frame in a dedicated post-loop pass (see below) so each entry can
            // report not just the pre-render inputs but the per-frame VISIBILITY
            // OUTCOME (mesh shown/hidden + reason), read from the final
            // GhostPlaybackState after the loop has positioned/hidden it.
            // Bypasses GhostRenderTrace's IsDetailedWindowOpen gate so a
            // ghost-vanish repro can tell "fell out of iteration" from
            // "iterated, positioned, then hidden." Auto-on whenever a Re-Fly
            // session is active (the ghost-vanish-inertial repro is a Re-Fly
            // continuation ghost) so the next repro is captured without the user
            // first enabling ghostRenderTracing; emit is rate-limited to 1.0s.
            bool engineIterTraceEnabled =
                ParsekSettings.Current?.ghostRenderTracing == true
                || ctx.activeReFlyMarker != null;

            for (int i = 0; i < trajectories.Count; i++)
            {
                trajectoriesIterated++;
                UpdatePlaybackEntry(
                    i, trajectories[i], flags[i], trajectories, ctx,
                    suppressGhosts, suppressVisualFx, ref ghostsProcessed);
            }

            CompletePlaybackFrame(
                trajectories, flags, ctx, engineIterTraceEnabled, ghostsProcessed, trajectoriesIterated);
        }

        /// <summary>
        /// One trajectory of <see cref="UpdatePlayback"/>'s per-frame loop: the guard chain (skip flag, renderable
        /// data, Re-Fly session suppression, unstable anchor, activation start, loop-unit / loop / loop-synced
        /// debris dispatch, warp hide, chain-seam shadow), then in-range rendering, the past-end completion and the
        /// stale past-end cleanup. Each early exit of the original loop body is a return here.
        /// <paramref name="ghostsProcessed"/> counts trajectories that entered with loaded ghost visuals.
        /// </summary>
        internal void UpdatePlaybackEntry(
            int i,
            IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags f,
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            FrameContext ctx,
            bool suppressGhosts,
            bool suppressVisualFx,
            ref int ghostsProcessed)
        {
            // Disabled/suppressed: destroy any active ghost before skipping.
            // Bug #433: only the PlaybackEnabled=false cause is career-neutral, so
            // only that cause must still fire PlaybackCompleted at past-end — the
            // policy reads the event to spawn the persistent vessel, which must
            // happen regardless of the visual toggle. !hasData and
            // externalVesselSuppressed are structural (no valid trajectory / no
            // spawn path) and keep their silent-skip behaviour.
            if (f.skipGhost)
            {
                HandleSkippedPlaybackEntry(i, traj, f, ctx);
                return;
            }

            bool hasPointData = traj.Points != null && traj.Points.Count > 0;
            bool hasInterpolatedPoints = traj.Points != null && traj.Points.Count >= 2;
            bool hasOrbitData = traj.HasOrbitSegments;
            bool hasSurfaceData = traj.SurfacePos.HasValue;
            if (!HasRenderableGhostData(traj))
            {
                CountFrameSkip(GhostPlaybackSkipReason.NoRenderableData);
                GhostRenderTrace.EmitGuardSkip(
                    traj, i, ctx.currentUT, "no-renderable-ghost-data");
                return;
            }

            // Phase 7 of Rewind-to-Staging (design §3.3): during an active
            // re-fly session, skip ghosts whose source recording is in the
            // SessionSuppressedSubtree. Parent-owned companion debris is a
            // render-only exception for already-committed rows:
            // ERS/merge/supersede still suppresses it, NotCommitted rows
            // stay hidden, but the debris body-fixed playback path can
            // render committed companion debris without showing the
            // replaced parent ghost. Destroy any other leftover ghost
            // visuals first so the player doesn't see stale meshes from a
            // recording that just got superseded mid-flight.
            //
            // The marker and per-recording suppression bit are pushed in by
            // the host through FrameContext / TrajectoryPlaybackFlags so
            // the engine doesn't reach into SessionSuppressionState
            // directly. Both inputs are stable across a frame: every
            // re-fly marker writer (scenario lifecycle, merge / rewind /
            // revert orchestration, reconciliation apply) runs outside the
            // engine's per-frame loop.
            ReFlySessionMarker activeReFlyMarker = ctx.activeReFlyMarker;
            if (activeReFlyMarker != null && f.sessionSuppressed)
            {
                if (!ShouldRenderSuppressedCompanionDebris(
                        traj, activeReFlyMarker, f))
                {
                    GhostRenderTrace.EmitGuardSkip(
                        traj, i, ctx.currentUT, "session-suppressed-subtree");
                    if (ghostStates.ContainsKey(i))
                    {
                        DestroyAllOverlapGhosts(i);
                        DestroyGhost(i, traj, f, reason: "session-suppressed subtree");
                    }
                    CountFrameSkip(GhostPlaybackSkipReason.SessionSuppressed);
                    return;
                }

                LogSessionSuppressedCompanionDebrisRenderAllowed(
                    i, traj, activeReFlyMarker);
            }

            GhostPlaybackState state;
            ghostStates.TryGetValue(i, out state);
            bool ghostActive = HasLoadedGhostVisuals(state);
            if (ghostActive) ghostsProcessed++;

            if (f.anchorReFlyUnstable)
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, i, ctx.currentUT, GhostPlaybackSkipReason.AnchorReFlyUnstable.ToLogToken());
                if (ghostActive && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ResetGhostAppearanceTracking(state);
                }
                SetOverlapGhostsActive(i, false);
                CountFrameSkip(GhostPlaybackSkipReason.AnchorReFlyUnstable);
                return;
            }

            double activationStartUT = ResolveGhostActivationStartUT(traj);

            // === Loop first run is real ===
            // Both loop dispatches below `continue` before the past-end completion, so a
            // looping index would otherwise never spawn its terminal vessel. Its first run
            // (the recording's own UT window) is the real run: queue the one spawn-only
            // completion here (and re-arm it while the playhead is before the recording),
            // then let the loop render as usual.
            TryQueueLoopFirstRunSpawn(i, traj, f, ctx, activationStartUT, hasPointData);

            if (ctx.currentUT < activationStartUT)
            {
                completedEventFired.Remove(i);
                earlyDestroyedDebrisCompleted.Remove(i);
                GhostRenderTrace.EmitGuardSkip(
                    traj, i, ctx.currentUT, "before-activation-start-ut");
                if (ghostActive)
                {
                    DestroyAllOverlapGhosts(i);
                    DestroyGhost(i, traj, f, reason: "before activation start UT");
                }
                CountFrameSkip(GhostPlaybackSkipReason.BeforeActivation);
                return;
            }

            bool inRange = ctx.currentUT <= traj.EndUT;
            bool pastEnd = ctx.currentUT > traj.EndUT;
            bool pastEffectiveEnd = ctx.currentUT > f.chainEndUT;

            // === Mission loop unit interception (Phase D2) ===
            // If this index is a member of a Mission loop unit, it is driven by the unit's
            // shared span clock, NOT its own per-recording loop clock. Mission members do not
            // carry their own per-recording LoopPlayback flag, so ShouldLoopPlayback(traj) is
            // false for them - the interception must sit ABOVE the loop-dispatch gate (this
            // deviates from the chain-auto-loop branch, where the equivalent interception lives
            // inside the gate). Route it through UpdateUnitMemberPlayback (which re-checks anchor
            // gating + warp suppression per-member). Gated entirely on unit membership, which is
            // empty until a Mission loops, so non-members fall straight through below.
            if (currentLoopUnits.TryGetUnitForMember(i, out GhostPlaybackLogic.LoopUnit unit))
            {
                UpdateUnitMemberPlayback(
                    i, traj, f, ctx, trajectories, unit, suppressGhosts, suppressVisualFx,
                    hasPointData, hasInterpolatedPoints,
                    hasSurfaceData, hasOrbitData, ref state, ref ghostActive);
                return;
            }

            // === Loop dispatch (before main rendering) ===
            if (ShouldLoopPlayback(traj))
            {
                // Anchor gating: if anchor configured but not loaded, skip ghost
                if (traj.LoopAnchorVesselId != 0 && !loadedAnchorVessels.Contains(traj.LoopAnchorVesselId))
                {
                    GhostRenderTrace.EmitGuardSkip(
                        traj, i, ctx.currentUT,
                        "loop-anchor-unloaded pid=" + traj.LoopAnchorVesselId.ToString(CultureInfo.InvariantCulture));
                    if (ghostActive)
                    {
                        DestroyGhost(i, traj, f, reason: $"anchor {traj.LoopAnchorVesselId} unloaded");
                        DestroyAllOverlapGhosts(i);
                    }
                    CountFrameSkip(GhostPlaybackSkipReason.AnchorMissing);
                    return;
                }

                UpdateLoopingPlayback(i, traj, f, ctx, suppressGhosts, suppressVisualFx);

                // Clean up leftover overlap ghosts from loop->non-loop transition
                List<GhostPlaybackState> leftoverOverlaps;
                if (overlapGhosts.TryGetValue(i, out leftoverOverlaps) && leftoverOverlaps.Count > 0)
                {
                    // Overlap cleanup handled in UpdateLoopingPlayback/UpdateOverlapPlayback
                }

                return;
            }

            // === Loop-synced debris: use parent's loop clock ===
            // Runs BEFORE the loop->non-loop overlap cleanup below. A ride-along debris whose
            // parent loops renders through the OVERLAP path (UpdateOverlapPlayback owns
            // overlapGhosts[i] and expires its own staggered instances). The cleanup below must
            // NOT wipe that list every frame: doing so destroys each demoted background instance
            // (the older debris pieces still coasting to their crash) the very next frame, so
            // only the freshest instance survives and it resets every cadence - the debris
            // "disappears" long before its recorded flight ends. Non-debris recordings and a
            // debris whose parent is NOT looping return false here and fall through to the
            // cleanup, which is correct for them.
            if (TryUpdateLoopSyncedDebris(
                    i, traj, f, ctx, trajectories, suppressGhosts, suppressVisualFx,
                    activationStartUT, hasPointData, hasInterpolatedPoints,
                    hasSurfaceData, hasOrbitData, ref state, ref ghostActive))
                return;

            // Clean up overlap ghosts if recording switched from looping to non-looping.
            if (overlapGhosts.ContainsKey(i))
            {
                DestroyAllOverlapGhosts(i);
                overlapGhosts.Remove(i);
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
                return;
            }

            // === Chain-seam handoff: shadow the head when the chain
            // continuation is rendering. This collapses the section-overlap
            // case (head + continuation both authored across a UT window)
            // onto a single visible ghost. The continuation already covers
            // playback inside its UT range, so the head's render here is
            // redundant. Bridge bookkeeping for the gap-case lives below
            // in the stale-past-end cleanup. ===
            //
            // Note (flag/part events): the shadow path short-circuits
            // before RenderInRangeGhost, which also means
            // ApplyFlagEvents and per-frame part-event playback do not
            // run for the shadowed head this frame. Authored flags are
            // world-permanent and the same FlagEvent payload typically
            // exists on the continuation recording (chain splits clone
            // the events to both sides), so the continuation's render
            // path plants them. Part events on the head past the
            // continuation's startUT are intentionally skipped — the
            // continuation's authored events are the authoritative
            // surface during the overlap.
            //
            // Note (inRange vs chainEndUT): inRange uses traj.EndUT and
            // pastEffectiveEnd uses f.chainEndUT.
            // RecordingChainQueries.GetChainEndUT returns Max(rec.EndUT, ...)
            // over branch-0 chain peers, so chainEndUT >= EndUT and
            // inRange implies !pastEffectiveEnd. Shadow firing here
            // therefore cannot collide with the past-end branch below.
            int chainNextIndex = ResolveChainNextIndex != null
                ? ResolveChainNextIndex(i)
                : -1;
            bool continuationHasActiveGhost = chainNextIndex >= 0
                && HasActiveGhost(chainNextIndex);
            // Defensive: never shadow the watched head. WatchModeController
            // already coordinates the chain transition via its own destroy
            // path (`auto-followed during hold`) and only fires after the
            // continuation's ghost is active, so in normal flow the watch
            // transfer wins the race and the head is destroyed before
            // the shadow ever runs. But for chains whose head and
            // continuation sectionUTs overlap (post-optimizer splits
            // routinely leave a sub-second overlap), there is a 1-2 frame
            // window where the head is still in-range AND watched AND the
            // continuation has just activated. Without this guard the
            // shadow would SetActive(false) on the watched ghost mesh in
            // that window. The downstream watch transfer then destroys
            // the head a frame or two later. Mesh hiding is visually
            // subtle because the continuation's mesh is at an
            // approximately overlapping world position, but the watch
            // camera target should never be a deactivated ghost
            // GameObject — IsGhostHeld(i) returns true only for the
            // watched slot (or a held-pending-spawn slot), so the guard
            // closes the race without affecting non-watched chain
            // segments which is the primary case the shadow fixes.
            // Bridge-hold path below is already gated by !IsGhostHeld
            // via the surrounding stale-past-end cleanup branch.
            if (inRange
                && (IsGhostHeld == null || !IsGhostHeld(i))
                && ChainHandoffLogic.DecideShadow(
                    chainNextIndex, continuationHasActiveGhost))
            {
                ShadowChainHead(i, traj, ctx, state, ghostActive, chainNextIndex);
                return;
            }

            // === In-range rendering ===
            if (inRange)
            {
                if (RenderInRangeGhost(i, traj, f, ctx, suppressVisualFx,
                        hasPointData, hasInterpolatedPoints, hasSurfaceData, hasOrbitData,
                        allowEarlyDestroyedDebrisCompletion: true,
                        ref state, ref ghostActive))
                    return;
            }

            // === Past end: fire completed event, optionally destroy ===
            if ((pastEnd || pastEffectiveEnd)
                && !completedEventFired.Contains(i)
                && !earlyDestroyedDebrisCompleted.Contains(i))
                HandlePastEndGhost(i, traj, f, ctx, state, ghostActive, hasPointData);

            // === Stale past-end ghost cleanup ===
            // Ghost survived past-end (e.g. watch hold), completed event already fired,
            // and not being held by the policy - destroy it. Prevents debris ghosts
            // from freezing at their last trajectory point indefinitely. A slot whose
            // completion event is still queued in deferredCompletedEvents has not been
            // seen by the policy yet, so "not held" is not a decision the policy made:
            // postpone its cleanup to RunStalePastEndCleanupsAfterCompletionDelivery,
            // which re-evaluates it after FireDeferredFrameEvents in this same frame.
            ApplyStalePastEndCleanupStep(i, traj, f, ctx.currentUT, state,
                chainNextIndex, continuationHasActiveGhost);
        }

        /// <summary>
        /// The skip-flag exit of <see cref="UpdatePlaybackEntry"/>: counts the skip, traces it, destroys any ghost
        /// the slot still holds, and for the PlaybackEnabled=false cause still queues the past-end completion
        /// (bug #433). The caller returns after it.
        /// </summary>
        internal void HandleSkippedPlaybackEntry(
            int i,
            IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags f,
            FrameContext ctx)
        {
            CountFrameSkip(f.skipReason);
            GhostRenderTrace.EmitGuardSkip(
                traj, i, ctx.currentUT, f.skipReason.ToLogToken());
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
        }

        /// <summary>
        /// The chain-seam shadow exit of <see cref="UpdatePlaybackEntry"/>: the chain continuation is rendering,
        /// so the head's mesh and overlap clones are hidden, its gap-case bridge entry is dropped, and the skip is
        /// counted, logged (rate-limited) and traced. The caller returns after it.
        /// </summary>
        internal void ShadowChainHead(
            int i,
            IPlaybackTrajectory traj,
            FrameContext ctx,
            GhostPlaybackState state,
            bool ghostActive,
            int chainNextIndex)
        {
            if (ghostActive && state != null && state.ghost != null
                && state.ghost.activeSelf)
            {
                state.ghost.SetActive(false);
                ResetGhostAppearanceTracking(state);
            }
            SetOverlapGhostsActive(i, false);
            // Continuation is rendering, so the gap-case bridge for
            // this slot is no longer relevant. Clearing here keeps the
            // dictionary footprint bounded.
            chainBridgeOpenedUT.Remove(i);
            CountFrameSkip(GhostPlaybackSkipReason.ChainShadowed);
            ParsekLog.VerboseRateLimited(
                "Engine",
                "chain-shadow-" + i.ToString(CultureInfo.InvariantCulture),
                "Ghost #" + i.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj.VesselName ?? "?")
                    + "\" chain-shadowed by continuation slot #"
                    + chainNextIndex.ToString(CultureInfo.InvariantCulture)
                    + " at UT="
                    + ctx.currentUT.ToString("F2", CultureInfo.InvariantCulture),
                5.0);
            GhostRenderTrace.EmitGuardSkip(
                traj, i, ctx.currentUT, "chain-shadowed");
        }

        /// <summary>
        /// <see cref="UpdatePlayback"/>'s frame tail after the per-trajectory loop: the engine-iteration trace,
        /// the frame summary, deferred event delivery, the postponed stale past-end cleanups, the observability
        /// capture, and the #414 budget bookkeeping and threshold check.
        /// </summary>
        internal void CompletePlaybackFrame(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            TrajectoryPlaybackFlags[] flags,
            FrameContext ctx,
            bool engineIterTraceEnabled,
            int ghostsProcessed,
            int trajectoriesIterated)
        {
            long spawnMicroseconds = 0;

            // Engine-iteration trace emit: one log line listing every iterated
            // trajectory's identity + producer-side inputs (skipReason /
            // anchorReFlyUnstable / renderable-data / ghostStates membership /
            // endUT) AND its per-frame VISIBILITY OUTCOME, read here from the
            // post-loop GhostPlaybackState: did the ghost end the frame with its
            // mesh shown, and if hidden, was it the relative-anchor retire path
            // (retired=T) or distance LOD (zone=Beyond + rdist)? This closes the
            // gap that made the loop-top-only trace inconclusive for the
            // "active=1 but invisible" symptom — skip=None at the loop top did
            // NOT prove the ghost rendered, because a retire/zone-hide happens
            // after positioning. Bypasses GhostRenderTrace's anomaly-window gate
            // so a ghost-vanish repro can answer definitively. Rate-limited to
            // 1.0s to keep steady-state cost negligible.
            EmitEngineIterTraceForFrame(trajectories, flags, engineIterTraceEnabled);

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
            FireDeferredFrameEvents(out int createdEventsFired, out int completedEventsFired);

            // The policy has now seen this frame's completions (and held, destroyed or left
            // each ghost), so the stale cleanups the loop postponed can be decided. Like the
            // policy's own destroys during delivery, these land after the frame summary and
            // the iteration trace above, so those report the ghost alive this frame.
            RunStalePastEndCleanupsAfterCompletionDelivery(ctx.currentUT);

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
            // below; nothing is written unless the warn fires. The already-divided
            // spawnMicroseconds / destroyMicroseconds are reused (not recomputed from ticks)
            // so the breakdown's buildOtherMicroseconds subtraction matches
            // DiagnosticsState.playbackBudget.spawnMicroseconds to the microsecond (#414).
            PlaybackBudgetPhases phases = BuildPlaybackBudgetPhases(
                elapsedTicksAtLoopEnd: elapsedTicksAtLoopEnd,
                spawnMicroseconds: spawnMicroseconds,
                destroyMicroseconds: destroyMicroseconds,
                deferredCreatedTicks: deferredCreatedStopwatch.ElapsedTicks,
                deferredCompletedTicks: deferredCompletedStopwatch.ElapsedTicks,
                observabilityTicks: observabilityStopwatch.ElapsedTicks,
                trajectoriesIterated: trajectoriesIterated,
                overlapGhostIterationCount: frameOverlapGhostIterationCount,
                createdEventsFired: createdEventsFired,
                completedEventsFired: completedEventsFired,
                spawnsAttempted: frameSpawnCount,
                spawnsThrottled: frameSpawnDeferred,
                frameMaxSpawnTicks: frameMaxSpawnTicks,
                buildSnapshotResolveTicks: buildSnapshotResolveStopwatch.ElapsedTicks,
                buildTimelineTicks: buildTimelineStopwatch.ElapsedTicks,
                buildDictionariesTicks: buildDictionariesStopwatch.ElapsedTicks,
                buildReentryFxTicks: buildReentryFxStopwatch.ElapsedTicks,
                heaviestSnapshotResolveTicks: frameHeaviestSpawnSnapshotResolveTicks,
                heaviestTimelineTicks: frameHeaviestSpawnTimelineTicks,
                heaviestDictionariesTicks: frameHeaviestSpawnDictionariesTicks,
                heaviestReentryTicks: frameHeaviestSpawnReentryTicks,
                heaviestOtherTicks: frameHeaviestSpawnOtherTicks,
                heaviestBuildType: frameHeaviestSpawnBuildType);

            // Budget threshold warning (8ms = half of 16.6ms frame budget at 60fps).
            // WithBreakdown variant: also emits a one-shot WARN with phase breakdown the
            // first time a spike is seen, to localize the responsible sub-phase (bug #414).
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                totalMicroseconds, ghostsProcessed, ctx.warpRate, phases);
        }

        /// <summary>
        /// Phase-C tail: fire the deferred OnGhostCreated then OnPlaybackCompleted events
        /// accumulated during the per-trajectory loop, each timed by its own stopwatch for the
        /// #414 budget breakdown. Extracted verbatim from UpdatePlayback; the index `for` loops
        /// (NOT foreach — avoids enumerator allocation) and the capture-count-before-iterate
        /// ordering are preserved. The two counts return via <c>out</c> because the caller feeds
        /// them into the budget-phase struct.
        /// </summary>
        private void FireDeferredFrameEvents(out int createdEventsFired, out int completedEventsFired)
        {
            createdEventsFired = deferredCreatedEvents.Count;
            deferredCreatedStopwatch.Start();
            // Spawn-pending fires BEFORE created (pending precedes finalize semantically, even for
            // a same-frame immediate build). Shares the created stopwatch so the #414 budget
            // breakdown keeps accounting for the map-presence enqueue without a new phase field.
            for (int i = 0; i < deferredSpawnPendingEvents.Count; i++)
                OnGhostSpawnPending?.Invoke(deferredSpawnPendingEvents[i]);
            for (int i = 0; i < deferredCreatedEvents.Count; i++)
                OnGhostCreated?.Invoke(deferredCreatedEvents[i]);
            deferredCreatedStopwatch.Stop();

            completedEventsFired = deferredCompletedEvents.Count;
            deferredCompletedStopwatch.Start();
            for (int i = 0; i < deferredCompletedEvents.Count; i++)
                OnPlaybackCompleted?.Invoke(deferredCompletedEvents[i]);
            deferredCompletedStopwatch.Stop();
        }

        /// <summary>
        /// Bug #414/#450: builds the one-shot per-phase budget breakdown struct from the
        /// UpdatePlayback Phase-C finalization values. Pure arithmetic — tick-to-microsecond
        /// conversions plus the two negative-clamp floors — extracted verbatim from the inline
        /// block so it can be unit-tested independently. <paramref name="spawnMicroseconds"/>
        /// and <paramref name="destroyMicroseconds"/> arrive ALREADY divided (the caller wrote
        /// them into DiagnosticsState.playbackBudget); they are reused here, never recomputed
        /// from ticks, so the buildOtherMicroseconds subtraction matches the budget total to
        /// the microsecond (#414). Everything else arrives as raw stopwatch ticks so the
        /// `* 1000000L / Stopwatch.Frequency` division lives in exactly one place.
        /// </summary>
        internal static PlaybackBudgetPhases BuildPlaybackBudgetPhases(
            long elapsedTicksAtLoopEnd,
            long spawnMicroseconds,
            long destroyMicroseconds,
            long deferredCreatedTicks,
            long deferredCompletedTicks,
            long observabilityTicks,
            int trajectoriesIterated,
            int overlapGhostIterationCount,
            int createdEventsFired,
            int completedEventsFired,
            int spawnsAttempted,
            int spawnsThrottled,
            long frameMaxSpawnTicks,
            long buildSnapshotResolveTicks,
            long buildTimelineTicks,
            long buildDictionariesTicks,
            long buildReentryFxTicks,
            long heaviestSnapshotResolveTicks,
            long heaviestTimelineTicks,
            long heaviestDictionariesTicks,
            long heaviestReentryTicks,
            long heaviestOtherTicks,
            HeaviestSpawnBuildType heaviestBuildType)
        {
            long elapsedMicrosecondsAtLoopEnd = elapsedTicksAtLoopEnd * 1000000L / Stopwatch.Frequency;
            long mainLoopMicroseconds = elapsedMicrosecondsAtLoopEnd - spawnMicroseconds - destroyMicroseconds;
            if (mainLoopMicroseconds < 0) mainLoopMicroseconds = 0;
            // Bug #450: per-sub-phase aggregate = sum across all spawns in the frame.
            long buildSnapshotResolveMicroseconds = buildSnapshotResolveTicks * 1000000L / Stopwatch.Frequency;
            long buildTimelineFromSnapshotMicroseconds = buildTimelineTicks * 1000000L / Stopwatch.Frequency;
            long buildDictionariesMicroseconds = buildDictionariesTicks * 1000000L / Stopwatch.Frequency;
            long buildReentryFxMicroseconds = buildReentryFxTicks * 1000000L / Stopwatch.Frequency;
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

            return new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = mainLoopMicroseconds,
                spawnMicroseconds = spawnMicroseconds,
                destroyMicroseconds = destroyMicroseconds,
                explosionCleanupMicroseconds = 0, // FXMonger owns explosion-side cleanup; nothing engine-local to measure
                deferredCreatedEventsMicroseconds = deferredCreatedTicks * 1000000L / Stopwatch.Frequency,
                deferredCompletedEventsMicroseconds = deferredCompletedTicks * 1000000L / Stopwatch.Frequency,
                observabilityCaptureMicroseconds = observabilityTicks * 1000000L / Stopwatch.Frequency,
                trajectoriesIterated = trajectoriesIterated,
                // Bug #460: total overlap-ghost iterations this frame so the mainLoop
                // breakdown WARN can compute `meanPerDispatch` alongside `meanPerTraj`.
                overlapGhostIterationCount = overlapGhostIterationCount,
                createdEventsFired = createdEventsFired,
                completedEventsFired = completedEventsFired,
                spawnsAttempted = spawnsAttempted,
                spawnsThrottled = spawnsThrottled,
                spawnMaxMicroseconds = frameMaxSpawnTicks * 1000000L / Stopwatch.Frequency,
                // Bug #450: per-sub-phase aggregate across every spawn this frame.
                buildSnapshotResolveMicroseconds = buildSnapshotResolveMicroseconds,
                buildTimelineFromSnapshotMicroseconds = buildTimelineFromSnapshotMicroseconds,
                buildDictionariesMicroseconds = buildDictionariesMicroseconds,
                buildReentryFxMicroseconds = buildReentryFxMicroseconds,
                buildOtherMicroseconds = buildOtherMicroseconds,
                // Bug #450: heaviest-spawn breakdown (latched on whichever single
                // BuildGhostVisualsWithMetrics call produced the largest delta).
                heaviestSpawnSnapshotResolveMicroseconds = heaviestSnapshotResolveTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnTimelineFromSnapshotMicroseconds = heaviestTimelineTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnDictionariesMicroseconds = heaviestDictionariesTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnReentryFxMicroseconds = heaviestReentryTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnOtherMicroseconds = heaviestOtherTicks * 1000000L / Stopwatch.Frequency,
                heaviestSpawnBuildType = heaviestBuildType,
            };
        }

        private void ResetPerFramePlaybackCounters(bool suppressGhosts)
        {
            // Reset reshow dedup when entering warp suppression
            if (suppressGhosts)
                loggedReshow.Clear();

            deferredCompletedEvents.Clear();
            deferredCreatedEvents.Clear();
            deferredSpawnPendingEvents.Clear();
            staleCleanupsAwaitingCompletionDelivery.Clear();
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
            frameSkipSupersededByRelation = 0;
            frameSkipRewindRetired = 0;
            frameSkipSpawnSuppressedDeadOnArrival = 0;
            frameSkipAnchorRotationUnreliable = 0;
            frameSkipAnchorReFlyUnstable = 0;
            frameSkipChainShadowed = 0;
            frameSkipChainBridgeHeld = 0;
            frameSkipMissionLoopUnitInactive = 0;
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

        private void ClearPrimaryGhostPositionedThisFrame()
        {
            foreach (var state in ghostStates.Values)
            {
                if (state != null)
                    state.positionedThisFrame = false;
            }
        }

        private static void MarkPrimaryGhostPositionedThisFrame(GhostPlaybackState state)
        {
            if (state != null)
                state.positionedThisFrame = true;
        }

        /// <summary>Completion events queued this frame and not yet delivered. Tests only.</summary>
        internal IReadOnlyList<PlaybackCompletedEvent> PendingCompletedEventsForTesting => deferredCompletedEvents;

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

        /// <summary>
        /// Whether the given GameObject is a ghost mesh THIS engine currently
        /// references (primary or overlap). Used by the test runner's post-abort
        /// orphaned-ghost-mesh sweep to distinguish live "Parsek_Timeline_*"
        /// meshes from abandoned ones (e.g. a mesh spawned by a test-private
        /// engine instance discarded without DestroyAllGhosts, 2026-07-10 rerun2).
        /// One-shot cleanup consumer only; not for per-frame use.
        /// </summary>
        internal bool OwnsGhostGameObject(GameObject go)
        {
            if (go == null)
                return false;
            foreach (var state in ghostStates.Values)
            {
                if (state != null && state.ghost == go)
                    return true;
            }
            foreach (var list in overlapGhosts.Values)
            {
                if (list == null)
                    continue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null && list[i].ghost == go)
                        return true;
                }
            }
            return false;
        }

        internal bool EnsureGhostVisualsLoadedForWatch(
            int index, IPlaybackTrajectory traj, double playbackUT,
            double currentUT,
            bool forceRebuildLoadedVisuals = false)
        {
            if (!ghostStates.TryGetValue(index, out var state) || state == null)
                return false;
            if (TryHandleParentAnchoredDebrisCoverageRetired(
                    index, traj, state, playbackUT, currentUT, TimeWarp.CurrentRate,
                    "GhostPlaybackEngine.EnsureGhostVisualsLoadedForWatch",
                    emitExitWatch: false,
                    out _))
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

            SynchronizeLoadedGhostForWatch(index, traj, state, playbackUT, currentUT);
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

        /// <summary>Get active primary ghosts that can be considered as recording-id anchors.</summary>
        internal IEnumerable<GhostAnchorCandidate> GetActiveAnchorCandidates(
            IReadOnlyList<IPlaybackTrajectory> trajectories)
        {
            if (trajectories == null)
                yield break;

            foreach (var kv in ghostStates)
            {
                int index = kv.Key;
                if (index < 0 || index >= trajectories.Count)
                    continue;

                var state = kv.Value;
                if (state == null)
                    continue;

                string recordingId = trajectories[index]?.RecordingId;
                if (string.IsNullOrEmpty(recordingId))
                    continue;

                yield return new GhostAnchorCandidate(
                    index, recordingId, state.positionedThisFrame);
            }
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

        internal static Func<double> MeshTraceUTOverrideForTesting;

        // Bug #414 test hooks. The throttle gate and counters are private so UpdatePlayback
        // remains the only production path that touches them; tests need a narrow seam to
        // exercise the decision directly without constructing a full FrameContext.
        internal bool TryReserveSpawnSlotForTesting(int index, string site)
            => TryReserveSpawnSlot(index, site);
        internal void IncrementFrameSpawnCountForTesting()
            => frameSpawnCount++;
        internal int FrameSpawnCountForTesting => frameSpawnCount;
        internal int FrameSpawnDeferredForTesting => frameSpawnDeferred;
        internal int SpawnReserveAttemptCountForTesting => spawnReserveAttemptCountForTesting;
        internal int VisualLoadAttemptCountForTesting => visualLoadAttemptCountForTesting;
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
            ClearPrimaryGhostPositionedThisFrame();
            frameSpawnCount = 0;
            frameDestroyCount = 0;
            frameSpawnDeferred = 0;
            spawnReserveAttemptCountForTesting = 0;
            visualLoadAttemptCountForTesting = 0;
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
            frameSkipSupersededByRelation = 0;
            frameSkipRewindRetired = 0;
            frameSkipSpawnSuppressedDeadOnArrival = 0;
            frameSkipAnchorRotationUnreliable = 0;
            frameSkipAnchorReFlyUnstable = 0;
            frameSkipChainShadowed = 0;
            frameSkipChainBridgeHeld = 0;
            frameSkipMissionLoopUnitInactive = 0;
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

        internal bool MarkGhostPositionedThisFrameForTesting(int index)
        {
            if (!ghostStates.TryGetValue(index, out var state) || state == null)
                return false;

            MarkPrimaryGhostPositionedThisFrame(state);
            return true;
        }

        // Bug #613 (PR #594 P1 round 2) test seams: in-game tests need to
        // observe whether TryHandleEarlyDestroyedDebrisCompletion ran on the
        // retire frame. Both sets / lists are private; expose count-only
        // accessors so the test file does not need InternalsVisibleTo
        // gymnastics to peek at internal state.
        internal int Bug613TestEarlyDestroyedCount => earlyDestroyedDebrisCompleted.Count;
        internal int Bug613TestDeferredCompletedCount => deferredCompletedEvents.Count;

        /// <summary>
        /// Test seam: drives one slot through the same past-end frame-tail sequence
        /// UpdatePlayback runs (queue the completion when <paramref name="queueCompletion"/>,
        /// the loop's stale-cleanup step, event delivery, the post-delivery cleanup pass),
        /// without the Unity-bound positioning around it.
        /// </summary>
        internal void RunPastEndFrameTailForTesting(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, double currentUT, bool queueCompletion,
            GhostPlaybackState staleLoopStateOverride = null)
        {
            deferredCompletedEvents.Clear();
            deferredCreatedEvents.Clear();
            deferredSpawnPendingEvents.Clear();
            staleCleanupsAwaitingCompletionDelivery.Clear();

            GhostPlaybackState state;
            ghostStates.TryGetValue(index, out state);
            if (staleLoopStateOverride != null)
                state = staleLoopStateOverride;
            if (queueCompletion)
                QueuePastEndCompletedEvent(index, traj, flags, currentUT, state,
                    ghostActive: state != null, hasPointData: false);

            int chainNextIndex = ResolveChainNextIndex != null ? ResolveChainNextIndex(index) : -1;
            bool continuationHasActiveGhost = chainNextIndex >= 0 && HasActiveGhost(chainNextIndex);
            ApplyStalePastEndCleanupStep(index, traj, flags, currentUT, state,
                chainNextIndex, continuationHasActiveGhost);
            FireDeferredFrameEvents(out _, out _);
            RunStalePastEndCleanupsAfterCompletionDelivery(currentUT);
        }

        internal bool IsCompletionFiredForTesting(int index) => completedEventFired.Contains(index);
        internal bool TryHandleParentAnchoredDebrisCoverageRetiredForTesting(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, double currentUT, float warpRate,
            bool emitExitWatch, out bool ghostActive)
            => TryHandleParentAnchoredDebrisCoverageRetired(
                index, traj, state, playbackUT, currentUT, warpRate,
                "test", emitExitWatch, out ghostActive);
        internal static bool ShouldExitWatchForCoverageRetiredStateForTesting(
            int index, GhostPlaybackState state, FrameContext ctx)
            => ShouldExitWatchForCoverageRetiredState(index, state, ctx);
        internal static bool ShouldExitWatchForCoverageRetiredCycleForTesting(
            int index, long loopCycleIndex, FrameContext ctx)
            => ShouldExitWatchForCoverageRetiredCycle(index, loopCycleIndex, ctx);
        internal static GhostRenderTrace.RenderSurface ResolveRenderSurfaceForTesting(
            bool usedBodyFixedPrimary, bool retired)
            => ResolveRenderSurface(usedBodyFixedPrimary, retired);

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

        // Maximum gap (seconds) between the last authored trajectory point and a
        // predicted orbit tail's startUT that the renderer will bridge by playing
        // the orbit (back-propagated) through the gap instead of holding the last
        // point and then teleporting to the orbit when it activates. The gap is the
        // unsampled window between when per-frame sampling stopped (the vessel went
        // on-rails / left the physics bubble) and when the predicted coast tail was
        // captured (on-rails / scene-exit snapshot). Across this window the vessel is
        // a pure Keplerian coast, so back-propagating the tail orbit to the last
        // point is correct, and the orbit-tail continuity offset pins the handoff so
        // there is no visible teleport (it only smooths the small reseed residual).
        // Sized for on-rails / scene-exit transition gaps (the payload-to-orbit
        // teleport was an observed ~24s gap) with headroom; bounded so a far-future
        // post-maneuver orbit is never back-propagated into the coast window (the
        // first predicted segment after the last point is always the current coast,
        // whose own range covers the run up to any later maneuver segment).
        //
        // A single cap covers both live and Destroyed tails: the historical
        // Destroyed-specific 5s tier was raised because destroyed finalizer tails had
        // a larger gap than live finalizer tails; the on-rails discovery shows live
        // tails need an even larger window, so the distinction is obsolete and a
        // wider cap can never reduce destroyed bridging (it can only widen it, which
        // the continuity offset keeps teleport-free).
        internal const double PredictedOrbitTailBridgeMaxGapSeconds = 30.0;
        internal const double PredictedOrbitTailContinuityMinBlendSeconds = 5.0;
        internal const double PredictedOrbitTailContinuityExtraBlendSeconds = 2.0;
        internal const double PredictedOrbitTailContinuityMaxBlendSeconds = 10.0;

        /// <summary>Test seam: replaces the Unity-bound resource teardown in DestroyGhost so
        /// headless tests can drive the slot bookkeeping (ghostStates, completion dedup).</summary>
        internal Action<GhostPlaybackState> DestroyGhostResourcesOverrideForTesting;

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
