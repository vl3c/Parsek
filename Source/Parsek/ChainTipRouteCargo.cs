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

    /// <summary>A cargo-moving route row of the current effective ledger, as the replay match reads it.</summary>
    internal struct ReplayedCrossing
    {
        internal string RouteId;
        internal int StopIndex;
        internal GameActionType Type;
        internal double UT;
    }

    /// <summary>A vessel named only by a POSITIVE launch identity: pid plus a launch guid or root part flightID that matches.</summary>
    internal struct CargoVesselIdentity
    {
        internal uint Pid;
        internal string LaunchGuid;
        internal uint RootPartUId;
    }

    /// <summary>
    /// Who an end-of-recording snapshot is (a chain tip's, or an ordinary leaf's): which
    /// snapshot, and the vessels whose route cargo it carries.
    /// </summary>
    internal sealed class ChainTipCargoIdentity
    {
        /// <summary>
        /// True for a ghost chain tip; false for an ordinary end-of-recording spawn whose own
        /// recorded vessel is the route endpoint (<see cref="PositiveVessels"/>).
        /// </summary>
        internal bool IsChainTip = true;
        /// <summary>
        /// Vessels a row must name POSITIVELY (pid plus a matching launch guid or root part
        /// flightID, never a bare pid): an ordinary leaf's own recorded vessel.
        /// </summary>
        internal List<CargoVesselIdentity> PositiveVessels = new List<CargoVesselIdentity>();
        internal string TipRecordingId;
        internal string TipTreeId;
        /// <summary>
        /// When the snapshot was captured: the recording's end, or its end before a tail trim
        /// (<see cref="ChainTipRouteCargo.SnapshotCaptureUT"/>); a tag keeps the capture time it
        /// was tagged with.
        /// </summary>
        internal double CaptureUT;
        /// <summary><see cref="ChainTipRouteCargo.SnapshotFingerprint(ConfigNode)"/> of the tip's stored snapshot; null without one.</summary>
        internal string Fingerprint;
        /// <summary>The tip recording's optimizer chain id; null when it has none.</summary>
        internal string ChainId;
        /// <summary>The tip's own id and the earlier segments of its optimizer chain (an optimizer split moves the snapshot to the later half).</summary>
        internal HashSet<string> AcceptedRecordingIds = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Every recording id in the tip's tree (a tagged id missing from it was merged away by the optimizer); null when unknown.</summary>
        internal HashSet<string> TreeRecordingIds;
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
        internal int SkippedOtherSnapshot;
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
    /// station would hold cargo nobody paid for. The rewind keeps the retired crossings that a
    /// committed tip snapshot carries (<see cref="RetiredRouteCargoStore"/>), and a spawn copy
    /// of that SAME snapshot takes them back out: deliveries removed, pickups and origin debits
    /// (cargo the route took from the station) added back, per tank clamped at zero and at
    /// capacity, the claimed vessel's own parts first. The committed snapshot is never touched.
    ///
    /// <para><b>Which snapshot.</b> A retired row is tagged with the tip snapshots that carry
    /// it: the recording holding the snapshot, its optimizer chain, its capture time and a
    /// fingerprint of its resource content (<see cref="RetiredRouteCargoTipTag"/>). A later tip
    /// of the same tree (a switch continuation flown on from the spawned station, a Re-Fly
    /// fork) is another recording with its own snapshot and gets none of those rows; an
    /// optimizer split that moves the snapshot to its later half is followed through the chain.
    /// A row is tagged only when it is no later than the snapshot's capture and no later than
    /// the lowest cutoff of every earlier retire since that snapshot was first seen (its
    /// watermark): a row above that was created in a timeline that branched after the capture,
    /// so the snapshot cannot hold it.</para>
    ///
    /// <para><b>Replays.</b> A crossing the current timeline performed again (its row is back
    /// in the effective ledger, for example into the pre-claim station standing live at the
    /// Space Center) is paid again, and the snapshot's copy is the one that survives the
    /// replacement, so it is left in. A replay is matched to a stashed crossing by route, stop,
    /// row type and the nearest UT inside the snapshot's window, one to one, never by cycle id:
    /// the counters behind cycle ids are rebuilt from the kept rows at every rewind, and a
    /// blocked crossing writes no dispatch row, so a replay can carry another id.</para>
    ///
    /// <para><b>Every end-of-recording spawn (owner ruling 2026-10-07,
    /// END-SPAWN-SNAPSHOT-CARRIES-UNPAID-ROUTE-CARGO).</b> The same holds for an ordinary leaf
    /// whose own recorded vessel is the route endpoint (the route-fed vessel's own recording):
    /// its commit-time snapshot holds the crossings made while it recorded. Such a leaf is
    /// tagged and adjusted the same way, its vessel named positively (pid plus a matching
    /// launch guid or root part flightID, never a bare pid, <see cref="IsPositiveEndpointMatch"/>).
    /// One identity per spawn (<see cref="BuildSpawnIdentity"/>): a chain tip is never also
    /// taken as a leaf.</para>
    ///
    /// <para><b>Trimmed tails.</b> The optimizer's tail trim moves a recording's end and spawn
    /// earlier and keeps the snapshot captured at the old end, so the crossings of the trimmed
    /// tail are in the snapshot and are replayed live after the early spawn. The trim notes
    /// the old end (<see cref="NoteTrimmedSnapshotCapture"/>), and the capture UT of a tag is
    /// that moment (<see cref="SnapshotCaptureUT"/>): those crossings are taken out of the
    /// spawn copy and the live replay delivers them, each paid once.</para>
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
            if (row == null || tip == null)
                return false;
            if (tip.Vessels != null)
            {
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
            }
            if (tip.PositiveVessels != null)
            {
                for (int i = 0; i < tip.PositiveVessels.Count; i++)
                {
                    if (IsPositiveEndpointMatch(
                            row.EndpointPid, row.EndpointGuid, row.EndpointRootPartUId, tip.PositiveVessels[i]))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Pure: is the route endpoint POSITIVELY <paramref name="vessel"/>? The pids are equal
        /// and nonzero, and then the launch guids decide when both are known (equal: yes,
        /// different: another launch of the same craft); otherwise both root part flightIDs
        /// must be known and equal. A bare pid match is never enough: a persistentId is baked
        /// into the craft file and reused on every launch of it (the non-degrading sibling of
        /// the <see cref="VesselLaunchIdentity"/> rule, for a site whose behaviour before was to
        /// leave the vessel alone).
        /// </summary>
        internal static bool IsPositiveEndpointMatch(
            uint endpointPid, string endpointGuid, uint endpointRootPartUId, CargoVesselIdentity vessel)
        {
            if (endpointPid == 0u || vessel.Pid == 0u || endpointPid != vessel.Pid)
                return false;
            string endpoint = VesselLaunchIdentity.NormalizeGuid(endpointGuid);
            string own = VesselLaunchIdentity.NormalizeGuid(vessel.LaunchGuid);
            if (endpoint != null && own != null)
                return string.Equals(endpoint, own, StringComparison.OrdinalIgnoreCase);
            return endpointRootPartUId != 0u && vessel.RootPartUId != 0u
                && endpointRootPartUId == vessel.RootPartUId;
        }

        /// <summary>
        /// When a recording's end snapshot was captured: the recording's end, or the end it had
        /// before the optimizer trimmed its tail when that was noted
        /// (<see cref="RetiredRouteCargoStore.CaptureUTOf"/>), whichever is later. The trim moves
        /// the end (and so the spawn) earlier and keeps the snapshot, so the crossings of the
        /// trimmed tail are in the snapshot and are replayed live after the early spawn.
        /// </summary>
        internal static double SnapshotCaptureUT(double recordingEndUT, string treeId, string fingerprint)
        {
            double noted = RetiredRouteCargoStore.CaptureUTOf(treeId, fingerprint);
            return !double.IsNaN(noted) && noted > recordingEndUT ? noted : recordingEndUT;
        }

        /// <summary>
        /// Pure: does <paramref name="tag"/> name the snapshot <paramref name="tip"/> spawns
        /// from? Same tree and the same resource fingerprint, and the tagged recording is the
        /// tip, an earlier segment of its optimizer chain (a split moved the snapshot on), or a
        /// recording of its chain the optimizer has since merged away.
        /// </summary>
        internal static bool TagMatchesTip(RetiredRouteCargoTipTag tag, ChainTipCargoIdentity tip)
        {
            if (tag == null || tip == null)
                return false;
            if (!string.Equals(tag.TreeId, tip.TipTreeId, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrEmpty(tag.Fingerprint)
                || !string.Equals(tag.Fingerprint, tip.Fingerprint, StringComparison.Ordinal))
                return false;
            if (tip.AcceptedRecordingIds != null && tag.RecordingId != null
                && tip.AcceptedRecordingIds.Contains(tag.RecordingId))
                return true;
            return !string.IsNullOrEmpty(tag.ChainId)
                && string.Equals(tag.ChainId, tip.ChainId, StringComparison.Ordinal)
                && tip.TreeRecordingIds != null
                && tag.RecordingId != null
                && !tip.TreeRecordingIds.Contains(tag.RecordingId);
        }

        /// <summary>The tag naming the snapshot <paramref name="tip"/> spawns from today.</summary>
        internal static RetiredRouteCargoTipTag TagFor(ChainTipCargoIdentity tip)
        {
            return new RetiredRouteCargoTipTag
            {
                TreeId = tip.TipTreeId,
                RecordingId = tip.TipRecordingId,
                ChainId = tip.ChainId,
                CaptureUT = tip.CaptureUT,
                Fingerprint = tip.Fingerprint
            };
        }

        /// <summary>
        /// Pure: the spawn copy's new tank amounts after taking out every stashed crossing that
        /// this tip's snapshot carries and the current timeline has not performed again: a tag
        /// of the row names this snapshot, the row names one of its vessels, lies after its own
        /// rewind cutoff and at or before the tag's capture, and no replay in
        /// <paramref name="replayed"/> was matched to it (<see cref="MatchReplays"/>). Rows are
        /// undone latest first; a delivery is removed, a pickup or debit added back, each
        /// resource from the claimed vessel's own parts first, every tank clamped at zero and at
        /// its capacity. What could not be removed or added is reported as clamped.
        /// </summary>
        internal static ChainTipCargoAdjustment ComputeAdjustment(
            IList<SnapshotTank> tanks,
            IList<RetiredRouteCargoRow> rows,
            ChainTipCargoIdentity tip,
            IList<ReplayedCrossing> replayed)
        {
            var result = new ChainTipCargoAdjustment();
            int n = tanks != null ? tanks.Count : 0;
            result.Amounts = new double[n];
            for (int i = 0; i < n; i++)
                result.Amounts[i] = tanks[i].Amount;
            if (tip == null || rows == null)
                return result;

            var selected = new List<RetiredRouteCargoRow>();
            var selectedTags = new List<RetiredRouteCargoTipTag>();
            var selectedOrder = new List<int>();
            for (int r = 0; r < rows.Count; r++)
            {
                RetiredRouteCargoRow row = rows[r];
                if (row == null)
                    continue;
                RetiredRouteCargoTipTag tag = FirstMatchingTag(row, tip);
                if (tag == null)
                {
                    result.SkippedOtherSnapshot++;
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
                if (row.UT > tag.CaptureUT)
                {
                    result.SkippedAfterCapture++;
                    continue;
                }
                if (row.Resources == null || row.Resources.Count == 0)
                {
                    result.SkippedNoResources++;
                    continue;
                }
                selected.Add(row);
                selectedTags.Add(tag);
                selectedOrder.Add(r);
            }

            bool[] matched = MatchReplays(selected, selectedTags, replayed);
            var apply = new List<int>();
            for (int s = 0; s < selected.Count; s++)
            {
                if (matched[s])
                    result.SkippedReplayed++;
                else
                    apply.Add(s);
            }

            // Undo latest first; equal UTs keep the stash order reversed.
            apply.Sort((a, b) =>
            {
                int c = selected[b].UT.CompareTo(selected[a].UT);
                return c != 0 ? c : selectedOrder[b].CompareTo(selectedOrder[a]);
            });

            var entryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int s = 0; s < apply.Count; s++)
            {
                RetiredRouteCargoRow row = selected[apply[s]];
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

        private static RetiredRouteCargoTipTag FirstMatchingTag(RetiredRouteCargoRow row, ChainTipCargoIdentity tip)
        {
            if (row.Tips == null)
                return null;
            for (int t = 0; t < row.Tips.Count; t++)
            {
                if (TagMatchesTip(row.Tips[t], tip))
                    return row.Tips[t];
            }
            return null;
        }

        /// <summary>
        /// Pure: which stashed crossings the current timeline performed again. Replays are taken
        /// in UT order; each is matched to the unmatched stashed crossing of the same route,
        /// stop and row type whose window (its own cutoff, the tag's capture] holds the replay's
        /// UT, the nearest in UT first (the earlier on a tie). One replay pays for one crossing.
        /// </summary>
        internal static bool[] MatchReplays(
            IList<RetiredRouteCargoRow> selected,
            IList<RetiredRouteCargoTipTag> tags,
            IList<ReplayedCrossing> replayed)
        {
            var matched = new bool[selected != null ? selected.Count : 0];
            if (selected == null || selected.Count == 0 || replayed == null || replayed.Count == 0)
                return matched;

            var order = new List<ReplayedCrossing>(replayed);
            order.Sort((a, b) => a.UT.CompareTo(b.UT));
            for (int e = 0; e < order.Count; e++)
            {
                ReplayedCrossing replay = order[e];
                string group = RetiredRouteCargoStore.ReplayGroupKey(replay.RouteId, replay.StopIndex, replay.Type);
                int best = -1;
                double bestDistance = double.PositiveInfinity;
                for (int s = 0; s < selected.Count; s++)
                {
                    if (matched[s])
                        continue;
                    RetiredRouteCargoRow row = selected[s];
                    if (!string.Equals(row.ReplayGroupKey, group, StringComparison.Ordinal))
                        continue;
                    if (!(replay.UT > row.CutoffUT) || replay.UT > tags[s].CaptureUT)
                        continue;
                    double distance = Math.Abs(replay.UT - row.UT);
                    if (distance < bestDistance
                        || (distance == bestDistance && best >= 0 && row.UT < selected[best].UT))
                    {
                        best = s;
                        bestDistance = distance;
                    }
                }
                if (best >= 0)
                    matched[best] = true;
            }
            return matched;
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
        /// Pure: the rows a retire should keep, each tagged with the tip snapshots that carry
        /// it: the row names the tip's vessel, lies after its cutoff, and is no later than both
        /// the snapshot's capture and the snapshot's watermark (<paramref name="watermarkOf"/>,
        /// +infinity when null or when the snapshot has none). Rows no snapshot carries are
        /// dropped.
        /// </summary>
        internal static List<RetiredRouteCargoRow> TagForChainTips(
            IList<RetiredRouteCargoRow> candidates,
            IList<ChainTipCargoIdentity> tips,
            Func<ChainTipCargoIdentity, double> watermarkOf)
        {
            var kept = new List<RetiredRouteCargoRow>();
            if (candidates == null || tips == null)
                return kept;
            var limits = new double[tips.Count];
            for (int t = 0; t < tips.Count; t++)
            {
                double watermark = tips[t] != null && watermarkOf != null
                    ? watermarkOf(tips[t])
                    : double.PositiveInfinity;
                limits[t] = tips[t] != null ? Math.Min(tips[t].CaptureUT, watermark) : double.NegativeInfinity;
            }
            for (int r = 0; r < candidates.Count; r++)
            {
                RetiredRouteCargoRow row = candidates[r];
                if (row == null)
                    continue;
                if (row.Tips == null)
                    row.Tips = new List<RetiredRouteCargoTipTag>();
                for (int t = 0; t < tips.Count; t++)
                {
                    ChainTipCargoIdentity tip = tips[t];
                    if (tip == null || string.IsNullOrEmpty(tip.TipTreeId) || string.IsNullOrEmpty(tip.Fingerprint))
                        continue;
                    if (!(row.UT > row.CutoffUT) || row.UT > limits[t])
                        continue;
                    if (!RowAddressesTip(row, tip))
                        continue;
                    RetiredRouteCargoTipTag tag = TagFor(tip);
                    bool known = false;
                    for (int k = 0; k < row.Tips.Count; k++)
                    {
                        if (row.Tips[k].SameAs(tag))
                        {
                            known = true;
                            break;
                        }
                    }
                    if (!known)
                        row.Tips.Add(tag);
                }
                if (row.Tips.Count > 0)
                    kept.Add(row);
            }
            return kept;
        }

        // ------------------------------------------------------------------
        // Snapshot identity
        // ------------------------------------------------------------------

        /// <summary>
        /// A fingerprint of a vessel snapshot's resource content: every PART's persistentId and
        /// every RESOURCE's name, amount and capacity, in order (FNV-1a 64 over the invariant
        /// text). Spawn paths rewrite a stored snapshot's position, situation and crew, never
        /// its resources, and an optimizer split moves the snapshot unchanged, so the
        /// fingerprint names one captured snapshot. Null for a null snapshot.
        /// </summary>
        internal static string SnapshotFingerprint(ConfigNode vessel)
        {
            if (vessel == null)
                return null;
            return SnapshotFingerprint(ReadTanks(vessel, null));
        }

        internal static string SnapshotFingerprint(IList<SnapshotTank> tanks)
        {
            var sb = new StringBuilder();
            if (tanks != null)
            {
                for (int i = 0; i < tanks.Count; i++)
                {
                    sb.Append(tanks[i].PartPersistentId.ToString(IC)).Append(':')
                        .Append(tanks[i].Resource ?? "").Append(':')
                        .Append(tanks[i].Amount.ToString("R", IC)).Append(':')
                        .Append(tanks[i].MaxAmount.ToString("R", IC)).Append(';');
                }
            }
            ulong hash = 14695981039346656037UL;
            string text = sb.ToString();
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16", IC);
        }

        // ------------------------------------------------------------------
        // Chain tip identity (reads committed trees, no live KSP)
        // ------------------------------------------------------------------

        /// <summary>
        /// The cargo identity of <paramref name="tip"/> when it is the tip of a non-terminated
        /// ghost chain, else null: its snapshot (fingerprint, optimizer chain, the recording ids
        /// a tag of that snapshot can name), every claimed pid ending in it with its expected
        /// launch guid (<see cref="ChainTipStaleVessel.ExpectedClaimedGuid"/>, the replacement's
        /// rule), the tip's own pid and guid, and the claimed vessels' parts at their claims
        /// (<see cref="GhostChainWalker.ResolveClaimedPartIds"/>).
        /// </summary>
        internal static ChainTipCargoIdentity BuildTipIdentity(
            Recording rec, Dictionary<uint, GhostChain> chains, IList<RecordingTree> trees)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return null;
            RecordingTree recTree = FindTree(trees, rec.TreeId);

            // The walker's tip is the recording its walk reached; an optimizer split leaves the
            // walk on the first segment and moves the snapshot to the last one, so the tip is
            // also found through the earlier segments of this recording's chain.
            List<GhostChain> tipChains = ChainTipStaleVessel.FindChainsForTip(chains, rec.RecordingId);
            if (tipChains.Count == 0)
            {
                List<Recording> earlier = EarlierChainSegments(rec, recTree);
                for (int i = 0; i < earlier.Count && tipChains.Count == 0; i++)
                    tipChains = ChainTipStaleVessel.FindChainsForTip(chains, earlier[i].RecordingId);
            }
            if (tipChains.Count == 0 || tipChains[0].IsTerminated)
                return null;
            string walkerTipId = tipChains[0].TipRecordingId;

            // The recording that holds the snapshot: the tip itself, or the last segment of its
            // chain when a split moved the snapshot there.
            Recording tip = rec;
            if (tip.VesselSnapshot == null)
            {
                Recording later = LatestChainSegmentWithSnapshot(rec, recTree);
                if (later != null)
                    tip = later;
            }

            string fingerprint = SnapshotFingerprint(tip.VesselSnapshot);
            var id = new ChainTipCargoIdentity
            {
                IsChainTip = true,
                TipRecordingId = tip.RecordingId,
                TipTreeId = !string.IsNullOrEmpty(tipChains[0].TipTreeId) ? tipChains[0].TipTreeId : tip.TreeId,
                CaptureUT = SnapshotCaptureUT(tip.EndUT, tip.TreeId, fingerprint),
                Fingerprint = fingerprint,
                ChainId = string.IsNullOrEmpty(tip.ChainId) ? null : tip.ChainId
            };
            id.AcceptedRecordingIds.Add(tip.RecordingId);
            RecordingTree tipTree = FindTree(trees, id.TipTreeId);
            if (tipTree != null && tipTree.Recordings != null)
            {
                id.TreeRecordingIds = new HashSet<string>(tipTree.Recordings.Keys, StringComparer.Ordinal);
                List<Recording> earlier = EarlierChainSegments(tip, tipTree);
                for (int i = 0; i < earlier.Count; i++)
                    id.AcceptedRecordingIds.Add(earlier[i].RecordingId);
            }

            var claimed = new List<uint>(ChainTipStaleVessel.ResolveClaimedPidsForTip(chains, walkerTipId, trees));
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

        /// <summary>
        /// The cargo identity of every non-terminated chain tip, one per tip recording, skipping
        /// a tip whose recording (and the later segments of its optimizer chain) ends at or
        /// before <paramref name="onlyCapturedAfterUT"/>: a retire or load at that cutoff can
        /// neither tag a row to such a snapshot (every retired row lies after the cutoff) nor
        /// lower its watermark below its capture. A tip whose in-memory snapshot was dropped is
        /// re-hydrated from its sidecar first.
        /// </summary>
        internal static List<ChainTipCargoIdentity> BuildTipIdentities(
            Dictionary<uint, GhostChain> chains, IList<RecordingTree> trees,
            double onlyCapturedAfterUT = double.NegativeInfinity)
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
                if (tip == null
                    || !MayBeCapturedAfter(LatestChainEndUT(tip, FindTree(trees, tip.TreeId)), tip.TreeId, onlyCapturedAfterUT))
                    continue;
                HydrateSnapshot(tip);
                ChainTipCargoIdentity id = BuildTipIdentity(tip, chains, trees);
                if (id != null && id.CaptureUT > onlyCapturedAfterUT)
                    tips.Add(id);
            }
            return tips;
        }

        /// <summary>
        /// Cheap pre-check before a snapshot is read: a recording that ends at or before the
        /// cutoff can still hold a snapshot captured after it when the optimizer trimmed its
        /// tail, which a noted capture of its tree says.
        /// </summary>
        private static bool MayBeCapturedAfter(double recordingEndUT, string treeId, double cutoffUT)
        {
            return recordingEndUT > cutoffUT || RetiredRouteCargoStore.HasCaptureAfter(treeId, cutoffUT);
        }

        private static void HydrateSnapshot(Recording rec)
        {
            if (rec == null || rec.VesselSnapshot != null)
                return;
            try
            {
                RecordingStore.TryHydrateVesselSnapshotFromSidecar(rec);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    "Snapshot re-hydrate threw " + ex.GetType().Name + " for rec="
                    + rec.RecordingId + " - its retired cargo is not kept");
            }
        }

        /// <summary>
        /// The cargo identity of an ORDINARY end-of-recording spawn (not a ghost chain tip):
        /// the snapshot <paramref name="rec"/> spawns its vessel from (its own, or the later
        /// segment of its optimizer chain a split moved it to), and its own recorded vessel
        /// named POSITIVELY: pid plus the recorded launch guid and the snapshot's root part
        /// flightID (<see cref="IsPositiveEndpointMatch"/>). Null without a snapshot, without a
        /// pid, or with neither a launch guid nor a root part flightID: a bare pid never names
        /// an ordinary leaf's vessel. The whole vessel is the endpoint, so there are no "own
        /// parts first".
        /// </summary>
        internal static ChainTipCargoIdentity BuildLeafIdentity(Recording rec, IList<RecordingTree> trees)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return null;
            RecordingTree tree = FindTree(trees, rec.TreeId);
            Recording holder = rec;
            if (holder.VesselSnapshot == null)
            {
                Recording later = LatestChainSegmentWithSnapshot(rec, tree);
                if (later != null)
                    holder = later;
            }
            if (holder.VesselSnapshot == null || holder.VesselPersistentId == 0u)
                return null;

            VesselSnapshotOps.TryReadRootPartFlightId(holder.VesselSnapshot, out uint rootPartUId);
            string guid = VesselLaunchIdentity.NormalizeGuid(holder.RecordedVesselGuid);
            if (guid == null && rootPartUId == 0u)
                return null;

            string fingerprint = SnapshotFingerprint(holder.VesselSnapshot);
            var id = new ChainTipCargoIdentity
            {
                IsChainTip = false,
                TipRecordingId = holder.RecordingId,
                TipTreeId = holder.TreeId,
                CaptureUT = SnapshotCaptureUT(holder.EndUT, holder.TreeId, fingerprint),
                Fingerprint = fingerprint,
                ChainId = string.IsNullOrEmpty(holder.ChainId) ? null : holder.ChainId
            };
            id.AcceptedRecordingIds.Add(holder.RecordingId);
            if (tree != null && tree.Recordings != null)
            {
                id.TreeRecordingIds = new HashSet<string>(tree.Recordings.Keys, StringComparer.Ordinal);
                List<Recording> earlier = EarlierChainSegments(holder, tree);
                for (int i = 0; i < earlier.Count; i++)
                    id.AcceptedRecordingIds.Add(earlier[i].RecordingId);
            }
            id.PositiveVessels.Add(new CargoVesselIdentity
            {
                Pid = holder.VesselPersistentId,
                LaunchGuid = guid,
                RootPartUId = rootPartUId
            });
            return id;
        }

        /// <summary>
        /// The cargo identity of the snapshot a spawn of <paramref name="rec"/> materializes:
        /// the chain tip's when it is the tip of a live ghost chain (its own vessel is already
        /// one of the tip's vessels), else the ordinary leaf's. Exactly one per spawn, so a
        /// recording that is both is adjusted once.
        /// </summary>
        internal static ChainTipCargoIdentity BuildSpawnIdentity(
            Recording rec, Dictionary<uint, GhostChain> chains, IList<RecordingTree> trees)
        {
            return BuildTipIdentity(rec, chains, trees) ?? BuildLeafIdentity(rec, trees);
        }

        /// <summary>
        /// The cargo identity of every committed ORDINARY end-of-recording spawn whose own
        /// vessel is positively one of <paramref name="endpoints"/>: a final spawn segment
        /// (<see cref="GhostPlaybackLogic.IsFinalSpawnSegment"/>), not debris, with a spawnable
        /// or no terminal, whose snapshot was captured after <paramref name="onlyCapturedAfterUT"/>
        /// and is not a chain tip already in <paramref name="chainTipRecordingIds"/>. A leaf
        /// whose in-memory snapshot was dropped is re-hydrated from its sidecar first.
        /// </summary>
        internal static List<ChainTipCargoIdentity> BuildLeafIdentities(
            IList<RecordingTree> trees, IList<RouteEndpoint> endpoints,
            ICollection<string> chainTipRecordingIds,
            double onlyCapturedAfterUT = double.NegativeInfinity)
        {
            var leaves = new List<ChainTipCargoIdentity>();
            if (trees == null || endpoints == null || endpoints.Count == 0)
                return leaves;
            var endpointPids = new HashSet<uint>();
            for (int e = 0; e < endpoints.Count; e++)
            {
                if (endpoints[e].VesselPersistentId != 0u)
                    endpointPids.Add(endpoints[e].VesselPersistentId);
            }
            if (endpointPids.Count == 0)
                return leaves;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree == null || tree.Recordings == null)
                    continue;
                var ids = new List<string>(tree.Recordings.Keys);
                ids.Sort(StringComparer.Ordinal);
                for (int r = 0; r < ids.Count; r++)
                {
                    Recording rec = tree.Recordings[ids[r]];
                    if (rec == null || rec.VesselPersistentId == 0u || rec.IsDebris
                        || !endpointPids.Contains(rec.VesselPersistentId))
                        continue;
                    if (chainTipRecordingIds != null && chainTipRecordingIds.Contains(rec.RecordingId))
                        continue;
                    if (rec.TerminalStateValue.HasValue
                        && !GhostPlaybackLogic.IsSpawnableTerminal(rec.TerminalStateValue.Value))
                        continue;
                    if (!GhostPlaybackLogic.IsFinalSpawnSegment(rec, tree))
                        continue;
                    if (!MayBeCapturedAfter(rec.EndUT, rec.TreeId, onlyCapturedAfterUT))
                        continue;
                    HydrateSnapshot(rec);
                    ChainTipCargoIdentity id = BuildLeafIdentity(rec, trees);
                    if (id == null || id.CaptureUT <= onlyCapturedAfterUT)
                        continue;
                    if (chainTipRecordingIds != null && chainTipRecordingIds.Contains(id.TipRecordingId))
                        continue;
                    if (!AnyEndpointIsPositively(endpoints, id.PositiveVessels[0]))
                        continue;
                    if (seen.Add(id.TipRecordingId))
                        leaves.Add(id);
                }
            }
            return leaves;
        }

        private static bool AnyEndpointIsPositively(IList<RouteEndpoint> endpoints, CargoVesselIdentity vessel)
        {
            for (int e = 0; e < endpoints.Count; e++)
            {
                if (IsPositiveEndpointMatch(
                        endpoints[e].VesselPersistentId, endpoints[e].LaunchGuid, endpoints[e].RootPartUId, vessel))
                    return true;
            }
            return false;
        }

        /// <summary>Every vessel endpoint of the routes: each stop, and the origin unless it is the Space Center.</summary>
        internal static List<RouteEndpoint> CollectRouteEndpoints(params IEnumerable<Route>[] routeSets)
        {
            var endpoints = new List<RouteEndpoint>();
            if (routeSets == null)
                return endpoints;
            for (int s = 0; s < routeSets.Length; s++)
            {
                if (routeSets[s] == null)
                    continue;
                foreach (Route route in routeSets[s])
                {
                    if (route == null)
                        continue;
                    if (!route.IsKscOrigin && route.Origin.VesselPersistentId != 0u)
                        endpoints.Add(route.Origin);
                    if (route.Stops == null)
                        continue;
                    for (int i = 0; i < route.Stops.Count; i++)
                    {
                        if (route.Stops[i] != null && route.Stops[i].Endpoint.VesselPersistentId != 0u)
                            endpoints.Add(route.Stops[i].Endpoint);
                    }
                }
            }
            return endpoints;
        }

        /// <summary>
        /// Called by the optimizer pass for every recording whose boring tail it just trimmed,
        /// with the recording's end before the trim: notes that moment as the real capture of
        /// the snapshot the recording keeps (<see cref="RetiredRouteCargoStore.NoteSnapshotCapture"/>),
        /// so a retire tags, and a spawn copy takes out, the crossings of the trimmed tail too.
        /// Only while some supply route exists: a route made later delivers after the capture.
        /// Never throws.
        /// </summary>
        internal static void NoteTrimmedSnapshotCapture(Recording rec, double captureUT)
        {
            if (rec == null || double.IsNaN(captureUT) || double.IsInfinity(captureUT))
                return;
            try
            {
                int routes = RouteStore.CommittedRoutes.Count + RouteStore.DormantRoutes.Count;
                if (routes == 0)
                {
                    ParsekLog.Verbose(Tag,
                        "Trimmed snapshot capture not noted: rec=" + (rec.RecordingId ?? "(null)")
                        + " captureUT=" + captureUT.ToString("R", IC) + " - no supply route");
                    return;
                }
                HydrateSnapshot(rec);
                if (rec.VesselSnapshot == null || string.IsNullOrEmpty(rec.TreeId))
                {
                    ParsekLog.Verbose(Tag,
                        "Trimmed snapshot capture not noted: rec=" + (rec.RecordingId ?? "(null)")
                        + " tree=" + (rec.TreeId ?? "(null)") + " hasSnapshot=" + (rec.VesselSnapshot != null)
                        + " - nothing to name");
                    return;
                }
                string fingerprint = SnapshotFingerprint(rec.VesselSnapshot);
                bool noted = RetiredRouteCargoStore.NoteSnapshotCapture(
                    rec.TreeId, rec.RecordingId, fingerprint, captureUT);
                ParsekLog.Verbose(Tag,
                    "Trimmed snapshot capture " + (noted ? "noted" : "not noted") + ": rec=" + rec.RecordingId
                    + " tree=" + rec.TreeId + " fingerprint=" + fingerprint
                    + " captureUT=" + captureUT.ToString("R", IC)
                    + " trimmedEndUT=" + rec.EndUT.ToString("R", IC)
                    + " captures=" + RetiredRouteCargoStore.Captures.Count.ToString(IC));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    "Trimmed snapshot capture threw " + ex.GetType().Name + ": " + ex.Message
                    + " - rec=" + (rec.RecordingId ?? "(null)")
                    + " keeps its trimmed end as the capture");
            }
        }

        /// <summary>The earlier segments of <paramref name="rec"/>'s optimizer chain in its tree (same chain id and branch, lower index).</summary>
        private static List<Recording> EarlierChainSegments(Recording rec, RecordingTree tree)
        {
            var found = new List<Recording>();
            if (rec == null || tree == null || tree.Recordings == null
                || string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0)
                return found;
            foreach (Recording r in tree.Recordings.Values)
            {
                if (r != null && !ReferenceEquals(r, rec)
                    && string.Equals(r.ChainId, rec.ChainId, StringComparison.Ordinal)
                    && r.ChainBranch == rec.ChainBranch
                    && r.ChainIndex >= 0 && r.ChainIndex < rec.ChainIndex)
                    found.Add(r);
            }
            found.Sort((a, b) => b.ChainIndex.CompareTo(a.ChainIndex));
            return found;
        }

        /// <summary>The latest end over <paramref name="rec"/> and the later segments of its optimizer chain.</summary>
        private static double LatestChainEndUT(Recording rec, RecordingTree tree)
        {
            double end = rec.EndUT;
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0)
                return end;
            foreach (Recording r in tree.Recordings.Values)
            {
                if (r != null && string.Equals(r.ChainId, rec.ChainId, StringComparison.Ordinal)
                    && r.ChainBranch == rec.ChainBranch && r.ChainIndex > rec.ChainIndex
                    && r.EndUT > end)
                    end = r.EndUT;
            }
            return end;
        }

        /// <summary>The highest later segment of <paramref name="rec"/>'s optimizer chain that holds a snapshot; null when none.</summary>
        private static Recording LatestChainSegmentWithSnapshot(Recording rec, RecordingTree tree)
        {
            if (rec == null || tree == null || tree.Recordings == null
                || string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0)
                return null;
            Recording best = null;
            foreach (Recording r in tree.Recordings.Values)
            {
                if (r == null || r.VesselSnapshot == null
                    || !string.Equals(r.ChainId, rec.ChainId, StringComparison.Ordinal)
                    || r.ChainBranch != rec.ChainBranch || r.ChainIndex <= rec.ChainIndex)
                    continue;
                if (best == null || r.ChainIndex > best.ChainIndex)
                    best = r;
            }
            return best;
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
        /// Called by every retire (the ledger retire of the go-back rewind and the in-session
        /// load reconcile, and the Re-Fly restore) with the rows it just removed, even when it
        /// removed none, and with no rows by an in-session load whose route reconcile found
        /// nothing after its cutoff: keeps the cargo rows a committed chain tip snapshot carries,
        /// tagged with that snapshot, then lowers the watermark of every tip snapshot captured
        /// after this cutoff to it. The chains
        /// are walked over the trees committed now, before the rewind's future is replayed.
        /// Never throws. Returns how many rows were stashed.
        /// </summary>
        internal static int CaptureRetiredRouteCargo(
            IReadOnlyList<GameAction> retired,
            double cutoffUT,
            IEnumerable<Route> committedRoutes,
            IEnumerable<Route> dormantRoutes,
            string site)
        {
            string siteLabel = site ?? "?";
            try
            {
                List<RecordingTree> trees = RecordingStore.CommittedTrees;
                if (trees == null || trees.Count == 0)
                    return 0;

                var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
                AddRoutes(routes, committedRoutes);
                AddRoutes(routes, dormantRoutes);

                var candidates = new List<RetiredRouteCargoRow>();
                int retiredCount = retired != null ? retired.Count : 0;
                int cargoRows = 0, noResource = 0, noRoute = 0;
                for (int i = 0; i < retiredCount; i++)
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

                List<ChainTipCargoIdentity> chainTips =
                    BuildTipIdentities(GhostChainWalker.ComputeAllGhostChains(trees, 0.0), trees, cutoffUT);
                var chainTipIds = new HashSet<string>(StringComparer.Ordinal);
                for (int t = 0; t < chainTips.Count; t++)
                {
                    if (!string.IsNullOrEmpty(chainTips[t].TipRecordingId))
                        chainTipIds.Add(chainTips[t].TipRecordingId);
                }
                // Ordinary end spawns whose own vessel is a route endpoint; a recording that is
                // already a chain tip keeps its one chain identity.
                List<ChainTipCargoIdentity> leaves = BuildLeafIdentities(
                    trees, CollectRouteEndpoints(committedRoutes, dormantRoutes), chainTipIds, cutoffUT);
                var tips = new List<ChainTipCargoIdentity>(chainTips);
                tips.AddRange(leaves);
                List<RetiredRouteCargoRow> tagged = TagForChainTips(candidates, tips,
                    tip => RetiredRouteCargoStore.WatermarkOf(tag => TagMatchesTip(tag, tip)));
                int added = RetiredRouteCargoStore.Merge(tagged, out int merged);

                int lowered = 0, noSnapshot = 0;
                for (int t = 0; t < tips.Count; t++)
                {
                    ChainTipCargoIdentity tip = tips[t];
                    if (string.IsNullOrEmpty(tip.Fingerprint))
                    {
                        noSnapshot++;
                        continue;
                    }
                    RetiredRouteCargoStore.LowerWatermark(TagFor(tip), cutoffUT, tag => TagMatchesTip(tag, tip));
                    lowered++;
                }

                string line = "Retired route cargo kept for end-of-recording snapshots (" + siteLabel + "): cutoffUT="
                    + cutoffUT.ToString("R", IC)
                    + " retiredRows=" + retiredCount.ToString(IC)
                    + " cargoRows=" + cargoRows.ToString(IC)
                    + " stashed=" + tagged.Count.ToString(IC)
                    + " (new=" + added.ToString(IC) + " merged=" + merged.ToString(IC) + ")"
                    + " carriedByNoSnapshot=" + (candidates.Count - tagged.Count).ToString(IC)
                    + " noResource=" + noResource.ToString(IC)
                    + " routeNotFound=" + noRoute.ToString(IC)
                    + " chainTips=" + chainTips.Count.ToString(IC)
                    + " leaves=" + leaves.Count.ToString(IC)
                    + " watermarksLowered=" + lowered.ToString(IC)
                    + " tipsWithoutSnapshot=" + noSnapshot.ToString(IC)
                    + " totalRows=" + RetiredRouteCargoStore.Rows.Count.ToString(IC)
                    + " totalWatermarks=" + RetiredRouteCargoStore.Watermarks.Count.ToString(IC)
                    + " notedCaptures=" + RetiredRouteCargoStore.Captures.Count.ToString(IC);
                if (tagged.Count > 0)
                    ParsekLog.Info(Tag, line);
                else
                    ParsekLog.Verbose(Tag, line);
                return tagged.Count;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    "Retired route cargo capture (" + siteLabel + ") threw " + ex.GetType().Name
                    + ": " + ex.Message + " - nothing kept, a snapshot spawned later carries the refunded cargo");
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
        /// Takes the stashed retired route cargo out of an end-of-recording spawn copy: a chain
        /// tip's, or an ordinary leaf's whose own vessel is the route endpoint
        /// (<see cref="BuildSpawnIdentity"/>, one identity per spawn). Every site that turns a
        /// recording's stored snapshot into a spawn copy calls it on the copy (never on the
        /// stored snapshot), so each spawn attempt starts again from the recorded state: the
        /// shared <c>VesselSpawner.BuildValidatedRespawnSnapshot(Recording, ...)</c> (flight
        /// leaf, Tracking Station hand-off, deferred spawn queue, Real Spawn Control's warp, the
        /// chain-tip fallbacks), <c>VesselGhoster.SpawnChainTipWithResolvedState</c> and the
        /// Space Center end spawn's working copy. A recording whose snapshot no stashed crossing
        /// names is left as it is. Never throws.
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
                ChainTipCargoIdentity tip = BuildSpawnIdentity(rec, chains, trees);
                if (tip == null)
                {
                    ParsekLog.Verbose(Tag,
                        "Spawn copy kept as recorded (" + siteLabel + "): rec=" + (rec.RecordingId ?? "(null)")
                        + " is not the tip of a live ghost chain and names its vessel by no launch guid"
                        + " or root part (or has no snapshot)");
                    return;
                }
                string kind = tip.IsChainTip ? "chain-tip" : "leaf";

                List<ReplayedCrossing> replayed = CollectReplayedCrossings();
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

                string skipped = " skipped(otherSnapshot=" + adj.SkippedOtherSnapshot.ToString(IC)
                    + " otherVessel=" + adj.SkippedOtherVessel.ToString(IC)
                    + " notAfterCutoff=" + adj.SkippedNotAfterCutoff.ToString(IC)
                    + " afterCapture=" + adj.SkippedAfterCapture.ToString(IC)
                    + " replayed=" + adj.SkippedReplayed.ToString(IC)
                    + " noResources=" + adj.SkippedNoResources.ToString(IC) + ")";
                if (adj.RowsApplied == 0)
                {
                    ParsekLog.Verbose(Tag,
                        "Spawn copy kept as recorded (" + siteLabel + "): kind=" + kind
                        + " rec=" + tip.TipRecordingId
                        + " tree=" + (tip.TipTreeId ?? "(null)")
                        + " fingerprint=" + (tip.Fingerprint ?? "(none)")
                        + " captureUT=" + tip.CaptureUT.ToString("R", IC)
                        + " - no retired route crossing in this snapshot" + skipped);
                    return;
                }

                ParsekLog.Info(Tag,
                    "Retired route cargo taken out of the end-of-recording spawn copy (" + siteLabel + "): kind="
                    + kind + " rec="
                    + tip.TipRecordingId + " vessel=\"" + (rec.VesselName ?? "(null)") + "\""
                    + " tree=" + (tip.TipTreeId ?? "(null)")
                    + " fingerprint=" + (tip.Fingerprint ?? "(none)")
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
                List<RetiredRouteCargoTipTag> tips = stash[i]?.Tips;
                if (tips == null)
                    continue;
                for (int t = 0; t < tips.Count; t++)
                {
                    if (tips[t] != null && string.Equals(tips[t].TreeId, treeId, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Every cargo-moving route row in the effective ledger: crossings the current timeline
        /// performed (and paid).
        /// </summary>
        private static List<ReplayedCrossing> CollectReplayedCrossings()
        {
            var replayed = new List<ReplayedCrossing>();
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
                return replayed;
            }
            if (els == null)
                return replayed;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null || !RetiredRouteCargoStore.IsCargoRowType(a.Type)
                    || !RouteLedgerRetire.IsPhysicalRouteMutation(a))
                    continue;
                replayed.Add(new ReplayedCrossing
                {
                    RouteId = a.RouteId,
                    StopIndex = a.RouteStopIndex,
                    Type = a.Type,
                    UT = a.UT
                });
            }
            return replayed;
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
