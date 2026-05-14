using System;
using System.Collections.Generic;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 5 designated-primary selector (design doc §10.1). Pure function
    /// over (recordings, marker, anchor map, recording metadata) → a
    /// <c>peerRecordingId → primaryRecordingId</c> dictionary. Invoked once
    /// per <see cref="RenderSessionState.RebuildFromMarker"/> after the
    /// <see cref="AnchorPropagator"/> writes its DAG-resolved entries; the
    /// result lives on <see cref="RenderSessionState"/> until the next
    /// rebuild / clear.
    ///
    /// <para>
    /// Selection rules (§10.1):
    /// <list type="number">
    ///   <item>Live wins. Recording carrying any
    ///   <see cref="AnchorSource.LiveSeparation"/> anchor (or matching
    ///   <see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/>) is primary.</item>
    ///   <item>Closest-to-live in DAG ancestry.</item>
    ///   <item>Earlier <see cref="Recording.StartUT"/> (then
    ///   <see cref="RecordingTree.TreeOrder"/>) wins.</item>
    ///   <item>Higher <see cref="TrackSection.sampleRateHz"/> at the
    ///   overlap-window midpoint wins.</item>
    ///   <item>HR-3 deterministic tiebreaker:
    ///   <c>string.CompareOrdinal(idA, idB) &lt; 0 → A wins</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class CoBubblePrimarySelector
    {
        /// <summary>
        /// For every co-bubble trace stored in
        /// <see cref="SectionAnnotationStore"/>, decide which side is the
        /// primary. Returns a map from peer recording id → primary recording
        /// id. A recording can be a primary for one peer and a peer of
        /// another at the same time — multi-tier formations are resolved
        /// pairwise.
        /// </summary>
        internal static Dictionary<string, string> Resolve(
            IReadOnlyList<Recording> recordings,
            ReFlySessionMarker marker)
        {
            return Resolve(recordings, marker, out _);
        }

        /// <summary>
        /// Same as <see cref="Resolve(IReadOnlyList{Recording}, ReFlySessionMarker)"/>
        /// but also returns the deciding rule index per (peer → primary)
        /// pair so <see cref="RenderSessionState.NotifyCoBubblePrimarySelection"/>
        /// can include the §10.1 rule index in the Pipeline-CoBubble Info
        /// log line (P2-E). Rule indices are 1-based and match the §10.1
        /// numbering: 1=live, 2=DAG-hops, 3=earlier-StartUT,
        /// 4=higher-sample-rate, 5=ordinal-id.
        /// </summary>
        internal static Dictionary<string, string> Resolve(
            IReadOnlyList<Recording> recordings,
            ReFlySessionMarker marker,
            out Dictionary<string, int> rulesByPeer)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            rulesByPeer = new Dictionary<string, int>(StringComparer.Ordinal);

            // P2-F: gate the entire selector on the useCoBubbleBlend flag.
            // Without this gate, a save with stored traces loaded by a
            // user who has the flag off would still get primaries assigned —
            // IsPrimary would then return true for those recordings and
            // kick them into the recursion-guard branch. With the gate the
            // primary map stays empty and the standalone Stages 1+2+3+4
            // path renders the recording cleanly. The Verbose log makes the
            // skip visible (HR-9).
            if (!SmoothingPipeline.ResolveUseCoBubbleBlend())
            {
                ParsekLog.Verbose("Pipeline-CoBubble",
                    "flag-off-skip-primary-resolve: useCoBubbleBlend=false");
                return result;
            }

            if (recordings == null || recordings.Count == 0) return result;

            // Index recordings by id, find which ones have LiveSeparation
            // anchors (any section), and compute hop counts to a live-anchored
            // recording via DAG ancestry through CommittedTrees.
            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                byId[r.RecordingId] = r;
            }

            string activeReFlyId = marker?.ActiveReFlyRecordingId;
            var liveAnchored = ComputeLiveAnchoredSet(recordings, activeReFlyId);
            var hopCounts = ComputeHopCountsToLive(byId, liveAnchored);

            // Walk the trace map: enumerate every (recording, peer) pair
            // that has a stored trace. The traces map is symmetric (both
            // sides hold their own copy), so we deduplicate by (sortedA,
            // sortedB) when populating the result.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                if (!SectionAnnotationStore.TryGetCoBubbleTraces(r.RecordingId, out var traces) || traces == null) continue;
                for (int t = 0; t < traces.Count; t++)
                {
                    CoBubbleOffsetTrace tr = traces[t];
                    if (tr == null || string.IsNullOrEmpty(tr.PeerRecordingId)) continue;
                    string idA = r.RecordingId;
                    string idB = tr.PeerRecordingId;
                    string pairKey = string.CompareOrdinal(idA, idB) < 0 ? idA + "|" + idB : idB + "|" + idA;
                    if (!seen.Add(pairKey)) continue;

                    if (!byId.TryGetValue(idA, out Recording recA)) continue;
                    if (!byId.TryGetValue(idB, out Recording recB)) continue;
                    string primaryId = SelectPrimaryForPair(recA, recB, liveAnchored, hopCounts, tr,
                        out int ruleIndex);

                    string peerId = string.Equals(primaryId, idA, StringComparison.Ordinal) ? idB : idA;
                    result[peerId] = primaryId;
                    rulesByPeer[peerId] = ruleIndex;
                }
            }
            return result;
        }

        private static HashSet<string> ComputeLiveAnchoredSet(
            IReadOnlyList<Recording> recordings, string activeReFlyId)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(activeReFlyId)) live.Add(activeReFlyId);
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                if (r.TrackSections == null) continue;
                for (int s = 0; s < r.TrackSections.Count; s++)
                {
                    if (RenderSessionState.TryLookup(r.RecordingId, s, AnchorSide.Start, out AnchorCorrection ac)
                        && ac.Source == AnchorSource.LiveSeparation)
                    {
                        live.Add(r.RecordingId);
                        break;
                    }
                }
            }
            return live;
        }

        private static Dictionary<string, int> ComputeHopCountsToLive(
            Dictionary<string, Recording> byId, HashSet<string> liveAnchored)
        {
            // BFS from every live-anchored recording through DAG edges
            // (BranchPoints + chain continuity). Distance for non-reachable
            // recordings stays absent (treated as int.MaxValue).
            var dist = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (string id in liveAnchored)
            {
                dist[id] = 0;
                queue.Enqueue(id);
            }

            // Build adjacency: for each tree's BranchPoints, parent <-> children;
            // for chain id groups, consecutive members.
            var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void AddEdge(string a, string b)
            {
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
                if (!adj.TryGetValue(a, out var listA)) { listA = new List<string>(); adj[a] = listA; }
                listA.Add(b);
                if (!adj.TryGetValue(b, out var listB)) { listB = new List<string>(); adj[b] = listB; }
                listB.Add(a);
            }
            try
            {
                List<RecordingTree> trees = RecordingStore.CommittedTrees;
                if (trees != null)
                {
                    for (int t = 0; t < trees.Count; t++)
                    {
                        RecordingTree tree = trees[t];
                        if (tree == null || tree.BranchPoints == null) continue;
                        for (int b = 0; b < tree.BranchPoints.Count; b++)
                        {
                            BranchPoint bp = tree.BranchPoints[b];
                            if (bp == null) continue;
                            if (bp.ParentRecordingIds == null || bp.ChildRecordingIds == null) continue;
                            for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                                    AddEdge(bp.ParentRecordingIds[p], bp.ChildRecordingIds[c]);
                        }
                    }
                }
            }
            catch
            {
                // Mid-load mutation — skip tree-edge contribution, fall
                // through to chain-only adjacency.
            }
            // Chain edges: group recordings by ChainId, consecutive members.
            var chainGroups = new Dictionary<string, List<Recording>>(StringComparer.Ordinal);
            foreach (var kv in byId)
            {
                Recording r = kv.Value;
                if (r == null || string.IsNullOrEmpty(r.ChainId)) continue;
                if (r.ChainIndex < 0) continue;
                if (!chainGroups.TryGetValue(r.ChainId, out var list))
                {
                    list = new List<Recording>();
                    chainGroups[r.ChainId] = list;
                }
                list.Add(r);
            }
            foreach (var kv in chainGroups)
            {
                var members = kv.Value;
                members.Sort((x, y) => x.ChainIndex.CompareTo(y.ChainIndex));
                for (int i = 0; i + 1 < members.Count; i++)
                    AddEdge(members[i].RecordingId, members[i + 1].RecordingId);
            }

            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                int curDist = dist[cur];
                if (!adj.TryGetValue(cur, out var nbrs)) continue;
                for (int i = 0; i < nbrs.Count; i++)
                {
                    string n = nbrs[i];
                    if (dist.ContainsKey(n)) continue;
                    dist[n] = curDist + 1;
                    queue.Enqueue(n);
                }
            }
            return dist;
        }

        internal static string SelectPrimaryForPair(
            Recording a, Recording b,
            HashSet<string> liveAnchored,
            Dictionary<string, int> hopCounts,
            CoBubbleOffsetTrace trace,
            out int ruleIndex)
        {
            // Rule 1: live wins (always).
            bool aLive = liveAnchored.Contains(a.RecordingId);
            bool bLive = liveAnchored.Contains(b.RecordingId);
            if (aLive && !bLive) { ruleIndex = 1; return a.RecordingId; }
            if (bLive && !aLive) { ruleIndex = 1; return b.RecordingId; }

            // Rule 2: closest-to-live in DAG ancestry.
            int aHop = hopCounts.TryGetValue(a.RecordingId, out int v1) ? v1 : int.MaxValue;
            int bHop = hopCounts.TryGetValue(b.RecordingId, out int v2) ? v2 : int.MaxValue;
            if (aHop < bHop) { ruleIndex = 2; return a.RecordingId; }
            if (bHop < aHop) { ruleIndex = 2; return b.RecordingId; }

            // Rule 3: earlier StartUT.
            if (a.StartUT < b.StartUT) { ruleIndex = 3; return a.RecordingId; }
            if (b.StartUT < a.StartUT) { ruleIndex = 3; return b.RecordingId; }

            // Rule 4: higher sample rate at overlap midpoint.
            double midUT = trace != null ? 0.5 * (trace.StartUT + trace.EndUT) : 0.0;
            float aRate = ResolveSampleRateAtUT(a, midUT);
            float bRate = ResolveSampleRateAtUT(b, midUT);
            if (aRate > bRate) { ruleIndex = 4; return a.RecordingId; }
            if (bRate > aRate) { ruleIndex = 4; return b.RecordingId; }

            // Rule 5: HR-3 deterministic tiebreaker.
            ruleIndex = 5;
            return string.CompareOrdinal(a.RecordingId, b.RecordingId) < 0
                ? a.RecordingId : b.RecordingId;
        }

        private static float ResolveSampleRateAtUT(Recording rec, double ut)
        {
            if (rec == null || rec.TrackSections == null) return 0f;
            int idx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, ut);
            if (idx < 0 || idx >= rec.TrackSections.Count) return 0f;
            return rec.TrackSections[idx].sampleRateHz;
        }
    }
}
