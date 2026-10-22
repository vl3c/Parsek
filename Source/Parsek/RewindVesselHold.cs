using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The plain rewind's vessel hold (todos REWIND-LEAVES-VESSELS-WITH-A-COMMITTED-FUTURE-LIVE and
    /// CLAIMED-VESSEL-DESPAWN-AT-REWIND-OUTSIDE-FLIGHT; rewind design 10.13, flight recorder
    /// design 12.5 / 20.3). A Rewind-to-Launch lands in the rewind save's world, which holds
    /// every vessel that was live at the rewind UT. A vessel the committed future uses after
    /// that UT (a ghost chain claims it, a committed recording of its launch flies or recovers
    /// it, a committed dock or board changes it) is held back at the rewind in every scene, as
    /// a Re-Fly invoke holds it (<see cref="ReFlyInvokeHold"/>): its ProtoVessel leaves the
    /// rewind's flightState before any scene sees it (its crew go Available through the
    /// rewind's orphaned-crew rescue), the committed recording plays it as a ghost from its
    /// first claim, and its end spawn (or the chain tip) brings it back. A vessel the committed
    /// future recovers or destroys does not come back.
    ///
    /// <para>
    /// The holds are kept here, with the rewind save's copy of each vessel, and saved in the
    /// Parsek scenario node (<see cref="NodeName"/>), so the flight scene draws each held
    /// vessel's hold ghost in its rewind-time shape until its first claim, across scene changes
    /// and save loads. The Space Center and the Tracking Station show nothing for it until the
    /// committed recording's own ghost appears (owner ruling pending, recommended option).
    /// </para>
    ///
    /// <para>
    /// Identity follows the Re-Fly hold: a chain claim matches the chain's pid (a folded claim
    /// also its launch guid, <c>ParsekFlight.SelectChainPidsToGhost</c>); a flies / recovers /
    /// changes claim needs POSITIVE launch identity (pid equal, both launch guids known and
    /// equal), so a relaunch of the same craft is never held.
    /// </para>
    /// </summary>
    internal static class RewindVesselHold
    {
        internal const string Tag = "RewindHold";
        internal const string EvidenceChain = "chain";
        internal const string NodeName = "REWIND_VESSEL_HOLDS";
        internal const string HoldNodeName = "HOLD";
        internal const string SnapshotNodeName = "VESSEL";

        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>One held vessel: the hold decision plus the rewind save's copy of it.</summary>
        internal sealed class Hold
        {
            public ReFlyInvokeHold.Entry Entry;
            public string VesselName;
            public ConfigNode Snapshot;
        }

        private static readonly List<Hold> holds = new List<Hold>();

        /// <summary>The live holds of the last plain rewind (empty when none).</summary>
        internal static IReadOnlyList<Hold> Holds => holds;

        /// <summary>Replaces the held set with <paramref name="next"/> (a rewind's own holds, or a save's).</summary>
        internal static void Replace(IList<Hold> next, string reason)
        {
            int before = holds.Count;
            holds.Clear();
            if (next != null)
            {
                for (int i = 0; i < next.Count; i++)
                {
                    if (next[i]?.Entry != null && next[i].Entry.Pid != 0u)
                        holds.Add(next[i]);
                }
            }
            ParsekLog.Info(Tag, string.Format(ic,
                "Rewind vessel holds replaced: before={0} after={1} reason={2}",
                before, holds.Count, reason ?? "unspecified"));
        }

        /// <summary>Drops every hold (a Re-Fly start, whose own invoke holds take over).</summary>
        internal static void Clear(string reason)
        {
            if (holds.Count == 0)
                return;
            int before = holds.Count;
            holds.Clear();
            ParsekLog.Info(Tag, string.Format(ic,
                "Rewind vessel holds cleared: dropped={0} reason={1}", before, reason ?? "unspecified"));
        }

        internal static void ResetForTesting()
        {
            holds.Clear();
        }

        /// <summary>
        /// Pure: every vessel an active ghost chain holds back at <paramref name="rewindUT"/>
        /// (spawn UT ahead, not terminated), the chain key and each folded claim, as hold entries
        /// with evidence <see cref="EvidenceChain"/> and the claim's first UT. A folded claim
        /// whose live vessel is conclusively another launch is left out
        /// (<c>ParsekFlight.SelectChainPidsToGhost</c>). <paramref name="liveGuidOf"/> answers a
        /// pid's launch guid in the rewind's world (null when unknown or absent).
        /// </summary>
        internal static List<ReFlyInvokeHold.Entry> CollectChainHolds(
            Dictionary<uint, GhostChain> chains, double rewindUT, Func<uint, string> liveGuidOf)
        {
            var result = new List<ReFlyInvokeHold.Entry>();
            if (chains == null || double.IsNaN(rewindUT) || double.IsInfinity(rewindUT))
                return result;
            var seen = new HashSet<uint>();
            foreach (var kv in chains)
            {
                GhostChain chain = kv.Value;
                if (chain == null || chain.IsTerminated || !GhostChainWalker.ShouldGhostChainAtUT(chain, rewindUT))
                    continue;
                List<uint> pids = ParsekFlight.SelectChainPidsToGhost(kv.Key, chain, liveGuidOf);
                List<ChainClaimedVessel> held = chain.HeldBackVessels();
                for (int p = 0; p < pids.Count; p++)
                {
                    uint pid = pids[p];
                    if (pid == 0u || !seen.Add(pid))
                        continue;
                    bool found = false;
                    ChainClaimedVessel claim = default(ChainClaimedVessel);
                    for (int h = 0; h < held.Count; h++)
                    {
                        if (held[h].Pid == pid)
                        {
                            claim = held[h];
                            found = true;
                            break;
                        }
                    }
                    string claimGuid = found ? claim.LaunchGuid : null;
                    result.Add(new ReFlyInvokeHold.Entry
                    {
                        Pid = pid,
                        LaunchGuid = !string.IsNullOrEmpty(claimGuid)
                            ? claimGuid
                            : (liveGuidOf != null ? liveGuidOf(pid) : null),
                        RecordingId = chain.TipRecordingId,
                        FirstClaimUT = found ? claim.FirstClaimUT : chain.GhostStartUT,
                        Evidence = EvidenceChain,
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Pure: one entry per pid from <paramref name="chainEntries"/> and
        /// <paramref name="futureEntries"/> (<see cref="ReFlyInvokeHold.Classify(IReadOnlyList{ValueTuple{uint, string}}, ICollection{uint}, IReadOnlyList{Recording}, IReadOnlyList{GameAction}, IReadOnlyList{ReFlyInvokeHold.ChangeClaim}, double, out int)"/>),
        /// keeping the earliest first claim and every flying recording either side names.
        /// </summary>
        internal static List<ReFlyInvokeHold.Entry> Merge(
            IList<ReFlyInvokeHold.Entry> chainEntries, IList<ReFlyInvokeHold.Entry> futureEntries)
        {
            var byPid = new Dictionary<uint, ReFlyInvokeHold.Entry>();
            var order = new List<uint>();
            AddAll(chainEntries, byPid, order);
            AddAll(futureEntries, byPid, order);
            var result = new List<ReFlyInvokeHold.Entry>(order.Count);
            for (int i = 0; i < order.Count; i++)
                result.Add(byPid[order[i]]);
            return result;
        }

        private static void AddAll(
            IList<ReFlyInvokeHold.Entry> entries, Dictionary<uint, ReFlyInvokeHold.Entry> byPid, List<uint> order)
        {
            if (entries == null)
                return;
            for (int i = 0; i < entries.Count; i++)
            {
                ReFlyInvokeHold.Entry e = entries[i];
                if (e == null || e.Pid == 0u)
                    continue;
                ReFlyInvokeHold.Entry existing;
                if (!byPid.TryGetValue(e.Pid, out existing))
                {
                    var copy = new ReFlyInvokeHold.Entry
                    {
                        Pid = e.Pid,
                        LaunchGuid = e.LaunchGuid,
                        RecordingId = e.RecordingId,
                        FirstClaimUT = e.FirstClaimUT,
                        Evidence = e.Evidence,
                    };
                    if (e.FlyingRecordingIds != null)
                        copy.FlyingRecordingIds.AddRange(e.FlyingRecordingIds);
                    byPid[e.Pid] = copy;
                    order.Add(e.Pid);
                    continue;
                }
                if (string.IsNullOrEmpty(existing.LaunchGuid))
                    existing.LaunchGuid = e.LaunchGuid;
                if (e.FirstClaimUT < existing.FirstClaimUT)
                {
                    existing.FirstClaimUT = e.FirstClaimUT;
                    existing.RecordingId = e.RecordingId;
                    existing.Evidence = e.Evidence;
                }
                if (e.FlyingRecordingIds != null)
                {
                    for (int r = 0; r < e.FlyingRecordingIds.Count; r++)
                    {
                        if (!existing.FlyingRecordingIds.Contains(e.FlyingRecordingIds[r]))
                            existing.FlyingRecordingIds.Add(e.FlyingRecordingIds[r]);
                    }
                }
            }
        }

        /// <summary>
        /// Writes every hold as a <see cref="NodeName"/> child of <paramref name="parsekNode"/>
        /// (nothing when none). A hold whose claim has passed is kept too: the flight scene skips
        /// it by the clock (a save can run before the rewind's clock adjustment lands), and the
        /// next plain rewind replaces the set.
        /// </summary>
        internal static int SaveInto(ConfigNode parsekNode)
        {
            if (parsekNode == null)
                return 0;
            parsekNode.RemoveNodes(NodeName);
            int written = 0;
            ConfigNode root = null;
            for (int i = 0; i < holds.Count; i++)
            {
                Hold h = holds[i];
                if (h?.Entry == null || h.Entry.Pid == 0u)
                    continue;
                if (root == null)
                    root = parsekNode.AddNode(NodeName);
                ConfigNode holdNode = root.AddNode(HoldNodeName);
                ReFlyInvokeHold.SaveEntry(holdNode, h.Entry);
                if (!string.IsNullOrEmpty(h.VesselName))
                    holdNode.AddValue("vesselName", h.VesselName);
                if (h.Snapshot != null)
                {
                    ConfigNode snap = holdNode.AddNode(SnapshotNodeName);
                    h.Snapshot.CopyTo(snap);
                }
                written++;
            }
            return written;
        }

        /// <summary>Reads the holds a <see cref="SaveInto"/> wrote; empty when the node has none.</summary>
        internal static List<Hold> LoadFrom(ConfigNode parsekNode)
        {
            var result = new List<Hold>();
            ConfigNode root = parsekNode?.GetNode(NodeName);
            if (root == null)
                return result;
            ConfigNode[] nodes = root.GetNodes(HoldNodeName);
            for (int i = 0; i < nodes.Length; i++)
            {
                ConfigNode entryNode = nodes[i].GetNode(ReFlyInvokeHold.EntryNodeName);
                ReFlyInvokeHold.Entry entry = ReFlyInvokeHold.LoadEntry(entryNode);
                if (entry == null)
                    continue;
                ConfigNode snap = nodes[i].GetNode(SnapshotNodeName);
                result.Add(new Hold
                {
                    Entry = entry,
                    VesselName = nodes[i].GetValue("vesselName"),
                    Snapshot = snap != null ? snap.CreateCopy() : null,
                });
            }
            return result;
        }

        /// <summary>Pure: the one-line description of a held vessel the rewind logs.</summary>
        internal static string FormatHeldLine(Hold hold)
        {
            ReFlyInvokeHold.Entry e = hold?.Entry;
            if (e == null)
                return "Rewind held vessel: <none>";
            return string.Format(ic,
                "Rewind held vessel for its committed future: pid={0} name='{1}' guid={2} rec={3} " +
                "firstClaimUT={4:F1} evidence={5} flying={6} snapshot={7}",
                e.Pid, hold.VesselName ?? "(unnamed)", e.LaunchGuid ?? "(none)", e.RecordingId ?? "(none)",
                e.FirstClaimUT, e.Evidence ?? "(none)", e.FlyingRecordingIds.Count,
                hold.Snapshot != null ? "yes" : "no");
        }
    }
}
