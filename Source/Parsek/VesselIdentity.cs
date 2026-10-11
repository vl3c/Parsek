// [ERS-exempt] VesselIdentity correlates PHYSICAL vessels across every recording the
// caller hands it, superseded ones included: a vessel name decision names the ship,
// whatever the timeline (design-vessel-and-mission-names.md 6.2). It reads no store itself;
// VesselNameStore gathers the raw committed / pending / live recordings it resolves.
using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The one answer to "is this the same vessel?" (design-vessel-and-mission-names.md 6.2):
    /// a union-find over recordings, shared by the vessel name store (identity-wide name
    /// decisions) and the mission vessel naming (<see cref="MissionVesselNaming"/>: one
    /// vessel per row, numbered per name).
    ///
    /// <para>Edges, strongest first:</para>
    /// <list type="number">
    /// <item>Same normalized <see cref="Recording.RecordedVesselGuid"/>, both known, across
    /// trees (guid alone, so a resumed spawn whose pid was rewritten stays linked).</item>
    /// <item>Same <see cref="Recording.ChainId"/>.</item>
    /// <item>A <see cref="BranchPointType.VesselSwitchContinuation"/> with one parent and one
    /// child (the hop <c>EffectiveState.WalkSlotVessel</c> takes).</item>
    /// <item>Link rows (a re-keyed guid continuing another guid's identity): Phase 2b. The
    /// seam is <c>guidLinks</c> on <see cref="Resolve"/>; nothing produces links yet.</item>
    /// <item>Inside ONE tree only: same persistent id and launch guids that do not
    /// conclusively differ (<see cref="VesselLaunchIdentity.RecordingsShareLaunch"/>, the rule
    /// the mission naming always used). Never across trees: pids are craft-baked.</item>
    /// <item>Inside ONE tree only: a re-pidded continuation (<see cref="MergeRepiddedContinuations"/>),
    /// the fallback for undocks recorded before dock-absorb rows exist.</item>
    /// </list>
    /// <para>EVA legs are kerbal identities and never part of a vessel. In the global
    /// <see cref="Resolve"/>, a tree's legs that carry ANOTHER mission's earlier launch (its
    /// dock partner, <see cref="MissionVesselNaming.TryResolveForeignOwner"/>) take part in the
    /// in-tree edges only among themselves and reach their owner through the guid edge, so a
    /// partner never merges into this tree's own ship. Pure: no Unity or KSP calls, no shared
    /// mutable state.</para>
    /// </summary>
    internal static class VesselIdentity
    {
        // Absorbs serialization noise only: a continuation must start after its vessel ended.
        private const double EarlierEpsilonSeconds = 1e-3;

        /// <summary>A recording that can be part of a vessel identity: has an id, is not an EVA kerbal.</summary>
        internal static bool IsVesselLeg(Recording r)
            => r != null && !string.IsNullOrEmpty(r.RecordingId) && string.IsNullOrEmpty(r.EvaCrewName);

        /// <summary>The leg's launch guid in normalized "N" form, or null when unknown.</summary>
        internal static string LegGuid(Recording r)
            => r == null ? null : VesselLaunchIdentity.NormalizeGuid(r.RecordedVesselGuid);

        /// <summary>Edge 1: both launch guids known and equal.</summary>
        internal static bool SharesGuid(Recording a, Recording b)
        {
            string ga = LegGuid(a), gb = LegGuid(b);
            return ga != null && gb != null && string.Equals(ga, gb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Edge 2: same non-empty chain id.</summary>
        internal static bool SharesChain(string chainA, string chainB)
            => !string.IsNullOrEmpty(chainA) && string.Equals(chainA, chainB, StringComparison.Ordinal);

        /// <summary>Edge 5 (callers restrict it to one tree): same pid, guids not conclusively different.</summary>
        internal static bool SharesInTreeLaunch(Recording a, Recording b)
            => VesselLaunchIdentity.RecordingsShareLaunch(a, b);

        /// <summary>
        /// Edge 3: every (parent, child) pair of a <see cref="BranchPointType.VesselSwitchContinuation"/>
        /// branch point in <paramref name="tree"/> with exactly one parent and exactly one child.
        /// </summary>
        internal static List<KeyValuePair<string, string>> SwitchContinuationPairs(RecordingTree tree)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            if (tree?.BranchPoints == null || tree.Recordings == null) return pairs;
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                BranchPoint bp = tree.BranchPoints[b];
                if (bp == null || bp.Type != BranchPointType.VesselSwitchContinuation
                    || string.IsNullOrEmpty(bp.Id))
                    continue;
                var parents = new HashSet<string>(StringComparer.Ordinal);
                if (bp.ParentRecordingIds != null)
                    foreach (string p in bp.ParentRecordingIds)
                        if (!string.IsNullOrEmpty(p)) parents.Add(p);
                string child = null;
                bool ambiguous = false;
                foreach (Recording r in tree.Recordings.Values)
                {
                    if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                    if (string.Equals(r.ChildBranchPointId, bp.Id, StringComparison.Ordinal))
                        parents.Add(r.RecordingId);
                    if (string.Equals(r.ParentBranchPointId, bp.Id, StringComparison.Ordinal))
                    {
                        if (child != null) ambiguous = true;
                        child = r.RecordingId;
                    }
                }
                if (ambiguous || child == null || parents.Count != 1) continue;
                foreach (string p in parents)
                    pairs.Add(new KeyValuePair<string, string>(p, child));
            }
            return pairs;
        }

        // ------------------------------------------------------------------ in-tree union

        /// <summary>
        /// Unions a subset of ONE tree's legs (<paramref name="legs"/>, indexed like
        /// <paramref name="parent"/>) through edges 1, 2, 3, 5 and 6. Returns the re-pidded
        /// continuations joined (edge 6). <paramref name="partPids"/> reads a recording's
        /// part pids (a caching wrapper of <see cref="PartPids"/> in per-frame callers).
        /// </summary>
        internal static int UnionInTreeLegs(
            RecordingTree tree, MissionStructure structure, List<MissionLeg> legs, int[] parent,
            Func<Recording, HashSet<uint>> partPids)
        {
            if (legs == null || parent == null || legs.Count == 0) return 0;
            if (partPids == null) partPids = PartPids;
            for (int i = 0; i < legs.Count; i++)
            {
                Recording ri = Rec(tree, legs[i].RecordingId);
                for (int j = i + 1; j < legs.Count; j++)
                {
                    if (SharesChain(legs[i].ChainId, legs[j].ChainId))
                    {
                        Union(parent, i, j);
                        continue;
                    }
                    Recording rj = Rec(tree, legs[j].RecordingId);
                    if (SharesInTreeLaunch(ri, rj) || SharesGuid(ri, rj))
                        Union(parent, i, j);
                }
            }

            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < legs.Count; i++) indexById[legs[i].RecordingId] = i;
            foreach (KeyValuePair<string, string> pair in SwitchContinuationPairs(tree))
            {
                if (indexById.TryGetValue(pair.Key, out int a) && indexById.TryGetValue(pair.Value, out int c))
                    Union(parent, a, c);
            }

            return structure != null
                ? MergeRepiddedContinuations(tree, structure, legs, parent, partPids)
                : 0;
        }

        /// <summary>
        /// Edge 6. Joins each re-pidded continuation to the vessel it continues, one hop at a
        /// time. A candidate is a pair (vessel V, leg L) where V holds L's NEAREST lineage
        /// ancestor among <paramref name="legs"/> (the walk up L's branch parents and chain
        /// predecessors passes through legs outside the subset - a partner's stack the ship
        /// docked into - and stops at the first subset legs), no leg of V runs past L's start,
        /// and L shares at least one part pid with V. Each round every vessel's candidates are
        /// weighed together and the best pair overall merges - most shared pids, then the
        /// later-ending vessel, then the ids - so the ship's real half wins over a smaller
        /// piece that split off the partner's stack a moment earlier. The round re-reads the
        /// current union-find roots, so a ship re-pidded twice (A -> A' -> A'') chains. The
        /// running test is the only guard: a vessel that carries on past a split is never
        /// continued by the piece that left it, and once V absorbs one half of an undock that
        /// half is running, so the other half cannot join V too. Each leg joins at most one
        /// vessel. In-tree only: craft-baked part pids collide across launches. Returns the
        /// merges made.
        /// </summary>
        internal static int MergeRepiddedContinuations(
            RecordingTree tree, MissionStructure structure, List<MissionLeg> legs, int[] parent,
            Func<Recording, HashSet<uint>> partPids)
        {
            if (legs.Count < 2 || structure == null) return 0;
            if (partPids == null) partPids = PartPids;
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < legs.Count; i++) indexById[legs[i].RecordingId] = i;
            var pidsByLeg = new HashSet<uint>[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                Recording r = Rec(tree, legs[i].RecordingId);
                pidsByLeg[i] = r != null ? partPids(r) : new HashSet<uint>();
            }
            // The nearest subset ancestors of each leg never change; only their roots do.
            var nearest = new List<int>[legs.Count];
            for (int i = 0; i < legs.Count; i++)
                nearest[i] = NearestSubsetAncestors(structure, legs[i], indexById);

            var joined = new HashSet<int>();
            int merges = 0;
            while (true)
            {
                Continuation best = default;
                bool found = false;
                for (int li = 0; li < legs.Count; li++)
                {
                    if (joined.Contains(li) || pidsByLeg[li].Count == 0) continue;
                    int legRoot = Find(parent, li);
                    var tried = new HashSet<int>();
                    foreach (int ai in nearest[li])
                    {
                        int vRoot = Find(parent, ai);
                        if (vRoot == legRoot || !tried.Add(vRoot)) continue;
                        if (!TryScore(legs, pidsByLeg, parent, vRoot, li,
                                out int shared, out double vesselEnd))
                            continue;
                        var c = new Continuation
                        {
                            Leg = li, Vessel = vRoot, Shared = shared, VesselEnd = vesselEnd,
                        };
                        if (!found || CompareContinuation(legs, c, best) < 0)
                        {
                            best = c;
                            found = true;
                        }
                    }
                }
                if (!found) break;
                Union(parent, best.Leg, best.Vessel);
                joined.Add(best.Leg);
                merges++;
            }
            return merges;
        }

        // The subset legs a leg L continues from directly: up its branch parents and chain
        // predecessors, through legs outside the subset, stopping at the first subset leg on
        // each path.
        private static List<int> NearestSubsetAncestors(
            MissionStructure structure, MissionLeg leg, Dictionary<string, int> indexById)
        {
            var result = new List<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { leg.RecordingId };
            var stack = new Stack<MissionLeg>();
            stack.Push(leg);
            while (stack.Count > 0)
            {
                MissionLeg cur = stack.Pop();
                var ups = new List<string>(cur.BranchParentIds);
                if (!string.IsNullOrEmpty(cur.SequencePrevId)) ups.Add(cur.SequencePrevId);
                foreach (string up in ups)
                {
                    if (string.IsNullOrEmpty(up) || !seen.Add(up)) continue;
                    if (indexById.TryGetValue(up, out int ai))
                    {
                        result.Add(ai);
                        continue;
                    }
                    if (structure.LegsById.TryGetValue(up, out MissionLeg upLeg) && upLeg != null)
                        stack.Push(upLeg);
                }
            }
            return result;
        }

        // Vessel vRoot can take leg li: none of its legs runs past li's start, and they share
        // at least one part pid with it.
        private static bool TryScore(List<MissionLeg> legs, HashSet<uint>[] pidsByLeg, int[] parent,
            int vRoot, int li, out int shared, out double vesselEnd)
        {
            shared = 0;
            vesselEnd = double.MinValue;
            for (int k = 0; k < legs.Count; k++)
            {
                if (Find(parent, k) != vRoot) continue;
                if (legs[k].EndUT > vesselEnd) vesselEnd = legs[k].EndUT;
                foreach (uint pid in pidsByLeg[k])
                    if (pidsByLeg[li].Contains(pid)) shared++;
            }
            return shared > 0 && vesselEnd <= legs[li].StartUT + EarlierEpsilonSeconds;
        }

        private static int CompareContinuation(List<MissionLeg> legs, Continuation a, Continuation b)
        {
            int c = b.Shared.CompareTo(a.Shared);
            if (c != 0) return c;
            c = b.VesselEnd.CompareTo(a.VesselEnd);
            if (c != 0) return c;
            c = string.CompareOrdinal(legs[a.Vessel].RecordingId, legs[b.Vessel].RecordingId);
            return c != 0 ? c : string.CompareOrdinal(legs[a.Leg].RecordingId, legs[b.Leg].RecordingId);
        }

        private struct Continuation
        {
            internal int Leg;
            internal int Vessel;
            internal int Shared;
            internal double VesselEnd;
        }

        /// <summary>A recording's part pids: its snapshot's PART nodes and its part events.
        /// Parses the snapshot, so a per-frame caller caches it.</summary>
        internal static HashSet<uint> PartPids(Recording rec)
        {
            var pids = new HashSet<uint>();
            if (rec == null) return pids;
            ConfigNode snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
            if (snapshot != null)
                foreach (uint pid in VesselSnapshotOps.CollectPartPersistentIds(snapshot))
                    if (pid != 0) pids.Add(pid);
            if (rec.PartEvents != null)
                for (int i = 0; i < rec.PartEvents.Count; i++)
                    if (rec.PartEvents[i].partPersistentId != 0)
                        pids.Add(rec.PartEvents[i].partPersistentId);
            return pids;
        }

        internal static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        internal static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        private static Recording Rec(RecordingTree tree, string id)
            => id != null && tree?.Recordings != null
               && tree.Recordings.TryGetValue(id, out Recording r) ? r : null;

        // ------------------------------------------------------------------ global resolver

        /// <summary>One physical vessel: its legs (by recording id) and every launch guid they carry.</summary>
        internal sealed class Identity
        {
            /// <summary>The smallest leg id (ordinal). Transient: valid for one resolution.</summary>
            internal string Key;
            internal readonly List<string> LegIds = new List<string>();
            internal readonly HashSet<string> Guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool HasGuid => Guids.Count > 0;
        }

        /// <summary>The identities of every vessel leg handed to <see cref="Resolve"/>.</summary>
        internal sealed class Resolution
        {
            internal readonly List<Identity> Identities = new List<Identity>();
            internal readonly Dictionary<string, Identity> ByRecordingId =
                new Dictionary<string, Identity>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Identity> ByGuid =
                new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Every Recording object carrying an id: a committed tree and the live
            /// copy-on-write clone of it share ids, and a name lands on both.</summary>
            internal readonly Dictionary<string, List<Recording>> CopiesById =
                new Dictionary<string, List<Recording>>(StringComparer.Ordinal);

            internal int Trees;
            internal int Legs;
            internal int EvaSkipped;
            internal int InTreeContinuations;
            internal int LinkEdges;

            internal bool TryGetIdentity(string recordingId, out Identity identity)
            {
                identity = null;
                return recordingId != null && ByRecordingId.TryGetValue(recordingId, out identity);
            }

            internal List<Recording> Copies(string recordingId)
                => recordingId != null && CopiesById.TryGetValue(recordingId, out List<Recording> list)
                    ? list : null;

            /// <summary>
            /// Live vessel -> identity (6.2): a positive launch-guid match against every guid
            /// any member carries. A bare pid never resolves, and a ghost map vessel is
            /// skipped (<paramref name="isGhostMapPid"/>, <c>GhostMapPresence.IsGhostMapVessel</c>).
            /// </summary>
            internal Identity ResolveLiveVessel(string liveGuid, uint livePid, Func<uint, bool> isGhostMapPid)
            {
                if (livePid != 0 && isGhostMapPid != null && isGhostMapPid(livePid)) return null;
                string g = VesselLaunchIdentity.NormalizeGuid(liveGuid);
                if (g == null) return null;
                return ByGuid.TryGetValue(g, out Identity id) ? id : null;
            }
        }

        /// <summary>
        /// Resolves every vessel leg of <paramref name="trees"/> (committed, pending, the live
        /// active tree; a tree id seen twice - the live clone of a committed tree - contributes
        /// the same keys) plus <paramref name="looseRecordings"/> (recordings no tree holds;
        /// they join through guid and chain only) into identities. <paramref name="guidLinks"/>
        /// is the edge-4 seam (re-keyed guid -> guid it continues); null in Phase 1.
        /// </summary>
        internal static Resolution Resolve(
            IEnumerable<RecordingTree> trees,
            IEnumerable<Recording> looseRecordings,
            IEnumerable<KeyValuePair<string, string>> guidLinks = null,
            Func<Recording, HashSet<uint>> partPids = null)
        {
            if (partPids == null) partPids = PartPids;
            var res = new Resolution();
            var nodeById = new Dictionary<string, int>(StringComparer.Ordinal);
            var ids = new List<string>();
            var reps = new List<Recording>();

            var treeList = new List<RecordingTree>();
            if (trees != null)
                foreach (RecordingTree t in trees)
                    if (t?.Recordings != null) treeList.Add(t);
            res.Trees = treeList.Count;

            Func<Recording, int> nodeOf = r =>
            {
                if (!nodeById.TryGetValue(r.RecordingId, out int n))
                {
                    n = ids.Count;
                    nodeById[r.RecordingId] = n;
                    ids.Add(r.RecordingId);
                    reps.Add(r);
                    res.CopiesById[r.RecordingId] = new List<Recording>(1);
                }
                List<Recording> copies = res.CopiesById[r.RecordingId];
                bool seen = false;
                for (int i = 0; i < copies.Count; i++)
                    if (ReferenceEquals(copies[i], r)) { seen = true; break; }
                if (!seen) copies.Add(r);
                return n;
            };

            for (int t = 0; t < treeList.Count; t++)
                foreach (Recording r in treeList[t].Recordings.Values)
                {
                    if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                    if (!IsVesselLeg(r)) { res.EvaSkipped++; continue; }
                    nodeOf(r);
                }
            if (looseRecordings != null)
                foreach (Recording r in looseRecordings)
                {
                    if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                    if (!IsVesselLeg(r)) { res.EvaSkipped++; continue; }
                    nodeOf(r);
                }

            // The node count is final once every recording is registered.
            var parent = new List<int>(ids.Count);
            for (int i = 0; i < ids.Count; i++) parent.Add(i);
            int[] uf = parent.ToArray();

            MissionVesselNaming.LaunchIndex launchIndex = MissionVesselNaming.LaunchIndex.Build(treeList);
            for (int t = 0; t < treeList.Count; t++)
                res.InTreeContinuations += UnionTree(treeList[t], launchIndex, nodeById, uf, partPids);

            // Edge 1 across everything: same known guid.
            var firstByGuid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int n = 0; n < reps.Count; n++)
            {
                foreach (Recording copy in res.CopiesById[ids[n]])
                {
                    string g = LegGuid(copy);
                    if (g == null) continue;
                    if (firstByGuid.TryGetValue(g, out int other)) Union(uf, n, other);
                    else firstByGuid[g] = n;
                }
            }

            // Edge 2 across everything (loose recordings carry chain ids too).
            var firstByChain = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int n = 0; n < reps.Count; n++)
            {
                string chain = reps[n].ChainId;
                if (string.IsNullOrEmpty(chain)) continue;
                if (firstByChain.TryGetValue(chain, out int other)) Union(uf, n, other);
                else firstByChain[chain] = n;
            }

            // Edge 4 seam (Phase 2b link rows): a re-keyed guid joins the guid it continues.
            if (guidLinks != null)
                foreach (KeyValuePair<string, string> link in guidLinks)
                {
                    string from = VesselLaunchIdentity.NormalizeGuid(link.Key);
                    string to = VesselLaunchIdentity.NormalizeGuid(link.Value);
                    if (from == null || to == null) continue;
                    if (firstByGuid.TryGetValue(from, out int a) && firstByGuid.TryGetValue(to, out int b))
                    {
                        Union(uf, a, b);
                        res.LinkEdges++;
                    }
                }

            var byRoot = new Dictionary<int, Identity>();
            for (int n = 0; n < ids.Count; n++)
            {
                int root = Find(uf, n);
                if (!byRoot.TryGetValue(root, out Identity identity))
                {
                    identity = new Identity();
                    byRoot[root] = identity;
                    res.Identities.Add(identity);
                }
                identity.LegIds.Add(ids[n]);
                foreach (Recording copy in res.CopiesById[ids[n]])
                {
                    string g = LegGuid(copy);
                    if (g != null) identity.Guids.Add(g);
                }
                res.ByRecordingId[ids[n]] = identity;
            }
            foreach (Identity identity in res.Identities)
            {
                identity.LegIds.Sort(StringComparer.Ordinal);
                identity.Key = identity.LegIds[0];
                foreach (string g in identity.Guids)
                    res.ByGuid[g] = identity;
            }
            res.Identities.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            res.Legs = ids.Count;
            return res;
        }

        // One tree's in-tree edges (3, 5, 6, and 1/2 locally) mapped onto the global nodes.
        // Own legs (this mission's) and partner legs (another mission's earlier launch) are
        // unioned separately so a partner never merges into the own ship through a pid;
        // recordings outside the structure (debris) union among themselves.
        private static int UnionTree(
            RecordingTree tree, MissionVesselNaming.LaunchIndex launchIndex,
            Dictionary<string, int> nodeById, int[] uf, Func<Recording, HashSet<uint>> partPids)
        {
            MissionStructure structure = MissionStructureBuilder.BuildForDisplay(tree);
            var roots = new List<Recording>();
            for (int i = 0; i < structure.RootLegIds.Count; i++)
            {
                Recording r = Rec(tree, structure.RootLegIds[i]);
                if (r != null) roots.Add(r);
            }

            var own = new List<MissionLeg>();
            var partners = new List<MissionLeg>();
            foreach (MissionLeg leg in structure.LegsById.Values)
            {
                if (leg == null || !string.IsNullOrEmpty(leg.EvaCrewName)) continue;
                Recording rec = Rec(tree, leg.RecordingId);
                if (rec == null || !nodeById.ContainsKey(leg.RecordingId)) continue;
                if (MissionVesselNaming.TryResolveForeignOwner(tree, rec, roots, launchIndex, out _))
                    partners.Add(leg);
                else
                    own.Add(leg);
            }
            own.Sort((a, b) => string.CompareOrdinal(a.RecordingId, b.RecordingId));
            partners.Sort((a, b) => string.CompareOrdinal(a.RecordingId, b.RecordingId));

            int continuations = UnionSubsetIntoGlobal(tree, structure, own, nodeById, uf, partPids, true);
            UnionSubsetIntoGlobal(tree, structure, partners, nodeById, uf, partPids, false);

            // Recordings the structure leaves out (debris): edges 2 and 5 among themselves.
            var rest = new List<MissionLeg>();
            foreach (Recording r in tree.Recordings.Values)
            {
                if (!IsVesselLeg(r) || structure.LegsById.ContainsKey(r.RecordingId)
                    || !nodeById.ContainsKey(r.RecordingId))
                    continue;
                rest.Add(new MissionLeg { RecordingId = r.RecordingId, ChainId = r.ChainId });
            }
            rest.Sort((a, b) => string.CompareOrdinal(a.RecordingId, b.RecordingId));
            UnionSubsetIntoGlobal(tree, null, rest, nodeById, uf, partPids, false);

            // Edge 3 for every pair, whichever subset its ends fall in.
            foreach (KeyValuePair<string, string> pair in SwitchContinuationPairs(tree))
            {
                if (nodeById.TryGetValue(pair.Key, out int a) && nodeById.TryGetValue(pair.Value, out int c))
                    Union(uf, a, c);
            }
            return continuations;
        }

        private static int UnionSubsetIntoGlobal(
            RecordingTree tree, MissionStructure structure, List<MissionLeg> legs,
            Dictionary<string, int> nodeById, int[] uf, Func<Recording, HashSet<uint>> partPids,
            bool continuations)
        {
            if (legs.Count == 0) return 0;
            var local = new int[legs.Count];
            for (int i = 0; i < local.Length; i++) local[i] = i;
            int merges = UnionInTreeLegs(tree, continuations ? structure : null, legs, local, partPids);
            for (int i = 0; i < legs.Count; i++)
            {
                int root = Find(local, i);
                if (root == i) continue;
                Union(uf, nodeById[legs[i].RecordingId], nodeById[legs[root].RecordingId]);
            }
            return merges;
        }
    }
}
