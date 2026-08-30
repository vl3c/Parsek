using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// The ONE production funnel that turns a <see cref="RouteCandidate"/> into a
    /// stored, Paused <see cref="Route"/>: build through
    /// <see cref="RouteBuilder.BuildRoute"/>, store through
    /// <see cref="RouteStore.AddRoute"/>, then run the design-§0.6 mutual-exclusion
    /// manual-loop clear (<see cref="RouteTreeGuard.ForceClearManualLoopForRoute"/>).
    ///
    /// <para><b>Why it exists.</b> Before the logistics seam verbs landed, that
    /// three-call sequence lived inline in
    /// <c>LogisticsWindowUI.CreateRouteFromCandidate</c> - a PRIVATE instance method
    /// on a UI window, so nothing outside the window could create a route the way a
    /// player does. The <c>RouteCommand action=create</c> seam verb needs exactly that
    /// path (a driven lane that re-derived build+add would certify a sequence no
    /// player performs), so the sequence moved here and the window calls it. The
    /// window keeps everything that is genuinely presentation - the interval resolve,
    /// the "manual loop turned off" toast, its own grep-stable
    /// <c>Logistics: Create Route from candidate ...</c> Info line - and the caller
    /// gets <see cref="RouteCreateOutcome.ManualLoopsCleared"/> back so that toast
    /// decision is unchanged.</para>
    ///
    /// <para><b>Deliberately not folded in:</b> the interval. Both UI paths resolve
    /// their default through <c>RouteCreationDialog.ComputeRootToUndockSpan</c> and
    /// the seam verb accepts an explicit override, so the interval is an INPUT here
    /// and this file never picks one - that keeps a single builder call shape and
    /// leaves "what interval does a create get" owned by exactly one helper.</para>
    /// </summary>
    internal static class RouteCreationService
    {
        private const string Tag = "Route";

        /// <summary>Reject reason when the candidate / analysis / tree is null.</summary>
        internal const string NullCandidateReason = "null-candidate";

        /// <summary>Result of <see cref="CreatePausedFromCandidate"/>.</summary>
        internal struct RouteCreateOutcome
        {
            /// <summary>The stored route, or null when the builder rejected.</summary>
            internal Route Route;

            /// <summary>The builder's reject reason (verbatim) when <see cref="Route"/>
            /// is null; null on success.</summary>
            internal string RejectReason;

            /// <summary>How many manual loops the mutual-exclusion guard turned off.
            /// The caller decides whether to surface that (the window toasts).</summary>
            internal int ManualLoopsCleared;

            /// <summary>The interval that was handed to the builder (echoed so a
            /// caller can report it without recomputing).</summary>
            internal double IntervalSeconds;
        }

        /// <summary>
        /// Build + store a PAUSED route from <paramref name="candidate"/>, then clear
        /// any manual loop on its source tree(s). Created Paused (never Active) for
        /// the window's stated reason: the operator verifies with Send Once before
        /// turning on periodic dispatch, and an <c>activate</c> is a separate,
        /// explicit act.
        /// </summary>
        /// <param name="candidate">The eligible candidate (tree + analysis).</param>
        /// <param name="name">Route name, or null/empty to let the builder generate one.</param>
        /// <param name="intervalSeconds">Dispatch interval handed to the builder; it is
        /// snapped to <c>N * TransitDuration</c> there.</param>
        /// <param name="mode">The game mode (career cost legs).</param>
        /// <param name="currentUT">Live UT for the manual-loop clear.</param>
        internal static RouteCreateOutcome CreatePausedFromCandidate(
            RouteCandidate candidate,
            string name,
            double intervalSeconds,
            Game.Modes mode,
            double currentUT)
        {
            if (candidate == null || candidate.Analysis == null || candidate.Tree == null)
            {
                ParsekLog.Warn(Tag,
                    "CreatePausedFromCandidate: null candidate/analysis/tree, ignored");
                return new RouteCreateOutcome
                {
                    RejectReason = NullCandidateReason,
                    IntervalSeconds = intervalSeconds
                };
            }

            var inputs = new RouteBuilder.RouteCreationInputs
            {
                Name = string.IsNullOrEmpty(name) ? null : name,
                DispatchIntervalSeconds = intervalSeconds
            };

            RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                candidate.Analysis, candidate.Tree, inputs, mode,
                idFactory: null,
                initialStatus: RouteStatus.Paused,
                // Belt-and-suspenders, carried over verbatim from the window path:
                // the dock-based span should never be below transit, but a degenerate
                // span must never reject an operator-initiated create.
                allowIntervalBelowTransit: true);

            if (outcome.Route == null)
            {
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "CreatePausedFromCandidate rejected tree={0} interval={1}s reason={2}",
                    RouteIds.Short(candidate.Tree.Id),
                    intervalSeconds.ToString("R", CultureInfo.InvariantCulture),
                    outcome.RejectReason ?? "<none>"));
                return new RouteCreateOutcome
                {
                    RejectReason = outcome.RejectReason,
                    IntervalSeconds = intervalSeconds
                };
            }

            RouteStore.AddRoute(outcome.Route);
            // Mutual exclusion (design §0.6): a tree is EITHER a supply route OR a
            // manually looped recording/mission. Route looping wins.
            int cleared = RouteTreeGuard.ForceClearManualLoopForRoute(outcome.Route, currentUT);

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "CreatePausedFromCandidate created tree={0} route={1} name='{2}' " +
                "status={3} interval={4}s transit={5}s cadence={6} manualLoopsCleared={7}",
                RouteIds.Short(candidate.Tree.Id),
                RouteIds.Short(outcome.Route.Id),
                outcome.Route.Name ?? string.Empty,
                outcome.Route.Status,
                outcome.Route.DispatchInterval.ToString("R", CultureInfo.InvariantCulture),
                outcome.Route.TransitDuration.ToString("R", CultureInfo.InvariantCulture),
                outcome.Route.CadenceMultiplier.ToString(CultureInfo.InvariantCulture),
                cleared.ToString(CultureInfo.InvariantCulture)));

            return new RouteCreateOutcome
            {
                Route = outcome.Route,
                ManualLoopsCleared = cleared,
                IntervalSeconds = intervalSeconds
            };
        }
    }
}
