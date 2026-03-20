using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Walks all committed recording trees to compute ghost chains — the set of
    /// pre-existing vessels claimed by ghosting-trigger interactions. Pure static
    /// methods, fully testable without Unity/KSP.
    /// </summary>
    internal static class GhostChainWalker
    {
        private const string Tag = "ChainWalker";

        /// <summary>
        /// Scans all committed trees and builds ghost chains for every pre-existing
        /// vessel claimed by a ghosting-trigger interaction.
        /// Returns dict of vessel PID -> GhostChain.
        /// </summary>
        internal static Dictionary<uint, GhostChain> ComputeAllGhostChains(
            List<RecordingTree> committedTrees, double rewindUT)
        {
            var ic = CultureInfo.InvariantCulture;

            if (committedTrees == null || committedTrees.Count == 0)
            {
                ParsekLog.Verbose(Tag, "No committed trees — returning empty chain map");
                return new Dictionary<uint, GhostChain>();
            }

            // Step 1: Collect claims per vessel PID
            var claimsByPid = new Dictionary<uint, List<ChainLink>>();

            // Step 2: Scan BranchPoints for claiming events
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];
                ScanBranchPointClaims(tree, claimsByPid);
                ScanBackgroundEventClaims(tree, claimsByPid);
            }

            if (claimsByPid.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "No claims found in {0} committed trees", committedTrees.Count));
                return new Dictionary<uint, GhostChain>();
            }

            // Step 4: Build chains from sorted claims
            var chains = new Dictionary<uint, GhostChain>();
            foreach (var kvp in claimsByPid)
            {
                uint pid = kvp.Key;
                var links = kvp.Value;
                links.Sort((a, b) => a.ut.CompareTo(b.ut));

                var chain = new GhostChain
                {
                    OriginalVesselPid = pid,
                    Links = links,
                    GhostStartUT = rewindUT
                };

                chains[pid] = chain;
            }

            // Step 5 + 6: Find chain tips and cross-tree linking
            ResolveTipsAndCrossTreeLinks(chains, committedTrees);

            // Step 7: Set IsTerminated and log each chain
            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                ResolveTermination(chain, committedTrees);

                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Chain built: vessel={0} links={1} tip={2} spawnUT={3:F1} terminated={4}",
                        chain.OriginalVesselPid, chain.Links.Count,
                        chain.TipRecordingId ?? "null", chain.SpawnUT, chain.IsTerminated));
            }

            return chains;
        }

        /// <summary>
        /// Returns true if the given recording is an intermediate chain link
        /// (not the final tip) — its spawn should be suppressed.
        /// </summary>
        internal static bool IsIntermediateChainLink(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0 || rec == null)
                return false;

            // Check if rec's RecordingId matches any non-final link in any chain
            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                for (int i = 0; i < chain.Links.Count; i++)
                {
                    if (chain.Links[i].recordingId == rec.RecordingId)
                    {
                        // If this is the last link AND it is the tip, not intermediate
                        if (i == chain.Links.Count - 1 && chain.TipRecordingId == rec.RecordingId)
                            return false;

                        // Non-final link — intermediate
                        return true;
                    }
                }

                // Check if rec's VesselPersistentId matches a chain's OriginalVesselPid
                // and the chain's SpawnUT > rec.EndUT (a later recording extends through this vessel)
                if (rec.VesselPersistentId != 0
                    && rec.VesselPersistentId == chain.OriginalVesselPid
                    && chain.SpawnUT > rec.EndUT
                    && chain.TipRecordingId != rec.RecordingId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the ghost chain that claims the given vessel PID.
        /// Returns null if not claimed.
        /// </summary>
        internal static GhostChain FindChainForVessel(
            Dictionary<uint, GhostChain> chains, uint vesselPid)
        {
            if (chains == null)
                return null;

            GhostChain chain;
            if (chains.TryGetValue(vesselPid, out chain))
                return chain;

            return null;
        }

        #region Private Helpers

        /// <summary>
        /// Scans a tree's BranchPoints for claiming events (Dock, Board, Undock, EVA, JointBreak)
        /// with a non-zero TargetVesselPersistentId.
        /// </summary>
        private static void ScanBranchPointClaims(
            RecordingTree tree, Dictionary<uint, List<ChainLink>> claimsByPid)
        {
            var ic = CultureInfo.InvariantCulture;

            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];

                if (!GhostingTriggerClassifier.IsClaimingBranchPoint(bp.Type))
                    continue;

                if (bp.TargetVesselPersistentId == 0)
                    continue;

                // Determine interaction type and claiming recording
                string interactionType;
                string claimingRecordingId;

                if (bp.Type == BranchPointType.Dock || bp.Type == BranchPointType.Board)
                {
                    interactionType = "MERGE";
                    claimingRecordingId = bp.ParentRecordingIds.Count > 0
                        ? bp.ParentRecordingIds[0] : null;
                }
                else
                {
                    // Undock, EVA, JointBreak
                    interactionType = "SPLIT";
                    claimingRecordingId = bp.ChildRecordingIds.Count > 0
                        ? bp.ChildRecordingIds[0] : null;
                }

                if (claimingRecordingId == null)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "BranchPoint {0} has no {1} recording IDs — skipping claim for PID={2}",
                            bp.Id, interactionType == "MERGE" ? "parent" : "child",
                            bp.TargetVesselPersistentId));
                    continue;
                }

                var link = new ChainLink
                {
                    recordingId = claimingRecordingId,
                    treeId = tree.Id,
                    branchPointId = bp.Id,
                    ut = bp.UT,
                    interactionType = interactionType
                };

                uint pid = bp.TargetVesselPersistentId;
                List<ChainLink> list;
                if (!claimsByPid.TryGetValue(pid, out list))
                {
                    list = new List<ChainLink>();
                    claimsByPid[pid] = list;
                }
                list.Add(link);

                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Vessel PID={0} claimed by tree={1} via {2} at UT={3:F1}",
                        pid, tree.Id, interactionType, bp.UT));
            }
        }

        /// <summary>
        /// Scans a tree's recordings for background vessel recordings that contain
        /// ghosting-trigger events. A recording is a "background vessel" recording
        /// if its VesselPersistentId differs from the tree's root lineage vessel PID.
        /// </summary>
        private static void ScanBackgroundEventClaims(
            RecordingTree tree, Dictionary<uint, List<ChainLink>> claimsByPid)
        {
            var ic = CultureInfo.InvariantCulture;

            // Find root lineage vessel PIDs by tracing from the root recording
            var rootLineagePids = GetRootLineageVesselPids(tree);

            foreach (var kvp in tree.Recordings)
            {
                var rec = kvp.Value;
                if (rec.VesselPersistentId == 0)
                    continue;

                // Skip recordings that belong to the tree's own lineage
                if (rootLineagePids.Contains(rec.VesselPersistentId))
                    continue;

                if (!GhostingTriggerClassifier.HasGhostingTriggerEvents(rec))
                    continue;

                var link = new ChainLink
                {
                    recordingId = rec.RecordingId,
                    treeId = tree.Id,
                    branchPointId = null,
                    ut = rec.StartUT,
                    interactionType = "BACKGROUND_EVENT"
                };

                uint pid = rec.VesselPersistentId;
                List<ChainLink> list;
                if (!claimsByPid.TryGetValue(pid, out list))
                {
                    list = new List<ChainLink>();
                    claimsByPid[pid] = list;
                }
                list.Add(link);

                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Vessel PID={0} claimed by tree={1} via BACKGROUND_EVENT at UT={2:F1}",
                        pid, tree.Id, rec.StartUT));
            }
        }

        /// <summary>
        /// Collects the set of VesselPersistentIds for recordings in the tree's root
        /// lineage — tracing from the root recording through ChildBranchPointId chains.
        /// Any recording with a VesselPersistentId NOT in this set is a background recording.
        /// </summary>
        private static HashSet<uint> GetRootLineageVesselPids(RecordingTree tree)
        {
            var pids = new HashSet<uint>();

            // Find root recording
            Recording rootRec;
            if (!string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.TryGetValue(tree.RootRecordingId, out rootRec))
            {
                TraceLineagePids(tree, rootRec, pids, new HashSet<string>());
            }

            return pids;
        }

        /// <summary>
        /// Recursively traces from a recording through its ChildBranchPointId to collect
        /// all VesselPersistentIds in the lineage. Tracks visited recording IDs to avoid
        /// infinite loops.
        /// </summary>
        private static void TraceLineagePids(
            RecordingTree tree, Recording rec,
            HashSet<uint> pids, HashSet<string> visited)
        {
            if (rec == null || visited.Contains(rec.RecordingId))
                return;

            visited.Add(rec.RecordingId);

            if (rec.VesselPersistentId != 0)
                pids.Add(rec.VesselPersistentId);

            // Follow ChildBranchPointId to find child recordings
            if (rec.ChildBranchPointId != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    if (tree.BranchPoints[i].Id == rec.ChildBranchPointId)
                    {
                        var bp = tree.BranchPoints[i];
                        for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                        {
                            Recording childRec;
                            if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out childRec))
                                TraceLineagePids(tree, childRec, pids, visited);
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// For each chain, finds the tip recording by walking from the last link's recording
        /// to its leaf. Also performs cross-tree PID-based linking: if the tip recording's
        /// VesselPersistentId is claimed by another chain, merges the chains.
        /// </summary>
        private static void ResolveTipsAndCrossTreeLinks(
            Dictionary<uint, GhostChain> chains, List<RecordingTree> committedTrees)
        {
            var ic = CultureInfo.InvariantCulture;

            // First pass: resolve tip for each chain
            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                ResolveChainTip(chain, committedTrees);
            }

            // Second pass: cross-tree linking
            // If a chain's tip recording has a VesselPersistentId that matches another chain's
            // OriginalVesselPid, merge the second chain's links into the first chain.
            var visited = new HashSet<uint>();
            var pidsToMerge = new List<uint>();

            foreach (var kvp in chains)
            {
                uint originPid = kvp.Key;
                var chain = kvp.Value;

                if (visited.Contains(originPid))
                    continue;
                visited.Add(originPid);

                // Walk cross-tree links
                var current = chain;
                var chainVisited = new HashSet<uint>();
                chainVisited.Add(originPid);

                while (current.TipRecordingId != null)
                {
                    // Find tip recording's VesselPersistentId
                    uint tipVesselPid = FindRecordingVesselPid(
                        current.TipRecordingId, current.TipTreeId, committedTrees);

                    if (tipVesselPid == 0 || tipVesselPid == originPid)
                        break;

                    // Check if the tip vessel PID is claimed by another chain
                    GhostChain linkedChain;
                    if (!chains.TryGetValue(tipVesselPid, out linkedChain))
                        break;

                    if (chainVisited.Contains(tipVesselPid))
                    {
                        ParsekLog.Warn(Tag,
                            string.Format(ic,
                                "Cross-tree link cycle detected: vessel={0} already visited — breaking",
                                tipVesselPid));
                        break;
                    }

                    chainVisited.Add(tipVesselPid);
                    visited.Add(tipVesselPid);

                    // Merge linked chain's links into the current chain
                    for (int i = 0; i < linkedChain.Links.Count; i++)
                        chain.Links.Add(linkedChain.Links[i]);

                    // Re-sort after merge
                    chain.Links.Sort((a, b) => a.ut.CompareTo(b.ut));

                    // Update tip from merged chain
                    chain.TipRecordingId = linkedChain.TipRecordingId;
                    chain.TipTreeId = linkedChain.TipTreeId;
                    chain.SpawnUT = linkedChain.SpawnUT;

                    pidsToMerge.Add(tipVesselPid);

                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "Cross-tree link: vessel={0} → merged with chain for vessel={1}, new tip={2}",
                            originPid, tipVesselPid, chain.TipRecordingId));

                    current = linkedChain;
                }
            }

            // Remove merged chains (they were absorbed into the originating chain)
            for (int i = 0; i < pidsToMerge.Count; i++)
            {
                chains.Remove(pidsToMerge[i]);
            }
        }

        /// <summary>
        /// Resolves the chain tip by finding the last link's recording and walking
        /// from it through ChildBranchPointId to find the leaf.
        /// </summary>
        private static void ResolveChainTip(
            GhostChain chain, List<RecordingTree> committedTrees)
        {
            if (chain.Links.Count == 0)
                return;

            // Start from the last link
            var lastLink = chain.Links[chain.Links.Count - 1];
            string recId = lastLink.recordingId;
            string treeId = lastLink.treeId;

            // Find the recording in its tree
            Recording rec = FindRecording(recId, treeId, committedTrees);
            if (rec == null)
            {
                ParsekLog.Warn(Tag,
                    string.Format(CultureInfo.InvariantCulture,
                        "Chain tip: recording {0} not found in tree {1}", recId, treeId));
                chain.TipRecordingId = recId;
                chain.TipTreeId = treeId;
                return;
            }

            // Walk to the leaf through ChildBranchPointId
            RecordingTree tree = FindTree(treeId, committedTrees);
            Recording leaf = WalkToLeaf(rec, tree);

            chain.TipRecordingId = leaf.RecordingId;
            chain.TipTreeId = treeId;
            chain.SpawnUT = leaf.EndUT;
        }

        /// <summary>
        /// Walks from a recording through ChildBranchPointId to find the leaf recording
        /// (one with no ChildBranchPointId).
        /// </summary>
        private static Recording WalkToLeaf(Recording rec, RecordingTree tree)
        {
            if (tree == null)
                return rec;

            var visited = new HashSet<string>();
            var current = rec;

            while (current.ChildBranchPointId != null && !visited.Contains(current.RecordingId))
            {
                visited.Add(current.RecordingId);

                BranchPoint bp = null;
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    if (tree.BranchPoints[i].Id == current.ChildBranchPointId)
                    {
                        bp = tree.BranchPoints[i];
                        break;
                    }
                }

                if (bp == null || bp.ChildRecordingIds.Count == 0)
                    break;

                // Follow first child recording
                Recording child;
                if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[0], out child))
                    current = child;
                else
                    break;
            }

            return current;
        }

        /// <summary>
        /// Sets IsTerminated on a chain if the tip recording has a non-spawnable terminal state.
        /// </summary>
        private static void ResolveTermination(
            GhostChain chain, List<RecordingTree> committedTrees)
        {
            if (chain.TipRecordingId == null)
                return;

            Recording tipRec = FindRecording(
                chain.TipRecordingId, chain.TipTreeId, committedTrees);

            if (tipRec == null)
                return;

            if (tipRec.TerminalStateValue.HasValue)
            {
                var ts = tipRec.TerminalStateValue.Value;
                if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered)
                    chain.IsTerminated = true;
            }
        }

        /// <summary>
        /// Finds a recording by ID, searching within the specified tree first,
        /// then falling back to all trees.
        /// </summary>
        private static Recording FindRecording(
            string recordingId, string treeId, List<RecordingTree> committedTrees)
        {
            // Try specified tree first
            if (treeId != null)
            {
                RecordingTree tree = FindTree(treeId, committedTrees);
                if (tree != null)
                {
                    Recording rec;
                    if (tree.Recordings.TryGetValue(recordingId, out rec))
                        return rec;
                }
            }

            // Fallback: search all trees
            for (int t = 0; t < committedTrees.Count; t++)
            {
                Recording rec;
                if (committedTrees[t].Recordings.TryGetValue(recordingId, out rec))
                    return rec;
            }

            return null;
        }

        /// <summary>
        /// Finds a tree by ID.
        /// </summary>
        private static RecordingTree FindTree(
            string treeId, List<RecordingTree> committedTrees)
        {
            for (int t = 0; t < committedTrees.Count; t++)
            {
                if (committedTrees[t].Id == treeId)
                    return committedTrees[t];
            }
            return null;
        }

        /// <summary>
        /// Looks up a recording's VesselPersistentId by recording ID and tree ID.
        /// </summary>
        private static uint FindRecordingVesselPid(
            string recordingId, string treeId, List<RecordingTree> committedTrees)
        {
            Recording rec = FindRecording(recordingId, treeId, committedTrees);
            return rec != null ? rec.VesselPersistentId : 0;
        }

        #endregion
    }
}
