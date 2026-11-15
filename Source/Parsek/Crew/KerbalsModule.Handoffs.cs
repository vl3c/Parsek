using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Split and dock handoffs: the split handoff key and UT, dock claims and when a claim hands off a hold, and the split branch-point collection the pre-pass reads.
    internal partial class KerbalsModule : IResourceModule
    {
        internal static string SplitHandoffKey(string treeId, string branchPointId, string kerbalName)
        {
            return (treeId ?? "") + "\n" + (branchPointId ?? "") + "\n" + (kerbalName ?? "");
        }

        /// <summary>
        /// When a recording ending at <paramref name="holdEndUT"/> with child branch point
        /// <paramref name="childBranchPointId"/> handed <paramref name="kerbalName"/> to a child
        /// of that branch point, returns <paramref name="holdEndUT"/> (the split); else +inf.
        /// The recording must have ENDED at the split (EndUT no later than the child's start,
        /// within <see cref="RecoveryClosureEndToleranceSeconds"/>): a breakup-continuous
        /// parent that flies on past its branch point keeps its own row. Pure.
        /// </summary>
        internal static double ResolveSplitHandoffUT(
            IReadOnlyDictionary<string, SplitHandoff> handoffs,
            string treeId,
            string childBranchPointId,
            double holdEndUT,
            string kerbalName,
            out string childRecordingId)
        {
            childRecordingId = null;
            if (handoffs == null || string.IsNullOrEmpty(treeId)
                || string.IsNullOrEmpty(childBranchPointId) || string.IsNullOrEmpty(kerbalName))
                return double.PositiveInfinity;
            if (double.IsNaN(holdEndUT) || double.IsInfinity(holdEndUT))
                return double.PositiveInfinity;

            SplitHandoff handoff;
            if (!handoffs.TryGetValue(SplitHandoffKey(treeId, childBranchPointId, kerbalName), out handoff))
                return double.PositiveInfinity;
            if (holdEndUT > handoff.ChildStartUT + RecoveryClosureEndToleranceSeconds)
                return double.PositiveInfinity;

            childRecordingId = handoff.ChildRecordingId;
            return holdEndUT;
        }

        /// <summary>
        /// A recording that FLEW ON past a split (breakup-continuous: a decoupled Hitchhiker
        /// left the pod with a kerbal inside) and whose own row for <paramref name="kerbalName"/>
        /// is open-ended Unknown (he is not in its end snapshot): when a child of one of its
        /// split branch points (<paramref name="splitBranchPointIds"/>) carries him on and that
        /// child started inside the recording's span, the hold ends at the earliest such child's
        /// start and the child's own row decides the rest; else +inf
        /// (SPLIT-OF-A-CREWED-COMMANDLESS-PART-READS-AS-DEBRIS). Pure.
        /// </summary>
        internal static double ResolveMidFlightSplitHandoffUT(
            IReadOnlyDictionary<string, SplitHandoff> handoffs,
            string treeId,
            IReadOnlyList<string> splitBranchPointIds,
            double holdStartUT,
            double holdEndUT,
            string kerbalName,
            out string childRecordingId,
            out string branchPointId)
        {
            childRecordingId = null;
            branchPointId = null;
            if (handoffs == null || splitBranchPointIds == null || string.IsNullOrEmpty(treeId)
                || string.IsNullOrEmpty(kerbalName))
                return double.PositiveInfinity;
            if (double.IsNaN(holdEndUT) || double.IsNaN(holdStartUT))
                return double.PositiveInfinity;

            double best = double.PositiveInfinity;
            for (int i = 0; i < splitBranchPointIds.Count; i++)
            {
                string bpId = splitBranchPointIds[i];
                if (string.IsNullOrEmpty(bpId)) continue;
                SplitHandoff handoff;
                if (!handoffs.TryGetValue(SplitHandoffKey(treeId, bpId, kerbalName), out handoff))
                    continue;
                if (double.IsNaN(handoff.ChildStartUT) || double.IsInfinity(handoff.ChildStartUT))
                    continue;
                if (handoff.ChildStartUT < holdStartUT - RecoveryClosureEndToleranceSeconds
                    || handoff.ChildStartUT > holdEndUT + RecoveryClosureEndToleranceSeconds)
                    continue;
                if (handoff.ChildStartUT < best)
                {
                    best = handoff.ChildStartUT;
                    childRecordingId = handoff.ChildRecordingId;
                    branchPointId = bpId;
                }
            }
            return best;
        }

        /// <summary>The branch point types that SPLIT a vessel (a child carries part of it, and
        /// any crew aboard that part, away from its parent).</summary>
        internal static bool IsSplitBranchPointType(BranchPointType type)
        {
            return type == BranchPointType.Undock
                || type == BranchPointType.EVA
                || type == BranchPointType.JointBreak
                || type == BranchPointType.Breakup;
        }

        /// <summary>
        /// True when a recording whose parent branch point is <paramref name="parentBranchPointId"/>
        /// (type <paramref name="parentBranchPointType"/>, null when the tree does not list it)
        /// carries a vessel that JOINED its tree: no parent (the root, a parentless post-switch
        /// recording, a standalone switch segment) or a post-switch Launch branch point. A
        /// split, dock, board or switch-continuation child carries a vessel of the tree's own
        /// lineage. Pure.
        /// </summary>
        internal static bool IsJoinedVesselRecording(
            string parentBranchPointId, BranchPointType? parentBranchPointType)
        {
            if (string.IsNullOrEmpty(parentBranchPointId))
                return true;
            return parentBranchPointType == BranchPointType.Launch;
        }

        /// <summary>
        /// Does a dock (or board) claiming <paramref name="dockTargetPid"/> /
        /// <paramref name="dockTargetGuid"/> at <paramref name="dockUT"/> take over the
        /// vessel an open-ended hold's recording (<paramref name="holdVesselPid"/> /
        /// <paramref name="holdVesselGuid"/>, ending <paramref name="holdEndUT"/>) left the
        /// kerbal on? The pid must match and the launch guids must not conclusively differ (a
        /// pid is craft-baked, so another launch of the same craft is not the same vessel),
        /// OR the dock's guid continues the hold's guid through a link row
        /// (<paramref name="guidContinues"/>, default <see cref="VesselNameStore.GuidContinuesIdentity"/>):
        /// a Parsek spawn of the held vessel carries a fresh pid and guid, and a rescue that
        /// docks after a rewind docks to that spawn. The held recording must have ENDED by the
        /// dock (within <see cref="RecoveryClosureEndToleranceSeconds"/>): the docking mission
        /// can only carry on a kerbal the vessel still held when it arrived. Pure given the
        /// reader.
        /// </summary>
        internal static bool DockClaimHandsOffHold(
            uint holdVesselPid, string holdVesselGuid, double holdEndUT,
            uint dockTargetPid, string dockTargetGuid, double dockUT,
            Func<string, string, bool> guidContinues = null)
        {
            if (holdVesselPid == 0 || dockTargetPid == 0)
                return false;
            bool sameLaunch = dockTargetPid == holdVesselPid
                && !VesselLaunchIdentity.GuidsConclusivelyDiffer(holdVesselGuid, dockTargetGuid);
            if (!sameLaunch
                && (VesselLaunchIdentity.NormalizeGuid(holdVesselGuid) == null
                    || VesselLaunchIdentity.NormalizeGuid(dockTargetGuid) == null
                    || !(guidContinues ?? VesselNameStore.GuidContinuesIdentity)(dockTargetGuid, holdVesselGuid)))
                return false;
            if (double.IsNaN(holdEndUT) || double.IsInfinity(holdEndUT)
                || double.IsNaN(dockUT) || double.IsInfinity(dockUT))
                return false;
            return holdEndUT <= dockUT + RecoveryClosureEndToleranceSeconds;
        }

        /// <summary>
        /// The earliest dock claim on the vessel <paramref name="recordingId"/> ended as whose
        /// merged child recording carries <paramref name="kerbalName"/> on
        /// (<paramref name="carriedBy"/>: recording id -> the kerbals its own rows name), or
        /// +inf. The claim may live in any tree: a rescue mission docks to a station another
        /// mission left him on (design 9.3: a STRANDED hold stays open "until a rescue
        /// recording closes it"). The held recording's own outgoing branch point is the
        /// in-tree split hand-off, never a claim on itself. A
        /// <see cref="DockClaim.ForeignVesselRecording"/> claim counts only when it lives in
        /// a tree other than <paramref name="holdTreeId"/> (an unknown tree on either side
        /// claims nothing). Pure.
        /// </summary>
        internal static double ResolveDockClaimHandoffUT(
            IReadOnlyList<DockClaim> claims,
            IReadOnlyDictionary<string, HashSet<string>> carriedBy,
            string recordingId,
            string holdTreeId,
            uint holdVesselPid,
            string holdVesselGuid,
            double holdEndUT,
            string kerbalName,
            out DockClaim claim)
        {
            claim = default(DockClaim);
            if (claims == null || carriedBy == null || string.IsNullOrEmpty(kerbalName)
                || string.IsNullOrEmpty(recordingId))
                return double.PositiveInfinity;

            double best = double.PositiveInfinity;
            for (int i = 0; i < claims.Count; i++)
            {
                DockClaim c = claims[i];
                if (string.IsNullOrEmpty(c.MergedChildRecordingId)
                    || string.Equals(c.MergedChildRecordingId, recordingId, StringComparison.Ordinal))
                    continue;
                if (c.ForeignVesselRecording
                    && (string.IsNullOrEmpty(c.TreeId) || string.IsNullOrEmpty(holdTreeId)
                        || string.Equals(c.TreeId, holdTreeId, StringComparison.Ordinal)))
                    continue;
                if (!DockClaimHandsOffHold(holdVesselPid, holdVesselGuid, holdEndUT,
                        c.TargetPid, c.TargetGuid, c.DockUT))
                    continue;
                HashSet<string> carried;
                if (!carriedBy.TryGetValue(c.MergedChildRecordingId, out carried)
                    || carried == null || !carried.Contains(kerbalName))
                    continue;
                if (c.DockUT < best)
                {
                    best = c.DockUT;
                    claim = c;
                }
            }
            return best;
        }

        /// <summary>
        /// Fills <see cref="dockClaims"/> from the committed trees' Dock / Board branch points
        /// that name a target vessel (a Board's is its merged child's vessel) and a merged
        /// child, plus every InventoryTransfer branch point (the claimed cargo partner is its
        /// member recording's own vessel, which the member carries on), plus every crewed
        /// recording whose vessel joined its tree (<see cref="IsJoinedVesselRecording"/>,
        /// claiming its own vessel at its start), and <see cref="rowKerbalsByRecording"/>
        /// from the walk's holding KerbalAssignment rows (<see cref="RoleHoldsKerbal(string)"/>; the walk's actions are the
        /// effective ledger, so a superseded docked recording carries nobody). Returns the
        /// branch-point claims collected (the joined-vessel claims log their own count).
        /// </summary>
        private int CollectDockClaims(IReadOnlyList<RecordingTree> trees, List<GameAction> actions)
        {
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null || a.Type != GameActionType.KerbalAssignment) continue;
                    if (string.IsNullOrEmpty(a.KerbalName) || string.IsNullOrEmpty(a.RecordingId)) continue;
                    if (!RoleHoldsKerbal(a.KerbalRole)) continue;
                    HashSet<string> set;
                    if (!rowKerbalsByRecording.TryGetValue(a.RecordingId, out set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        rowKerbalsByRecording[a.RecordingId] = set;
                    }
                    set.Add(a.KerbalName);
                }
            }

            if (trees == null) return 0;
            int collected = 0;
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree?.BranchPoints == null) continue;
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp == null
                        || (bp.Type != BranchPointType.Dock && bp.Type != BranchPointType.Board
                            && bp.Type != BranchPointType.InventoryTransfer)
                        || bp.ChildRecordingIds == null || bp.ChildRecordingIds.Count == 0
                        || string.IsNullOrEmpty(bp.ChildRecordingIds[0]))
                        continue;
                    string mergedChildId = bp.ChildRecordingIds[0];
                    uint targetPid = bp.TargetVesselPersistentId;
                    string targetGuid = bp.TargetVesselGuid;
                    if (bp.Type == BranchPointType.InventoryTransfer)
                    {
                        // A cargo partner joins the tree as its own member recording (design
                        // 4.12): the member IS the claimed vessel flying on, so it takes over
                        // the crew an earlier mission left aboard it (or the kerbal himself,
                        // when the partner is another kerbal on EVA). The branch point names
                        // no target; the member's own identity is the target.
                        RecordingMeta memberMeta;
                        if (!recordingMeta.TryGetValue(mergedChildId, out memberMeta))
                            continue;
                        targetPid = memberMeta.VesselPid;
                        targetGuid = memberMeta.VesselGuid;
                    }
                    else if (targetPid == 0 && bp.Type == BranchPointType.Board)
                    {
                        // A Board onto a vessel of the same tree stamps no target
                        // (ParsekFlight.ResolveBoardPartnerStamp), but the boarded vessel IS the
                        // merged child's vessel: the kerbal joins it and the vessel flies on
                        // under its own pid and launch guid.
                        RecordingMeta childMeta;
                        if (recordingMeta.TryGetValue(mergedChildId, out childMeta))
                        {
                            targetPid = childMeta.VesselPid;
                            targetGuid = childMeta.VesselGuid;
                        }
                    }
                    if (targetPid == 0)
                        continue;
                    dockClaims.Add(new DockClaim
                    {
                        TreeId = tree.Id,
                        BranchPointId = bp.Id,
                        DockUT = bp.UT,
                        TargetPid = targetPid,
                        TargetGuid = targetGuid,
                        MergedChildRecordingId = mergedChildId,
                    });
                    collected++;
                }
            }

            int joinedClaims = 0;
            int joinedSkippedUncrewed = 0;
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree?.Recordings == null || string.IsNullOrEmpty(tree.Id)) continue;
                foreach (var kvp in tree.Recordings)
                {
                    string recId = kvp.Key;
                    RecordingMeta meta;
                    if (string.IsNullOrEmpty(recId) || !recordingMeta.TryGetValue(recId, out meta))
                        continue;
                    if (meta.VesselPid == 0 || double.IsNaN(meta.StartUT) || double.IsInfinity(meta.StartUT))
                        continue;
                    BranchPointType? parentType = null;
                    if (!string.IsNullOrEmpty(meta.ParentBranchPointId))
                    {
                        BranchPoint parentBp = FindBranchPoint(tree, meta.ParentBranchPointId);
                        if (parentBp != null) parentType = parentBp.Type;
                    }
                    if (!IsJoinedVesselRecording(meta.ParentBranchPointId, parentType))
                        continue;
                    // A recording whose rows name nobody can hand no hold on.
                    HashSet<string> carried;
                    if (!rowKerbalsByRecording.TryGetValue(recId, out carried)
                        || carried == null || carried.Count == 0)
                    {
                        joinedSkippedUncrewed++;
                        continue;
                    }
                    dockClaims.Add(new DockClaim
                    {
                        TreeId = tree.Id,
                        BranchPointId = meta.ParentBranchPointId,
                        DockUT = meta.StartUT,
                        TargetPid = meta.VesselPid,
                        TargetGuid = meta.VesselGuid,
                        MergedChildRecordingId = recId,
                        ForeignVesselRecording = true,
                    });
                    joinedClaims++;
                }
            }
            if (joinedClaims > 0 || joinedSkippedUncrewed > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: {joinedClaims.ToString(CultureInfo.InvariantCulture)} crewed recording(s) of a " +
                    "vessel that joined its tree can end another tree's open-ended hold on that vessel " +
                    $"(uncrewed skipped={joinedSkippedUncrewed.ToString(CultureInfo.InvariantCulture)})");
            }
            return collected;
        }

        private static BranchPoint FindBranchPoint(RecordingTree tree, string branchPointId)
        {
            if (tree?.BranchPoints == null || string.IsNullOrEmpty(branchPointId)) return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp != null && string.Equals(bp.Id, branchPointId, StringComparison.Ordinal))
                    return bp;
            }
            return null;
        }

        /// <summary>
        /// Fills <see cref="splitBranchPointsByParent"/> from the committed trees' split branch
        /// points. Returns the number of parent recordings collected.
        /// </summary>
        private int CollectSplitBranchPointsByParent(IReadOnlyList<RecordingTree> trees)
        {
            if (trees == null) return 0;
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree?.BranchPoints == null) continue;
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp == null || string.IsNullOrEmpty(bp.Id) || !IsSplitBranchPointType(bp.Type)
                        || bp.ParentRecordingIds == null)
                        continue;
                    for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                    {
                        string parentId = bp.ParentRecordingIds[p];
                        if (string.IsNullOrEmpty(parentId)) continue;
                        List<string> list;
                        if (!splitBranchPointsByParent.TryGetValue(parentId, out list))
                        {
                            list = new List<string>();
                            splitBranchPointsByParent[parentId] = list;
                        }
                        if (!list.Contains(bp.Id))
                            list.Add(bp.Id);
                    }
                }
            }

            // A later chain segment of a split's parent inherits its split branch points: the
            // optimizer keeps a split before its cut on the earlier segment and moves the end
            // (and the kerbal's open-ended row) to the later one.
            int inherited = 0;
            for (int t = 0; t < trees.Count; t++)
            {
                RecordingTree tree = trees[t];
                if (tree?.Recordings == null) continue;
                foreach (Recording later in tree.Recordings.Values)
                {
                    if (later == null || string.IsNullOrEmpty(later.RecordingId)
                        || string.IsNullOrEmpty(later.ChainId))
                        continue;
                    foreach (Recording earlier in tree.Recordings.Values)
                    {
                        if (earlier == null || ReferenceEquals(earlier, later)
                            || !IsEarlierChainSegment(earlier, later))
                            continue;
                        List<string> earlierBps;
                        if (!splitBranchPointsByParent.TryGetValue(earlier.RecordingId, out earlierBps))
                            continue;
                        List<string> list;
                        if (!splitBranchPointsByParent.TryGetValue(later.RecordingId, out list))
                        {
                            list = new List<string>();
                            splitBranchPointsByParent[later.RecordingId] = list;
                        }
                        for (int i = 0; i < earlierBps.Count; i++)
                        {
                            if (list.Contains(earlierBps[i])) continue;
                            list.Add(earlierBps[i]);
                            inherited++;
                        }
                        double start = earlier.StartUT;
                        double known;
                        if (!double.IsNaN(start)
                            && (!splitChainStartByRecording.TryGetValue(later.RecordingId, out known)
                                || start < known))
                            splitChainStartByRecording[later.RecordingId] = start;
                    }
                }
            }
            if (inherited > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PrePass: {inherited.ToString(CultureInfo.InvariantCulture)} split branch " +
                    "point(s) inherited by later chain segments of their parent");
            }
            return splitBranchPointsByParent.Count;
        }

        /// <summary>
        /// True when <paramref name="earlier"/> is an earlier segment of the same chain and
        /// branch as <paramref name="later"/> (same non-empty <c>ChainId</c> and
        /// <c>ChainBranch</c>, lower <c>ChainIndex</c>). Pure.
        /// </summary>
        internal static bool IsEarlierChainSegment(Recording earlier, Recording later)
        {
            return earlier != null && later != null
                && !string.IsNullOrEmpty(later.ChainId)
                && string.Equals(earlier.ChainId, later.ChainId, StringComparison.Ordinal)
                && earlier.ChainBranch == later.ChainBranch
                && earlier.ChainIndex >= 0
                && earlier.ChainIndex < later.ChainIndex;
        }

        /// <summary>
        /// Fills <see cref="splitHandoffs"/> from the walk's KerbalAssignment rows whose
        /// recording is a child of a branch point, looped or not (the child row is what carries
        /// the kerbal on, under the same reverse-mapped name the parent's row uses). Rows that
        /// hold nobody (<see cref="RoleHoldsKerbal(string)"/>) are skipped. Returns the rows recorded.
        /// </summary>
        private int CollectSplitHandoffs(List<GameAction> actions)
        {
            if (actions == null) return 0;
            int collected = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.KerbalAssignment) continue;
                if (string.IsNullOrEmpty(a.KerbalName) || string.IsNullOrEmpty(a.RecordingId)) continue;
                if (!RoleHoldsKerbal(a.KerbalRole)) continue;

                RecordingMeta meta;
                if (!recordingMeta.TryGetValue(a.RecordingId, out meta)) continue;
                if (string.IsNullOrEmpty(meta.ParentBranchPointId)
                    || string.IsNullOrEmpty(meta.TreeId))
                    continue;

                string key = SplitHandoffKey(meta.TreeId, meta.ParentBranchPointId, a.KerbalName);
                SplitHandoff existing;
                if (splitHandoffs.TryGetValue(key, out existing) && existing.ChildStartUT <= meta.StartUT)
                    continue;
                splitHandoffs[key] = new SplitHandoff
                {
                    ChildRecordingId = a.RecordingId,
                    ChildStartUT = meta.StartUT
                };
                collected++;
            }
            return collected;
        }
    }
}
