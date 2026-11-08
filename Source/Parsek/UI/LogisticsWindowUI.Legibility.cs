using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The window's derived data: candidates and near misses, the candidate display and legibility caches, capacity and delivery summaries, Go To and history resolution, sorted routes and the destination cell.
    internal partial class LogisticsWindowUI
    {
        // ------------------------------------------------------------------
        // Candidate cache (throttled)
        // ------------------------------------------------------------------

        private List<RouteCandidate> GetCandidates()
        {
            float now = Time.realtimeSinceStartup;
            // Refresh only on a Layout pass (as the legibility cache does): the candidate
            // rows, their detail and the dismissed rows draw from this list, so a refresh
            // between a frame's Layout and its Repaint would change the control count.
            bool layoutPass = Event.current == null || Event.current.type == EventType.Layout;
            if (layoutPass && (lastCandidateComputeRealtime < 0f
                || now - lastCandidateComputeRealtime >= CandidateRecomputeIntervalSeconds))
            {
                cachedCandidates = RouteCandidateFinder.DeriveCandidates();
                lastCandidateComputeRealtime = now;
                RebuildCandidateDisplayCache(cachedCandidates);

                // M6: rebuild the Dismissed-subsection display rows on the same
                // refresh (tree names resolved here, never on the draw path).
                // Sorted by label then id so the row order is stable across
                // HashSet iteration order changes.
                cachedDismissedRows.Clear();
                foreach (string dismissedId in RouteStore.DismissedCandidateTreeIds)
                    cachedDismissedRows.Add((dismissedId, ResolveTreeDisplayName(dismissedId)));
                cachedDismissedRows.Sort((a, b) =>
                {
                    int byLabel = string.CompareOrdinal(a.label, b.label);
                    return byLabel != 0 ? byLabel : string.CompareOrdinal(a.treeId, b.treeId);
                });
                // A repeated mission name reads "Name", "Name [2]", in the sorted order.
                var dismissedNames = new List<string>(cachedDismissedRows.Count);
                for (int i = 0; i < cachedDismissedRows.Count; i++)
                    dismissedNames.Add(cachedDismissedRows[i].label);
                string[] numbered = LogisticsNearMissPresentation.NumberRepeatedNames(dismissedNames);
                for (int i = 0; i < cachedDismissedRows.Count; i++)
                    cachedDismissedRows[i] = (cachedDismissedRows[i].treeId, numbered[i]);
            }
            return cachedCandidates ?? new List<RouteCandidate>();
        }

        // M3: the throttled "recently committed trees not yet eligible" near-miss
        // list. Same ~1 Hz timer as GetCandidates (both derive from the same
        // committed trees through RouteAnalysisEngine), so the subsection never
        // scans live on the IMGUI draw path. Gate-safe: DeriveNearMisses reads only
        // RecordingTree.Recordings[].MergeState + RouteAnalysisEngine, no raw
        // committed-recording / ledger read.
        private List<RouteNearMiss> GetNearMisses()
        {
            float now = Time.realtimeSinceStartup;
            // Layout passes only, like GetCandidates: the grouped rows draw from this.
            bool layoutPass = Event.current == null || Event.current.type == EventType.Layout;
            if (layoutPass && (lastNearMissComputeRealtime < 0f
                || now - lastNearMissComputeRealtime >= CandidateRecomputeIntervalSeconds))
            {
                cachedNearMisses = RouteCandidateFinder.DeriveNearMisses();
                lastNearMissComputeRealtime = now;
                cachedNearMissGroups = BuildNearMissGroups(cachedNearMisses);
            }
            return cachedNearMisses ?? new List<RouteNearMiss>();
        }

        // The near-miss list as one group per reason (pure LogisticsNearMissPresentation),
        // with one batch summary line per rebuild.
        private static List<NearMissGroup> BuildNearMissGroups(List<RouteNearMiss> nearMisses)
        {
            var inputs = new List<NearMissInput>(nearMisses?.Count ?? 0);
            for (int i = 0; nearMisses != null && i < nearMisses.Count; i++)
            {
                RouteNearMiss nm = nearMisses[i];
                if (nm == null) continue;
                inputs.Add(new NearMissInput
                {
                    TreeId = nm.Tree?.Id,
                    Name = NearMissTreeLabel(nm.Tree),
                    Status = nm.Status,
                    NotSealed = nm.NotSealed,
                    ReflyableCount = nm.ReflyableCount,
                    RejectDetail = nm.RejectDetail,
                });
            }
            List<NearMissGroup> groups = LogisticsNearMissPresentation.Group(inputs,
                TooltipEchoBox.BudgetChars(DefaultWindowWidth, TooltipEchoBox.SingleLine));
            ParsekLog.Verbose("UI",
                $"Logistics near-miss groups rebuilt missions={inputs.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"groups={groups.Count.ToString(CultureInfo.InvariantCulture)}");
            return groups;
        }

        // The candidate rows' cached display text: the from/to line (origin, then the
        // dock endpoint's live vessel name or its place) with the full origin and
        // destination in its hover, and the Would deliver cell (pickups named).
        private void RebuildCandidateDisplayCache(List<RouteCandidate> candidates)
        {
            candidateDisplayCache.Clear();
            int resolved = 0;
            int missionNamed = 0;
            for (int i = 0; candidates != null && i < candidates.Count; i++)
            {
                RouteCandidate cand = candidates[i];
                if (cand?.Analysis == null) continue;
                string treeId = cand.Tree?.Id ?? "<no-tree>";
                RouteEndpoint? ep = cand.Analysis.ConnectionWindow?.EndpointAtDock;
                string destName = null;
                if (ep.HasValue && RouteEndpointResolver.TryResolveEndpoint(ep.Value, out Vessel v, out _) && v != null)
                {
                    destName = v.vesselName;
                    resolved++;
                }
                string destShort = destName ?? FormatEndpointPlace(ep);
                string destLong = (destName != null ? destName + " " : string.Empty)
                    + (ep.HasValue ? FormatEndpointShort(ep.Value) : "-");
                string defaultName = RouteCreationService.ResolveDefaultName(cand, out bool fromMission, out _);
                if (fromMission) missionNamed++;
                candidateDisplayCache[treeId] = new CandidateDisplay
                {
                    Name = defaultName,
                    FromTo = LogisticsRoutePresentation.FormatFromTo(
                        FormatCandidateOriginShort(cand.Analysis, cand.Tree), destShort),
                    FromToTooltip = LogisticsRoutePresentation.CapToStrip(
                        "From " + FormatCandidateOrigin(cand.Analysis, cand.Tree) + " to " + destLong + ".",
                        TooltipEchoBox.BudgetChars(DefaultWindowWidth, TooltipEchoBox.SingleLine)),
                    DestinationName = destName,
                    Cargo = LogisticsDeliveryPresentation.FormatCandidateCargo(
                        cand.Analysis.ResourceDeliveryManifest, cand.Analysis.InventoryDeliveryManifest,
                        cand.Analysis.ResourceLoadManifest, cand.Analysis.InventoryLoadManifest,
                        destShort),
                };
            }
            ParsekLog.Verbose("UI",
                $"Logistics candidate display cache rebuilt candidates={candidateDisplayCache.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"destinationsResolved={resolved.ToString(CultureInfo.InvariantCulture)} " +
                $"missionNamed={missionNamed.ToString(CultureInfo.InvariantCulture)}");
        }

        // ------------------------------------------------------------------
        // Per-route legibility cache (throttled): H1 next-delivery countdown,
        // H2 realized / cumulative delivery, the merged Status cell.
        // ------------------------------------------------------------------

        /// <summary>
        /// Recomputes the whole per-route legibility cache on the realtime timer
        /// (mirrors <see cref="GetCandidates"/>). For each committed route it builds:
        /// the H1 next-dock-crossing countdown (throttled <see cref="RouteOrchestrator"/>
        /// accessor + the wait-state retry fallback branch), and the H2/H3 realized /
        /// cumulative delivery summary from a SINGLE event-log scan of the route's
        /// <c>RouteCargoDelivered</c> rows (H3 reuses H2's scan). Emits one batch-summary
        /// Verbose line after the pass (route count + how many had deliveries), never
        /// per route. Called once per frame at the top of <see cref="DrawWindow"/>; the
        /// timer / dirty-flag gate keeps the LoopUnit build + event-log scan to ~1 Hz.
        /// </summary>
        private void RefreshLegibilityCacheIfDue(IReadOnlyList<Route> routes, double currentUT)
        {
            // Refresh only on a Layout pass. Several detail lines and the Every / Next cells
            // draw or not from cached fields, so a refresh between a frame's Layout and its
            // Repaint would change the control count inside one frame. A dirtied cache
            // (stamp -1) keeps its old entries until the next Layout, so the passes before
            // it still agree with each other.
            if (Event.current != null && Event.current.type != EventType.Layout)
                return;
            float now = Time.realtimeSinceStartup;
            if (lastLegibilityComputeRealtime >= 0f
                && now - lastLegibilityComputeRealtime < LegibilityRecomputeIntervalSeconds)
                return;
            lastLegibilityComputeRealtime = now;

            legibilityCache.Clear();
            // M6: prune the Send-Once provenance set to routes that still exist and are
            // still armed, so a disarmed/deleted route never leaks a stale entry. Only
            // allocate when the set is non-empty (the common case is empty).
            bool pruneArmed = sendOnceArmedRouteIds.Count > 0;
            HashSet<string> stillArmed = pruneArmed ? new HashSet<string>() : null;
            int routeCount = routes?.Count ?? 0;
            int withCountdown = 0;
            int withDeliveries = 0;
            int withHold = 0;
            int withGoTo = 0;
            for (int i = 0; i < routeCount; i++)
            {
                Route route = routes[i];
                if (route == null || string.IsNullOrEmpty(route.Id)) continue;
                if (pruneArmed && route.PauseAfterCurrentCycle)
                    stillArmed.Add(route.Id);

                RouteLegibility leg = ComputeRouteLegibility(route, currentUT);
                legibilityCache[route.Id] = leg;
                if (leg.CountdownBranch != LogisticsCountdownPresentation.CountdownBranch.None)
                    withCountdown++;
                if (leg.HasDeliveries)
                    withDeliveries++;
                // M6 hold reasons: batch counter, one summary line below.
                if (leg.HoldText != null)
                    withHold++;
                // Go to: how many routes still resolve their source mission.
                if (leg.GoToRecordingId != null)
                    withGoTo++;
            }
            if (pruneArmed)
                sendOnceArmedRouteIds.RemoveWhere(id => !stillArmed.Contains(id));

            ParsekLog.Verbose("UI",
                $"Logistics legibility cache refreshed routes={routeCount.ToString(CultureInfo.InvariantCulture)} " +
                $"withCountdown={withCountdown.ToString(CultureInfo.InvariantCulture)} " +
                $"withDeliveries={withDeliveries.ToString(CultureInfo.InvariantCulture)} " +
                $"withHold={withHold.ToString(CultureInfo.InvariantCulture)} " +
                $"withGoTo={withGoTo.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Builds one route's legibility values: the countdown (next dock crossing, next
        /// launch window, or the held route's recheck) and its Next cell / hover / detail
        /// line, the read-only Every cell, one event-log scan for realized deliveries (the
        /// dated Last delivered line and the Advanced Recent runs), the destination and
        /// from/to names, the run cost, the dated hold and partial-delivery lines, and the
        /// merged Status cell with its sentence. Every date goes through
        /// <see cref="ReservationExplanation.DefaultDateFormatter"/>.
        /// </summary>
        private RouteLegibility ComputeRouteLegibility(Route route, double currentUT)
        {
            var leg = new RouteLegibility();
            System.Func<double, string> formatDate = ReservationExplanation.DefaultDateFormatter;
            bool sendingOnce = IsSendingOnce(route);
            bool pausingAfterRun = IsPausingAfterRun(route);
            bool inPausedTable = LogisticsRoutePresentation.BelongsInPausedTable(route.Status);

            // Countdown: next dock crossing (read-only; the LoopUnit build stays behind the
            // allowlisted RouteOrchestrator accessor), or the basis-aware next launch window
            // for the windowed bases the flat helper refuses. Both accessors share the
            // signature-cached LoopUnit resolve, so this stays a ~1 Hz cost.
            bool hasCrossing = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, currentUT, out double secondsToCrossing);
            bool hasWindow = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                route, currentUT, out double secondsToWindow,
                out RouteWindowBasis basis, out string targetBody);
            leg.Basis = basis;
            leg.BasisLabel = RouteWindowBasisPresentation.BasisLabel(basis, targetBody);
            LogisticsCountdownPresentation.CountdownDecision countdown =
                LogisticsCountdownPresentation.ResolveDetailCountdown(
                    route.Status, route.NextEligibilityCheckUT,
                    hasCrossing, secondsToCrossing,
                    hasWindow, secondsToWindow, currentUT);
            leg.CountdownBranch = countdown.Branch;
            leg.CountdownSeconds = countdown.Seconds;
            // A run is scheduled unless the route is Paused. A Send-armed route sits in
            // Active and keeps its countdown: to its window before launch, then to the
            // arrival of the run in flight, like any scheduled run.
            leg.RunScheduled = !inPausedTable || sendingOnce;
            double nextUT = countdown.Branch != LogisticsCountdownPresentation.CountdownBranch.None
                ? currentUT + countdown.Seconds
                : double.NaN;

            // The read-only Every cell (Basic, and a Send-armed row in either mode).
            bool windowed = RouteWindowBasisPresentation.IsWindowedBasis(basis);
            leg.EveryText = LogisticsRoutePresentation.FormatEveryReadOnly(
                windowed, route.CadenceMultiplier, FormatDuration(route.DispatchInterval));
            leg.EveryTooltip = LogisticsRoutePresentation.EveryTooltip(windowed, leg.BasisLabel);

            // Realized deliveries: one event-log scan -> the latest run, its shortfall flag and
            // the cumulative total. (The route's runs themselves are the Route History
            // window's, built on open from the same log.)
            LogisticsDeliveryPresentation.RouteDeliverySummary summary =
                CollectRouteDeliverySummary(route.Id, null);
            leg.HasDeliveries = summary.HasAny;
            leg.LastCycleText = summary.HasAny
                ? LogisticsDeliveryPresentation.FormatRealizedDelivery(summary.LastRequested, summary.LastActual)
                : null;
            leg.LastCycleShortfall = summary.HasAny
                && LogisticsDeliveryPresentation.HasShortfall(summary.LastRequested, summary.LastActual);
            leg.CumulativeText = LogisticsDeliveryPresentation.FormatCumulativeTotal(summary.CumulativeTotal);
            leg.LastDeliveredLine = LogisticsRoutePresentation.FormatLastDeliveredLine(
                summary.HasAny, summary.LastUt, leg.LastCycleText, leg.CumulativeText,
                route.CompletedCycles, formatDate);

            // Go to: a recording of the source mission still in the effective set.
            leg.GoToRecordingId = ResolveGoToRecordingId(route);

            // Destination name (the Delivers line, the from/to line and the sort key). It
            // touches FlightGlobals and, on an unresolved surface endpoint, scans all
            // vessels; the resolved Vessel feeds the capacity probe below so there is no
            // second O(vessels) scan.
            ResolveDestinationCell(route, out string destText, out string destTooltip, out Vessel destVessel,
                out Vessel[] stopVessels, out string[] stopTexts);
            leg.DestinationText = destText;
            leg.DestinationTooltip = destTooltip;
            // The row and the detail name places, never raw coordinates: an unresolved
            // single-stop destination or depot origin reads "Kerbin (surface)", and the
            // coordinates ride the from/to hover.
            string destShort = destVessel == null && route.Stops != null && route.Stops.Count == 1
                && route.Stops[0] != null
                ? FormatEndpointPlace(route.Stops[0].Endpoint)
                : destText;
            leg.OriginShort = LogisticsRoutePresentation.FormatOriginShort(
                route.IsKscOrigin, route.IsHarvestOrigin,
                route.IsKscOrigin || route.IsHarvestOrigin
                    ? null
                    : (TryResolveLiveVesselName(route.Origin.VesselPersistentId) ?? FormatEndpointPlace(route.Origin)));
            leg.FromToText = LogisticsRoutePresentation.FormatFromTo(leg.OriginShort, destShort);
            leg.FromToTooltip = "From " + FormatOrigin(route) + " to " + destText
                + (string.IsNullOrEmpty(destTooltip) ? string.Empty : " (" + destTooltip + ")") + ".";
            string harvestCapNote = LogisticsRoutePresentation.FormatHarvestCapNote(route.HarvestCaps);
            if (harvestCapNote.Length > 0)
                leg.FromToTooltip += " " + harvestCapNote;
            // What each run carries: the delivery, and on a route that loads cargo (a
            // relay's pickup stop, or a pure pickup run) what it picks up and where.
            string[] stopNames = ResolveStopNames(route, destShort, stopVessels, stopTexts);
            leg.DeliversCellText = LogisticsDeliveryPresentation.FormatRouteCargoCell(route.Stops, stopNames);
            leg.DeliversLine = LogisticsDeliveryPresentation.FormatRouteCargoLine(route.Stops, stopNames, destShort);

            if (route.Status == RouteStatus.DestinationFull)
                leg.CapacityContext = ResolveCapacityContext(route, destVessel, destText, stopVessels, stopTexts);

            // Last-partial-delivery report, dated. Not gated by ShouldDisplayHold: it
            // reports a past physical loss, which no status makes misleading; the
            // orchestrator clears it on the next full delivery.
            leg.PartialText = LogisticsHoldPresentation.FormatPartialDeliveryLine(
                route.LastPartialDeliverySummary, route.LastPartialDeliveryUT, formatDate);

            // The persisted last hold in player language. ShouldDisplayHold gates DISPLAY
            // only (persistence is unconditional): MissingSourceRecording / SourceChanged
            // rows suppress an older hold that would mislead. The detail line and the
            // Status hover carry the dated long clause; the cell carries the compact one.
            if (LogisticsHoldPresentation.ShouldDisplayHold(route.Status, route.LastHoldKind))
            {
                // Stored parts read by their title, through the resolver the Route
                // History's held rows use.
                leg.HoldShort = LogisticsHoldPresentation.DescribeHold(
                    route.LastHoldKind, route.LastHoldDetail, route.LastHoldShortfall,
                    StructureListWindowUI.ResolvePartTitle);
                leg.HoldText = LogisticsHoldPresentation.FormatHoldDetailLine(
                    leg.HoldShort, route.LastHoldUT, formatDate);
                leg.HoldCellText = LogisticsHoldPresentation.StatusCellText(
                    route.LastHoldKind, route.LastHoldDetail, route.LastHoldShortfall,
                    StructureListWindowUI.ResolvePartTitle);
            }

            // The merged Status cell + its sentence, and the Next cell (warned when the
            // last run was held: the countdown may well be held again).
            bool originLost = route.Status == RouteStatus.EndpointLost
                && route.LastHoldDetail != null
                && route.LastHoldDetail.StartsWith("origin-", System.StringComparison.Ordinal);
            leg.Status = LogisticsRoutePresentation.ClassifyStatus(
                route.Status, sendingOnce, pausingAfterRun, leg.HoldCellText,
                route.CompletedCycles, originLost);
            leg.StatusTooltip = LogisticsRoutePresentation.StatusTooltip(
                leg.Status, route.Status, leg.HoldText, route.CompletedCycles,
                route.CreatedUT, nextUT, originLost, formatDate);
            // A Send-armed run in flight: the greyed Delivering... and the Status hover
            // date the launch (the cycle's start) and the arrival (the countdown target).
            if (sendingOnce && route.Status == RouteStatus.InTransit)
            {
                leg.DeliveringTooltip = LogisticsRoutePresentation.FormatDeliveringTooltip(
                    route.CurrentCycleStartUT ?? double.NaN, nextUT, formatDate);
                leg.StatusTooltip = "Sending one run. " + leg.DeliveringTooltip;
            }
            leg.UpdatePartsTooltip = LogisticsRoutePresentation.FormatUpdatePartsTooltip(
                RouteEndpointPartAdoption.LastAdoptedUT(route), formatDate);
            bool warned = leg.Status.Word == LogisticsRoutePresentation.StatusWord.Held;
            leg.NextCellText = LogisticsRoutePresentation.FormatNextCell(
                countdown.Branch, countdown.Seconds, leg.RunScheduled, warned);
            leg.NextTooltip = LogisticsRoutePresentation.FormatNextTooltip(
                countdown.Branch, nextUT, leg.RunScheduled, warned, formatDate);
            leg.NextLine = LogisticsRoutePresentation.FormatNextLine(
                countdown.Branch, nextUT, leg.RunScheduled,
                route.TransitDuration > 0.0 ? ParsekTimeFormat.FormatDuration(route.TransitDuration) : null,
                formatDate);

            // The classification is logged once per refresh per route, and at once when
            // its word changes (the word is in the rate key).
            ParsekLog.VerboseRateLimited("UI", "route-status-" + route.Id + "-" + leg.Status.Word,
                $"Logistics route status route={ShortId(route.Id)} word={leg.Status.Word} " +
                $"table={(inPausedTable ? "paused" : "active")} sendingOnce={sendingOnce} " +
                $"pausing={pausingAfterRun} scheduled={leg.RunScheduled}",
                30.0);

            return leg;
        }

        /// <summary>
        /// Builds the M4 DestinationFull free-capacity context line for the legibility
        /// cache. Reads the route's first stop's <see cref="RouteStop.DeliveryManifest"/>
        /// (the requested amounts) and, for each resource, the LIVE free capacity on the
        /// resolved destination <paramref name="destVessel"/> via a
        /// <see cref="LiveDeliveryCapacityProbe"/> constructed with the same
        /// <see cref="RouteOrchestrator.EndpointStoreIsLiveParts(Vessel)"/> gate the orchestrator uses (so the reported
        /// number matches what a real delivery would fill). When the vessel could not be
        /// resolved or the manifest is empty, the entry list is empty and the pure
        /// <see cref="LogisticsDeliveryPresentation.FormatCapacityContext"/> renders a
        /// "(capacity unknown)" line. This is the one non-pure piece of M4 (it touches a
        /// live Vessel), so it stays in the window file and runs only on the ~1 Hz
        /// refresh; the draw path reads the cached string. Logs the probe decision
        /// (rate-limited per route).
        /// </summary>
        private string ResolveCapacityContext(Route route, Vessel destVessel, string destName,
            Vessel[] stopVessels, string[] stopTexts)
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>();
            Dictionary<string, double> manifest;
            int capacityStop = 0;
            int fullStop = -1;
            if (route?.Stops != null && route.Stops.Count > 1 && stopVessels != null && stopTexts != null)
            {
                // GUI-P20: name and probe the stop the capacity gate refuses (its
                // requested amounts include earlier stops delivering to the same
                // vessel, as the gate counts them); when every stop fits now, fall
                // back to the stop the Destination cell names.
                fullStop = FindFullStopIndex(route, stopVessels);
                capacityStop = fullStop >= 0
                    ? fullStop
                    : LogisticsDeliveryPresentation.ResolveDestinationStopIndex(route.Stops);
                if (capacityStop >= 0)
                {
                    destVessel = stopVessels[capacityStop];
                    destName = stopTexts[capacityStop];
                    Vessel target = destVessel;
                    manifest = LogisticsDeliveryPresentation.CombineRequestedForFullStop(
                        route.Stops, capacityStop,
                        i => target != null && stopVessels[i] != null
                            && stopVessels[i].persistentId == target.persistentId);
                }
                else
                {
                    destVessel = null;
                    manifest = null;
                }
            }
            else
            {
                manifest = route?.Stops != null && route.Stops.Count > 0 && route.Stops[0] != null
                    ? route.Stops[0].DeliveryManifest
                    : null;
            }

            if (destVessel != null && manifest != null && manifest.Count > 0)
            {
                bool destinationIsLoaded = RouteOrchestrator.EndpointStoreIsLiveParts(destVessel);
                // The same own-parts scope the gate and the delivery use, so the line
                // does not count a docked visitor's tanks.
                EndpointPartScope scope = route?.Stops != null
                    && capacityStop >= 0 && capacityStop < route.Stops.Count
                    && route.Stops[capacityStop] != null
                    ? RouteEndpointPartScope.ForEndpoint(route, route.Stops[capacityStop].Endpoint,
                        destVessel, destinationIsLoaded, "capacity-context")
                    : null;
                var probe = new LiveDeliveryCapacityProbe(destVessel, destinationIsLoaded, scope);
                foreach (KeyValuePair<string, double> kv in manifest)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    double free = probe.ProbeResourceFreeCapacity(kv.Key);
                    entries.Add(new LogisticsDeliveryPresentation.CapacityEntry(kv.Key, kv.Value, free));
                }
            }

            ParsekLog.VerboseRateLimited("UI", "dest-capacity-" + route.Id,
                $"Logistics: capacity context route={ShortId(route.Id)} dest='{destName}' " +
                $"resolved={(destVessel != null ? "true" : "false")} resources={entries.Count.ToString(CultureInfo.InvariantCulture)}" +
                (route.Stops != null && route.Stops.Count > 1
                    ? $" stop={(capacityStop + 1).ToString(CultureInfo.InvariantCulture)}/{route.Stops.Count.ToString(CultureInfo.InvariantCulture)} gateFull={(fullStop >= 0 ? "true" : "false")}"
                    : string.Empty),
                5.0);

            return LogisticsDeliveryPresentation.FormatCapacityContext(destName, entries);
        }

        /// <summary>
        /// Scans the route event log (<see cref="RouteEventLog.Events"/>) for this route's
        /// <see cref="RouteEventType.RouteCargoDelivered"/> events and reduces them to a
        /// <see cref="LogisticsDeliveryPresentation.RouteDeliverySummary"/> (latest
        /// cycle + cumulative total). Mirrors
        /// <c>RouteOrchestrator.IsDeliveryAlreadyLogged</c>'s scan idiom. Matches events
        /// by RouteId only (all cycles), NOT by cycle id. It reads the static log, so it
        /// stays in the window file and feeds the pure summary/format helpers; called only
        /// on the ~1 Hz cache refresh.
        /// M6 per-cycle flow: the SAME single walk also fills
        /// <paramref name="flowRows"/> (debit / pickup / delivery rows bucketed
        /// later by cycle) via <see cref="LogisticsFlowPresentation.CollectRows"/>,
        /// so the flow display adds NO second walk; pass null to skip.
        /// </summary>
        private static LogisticsDeliveryPresentation.RouteDeliverySummary CollectRouteDeliverySummary(
            string routeId, List<LogisticsFlowPresentation.FlowRow> flowRows)
        {
            var rows = new List<LogisticsDeliveryPresentation.DeliveryRow>();
            if (string.IsNullOrEmpty(routeId))
                return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);

            LogisticsFlowPresentation.CollectRows(RouteEventLog.Events, routeId, rows, flowRows);
            return LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);
        }

        /// <summary>
        /// The recording Go to hands the Missions cross-link: one of the route's source
        /// tree's recordings that is still in the effective set (ERS), preferring the
        /// route's own copied flights in order, so the cross-link resolves it to the tree's
        /// ORIGINAL mission. Null when the source tree has no original mission any more or
        /// none of its recordings is effective (the button then greys). Runs on the ~1 Hz
        /// refresh only.
        /// </summary>
        private static string ResolveGoToRecordingId(Route route)
        {
            string treeId = ResolveRouteSourceTreeId(route);
            if (string.IsNullOrEmpty(treeId) || MissionStore.FindOriginalMission(treeId) == null)
                return null;
            IReadOnlyList<Recording> effective;
            try
            {
                effective = EffectiveState.ComputeERS();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("UI",
                    $"Logistics Go to: ComputeERS threw {ex.GetType().Name}: {ex.Message}; greyed");
                return null;
            }
            if (effective == null)
                return null;
            string firstOfTree = null;
            for (int i = 0; i < effective.Count; i++)
            {
                Recording rec = effective[i];
                if (rec == null || !string.Equals(rec.TreeId, treeId, System.StringComparison.Ordinal))
                    continue;
                if (route.RecordingIds != null && route.RecordingIds.Contains(rec.RecordingId))
                    return rec.RecordingId;
                if (firstOfTree == null)
                    firstOfTree = rec.RecordingId;
            }
            return firstOfTree;
        }

        // ------------------------------------------------------------------
        // Route History (the route's Log button opens it)
        // ------------------------------------------------------------------

        /// <summary>
        /// The Route History rows for one route: its events from the route event log
        /// (<see cref="RouteEventLog.Events"/>, so a run a load back in time retired is
        /// absent) through the pure <see cref="RouteHistoryBuilder"/>, with the origin and
        /// each stop named by its live vessel (the place, never raw coordinates, when the
        /// vessel no longer resolves). Called on open and when
        /// <see cref="RouteHistorySignature"/> moves, never per frame.
        /// </summary>
        internal static List<StructureStep> BuildRouteHistorySteps(string routeId)
        {
            if (string.IsNullOrEmpty(routeId) || !RouteStore.TryGetRoute(routeId, out Route route) || route == null)
                return new List<StructureStep>();
            IReadOnlyList<RouteEvent> els = RouteEventLog.Events;

            string originVesselName = route.IsKscOrigin || route.IsHarvestOrigin
                ? null : TryResolveLiveVesselName(route.Origin.VesselPersistentId);
            string originPlace = route.IsKscOrigin ? "KSC"
                : route.IsHarvestOrigin ? "Harvested en route"
                : FormatEndpointPlace(route.Origin);
            System.Func<int, RouteStop> stopAt = index =>
            {
                if (route.Stops == null || route.Stops.Count == 0) return null;
                int i = index < 0 ? 0 : index;
                return i < route.Stops.Count ? route.Stops[i] : null;
            };
            List<StructureStep> steps = RouteHistoryBuilder.Build(
                els, route.Id, originPlace, originVesselName,
                index => stopAt(index) != null ? FormatEndpointPlace(stopAt(index).Endpoint) : null,
                index => stopAt(index) != null ? TryResolveLiveVesselName(stopAt(index).Endpoint.VesselPersistentId) : null,
                TryResolveLiveVesselName, StructureListWindowUI.ResolvePartTitle,
                id => RouteStore.TryGetRoute(id, out Route partner) && partner != null ? partner.Name : null);
            ParsekLog.Verbose("UI",
                $"Route History rows built: route={ShortId(route.Id)} rows={steps.Count.ToString(CultureInfo.InvariantCulture)}");
            return steps;
        }

        /// <summary>
        /// The Route History's change signature: the route event log's version, whether the
        /// route still resolves, and its name (the title follows a rename). An in-memory compare value, read on Layout passes only.
        /// </summary>
        internal static int RouteHistorySignature(string routeId, out string routeName)
        {
            routeName = null;
            bool found = !string.IsNullOrEmpty(routeId) && RouteStore.TryGetRoute(routeId, out Route route)
                && route != null;
            if (found && RouteStore.TryGetRoute(routeId, out Route named))
                routeName = named.Name;
            return LogisticsRoutePresentation.RouteHistoryChangeSignature(
                RouteEventLog.StateVersion, found, routeName);
        }

        /// <summary>
        /// Live vessel name by persistent id, or null when the pid is 0 / the
        /// vessel is gone / FlightGlobals is unavailable. O(1) per pid
        /// (FlightGlobals.PersistentVesselIds lookup); defensively wrapped like
        /// <c>RouteEndpointResolver.ResolveByPid</c> so a stock-side teardown
        /// null-deref degrades to the pid fallback instead of a crash.
        /// </summary>
        private static string TryResolveLiveVesselName(uint pid)
        {
            if (pid == 0u) return null;
            try
            {
                if (FlightGlobals.fetch != null
                    && FlightGlobals.FindVessel(pid, out Vessel found)
                    && found != null)
                    return found.vesselName;
            }
            catch
            {
                // Best-effort name only; the formatter's pid fallback covers a miss.
            }
            return null;
        }

        /// <summary>
        /// Reads a route's cached legibility values by id. Returns a default
        /// (no-countdown / no-deliveries / grey "-" Status)
        /// struct when the route is absent from the cache (e.g. the very first frame
        /// before the timer has fired). The draw path never recomputes; it only reads
        /// here.
        /// </summary>
        private RouteLegibility GetLegibility(Route route)
        {
            if (route != null && !string.IsNullOrEmpty(route.Id)
                && legibilityCache.TryGetValue(route.Id, out RouteLegibility leg))
                return leg;
            // Cache miss (route not yet refreshed this cycle, or a null/empty id the
            // refresh loop skipped): an explicit "unknown" struct. The Status cell reads a
            // grey "-", never a green Delivering or a cyan New it has not earned, and Next
            // reads "-", until the cache fills.
            return new RouteLegibility
            {
                DestinationText = "-",
                FromToText = "-",
                NextCellText = "-",
                Status = new LogisticsRoutePresentation.StatusCell
                {
                    Word = LogisticsRoutePresentation.StatusWord.Paused,
                    Text = "-",
                    Color = ParsekUI.StatusColorKind.Grey
                },
            };
        }

        // ------------------------------------------------------------------
        // L2: cached-sorted route tables. Re-sort a section only when its row
        // count changes, the sort key/direction changes, or the throttled
        // legibility cache was refreshed (the NextDelivery / Destination sort
        // keys live in that cache). Never per IMGUI frame.
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the cached, sorted list for one route section, re-sorting only when
        /// the section's row count, the shared sort column/direction, or the legibility
        /// stamp changed since the last sort (the SpawnControlUI cached-sorted idiom,
        /// one cache per section). The sort keys for Origin / Destination / NextDelivery
        /// / Status / Delivery are projected from the throttled legibility cache through
        /// <see cref="BuildRouteSortKeys"/>, so the pure comparer never recomputes them.
        /// Logs the decision (the column / direction) once per actual re-sort, NOT per
        /// frame, via a Verbose line.
        /// </summary>
        private List<Route> GetSortedRoutesForSection(List<Route> rows, RouteSection section)
        {
            bool active = section == RouteSection.Active;
            int rowCount = rows?.Count ?? 0;
            int cachedCount = active ? cachedActiveCount : cachedPausedCount;
            float cachedStamp = active ? cachedActiveLegibilityStamp : cachedPausedLegibilityStamp;
            // The column the tables sort by this pass: a sort on a column with no header in
            // the current mode (Runs in Basic) reads as a Route-name sort; the stored
            // routeSortColumn is untouched, so Advanced gets it back.
            LogisticsRouteSortColumn effectiveColumn =
                LogisticsRoutePresentation.EffectiveSortColumn(routeSortColumn, drawTuning);

            bool sortStateChanged = effectiveColumn != cachedRouteSortColumn
                || routeSortAscending != cachedRouteSortAscending
                || cachedStamp != lastLegibilityComputeRealtime;

            if (rowCount != cachedCount || sortStateChanged)
            {
                Dictionary<string, RouteSortKeys> keys = BuildRouteSortKeys(rows);
                List<Route> sorted = LogisticsSortPresentation.SortRoutes(
                    rows, effectiveColumn, routeSortAscending, keys);

                if (active)
                {
                    cachedSortedActive = sorted;
                    cachedActiveCount = rowCount;
                    cachedActiveLegibilityStamp = lastLegibilityComputeRealtime;
                }
                else
                {
                    cachedSortedPaused = sorted;
                    cachedPausedCount = rowCount;
                    cachedPausedLegibilityStamp = lastLegibilityComputeRealtime;
                }
                // The sort column/direction is genuinely shared (both sections sort the
                // same way); a header click resets BOTH counts so both re-sort. The
                // legibility freshness is per-section (stamped above) so a ~1 Hz refresh
                // re-sorts each section independently.
                cachedRouteSortColumn = effectiveColumn;
                cachedRouteSortAscending = routeSortAscending;

                ParsekLog.Verbose("UI",
                    $"Logistics route sort applied section={section} " +
                    $"column={effectiveColumn} stored={routeSortColumn} ascending={routeSortAscending} " +
                    $"rows={rowCount.ToString(CultureInfo.InvariantCulture)}");
            }

            return active ? cachedSortedActive : cachedSortedPaused;
        }

        /// <summary>
        /// Projects the per-route sort keys (Origin / Destination / NextDelivery /
        /// Status / Delivery display values) from the throttled legibility cache into a
        /// plain dictionary the pure <see cref="LogisticsSortPresentation.SortRoutes"/>
        /// comparer reads. Built only when a section actually re-sorts (throttled), not
        /// per frame. Origin and Status come from the same pure formatters the cells
        /// render, so a column sorts by exactly what the player sees.
        /// </summary>
        private Dictionary<string, RouteSortKeys> BuildRouteSortKeys(List<Route> rows)
        {
            var keys = new Dictionary<string, RouteSortKeys>();
            if (rows == null) return keys;
            for (int i = 0; i < rows.Count; i++)
            {
                Route route = rows[i];
                if (route == null || string.IsNullOrEmpty(route.Id)) continue;

                RouteLegibility leg = GetLegibility(route);
                // Next sorts by the countdown only where the cell shows one (a Paused
                // route that is not Send-armed reads "-" and sorts with the dashes).
                bool hasNext = leg.RunScheduled && leg.CountdownBranch
                    != LogisticsCountdownPresentation.CountdownBranch.None;
                // Status sorts by exactly what the merged cell shows; the retired
                // Delivery column's sort member (kept so the seam's sort vocabulary still
                // parses) aliases it.
                string statusText = LogisticsRoutePresentation.StatusSortKey(leg.Status);

                keys[route.Id] = new RouteSortKeys
                {
                    OriginText = leg.OriginShort ?? FormatOrigin(route),
                    DestinationText = leg.DestinationText ?? string.Empty,
                    NextDeliverySeconds = leg.CountdownSeconds,
                    HasNextDelivery = hasNext,
                    StatusText = statusText,
                    DeliveryText = statusText
                };
            }
            return keys;
        }

        /// <summary>
        /// Resolves the H4 Destination cell strings for the legibility cache: the
        /// resolved destination vessel name as the text (coords in the hover tooltip),
        /// or the coords string as the text with no extra tooltip when the vessel
        /// cannot be resolved (e.g. it is unloaded / out of range). The live
        /// <see cref="RouteEndpointResolver.TryResolveEndpoint"/> call (which touches
        /// FlightGlobals and, on an unresolved surface endpoint, scans every vessel) is
        /// why this runs on the ~1 Hz cache refresh and NOT per IMGUI frame; the pure
        /// <see cref="LogisticsDeliveryPresentation.FormatDestinationDisplay"/> picks
        /// name-vs-coords. Logs the resolve decision (rate-limited per route).
        /// </summary>
        private void ResolveDestinationCell(Route route, out string text, out string tooltip,
            out Vessel resolvedVessel, out Vessel[] stopVessels, out string[] stopTexts)
        {
            resolvedVessel = null;
            stopVessels = null;
            stopTexts = null;
            if (route?.Stops != null && route.Stops.Count > 1)
            {
                ResolveMultiStopDestinationCell(route, out text, out tooltip,
                    out resolvedVessel, out stopVessels, out stopTexts);
                return;
            }
            if (route?.Stops == null || route.Stops.Count == 0 || route.Stops[0] == null)
            {
                text = "-";
                tooltip = string.Empty;
                return;
            }

            RouteEndpoint endpoint = route.Stops[0].Endpoint;
            string coords = LogisticsDeliveryPresentation.FormatEndpointCoords(endpoint);

            string resolvedName = null;
            bool resolved = RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel v, out string reason);
            if (resolved && v != null)
            {
                resolvedName = v.vesselName;
                // Surface the resolved Vessel so the M4 capacity probe reuses this one
                // resolve pass instead of a second O(vessels) surface scan.
                resolvedVessel = v;
            }

            text = LogisticsDeliveryPresentation.FormatDestinationDisplay(resolvedName, endpoint);
            // When the name resolved, the coords go to the tooltip; on the coords
            // fallback the cell already shows the coords, so no extra tooltip.
            tooltip = resolved ? coords : string.Empty;

            ParsekLog.VerboseRateLimited("UI", "dest-resolve-" + route.Id,
                resolved
                    ? $"Logistics: destination route={ShortId(route.Id)} resolved=true name='{resolvedName}'"
                    : $"Logistics: destination route={ShortId(route.Id)} resolved=false reason='{reason}' (showing coords)",
                5.0);
        }

        /// <summary>
        /// GUI-P20 multi-stop Destination cell: resolves EVERY stop's endpoint (the
        /// dispatch gate resolves every stop each tick too, so this is the same set of
        /// lookups at ~1 Hz), names the stop
        /// <see cref="LogisticsDeliveryPresentation.ResolveDestinationStopIndex"/> picks
        /// plus "(+N stops)", and puts the whole visit-ordered stop list with each
        /// stop's cargo direction in the cell's hover tooltip. The per-stop vessels and
        /// display texts are surfaced so the DestinationFull capacity line can name and
        /// probe the stop that is actually full without a second resolve pass.
        /// </summary>
        private void ResolveMultiStopDestinationCell(Route route, out string text, out string tooltip,
            out Vessel resolvedVessel, out Vessel[] stopVessels, out string[] stopTexts)
        {
            int count = route.Stops.Count;
            stopVessels = new Vessel[count];
            stopTexts = new string[count];
            var roles = new string[count];
            int resolvedCount = 0;
            for (int i = 0; i < count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null) continue;
                string name = null;
                if (RouteEndpointResolver.TryResolveEndpoint(stop.Endpoint, out Vessel v, out _) && v != null)
                {
                    name = v.vesselName;
                    stopVessels[i] = v;
                    resolvedCount++;
                }
                stopTexts[i] = LogisticsDeliveryPresentation.FormatDestinationDisplay(name, stop.Endpoint);
                roles[i] = LogisticsDeliveryPresentation.StopRoleLabel(stop);
            }

            int shown = LogisticsDeliveryPresentation.ResolveDestinationStopIndex(route.Stops);
            resolvedVessel = shown >= 0 ? stopVessels[shown] : null;
            text = shown >= 0
                ? LogisticsDeliveryPresentation.FormatMultiStopDestination(
                    stopTexts[shown], LogisticsDeliveryPresentation.CountStops(route.Stops))
                : "-";
            tooltip = LogisticsDeliveryPresentation.FormatStopListTooltip(stopTexts, roles,
                TooltipEchoBox.BudgetChars(DefaultWindowWidth, TooltipEchoBox.SingleLine));

            ParsekLog.VerboseRateLimited("UI", "dest-resolve-" + route.Id,
                $"Logistics: destination route={ShortId(route.Id)} stops={count.ToString(CultureInfo.InvariantCulture)} " +
                $"resolved={resolvedCount.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} " +
                $"shownStop={(shown + 1).ToString(CultureInfo.InvariantCulture)} text='{text}'",
                5.0);
        }

        /// <summary>
        /// Replays the destination capacity gate over the already-resolved stop vessels
        /// to find which stop it refuses, so the DestinationFull capacity line names the
        /// stop that is actually full. Mirrors
        /// <c>LiveRouteRuntimeEnvironment.DestinationHasCapacity</c>: one
        /// <see cref="LiveDeliveryCapacityProbe"/> per resolved vessel pid and own-part scope,
        /// shared by every stop delivering there, and an unresolved stop fails open (null
        /// probe). Returns
        /// -1 when every stop fits now (capacity freed since the hold) or none resolved.
        /// </summary>
        private static int FindFullStopIndex(Route route, Vessel[] stopVessels)
        {
            if (route?.Stops == null || stopVessels == null)
                return -1;
            var probes = new EndpointScopedCache<IDeliveryCapacityProbe>();
            bool fits = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route,
                stopIndex =>
                {
                    Vessel v = stopIndex < stopVessels.Length ? stopVessels[stopIndex] : null;
                    if (v == null) return null;
                    bool isLoaded = RouteOrchestrator.EndpointStoreIsLiveParts(v);
                    RouteStop stop = route.Stops[stopIndex];
                    EndpointPartScope scope = stop != null
                        ? RouteEndpointPartScope.ForEndpoint(route, stop.Endpoint, v, isLoaded, "capacity-context")
                        : null;
                    // Same (pid, scope) sharing as the dispatch gate it mirrors.
                    return probes.GetOrAdd(v.persistentId, scope,
                        () => new LiveDeliveryCapacityProbe(v, isLoaded, scope));
                },
                out _,
                out int fullStopIndex);
            return fits ? -1 : fullStopIndex;
        }
    }
}
