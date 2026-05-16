namespace Parsek.Logistics
{
    /// <summary>
    /// Discrete outcomes returned by <see cref="RouteDispatchEvaluator.EvaluateRoute"/>.
    /// The orchestrator interprets the outcome and applies the transition + logging;
    /// the evaluator itself is pure and free of side effects.
    /// </summary>
    internal enum RouteDispatchOutcome
    {
        /// <summary>Benign no-op: route not due, paused, in-transit pending, rate-limited, etc.</summary>
        Skip,

        /// <summary>All conditions met — orchestrator emits dispatch actions and advances state.</summary>
        Dispatch,

        /// <summary>Origin lacks the manifest's required resources.</summary>
        WaitResources,

        /// <summary>Career KSC-origin route lacks dispatch funds.</summary>
        WaitFunds,

        /// <summary>
        /// At least one stop's destination has no capacity for the cost manifest.
        /// Per design §10.4, the orchestrator must NOT advance <c>NextDispatchUT</c>
        /// for this outcome — only <c>NextEligibilityCheckUT</c>.
        /// </summary>
        WaitDestinationFull,

        /// <summary>An endpoint vessel could not be resolved (PID miss + no surface fallback).</summary>
        EndpointLost,

        /// <summary>
        /// In-transit cycle has reached the transit-duration boundary. The
        /// orchestrator marks <c>PendingDeliveryUT</c> and emits delivery actions.
        /// </summary>
        InTransitComplete,
    }

    /// <summary>
    /// Output of <see cref="RouteDispatchEvaluator.EvaluateRoute"/>. Pure data;
    /// the orchestrator applies the transition. Construct via the static
    /// helpers — they pin the canonical (outcome, NextStatus, reason) triples.
    /// </summary>
    internal readonly struct RouteDispatchDecision
    {
        /// <summary>What the evaluator decided.</summary>
        public readonly RouteDispatchOutcome Outcome;

        /// <summary>
        /// Status the orchestrator should transition the route to, or <c>null</c>
        /// when the outcome does not imply a status change (e.g.,
        /// <see cref="RouteDispatchOutcome.Skip"/>, <see cref="RouteDispatchOutcome.InTransitComplete"/>).
        /// </summary>
        public readonly RouteStatus? NextStatus;

        /// <summary>
        /// New <c>Route.NextEligibilityCheckUT</c> value, or <c>null</c> when the
        /// outcome does not request a retry rate-limit update.
        /// </summary>
        public readonly double? NewNextEligibilityCheckUT;

        /// <summary>Short stable reason token used in log lines.</summary>
        public readonly string Reason;

        public RouteDispatchDecision(
            RouteDispatchOutcome outcome,
            RouteStatus? nextStatus,
            double? nextEligibilityCheckUT,
            string reason)
        {
            Outcome = outcome;
            NextStatus = nextStatus;
            NewNextEligibilityCheckUT = nextEligibilityCheckUT;
            Reason = reason ?? string.Empty;
        }

        internal static RouteDispatchDecision Skip(string reason) =>
            new RouteDispatchDecision(RouteDispatchOutcome.Skip, null, null, reason);

        internal static RouteDispatchDecision Dispatch() =>
            new RouteDispatchDecision(RouteDispatchOutcome.Dispatch, RouteStatus.InTransit, null, "all-conditions-met");

        internal static RouteDispatchDecision InTransitComplete() =>
            new RouteDispatchDecision(RouteDispatchOutcome.InTransitComplete, null, null, "transit-duration-elapsed");

        internal static RouteDispatchDecision WaitResources(double retryUT, string lacking) =>
            new RouteDispatchDecision(RouteDispatchOutcome.WaitResources, RouteStatus.WaitingForResources, retryUT, $"origin-lacks-{lacking}");

        internal static RouteDispatchDecision WaitFunds(double retryUT, double shortfall) =>
            new RouteDispatchDecision(
                RouteDispatchOutcome.WaitFunds,
                RouteStatus.WaitingForFunds,
                retryUT,
                $"funds-shortfall-{shortfall.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}");

        internal static RouteDispatchDecision WaitDestinationFull(double retryUT, string fullResource) =>
            new RouteDispatchDecision(RouteDispatchOutcome.WaitDestinationFull, RouteStatus.DestinationFull, retryUT, $"destination-full-{fullResource}");

        internal static RouteDispatchDecision EndpointLost(double retryUT, string reason) =>
            new RouteDispatchDecision(RouteDispatchOutcome.EndpointLost, RouteStatus.EndpointLost, retryUT, reason);
    }
}
