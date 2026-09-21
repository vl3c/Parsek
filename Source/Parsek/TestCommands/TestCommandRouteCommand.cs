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

    /// <summary>Why a <c>RouteCommand action=link</c> could not be applied. Each member is
    /// a failure <c>RouteStore.LinkRoutes</c> distinguishes by LOG TEXT ONLY (it returns a
    /// bare false), so the classification is made BEFORE the call and the wire says
    /// which.</summary>
    internal enum RouteLinkRefusal
    {
        /// <summary>The pair may be linked (or is already the same bidirectional pair,
        /// which the store treats as an idempotent success).</summary>
        None = 0,

        /// <summary>Subject and partner are the same route.</summary>
        SelfLink = 1,

        /// <summary>Either endpoint already points at a THIRD route. The store refuses
        /// rather than re-point, so the fix is an <c>action=unlink</c> first.</summary>
        AlreadyLinkedElsewhere = 2,

        /// <summary>No <c>partner=</c> was given and
        /// <c>LogisticsLinkPresentation.BuildLinkCandidates</c> offered nothing.</summary>
        NoCandidate = 3,
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
    /// <c>activate</c>. The round-trip and cadence lane adds three more in the same
    /// route-addressed shape: <c>link</c>, <c>unlink</c>, <c>set-cadence</c>.</para>
    ///
    /// <para><b>EVERY STORE REFUSAL GETS ITS OWN TOKEN.</b> The three production methods
    /// behind the new actions (<c>RouteStore.LinkRoutes</c> / <c>UnlinkRoute</c> /
    /// <c>RouteCadence.ApplyMultiplier</c>) each fold several distinct failures into one
    /// bare <c>false</c> and distinguish them by log text: a self-link, an endpoint linked
    /// elsewhere, an already-unlinked route, an unchanged N. A lane reading one opaque
    /// <c>route-action-refused</c> could not tell a fixture problem from a spec problem, so
    /// the classification is made HERE, before the call, and the reject names the case.
    /// The <c>route-action-refused &lt;action&gt;</c> compound stays as the answer to a
    /// false the classification did not predict.</para>
    ///
    /// <para><b>CADENCE TAKES ITS OWN ARG.</b> <c>interval=</c> already means "seconds
    /// passed to the builder" on <c>action=create</c>, and a key whose unit changed with
    /// the action is how a spec author writes 3 and gets three seconds. So the multiplier
    /// is <c>cadence=</c>, matching the <c>cadence</c> key the create payload already
    /// reports.</para>
    /// </summary>
    internal static class TestCommandRouteCommand
    {
        // ---- actions ----

        internal const string ActionCreate = "create";
        internal const string ActionSendOnce = "send-once";
        internal const string ActionPause = "pause";
        internal const string ActionActivate = "activate";
        internal const string ActionLink = "link";
        internal const string ActionUnlink = "unlink";
        internal const string ActionSetCadence = "set-cadence";

        /// <summary>The action vocabulary, in wire spelling. Case-sensitive and
        /// exact, like every other fail-closed arg parse on this seam.</summary>
        internal static readonly string[] KnownActions =
        {
            ActionCreate, ActionSendOnce, ActionPause, ActionActivate,
            ActionLink, ActionUnlink, ActionSetCadence
        };

        // ---- arg keys ----

        /// <summary>
        /// <c>action=link</c>'s partner route. OPEN valued and resolved through the SAME
        /// three-tier <see cref="ResolveRoute"/> as <c>route=</c>.
        ///
        /// <para>A distinct key because <c>route=</c> names the SUBJECT. ABSENT means "the
        /// first partner <c>LogisticsLinkPresentation.BuildLinkCandidates</c> offers" -
        /// the <c>recording=first</c> rationale, and the only form a COMMITTED spec can
        /// write, since a route id is a fresh Guid minted at create time.</para>
        /// </summary>
        internal const string PartnerArg = "partner";

        /// <summary><c>action=set-cadence</c>'s REQUIRED multiplier N (the number of
        /// transit spans between dispatches). Deliberately NOT <c>interval=</c>, which
        /// already means seconds on <c>action=create</c>.</summary>
        internal const string CadenceArg = "cadence";

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
            return IsStatusOperation(action)
                || string.Equals(action, ActionLink, StringComparison.Ordinal)
                || string.Equals(action, ActionUnlink, StringComparison.Ordinal)
                || string.Equals(action, ActionSetCadence, StringComparison.Ordinal);
        }

        /// <summary>True for the three actions that hand the route to
        /// <c>RouteOrchestrator</c> and report only its status. The rest of
        /// <see cref="IsRouteOperation"/> still addresses an existing route but calls a
        /// different production surface and reports different before/after fields. Pure.
        /// </summary>
        internal static bool IsStatusOperation(string action)
        {
            return string.Equals(action, ActionSendOnce, StringComparison.Ordinal)
                || string.Equals(action, ActionPause, StringComparison.Ordinal)
                || string.Equals(action, ActionActivate, StringComparison.Ordinal);
        }

        /// <summary>
        /// Parses <c>action=set-cadence</c>'s REQUIRED <see cref="CadenceArg"/>: an
        /// InvariantCulture integer of at least 1. Missing and invalid are DISTINCT
        /// rejects, and a sub-1 value is INVALID rather than clamped - see
        /// <see cref="CadenceArgInvalidReason"/>. Pure.
        /// </summary>
        internal static bool TryParseCadenceArg(string raw, out int multiplier,
                                                out string rejectReason)
        {
            multiplier = 0;
            if (string.IsNullOrEmpty(raw))
            {
                rejectReason = CadenceArgMissingReason;
                return false;
            }
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out multiplier)
                || multiplier < 1)
            {
                rejectReason = CadenceArgInvalidReason;
                return false;
            }
            rejectReason = null;
            return true;
        }

        /// <summary>
        /// Maps a <see cref="ResolveRoute"/> reject on the PARTNER selector onto the
        /// partner-scoped token, so one message cannot leave a lane guessing which of the
        /// two selectors missed. Pure.
        /// </summary>
        internal static string MapPartnerReject(string routeReject)
        {
            if (string.Equals(routeReject, RouteAmbiguousReason, StringComparison.Ordinal))
                return PartnerAmbiguousReason;
            return UnknownPartnerReason;
        }

        /// <summary>
        /// Classifies an <c>action=link</c> pair BEFORE <c>RouteStore.LinkRoutes</c> is
        /// called, in that method's own guard order (self, then either endpoint linked
        /// elsewhere).
        ///
        /// <para>An already-bidirectional SAME pair is <see cref="RouteLinkRefusal.None"/>,
        /// not a refusal: the store accepts it as an idempotent true that deliberately
        /// preserves the live alternation cursors, and a lane re-asserting a link must see
        /// that success rather than a complaint.</para>
        /// Pure.
        /// </summary>
        internal static RouteLinkRefusal ClassifyLink(string subjectId, string subjectPartnerId,
                                                      string partnerId, string partnerPartnerId)
        {
            if (string.IsNullOrEmpty(partnerId))
                return RouteLinkRefusal.NoCandidate;
            if (string.Equals(subjectId, partnerId, StringComparison.Ordinal))
                return RouteLinkRefusal.SelfLink;

            bool subjectElsewhere = !string.IsNullOrEmpty(subjectPartnerId)
                && !string.Equals(subjectPartnerId, partnerId, StringComparison.Ordinal);
            bool partnerElsewhere = !string.IsNullOrEmpty(partnerPartnerId)
                && !string.Equals(partnerPartnerId, subjectId, StringComparison.Ordinal);
            if (subjectElsewhere || partnerElsewhere)
                return RouteLinkRefusal.AlreadyLinkedElsewhere;

            return RouteLinkRefusal.None;
        }

        /// <summary>The wire msg token for a link refusal. Pure.</summary>
        internal static string LinkRefusalToken(RouteLinkRefusal refusal)
        {
            switch (refusal)
            {
                case RouteLinkRefusal.SelfLink: return LinkSelfReason;
                case RouteLinkRefusal.AlreadyLinkedElsewhere: return LinkAlreadyLinkedReason;
                case RouteLinkRefusal.NoCandidate: return LinkNoCandidateReason;
                default: return null;
            }
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

        /// <summary><c>action=link</c> named a <c>partner=</c> no stored route matched.
        /// Distinct from <see cref="UnknownRouteReason"/> so a lane knows WHICH of the two
        /// selectors missed.</summary>
        internal const string UnknownPartnerReason = "unknown-partner";

        /// <summary><c>partner=</c> matched more than one stored route.</summary>
        internal const string PartnerAmbiguousReason = "partner-ambiguous";

        /// <summary><c>partner=</c> named the subject itself. The store refuses and logs;
        /// this names it on the wire.</summary>
        internal const string LinkSelfReason = "link-self";

        /// <summary>Subject or partner already points at a THIRD route. The remedy is
        /// <c>action=unlink</c> first, which is why this is not folded into the generic
        /// refusal.</summary>
        internal const string LinkAlreadyLinkedReason = "link-already-linked";

        /// <summary>No <c>partner=</c> and <c>BuildLinkCandidates</c> offered nothing: every
        /// other stored route is already linked, or there is no other route.</summary>
        internal const string LinkNoCandidateReason = "link-no-candidate";

        /// <summary><c>action=unlink</c> on a route that carries no
        /// <c>LinkedRouteId</c>. <c>UnlinkRoute</c> returns FALSE for this, which must not
        /// read as an error: nothing was wrong, there was simply nothing to
        /// break.</summary>
        internal const string RouteNotLinkedReason = "route-not-linked";

        /// <summary><c>action=set-cadence</c> without <c>cadence=</c>.</summary>
        internal const string CadenceArgMissingReason = "cadence-arg-missing";

        /// <summary><c>cadence=</c> present and not an InvariantCulture integer of at least
        /// 1. REJECTED rather than clamped: <c>ApplyMultiplier</c> clamps N &gt;= 1
        /// silently, so a spec that wrote 0 would read as a successful edit to 1.</summary>
        internal const string CadenceArgInvalidReason = "cadence-arg-invalid";

        /// <summary><c>cadence=</c> equals the route's current multiplier.
        /// <c>ApplyMultiplier</c> no-ops false on this, which is not a failure - the route
        /// is already where the lane asked for.</summary>
        internal const string CadenceUnchangedReason = "cadence-unchanged";

        /// <summary>Compound msg for an orchestrator decline. Pure.</summary>
        internal static string ActionRefusedMsg(string action)
        {
            return RouteActionRefusedReason + " "
                   + (string.IsNullOrEmpty(action) ? "unknown" : action);
        }
    }
}
