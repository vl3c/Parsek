using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// One physical route crossing (a delivery into, a pickup from, or an origin debit of a
    /// vessel) that a rewind retired from the ledger and refunded, kept because a chain tip
    /// snapshot captured after it still carries its cargo
    /// (CHAIN-TIP-SNAPSHOT-CARRIES-UNPAID-ROUTE-CARGO). Built from the retired
    /// <see cref="GameAction"/> plus the route endpoint it touched, resolved while the route
    /// was still in the store: the delivery row itself names no vessel.
    /// </summary>
    internal sealed class RetiredRouteCargoRow
    {
        /// <summary><see cref="GameActionType.RouteCargoDelivered"/>, <see cref="GameActionType.RouteCargoPickedUp"/> or <see cref="GameActionType.RouteCargoDebited"/>.</summary>
        internal GameActionType Type;
        internal double UT;
        /// <summary>The rewind cutoff that retired the row (the row's UT is strictly after it).</summary>
        internal double CutoffUT;
        internal string RouteId;
        internal string CycleId;
        internal int StopIndex = -1;
        /// <summary>The route endpoint's pid (the stop for a delivery or pickup, the origin for a debit); 0 when the route was not found.</summary>
        internal uint EndpointPid;
        /// <summary>The route endpoint's launch guid; null when unknown.</summary>
        internal string EndpointGuid;
        /// <summary>The live vessel the writer resolved (pickup and debit rows carry it); 0 when unknown.</summary>
        internal uint ActualVesselPid;
        /// <summary>Positive per-resource amounts the crossing moved.</summary>
        internal Dictionary<string, double> Resources;
        /// <summary>The committed trees whose chain tip snapshot was captured after this crossing, at the retire.</summary>
        internal List<string> TipTreeIds = new List<string>();

        /// <summary>A pickup or an origin debit took the cargo FROM the vessel; a delivery put it in.</summary>
        internal bool TookFromVessel => Type != GameActionType.RouteCargoDelivered;

        internal string CrossingKey => RetiredRouteCargoStore.CrossingKey(RouteId, CycleId, StopIndex, Type, UT);
    }

    /// <summary>
    /// The retired route crossings that some committed chain tip's snapshot still carries.
    ///
    /// <para>A go-back rewind, a Re-Fly restore and an in-session load back in time remove the
    /// route rows after the cutoff from the ledger (<see cref="RouteLedgerRetire"/>): the
    /// refund is right, because the reverted save restores the origin. But a chain tip
    /// snapshot (the claimed station as a committed mission left it) was captured after those
    /// crossings and holds their cargo, and while the station is held back from the rewind to
    /// the tip's spawn the replayed crossings into it are blocked. So the rows are kept here,
    /// each tagged with the trees whose tip it lands in, and the tip spawn takes them back out
    /// of its spawn copy (<see cref="ChainTipRouteCargo"/>).</para>
    ///
    /// <para>One entry per crossing: a crossing retired again (a later timeline replayed it)
    /// replaces the earlier entry. Persisted beside the ledger actions as an additive
    /// <c>RETIRED_ROUTE_CARGO</c> child of the ledger file, absent while empty, so a build that
    /// predates it reads the file unchanged. Survives in-session loads with the ledger and is
    /// kept by <c>Ledger.Clear</c> (the Re-Fly restore clears and re-adds the actions).</para>
    /// </summary>
    internal static class RetiredRouteCargoStore
    {
        private const string Tag = "ChainTipCargo";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal const string NodeName = "RETIRED_ROUTE_CARGO";
        private const string RowNodeName = "ROW";
        private const string ResourceNodeName = "RESOURCE";

        private static readonly List<RetiredRouteCargoRow> rows = new List<RetiredRouteCargoRow>();

        internal static IReadOnlyList<RetiredRouteCargoRow> Rows => rows;

        /// <summary>
        /// The identity of one route crossing across timelines: route, cycle, stop and row type.
        /// A replayed crossing reuses its cycle id (blocked crossings advance the counter too),
        /// so the same key names the same crossing. A row with no cycle id keys on its UT.
        /// </summary>
        internal static string CrossingKey(string routeId, string cycleId, int stopIndex, GameActionType type, double ut)
        {
            string key = (routeId ?? "") + "|" + (cycleId ?? "") + "|"
                + stopIndex.ToString(IC) + "|" + ((int)type).ToString(IC);
            if (string.IsNullOrEmpty(cycleId))
                key += "|" + ut.ToString("R", IC);
            return key;
        }

        internal static string CrossingKey(GameAction a)
        {
            return a == null ? null : CrossingKey(a.RouteId, a.RouteCycleId, a.RouteStopIndex, a.Type, a.UT);
        }

        /// <summary>The three route row types that move cargo physically.</summary>
        internal static bool IsCargoRowType(GameActionType t)
        {
            return t == GameActionType.RouteCargoDelivered
                || t == GameActionType.RouteCargoPickedUp
                || t == GameActionType.RouteCargoDebited;
        }

        /// <summary>
        /// Pure: the stash entry for one retired row, or null when it moved no resource
        /// (funds-only, marker and inventory-only rows). The endpoint comes from
        /// <paramref name="route"/> (null when the route is gone: the entry then matches by the
        /// row's own vessel pid only).
        /// </summary>
        internal static RetiredRouteCargoRow BuildRow(GameAction retired, double cutoffUT, Route route)
        {
            if (retired == null || !IsCargoRowType(retired.Type)
                || !RouteLedgerRetire.IsPhysicalRouteMutation(retired))
                return null;
            Dictionary<string, double> resources = CopyPositive(retired.RouteResourceManifest);
            if (resources == null)
                return null;

            var row = new RetiredRouteCargoRow
            {
                Type = retired.Type,
                UT = retired.UT,
                CutoffUT = cutoffUT,
                RouteId = retired.RouteId,
                CycleId = retired.RouteCycleId,
                StopIndex = retired.RouteStopIndex,
                ActualVesselPid = retired.Type == GameActionType.RouteCargoDelivered
                    ? 0u
                    : retired.RouteOriginVesselPid,
                Resources = resources
            };

            if (route != null)
            {
                if (retired.Type == GameActionType.RouteCargoDebited)
                {
                    if (!route.IsKscOrigin)
                    {
                        row.EndpointPid = route.Origin.VesselPersistentId;
                        row.EndpointGuid = route.Origin.LaunchGuid;
                    }
                }
                else
                {
                    int stop = retired.RouteStopIndex >= 0 ? retired.RouteStopIndex : 0;
                    if (route.Stops != null && stop < route.Stops.Count && route.Stops[stop] != null)
                    {
                        row.EndpointPid = route.Stops[stop].Endpoint.VesselPersistentId;
                        row.EndpointGuid = route.Stops[stop].Endpoint.LaunchGuid;
                    }
                }
            }
            return row;
        }

        private static Dictionary<string, double> CopyPositive(Dictionary<string, double> manifest)
        {
            if (manifest == null || manifest.Count == 0)
                return null;
            Dictionary<string, double> copy = null;
            foreach (KeyValuePair<string, double> kv in manifest)
            {
                if (string.IsNullOrEmpty(kv.Key) || double.IsNaN(kv.Value)
                    || double.IsInfinity(kv.Value) || kv.Value <= 0.0)
                    continue;
                if (copy == null)
                    copy = new Dictionary<string, double>(StringComparer.Ordinal);
                copy[kv.Key] = kv.Value;
            }
            return copy;
        }

        /// <summary>
        /// Adds entries, one per crossing: an entry whose crossing is already stashed replaces
        /// it and keeps the earlier entry's tree tags too. Returns how many were new;
        /// <paramref name="replaced"/> counts the replacements.
        /// </summary>
        internal static int Merge(IList<RetiredRouteCargoRow> incoming, out int replaced)
        {
            replaced = 0;
            int added = 0;
            if (incoming == null)
                return 0;
            for (int i = 0; i < incoming.Count; i++)
            {
                RetiredRouteCargoRow row = incoming[i];
                if (row == null)
                    continue;
                string key = row.CrossingKey;
                int existing = -1;
                for (int j = 0; j < rows.Count; j++)
                {
                    if (string.Equals(rows[j].CrossingKey, key, StringComparison.Ordinal))
                    {
                        existing = j;
                        break;
                    }
                }
                if (existing >= 0)
                {
                    // The trees tagged earlier still carry this crossing in their snapshots.
                    List<string> earlierTrees = rows[existing].TipTreeIds;
                    if (row.TipTreeIds == null)
                        row.TipTreeIds = new List<string>();
                    if (earlierTrees != null)
                    {
                        for (int t = 0; t < earlierTrees.Count; t++)
                        {
                            if (!row.TipTreeIds.Contains(earlierTrees[t]))
                                row.TipTreeIds.Add(earlierTrees[t]);
                        }
                    }
                    rows[existing] = row;
                    replaced++;
                }
                else
                {
                    rows.Add(row);
                    added++;
                }
            }
            return added;
        }

        internal static void Clear()
        {
            rows.Clear();
        }

        internal static void ResetForTesting()
        {
            rows.Clear();
        }

        /// <summary>Writes the stash as a child of the ledger file root; nothing while empty.</summary>
        internal static void SerializeInto(ConfigNode ledgerRoot)
        {
            if (ledgerRoot == null || rows.Count == 0)
                return;

            ConfigNode node = ledgerRoot.AddNode(NodeName);
            for (int i = 0; i < rows.Count; i++)
            {
                RetiredRouteCargoRow row = rows[i];
                ConfigNode r = node.AddNode(RowNodeName);
                r.AddValue("type", row.Type.ToString());
                r.AddValue("ut", row.UT.ToString("R", IC));
                r.AddValue("cutoffUT", row.CutoffUT.ToString("R", IC));
                r.AddValue("routeId", row.RouteId ?? "");
                if (!string.IsNullOrEmpty(row.CycleId))
                    r.AddValue("cycleId", row.CycleId);
                r.AddValue("stopIndex", row.StopIndex.ToString(IC));
                r.AddValue("endpointPid", row.EndpointPid.ToString(IC));
                if (!string.IsNullOrEmpty(row.EndpointGuid))
                    r.AddValue("endpointGuid", row.EndpointGuid);
                if (row.ActualVesselPid != 0u)
                    r.AddValue("actualPid", row.ActualVesselPid.ToString(IC));
                if (row.TipTreeIds != null)
                {
                    for (int t = 0; t < row.TipTreeIds.Count; t++)
                        r.AddValue("tipTree", row.TipTreeIds[t]);
                }
                if (row.Resources != null)
                {
                    var names = new List<string>(row.Resources.Keys);
                    names.Sort(StringComparer.Ordinal);
                    for (int k = 0; k < names.Count; k++)
                    {
                        ConfigNode res = r.AddNode(ResourceNodeName);
                        res.AddValue("name", names[k]);
                        res.AddValue("amount", row.Resources[names[k]].ToString("R", IC));
                    }
                }
            }
        }

        /// <summary>
        /// Replaces the stash with the ledger root's child (none: empty). Returns the number of
        /// unreadable entries dropped.
        /// </summary>
        internal static int LoadFrom(ConfigNode ledgerRoot)
        {
            rows.Clear();
            ConfigNode node = ledgerRoot?.GetNode(NodeName);
            if (node == null)
                return 0;

            int malformed = 0;
            ConfigNode[] rowNodes = node.GetNodes(RowNodeName);
            for (int i = 0; i < rowNodes.Length; i++)
            {
                RetiredRouteCargoRow row = ReadRow(rowNodes[i]);
                if (row == null)
                {
                    malformed++;
                    continue;
                }
                rows.Add(row);
            }
            if (malformed > 0)
                ParsekLog.Warn(Tag,
                    "Retired route cargo: dropped " + malformed.ToString(IC)
                    + " unreadable entries on load, kept=" + rows.Count.ToString(IC));
            else
                ParsekLog.Verbose(Tag,
                    "Retired route cargo loaded: rows=" + rows.Count.ToString(IC));
            return malformed;
        }

        private static RetiredRouteCargoRow ReadRow(ConfigNode r)
        {
            GameActionType type;
            if (!TryParseType(r.GetValue("type"), out type) || !IsCargoRowType(type))
                return null;
            if (!TryParseDouble(r.GetValue("ut"), out double ut)
                || !TryParseDouble(r.GetValue("cutoffUT"), out double cutoff))
                return null;
            string routeId = r.GetValue("routeId");
            if (string.IsNullOrEmpty(routeId))
                return null;

            var row = new RetiredRouteCargoRow
            {
                Type = type,
                UT = ut,
                CutoffUT = cutoff,
                RouteId = routeId,
                CycleId = r.GetValue("cycleId"),
                EndpointGuid = r.GetValue("endpointGuid")
            };
            if (int.TryParse(r.GetValue("stopIndex"), NumberStyles.Integer, IC, out int stop))
                row.StopIndex = stop;
            if (uint.TryParse(r.GetValue("endpointPid"), NumberStyles.Integer, IC, out uint endpointPid))
                row.EndpointPid = endpointPid;
            if (uint.TryParse(r.GetValue("actualPid"), NumberStyles.Integer, IC, out uint actualPid))
                row.ActualVesselPid = actualPid;
            string[] trees = r.GetValues("tipTree");
            for (int t = 0; t < trees.Length; t++)
            {
                if (!string.IsNullOrEmpty(trees[t]))
                    row.TipTreeIds.Add(trees[t]);
            }

            ConfigNode[] resNodes = r.GetNodes(ResourceNodeName);
            for (int k = 0; k < resNodes.Length; k++)
            {
                string name = resNodes[k].GetValue("name");
                if (string.IsNullOrEmpty(name)
                    || !TryParseDouble(resNodes[k].GetValue("amount"), out double amount)
                    || amount <= 0.0)
                    continue;
                if (row.Resources == null)
                    row.Resources = new Dictionary<string, double>(StringComparer.Ordinal);
                row.Resources[name] = amount;
            }
            if (row.Resources == null || row.TipTreeIds.Count == 0)
                return null;
            return row;
        }

        private static bool TryParseType(string s, out GameActionType type)
        {
            type = default(GameActionType);
            if (string.IsNullOrEmpty(s))
                return false;
            try
            {
                type = (GameActionType)Enum.Parse(typeof(GameActionType), s, false);
                return Enum.IsDefined(typeof(GameActionType), type);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryParseDouble(string s, out double value)
        {
            return double.TryParse(s, NumberStyles.Float, IC, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
