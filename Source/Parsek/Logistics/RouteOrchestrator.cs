namespace Parsek.Logistics
{
    /// <summary>
    /// Cross-cutting constants for the route dispatch scheduler. The orchestrator
    /// itself (the <c>Tick()</c> driver, save-load hooks, action emission) lands
    /// in a later phase; for now this class is constants-only so Phase 1+2 code
    /// (the pure evaluator, endpoint resolver, and funds calculator) can
    /// reference the canonical values.
    /// </summary>
    internal static class RouteOrchestrator
    {
        /// <summary>Wall-clock cadence at which the orchestrator advances routes.</summary>
        internal const double TickIntervalSec = 1.0;

        /// <summary>
        /// Retry backoff for transient wait states (resources, funds, capacity,
        /// endpoint loss). Sets <c>Route.NextEligibilityCheckUT = currentUT + this</c>.
        /// </summary>
        internal const double WaitRetryIntervalSec = 30.0;

        /// <summary>
        /// Cap on the number of catch-up cycles processed in a single tick when
        /// the route is behind schedule (long unfocused warp). Prevents pathological
        /// per-tick work explosions on resume.
        /// </summary>
        internal const int MaxCatchUpCyclesPerTick = 32;

        /// <summary>
        /// Surface proximity radius (metres) for endpoint resolution fallback. When
        /// the saved <see cref="RouteEndpoint.VesselPersistentId"/> no longer
        /// resolves but the endpoint is surface-typed, the resolver searches for
        /// the closest matching surface vessel within this radius.
        /// </summary>
        internal const double SurfaceProximityRadiusMeters = 2000.0;

        /// <summary>
        /// Canonical <see cref="ParsekLog"/> subsystem tag for every route-subsystem
        /// log line. Do not introduce new tag names — the integration follow-up
        /// unified all route logs under this single tag.
        /// </summary>
        internal const string Tag = "Route";
    }
}
