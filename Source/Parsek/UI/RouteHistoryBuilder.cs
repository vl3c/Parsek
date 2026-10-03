using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// The rows of a supply route's Route History window: one row per route ledger action,
    /// in time order, drawn by the Mission Log's table (<see cref="StructureListWindowUI"/>
    /// in its route mode) under the same Time | Event | Location | Vessel columns.
    /// <list type="bullet">
    ///   <item>RouteDispatched: "Run N: Sent", or "Run N: Sent once" for a run armed by Send
    ///   (the row's <c>RouteSendOnce</c> stamp), followed by what the launch cost when it
    ///   cost anything: ", cost 7,410 funds" and / or the cargo taken from the origin vessel
    ///   (", cost 257.8 LiquidFuel, 315.1 Oxidizer"), read from the run's own
    ///   RouteCargoDebited row (same RouteCycleId). Location and Vessel name the origin.</item>
    ///   <item>RouteCargoPickedUp: "Run N: Picked up ..." at an intermediate stop, located
    ///   at that stop and its vessel (the row's endpoint pid first).</item>
    ///   <item>RouteCargoDelivered: "Run N: Delivered ..." (with "of" amounts when the run
    ///   fell short), the row that finishes a run, located at its stop.</item>
    ///   <item>RouteHeld: "Held: origin is short 108.8 LiquidFuel" - the hold's reason as
    ///   the Logistics window words it, without its live-route advice, and with no run
    ///   number (a held run is not a run). One row per hold episode and reason. Located at
    ///   the origin for an origin-cargo or funds hold, at the named pickup source's vessel
    ///   for a source hold, "-" otherwise.</item>
    ///   <item>RoutePaused / RouteResumed / RouteEndpointLost: state rows ("Paused",
    ///   "Paused after the run", "Activated", "Stopped: destination lost", ...).</item>
    /// </list>
    /// Cargo reads amount first everywhere ("150.0 LiquidFuel, 40.0 Oxidizer"), as the Sent
    /// cost and a short delivery do. Stored parts in a held reason are named by
    /// <c>partTitle</c> (the part's title; the raw name when it does not resolve).
    /// N is the run's position in dispatch order (a run's first row assigns it). The input
    /// is the Effective Ledger Set (<see cref="EffectiveState.ComputeELS"/>), so a rewound or
    /// tombstoned run is simply absent, its debit with it. Debit rows (funds or origin
    /// cargo) are not shown as rows: their cost is folded into the run's Sent row, and a
    /// debit with no Sent row of its own adds nothing. Pure: names come through the resolvers.
    /// </summary>
    internal static class RouteHistoryBuilder
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The window's empty-state line.</summary>
        internal const string EmptyText = "No runs yet.";

        /// <summary>
        /// Builds the rows for <paramref name="routeId"/> from <paramref name="els"/>.
        /// <paramref name="originPlace"/> / <paramref name="originVessel"/> label the Sent
        /// rows ("KSC" / "-" for a funds-paid launch); <paramref name="stopPlace"/> and
        /// <paramref name="stopVessel"/> name a stop by index; <paramref name="vesselByPid"/>
        /// names a pickup endpoint by its recorded pid (null when it no longer resolves).
        /// </summary>
        internal static List<StructureStep> Build(
            IReadOnlyList<GameAction> els, string routeId,
            string originPlace, string originVessel,
            Func<int, string> stopPlace, Func<int, string> stopVessel,
            Func<uint, string> vesselByPid, Func<string, string> partTitle = null)
        {
            var rows = new List<KeyValuePair<int, StructureStep>>();
            if (els == null || string.IsNullOrEmpty(routeId))
                return new List<StructureStep>();

            // The run's debit, keyed by RouteCycleId, folded into its Sent row below.
            var debitByRun = new Dictionary<string, GameAction>(StringComparer.Ordinal);
            for (int i = 0; i < els.Count; i++)
            {
                GameAction d = els[i];
                if (d != null && d.Type == GameActionType.RouteCargoDebited
                    && !string.IsNullOrEmpty(d.RouteCycleId)
                    && string.Equals(d.RouteId, routeId, StringComparison.Ordinal)
                    && !debitByRun.ContainsKey(d.RouteCycleId))
                    debitByRun[d.RouteCycleId] = d;
            }

            var runOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
            int order = 0;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null || !string.Equals(a.RouteId, routeId, StringComparison.Ordinal))
                    continue;
                StructureStep step;
                switch (a.Type)
                {
                    case GameActionType.RouteDispatched:
                    {
                        GameAction debit = null;
                        if (!string.IsNullOrEmpty(a.RouteCycleId))
                            debitByRun.TryGetValue(a.RouteCycleId, out debit);
                        step = Row(a.UT, StructureStepKind.Launch,
                            RunPrefix(runOrdinal, a.RouteCycleId) + (a.RouteSendOnce ? "Sent once" : "Sent")
                                + SentCostSuffix(debit),
                            Or(originPlace), Or(originVessel));
                        break;
                    }
                    case GameActionType.RouteCargoPickedUp:
                    {
                        string vessel = (a.RouteOriginVesselPid != 0u && vesselByPid != null
                                ? vesselByPid(a.RouteOriginVesselPid) : null)
                            ?? (stopVessel != null ? stopVessel(a.RouteStopIndex) : null);
                        step = Row(a.UT, StructureStepKind.Dock,
                            RunPrefix(runOrdinal, a.RouteCycleId) + "Picked up "
                                + Amounts(null, a.RouteResourceManifest, a.RouteInventoryManifest)
                                + (a.RouteRequestedResourceManifest != null
                                   && a.RouteRequestedResourceManifest.Count > 0
                                    ? " (the source was short)" : ""),
                            Or(stopPlace != null ? stopPlace(a.RouteStopIndex) : null), Or(vessel));
                        break;
                    }
                    case GameActionType.RouteCargoDelivered:
                        step = Row(a.UT, StructureStepKind.Dock,
                            RunPrefix(runOrdinal, a.RouteCycleId) + "Delivered "
                                + Amounts(a.RouteRequestedResourceManifest, a.RouteResourceManifest,
                                    a.RouteInventoryManifest),
                            Or(stopPlace != null ? stopPlace(a.RouteStopIndex) : null),
                            Or(stopVessel != null ? stopVessel(a.RouteStopIndex) : null));
                        break;
                    case GameActionType.RouteHeld:
                    {
                        HeldPlace(a, originPlace, originVessel, vesselByPid,
                            out string heldPlace, out string heldVessel);
                        step = Row(a.UT, StructureStepKind.Terminal,
                            LogisticsHoldPresentation.FormatHistoryHeldRow(
                                LogisticsHoldPresentation.DescribeHoldForHistory(
                                    a.RouteHoldKind, a.RouteEndpointReason, a.RouteHoldShortfall, partTitle)),
                            heldPlace, heldVessel);
                        break;
                    }
                    case GameActionType.RoutePaused:
                        step = Row(a.UT, StructureStepKind.Terminal, PausedLabel(a.RouteEndpointReason),
                            StructureLocationFormatter.Missing, StructureLocationFormatter.Missing);
                        break;
                    case GameActionType.RouteResumed:
                        step = Row(a.UT, StructureStepKind.Terminal, ResumedLabel(a.RouteEndpointReason),
                            StructureLocationFormatter.Missing, StructureLocationFormatter.Missing);
                        break;
                    case GameActionType.RouteEndpointLost:
                        step = Row(a.UT, StructureStepKind.Terminal,
                            a.RouteEndpointReason != null
                                && a.RouteEndpointReason.StartsWith("origin-", StringComparison.Ordinal)
                                ? "Stopped: origin lost" : "Stopped: destination lost",
                            StructureLocationFormatter.Missing, StructureLocationFormatter.Missing);
                        break;
                    default:
                        continue;
                }
                rows.Add(new KeyValuePair<int, StructureStep>(order++, step));
            }

            // Time order; ledger order breaks ties (a run's Sent before its first cargo row).
            rows.Sort((x, y) =>
            {
                int byUt = x.Value.UT.CompareTo(y.Value.UT);
                return byUt != 0 ? byUt : x.Key.CompareTo(y.Key);
            });
            var result = new List<StructureStep>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
                result.Add(rows[i].Value);
            return result;
        }

        /// <summary>
        /// What a run's launch cost, from its RouteCargoDebited row: ", cost 7,410 funds",
        /// ", cost 257.8 LiquidFuel, 315.1 Oxidizer", both joined, or "" when the launch cost
        /// nothing. Funds read <c>RouteKscFundsCost</c>, which the dispatch writes only for a
        /// Career KSC launch, so Sandbox and Science never show funds; a zero cost is left out
        /// rather than shown as "free". Funds format as the route's Cost/run line
        /// (<see cref="LogisticsCostPresentation.FormatFunds"/>: grouped whole funds,
        /// InvariantCulture). Cargo is shown only for a debit taken from an origin VESSEL (the
        /// row carries its pid): a KSC launch's row lists the cargo the funds bought, and a
        /// legacy row a manifest nothing removed, so neither is a cost. Cargo reads amount
        /// first, as a short delivery does ("40.0 of 150.0 LiquidFuel").
        /// </summary>
        internal static string SentCostSuffix(GameAction debit)
        {
            if (debit == null)
                return "";
            var parts = new List<string>(2);
            double funds = debit.RouteKscFundsCost;
            if (funds > 0.0 && !double.IsNaN(funds) && !double.IsInfinity(funds))
                parts.Add(Logistics.LogisticsCostPresentation.FormatFunds(funds));
            if (debit.RouteOriginVesselPid != 0u)
            {
                string cargo = DebitedCargo(debit.RouteResourceManifest, debit.RouteInventoryManifest);
                if (!string.IsNullOrEmpty(cargo))
                    parts.Add(cargo);
            }
            return parts.Count == 0 ? "" : ", cost " + string.Join(", ", parts.ToArray());
        }

        // The cargo a launch took from its origin vessel; null when it took nothing.
        private static string DebitedCargo(
            IReadOnlyDictionary<string, double> resources, List<InventoryPayloadItem> inventory)
        {
            var taken = new List<string>();
            if (resources != null)
            {
                foreach (KeyValuePair<string, double> kv in resources)
                {
                    if (kv.Value > 0.0)
                        taken.Add(kv.Value.ToString("F1", IC) + " " + kv.Key);
                }
            }
            int parts = inventory?.Count ?? 0;
            if (parts > 0)
                taken.Add(parts.ToString(IC) + (parts == 1 ? " stored part" : " stored parts"));
            return taken.Count == 0 ? null : string.Join(", ", taken.ToArray());
        }

        /// <summary>
        /// Where a held row is located. An origin-cargo or funds hold sits at the origin
        /// (<paramref name="originPlace"/> / <paramref name="originVessel"/>); a pickup-source
        /// hold ("source:" / "source-reserved:" tokens carry the source's pid and name) names
        /// that vessel, live name first; every other hold (destination full, endpoint lost,
        /// a linked-route wait, unavailable flights) has no single place, so "-".
        /// </summary>
        internal static void HeldPlace(GameAction held, string originPlace, string originVessel,
            Func<uint, string> vesselByPid, out string place, out string vessel)
        {
            place = StructureLocationFormatter.Missing;
            vessel = StructureLocationFormatter.Missing;
            if (held == null)
                return;
            bool originKind = held.RouteHoldKind == RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo
                || held.RouteHoldKind == RouteDispatchEvaluator.EligibilityFailureKind.FundsShort;
            if (!originKind)
                return;
            string token = held.RouteEndpointReason ?? "";
            if (token.StartsWith("origin-lacks-", StringComparison.Ordinal))
                token = token.Substring("origin-lacks-".Length);
            string sourceBody = token.StartsWith("source:", StringComparison.Ordinal)
                ? token.Substring("source:".Length)
                : token.StartsWith("source-reserved:", StringComparison.Ordinal)
                    ? token.Substring("source-reserved:".Length)
                    : null;
            if (sourceBody != null)
            {
                string[] parts = sourceBody.Split(new[] { ':' }, 3);
                string live = null;
                if (parts.Length > 0 && vesselByPid != null
                    && uint.TryParse(parts[0], NumberStyles.Integer, IC, out uint pid) && pid != 0u)
                    live = vesselByPid(pid);
                vessel = Or(live ?? (parts.Length > 1 ? parts[1] : null));
                return;
            }
            if (token.StartsWith("pickup-source-unresolved:", StringComparison.Ordinal))
                return;
            place = Or(originPlace);
            vessel = Or(originVessel);
        }

        /// <summary>The state row label of a RoutePaused reason.</summary>
        internal static string PausedLabel(string reason)
        {
            switch (reason)
            {
                case "delivered-then-paused":
                case "delivered-partial-then-paused":
                case RouteOrchestrator.DeliveredReplayThenPausedReason:
                    return "Paused after the run";
                case "blocked-then-paused":
                    return "Paused after a held run";
                case "AutoPause:MissingSourceRecording":
                    return "Stopped: flight missing";
                case "AutoPause:SourceChanged":
                    return "Stopped: flight changed";
                default:
                    return "Paused";
            }
        }

        /// <summary>The state row label of a RouteResumed reason.</summary>
        internal static string ResumedLabel(string reason)
        {
            return string.IsNullOrEmpty(reason) || reason == "player-activate" ? "Activated" : "Resumed";
        }

        /// <summary>
        /// One Time cell per row: every row its own exact date (a route's runs are days to
        /// years apart, so the Mission Log's elapsed "T+" from the first row would read as
        /// noise). A row with no time reads the missing-value text.
        /// </summary>
        internal static string[] FormatRowTimes(List<StructureStep> steps, Func<double, string> dateFormatter)
        {
            if (steps == null) return new string[0];
            var cells = new string[steps.Count];
            for (int i = 0; i < steps.Count; i++)
            {
                double ut = steps[i].UT;
                cells[i] = double.IsNaN(ut) || double.IsInfinity(ut)
                    ? StructureLocationFormatter.Missing
                    : ReservationExplanation.FormatDate(ut, dateFormatter);
            }
            return cells;
        }

        private static string RunPrefix(Dictionary<string, int> ordinals, string cycleId)
        {
            if (string.IsNullOrEmpty(cycleId))
                return "";
            if (!ordinals.TryGetValue(cycleId, out int n))
            {
                n = ordinals.Count + 1;
                ordinals[cycleId] = n;
            }
            return "Run " + n.ToString(IC) + ": ";
        }

        // A delivery that fell short: "40.0 of 150.0 LiquidFuel (110.0 did not fit), 2 stored
        // parts" (the delivered-row wording); otherwise amount first, resources in ordinal
        // order: "150.0 LiquidFuel, 40.0 Oxidizer, 2 stored parts"; "nothing" when nothing
        // moved. (The Logistics table's Delivers column keeps its own "LiquidFuel 150.0".)
        private static string Amounts(
            IReadOnlyDictionary<string, double> requested, IReadOnlyDictionary<string, double> actual,
            List<InventoryPayloadItem> inventory)
        {
            string text;
            if (requested != null && requested.Count > 0)
            {
                text = LogisticsDeliveryPresentation.FormatRealizedDelivery(requested, actual);
                if (text.StartsWith("delivered ", StringComparison.Ordinal))
                    text = text.Substring("delivered ".Length);
            }
            else if (actual != null && actual.Count > 0)
            {
                text = LogisticsDeliveryPresentation.FormatRealizedDelivery(null, actual);
                if (text.StartsWith("delivered ", StringComparison.Ordinal))
                    text = text.Substring("delivered ".Length);
            }
            else
            {
                text = null;
            }
            int parts = inventory?.Count ?? 0;
            if (parts > 0)
            {
                string partsText = parts.ToString(IC) + (parts == 1 ? " stored part" : " stored parts");
                text = string.IsNullOrEmpty(text) ? partsText : text + ", " + partsText;
            }
            return string.IsNullOrEmpty(text) ? "nothing" : text;
        }

        private static StructureStep Row(double ut, StructureStepKind kind, string label, string location, string vessel)
        {
            return new StructureStep
            {
                UT = ut,
                Kind = kind,
                Label = label,
                Location = location,
                VesselName = vessel,
            };
        }

        private static string Or(string text)
        {
            return string.IsNullOrEmpty(text) ? StructureLocationFormatter.Missing : text;
        }
    }
}
