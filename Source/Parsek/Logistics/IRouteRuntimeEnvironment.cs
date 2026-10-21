namespace Parsek.Logistics
{
    /// <summary>
    /// Delegate surface for runtime queries the pure dispatch evaluator cannot
    /// perform itself. Implementations supply live KSP state (live vessels,
    /// resource stores, etc.); tests supply fakes so
    /// <see cref="RouteDispatchEvaluator"/> can be exercised entirely in xUnit.
    /// </summary>
    internal interface IRouteRuntimeEnvironment
    {
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
        /// KSC origin always returns <c>true</c> (a Space Center origin is free
        /// and has no cargo to gate).
        ///
        /// <para><paramref name="shortfall"/> is the missing AMOUNT of
        /// <paramref name="lackingResource"/> (need minus stored, in the
        /// resource's own units): it is the only
        /// channel the number has, because the hold token is a legibility
        /// string and must never be parsed for a magnitude. <c>0</c> means
        /// "unknown / not a resource shortfall": the short is an inventory
        /// (stored-part) short, or the endpoint could not be resolved.
        /// Undefined when the call returns <c>true</c>.</para>
        /// </summary>
        bool OriginHasCargo(Route route, out string lackingResource, out double shortfall);

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
