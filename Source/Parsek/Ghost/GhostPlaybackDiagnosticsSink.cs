namespace Parsek
{
    /// <summary>
    /// Where a <see cref="GhostPlaybackEngine"/> counts its ghost lifecycle health events.
    /// The engine never writes the static <see cref="DiagnosticsState"/> directly, so a second
    /// engine in the same scene can be given a sink that does not feed the player's own
    /// diagnostics. Each member is one count of one event; a sink must not throw.
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
    }

    /// <summary>
    /// The sink of the player's own playback engine: each count is one increment of the
    /// session health counters in <see cref="DiagnosticsState.health"/>, which the diagnostics
    /// report and the run index read. Stateless; each engine builds its own instance.
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
    }
}
