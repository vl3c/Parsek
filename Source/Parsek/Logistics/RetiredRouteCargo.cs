using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Names ONE chain tip snapshot: the recording that held it when it was tagged, that
    /// recording's optimizer chain, its tree, when it was captured (the recording's end at the
    /// time) and a fingerprint of its resource content. A later recording of the same tree (a
    /// switch continuation, a Re-Fly fork) carries a different snapshot and is a different
    /// tip; a snapshot replaced in place changes the fingerprint.
    /// </summary>
    internal sealed class RetiredRouteCargoTipTag
    {
        internal string TreeId;
        internal string RecordingId;
        /// <summary>The recording's optimizer chain id when tagged; null when it had none.</summary>
        internal string ChainId;
        internal double CaptureUT;
        internal string Fingerprint;

        internal RetiredRouteCargoTipTag Clone()
        {
            return new RetiredRouteCargoTipTag
            {
                TreeId = TreeId,
                RecordingId = RecordingId,
                ChainId = ChainId,
                CaptureUT = CaptureUT,
                Fingerprint = Fingerprint
            };
        }

        internal bool SameAs(RetiredRouteCargoTipTag other)
        {
            return other != null
                && string.Equals(TreeId, other.TreeId, StringComparison.Ordinal)
                && string.Equals(RecordingId, other.RecordingId, StringComparison.Ordinal)
                && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The lowest rewind or load-back cutoff seen since a tip snapshot was first seen by a
    /// retire. Every row created after such a cutoff belongs to a timeline that branched after
    /// the snapshot was captured, so a later retire never tags it to that snapshot.
    /// </summary>
    internal sealed class RetiredRouteCargoWatermark
    {
        internal RetiredRouteCargoTipTag Snapshot;
        internal double LowestCutoffUT = double.PositiveInfinity;
    }

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
        /// <summary>Diagnostic only: cycle ids are rebuilt from the kept rows at every rewind and do not name a crossing across timelines.</summary>
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
        /// <summary>The chain tip snapshots that carry this crossing.</summary>
        internal List<RetiredRouteCargoTipTag> Tips = new List<RetiredRouteCargoTipTag>();

        /// <summary>A pickup or an origin debit took the cargo FROM the vessel; a delivery put it in.</summary>
        internal bool TookFromVessel => Type != GameActionType.RouteCargoDelivered;

        internal string RowKey => RetiredRouteCargoStore.RowKey(RouteId, CycleId, StopIndex, Type, UT);

        internal string ReplayGroupKey => RetiredRouteCargoStore.ReplayGroupKey(RouteId, StopIndex, Type);
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
    /// each tagged with the tip snapshots that carry it, and a spawn from one of those
    /// snapshots takes them back out of its spawn copy (<see cref="ChainTipRouteCargo"/>).</para>
    ///
    /// <para>Also kept: one watermark per tip snapshot, the lowest retire cutoff seen since a
    /// retire first saw it. Persisted beside the ledger actions as an additive
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
        private const string TipNodeName = "TIP";
        private const string WatermarkNodeName = "WATERMARK";
        private const string ResourceNodeName = "RESOURCE";

        private static readonly List<RetiredRouteCargoRow> rows = new List<RetiredRouteCargoRow>();
        private static readonly List<RetiredRouteCargoWatermark> watermarks = new List<RetiredRouteCargoWatermark>();

        internal static IReadOnlyList<RetiredRouteCargoRow> Rows => rows;

        internal static IReadOnlyList<RetiredRouteCargoWatermark> Watermarks => watermarks;

        /// <summary>One retired row exactly (a row is retired once; a re-retire of the same row is a duplicate).</summary>
        internal static string RowKey(string routeId, string cycleId, int stopIndex, GameActionType type, double ut)
        {
            return ReplayGroupKey(routeId, stopIndex, type) + "|" + (cycleId ?? "") + "|" + ut.ToString("R", IC);
        }

        /// <summary>
        /// The crossings one replay can stand for: same route, stop and row type. Which crossing
        /// a replay stands for is decided by UT, never by cycle id (the counters behind cycle ids
        /// are rebuilt from the kept rows at every rewind, so a replay can carry another id).
        /// </summary>
        internal static string ReplayGroupKey(string routeId, int stopIndex, GameActionType type)
        {
            return (routeId ?? "") + "|" + stopIndex.ToString(IC) + "|" + ((int)type).ToString(IC);
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
        /// Adds entries; an entry for a row already stashed (same <see cref="RetiredRouteCargoRow.RowKey"/>)
        /// only adds its snapshot tags to the existing one. Returns how many were new;
        /// <paramref name="merged"/> counts the duplicates folded in.
        /// </summary>
        internal static int Merge(IList<RetiredRouteCargoRow> incoming, out int merged)
        {
            merged = 0;
            int added = 0;
            if (incoming == null)
                return 0;
            for (int i = 0; i < incoming.Count; i++)
            {
                RetiredRouteCargoRow row = incoming[i];
                if (row == null)
                    continue;
                RetiredRouteCargoRow existing = null;
                string key = row.RowKey;
                for (int j = 0; j < rows.Count; j++)
                {
                    if (string.Equals(rows[j].RowKey, key, StringComparison.Ordinal))
                    {
                        existing = rows[j];
                        break;
                    }
                }
                if (existing == null)
                {
                    rows.Add(row);
                    added++;
                    continue;
                }
                merged++;
                if (row.Tips == null)
                    continue;
                for (int t = 0; t < row.Tips.Count; t++)
                {
                    bool known = false;
                    for (int k = 0; k < existing.Tips.Count; k++)
                    {
                        if (existing.Tips[k].SameAs(row.Tips[t]))
                        {
                            known = true;
                            break;
                        }
                    }
                    if (!known)
                        existing.Tips.Add(row.Tips[t]);
                }
            }
            return added;
        }

        /// <summary>
        /// Lowers (or creates) the watermark of the snapshot <paramref name="snapshot"/> names,
        /// found through <paramref name="isSameSnapshot"/>. The stored identity is refreshed to
        /// the tag given (an optimizer split moves a snapshot to a new recording id).
        /// </summary>
        internal static void LowerWatermark(
            RetiredRouteCargoTipTag snapshot, double cutoffUT, Func<RetiredRouteCargoTipTag, bool> isSameSnapshot)
        {
            if (snapshot == null)
                return;
            for (int i = 0; i < watermarks.Count; i++)
            {
                if (isSameSnapshot != null && isSameSnapshot(watermarks[i].Snapshot))
                {
                    watermarks[i].Snapshot = snapshot;
                    if (cutoffUT < watermarks[i].LowestCutoffUT)
                        watermarks[i].LowestCutoffUT = cutoffUT;
                    return;
                }
            }
            watermarks.Add(new RetiredRouteCargoWatermark { Snapshot = snapshot, LowestCutoffUT = cutoffUT });
        }

        /// <summary>The watermark of the snapshot <paramref name="isSameSnapshot"/> picks; +infinity when none.</summary>
        internal static double WatermarkOf(Func<RetiredRouteCargoTipTag, bool> isSameSnapshot)
        {
            for (int i = 0; i < watermarks.Count; i++)
            {
                if (isSameSnapshot != null && isSameSnapshot(watermarks[i].Snapshot))
                    return watermarks[i].LowestCutoffUT;
            }
            return double.PositiveInfinity;
        }

        internal static void Clear()
        {
            rows.Clear();
            watermarks.Clear();
        }

        internal static void ResetForTesting()
        {
            Clear();
        }

        /// <summary>Writes the stash as a child of the ledger file root; nothing while empty.</summary>
        internal static void SerializeInto(ConfigNode ledgerRoot)
        {
            if (ledgerRoot == null || (rows.Count == 0 && watermarks.Count == 0))
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
                if (row.Tips != null)
                {
                    for (int t = 0; t < row.Tips.Count; t++)
                        WriteTag(r.AddNode(TipNodeName), row.Tips[t]);
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
            for (int i = 0; i < watermarks.Count; i++)
            {
                ConfigNode w = node.AddNode(WatermarkNodeName);
                WriteTag(w, watermarks[i].Snapshot);
                w.AddValue("lowestCutoffUT", watermarks[i].LowestCutoffUT.ToString("R", IC));
            }
        }

        private static void WriteTag(ConfigNode n, RetiredRouteCargoTipTag tag)
        {
            if (tag == null)
                return;
            n.AddValue("tree", tag.TreeId ?? "");
            n.AddValue("rec", tag.RecordingId ?? "");
            if (!string.IsNullOrEmpty(tag.ChainId))
                n.AddValue("chain", tag.ChainId);
            n.AddValue("captureUT", tag.CaptureUT.ToString("R", IC));
            n.AddValue("fingerprint", tag.Fingerprint ?? "");
        }

        private static RetiredRouteCargoTipTag ReadTag(ConfigNode n)
        {
            if (n == null)
                return null;
            string tree = n.GetValue("tree");
            string rec = n.GetValue("rec");
            string fingerprint = n.GetValue("fingerprint");
            if (string.IsNullOrEmpty(tree) || string.IsNullOrEmpty(rec) || string.IsNullOrEmpty(fingerprint)
                || !TryParseDouble(n.GetValue("captureUT"), out double capture))
                return null;
            return new RetiredRouteCargoTipTag
            {
                TreeId = tree,
                RecordingId = rec,
                ChainId = n.GetValue("chain"),
                CaptureUT = capture,
                Fingerprint = fingerprint
            };
        }

        /// <summary>
        /// Replaces the stash with the ledger root's child (none: empty). Returns the number of
        /// unreadable entries dropped.
        /// </summary>
        internal static int LoadFrom(ConfigNode ledgerRoot)
        {
            Clear();
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
            ConfigNode[] markNodes = node.GetNodes(WatermarkNodeName);
            for (int i = 0; i < markNodes.Length; i++)
            {
                RetiredRouteCargoTipTag tag = ReadTag(markNodes[i]);
                string lowest = markNodes[i].GetValue("lowestCutoffUT");
                double cutoff;
                if (tag == null || !double.TryParse(lowest, NumberStyles.Float, IC, out cutoff) || double.IsNaN(cutoff))
                {
                    malformed++;
                    continue;
                }
                watermarks.Add(new RetiredRouteCargoWatermark { Snapshot = tag, LowestCutoffUT = cutoff });
            }
            if (malformed > 0)
                ParsekLog.Warn(Tag,
                    "Retired route cargo: dropped " + malformed.ToString(IC)
                    + " unreadable entries on load, kept rows=" + rows.Count.ToString(IC)
                    + " watermarks=" + watermarks.Count.ToString(IC));
            else
                ParsekLog.Verbose(Tag,
                    "Retired route cargo loaded: rows=" + rows.Count.ToString(IC)
                    + " watermarks=" + watermarks.Count.ToString(IC));
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
            ConfigNode[] tipNodes = r.GetNodes(TipNodeName);
            for (int t = 0; t < tipNodes.Length; t++)
            {
                RetiredRouteCargoTipTag tag = ReadTag(tipNodes[t]);
                if (tag != null)
                    row.Tips.Add(tag);
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
            if (row.Resources == null || row.Tips.Count == 0)
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
