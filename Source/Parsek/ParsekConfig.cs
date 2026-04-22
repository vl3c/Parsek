namespace Parsek
{
    // ==========================================================================
    // ParsekConfig.cs
    // --------------------------------------------------------------------------
    // Central home for Parsek tunables and thresholds. If a number is reasonable
    // to tune by a developer — a cap, a threshold, a default, a grace window —
    // it lives here. One file to open, one place to change.
    //
    // Keep OUT of this file:
    //   • format-version integers (TreeFormatVersion, sidecar format tags)
    //   • enum underlying values
    //   • string tags, keys, paths, regex literals
    //   • buffer sizes / hash seeds / strictly implementation-local numbers
    //   • UI geometry (padding, widths) specific to a single window
    //
    // Organization: several top-level internal static classes grouped by
    // subsystem. Each class is importable by its short name (e.g.
    // `LoopTiming.MinCycleDuration`) so call sites stay readable.
    // ==========================================================================

    /// <summary>
    /// Authoritative home for distance-based thresholds that affect ghost playback,
    /// watch mode, related scene policies, and nearby recorder behavior.
    /// Keep scene-specific exceptions here rather than scattering literals.
    /// </summary>
    internal static class DistanceThresholds
    {
        /// <summary>
        /// KSP's loaded-physics envelope around the active vessel. This is the core
        /// spatial boundary that several systems key off: rendering fidelity,
        /// relative-frame anchoring, spawn scoping, and background sampling.
        /// </summary>
        internal const double PhysicsBubbleMeters = 2300.0;

        /// <summary>
        /// Flight-scene ghost visual range boundary. Beyond this, the mesh is hidden
        /// and only logical playback / map presence remains.
        /// </summary>
        internal const double GhostVisualRangeMeters = 120000.0;

        internal static class RelativeFrame
        {
            internal const double EntryMeters = PhysicsBubbleMeters;
            internal const double ExitMeters = 2500.0;
            internal const double DockingApproachMeters = 200.0;
        }

        internal static class BackgroundSampling
        {
            internal const double DockingRangeMeters = RelativeFrame.DockingApproachMeters;
            internal const double MidRangeMeters = 1000.0;
            internal const double MaxDistanceMeters = PhysicsBubbleMeters;
        }

        internal static class GhostFlight
        {
            internal const double LoopFullFidelityMeters = PhysicsBubbleMeters;
            internal const double LoopSimplifiedMeters = 50000.0;

            // Keep the watch camera available through typical ascent/coast ghosts
            // without letting it stay latched to whole-orbit distant playback.
            internal const float DefaultWatchCameraCutoffKm = 300f;
            internal const double AirlessWatchHorizonLockAltitudeMeters = 50000.0;

            internal static float GetWatchCameraCutoffKm()
                => DefaultWatchCameraCutoffKm;

            internal static double GetWatchCameraCutoffMeters()
                => GetWatchCameraCutoffKm() * 1000.0;

            internal static bool ShouldAutoHorizonLock(
                bool hasAtmosphere, double atmosphereDepth, double altitudeMeters)
            {
                double threshold = hasAtmosphere
                    ? atmosphereDepth
                    : AirlessWatchHorizonLockAltitudeMeters;
                return altitudeMeters < threshold;
            }

            internal static double ComputeTerrainClearance(double distanceToVesselMeters)
            {
                if (distanceToVesselMeters <= PhysicsBubbleMeters)
                    return 0.5;

                double t = (distanceToVesselMeters - PhysicsBubbleMeters)
                    / (GhostVisualRangeMeters - PhysicsBubbleMeters);
                if (t > 1.0) t = 1.0;
                return 2.0 + t * 3.0;
            }
        }

        internal static class GhostAudio
        {
            internal const float RolloffMinDistanceMeters = 30f;
            internal const float RolloffMaxDistanceMeters = 5000f;
        }

        internal static class KscGhosts
        {
            internal const float CullDistanceMeters = 25000f;
            internal const float CullDistanceSq = CullDistanceMeters * CullDistanceMeters;
        }
    }

    /// <summary>
    /// Ghost playback simultaneity caps, per-frame work budgets, and LOD
    /// prewarm buffers. Consumed by <see cref="GhostPlaybackEngine"/> (flight
    /// scene) and <see cref="ParsekKSC"/> — these two scenes stay in lockstep.
    /// </summary>
    internal static class GhostPlayback
    {
        /// <summary>
        /// Hard ceiling on simultaneously-live ghost clones per recording
        /// (primary + overlap). Per-frame cost scales with this value (mesh
        /// renderers, FX, audio, positioner); mesh vertex buffers themselves
        /// are shared via Unity <c>sharedMesh</c>, but every clone is still
        /// its own GameObject / Transform / renderer / draw call. Combined
        /// with <see cref="GhostPlaybackLogic.ComputeEffectiveLaunchCadence"/>
        /// the cap is strictly observed — cadence is raised to the minimum
        /// value that keeps <c>ceil(duration/cadence)</c> within the cap, so
        /// no cycle is ever silently culled mid-trajectory. Raising the cap
        /// therefore also lowers the minimum effective looping interval
        /// (floor = <c>duration / cap</c>).
        /// </summary>
        internal const int MaxOverlapGhostsPerRecording = 20;

        /// <summary>
        /// Bug #414: cap on throttle-eligible ghost-visual builds per
        /// UpdatePlayback tick. Worst-case spawn cost =
        /// cap × <see cref="MaxSpawnBuildMillisecondsPerAdvance"/> ≈ under
        /// the 8ms playback-budget WARN threshold. Watch-mode and
        /// loop-cycle-rebuild spawns bypass this cap; see
        /// <c>docs/dev/plan-414-spawn-throttle.md</c> for the full call-site
        /// taxonomy.
        /// </summary>
        internal const int MaxSpawnsPerFrame = 2;

        /// <summary>
        /// Bug #450 B3: cap on deferred reentry-FX builds that can fire on a
        /// single frame. Without a cap, N ghosts crossing atmosphere depth on
        /// the same frame would each pay the ~7 ms TryBuildReentryFx cost,
        /// relocating the bimodal spawn-burst pattern to atmosphere-entry time.
        /// </summary>
        internal const int MaxLazyReentryBuildsPerFrame = 2;

        /// <summary>
        /// Bug #450 B2: maximum timeline-build work one ghost is allowed to
        /// consume in a single BuildGhostVisualsWithMetrics call. Explicit
        /// watch-mode loads bypass this cap and complete immediately.
        /// </summary>
        internal const double MaxSpawnBuildMillisecondsPerAdvance = 4.0;

        /// <summary>
        /// Wall-clock seconds a dying overlap-cycle explosion remains visible
        /// before its GameObject is destroyed.
        /// </summary>
        internal const double OverlapExplosionHoldSeconds = 3.0;

        /// <summary>
        /// Distance buffer added to the LOD simplified tier before prewarming
        /// hidden ghost visuals. Prevents last-second builds at the
        /// full-fidelity boundary.
        /// </summary>
        internal const double HiddenGhostVisibleTierPrewarmBufferMeters = 5000.0;

        /// <summary>
        /// Lookahead window (seconds) used when deciding whether an upcoming
        /// part event should trigger a hidden-ghost prewarm.
        /// </summary>
        internal const double HiddenGhostEventPrewarmLookaheadSeconds = 2.0;

        /// <summary>
        /// Initial post-activation window during which ghost visible frames
        /// are clamped to a tight range to avoid spawn-time pop.
        /// </summary>
        internal const double InitialVisibleFrameClampWindowSeconds = 0.25;
    }

    /// <summary>
    /// Loop period, cycle sizing, boundary tolerances. Consumed by
    /// <see cref="GhostPlaybackLogic"/>, <see cref="GhostPlaybackEngine"/>,
    /// <see cref="ParsekKSC"/>, <see cref="Recording"/>,
    /// <see cref="RecordingOptimizer"/>, and the recording-settings UI.
    /// </summary>
    internal static class LoopTiming
    {
        /// <summary>
        /// User-facing default for fresh <c>ParsekSettings.autoLoopIntervalSeconds</c>
        /// and the Settings "reset" path. Also the engine fallback when a
        /// trajectory's loop interval is NaN / infinite / unset. NOT an
        /// "untouched" sentinel — see <see cref="UntouchedLoopIntervalSentinel"/>.
        /// </summary>
        internal const double DefaultLoopIntervalSeconds = 30.0;

        /// <summary>
        /// Sentinel value used by <c>RecordingOptimizer.CanAutoMerge</c> to
        /// detect an uncustomized loop interval on a <see cref="Recording"/>,
        /// and by <c>Recording.LoopIntervalSeconds</c> as its field initializer.
        /// The two MUST stay equal — a fresh Recording whose loop settings the
        /// user never touched must compare equal to this constant, otherwise
        /// the optimizer treats it as user-customized and refuses to auto-merge.
        /// Deliberately decoupled from <see cref="DefaultLoopIntervalSeconds"/>
        /// so changing the user-facing default doesn't silently break
        /// auto-merge for legacy saves.
        /// </summary>
        internal const double UntouchedLoopIntervalSentinel = 10.0;

        /// <summary>Minimum loop duration (seconds) a recording must have to be loop-eligible.</summary>
        internal const double MinLoopDurationSeconds = 1.0;

        /// <summary>
        /// Minimum user-requested loop period / minimum cycle duration. Periods
        /// below this floor are clamped at UI input and by engine math. 5s is
        /// the smallest value that lets a typical KSP rocket clear launch
        /// clamps between successive cycles, and matches the 5 m/s first-motion
        /// threshold used by <c>TrajectoryMath.FindFirstMovingPoint</c> so the
        /// static-pad visual window of one cycle no longer overlaps the next.
        /// </summary>
        internal const double MinCycleDuration = 5.0;

        /// <summary>
        /// #410: shared boundary tolerance for loop-phase comparisons. Used by
        /// ComputeLoopPhaseFromUT and TryComputeLoopPlaybackUT to keep both
        /// helpers in sync on whether the ghost is "still playing the final
        /// frame" vs "entered the pause window" at exact cycle-boundary UTs.
        /// </summary>
        internal const double BoundaryEpsilon = 1e-6;

        /// <summary>Minimum lead time (seconds) for scheduling early debris explosions.</summary>
        internal const double MinEarlyDebrisExplosionLeadSeconds = 0.25;
    }

    /// <summary>
    /// Time-warp thresholds for suppressing FX / hiding ghosts at high warp.
    /// KSP rails warp levels: 1, 5, 10, 50, 100, 1000, 10000, 100000.
    /// </summary>
    internal static class WarpThresholds
    {
        /// <summary>
        /// Warp level above which explosions / puffs / reentry / RCS are
        /// suppressed (10× is the last level where they look reasonable).
        /// </summary>
        internal const float FxSuppress = 10f;

        /// <summary>
        /// Warp level above which ghost meshes are hidden entirely (50× is the
        /// last level where ghost meshes update often enough to be useful).
        /// </summary>
        internal const float GhostHide = 50f;
    }

    /// <summary>
    /// Watch-mode grace windows, camera entry defaults, and pending-bridge
    /// frame budgets. Consumed by <see cref="WatchModeController"/>.
    /// </summary>
    internal static class WatchMode
    {
        /// <summary>
        /// Grace period before zone-based watch-mode exit. Prevents immediate
        /// exit when a ghost briefly crosses a zone boundary at watch-mode start.
        /// </summary>
        internal const float ZoneGraceSeconds = 2f;

        /// <summary>
        /// Grace window after pending-watch activation during which the
        /// continuation check still considers the watch active. Keeps
        /// back-to-back overlap handoffs from tripping a premature decline.
        /// </summary>
        internal const float PendingPostActivationGraceSeconds = 2f;

        /// <summary>
        /// Upper bound on how long a pending-watch hold may persist. Guards
        /// against runaway holds if the expected continuation never arrives.
        /// </summary>
        internal const float MaxPendingHoldSeconds = 45f;

        /// <summary>
        /// Default camera-to-ghost distance (meters) when watch mode first
        /// binds to a ghost. Overrides KSP's stock [75, 400] entry clamp.
        /// </summary>
        internal const float EntryDistance = 50f;

        /// <summary>Default camera pitch (degrees above horizon) on watch-mode entry.</summary>
        internal const float EntryPitchDegrees = 12f;

        /// <summary>Default camera heading (degrees) on watch-mode entry.</summary>
        internal const float EntryHeadingDegrees = 0f;

        /// <summary>
        /// How many frames the pending-watch bridge may linger while waiting
        /// for the next overlap cycle to take over the camera target before
        /// giving up and releasing the bridge.
        /// </summary>
        internal const int MaxPendingOverlapBridgeFrames = 3;
    }
}
