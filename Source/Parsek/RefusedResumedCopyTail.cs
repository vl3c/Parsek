using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure test of what a resumed copy of a committed tree recorded AFTER the load,
    /// relative to its committed original. Used by the fresh-rollout refusal
    /// (<see cref="ParsekFlight.DisposeFreshRolloutRefusedPendingTree"/>): only a copy
    /// whose post-load content is meaningless may be discarded, because the operator
    /// ruling (FRESH-LAUNCH-REFUSED-TREE-PENDING-LIFETIME) covers dropping a few idle
    /// resumed seconds, not a flown switch segment or a long re-adopted continuation.
    ///
    /// <para>"Meaningless" is: no branch point the committed tree lacks, no recording the
    /// committed tree lacks, and for every shared recording a tail past the committed
    /// recording's EndUT that <see cref="SwitchSegmentNoOpClassifier.IsNoOpResumeTail"/>
    /// calls a no-op (no meaningful part event, segment event, flag, in-window destruction,
    /// non-boring section or orbit change) and whose sampled points span at most
    /// <see cref="MaxDiscardableTailPointSpanSeconds"/>. When in doubt it keeps.</para>
    /// </summary>
    internal static class RefusedResumedCopyTail
    {
        /// <summary>Longest sampled tail (seconds of points past the committed end) that
        /// still counts as the "few idle seconds" the ruling covers.</summary>
        internal const double MaxDiscardableTailPointSpanSeconds = 60.0;

        internal struct Result
        {
            /// <summary>True when the post-load content is meaningless (discardable).</summary>
            public bool IsNoOp;
            /// <summary>First gate that decided to keep, or null when <see cref="IsNoOp"/>.</summary>
            public string KeepReason;
            /// <summary>Trajectory points past the committed end, summed over shared recordings.</summary>
            public int TailPointCount;
            /// <summary>Earliest post-load UT seen (NaN when there is no tail content).</summary>
            public double TailFromUT;
            /// <summary>Latest post-load UT seen (NaN when there is no tail content).</summary>
            public double TailToUT;
        }

        internal static Result Evaluate(RecordingTree copy, RecordingTree committed)
        {
            var r = new Result { TailFromUT = double.NaN, TailToUT = double.NaN };
            if (copy == null || committed == null)
            {
                r.KeepReason = copy == null ? "copy-null" : "committed-null";
                return r;
            }

            var committedBranchIds = new HashSet<string>(StringComparer.Ordinal);
            if (committed.BranchPoints != null)
            {
                for (int i = 0; i < committed.BranchPoints.Count; i++)
                {
                    string id = committed.BranchPoints[i]?.Id;
                    if (!string.IsNullOrEmpty(id))
                        committedBranchIds.Add(id);
                }
            }
            if (copy.BranchPoints != null)
            {
                for (int i = 0; i < copy.BranchPoints.Count; i++)
                {
                    BranchPoint bp = copy.BranchPoints[i];
                    if (bp != null && !committedBranchIds.Contains(bp.Id ?? string.Empty))
                    {
                        r.KeepReason = "new-branch-point:" + (bp.Id ?? "<no-id>") + ":" + bp.Type;
                        return r;
                    }
                }
            }

            if (copy.Recordings == null)
            {
                r.IsNoOp = true;
                return r;
            }

            foreach (KeyValuePair<string, Recording> kvp in copy.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null)
                    continue;
                Recording original = null;
                if (committed.Recordings == null
                    || !committed.Recordings.TryGetValue(kvp.Key, out original)
                    || original == null)
                {
                    r.KeepReason = "pending-only-recording:" + kvp.Key;
                    return r;
                }

                double anchor = original.EndUT;
                int tailPoints = 0;
                double firstTailPoint = double.NaN, lastTailPoint = double.NaN;
                if (rec.Points != null)
                {
                    for (int i = 0; i < rec.Points.Count; i++)
                    {
                        double ut = rec.Points[i].ut;
                        if (ut <= anchor)
                            continue;
                        tailPoints++;
                        if (double.IsNaN(firstTailPoint) || ut < firstTailPoint) firstTailPoint = ut;
                        if (double.IsNaN(lastTailPoint) || ut > lastTailPoint) lastTailPoint = ut;
                    }
                }

                double tailTo = lastTailPoint;
                if (rec.OrbitSegments != null)
                {
                    for (int i = 0; i < rec.OrbitSegments.Count; i++)
                        if (rec.OrbitSegments[i].endUT > anchor)
                            tailTo = MaxIgnoringNaN(tailTo, rec.OrbitSegments[i].endUT);
                }
                if (rec.TrackSections != null)
                {
                    for (int i = 0; i < rec.TrackSections.Count; i++)
                        if (rec.TrackSections[i].endUT > anchor)
                            tailTo = MaxIgnoringNaN(tailTo, rec.TrackSections[i].endUT);
                }
                bool hasTailEvents = HasPartEventAfter(rec, anchor);
                if (tailPoints == 0 && double.IsNaN(tailTo) && !hasTailEvents)
                    continue; // nothing recorded past the committed end

                r.TailPointCount += tailPoints;
                double from = !double.IsNaN(firstTailPoint) ? firstTailPoint : anchor;
                r.TailFromUT = MinIgnoringNaN(r.TailFromUT, from);
                r.TailToUT = MaxIgnoringNaN(r.TailToUT, double.IsNaN(tailTo) ? from : tailTo);

                if (!SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, anchor, out string tailKeep))
                {
                    r.KeepReason = "meaningful-tail:" + kvp.Key + ":" + (tailKeep ?? "<none>");
                    return r;
                }
                if (tailPoints > 1 && lastTailPoint - firstTailPoint > MaxDiscardableTailPointSpanSeconds)
                {
                    r.KeepReason = "long-tail:" + kvp.Key + ":" +
                        (lastTailPoint - firstTailPoint).ToString("F1",
                            System.Globalization.CultureInfo.InvariantCulture) + "s";
                    return r;
                }
            }

            r.IsNoOp = true;
            return r;
        }

        private static bool HasPartEventAfter(Recording rec, double anchor)
        {
            if (rec.PartEvents == null)
                return false;
            for (int i = 0; i < rec.PartEvents.Count; i++)
                if (rec.PartEvents[i].ut > anchor)
                    return true;
            return false;
        }

        private static double MinIgnoringNaN(double a, double b)
            => double.IsNaN(a) ? b : (double.IsNaN(b) ? a : Math.Min(a, b));

        private static double MaxIgnoringNaN(double a, double b)
            => double.IsNaN(a) ? b : (double.IsNaN(b) ? a : Math.Max(a, b));
    }
}
