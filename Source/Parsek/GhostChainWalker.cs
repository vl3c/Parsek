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
                ParsekLog.VerboseOnChange(Tag,
                    identity: "claims-summary",
                    stateKey: "no-trees",
                    message: "No committed trees — returning empty chain map");
                return new Dictionary<uint, GhostChain>();
            }

            // Step 1: Collect claims per vessel PID
            var claimsByPid = new Dictionary<uint, List<ChainLink>>();

            // Step 2: Scan BranchPoints for claiming events
            int skippedTerminated = 0;
            int skippedSessionSuppressed = 0;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];

                // #174: Skip trees where all leaf recordings are terminated — no ghost
                // can ever be produced from a fully-terminated tree.
                if (IsTreeFullyTerminated(tree))
                {
                    skippedTerminated++;
                    continue;
                }

                ScanBranchPointClaims(tree, claimsByPid, ref skippedSessionSuppressed);
                ScanBackgroundEventClaims(tree, claimsByPid, ref skippedSessionSuppressed);
            }

            if (skippedTerminated > 0)
            {
                ParsekLog.VerboseOnChange(Tag,
                    identity: "skipped-terminated",
                    stateKey: skippedTerminated.ToString(ic),
                    message: string.Format(ic,
                        "Skipped {0} fully-terminated tree(s)", skippedTerminated));
            }

            // Phase 7 of Rewind-to-Staging (design §3.3): during an active re-fly
            // session, recordings in the SessionSuppressedSubtree must not claim
            // chain tips — the stripped siblings from the RP are no longer in
            // ERS from the walker's perspective.
            if (skippedSessionSuppressed > 0)
            {
                ParsekLog.VerboseOnChange(Tag,
                    identity: "skipped-session-suppressed",
                    stateKey: skippedSessionSuppressed.ToString(ic),
                    message: string.Format(ic,
                        "Skipped {0} claim(s) from session-suppressed recording(s)",
                        skippedSessionSuppressed));
            }

            if (claimsByPid.Count == 0)
            {
                ParsekLog.VerboseOnChange(Tag,
                    identity: "claims-summary",
                    stateKey: string.Format(ic, "no-claims|{0}", committedTrees.Count),
                    message: string.Format(ic,
                        "No claims found in {0} committed trees", committedTrees.Count));
                return new Dictionary<uint, GhostChain>();
            }

            ParsekLog.VerboseOnChange(Tag,
                identity: "claims-summary",
                stateKey: string.Format(ic, "found|{0}|{1}",
                    claimsByPid.Count, committedTrees.Count),
                message: string.Format(ic, "Found claims for {0} vessel(s) across {1} committed trees",
                    claimsByPid.Count, committedTrees.Count));

            // Step 4: Build chains from sorted claims
            var chains = new Dictionary<uint, GhostChain>();
            foreach (var kvp in claimsByPid)
            {
                uint pid = kvp.Key;
                var links = new List<ChainLink>(kvp.Value);
                links.Sort(CompareChainLinks);
                if (links.Count == 0)
                    continue;

                var chain = new GhostChain
                {
                    OriginalVesselPid = pid,
                    GhostStartUT = links[0].ut
                };
                chain.Links.AddRange(links);

                chains[pid] = chain;
            }

            // Step 5 + 6: Find chain tips and cross-tree linking
            ResolveTipsAndCrossTreeLinks(chains, committedTrees);

            // Step 7: Set IsTerminated and log each chain
            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                ResolveTermination(chain, committedTrees);

                ParsekLog.VerboseOnChange(Tag,
                    identity: string.Format(ic, "chain|{0}", chain.OriginalVesselPid),
                    stateKey: string.Format(ic, "{0}|{1}|{2:F1}|{3}",
                        chain.Links.Count,
                        chain.TipRecordingId ?? "null", chain.SpawnUT, chain.IsTerminated),
                    message: string.Format(ic,
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
            var ic = CultureInfo.InvariantCulture;

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

                        return true;
                    }
                }

                // Check if rec's VesselPersistentId matches a chain's OriginalVesselPid
                // and the chain's SpawnUT > rec.EndUT (a later recording extends through this vessel)
                if (rec.VesselPersistentId != 0
                    && rec.VesselPersistentId == chain.OriginalVesselPid
                    && chain.SpawnUT > rec.EndUT
                    && chain.TipRecordingId != rec.RecordingId)
                    return true;
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
            RecordingTree tree, Dictionary<uint, List<ChainLink>> claimsByPid,
            ref int skippedSessionSuppressed)
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

                // Phase 7 of Rewind-to-Staging (design §3.3): the claiming
                // recording sits in the stripped sibling subtree during an
                // active re-fly. Skip the claim so the walker doesn't pin a
                // chain tip on a recording that's not playing.
                if (SessionSuppressionState.IsSuppressed(claimingRecordingId))
                {
                    skippedSessionSuppressed++;
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

                ParsekLog.VerboseOnChange(Tag,
                    identity: string.Format(ic, "claim|{0}|{1}|{2}", pid, tree.Id, bp.Id),
                    stateKey: string.Format(ic, "{0}|{1:F1}", interactionType, bp.UT),
                    message: string.Format(ic,
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
            RecordingTree tree, Dictionary<uint, List<ChainLink>> claimsByPid,
            ref int skippedSessionSuppressed)
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

                // Phase 7 of Rewind-to-Staging (design §3.3): suppress claims
                // from recordings inside the active session's closure so the
                // stripped siblings cannot reclaim a chain tip.
                if (SessionSuppressionState.IsSuppressed(rec.RecordingId))
                {
                    skippedSessionSuppressed++;
                    continue;
                }

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

                ParsekLog.VerboseOnChange(Tag,
                    identity: string.Format(ic, "claim-bg|{0}|{1}|{2}",
                        pid, tree.Id, rec.RecordingId),
                    stateKey: string.Format(ic, "{0:F1}", rec.StartUT),
                    message: string.Format(ic,
                        "Vessel PID={0} claimed by tree={1} via BACKGROUND_EVENT at UT={2:F1}",
                        pid, tree.Id, rec.StartUT));
            }
        }

        /// <summary>
        /// Collects the set of VesselPersistentIds for recordings in the tree's root
        /// lineage — tracing from the root recording through ChildBranchPointId chains.
        /// Any recording with a VesselPersistentId NOT in this set is a background recording.
        /// </summary>
        internal static HashSet<uint> GetRootLineageVesselPids(RecordingTree tree)
        {
            var pids = new HashSet<uint>();

            if (!string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.TryGetValue(tree.RootRecordingId, out Recording rootRec))
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
                            if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out Recording childRec))
                                TraceLineagePids(tree, childRec, pids, visited);
                        }
                        break;
                    }
                }
            }

            // Follow chain links: optimizer splits create chain-linked segments
            // where ChildBranchPointId moves to the last segment. Trace through
            // the next chain member to reach the segment that carries the BP.
            if (!string.IsNullOrEmpty(rec.ChainId))
            {
                int nextIdx = rec.ChainIndex + 1;
                foreach (var kvp in tree.Recordings)
                {
                    if (kvp.Value.ChainId == rec.ChainId
                        && kvp.Value.ChainIndex == nextIdx
                        && kvp.Value.ChainBranch == rec.ChainBranch)
                    {
                        TraceLineagePids(tree, kvp.Value, pids, visited);
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
            // First pass: resolve tip for each chain
            foreach (var kvp in chains)
            {
                var chain = kvp.Value;
                ResolveChainTip(chain, committedTrees);
            }

            // Second pass: cross-tree linking and merge
            MergeCrossTreeLinks(chains, committedTrees);
        }

        /// <summary>
        /// Cross-tree linking: if a chain's tip recording has a VesselPersistentId that
        /// matches another chain's OriginalVesselPid, merges the second chain's links into
        /// the first. Removes absorbed chains from the dictionary.
        /// </summary>
        private static void MergeCrossTreeLinks(
            Dictionary<uint, GhostChain> chains, List<RecordingTree> committedTrees)
        {
            var ic = CultureInfo.InvariantCulture;
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
                    chain.Links.Sort(CompareChainLinks);

                    // Update tip from merged chain
                    chain.TipRecordingId = linkedChain.TipRecordingId;
                    chain.TipTreeId = linkedChain.TipTreeId;
                    chain.SpawnUT = linkedChain.SpawnUT;

                    pidsToMerge.Add(tipVesselPid);

                    ParsekLog.VerboseOnChange(Tag,
                        identity: string.Format(ic,
                            "cross-tree-link|{0}|{1}", originPid, tipVesselPid),
                        stateKey: chain.TipRecordingId ?? "null",
                        message: string.Format(ic,
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

            if (pidsToMerge.Count > 0)
            {
                ParsekLog.VerboseOnChange(Tag,
                    identity: "cross-tree-merge-summary",
                    stateKey: pidsToMerge.Count.ToString(ic),
                    message: string.Format(ic,
                        "MergeCrossTreeLinks: absorbed {0} chain(s)", pidsToMerge.Count));
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
            var ic = CultureInfo.InvariantCulture;

            if (tree == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "WalkToLeaf: tree is null — returning rec={0}", rec.RecordingId));
                return rec;
            }

            var visited = new HashSet<string>();
            var current = rec;
            int steps = 0;

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
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "WalkToLeaf: no branch point or children for rec={0} bp={1} — stopping",
                            current.RecordingId, current.ChildBranchPointId));
                    break;
                }

                // Prefer the child whose VesselPersistentId matches the current recording's PID.
                // This ensures we follow the same vessel through splits rather than arbitrarily
                // picking the first child, which may be a detached stage or debris piece.
                // Fall back to child[0] if no PID match is found (e.g. EVA, or PID not set).
                string bestChildId = bp.ChildRecordingIds[0];
                if (current.VesselPersistentId != 0)
                {
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        Recording candidate;
                        if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out candidate)
                            && candidate.VesselPersistentId == current.VesselPersistentId)
                        {
                            bestChildId = bp.ChildRecordingIds[c];
                            break;
                        }
                    }
                }

                Recording child;
                if (tree.Recordings.TryGetValue(bestChildId, out child))
                {
                    steps++;
                    ParsekLog.VerboseOnChange(Tag,
                        identity: string.Format(ic,
                            "walk-step|{0}|{1}", rec.RecordingId, steps),
                        stateKey: string.Format(ic,
                            "{0}|{1}|{2}", current.RecordingId, bestChildId, bp.Id),
                        message: string.Format(ic,
                            "WalkToLeaf: step {0}: rec={1} → child={2} via bp={3}",
                            steps, current.RecordingId, bestChildId, bp.Id));
                    current = child;
                }
                else
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "WalkToLeaf: child recording '{0}' not found in tree — stopping",
                            bestChildId));
                    break;
                }
            }

            ParsekLog.VerboseOnChange(Tag,
                identity: string.Format(ic, "walk|{0}", rec.RecordingId),
                stateKey: string.Format(ic, "{0}|{1}", current.RecordingId, steps),
                message: string.Format(ic,
                    "WalkToLeaf: reached leaf={0} after {1} steps from start={2}",
                    current.RecordingId, steps, rec.RecordingId));
            return current;
        }

        /// <summary>
        /// Sets IsTerminated on a chain if the tip recording has a non-spawnable terminal state.
        /// </summary>
        private static void ResolveTermination(
            GhostChain chain, List<RecordingTree> committedTrees)
        {
            var ic = CultureInfo.InvariantCulture;

            if (chain.TipRecordingId == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "ResolveTermination: vessel={0} has null tip — skipping",
                        chain.OriginalVesselPid));
                return;
            }

            Recording tipRec = FindRecording(
                chain.TipRecordingId, chain.TipTreeId, committedTrees);

            if (tipRec == null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic, "ResolveTermination: tip recording '{0}' not found — skipping",
                        chain.TipRecordingId));
                return;
            }

            if (tipRec.TerminalStateValue.HasValue)
            {
                var ts = tipRec.TerminalStateValue.Value;
                if (ts == TerminalState.Destroyed || ts == TerminalState.Recovered)
                {
                    chain.IsTerminated = true;
                    ParsekLog.VerboseOnChange(Tag,
                        identity: string.Format(ic, "terminate|{0}", chain.OriginalVesselPid),
                        stateKey: string.Format(ic, "{0}|{1}", chain.TipRecordingId, ts),
                        message: string.Format(ic,
                            "ResolveTermination: vessel={0} tip={1} terminalState={2} — marked terminated",
                            chain.OriginalVesselPid, chain.TipRecordingId, ts));
                }
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

        private static int CompareChainLinks(ChainLink a, ChainLink b)
        {
            int cmp = a.ut.CompareTo(b.ut);
            if (cmp != 0)
                return cmp;

            cmp = string.CompareOrdinal(a.treeId ?? "", b.treeId ?? "");
            if (cmp != 0)
                return cmp;

            cmp = string.CompareOrdinal(a.recordingId ?? "", b.recordingId ?? "");
            if (cmp != 0)
                return cmp;

            cmp = string.CompareOrdinal(a.branchPointId ?? "", b.branchPointId ?? "");
            if (cmp != 0)
                return cmp;

            return string.CompareOrdinal(a.interactionType ?? "", b.interactionType ?? "");
        }

        /// <summary>
        /// Returns true if every leaf recording in the tree has a terminal state of
        /// Destroyed or Recovered. Such trees can never produce a ghost (#174).
        /// </summary>
        internal static bool IsTreeFullyTerminated(RecordingTree tree)
        {
            if (tree.Recordings.Count == 0)
                return true;

            foreach (var kvp in tree.Recordings)
            {
                var rec = kvp.Value;
                // Only check leaves (no child branch point)
                if (rec.ChildBranchPointId != null)
                    continue;

                if (!rec.TerminalStateValue.HasValue)
                    return false;

                var ts = rec.TerminalStateValue.Value;
                if (ts != TerminalState.Destroyed && ts != TerminalState.Recovered)
                    return false;
            }

            return true;
        }
    }
}
