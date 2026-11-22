using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure list operations on a recording's <see cref="Recording.DeltaVSnapshots"/>: the
    /// insert the recorder uses, the carries through optimizer splits and merges, and the
    /// Spent / Left reads the Profile window (design-mission-profile.md section 8.1) builds on.
    /// A null list means no snapshots.
    /// </summary>
    internal static class DeltaVSnapshotList
    {
        /// <summary>The most snapshots one recording keeps; a few dozen is a normal flight.</summary>
        internal const int MaxSnapshotsPerRecording = 400;

        /// <summary>
        /// Inserts <paramref name="snapshot"/> after every snapshot whose UT is not later, so
        /// the list stays in UT order with same-UT snapshots in insertion order. Returns false
        /// (nothing added) when the list already holds <paramref name="cap"/> snapshots.
        /// </summary>
        internal static bool TryInsertByUT(
            ref List<DeltaVSnapshot> list, DeltaVSnapshot snapshot, int cap = MaxSnapshotsPerRecording)
        {
            if (snapshot == null)
                return false;
            if (list == null)
                list = new List<DeltaVSnapshot>();
            if (list.Count >= cap)
                return false;

            int index = list.Count;
            while (index > 0 && list[index - 1] != null && list[index - 1].UT > snapshot.UT)
                index--;
            list.Insert(index, snapshot);
            return true;
        }

        /// <summary>Removes <paramref name="snapshot"/> by reference. Returns true when found.</summary>
        internal static bool RemoveByReference(List<DeltaVSnapshot> list, DeltaVSnapshot snapshot)
        {
            if (list == null || snapshot == null)
                return false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i], snapshot))
                {
                    list.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Split carry: moves every snapshot at or after <paramref name="splitUT"/> to the
        /// returned list (the later half), the same rule the part events follow. Either side
        /// becomes null when it ends up empty.
        /// </summary>
        internal static List<DeltaVSnapshot> SplitAtUT(ref List<DeltaVSnapshot> first, double splitUT)
        {
            if (first == null || first.Count == 0)
            {
                first = null;
                return null;
            }

            List<DeltaVSnapshot> kept = null;
            List<DeltaVSnapshot> moved = null;
            for (int i = 0; i < first.Count; i++)
            {
                DeltaVSnapshot s = first[i];
                if (s == null)
                    continue;
                if (s.UT >= splitUT)
                    (moved ?? (moved = new List<DeltaVSnapshot>())).Add(s);
                else
                    (kept ?? (kept = new List<DeltaVSnapshot>())).Add(s);
            }
            first = kept;
            return moved;
        }

        /// <summary>
        /// Merge carry: the snapshots of both recordings in UT order (a stable merge, so
        /// same-UT snapshots keep target-first order). Null when both are empty.
        /// </summary>
        internal static List<DeltaVSnapshot> Concat(List<DeltaVSnapshot> target, List<DeltaVSnapshot> absorbed)
        {
            int count = (target?.Count ?? 0) + (absorbed?.Count ?? 0);
            if (count == 0)
                return null;

            var merged = new List<DeltaVSnapshot>(count);
            if (target != null)
                merged.AddRange(target);
            if (absorbed != null)
            {
                for (int i = 0; i < absorbed.Count; i++)
                    TryInsertByUT(ref merged, absorbed[i], int.MaxValue);
            }
            merged.RemoveAll(s => s == null);
            return merged.Count > 0 ? merged : null;
        }

        /// <summary>Removes every snapshot later than <paramref name="cutoffUT"/>.</summary>
        internal static bool TrimPastUT(List<DeltaVSnapshot> list, double cutoffUT)
        {
            if (list == null || list.Count == 0)
                return false;
            int before = list.Count;
            list.RemoveAll(s => s == null || s.UT > cutoffUT);
            return list.Count != before;
        }

        internal static int CountUnknown(List<DeltaVSnapshot> list)
        {
            if (list == null)
                return 0;
            int unknown = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && !list[i].IsOk)
                    unknown++;
            }
            return unknown;
        }

        /// <summary>
        /// Left at <paramref name="ut"/>: the vacuum total of the last Ok snapshot taken at or
        /// before it. False when no Ok snapshot is that early.
        /// </summary>
        internal static bool TryGetLeftAt(
            IList<DeltaVSnapshot> list, double ut, out DeltaVSnapshot snapshot)
        {
            snapshot = null;
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                DeltaVSnapshot s = list[i];
                if (s == null || !s.IsOk)
                    continue;
                if (s.UT > ut)
                    break;
                snapshot = s;
            }
            return snapshot != null;
        }

        /// <summary>
        /// A burn's Spent and Left: <paramref name="spent"/> = vacuum left before the burn
        /// minus vacuum left after it, <paramref name="left"/> = vacuum left after it. Before
        /// is the last Ok snapshot at or before <c>burnStartUT + toleranceSeconds</c> (the
        /// burn-start read lands a few frames after ignition); after is the first Ok snapshot
        /// later than that one and at or after <c>burnEndUT - toleranceSeconds</c>. False
        /// when either side has no Ok snapshot.
        /// </summary>
        internal static bool TryGetBurnSpent(
            IList<DeltaVSnapshot> list, double burnStartUT, double burnEndUT, double toleranceSeconds,
            out double spent, out double left)
        {
            spent = double.NaN;
            left = double.NaN;
            if (list == null || burnEndUT < burnStartUT)
                return false;
            if (!TryGetLeftAt(list, burnStartUT + toleranceSeconds, out DeltaVSnapshot before))
                return false;
            DeltaVSnapshot after = FirstOkAfter(list, before.UT, burnEndUT - toleranceSeconds);
            if (after == null)
                return false;
            spent = before.TotalVacuum - after.TotalVacuum;
            left = after.TotalVacuum;
            return true;
        }

        /// <summary>
        /// How much a staging lowered Left without any burn: the vacuum left at
        /// <paramref name="stagingUT"/> minus the first Ok snapshot after it and no later than
        /// <c>stagingUT + windowSeconds</c> (the settled read after the staging). Positive
        /// when fuel was dropped. False when either side has no Ok snapshot.
        /// </summary>
        internal static bool TryGetStagingDrop(
            IList<DeltaVSnapshot> list, double stagingUT, double windowSeconds, out double drop)
        {
            drop = double.NaN;
            if (list == null)
                return false;
            if (!TryGetLeftAt(list, stagingUT, out DeltaVSnapshot before))
                return false;
            DeltaVSnapshot after = FirstOkAfter(list, stagingUT, stagingUT);
            if (after == null || after.UT > stagingUT + windowSeconds)
                return false;
            drop = before.TotalVacuum - after.TotalVacuum;
            return true;
        }

        /// <summary>The first Ok snapshot later than <paramref name="afterUT"/> and at or after <paramref name="notBeforeUT"/>.</summary>
        private static DeltaVSnapshot FirstOkAfter(IList<DeltaVSnapshot> list, double afterUT, double notBeforeUT)
        {
            for (int i = 0; i < list.Count; i++)
            {
                DeltaVSnapshot s = list[i];
                if (s == null || !s.IsOk)
                    continue;
                if (s.UT > afterUT && s.UT >= notBeforeUT)
                    return s;
            }
            return null;
        }
    }
}
