using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // M-MIS-8 (design: docs/dev/design-mission-crosstree-dock.md): cross-tree foreign-dock
    // derivation for Missions. When vessel B (this mission's tree) was docked by a FOREIGN
    // vessel A (another tree), the combined leg + B's post-undock offshoot live in A's tree.
    // This class DERIVES that link live - the same PID + launch-guid claim rule
    // GhostChainWalker uses at playback - so the Mission persists only the set of INCLUDED
    // link ids (the claiming BranchPoint's GUID), never any second-tree topology.
    // Pure: no Unity calls, no shared mutable state, no recording mutation.

    /// <summary>
    /// One derived cross-tree dock link: a Dock/Board branch point in a FOREIGN tree whose
    /// <see cref="BranchPoint.TargetVesselPersistentId"/> claims a vessel recorded in MY tree.
    /// </summary>
    internal sealed class ForeignDockLink
    {
        public string LinkId;                 // claiming BranchPoint.Id (foreign tree; stable GUID)
        public string ForeignTreeId;
        public double DockUT;                 // branch point UT (the merge)
        public BranchPointType ClaimType;     // Dock or Board
        public uint PartnerPid;               // bp.TargetVesselPersistentId = MY vessel's pid
        public string PartnerLaunchGuid;      // my launch guid (from my tree; null = unknown)
        public string ClaimedRecordingId;     // my recording matched by pid (+ guid gate)
        public string MergedChildRecordingId; // the combined-stack recording in the foreign tree
        public string ForeignVesselName;      // merged-stack vessel name (affordance label)
    }

    internal static class MissionCrossTreeDock
    {
        // Comp-node UTs derive from the same leg UT doubles the journey windows derive from,
        // so the containment epsilon only needs to absorb representation noise.
        private const double WindowEpsilon = 1e-3;

        // Set true by per-frame / per-tick callers (Missions window caches, loop-unit display
        // mirror, route resolve) so the per-derivation Verbose summaries do not flood - mirrors
        // MissionStructureBuilder.SuppressLogging.
        internal static bool SuppressLogging;

        /// <summary>
        /// Finds every cross-tree dock link targeting a vessel of <paramref name="myTree"/>:
        /// a Dock/Board branch point in any OTHER committed tree whose
        /// <c>TargetVesselPersistentId</c> matches a recording pid in my tree and whose launch
        /// guids do not conclusively differ (walker claim parity; unknown guid falls back to
        /// pid-only). Ordered by DockUT then LinkId for deterministic output. Pure.
        /// </summary>
        internal static List<ForeignDockLink> FindLinks(
            RecordingTree myTree, IReadOnlyList<RecordingTree> allTrees)
        {
            var links = new List<ForeignDockLink>();
            if (myTree?.Recordings == null || myTree.Recordings.Count == 0 || allTrees == null)
                return links;

            int scannedTrees = 0;
            int guidRejected = 0;
            for (int t = 0; t < allTrees.Count; t++)
            {
                RecordingTree foreignTree = allTrees[t];
                if (foreignTree?.BranchPoints == null || foreignTree.Id == null
                    || string.Equals(foreignTree.Id, myTree.Id, StringComparison.Ordinal))
                    continue;
                scannedTrees++;

                for (int b = 0; b < foreignTree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = foreignTree.BranchPoints[b];
                    if (bp == null
                        || (bp.Type != BranchPointType.Dock && bp.Type != BranchPointType.Board)
                        || bp.TargetVesselPersistentId == 0
                        || string.IsNullOrEmpty(bp.Id))
                        continue;

                    // The claim must land on a vessel recorded in MY tree (pid + guid gate).
                    string foreignLaunchGuid = ResolveLaunchGuidForPid(
                        foreignTree, bp.TargetVesselPersistentId);
                    Recording claimed = FindClaimedRecording(
                        myTree, bp.TargetVesselPersistentId, foreignLaunchGuid, out bool rejectedByGuid);
                    if (rejectedByGuid)
                        guidRejected++;
                    if (claimed == null)
                        continue;

                    string mergedChildId = bp.ChildRecordingIds != null && bp.ChildRecordingIds.Count > 0
                        ? bp.ChildRecordingIds[0]
                        : null;
                    if (string.IsNullOrEmpty(mergedChildId)
                        || !foreignTree.Recordings.TryGetValue(mergedChildId, out Recording mergedChild)
                        || mergedChild == null)
                        continue;

                    links.Add(new ForeignDockLink
                    {
                        LinkId = bp.Id,
                        ForeignTreeId = foreignTree.Id,
                        DockUT = bp.UT,
                        ClaimType = bp.Type,
                        PartnerPid = bp.TargetVesselPersistentId,
                        PartnerLaunchGuid = claimed.RecordedVesselGuid,
                        ClaimedRecordingId = claimed.RecordingId,
                        MergedChildRecordingId = mergedChildId,
                        ForeignVesselName = mergedChild.VesselName
                    });
                }
            }

            links.Sort((a, b) =>
            {
                int cmp = a.DockUT.CompareTo(b.DockUT);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.LinkId, b.LinkId);
            });

            if (!SuppressLogging && links.Count > 0)
                ParsekLog.Verbose("Mission",
                    $"CrossTreeDock: tree={myTree.Id} links={links.Count} " +
                    $"scannedTrees={scannedTrees} guidRejected={guidRejected}");
            return links;
        }

        /// <summary>
        /// Resolves one link of <paramref name="myTree"/> by its persisted id, or null when it
        /// no longer derives (foreign tree / branch point gone, claim no longer matches).
        /// </summary>
        internal static ForeignDockLink FindLinkById(
            RecordingTree myTree, IReadOnlyList<RecordingTree> allTrees, string linkId)
        {
            if (string.IsNullOrEmpty(linkId))
                return null;
            List<ForeignDockLink> links = FindLinks(myTree, allTrees);
            for (int i = 0; i < links.Count; i++)
                if (string.Equals(links[i].LinkId, linkId, StringComparison.Ordinal))
                    return links[i];
            return null;
        }

        /// <summary>
        /// Walks the PARTNER JOURNEY in the foreign tree: the ordered recording ids that carry
        /// my vessel from the merge on - the docked stretch, then (when the partner departs at
        /// an undock fork) the partner's own offshoot line to its end. At each branch point the
        /// walk PREFERS the child whose pid matches the partner (guid-gated) - the partner
        /// departing, or the continuing stack when the partner survived as the merged vessel -
        /// and otherwise follows the recorder's continuing-child convention while the partner
        /// is presumed still aboard. Cycle-guarded; exotic splits that move the partner onto a
        /// non-matching, non-continuation child mis-follow by design (logged, v1). Pure.
        /// </summary>
        internal static List<string> ComputePartnerJourneyLegIds(
            RecordingTree foreignTree, ForeignDockLink link)
        {
            var journey = new List<string>();
            if (foreignTree?.Recordings == null || link == null
                || string.IsNullOrEmpty(link.MergedChildRecordingId)
                || !foreignTree.Recordings.TryGetValue(link.MergedChildRecordingId, out Recording current)
                || current == null)
                return journey;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (current != null && visited.Add(current.RecordingId))
            {
                if (!current.IsDebris)
                    journey.Add(current.RecordingId);

                Recording next = FindChainSuccessor(foreignTree, current)
                                 ?? FindBranchSuccessor(foreignTree, current, link);
                current = next;
            }
            return journey;
        }

        /// <summary>
        /// Per through-line JOURNEY WINDOW: for each foreign through-line that contains journey
        /// legs, the [min StartUT, max EndUT] over those legs (keyed by the line's head id). The
        /// shared line's window starts at the dock (the merged child's start) and ends where the
        /// partner departs, so the foreign vessel's own pre-dock / post-departure intervals fall
        /// outside it. Pure.
        /// </summary>
        internal static Dictionary<string, MissionIntervalSelection.RenderWindow> ComputeJourneyWindowsByOwner(
            MissionStructure foreignStructure,
            MissionThroughLineView foreignView,
            ICollection<string> journeyLegIds)
        {
            var windows = new Dictionary<string, MissionIntervalSelection.RenderWindow>(StringComparer.Ordinal);
            if (foreignStructure == null || foreignView == null
                || journeyLegIds == null || journeyLegIds.Count == 0)
                return windows;

            foreach (var tl in foreignView.ByHeadId.Values)
            {
                double start = double.PositiveInfinity;
                double end = double.NegativeInfinity;
                bool any = false;
                for (int i = 0; i < tl.MemberLegIds.Count; i++)
                {
                    string legId = tl.MemberLegIds[i];
                    if (!journeyLegIds.Contains(legId)
                        || !foreignStructure.LegsById.TryGetValue(legId, out MissionLeg leg))
                        continue;
                    any = true;
                    if (leg.StartUT < start) start = leg.StartUT;
                    if (leg.EndUT > end) end = leg.EndUT;
                }
                if (any && end > start)
                    windows[tl.HeadLegId] = new MissionIntervalSelection.RenderWindow
                    {
                        StartUT = start,
                        EndUT = end
                    };
            }
            return windows;
        }

        /// <summary>
        /// True when a foreign composition node is a PARTNER-JOURNEY interval: selectable, owned
        /// by a through-line that carries journey legs, and lying inside that line's journey
        /// window (so the foreign vessel's own pre-dock / post-departure intervals are not
        /// offered). Pure.
        /// </summary>
        internal static bool IsJourneyNode(
            MissionCompositionNode node,
            IReadOnlyDictionary<string, MissionIntervalSelection.RenderWindow> journeyWindows)
        {
            if (node == null || !node.IsSelectable || string.IsNullOrEmpty(node.OwnerHeadId)
                || journeyWindows == null
                || !journeyWindows.TryGetValue(node.OwnerHeadId, out MissionIntervalSelection.RenderWindow w))
                return false;
            return node.StartUT >= w.StartUT - WindowEpsilon
                   && node.EndUT <= w.EndUT + WindowEpsilon;
        }

        /// <summary>
        /// Collects the selectable interval keys (HeadLegIds) of every partner-journey node -
        /// exactly the FOREIGN keys a linked Mission may hold in ExcludedIntervalKeys. Used by
        /// MissionStore.ReconcileSelections to extend the valid-key set across the seam. Pure.
        /// </summary>
        internal static void CollectJourneySelectableKeys(
            IReadOnlyList<MissionCompositionNode> compRoots,
            IReadOnlyDictionary<string, MissionIntervalSelection.RenderWindow> journeyWindows,
            HashSet<string> into)
        {
            if (compRoots == null || into == null)
                return;
            for (int i = 0; i < compRoots.Count; i++)
                CollectJourneyKeysRecursive(compRoots[i], journeyWindows, into);
        }

        /// <summary>
        /// The included render window per foreign through-line owner: accumulated over journey
        /// nodes NOT in the excluded set (mirrors MissionIntervalSelection.ComputeRenderWindows),
        /// then intersected with the journey window. An owner with every journey interval
        /// excluded is absent. Pure.
        /// </summary>
        internal static Dictionary<string, MissionIntervalSelection.RenderWindow> ComputeIncludedJourneyRenderWindows(
            IReadOnlyList<MissionCompositionNode> compRoots,
            IReadOnlyDictionary<string, MissionIntervalSelection.RenderWindow> journeyWindows,
            ICollection<string> excludedIntervalKeys)
        {
            var included = new Dictionary<string, MissionIntervalSelection.RenderWindow>(StringComparer.Ordinal);
            if (compRoots == null || journeyWindows == null || journeyWindows.Count == 0)
                return included;
            for (int i = 0; i < compRoots.Count; i++)
                AccumulateIncludedJourney(compRoots[i], journeyWindows, excludedIntervalKeys, included);

            // Clamp each accumulated window into its journey window (defensive: journey nodes
            // are inside the window by predicate, so this only trims epsilon slack).
            var owners = new List<string>(included.Keys);
            for (int i = 0; i < owners.Count; i++)
            {
                MissionIntervalSelection.RenderWindow w = included[owners[i]];
                MissionIntervalSelection.RenderWindow j = journeyWindows[owners[i]];
                w.StartUT = Math.Max(w.StartUT, j.StartUT);
                w.EndUT = Math.Min(w.EndUT, j.EndUT);
                if (w.EndUT > w.StartUT)
                    included[owners[i]] = w;
                else
                    included.Remove(owners[i]);
            }
            return included;
        }

        /// <summary>
        /// Resolves a mission's included links and maps every included partner-journey interval
        /// to committed member indices + trimmed windows, merging into
        /// <paramref name="memberWindowByIndex"/> (first claimant wins on a duplicate index).
        /// Returns the number of foreign members added. Batch-counts one Verbose summary per
        /// call (respects <see cref="SuppressLogging"/>). Pure.
        /// </summary>
        internal static int MergeForeignMemberWindows(
            Mission mission,
            RecordingTree myTree,
            IReadOnlyList<RecordingTree> allTrees,
            IReadOnlyList<Recording> committed,
            Dictionary<string, int> indexById,
            Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindowByIndex,
            out int resolvedLinks,
            out int staleLinks)
        {
            resolvedLinks = 0;
            staleLinks = 0;
            if (mission == null || mission.IncludedForeignDockLinkIds.Count == 0
                || myTree == null || allTrees == null || committed == null
                || indexById == null || memberWindowByIndex == null)
                return 0;

            List<ForeignDockLink> links = FindLinks(myTree, allTrees);
            var byLinkId = new Dictionary<string, ForeignDockLink>(StringComparer.Ordinal);
            for (int i = 0; i < links.Count; i++)
                byLinkId[links[i].LinkId] = links[i];

            // Deterministic link order (HashSet enumeration order is unspecified).
            var includedIds = new List<string>(mission.IncludedForeignDockLinkIds);
            includedIds.Sort(StringComparer.Ordinal);

            // Foreign structure / view / composition built at most once per foreign tree.
            var structureCache = new Dictionary<string, MissionStructure>(StringComparer.Ordinal);
            var viewCache = new Dictionary<string, MissionThroughLineView>(StringComparer.Ordinal);
            var compCache = new Dictionary<string, List<MissionCompositionNode>>(StringComparer.Ordinal);

            int added = 0;
            int duplicateIndices = 0;
            int skippedNotCommitted = 0;
            for (int li = 0; li < includedIds.Count; li++)
            {
                if (!byLinkId.TryGetValue(includedIds[li], out ForeignDockLink link))
                {
                    staleLinks++;
                    continue;
                }
                RecordingTree foreignTree = FindTree(allTrees, link.ForeignTreeId);
                if (foreignTree == null)
                {
                    staleLinks++;
                    continue;
                }
                resolvedLinks++;

                if (!structureCache.TryGetValue(foreignTree.Id, out MissionStructure structure))
                {
                    structure = MissionStructureBuilder.Build(foreignTree);
                    structureCache[foreignTree.Id] = structure;
                    viewCache[foreignTree.Id] = MissionThroughLineBuilder.Build(structure);
                    compCache[foreignTree.Id] = MissionCompositionBuilder.Build(structure);
                }
                MissionThroughLineView view = viewCache[foreignTree.Id];
                List<MissionCompositionNode> compRoots = compCache[foreignTree.Id];

                List<string> journeyLegs = ComputePartnerJourneyLegIds(foreignTree, link);
                if (journeyLegs.Count == 0)
                    continue;
                var journeySet = new HashSet<string>(journeyLegs, StringComparer.Ordinal);
                var journeyWindows = ComputeJourneyWindowsByOwner(structure, view, journeySet);
                var renderWindows = ComputeIncludedJourneyRenderWindows(
                    compRoots, journeyWindows, mission.ExcludedIntervalKeys);

                foreach (var rw in renderWindows)
                {
                    if (!view.ByHeadId.TryGetValue(rw.Key, out MissionThroughLine tl))
                        continue;
                    for (int m = 0; m < tl.MemberLegIds.Count; m++)
                    {
                        string legId = tl.MemberLegIds[m];
                        if (!journeySet.Contains(legId))
                            continue;
                        if (!indexById.TryGetValue(legId, out int idx))
                        {
                            skippedNotCommitted++;
                            continue;
                        }
                        Recording rec = committed[idx];
                        if (rec == null)
                            continue;
                        double rStart = Math.Max(rw.Value.StartUT, rec.StartUT);
                        double rEnd = Math.Min(rw.Value.EndUT, rec.EndUT);
                        if (rEnd <= rStart)
                            continue;
                        if (memberWindowByIndex.ContainsKey(idx))
                        {
                            duplicateIndices++;
                            continue;
                        }
                        memberWindowByIndex[idx] =
                            new GhostPlaybackLogic.LoopUnit.MemberWindow(rStart, rEnd);
                        added++;
                    }
                }
            }

            if (!SuppressLogging)
                ParsekLog.Verbose("Mission",
                    $"CrossTreeDock: mission='{mission.Name}' tree={mission.TreeId} " +
                    $"links included={includedIds.Count} resolved={resolvedLinks} stale={staleLinks} " +
                    $"foreignMembers={added} duplicates={duplicateIndices} " +
                    $"skippedNotCommitted={skippedNotCommitted}");
            return added;
        }

        // ---- private helpers ----

        private static void CollectJourneyKeysRecursive(
            MissionCompositionNode node,
            IReadOnlyDictionary<string, MissionIntervalSelection.RenderWindow> journeyWindows,
            HashSet<string> into)
        {
            if (node == null)
                return;
            if (IsJourneyNode(node, journeyWindows) && !string.IsNullOrEmpty(node.HeadLegId))
                into.Add(node.HeadLegId);
            for (int i = 0; i < node.Children.Count; i++)
                CollectJourneyKeysRecursive(node.Children[i], journeyWindows, into);
        }

        private static void AccumulateIncludedJourney(
            MissionCompositionNode node,
            IReadOnlyDictionary<string, MissionIntervalSelection.RenderWindow> journeyWindows,
            ICollection<string> excluded,
            Dictionary<string, MissionIntervalSelection.RenderWindow> windows)
        {
            if (node == null)
                return;
            if (IsJourneyNode(node, journeyWindows)
                && (excluded == null || !excluded.Contains(node.HeadLegId)))
            {
                if (!windows.TryGetValue(node.OwnerHeadId, out MissionIntervalSelection.RenderWindow w))
                {
                    windows[node.OwnerHeadId] = new MissionIntervalSelection.RenderWindow
                    {
                        StartUT = node.StartUT,
                        EndUT = node.EndUT
                    };
                }
                else
                {
                    if (node.StartUT < w.StartUT) w.StartUT = node.StartUT;
                    if (node.EndUT > w.EndUT) w.EndUT = node.EndUT;
                    windows[node.OwnerHeadId] = w;
                }
            }
            for (int i = 0; i < node.Children.Count; i++)
                AccumulateIncludedJourney(node.Children[i], journeyWindows, excluded, windows);
        }

        // My tree's recording claimed by the foreign branch point: pid match, launch guids not
        // conclusively differing (walker parity). Prefers the EARLIEST-starting match so the
        // claim pins the vessel's own line, not a later continuation segment.
        private static Recording FindClaimedRecording(
            RecordingTree myTree, uint pid, string foreignLaunchGuid, out bool rejectedByGuid)
        {
            rejectedByGuid = false;
            Recording best = null;
            foreach (var rec in myTree.Recordings.Values)
            {
                if (rec == null || rec.VesselPersistentId != pid)
                    continue;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(
                        rec.RecordedVesselGuid, foreignLaunchGuid))
                {
                    rejectedByGuid = true;
                    continue;
                }
                if (best == null || rec.StartUT < best.StartUT)
                    best = rec;
            }
            return best;
        }

        // Launch guid of the given pid within a tree (one launch per pid per tree) - mirrors
        // GhostChainWalker.ResolveLaunchGuidForPid. Null when not found. In the foreign tree
        // the merged child / departing offshoot carry the pid when the partner survived as the
        // merged stack; when the partner was absorbed, the foreign tree may carry no recording
        // with that pid at all (null -> pid-only fallback, walker semantics).
        private static string ResolveLaunchGuidForPid(RecordingTree tree, uint pid)
        {
            if (tree?.Recordings == null || pid == 0)
                return null;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec != null && rec.VesselPersistentId == pid
                    && !string.IsNullOrEmpty(rec.RecordedVesselGuid))
                    return rec.RecordedVesselGuid;
            }
            return null;
        }

        // Env-split chain successor: same ChainId, next ChainIndex, same ChainBranch (the
        // GhostChainWalker chain-link trace rule). Null at the run tail.
        private static Recording FindChainSuccessor(RecordingTree tree, Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChainId))
                return null;
            int nextIdx = rec.ChainIndex + 1;
            foreach (var kvp in tree.Recordings)
            {
                Recording candidate = kvp.Value;
                if (candidate != null
                    && candidate.ChainId == rec.ChainId
                    && candidate.ChainIndex == nextIdx
                    && candidate.ChainBranch == rec.ChainBranch)
                    return candidate;
            }
            return null;
        }

        // Branch-point successor for the journey walk: prefer the child whose pid matches the
        // partner (guid-gated) - the partner departing, or the continuing stack when the
        // partner survived as the merged vessel; else the recorder's continuing-child
        // convention (ChildRecordingIds[0]) when it is a controlled (non-debris, non-anchored,
        // non-EVA) leg; else the first controlled child. Null ends the walk.
        private static Recording FindBranchSuccessor(
            RecordingTree tree, Recording rec, ForeignDockLink link)
        {
            if (string.IsNullOrEmpty(rec.ChildBranchPointId) || tree.BranchPoints == null)
                return null;

            BranchPoint bp = null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                if (tree.BranchPoints[i] != null && tree.BranchPoints[i].Id == rec.ChildBranchPointId)
                {
                    bp = tree.BranchPoints[i];
                    break;
                }
            }
            if (bp?.ChildRecordingIds == null || bp.ChildRecordingIds.Count == 0)
                return null;

            Recording firstControlled = null;
            for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
            {
                if (bp.ChildRecordingIds[c] == null
                    || !tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out Recording child)
                    || child == null || child.IsDebris)
                    continue;

                // Partner departure (or partner-surviving continuation): pid + guid gate.
                if (child.VesselPersistentId == link.PartnerPid
                    && !VesselLaunchIdentity.GuidsConclusivelyDiffer(
                        child.RecordedVesselGuid, link.PartnerLaunchGuid))
                    return child;

                if (firstControlled == null
                    && string.IsNullOrEmpty(child.ParentAnchorRecordingId)
                    && string.IsNullOrEmpty(child.EvaCrewName))
                    firstControlled = child;
            }
            // No pid match: the partner is presumed still aboard the continuing stack.
            return firstControlled;
        }

        private static RecordingTree FindTree(IReadOnlyList<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                    return trees[i];
            return null;
        }
    }
}
