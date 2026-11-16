namespace Parsek
{
    /// <summary>
    /// Where a <see cref="GhostPlaybackEngine"/> counts its ghost lifecycle health events and
    /// publishes its per-frame playback budget. The engine never names the static
    /// <see cref="DiagnosticsState"/> or <see cref="DiagnosticsComputation"/> directly, so a
    /// second engine in the same scene can be given a sink that does not feed the player's own
    /// diagnostics. Each count member is one count of one event; a sink must not throw.
    /// </summary>
    internal interface IGhostPlaybackDiagnosticsSink
    {
        /// <summary>A ghost visual build completed in this call.</summary>
        void CountGhostBuild();

        /// <summary>A reentry-capable ghost deferred its reentry FX build to its first atmospheric frame.</summary>
        void CountReentryFxDeferred();

        /// <summary>A ghost skipped its reentry FX build (it cannot produce reentry visuals).</summary>
        void CountReentryFxSkipped();

        /// <summary>A deferred reentry FX build produced its FX objects.</summary>
        void CountReentryFxBuild();

        /// <summary>A loop-cycle boundary reused the existing ghost instead of destroy and spawn.</summary>
        void CountGhostReusedAcrossCycle();

        /// <summary>A primary ghost was destroyed.</summary>
        void CountGhostDestroy();

        /// <summary>
        /// The frame had nothing to play (no trajectories, no positioner, or a flags array
        /// shorter than the trajectory list): the published budget returns to empty.
        /// </summary>
        void ClearPlaybackBudget();

        /// <summary>The finished frame's playback budget, published after the stopwatches stop.</summary>
        void PublishPlaybackBudget(
            long totalMicroseconds,
            long spawnMicroseconds,
            long destroyMicroseconds,
            int ghostsProcessed,
            float warpRate,
            GhostObservability ghostObservability);

        /// <summary>One sample of the rolling frame-time history, stamped with wall-clock seconds.</summary>
        void AppendPlaybackFrame(float realtime, long totalMicroseconds);

        /// <summary>
        /// The frame-budget threshold check (and its one-shot phase breakdowns) for the
        /// finished frame; runs last, after the budget and the history sample.
        /// </summary>
        void CheckPlaybackBudgetThreshold(
            long totalMicroseconds, int ghostsProcessed, float warpRate, PlaybackBudgetPhases phases);
    }

    /// <summary>
    /// The sink of the player's own playback engine: each count is one increment of the
    /// session health counters in <see cref="DiagnosticsState.health"/>, the budget lands in
    /// <see cref="DiagnosticsState.playbackBudget"/> and
    /// <see cref="DiagnosticsState.playbackFrameHistory"/>, and the threshold check is
    /// <see cref="DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown"/>; the
    /// diagnostics report and the run index read all of them. Stateless; each engine builds
    /// its own instance.
    /// </summary>
    internal sealed class GlobalGhostPlaybackDiagnosticsSink : IGhostPlaybackDiagnosticsSink
    {
        public void CountGhostBuild()
        {
            DiagnosticsState.health.ghostBuildsThisSession++;
        }

        public void CountReentryFxDeferred()
        {
            DiagnosticsState.health.reentryFxDeferredThisSession++;
        }

        public void CountReentryFxSkipped()
        {
            DiagnosticsState.health.reentryFxSkippedThisSession++;
        }

        public void CountReentryFxBuild()
        {
            DiagnosticsState.health.reentryFxBuildsThisSession++;
        }

        public void CountGhostReusedAcrossCycle()
        {
            DiagnosticsState.health.ghostReusedAcrossCycleThisSession++;
        }

        public void CountGhostDestroy()
        {
            DiagnosticsState.health.ghostDestroysThisSession++;
        }

        public void ClearPlaybackBudget()
        {
            DiagnosticsState.playbackBudget = default;
        }

        public void PublishPlaybackBudget(
            long totalMicroseconds,
            long spawnMicroseconds,
            long destroyMicroseconds,
            int ghostsProcessed,
            float warpRate,
            GhostObservability ghostObservability)
        {
            DiagnosticsState.playbackBudget.totalMicroseconds = totalMicroseconds;
            DiagnosticsState.playbackBudget.spawnMicroseconds = spawnMicroseconds;
            DiagnosticsState.playbackBudget.destroyMicroseconds = destroyMicroseconds;
            DiagnosticsState.playbackBudget.ghostsProcessed = ghostsProcessed;
            DiagnosticsState.playbackBudget.warpRate = warpRate;
            DiagnosticsState.playbackBudget.ghostObservability = ghostObservability;
        }

        public void AppendPlaybackFrame(float realtime, long totalMicroseconds)
        {
            DiagnosticsState.playbackFrameHistory.Append(
                realtime, totalMicroseconds);
        }

        public void CheckPlaybackBudgetThreshold(
            long totalMicroseconds, int ghostsProcessed, float warpRate, PlaybackBudgetPhases phases)
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                totalMicroseconds, ghostsProcessed, warpRate, phases);
        }
    }
}
