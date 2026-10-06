using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>One RESOURCE entry of a vessel snapshot, as the cargo adjustment reads it.</summary>
    internal struct SnapshotTank
    {
        internal uint PartPersistentId;
        internal string Resource;
        internal double Amount;
        internal double MaxAmount;
    }

    /// <summary>Who a chain tip's snapshot is: the vessels whose route cargo it carries.</summary>
    internal sealed class ChainTipCargoIdentity
    {
        internal string TipRecordingId;
        internal string TipTreeId;
        /// <summary>When the snapshot was captured (the tip recording's end).</summary>
        internal double CaptureUT;
        /// <summary>Every claimed pid ending in the tip plus the tip's own, each with the launch guid it must not conclusively differ from (null = pid only).</summary>
        internal List<KeyValuePair<uint, string>> Vessels = new List<KeyValuePair<uint, string>>();
        /// <summary>The claimed vessel's own part persistentIds (taken from first); null when unknown.</summary>
        internal HashSet<uint> EndpointPartIds;
    }

    /// <summary>What one route moved of one resource in an adjustment.</summary>
    internal struct ChainTipCargoEntry
    {
        internal string RouteId;
        internal string Resource;
        internal double Removed;
        internal double Added;
        internal double Clamped;
    }

    /// <summary>The result of <see cref="ChainTipRouteCargo.ComputeAdjustment"/>.</summary>
    internal sealed class ChainTipCargoAdjustment
    {
        /// <summary>The new amount of each tank, in the order given.</summary>
        internal double[] Amounts;
        internal List<ChainTipCargoEntry> Entries = new List<ChainTipCargoEntry>();
        internal int RowsApplied;
        internal int SkippedOtherTree;
        internal int SkippedOtherVessel;
        internal int SkippedNotAfterCutoff;
        internal int SkippedAfterCapture;
        internal int SkippedReplayed;
        internal int SkippedNoResources;
        internal bool Changed;
    }

    /// <summary>
    /// CHAIN-TIP-SNAPSHOT-CARRIES-UNPAID-ROUTE-CARGO (owner ruling 2026-10-07: subtract from
    /// the snapshot). A chain tip spawns from a snapshot captured when its mission was
    /// committed, so it holds every route delivery into the claimed station up to then. A
    /// rewind to before the claim retires and refunds the route rows after the cutoff (the
    /// reverted save restores the origin), and the replayed crossings into the station are
    /// blocked while it is held back, so those deliveries never happen again: the spawned
    /// station would hold cargo nobody paid for. The rewind keeps the retired crossings that
    /// a committed tip snapshot carries (<see cref="RetiredRouteCargoStore"/>, tagged with the
    /// tip's tree), and each spawn copy of that tip takes them back out: deliveries removed,
    /// pickups and origin debits (cargo the route took from the station) added back, per tank
    /// clamped at zero and at capacity, the claimed vessel's own parts first. The committed
    /// snapshot is never touched.
    ///
    /// <para>The tree tag carries the timeline: a crossing retired before the tip's mission
    /// was committed is in no snapshot of it (the snapshot was captured after that rewind),
    /// and the retire only tags trees committed at that moment. A crossing the current
    /// timeline performed again (its row is back in the effective ledger, e.g. a route into
    /// the pre-claim station while it stood live at the Space Center) is paid again and the
    /// snapshot's copy of it is the one that survives the replacement, so it is left in.</para>
    ///
    /// <para>Inventory (stored parts) is not adjusted: delivery rows carry no inventory
    /// manifest, and a pickup's stored parts are not put back.</para>
    /// </summary>
    internal static class ChainTipRouteCargo
    {
        private const string Tag = "ChainTipCargo";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const double Epsilon = 1e-9;

        // ------------------------------------------------------------------
        // Pure decisions
        // ------------------------------------------------------------------

        /// <summary>
        /// Pure: does the row name one of the tip's vessels? The route endpoint's pid or the
        /// writer-resolved pid (pickups and debits) must be one of the tip's pids, and the
        /// endpoint's launch guid must not conclusively differ from that vessel's (an unknown
        /// guid on either side is no evidence, the <see cref="VesselLaunchIdentity"/> rule).
        /// </summary>
        internal static bool RowAddressesTip(RetiredRouteCargoRow row, ChainTipCargoIdentity tip)
        {
            if (row == null || tip == null || tip.Vessels == null)
                return false;
            for (int i = 0; i < tip.Vessels.Count; i++)
            {
                uint pid = tip.Vessels[i].Key;
                if (pid == 0u)
                    continue;
                bool pidMatch = row.EndpointPid == pid
                    || (row.ActualVesselPid != 0u && row.ActualVesselPid == pid);
                if (!pidMatch)
                    continue;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(row.EndpointGuid, tip.Vessels[i].Value))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pure: the spawn copy's new tank amounts after taking out every stashed crossing that
        /// this tip's snapshot carries and the current timeline has not performed again: the
        /// row is tagged with the tip's tree, names one of its vessels, lies after its own
        /// rewind cutoff and at or before the snapshot capture, and its crossing key is not in
        /// <paramref name="replayedCrossingKeys"/>. Rows are undone latest first; a delivery is
        /// removed, a pickup or debit added back, each resource from the claimed vessel's own
        /// parts first, every tank clamped at zero and at its capacity. What could not be
        /// removed or added is reported as clamped.
        /// </summary>
        internal static ChainTipCargoAdjustment ComputeAdjustment(
            IList<SnapshotTank> tanks,
            IList<RetiredRouteCargoRow> rows,
            ChainTipCargoIdentity tip,
            ICollection<string> replayedCrossingKeys)
        {
            var result = new ChainTipCargoAdjustment();
            int n = tanks != null ? tanks.Count : 0;
            result.Amounts = new double[n];
            for (int i = 0; i < n; i++)
                result.Amounts[i] = tanks[i].Amount;
            if (tip == null || rows == null)
                return result;

            var selected = new List<KeyValuePair<int, RetiredRouteCargoRow>>();
            for (int r = 0; r < rows.Count; r++)
            {
                RetiredRouteCargoRow row = rows[r];
                if (row == null)
                    continue;
                if (string.IsNullOrEmpty(tip.TipTreeId) || row.TipTreeIds == null
                    || !row.TipTreeIds.Contains(tip.TipTreeId))
                {
                    result.SkippedOtherTree++;
                    continue;
                }
                if (!RowAddressesTip(row, tip))
                {
                    result.SkippedOtherVessel++;
                    continue;
                }
                if (!(row.UT > row.CutoffUT))
                {
                    result.SkippedNotAfterCutoff++;
                    continue;
                }
                if (row.UT > tip.CaptureUT)
                {
                    result.SkippedAfterCapture++;
                    continue;
                }
                if (replayedCrossingKeys != null && replayedCrossingKeys.Contains(row.CrossingKey))
                {
                    result.SkippedReplayed++;
                    continue;
                }
                if (row.Resources == null || row.Resources.Count == 0)
                {
                    result.SkippedNoResources++;
                    continue;
                }
                selected.Add(new KeyValuePair<int, RetiredRouteCargoRow>(r, row));
            }

            // Undo latest first; equal UTs keep the stash order reversed.
            selected.Sort((a, b) =>
            {
                int c = b.Value.UT.CompareTo(a.Value.UT);
                return c != 0 ? c : b.Key.CompareTo(a.Key);
            });

            var entryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int s = 0; s < selected.Count; s++)
            {
                RetiredRouteCargoRow row = selected[s].Value;
                result.RowsApplied++;
                var names = new List<string>(row.Resources.Keys);
                names.Sort(StringComparer.Ordinal);
                for (int k = 0; k < names.Count; k++)
                {
                    double amount = row.Resources[names[k]];
                    if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0.0)
                        continue;
                    double moved = row.TookFromVessel
                        ? Put(tanks, result.Amounts, names[k], amount, tip.EndpointPartIds)
                        : Take(tanks, result.Amounts, names[k], amount, tip.EndpointPartIds);

                    string key = (row.RouteId ?? "") + "|" + names[k];
                    int idx;
                    if (!entryIndex.TryGetValue(key, out idx))
                    {
                        idx = result.Entries.Count;
                        entryIndex[key] = idx;
                        result.Entries.Add(new ChainTipCargoEntry { RouteId = row.RouteId, Resource = names[k] });
                    }
                    ChainTipCargoEntry e = result.Entries[idx];
                    if (row.TookFromVessel)
                        e.Added += moved;
                    else
                        e.Removed += moved;
                    double clamped = amount - moved;
                    if (clamped > Epsilon)
                        e.Clamped += clamped;
                    result.Entries[idx] = e;
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(result.Amounts[i] - tanks[i].Amount) > Epsilon)
                {
                    result.Changed = true;
                    break;
                }
            }
            return result;
        }

        private static bool IsOwnPart(SnapshotTank tank, HashSet<uint> ownParts)
        {
            return ownParts != null && ownParts.Contains(tank.PartPersistentId);
        }

        /// <summary>Removes up to <paramref name="amount"/>, own parts first; returns what was removed.</summary>
        private static double Take(IList<SnapshotTank> tanks, double[] amounts, string resource,
            double amount, HashSet<uint> ownParts)
        {
            double remaining = amount;
            bool hasOwn = ownParts != null && ownParts.Count > 0;
            for (int pass = 0; pass < (hasOwn ? 2 : 1) && remaining > Epsilon; pass++)
            {
                for (int i = 0; i < tanks.Count && remaining > Epsilon; i++)
                {
                    if (!string.Equals(tanks[i].Resource, resource, StringComparison.Ordinal))
                        continue;
                    if (hasOwn && IsOwnPart(tanks[i], ownParts) != (pass == 0))
                        continue;
                    double take = Math.Min(Math.Max(0.0, amounts[i]), remaining);
                    if (take <= 0.0)
                        continue;
                    amounts[i] = Math.Max(0.0, amounts[i] - take);
                    remaining -= take;
                }
            }
            return amount - Math.Max(0.0, remaining);
        }

        /// <summary>Adds up to <paramref name="amount"/>, own parts first, each capped at capacity; returns what was added.</summary>
        private static double Put(IList<SnapshotTank> tanks, double[] amounts, string resource,
            double amount, HashSet<uint> ownParts)
        {
            double remaining = amount;
            bool hasOwn = ownParts != null && ownParts.Count > 0;
            for (int pass = 0; pass < (hasOwn ? 2 : 1) && remaining > Epsilon; pass++)
            {
                for (int i = 0; i < tanks.Count && remaining > Epsilon; i++)
                {
                    if (!string.Equals(tanks[i].Resource, resource, StringComparison.Ordinal))
                        continue;
                    if (hasOwn && IsOwnPart(tanks[i], ownParts) != (pass == 0))
                        continue;
                    double room = Math.Max(0.0, tanks[i].MaxAmount - amounts[i]);
                    double put = Math.Min(room, remaining);
                    if (put <= 0.0)
                        continue;
                    amounts[i] += put;
                    remaining -= put;
                }
            }
            return amount - Math.Max(0.0, remaining);
        }

        /// <summary>
        /// Pure: the rows a retire should keep, each tagged with the trees whose chain tip
        /// snapshot carries it (the row names the tip's vessel, lies after its cutoff and at or
        /// before the capture). Rows no tip carries are dropped.
        /// </summary>
        internal static List<RetiredRouteCargoRow> TagForChainTips(
            IList<RetiredRouteCargoRow> candidates, IList<ChainTipCargoIdentity> tips)
        {
            var kept = new List<RetiredRouteCargoRow>();
            if (candidates == null || tips == null)
                return kept;
            for (int r = 0; r < candidates.Count; r++)
            {
                RetiredRouteCargoRow row = candidates[r];
                if (row == null)
                    continue;
                if (row.TipTreeIds == null)
                    row.TipTreeIds = new List<string>();
                for (int t = 0; t < tips.Count; t++)
                {
                    ChainTipCargoIdentity tip = tips[t];
                    if (tip == null || string.IsNullOrEmpty(tip.TipTreeId))
                        continue;
                    if (!(row.UT > row.CutoffUT) || row.UT > tip.CaptureUT)
                        continue;
                    if (!RowAddressesTip(row, tip))
                        continue;
                    if (!row.TipTreeIds.Contains(tip.TipTreeId))
                        row.TipTreeIds.Add(tip.TipTreeId);
                }
                if (row.TipTreeIds.Count > 0)
                    kept.Add(row);
            }
            return kept;
        }

        // ------------------------------------------------------------------
        // Chain tip identity (reads committed trees, no live KSP)
        // ------------------------------------------------------------------

        /// <summary>
        /// The cargo identity of <paramref name="tip"/> when it is the tip of a non-terminated
        /// ghost chain, else null: every claimed pid ending in it with its expected launch guid
        /// (<see cref="ChainTipStaleVessel.ExpectedClaimedGuid"/>, the replacement's rule), the
        /// tip's own pid and guid, and the claimed vessels' parts at their claims
        /// (<see cref="GhostChainWalker.ResolveClaimedPartIds"/>).
        /// </summary>
        internal static ChainTipCargoIdentity BuildTipIdentity(
            Recording tip, Dictionary<uint, GhostChain> chains, IList<RecordingTree> trees)
        {
            if (tip == null || string.IsNullOrEmpty(tip.RecordingId))
                return null;
            List<GhostChain> tipChains = ChainTipStaleVessel.FindChainsForTip(chains, tip.RecordingId);
            if (tipChains.Count == 0 || tipChains[0].IsTerminated)
                return null;

            var id = new ChainTipCargoIdentity
            {
                TipRecordingId = tip.RecordingId,
                TipTreeId = !string.IsNullOrEmpty(tipChains[0].TipTreeId) ? tipChains[0].TipTreeId : tip.TreeId,
                CaptureUT = tip.EndUT
            };

            var claimed = new List<uint>(ChainTipStaleVessel.ResolveClaimedPidsForTip(chains, tip.RecordingId, trees));
            claimed.Sort();
            for (int i = 0; i < claimed.Count; i++)
            {
                GhostChain keyed = null;
                for (int c = 0; c < tipChains.Count; c++)
                {
                    if (tipChains[c].OriginalVesselPid == claimed[i])
                    {
                        keyed = tipChains[c];
                        break;
                    }
                }
                id.Vessels.Add(new KeyValuePair<uint, string>(
                    claimed[i], ChainTipStaleVessel.ExpectedClaimedGuid(tip, keyed, claimed[i])));
            }
            if (tip.VesselPersistentId != 0u && !claimed.Contains(tip.VesselPersistentId))
                id.Vessels.Add(new KeyValuePair<uint, string>(tip.VesselPersistentId, tip.RecordedVesselGuid));

            var parts = new HashSet<uint>();
            for (int c = 0; c < tipChains.Count; c++)
            {
                List<ChainLink> links = tipChains[c].Links;
                if (links == null)
                    continue;
                for (int l = 0; l < links.Count; l++)
                {
                    RecordingTree tree = FindTree(trees, links[l].treeId);
                    if (tree == null || tree.Recordings == null)
                        continue;
                    uint linkPid = ChainTipStaleVessel.ResolveLinkClaimedPid(links[l], trees);
                    if (linkPid == 0u)
                        linkPid = tipChains[c].OriginalVesselPid;
                    Recording start;
                    tree.Recordings.TryGetValue(links[l].recordingId ?? "", out start);
                    HashSet<uint> claimedParts = GhostChainWalker.ResolveClaimedPartIds(
                        start, tree, linkPid, links[l].branchPointId, out _);
                    if (claimedParts != null)
                        parts.UnionWith(claimedParts);
                }
            }
            id.EndpointPartIds = parts.Count > 0 ? parts : null;
            return id;
        }

        /// <summary>The cargo identity of every non-terminated chain tip, one per tip recording.</summary>
        internal static List<ChainTipCargoIdentity> BuildTipIdentities(
            Dictionary<uint, GhostChain> chains, IList<RecordingTree> trees)
        {
            var tips = new List<ChainTipCargoIdentity>();
            if (chains == null || chains.Count == 0)
                return tips;
            var keys = new List<uint>(chains.Keys);
            keys.Sort();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < keys.Count; k++)
            {
                GhostChain chain = chains[keys[k]];
                if (chain == null || chain.IsTerminated || string.IsNullOrEmpty(chain.TipRecordingId))
                    continue;
                if (!seen.Add(chain.TipRecordingId))
                    continue;
                Recording tip = FindRecording(trees, chain.TipTreeId, chain.TipRecordingId);
                ChainTipCargoIdentity id = BuildTipIdentity(tip, chains, trees);
                if (id != null)
                    tips.Add(id);
            }
            return tips;
        }

        private static RecordingTree FindTree(IList<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int t = 0; t < trees.Count; t++)
            {
                if (trees[t] != null && string.Equals(trees[t].Id, treeId, StringComparison.Ordinal))
                    return trees[t];
            }
            return null;
        }

        private static Recording FindRecording(IList<RecordingTree> trees, string treeId, string recordingId)
        {
            if (trees == null || string.IsNullOrEmpty(recordingId))
                return null;
            Recording rec;
            RecordingTree tree = FindTree(trees, treeId);
            if (tree != null && tree.Recordings != null && tree.Recordings.TryGetValue(recordingId, out rec))
                return rec;
            for (int t = 0; t < trees.Count; t++)
            {
                if (trees[t] != null && trees[t].Recordings != null
                    && trees[t].Recordings.TryGetValue(recordingId, out rec))
                    return rec;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Snapshot tanks
        // ------------------------------------------------------------------

        /// <summary>Reads every PART/RESOURCE entry of a vessel snapshot, in order, with its node.</summary>
        internal static List<SnapshotTank> ReadTanks(ConfigNode vessel, List<ConfigNode> resourceNodes)
        {
            var tanks = new List<SnapshotTank>();
            if (vessel == null)
                return tanks;
            foreach (ConfigNode part in vessel.GetNodes("PART"))
            {
                uint partPid;
                uint.TryParse(part.GetValue("persistentId"), NumberStyles.Integer, IC, out partPid);
                foreach (ConfigNode res in part.GetNodes("RESOURCE"))
                {
                    string name = res.GetValue("name");
                    if (string.IsNullOrEmpty(name)
                        || !double.TryParse(res.GetValue("amount"), NumberStyles.Float, IC, out double amount)
                        || double.IsNaN(amount) || double.IsInfinity(amount))
                        continue;
                    double max;
                    if (!double.TryParse(res.GetValue("maxAmount"), NumberStyles.Float, IC, out max)
                        || double.IsNaN(max) || double.IsInfinity(max))
                        max = amount;
                    tanks.Add(new SnapshotTank
                    {
                        PartPersistentId = partPid,
                        Resource = name,
                        Amount = amount,
                        MaxAmount = max
                    });
                    resourceNodes?.Add(res);
                }
            }
            return tanks;
        }

        // ------------------------------------------------------------------
        // Live: the retire capture and the spawn-copy hook
        // ------------------------------------------------------------------

        /// <summary>
        /// Called by the ledger retire (go-back rewind, in-session load back in time) and the
        /// Re-Fly restore with the rows they just removed: stashes the cargo rows that a
        /// committed chain tip snapshot carries, tagged with the tip's tree. The chains are
        /// walked over the trees committed now, before the rewind's future is replayed. Never
        /// throws. Returns how many rows were stashed.
        /// </summary>
        internal static int CaptureRetiredRouteCargo(
            IReadOnlyList<GameAction> retired,
            double cutoffUT,
            IEnumerable<Route> committedRoutes,
            IEnumerable<Route> dormantRoutes,
            string site)
        {
            if (retired == null || retired.Count == 0)
                return 0;
            try
            {
                var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
                AddRoutes(routes, committedRoutes);
                AddRoutes(routes, dormantRoutes);

                var candidates = new List<RetiredRouteCargoRow>();
                int cargoRows = 0, noResource = 0, noRoute = 0;
                for (int i = 0; i < retired.Count; i++)
                {
                    GameAction a = retired[i];
                    if (a == null || !RetiredRouteCargoStore.IsCargoRowType(a.Type))
                        continue;
                    cargoRows++;
                    Route route;
                    routes.TryGetValue(a.RouteId ?? "", out route);
                    RetiredRouteCargoRow row = RetiredRouteCargoStore.BuildRow(a, cutoffUT, route);
                    if (row == null)
                    {
                        noResource++;
                        continue;
                    }
                    if (route == null)
                        noRoute++;
                    candidates.Add(row);
                }

                string cutoff = cutoffUT.ToString("R", IC);
                if (candidates.Count == 0)
                {
                    ParsekLog.Verbose(Tag,
                        "Retired route cargo (" + (site ?? "?") + "): nothing to keep cutoffUT=" + cutoff
                        + " retiredRows=" + retired.Count.ToString(IC)
                        + " cargoRows=" + cargoRows.ToString(IC)
                        + " noResource=" + noResource.ToString(IC));
                    return 0;
                }

                List<RecordingTree> trees = RecordingStore.CommittedTrees;
                List<ChainTipCargoIdentity> tips = trees != null && trees.Count > 0
                    ? BuildTipIdentities(GhostChainWalker.ComputeAllGhostChains(trees, 0.0), trees)
                    : new List<ChainTipCargoIdentity>();
                List<RetiredRouteCargoRow> tagged = TagForChainTips(candidates, tips);
                int added = RetiredRouteCargoStore.Merge(tagged, out int replaced);

                ParsekLog.Info(Tag,
                    "Retired route cargo kept for chain tips (" + (site ?? "?") + "): cutoffUT=" + cutoff
                    + " retiredRows=" + retired.Count.ToString(IC)
                    + " cargoRows=" + cargoRows.ToString(IC)
                    + " stashed=" + tagged.Count.ToString(IC)
                    + " (new=" + added.ToString(IC) + " replaced=" + replaced.ToString(IC) + ")"
                    + " carriedByNoTip=" + (candidates.Count - tagged.Count).ToString(IC)
                    + " noResource=" + noResource.ToString(IC)
                    + " routeNotFound=" + noRoute.ToString(IC)
                    + " chainTips=" + tips.Count.ToString(IC)
                    + " total=" + RetiredRouteCargoStore.Rows.Count.ToString(IC));
                return tagged.Count;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    "Retired route cargo capture (" + (site ?? "?") + ") threw " + ex.GetType().Name
                    + ": " + ex.Message + " - nothing kept, a chain tip spawned later carries the refunded cargo");
                return 0;
            }
        }

        private static void AddRoutes(Dictionary<string, Route> routes, IEnumerable<Route> source)
        {
            if (source == null)
                return;
            foreach (Route r in source)
            {
                if (r != null && !string.IsNullOrEmpty(r.Id) && !routes.ContainsKey(r.Id))
                    routes[r.Id] = r;
            }
        }

        /// <summary>
        /// Takes the stashed retired route cargo out of a chain tip's spawn copy. Every site
        /// that turns a recording's stored snapshot into a spawn copy calls it on the copy
        /// (never on the stored snapshot), so each spawn attempt starts again from the
        /// recorded state. A recording that is not a chain tip, or that no stashed crossing
        /// names, is left as it is. Never throws.
        /// </summary>
        internal static void ApplyToSpawnCopy(ConfigNode spawnCopy, Recording rec, string site)
        {
            if (spawnCopy == null || rec == null)
                return;
            IReadOnlyList<RetiredRouteCargoRow> stash = RetiredRouteCargoStore.Rows;
            if (stash.Count == 0)
                return;
            string siteLabel = string.IsNullOrEmpty(site) ? "?" : site;

            if (!string.IsNullOrEmpty(rec.TreeId) && !AnyRowTaggedFor(stash, rec.TreeId))
            {
                ParsekLog.Verbose(Tag,
                    "Spawn copy kept as recorded (" + siteLabel + "): rec=" + (rec.RecordingId ?? "(null)")
                    + " tree=" + rec.TreeId + " - no retired route cargo for this tree"
                    + " (stash=" + stash.Count.ToString(IC) + ")");
                return;
            }

            try
            {
                List<RecordingTree> trees = RecordingStore.CommittedTrees;
                Dictionary<uint, GhostChain> chains = GhostChainWalker.ComputeAllGhostChains(trees, 0.0);
                ChainTipCargoIdentity tip = BuildTipIdentity(rec, chains, trees);
                if (tip == null)
                {
                    ParsekLog.Verbose(Tag,
                        "Spawn copy kept as recorded (" + siteLabel + "): rec=" + (rec.RecordingId ?? "(null)")
                        + " is not the tip of a live ghost chain");
                    return;
                }

                HashSet<string> replayed = CollectReplayedCrossingKeys();
                var resourceNodes = new List<ConfigNode>();
                List<SnapshotTank> tanks = ReadTanks(spawnCopy, resourceNodes);
                var rows = new List<RetiredRouteCargoRow>(stash);
                ChainTipCargoAdjustment adj = ComputeAdjustment(tanks, rows, tip, replayed);

                int tanksChanged = 0;
                for (int i = 0; i < tanks.Count; i++)
                {
                    if (Math.Abs(adj.Amounts[i] - tanks[i].Amount) <= Epsilon)
                        continue;
                    resourceNodes[i].SetValue("amount", adj.Amounts[i].ToString("R", IC));
                    tanksChanged++;
                }

                string skipped = " skipped(otherTree=" + adj.SkippedOtherTree.ToString(IC)
                    + " otherVessel=" + adj.SkippedOtherVessel.ToString(IC)
                    + " notAfterCutoff=" + adj.SkippedNotAfterCutoff.ToString(IC)
                    + " afterCapture=" + adj.SkippedAfterCapture.ToString(IC)
                    + " replayed=" + adj.SkippedReplayed.ToString(IC)
                    + " noResources=" + adj.SkippedNoResources.ToString(IC) + ")";
                if (adj.RowsApplied == 0)
                {
                    ParsekLog.Verbose(Tag,
                        "Spawn copy kept as recorded (" + siteLabel + "): chain tip rec=" + tip.TipRecordingId
                        + " tree=" + (tip.TipTreeId ?? "(null)")
                        + " captureUT=" + tip.CaptureUT.ToString("R", IC)
                        + " - no retired route crossing in it" + skipped);
                    return;
                }

                ParsekLog.Info(Tag,
                    "Retired route cargo taken out of the chain tip spawn copy (" + siteLabel + "): rec="
                    + tip.TipRecordingId + " vessel=\"" + (rec.VesselName ?? "(null)") + "\""
                    + " tree=" + (tip.TipTreeId ?? "(null)")
                    + " captureUT=" + tip.CaptureUT.ToString("R", IC)
                    + " rows=" + adj.RowsApplied.ToString(IC)
                    + " tanksChanged=" + tanksChanged.ToString(IC)
                    + " ownParts=" + (tip.EndpointPartIds != null ? tip.EndpointPartIds.Count : 0).ToString(IC)
                    + " [" + FormatEntries(adj.Entries) + "]" + skipped
                    + " - the recorded snapshot is unchanged");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    "Retired route cargo adjustment (" + siteLabel + ") threw " + ex.GetType().Name
                    + ": " + ex.Message + " - rec=" + (rec.RecordingId ?? "(null)")
                    + " spawns with the recorded cargo");
            }
        }

        private static bool AnyRowTaggedFor(IReadOnlyList<RetiredRouteCargoRow> stash, string treeId)
        {
            for (int i = 0; i < stash.Count; i++)
            {
                if (stash[i] != null && stash[i].TipTreeIds != null && stash[i].TipTreeIds.Contains(treeId))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The crossing keys of every cargo-moving route row in the effective ledger: crossings
        /// the current timeline performed (and paid) again.
        /// </summary>
        private static HashSet<string> CollectReplayedCrossingKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    "ComputeELS threw " + ex.GetType().Name + ": " + ex.Message
                    + " - treating no retired crossing as replayed");
                return keys;
            }
            if (els == null)
                return keys;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null || !RetiredRouteCargoStore.IsCargoRowType(a.Type)
                    || !RouteLedgerRetire.IsPhysicalRouteMutation(a))
                    continue;
                keys.Add(RetiredRouteCargoStore.CrossingKey(a));
            }
            return keys;
        }

        private static string FormatEntries(List<ChainTipCargoEntry> entries)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                    sb.Append("; ");
                ChainTipCargoEntry e = entries[i];
                sb.Append("route=").Append(e.RouteId ?? "(null)")
                    .Append(" res=").Append(e.Resource ?? "(null)")
                    .Append(" removed=").Append(e.Removed.ToString("0.###", IC))
                    .Append(" added=").Append(e.Added.ToString("0.###", IC))
                    .Append(" clamped=").Append(e.Clamped.ToString("0.###", IC));
            }
            return sb.ToString();
        }
    }
}
