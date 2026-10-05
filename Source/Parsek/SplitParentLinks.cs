using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Parent links across a recording split. A split cuts one recording at UT t into a head
    /// (the original id, [start, t]) and a tail (a new id, [t, end]): the optimizer's environment
    /// / body split (<c>RecordingStore.RunOptimizationSplitPass</c>) and the Re-Fly HEAD/TIP split
    /// (<see cref="RecordingTreeSplitter"/>). Every link that names the split recording as a
    /// PARENT of something that happened at or after t belongs to the tail:
    /// <list type="bullet">
    /// <item>a <see cref="BranchPoint"/> whose <c>ParentRecordingIds</c> lists it and whose UT is
    /// at or after t (any type: a decouple the vessel flew on past, a dock, an EVA, a ground part
    /// placement, the branch point that ends it);</item>
    /// <item>a child recording whose <see cref="Recording.ParentRecordingId"/> names it (an EVA
    /// kerbal, a switch continuation) and whose link UT is at or after t. The link UT is the UT
    /// of the branch point that created the child's chain-first segment, else that segment's
    /// start, so every segment of a split child moves together.</item>
    /// </list>
    /// An event exactly at the cut belongs to the tail: the cut sample is the tail's first point,
    /// <c>SplitAtSection</c> forwards the permanent part state onto the tail at that UT, and the
    /// ledger retag (<c>Ledger.RetagActionsForSplitSecondHalf</c>) moves rows at the cut to the
    /// tail. <see cref="BoundaryEpsilonSeconds"/> is the tolerance the optimizer already used for
    /// the moved <c>ChildBranchPointId</c>.
    ///
    /// The merge that reverses a split (<c>RecordingStore.RunOptimizationMergePass</c>) points
    /// every link that names the absorbed recording back at the survivor.
    /// <see cref="RepairStaleParents"/> applies the same rule on load to trees written before the
    /// split moved these links.
    ///
    /// Not a format change: the links are the same fields in the same shape; only which
    /// recording id they hold changes, to the one whose time range contains the event. The
    /// reference-frame anchors (<see cref="Recording.ParentAnchorRecordingId"/>,
    /// <see cref="TrackSection.anchorRecordingId"/>) are not parent links of this kind and are
    /// left alone: they name the trajectory a frame resolves against, and the relative-anchor
    /// resolver already continues through a same-chain successor.
    /// </summary>
    internal static class SplitParentLinks
    {
        internal const string Tag = "SplitParentLinks";
        internal const double BoundaryEpsilonSeconds = 0.0001;
        private const int MaxPerItemRepairLines = 20;

        internal enum LinkKind
        {
            BranchPointParent = 0,
            ChildParentRecording = 1,
        }

        /// <summary>One reversible link rewrite (the Re-Fly splitter's rollback ledger reads it).</summary>
        internal struct LinkChange
        {
            internal LinkKind Kind;
            internal BranchPoint BranchPoint;
            internal int ParentIndex;
            internal Recording Child;
            internal string OldId;
            internal string NewId;
        }

        /// <summary>
        /// True when an event at <paramref name="eventUT"/> belongs to a split tail that starts at
        /// <paramref name="tailStartUT"/> (at or after the cut, within
        /// <see cref="BoundaryEpsilonSeconds"/>). False for any non-finite input.
        /// </summary>
        internal static bool BelongsToTail(double eventUT, double tailStartUT)
        {
            if (double.IsNaN(eventUT) || double.IsInfinity(eventUT)) return false;
            if (double.IsNaN(tailStartUT) || double.IsInfinity(tailStartUT)) return false;
            return eventUT >= tailStartUT - BoundaryEpsilonSeconds;
        }

        /// <summary>
        /// How far from a segment's end a branch point may sit and still be the one the segment
        /// ends at (a recorded end and its branch UT agree to a frame).
        /// </summary>
        internal const double SegmentEndToleranceSeconds = 1.0;

        /// <summary>
        /// True when <paramref name="bp"/> names <paramref name="rec"/> only because a split
        /// moved it onto this segment and the vessel flew on past it: <paramref name="rec"/> is
        /// a later segment of a split chain (<c>ChainIndex &gt; 0</c>), the branch point sits at
        /// or after the segment's start, and it is not the branch point the segment ends at
        /// (its <c>ChildBranchPointId</c>, or one at its end UT). Before this fix every such
        /// branch point stayed on the chain's first segment; readers that decide a segment's
        /// continuation or leafness from its branch points skip these so they answer as they
        /// always did (the Missions composition, the spawn leaf safety net).
        /// </summary>
        internal static bool IsFlownPastOnLaterSegment(Recording rec, BranchPoint bp)
        {
            if (rec == null || bp == null) return false;
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex <= 0) return false;
            if (string.Equals(rec.ChildBranchPointId, bp.Id, StringComparison.Ordinal)) return false;
            if (!TryGetUsableStart(rec, out double start)) return false;
            if (!BelongsToTail(bp.UT, start)) return false;
            double end = rec.EndUT;
            if (!double.IsNaN(end) && Math.Abs(bp.UT - end) <= SegmentEndToleranceSeconds) return false;
            return true;
        }

        /// <summary>Reverses one <see cref="LinkChange"/> (writes the old id back).</summary>
        internal static void Undo(LinkChange change)
        {
            switch (change.Kind)
            {
                case LinkKind.BranchPointParent:
                    var parents = change.BranchPoint?.ParentRecordingIds;
                    if (parents != null && change.ParentIndex >= 0 && change.ParentIndex < parents.Count)
                        parents[change.ParentIndex] = change.OldId;
                    break;
                case LinkKind.ChildParentRecording:
                    if (change.Child != null)
                        change.Child.ParentRecordingId = change.OldId;
                    break;
            }
        }

        /// <summary>
        /// After a split of <paramref name="headId"/> whose tail <paramref name="tail"/> starts
        /// at <paramref name="tailStartUT"/>: repoints every branch-point parent entry and every
        /// child <see cref="Recording.ParentRecordingId"/> in <paramref name="tree"/> that names
        /// the head and belongs to the tail. Appends each rewrite to <paramref name="changes"/>
        /// when given. Returns the number of rewrites. The tail need not be in the tree yet.
        /// </summary>
        internal static int RepointToTail(
            RecordingTree tree, string headId, Recording tail, double tailStartUT,
            List<LinkChange> changes, string context)
        {
            if (tree == null || string.IsNullOrEmpty(headId) || tail == null
                || string.IsNullOrEmpty(tail.RecordingId)
                || string.Equals(headId, tail.RecordingId, StringComparison.Ordinal))
                return 0;

            var ic = CultureInfo.InvariantCulture;
            int bpRewrites = 0;
            if (tree.BranchPoints != null)
            {
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp?.ParentRecordingIds == null) continue;
                    if (!BelongsToTail(bp.UT, tailStartUT)) continue;
                    for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                    {
                        if (!string.Equals(bp.ParentRecordingIds[p], headId, StringComparison.Ordinal))
                            continue;
                        bp.ParentRecordingIds[p] = tail.RecordingId;
                        changes?.Add(new LinkChange
                        {
                            Kind = LinkKind.BranchPointParent, BranchPoint = bp, ParentIndex = p,
                            OldId = headId, NewId = tail.RecordingId,
                        });
                        bpRewrites++;
                        ParsekLog.Verbose(Tag,
                            $"{context ?? "split"}: BranchPoint '{bp.Id}' type={bp.Type} " +
                            $"ut={bp.UT.ToString("R", ic)} parent[{p.ToString(ic)}] {headId} -> {tail.RecordingId} " +
                            $"(tailStartUT={tailStartUT.ToString("R", ic)})");
                    }
                }
            }

            int childRewrites = 0;
            if (tree.Recordings != null)
            {
                foreach (Recording child in SortedById(tree.Recordings.Values))
                {
                    if (!string.Equals(child.ParentRecordingId, headId, StringComparison.Ordinal))
                        continue;
                    if (string.Equals(child.RecordingId, tail.RecordingId, StringComparison.Ordinal))
                        continue;
                    // A segment of the split recording's own chain is not its child (a legacy
                    // chain names its predecessor here); the split leaves that link alone.
                    if (SameChain(child, tail)) continue;
                    double linkUT = ChildLinkUT(tree, child);
                    if (!BelongsToTail(linkUT, tailStartUT)) continue;
                    child.ParentRecordingId = tail.RecordingId;
                    changes?.Add(new LinkChange
                    {
                        Kind = LinkKind.ChildParentRecording, Child = child,
                        OldId = headId, NewId = tail.RecordingId,
                    });
                    childRewrites++;
                    ParsekLog.Verbose(Tag,
                        $"{context ?? "split"}: child '{child.RecordingId}' ParentRecordingId " +
                        $"{headId} -> {tail.RecordingId} (linkUT={linkUT.ToString("R", ic)} " +
                        $"tailStartUT={tailStartUT.ToString("R", ic)})");
                }
            }

            if (bpRewrites > 0 || childRewrites > 0)
                ParsekLog.Info(Tag,
                    $"{context ?? "split"}: tree={tree.Id} head={headId} tail={tail.RecordingId} " +
                    $"cut={tailStartUT.ToString("R", ic)} repointed bpParents={bpRewrites.ToString(ic)} " +
                    $"childParents={childRewrites.ToString(ic)}");
            return bpRewrites + childRewrites;
        }

        /// <summary>
        /// After a merge absorbed <paramref name="absorbedId"/> into
        /// <paramref name="survivorId"/>: points every branch-point parent entry and every child
        /// <see cref="Recording.ParentRecordingId"/> that names the absorbed recording at the
        /// survivor (a parent list that already holds the survivor drops the absorbed entry
        /// instead of listing the survivor twice). Returns the number of rewrites.
        /// </summary>
        internal static int RepointToSurvivor(
            RecordingTree tree, string absorbedId, string survivorId, string context)
        {
            if (tree == null || string.IsNullOrEmpty(absorbedId) || string.IsNullOrEmpty(survivorId)
                || string.Equals(absorbedId, survivorId, StringComparison.Ordinal))
                return 0;

            var ic = CultureInfo.InvariantCulture;
            int bpRewrites = 0;
            if (tree.BranchPoints != null)
            {
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp?.ParentRecordingIds == null) continue;
                    for (int p = bp.ParentRecordingIds.Count - 1; p >= 0; p--)
                    {
                        if (!string.Equals(bp.ParentRecordingIds[p], absorbedId, StringComparison.Ordinal))
                            continue;
                        if (bp.ParentRecordingIds.Contains(survivorId))
                            bp.ParentRecordingIds.RemoveAt(p);
                        else
                            bp.ParentRecordingIds[p] = survivorId;
                        bpRewrites++;
                        ParsekLog.Verbose(Tag,
                            $"{context ?? "merge"}: BranchPoint '{bp.Id}' type={bp.Type} " +
                            $"ut={bp.UT.ToString("R", ic)} parent {absorbedId} -> {survivorId}");
                    }
                }
            }

            int childRewrites = 0;
            if (tree.Recordings != null)
            {
                foreach (Recording child in SortedById(tree.Recordings.Values))
                {
                    if (!string.Equals(child.ParentRecordingId, absorbedId, StringComparison.Ordinal))
                        continue;
                    if (string.Equals(child.RecordingId, survivorId, StringComparison.Ordinal))
                        continue;
                    if (tree.Recordings.TryGetValue(survivorId, out Recording survivor)
                        && SameChain(child, survivor))
                        continue; // own-chain link: the split leaves it alone, so does the merge
                    child.ParentRecordingId = survivorId;
                    childRewrites++;
                    ParsekLog.Verbose(Tag,
                        $"{context ?? "merge"}: child '{child.RecordingId}' ParentRecordingId " +
                        $"{absorbedId} -> {survivorId}");
                }
            }

            if (bpRewrites > 0 || childRewrites > 0)
                ParsekLog.Info(Tag,
                    $"{context ?? "merge"}: tree={tree.Id} absorbed={absorbedId} survivor={survivorId} " +
                    $"repointed bpParents={bpRewrites.ToString(ic)} childParents={childRewrites.ToString(ic)}");
            return bpRewrites + childRewrites;
        }

        /// <summary>
        /// Load-time repair for trees written before a split moved its parent links. For every
        /// branch-point parent entry and every child <see cref="Recording.ParentRecordingId"/>,
        /// walks the named recording forward through its chain successors (same
        /// <c>ChainId</c> and <c>ChainBranch</c>, the single segment at <c>ChainIndex + 1</c>,
        /// with a usable start) while the event belongs to the successor by
        /// <see cref="BelongsToTail"/>, and repoints the link to the segment it stops on. Never
        /// moves a link backwards, never onto a segment the branch point itself created, and
        /// stops on an ambiguous successor. Deterministic and idempotent: an already-correct
        /// tree is untouched and logs nothing; a repaired tree logs one Info summary. Reads
        /// the hydrated trajectories (segment starts), so callers run it after sidecar load.
        /// Returns the number of rewrites.
        /// </summary>
        internal static int RepairStaleParents(RecordingTree tree, string context)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            var ic = CultureInfo.InvariantCulture;
            int bpRewrites = 0;
            int childRewrites = 0;
            int skippedDuplicates = 0;
            int lines = 0;

            if (tree.BranchPoints != null)
            {
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp?.ParentRecordingIds == null) continue;
                    for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                    {
                        string id = bp.ParentRecordingIds[p];
                        if (string.IsNullOrEmpty(id)
                            || !tree.Recordings.TryGetValue(id, out Recording named)
                            || named == null)
                            continue;
                        Recording owner = WalkToOwningSegment(tree, named, bp.UT, bp);
                        if (owner == null || ReferenceEquals(owner, named)) continue;
                        if (bp.ParentRecordingIds.Contains(owner.RecordingId))
                        {
                            skippedDuplicates++;
                            continue;
                        }
                        bp.ParentRecordingIds[p] = owner.RecordingId;
                        bpRewrites++;
                        if (lines++ < MaxPerItemRepairLines)
                            ParsekLog.Verbose(Tag,
                                $"RepairStaleParents: context={context ?? "unknown"} tree={tree.Id} " +
                                $"BranchPoint '{bp.Id}' type={bp.Type} ut={bp.UT.ToString("R", ic)} " +
                                $"parent[{p.ToString(ic)}] {id} -> {owner.RecordingId}");
                    }
                }
            }

            foreach (Recording child in SortedById(tree.Recordings.Values))
            {
                string id = child.ParentRecordingId;
                if (string.IsNullOrEmpty(id)
                    || !tree.Recordings.TryGetValue(id, out Recording named)
                    || named == null)
                    continue;
                if (SameChain(child, named)) continue;
                double linkUT = ChildLinkUT(tree, child);
                Recording owner = WalkToOwningSegment(tree, named, linkUT, null);
                if (owner == null || ReferenceEquals(owner, named)
                    || ReferenceEquals(owner, child) || SameChain(child, owner))
                    continue;
                child.ParentRecordingId = owner.RecordingId;
                childRewrites++;
                if (lines++ < MaxPerItemRepairLines)
                    ParsekLog.Verbose(Tag,
                        $"RepairStaleParents: context={context ?? "unknown"} tree={tree.Id} " +
                        $"child '{child.RecordingId}' ParentRecordingId {id} -> {owner.RecordingId} " +
                        $"(linkUT={linkUT.ToString("R", ic)})");
            }

            if (bpRewrites > 0 || childRewrites > 0 || skippedDuplicates > 0)
                ParsekLog.Info(Tag,
                    $"RepairStaleParents: context={context ?? "unknown"} tree={tree.Id} " +
                    $"repointed bpParents={bpRewrites.ToString(ic)} childParents={childRewrites.ToString(ic)} " +
                    $"skippedDuplicateParents={skippedDuplicates.ToString(ic)}");
            return bpRewrites + childRewrites;
        }

        /// <summary>
        /// The UT a child's parent link refers to: the UT of the branch point that created the
        /// child's chain-first segment, else that segment's start. NaN when neither is known.
        /// </summary>
        internal static double ChildLinkUT(RecordingTree tree, Recording child)
        {
            if (child == null) return double.NaN;
            Recording first = ChainFirstSegment(tree, child);
            if (!string.IsNullOrEmpty(first.ParentBranchPointId) && tree?.BranchPoints != null)
            {
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp != null && string.Equals(bp.Id, first.ParentBranchPointId, StringComparison.Ordinal))
                        return bp.UT;
                }
            }
            return TryGetUsableStart(first, out double start) ? start : double.NaN;
        }

        private static Recording ChainFirstSegment(RecordingTree tree, Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex <= 0
                || tree?.Recordings == null)
                return rec;
            Recording best = rec;
            foreach (Recording candidate in tree.Recordings.Values)
            {
                if (candidate == null || !SameChain(candidate, rec)) continue;
                if (candidate.ChainIndex < 0 || candidate.ChainIndex >= best.ChainIndex) continue;
                best = candidate;
            }
            return best;
        }

        private static Recording WalkToOwningSegment(
            RecordingTree tree, Recording named, double eventUT, BranchPoint bp)
        {
            if (named == null || string.IsNullOrEmpty(named.ChainId) || named.ChainIndex < 0)
                return named;
            if (double.IsNaN(eventUT) || double.IsInfinity(eventUT))
                return named;

            Recording cur = named;
            var visited = new HashSet<string>(StringComparer.Ordinal) { cur.RecordingId ?? "" };
            while (true)
            {
                Recording next = UniqueChainSuccessor(tree, cur);
                if (next == null || !visited.Add(next.RecordingId ?? "")) break;
                if (!TryGetUsableStart(next, out double nextStart)) break;
                if (!BelongsToTail(eventUT, nextStart)) break;
                if (bp != null
                    && (string.Equals(next.ParentBranchPointId, bp.Id, StringComparison.Ordinal)
                        || (bp.ChildRecordingIds != null && bp.ChildRecordingIds.Contains(next.RecordingId))))
                    break;
                cur = next;
            }
            return cur;
        }

        private static Recording UniqueChainSuccessor(RecordingTree tree, Recording cur)
        {
            Recording found = null;
            foreach (Recording candidate in tree.Recordings.Values)
            {
                if (candidate == null || ReferenceEquals(candidate, cur)) continue;
                if (!SameChain(candidate, cur)) continue;
                if (candidate.ChainIndex != cur.ChainIndex + 1) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        private static bool SameChain(Recording a, Recording b)
            => a != null && b != null
               && !string.IsNullOrEmpty(a.ChainId)
               && string.Equals(a.ChainId, b.ChainId, StringComparison.Ordinal)
               && a.ChainBranch == b.ChainBranch;

        private static bool TryGetUsableStart(Recording rec, out double start)
        {
            start = double.NaN;
            if (rec == null) return false;
            if (rec.TryGetActualTrajectoryBounds(out _, out _) || !double.IsNaN(rec.ExplicitStartUT))
            {
                start = rec.StartUT;
                return !double.IsNaN(start) && !double.IsInfinity(start);
            }
            return false;
        }

        private static List<Recording> SortedById(IEnumerable<Recording> recordings)
        {
            var list = new List<Recording>();
            foreach (Recording r in recordings)
                if (r != null) list.Add(r);
            list.Sort((a, b) => string.CompareOrdinal(a.RecordingId, b.RecordingId));
            return list;
        }
    }
}
