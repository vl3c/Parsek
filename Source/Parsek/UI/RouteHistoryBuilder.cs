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
    ///   (the row's <c>RouteSendOnce</c> stamp). Location and Vessel name the origin.</item>
    ///   <item>RouteCargoPickedUp: "Run N: Picked up ..." at an intermediate stop, located
    ///   at that stop and its vessel (the row's endpoint pid first).</item>
    ///   <item>RouteCargoDelivered: "Run N: Delivered ..." (with "of" amounts when the run
    ///   fell short), the row that finishes a run, located at its stop.</item>
    ///   <item>RoutePaused / RouteResumed / RouteEndpointLost: state rows ("Paused",
    ///   "Paused after the run", "Activated", "Stopped: destination lost", ...).</item>
    /// </list>
    /// N is the run's position in dispatch order (a run's first row assigns it). The input
    /// is the Effective Ledger Set (<see cref="EffectiveState.ComputeELS"/>), so a rewound or
    /// tombstoned run is simply absent. Debit rows (funds or origin cargo) are not shown:
    /// the run's Sent row stands for them. Pure: names come through the resolvers.
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
            Func<uint, string> vesselByPid)
        {
            var rows = new List<KeyValuePair<int, StructureStep>>();
            if (els == null || string.IsNullOrEmpty(routeId))
                return new List<StructureStep>();

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
                        step = Row(a.UT, StructureStepKind.Launch,
                            RunPrefix(runOrdinal, a.RouteCycleId) + (a.RouteSendOnce ? "Sent once" : "Sent"),
                            Or(originPlace), Or(originVessel));
                        break;
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

        /// <summary>The state row label of a RoutePaused reason.</summary>
        internal static string PausedLabel(string reason)
        {
            switch (reason)
            {
                case "delivered-then-paused":
                case "delivered-partial-then-paused":
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
        // parts" (the delivered-row wording); otherwise
        // "150.0 LiquidFuel, 2 stored parts"; "nothing" when nothing moved.
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
            else
            {
                text = LogisticsDeliveryPresentation.FormatWouldDeliver(actual, null);
                if (text == "(nothing)") text = null;
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
