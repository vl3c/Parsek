using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek.TestCommands
{
    /// <summary>Why a <c>RouteCommand action=create</c> could not pick up a tree as a
    /// candidate. Ordered exactly like <c>RouteCandidateFinder.DeriveCandidates</c>'
    /// own gate walk so the token a lane sees names the FIRST gate that closed.</summary>
    internal enum RouteCreateRefusal
    {
        /// <summary>The tree is a candidate; create may proceed.</summary>
        None = 0,

        /// <summary>No committed tree carries that id.</summary>
        UnknownTree = 1,

        /// <summary>The operator (or a prior UI session) dismissed this candidate.</summary>
        CandidateDismissed = 2,

        /// <summary>Not every recording is <c>MergeState.Immutable</c> - run
        /// <c>SealSlot</c> first.</summary>
        TreeNotSealed = 3,

        /// <summary>The route analysis rejected the tree; the status rides along.</summary>
        CandidateIneligible = 4,

        /// <summary>Its source recording is already promoted to a stored route.</summary>
        CandidateAlreadyPromoted = 5,
    }

    /// <summary>Pure result of <see cref="TestCommandRouteCommand.ResolveRoute"/>.</summary>
    internal struct RouteSelection
    {
        internal Route Route;

        /// <summary>The REJECTED msg token, or null when exactly one route matched.</summary>
        internal string RejectReason;

        /// <summary>How many routes matched the winning tier (for the log line).</summary>
        internal int Matches;

        /// <summary>Which tier matched: <c>id</c>, <c>id-prefix</c> or <c>name</c>.</summary>
        internal string MatchKind;

        internal bool Ok => RejectReason == null && Route != null;
    }

    /// <summary>
    /// Pure decision half of the <c>RouteCommand</c> seam verb (the SIXTH strict
    /// promotion out of the M-A2 reserved list). The Unity applier
    /// (<c>ParsekTestCommandAddon.RouteCommand.cs</c>) owns the
    /// <c>RouteCreationService</c> / <c>RouteOrchestrator</c> calls; the arg grammar,
    /// the route selector and the candidate-refusal classification live here so they
    /// are xUnit-covered without KSP.
    ///
    /// <para><b>Sub-command shape.</b> One verb, an <c>action=</c> arg, exactly like
    /// <c>KscAction</c>. v1 actions are <c>create</c> (the Logistics window's "Create
    /// Route" button, driven through <c>RouteCreationService</c>) plus the three
    /// per-route operations the window's row buttons and the in-game
    /// <c>RouteRewindTimeline</c> cells drive: <c>send-once</c>, <c>pause</c>,
    /// <c>activate</c>.</para>
    /// </summary>
    internal static class TestCommandRouteCommand
    {
        // ---- actions ----

        internal const string ActionCreate = "create";
        internal const string ActionSendOnce = "send-once";
        internal const string ActionPause = "pause";
        internal const string ActionActivate = "activate";

        /// <summary>The v1 action vocabulary, in wire spelling. Case-sensitive and
        /// exact, like every other fail-closed arg parse on this seam.</summary>
        internal static readonly string[] KnownActions =
        {
            ActionCreate, ActionSendOnce, ActionPause, ActionActivate
        };

        // ---- reject tokens ----

        /// <summary><c>action=</c> absent. Shares <c>KscAction</c>'s token.</summary>
        internal const string MissingArgReason = "missing-arg";

        /// <summary><c>action=</c> present but not one of <see cref="KnownActions"/>.
        /// Shares <c>KscAction</c>'s token.</summary>
        internal const string UnknownActionReason = "unknown-action";

        /// <summary><c>create</c> without <c>tree=</c>. Shares the loop lanes' token.</summary>
        internal const string TreeArgMissingReason = "tree-arg-missing";

        /// <summary>An operation action without <c>route=</c>.</summary>
        internal const string RouteArgMissingReason = "route-arg-missing";

        /// <summary>No stored route matched the selector.</summary>
        internal const string UnknownRouteReason = "unknown-route";

        /// <summary>More than one stored route matched the selector.</summary>
        internal const string RouteAmbiguousReason = "route-ambiguous";

        /// <summary><c>interval=</c> present and not a finite positive
        /// InvariantCulture double. Shares <c>MissionConfig</c>'s token.</summary>
        internal const string IntervalArgInvalidReason = "interval-arg-invalid";

        /// <summary>The tree is not fully sealed (run <c>SealSlot</c> first).</summary>
        internal const string TreeNotSealedReason = "tree-not-sealed";

        /// <summary>Route analysis rejected the tree; the <c>RouteAnalysisStatus</c>
        /// rides as the compound tail.</summary>
        internal const string CandidateIneligibleReason = "candidate-ineligible";

        /// <summary>The source recording is already promoted to a stored route.</summary>
        internal const string CandidateAlreadyPromotedReason = "candidate-already-promoted";

        /// <summary>The candidate was dismissed in the Logistics window.</summary>
        internal const string CandidateDismissedReason = "candidate-dismissed";

        /// <summary>The builder declined; its own reason rides as the compound tail.</summary>
        internal const string RouteBuildRejectedReason = "route-build-rejected";

        /// <summary>The orchestrator declined the operation (wrong status for the
        /// action); the action rides as the compound tail and the observed status goes
        /// in the payload.</summary>
        internal const string RouteActionRefusedReason = "route-action-refused";

        // ---- arg parsing ----

        /// <summary>Fail-closed, case-sensitive action parse (the <c>scene=</c> /
        /// <c>site=</c> convention). Pure.</summary>
        internal static bool IsKnownAction(string action)
        {
            if (string.IsNullOrEmpty(action))
                return false;
            for (int i = 0; i < KnownActions.Length; i++)
                if (string.Equals(KnownActions[i], action, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>True when the action addresses an EXISTING route (needs
        /// <c>route=</c>) rather than creating one. Pure.</summary>
        internal static bool IsRouteOperation(string action)
        {
            return string.Equals(action, ActionSendOnce, StringComparison.Ordinal)
                || string.Equals(action, ActionPause, StringComparison.Ordinal)
                || string.Equals(action, ActionActivate, StringComparison.Ordinal);
        }

        /// <summary>
        /// Optional positive interval. Null/empty parses to 0.0, the "use the route's
        /// snapped minimum" sentinel (<c>MissionConfig.TryParseIntervalArg</c>'s exact
        /// shape); a present value must be a finite positive InvariantCulture double.
        /// Pure.
        /// </summary>
        internal static bool TryParseIntervalArg(string raw, out double seconds)
        {
            seconds = 0.0;
            if (string.IsNullOrEmpty(raw))
                return true;
            if (!double.TryParse(raw, NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out seconds))
                return false;
            return !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds > 0.0;
        }

        /// <summary>Optional route name: null/empty means "let the builder name it".
        /// Pure.</summary>
        internal static string ResolveName(string raw)
        {
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        // ---- route selector ----

        /// <summary>
        /// Resolve a stored route from a caller-supplied selector, in three tiers:
        /// (1) exact <c>Id</c>, (2) unique <c>Id</c> PREFIX, (3) unique exact
        /// <c>Name</c>. A tier that matches more than once is
        /// <see cref="RouteAmbiguousReason"/> rather than an arbitrary pick; nothing
        /// matched at all is <see cref="UnknownRouteReason"/>.
        ///
        /// <para><b>Why a prefix tier exists.</b> A route id is a bare
        /// <c>Guid.NewGuid().ToString("N")</c> minted at create time, so a spec cannot
        /// pin one; what a lane HAS is the 8-char short id every log line prints
        /// (<c>RouteIds.Short</c>) or the create step's own OK payload. The prefix tier
        /// makes the logged handle directly usable. Prefix matching is
        /// ordinal-ignore-case because the ids are hex.</para>
        ///
        /// <para>The exact-id tier runs FIRST and alone: an id that IS a route wins
        /// even if it also prefixes another, which is the only way an exact handle can
        /// never be made ambiguous by an unrelated route appearing later.</para>
        /// Pure.
        /// </summary>
        internal static RouteSelection ResolveRoute(IReadOnlyList<Route> routes, string selector)
        {
            if (string.IsNullOrEmpty(selector))
                return new RouteSelection { RejectReason = RouteArgMissingReason };
            if (routes == null || routes.Count == 0)
                return new RouteSelection { RejectReason = UnknownRouteReason };

            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r != null && string.Equals(r.Id, selector, StringComparison.Ordinal))
                    return new RouteSelection { Route = r, Matches = 1, MatchKind = "id" };
            }

            RouteSelection byPrefix = SingleMatch(
                routes, selector, "id-prefix",
                (r, s) => !string.IsNullOrEmpty(r.Id)
                          && r.Id.StartsWith(s, StringComparison.OrdinalIgnoreCase));
            if (byPrefix.Ok || byPrefix.RejectReason == RouteAmbiguousReason)
                return byPrefix;

            RouteSelection byName = SingleMatch(
                routes, selector, "name",
                (r, s) => string.Equals(r.Name, s, StringComparison.Ordinal));
            if (byName.Ok || byName.RejectReason == RouteAmbiguousReason)
                return byName;

            return new RouteSelection { RejectReason = UnknownRouteReason };
        }

        private static RouteSelection SingleMatch(
            IReadOnlyList<Route> routes, string selector, string kind,
            Func<Route, string, bool> predicate)
        {
            Route hit = null;
            int matches = 0;
            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r == null || !predicate(r, selector))
                    continue;
                matches++;
                if (hit == null)
                    hit = r;
            }
            if (matches == 1)
                return new RouteSelection { Route = hit, Matches = 1, MatchKind = kind };
            if (matches > 1)
                return new RouteSelection
                {
                    RejectReason = RouteAmbiguousReason,
                    Matches = matches,
                    MatchKind = kind
                };
            return new RouteSelection { RejectReason = UnknownRouteReason, Matches = 0 };
        }

        // ---- create refusal classification ----

        /// <summary>
        /// Which candidacy gate closed, walked in
        /// <c>RouteCandidateFinder.DeriveCandidates</c>' own order (dismissed, sealed,
        /// eligible, already-promoted) so the token names the FIRST closed gate and a
        /// lane's fix is the right one. Pure.
        /// </summary>
        internal static RouteCreateRefusal ClassifyCreateRefusal(
            bool treeFound,
            bool dismissed,
            bool treeSealed,
            bool analysisEligible,
            bool alreadyPromoted)
        {
            if (!treeFound) return RouteCreateRefusal.UnknownTree;
            if (dismissed) return RouteCreateRefusal.CandidateDismissed;
            if (!treeSealed) return RouteCreateRefusal.TreeNotSealed;
            if (!analysisEligible) return RouteCreateRefusal.CandidateIneligible;
            if (alreadyPromoted) return RouteCreateRefusal.CandidateAlreadyPromoted;
            return RouteCreateRefusal.None;
        }

        /// <summary>The wire msg token for a refusal. Pure.</summary>
        internal static string RefusalToken(RouteCreateRefusal refusal)
        {
            switch (refusal)
            {
                case RouteCreateRefusal.UnknownTree: return TestCommandSealSlot.UnknownTreeReason;
                case RouteCreateRefusal.CandidateDismissed: return CandidateDismissedReason;
                case RouteCreateRefusal.TreeNotSealed: return TreeNotSealedReason;
                case RouteCreateRefusal.CandidateIneligible: return CandidateIneligibleReason;
                case RouteCreateRefusal.CandidateAlreadyPromoted: return CandidateAlreadyPromotedReason;
                default: return null;
            }
        }

        /// <summary>
        /// The full refusal msg, carrying the analysis status as a compound tail on the
        /// ineligible branch (<c>candidate-ineligible MissingRouteProof</c>; the wire
        /// percent-encodes the space and the harness classifies off the head token, the
        /// <c>refly-gate</c> shape). Pure.
        /// </summary>
        internal static string RefusalMsg(RouteCreateRefusal refusal, RouteAnalysisStatus status)
        {
            string token = RefusalToken(refusal);
            if (token == null)
                return null;
            return refusal == RouteCreateRefusal.CandidateIneligible
                ? token + " " + status
                : token;
        }

        /// <summary>Compound msg for a builder decline. Pure.</summary>
        internal static string BuildRejectedMsg(string builderReason)
        {
            return RouteBuildRejectedReason + " "
                   + (string.IsNullOrEmpty(builderReason) ? "unknown" : builderReason);
        }

        /// <summary>Compound msg for an orchestrator decline. Pure.</summary>
        internal static string ActionRefusedMsg(string action)
        {
            return RouteActionRefusedReason + " "
                   + (string.IsNullOrEmpty(action) ? "unknown" : action);
        }
    }
}
