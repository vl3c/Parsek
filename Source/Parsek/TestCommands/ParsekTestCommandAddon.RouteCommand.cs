using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Logistics-lane partial: the thin Unity applier for the single-phase
    /// <c>RouteCommand</c> verb (the SIXTH strict reserved-name promotion since M-C1 -
    /// the wire token is byte-identical before and after, only the response changes).
    ///
    /// <para><b>WHY THIS VERB EXISTS.</b> Nothing driveable could create or operate a
    /// supply route. Creation lived in a private instance method on the Logistics
    /// window, so every committed route fixture in the suite is a HARVEST of a hand-
    /// flown session and no lane could prove the create path still works. Paired with
    /// <c>SealSlot</c> (which closes the candidacy gate this verb reads), a driven lane
    /// can now take a recorded supply run all the way to a dispatching route.</para>
    ///
    /// <para><b>Every action drives the production path.</b> <c>create</c> goes through
    /// <c>RouteCreationService.CreatePausedFromCandidate</c> - the same
    /// build/store/manual-loop-clear funnel the window's "Create Route" button calls,
    /// with the same <c>initialStatus: Paused</c> and
    /// <c>allowIntervalBelowTransit: true</c> - and the three operations go through
    /// <c>RouteOrchestrator.TrySendOneCycleNow</c> / <c>TryPause</c> /
    /// <c>TryActivate</c>, which are what the window's row buttons and the in-game
    /// <c>RouteRewindTimeline</c> cells call.</para>
    ///
    /// <para><b>SINGLE-PHASE.</b> Build + store + orchestrator arm are all synchronous
    /// state mutation, so the read-back (the route's own status) is a final answer:
    /// no <c>TryComplete*</c> counterpart, no <c>DeferralBudget</c> row, the 60 s
    /// default budget bounding only the game-not-loaded defer.</para>
    ///
    /// <para><b>The round-trip and cadence actions.</b> <c>link</c> / <c>unlink</c> /
    /// <c>set-cadence</c> drive <c>RouteStore.LinkRoutes</c> / <c>UnlinkRoute</c> /
    /// <c>RouteCadence.ApplyMultiplier</c> - the same three methods the Logistics window's
    /// link picker and cadence stepper commit through. All three share the route resolution
    /// and the single-phase shape above. Their partner selection goes through the pure
    /// <c>LogisticsLinkPresentation.BuildLinkCandidates</c> the picker draws from, so a
    /// driven link picks a partner the UI would have OFFERED rather than any route the
    /// store happens to hold.</para>
    ///
    /// <para><b>What this verb still leaves for later:</b> <c>delete</c> and
    /// <c>dismiss</c>. Each is a separate production surface behind its own confirmation
    /// dialog (which <c>UiAction op=raise</c> now RAISES but deliberately cannot confirm -
    /// its press policy admits only Cancel), and neither is on the create-and-run path this
    /// verb exists to unblock. They are additive under the seam's unknown-key rule.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void RouteCommandImpl(ParsedCommand cmd)
        {
            string action = ArgOrNull(cmd, "action");
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "routecommand start action={0} tree={1} route={2} name={3} interval={4} "
                + "partner={5} cadence={6}",
                action ?? string.Empty,
                ArgOrNull(cmd, "tree") ?? string.Empty,
                ArgOrNull(cmd, "route") ?? string.Empty,
                ArgOrNull(cmd, "name") ?? string.Empty,
                ArgOrNull(cmd, "interval") ?? string.Empty,
                ArgOrNull(cmd, TestCommandRouteCommand.PartnerArg) ?? string.Empty,
                ArgOrNull(cmd, TestCommandRouteCommand.CadenceArg) ?? string.Empty));

            if (string.IsNullOrEmpty(action))
            {
                ParsekLog.Warn(Tag,
                    $"routecommand rejected reason={TestCommandRouteCommand.MissingArgReason} (action absent)");
                SetExecResult("REJECTED", null, TestCommandRouteCommand.MissingArgReason);
                return;
            }
            if (!TestCommandRouteCommand.IsKnownAction(action))
            {
                ParsekLog.Warn(Tag,
                    $"routecommand rejected reason={TestCommandRouteCommand.UnknownActionReason} action={action}");
                SetExecResult("REJECTED", null, TestCommandRouteCommand.UnknownActionReason);
                return;
            }

            if (TestCommandRouteCommand.IsRouteOperation(action))
            {
                RouteCommandOperate(cmd, action);
                return;
            }
            // IsKnownAction already filtered everything that is neither an operation
            // nor `create`, so this is the create branch and nothing else.
            RouteCommandCreate(cmd);
        }

        // ----- action=create -----

        private void RouteCommandCreate(ParsedCommand cmd)
        {
            string treeArg = ArgOrNull(cmd, "tree");
            string nameArg = TestCommandRouteCommand.ResolveName(ArgOrNull(cmd, "name"));
            string intervalArg = ArgOrNull(cmd, "interval");

            if (string.IsNullOrEmpty(treeArg))
            {
                ParsekLog.Warn(Tag,
                    $"routecommand rejected reason={TestCommandRouteCommand.TreeArgMissingReason} action=create");
                SetExecResult("REJECTED", null, TestCommandRouteCommand.TreeArgMissingReason);
                return;
            }

            double requestedInterval;
            if (!TestCommandRouteCommand.TryParseIntervalArg(intervalArg, out requestedInterval))
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand rejected reason={0} interval={1}",
                    TestCommandRouteCommand.IntervalArgInvalidReason,
                    intervalArg ?? string.Empty));
                SetExecResult("REJECTED", null,
                    TestCommandRouteCommand.IntervalArgInvalidReason);
                return;
            }

            RecordingTree tree = FindCommittedTreeByIdForSeal(treeArg);
            bool treeFound = tree != null;
            bool dismissed = treeFound && RouteStore.IsCandidateDismissed(treeArg);
            bool treeSealed = treeFound && RouteCandidateFinder.IsTreeFullySealed(tree);

            // Analyse only when the two cheap gates already passed - the analysis is the
            // expensive one, and its status would be meaningless on an unsealed tree.
            RouteAnalysisResult analysis = null;
            bool eligible = false;
            bool alreadyPromoted = false;
            if (treeFound && !dismissed && treeSealed)
            {
                analysis = RouteAnalysisEngine.AnalyzeTree(tree, RouteAnalysisLogMode.Diagnostic);
                eligible = analysis != null && analysis.IsEligible;
                if (eligible)
                    alreadyPromoted = IsSourceAlreadyPromoted(analysis);
            }

            RouteCreateRefusal refusal = TestCommandRouteCommand.ClassifyCreateRefusal(
                treeFound, dismissed, treeSealed, eligible, alreadyPromoted);

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "routecommand create gate tree={0} treeFound={1} dismissed={2} sealed={3} " +
                "eligible={4} status={5} alreadyPromoted={6} refusal={7}",
                treeArg, treeFound, dismissed, treeSealed, eligible,
                analysis != null ? analysis.Status.ToString() : "<not-analysed>",
                alreadyPromoted, refusal));

            if (refusal != RouteCreateRefusal.None)
            {
                string msg = TestCommandRouteCommand.RefusalMsg(
                    refusal,
                    analysis != null ? analysis.Status : RouteAnalysisStatus.Eligible);
                ParsekLog.Warn(Tag, $"routecommand rejected reason={msg} tree={treeArg}");
                SetExecResult("REJECTED", null, msg);
                return;
            }

            var candidate = new RouteCandidate { Tree = tree, Analysis = analysis };

            // Absent interval= means "the route's snapped minimum": the SAME helper both
            // production create paths funnel their default through, so a driven create
            // and a player create produce the identical interval for one analysis.
            double interval = requestedInterval > 0.0
                ? requestedInterval
                : RouteCreationDialog.ComputeRootToUndockSpan(analysis, tree);

            Game.Modes mode = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Mode
                : Game.Modes.SANDBOX;

            RouteCreationService.RouteCreateOutcome outcome =
                RouteCreationService.CreatePausedFromCandidate(
                    candidate, nameArg, interval, mode, TryGetRouteCommandUT());

            if (outcome.Route == null)
            {
                string msg = TestCommandRouteCommand.BuildRejectedMsg(outcome.RejectReason);
                ParsekLog.Warn(Tag, $"routecommand rejected reason={msg} tree={treeArg}");
                SetExecResult("REJECTED", null, msg);
                return;
            }

            Route route = outcome.Route;
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "routecommand complete action=create tree={0} route={1} name='{2}' " +
                "status={3} intervalSeconds={4} transitSeconds={5} cadence={6} stops={7} " +
                "manualLoopsCleared={8}",
                treeArg, route.Id ?? string.Empty, route.Name ?? string.Empty, route.Status,
                route.DispatchInterval.ToString("R", CultureInfo.InvariantCulture),
                route.TransitDuration.ToString("R", CultureInfo.InvariantCulture),
                route.CadenceMultiplier,
                route.Stops != null ? route.Stops.Count : 0,
                outcome.ManualLoopsCleared));

            SetExecResult("OK", Payload(
                Kv("action", TestCommandRouteCommand.ActionCreate),
                Kv("created", Bool(true)),
                Kv("route", route.Id ?? string.Empty),
                Kv("name", route.Name ?? string.Empty),
                Kv("tree", treeArg),
                Kv("status", route.Status.ToString()),
                Kv("intervalSeconds",
                    route.DispatchInterval.ToString("R", CultureInfo.InvariantCulture)),
                Kv("transitSeconds",
                    route.TransitDuration.ToString("R", CultureInfo.InvariantCulture)),
                Kv("cadence", Int(route.CadenceMultiplier)),
                Kv("stops", Int(route.Stops != null ? route.Stops.Count : 0)),
                Kv("manualLoopsCleared", Int(outcome.ManualLoopsCleared))), null);
        }

        /// <summary>True when a stored route already claims this analysis's source
        /// recording - the dedup <c>RouteCandidateFinder</c> applies as its last gate.</summary>
        private static bool IsSourceAlreadyPromoted(RouteAnalysisResult analysis)
        {
            string sourceId = analysis?.SourceRecording?.RecordingId;
            if (string.IsNullOrEmpty(sourceId))
                return false;
            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            if (routes == null)
                return false;
            for (int i = 0; i < routes.Count; i++)
            {
                List<RouteSourceRef> refs = routes[i]?.SourceRefs;
                if (refs == null)
                    continue;
                for (int j = 0; j < refs.Count; j++)
                    if (refs[j] != null && refs[j].RecordingId == sourceId)
                        return true;
            }
            return false;
        }

        // ----- action=send-once | pause | activate -----

        private void RouteCommandOperate(ParsedCommand cmd, string action)
        {
            string routeArg = ArgOrNull(cmd, "route");
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(
                RouteStore.CommittedRoutes, routeArg);

            if (!sel.Ok)
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand rejected reason={0} action={1} route={2} matches={3} kind={4}",
                    sel.RejectReason, action, routeArg ?? string.Empty,
                    sel.Matches, sel.MatchKind ?? "<none>"));
                SetExecResult("REJECTED", null, sel.RejectReason);
                return;
            }

            // ONE resolution site for every route-addressed action, so the three-tier
            // selector and its ambiguity refusals behave identically across all six.
            if (!TestCommandRouteCommand.IsStatusOperation(action))
            {
                switch (action)
                {
                    case TestCommandRouteCommand.ActionLink:
                        RouteCommandLink(cmd, sel);
                        return;
                    case TestCommandRouteCommand.ActionUnlink:
                        RouteCommandUnlink(sel);
                        return;
                    default: // ActionSetCadence (IsKnownAction already filtered the rest)
                        RouteCommandSetCadence(cmd, sel);
                        return;
                }
            }

            Route route = sel.Route;
            RouteStatus before = route.Status;
            double currentUT = TryGetRouteCommandUT();

            bool ok;
            switch (action)
            {
                case TestCommandRouteCommand.ActionSendOnce:
                    ok = RouteOrchestrator.TrySendOneCycleNow(route, currentUT);
                    break;
                case TestCommandRouteCommand.ActionPause:
                    ok = RouteOrchestrator.TryPause(route);
                    break;
                default: // ActionActivate (IsKnownAction already filtered the rest)
                    ok = RouteOrchestrator.TryActivate(route, currentUT);
                    break;
            }

            var payload = Payload(
                Kv("action", action),
                Kv("route", route.Id ?? string.Empty),
                Kv("name", route.Name ?? string.Empty),
                Kv("match", sel.MatchKind ?? string.Empty),
                Kv("applied", Bool(ok)),
                Kv("statusBefore", before.ToString()),
                Kv("status", route.Status.ToString()));

            if (!ok)
            {
                // The orchestrator declined because the route's STATUS does not admit
                // this action (TryActivate wants Paused; TrySendOneCycleNow refuses
                // InTransit / EndpointLost / SourceChanged / MissingSourceRecording;
                // TryPause refuses an already-Paused route). ERROR, not REJECTED: the
                // verb reached the production call and the PRODUCT declined, which is
                // the same line SimulateStockSwitchClick draws at
                // switch-refused-by-stock.
                string msg = TestCommandRouteCommand.ActionRefusedMsg(action);
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand refused action={0} route={1} statusBefore={2} status={3}",
                    action, route.Id ?? string.Empty, before, route.Status));
                SetExecResult("ERROR", payload, msg);
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "routecommand complete action={0} route={1} name='{2}' match={3} " +
                "statusBefore={4} status={5}",
                action, route.Id ?? string.Empty, route.Name ?? string.Empty,
                sel.MatchKind ?? "<none>", before, route.Status));
            SetExecResult("OK", payload, null);
        }

        // ----- action=link -----

        /// <summary>
        /// Drives <c>RouteStore.LinkRoutes</c>. The partner is either a named
        /// <c>partner=</c> resolved through the same three-tier selector, or - absent - the
        /// FIRST option <c>LogisticsLinkPresentation.BuildLinkCandidates</c> would have
        /// drawn in the picker, so a driven link can only reach a pair the UI offers.
        /// </summary>
        /// <summary>
        /// Dirties the Logistics window's throttled route-legibility cache, which every
        /// production handler that changes a route's NAME, LINK or CADENCE does as its last
        /// act.
        ///
        /// <para>Without it the window keeps drawing the pre-action Interval, Next and
        /// Destination cells for up to a refresh period - those sort keys live in that
        /// cache - so a census capture taken straight after the action photographs the OLD
        /// row under a label claiming the new one. No-op when the window has never been
        /// constructed, which is the ordinary case for a non-GUI lane.</para>
        /// </summary>
        private static void DirtyRouteLegibilityCache()
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null) return;
            LogisticsWindowUI window = ui.GetLogisticsUI();
            if (window == null) return;
            window.DirtyLegibilityCacheForTesting();
        }

        private void RouteCommandLink(ParsedCommand cmd, RouteSelection sel)
        {
            Route route = sel.Route;
            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            string partnerArg = ArgOrNull(cmd, TestCommandRouteCommand.PartnerArg);

            Route partner;
            string partnerSource;
            if (string.IsNullOrEmpty(partnerArg))
            {
                // The picker's own list. An empty one is a TYPED reject rather than a bare
                // false out of the store: every other route is already linked, or there is
                // no other route, and those send a lane to different fixes.
                List<LogisticsLinkPresentation.LinkCandidate> candidates =
                    LogisticsLinkPresentation.BuildLinkCandidates(routes, route.Id);
                if (candidates.Count == 0)
                {
                    ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                        "routecommand rejected reason={0} action=link route={1} routes={2}",
                        TestCommandRouteCommand.LinkNoCandidateReason,
                        route.Id ?? string.Empty, routes != null ? routes.Count : 0));
                    SetExecResult("REJECTED", null,
                        TestCommandRouteCommand.LinkNoCandidateReason);
                    return;
                }
                RouteSelection pick = TestCommandRouteCommand.ResolveRoute(
                    routes, candidates[0].Id);
                partner = pick.Route;
                partnerSource = "candidate-first";
            }
            else
            {
                RouteSelection pick = TestCommandRouteCommand.ResolveRoute(routes, partnerArg);
                if (!pick.Ok)
                {
                    string token = TestCommandRouteCommand.MapPartnerReject(pick.RejectReason);
                    ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                        "routecommand rejected reason={0} action=link route={1} partner={2} "
                        + "matches={3} kind={4}",
                        token, route.Id ?? string.Empty, partnerArg,
                        pick.Matches, pick.MatchKind ?? "<none>"));
                    SetExecResult("REJECTED", null, token);
                    return;
                }
                partner = pick.Route;
                partnerSource = pick.MatchKind ?? string.Empty;
            }

            RouteLinkRefusal refusal = TestCommandRouteCommand.ClassifyLink(
                route.Id, route.LinkedRouteId,
                partner != null ? partner.Id : null,
                partner != null ? partner.LinkedRouteId : null);
            if (refusal != RouteLinkRefusal.None)
            {
                string token = TestCommandRouteCommand.LinkRefusalToken(refusal);
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand rejected reason={0} action=link route={1} partner={2} "
                    + "routeLinked={3} partnerLinked={4}",
                    token, route.Id ?? string.Empty,
                    partner != null ? (partner.Id ?? string.Empty) : string.Empty,
                    route.LinkedRouteId ?? "<none>",
                    partner != null ? (partner.LinkedRouteId ?? "<none>") : "<none>"));
                SetExecResult("REJECTED", null, token);
                return;
            }

            string linkedBefore = route.LinkedRouteId;
            bool ok = RouteStore.LinkRoutes(route.Id, partner.Id);
            if (ok) DirtyRouteLegibilityCache();

            var payload = Payload(
                Kv("action", TestCommandRouteCommand.ActionLink),
                Kv("route", route.Id ?? string.Empty),
                Kv("name", route.Name ?? string.Empty),
                Kv("match", sel.MatchKind ?? string.Empty),
                Kv("applied", Bool(ok)),
                Kv("partner", partner.Id ?? string.Empty),
                Kv("partnerName", partner.Name ?? string.Empty),
                Kv("partnerMatch", partnerSource),
                Kv("linkedBefore", linkedBefore ?? "-"),
                Kv("linked", route.LinkedRouteId ?? "-"),
                Kv("partnerLinked", partner.LinkedRouteId ?? "-"));

            if (!ok)
            {
                // Every refusal the store distinguishes was classified above, so a false
                // here is one the classification did not predict. ERROR with the generic
                // compound, the RouteCommandOperate line.
                string msg = TestCommandRouteCommand.ActionRefusedMsg(
                    TestCommandRouteCommand.ActionLink);
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand refused action=link route={0} partner={1}",
                    route.Id ?? string.Empty, partner.Id ?? string.Empty));
                SetExecResult("ERROR", payload, msg);
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "routecommand complete action=link route={0} partner={1} partnerMatch={2} "
                + "linkedBefore={3} linked={4} partnerLinked={5}",
                route.Id ?? string.Empty, partner.Id ?? string.Empty, partnerSource,
                linkedBefore ?? "<none>", route.LinkedRouteId ?? "<none>",
                partner.LinkedRouteId ?? "<none>"));
            SetExecResult("OK", payload, null);
        }

        // ----- action=unlink -----

        /// <summary>
        /// Drives <c>RouteStore.UnlinkRoute</c>. An ALREADY-UNLINKED route is refused
        /// BEFORE the call with its own token: the store answers that case with a bare
        /// false, which must not read as a failed unlink.
        /// </summary>
        private void RouteCommandUnlink(RouteSelection sel)
        {
            Route route = sel.Route;
            string linkedBefore = route.LinkedRouteId;

            if (string.IsNullOrEmpty(linkedBefore))
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand rejected reason={0} action=unlink route={1}",
                    TestCommandRouteCommand.RouteNotLinkedReason, route.Id ?? string.Empty));
                SetExecResult("REJECTED", null,
                    TestCommandRouteCommand.RouteNotLinkedReason);
                return;
            }

            bool ok = RouteStore.UnlinkRoute(route.Id);

            if (ok) DirtyRouteLegibilityCache();

            var payload = Payload(
                Kv("action", TestCommandRouteCommand.ActionUnlink),
                Kv("route", route.Id ?? string.Empty),
                Kv("name", route.Name ?? string.Empty),
                Kv("match", sel.MatchKind ?? string.Empty),
                Kv("applied", Bool(ok)),
                Kv("linkedBefore", linkedBefore),
                Kv("linked", route.LinkedRouteId ?? "-"));

            if (!ok)
            {
                string msg = TestCommandRouteCommand.ActionRefusedMsg(
                    TestCommandRouteCommand.ActionUnlink);
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand refused action=unlink route={0} linkedBefore={1}",
                    route.Id ?? string.Empty, linkedBefore));
                SetExecResult("ERROR", payload, msg);
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "routecommand complete action=unlink route={0} linkedBefore={1} linked={2}",
                route.Id ?? string.Empty, linkedBefore, route.LinkedRouteId ?? "<none>"));
            SetExecResult("OK", payload, null);
        }

        // ----- action=set-cadence -----

        /// <summary>
        /// Drives <c>RouteCadence.ApplyMultiplier</c>, which on a real change writes BOTH
        /// <c>CadenceMultiplier</c> and a recomputed <c>DispatchInterval</c> plus a
        /// dispatch-clock rebase - so the payload reports the before and after of both
        /// fields rather than the multiplier alone.
        ///
        /// <para>An UNCHANGED N is refused before the call: the production method no-ops
        /// false on it, and a lane must be able to tell "already at N" from "the edit was
        /// declined".</para>
        /// </summary>
        private void RouteCommandSetCadence(ParsedCommand cmd, RouteSelection sel)
        {
            Route route = sel.Route;
            string raw = ArgOrNull(cmd, TestCommandRouteCommand.CadenceArg);

            if (!TestCommandRouteCommand.TryParseCadenceArg(
                    raw, out int multiplier, out string parseReject))
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand rejected reason={0} action=set-cadence route={1} cadence={2}",
                    parseReject, route.Id ?? string.Empty, raw ?? string.Empty));
                SetExecResult("REJECTED", null, parseReject);
                return;
            }

            int cadenceBefore = route.CadenceMultiplier;
            double intervalBefore = route.DispatchInterval;

            if (multiplier == cadenceBefore)
            {
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand rejected reason={0} action=set-cadence route={1} cadence={2}",
                    TestCommandRouteCommand.CadenceUnchangedReason,
                    route.Id ?? string.Empty, multiplier));
                SetExecResult("REJECTED", null,
                    TestCommandRouteCommand.CadenceUnchangedReason);
                return;
            }

            bool ok = RouteCadence.ApplyMultiplier(route, multiplier);
            if (ok) DirtyRouteLegibilityCache();

            var payload = Payload(
                Kv("action", TestCommandRouteCommand.ActionSetCadence),
                Kv("route", route.Id ?? string.Empty),
                Kv("name", route.Name ?? string.Empty),
                Kv("match", sel.MatchKind ?? string.Empty),
                Kv("applied", Bool(ok)),
                Kv("cadenceBefore", Int(cadenceBefore)),
                Kv("cadence", Int(route.CadenceMultiplier)),
                Kv("intervalBefore",
                    intervalBefore.ToString("R", CultureInfo.InvariantCulture)),
                Kv("intervalSeconds",
                    route.DispatchInterval.ToString("R", CultureInfo.InvariantCulture)),
                Kv("transitSeconds",
                    route.TransitDuration.ToString("R", CultureInfo.InvariantCulture)));

            if (!ok)
            {
                string msg = TestCommandRouteCommand.ActionRefusedMsg(
                    TestCommandRouteCommand.ActionSetCadence);
                ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                    "routecommand refused action=set-cadence route={0} cadenceBefore={1} "
                    + "requested={2}",
                    route.Id ?? string.Empty, cadenceBefore, multiplier));
                SetExecResult("ERROR", payload, msg);
                return;
            }

            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "routecommand complete action=set-cadence route={0} cadenceBefore={1} "
                + "cadence={2} intervalBefore={3} intervalSeconds={4}",
                route.Id ?? string.Empty, cadenceBefore, route.CadenceMultiplier,
                intervalBefore.ToString("R", CultureInfo.InvariantCulture),
                route.DispatchInterval.ToString("R", CultureInfo.InvariantCulture)));
            SetExecResult("OK", payload, null);
        }

        /// <summary>Live UT, or 0 when no game clock exists yet. The orchestrator and
        /// the manual-loop clear both take a UT by parameter, so this is the one place
        /// the verb reads the clock. Guarded the way the window's own
        /// <c>LogisticsWindowUI.TryGetCurrentUT</c> is: the RequiresGameLoaded
        /// precondition makes a throw unlikely, but a clock read must never be the
        /// reason a route command dies.</summary>
        private static double TryGetRouteCommandUT()
        {
            try
            {
                return HighLogic.CurrentGame != null ? Planetarium.GetUniversalTime() : 0.0;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"routecommand live UT resolution threw {ex.GetType().Name}; using 0");
                return 0.0;
            }
        }
    }
}
