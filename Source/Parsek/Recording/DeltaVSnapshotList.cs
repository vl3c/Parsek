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

        /// <summary>
        /// Moves every snapshot later than <paramref name="endUT"/> onto <paramref name="endUT"/>,
        /// keeping its figures. For the boring-tail trim: a trimmed tail holds no burn, so the
        /// vessel's delta-v at the new end is the one the later snapshots read, and dropping them
        /// would lose the recording's End figures.
        /// </summary>
        internal static bool ClampPastUT(List<DeltaVSnapshot> list, double endUT)
        {
            if (list == null || list.Count == 0 || double.IsNaN(endUT))
                return false;
            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                DeltaVSnapshot s = list[i];
                if (s == null || s.UT <= endUT)
                    continue;
                list[i] = new DeltaVSnapshot(endUT, s.Trigger, s.Status,
                    s.TotalVacuum, s.TotalSeaLevel, s.TotalActual, ToList(s.Stages));
                changed = true;
            }
            return changed;
        }

        private static List<DeltaVStageReading> ToList(IReadOnlyList<DeltaVStageReading> stages)
        {
            var copy = new List<DeltaVStageReading>(stages != null ? stages.Count : 0);
            if (stages != null)
                for (int i = 0; i < stages.Count; i++)
                    copy.Add(stages[i]);
            return copy;
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
        /// when either side has no Ok snapshot. <paramref name="before"/> and
        /// <paramref name="after"/> are the two snapshots read.
        /// </summary>
        internal static bool TryGetBurnSpent(
            IList<DeltaVSnapshot> list, double burnStartUT, double burnEndUT, double toleranceSeconds,
            out double spent, out double left, out DeltaVSnapshot before, out DeltaVSnapshot after)
        {
            spent = double.NaN;
            left = double.NaN;
            before = null;
            after = null;
            if (list == null || burnEndUT < burnStartUT)
                return false;
            if (!TryGetLeftAt(list, burnStartUT + toleranceSeconds, out before))
                return false;
            after = FirstOkAfter(list, before.UT, burnEndUT - toleranceSeconds);
            if (after == null)
                return false;
            spent = before.TotalVacuum - after.TotalVacuum;
            left = after.TotalVacuum;
            return true;
        }

        /// <summary>
        /// The slack between a staging anchor's UT (the decouple's branch point, a frame or so
        /// after the stage activation) and the recorder's staging instant.
        /// </summary>
        internal const double StagingPairToleranceSeconds = 0.5;

        /// <summary>
        /// How much the stagings of one batch lowered Left: for every
        /// <see cref="DeltaVTrigger.PreStaging"/> snapshot from
        /// <c>fromUT - CachedReadingMaxAgeSeconds - tolerance</c> to <c>toUT + tolerance</c>
        /// (the recorder's reading just before the staging, stamped with its own UT), its
        /// vacuum total minus the settled <see cref="DeltaVTrigger.Staging"/> read that follows
        /// it (the next Staging snapshot, before any later PreStaging, no later than
        /// <c>toUT + windowSeconds</c>). <paramref name="firstIndex"/> / <paramref name="lastIndex"/>
        /// are the first PreStaging's and the last Staging read's list indices. False when the
        /// span holds no PreStaging snapshot (a recording made before the recorder wrote them)
        /// or any pair is incomplete (its Staging read Unknown, missing or late): the drop is
        /// then unknown, never read from an older snapshot, which would count the fuel burned
        /// since that snapshot as dropped. The pair still holds the fuel burned in the second
        /// or two between its two reads.
        /// </summary>
        internal static bool TryGetStagingDrop(
            IList<DeltaVSnapshot> list, double fromUT, double toUT, double windowSeconds,
            out double drop, out int firstIndex, out int lastIndex)
        {
            drop = double.NaN;
            firstIndex = -1;
            lastIndex = -1;
            if (list == null || double.IsNaN(fromUT))
                return false;
            if (double.IsNaN(toUT) || toUT < fromUT)
                toUT = fromUT;
            double lo = fromUT - DeltaVSnapshotTracker.CachedReadingMaxAgeSeconds - StagingPairToleranceSeconds;
            double hi = toUT + StagingPairToleranceSeconds;
            double sum = 0.0;
            for (int i = 0; i < list.Count; i++)
            {
                DeltaVSnapshot pre = list[i];
                if (pre == null || pre.Trigger != DeltaVTrigger.PreStaging || pre.UT < lo || pre.UT > hi)
                    continue;
                int post = NextStagingRead(list, i);
                if (!pre.IsOk || post < 0 || !list[post].IsOk || list[post].UT > toUT + windowSeconds)
                    return false;
                sum += pre.TotalVacuum - list[post].TotalVacuum;
                if (firstIndex < 0)
                    firstIndex = i;
                lastIndex = post;
            }
            if (firstIndex < 0)
                return false;
            drop = sum;
            return true;
        }

        // The Staging read that answers the PreStaging at preIndex: the next Staging snapshot,
        // -1 when another PreStaging comes first (that staging's own pair) or none follows.
        private static int NextStagingRead(IList<DeltaVSnapshot> list, int preIndex)
        {
            for (int k = preIndex + 1; k < list.Count; k++)
            {
                DeltaVSnapshot s = list[k];
                if (s == null) continue;
                if (s.Trigger == DeltaVTrigger.Staging) return k;
                if (s.Trigger == DeltaVTrigger.PreStaging) return -1;
            }
            return -1;
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
