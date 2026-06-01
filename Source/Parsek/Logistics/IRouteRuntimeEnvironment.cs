namespace Parsek.Logistics
{
    /// <summary>
    /// Delegate surface for runtime queries the pure dispatch evaluator cannot
    /// perform itself. Implementations supply live KSP state (live vessels,
    /// career funds, game mode, etc.); tests supply fakes so
    /// <see cref="RouteDispatchEvaluator"/> can be exercised entirely in xUnit.
    /// </summary>
    internal interface IRouteRuntimeEnvironment
    {
        /// <summary>True when the current game is Career mode.</summary>
        bool IsCareer { get; }

        /// <summary>
        /// Resolve the endpoint to a live vessel. Returns <c>false</c> on miss
        /// with a human-readable reason; the <c>out</c> reason is undefined when
        /// the call returns <c>true</c>.
        /// </summary>
        bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason);

        /// <summary>
        /// Resolve the endpoint to a live <see cref="Vessel"/> reference. Same
        /// contract as <see cref="TryResolveEndpoint"/> for the success flag and
        /// reason text, but additionally returns the resolved vessel so the
        /// delivery applier can write to its parts or its <c>protoVessel</c>.
        /// On miss <c>vessel</c> is <c>null</c>.
        /// </summary>
        bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason);

        /// <summary>
        /// True if the origin currently has the manifest's required resources.
        /// KSC origin always returns <c>true</c> (funds are checked separately
        /// via <see cref="KscFundsAvailable"/>).
        /// </summary>
        bool OriginHasCargo(Route route, out string lackingResource);

        /// <summary>
        /// True if (Career mode AND KSC origin) has funds for the dispatch cost.
        /// Sandbox, Science, and non-KSC origins always return <c>true</c>; the
        /// evaluator avoids calling this entirely in those cases.
        /// </summary>
        bool KscFundsAvailable(Route route, out double shortfall);

        /// <summary>
        /// True if every stop's destination has capacity for the cost manifest.
        /// Returns <c>false</c> with the first full resource name as the out
        /// parameter when at least one destination is full.
        /// </summary>
        bool DestinationHasCapacity(Route route, out string fullResource);

        /// <summary>
        /// True if every <see cref="RouteSourceRef.RecordingId"/> resolves in
        /// ERS. Defensive cross-check; <see cref="RouteStore.RevalidateSources"/>
        /// owns the canonical validation.
        /// </summary>
        bool RouteHasValidSourcesInErs(Route route);
    }
}
