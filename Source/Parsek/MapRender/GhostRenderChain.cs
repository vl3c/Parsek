using System.Collections.Generic;

namespace Parsek.MapRender
{
    /// <summary>
    /// The per-(committed-member, cycle-instance) ordered list of typed <see cref="RenderSegment"/>s
    /// for one ghost — the central new abstraction the old code lacked (design §6.2).
    ///
    /// It is PER MEMBER (one <see cref="CommittedIndex"/>), not per whole mission: a looped
    /// mission's full launch→transfer→landing chain spans several committed members, and the
    /// existing LoopUnit + span clock already sequence which member is active at a given UT (design
    /// §6.6). This object is a render-oriented view; it is built once per
    /// (BuildSignature, reaim-window index, InstanceKey) and cached by the assembler.
    /// </summary>
    internal sealed class GhostRenderChain
    {
        internal string RecordingId { get; }
        /// <summary>Positional index into the committed-recordings list (the LoopUnitSet contract).</summary>
        internal int CommittedIndex { get; }
        /// <summary>Cycle/instance discriminator — distinguishes overlapping self-loop instances (design §10.8).</summary>
        internal int InstanceKey { get; }
        /// <summary>Segments ordered by <see cref="RenderSegment.StartUT"/>; gaps allowed only at FlexibleSoi seams.</summary>
        internal IReadOnlyList<RenderSegment> Segments { get; }
        internal double WindowStartUT { get; }
        internal double WindowEndUT { get; }
        /// <summary>Solver declined re-aim for this window → assembled from the recorded trajectory as-is (design §6.9).</summary>
        internal bool IsFaithfulFallback { get; }

        internal GhostRenderChain(
            string recordingId,
            int committedIndex,
            int instanceKey,
            IReadOnlyList<RenderSegment> segments,
            double windowStartUT,
            double windowEndUT,
            bool isFaithfulFallback = false)
        {
            RecordingId = recordingId;
            CommittedIndex = committedIndex;
            InstanceKey = instanceKey;
            Segments = segments ?? System.Array.Empty<RenderSegment>();
            WindowStartUT = windowStartUT;
            WindowEndUT = windowEndUT;
            IsFaithfulFallback = isFaithfulFallback;
        }

        internal int SegmentCount => Segments.Count;

        /// <summary>
        /// O(log n) locate: the index of the segment containing <paramref name="ut"/> (assembled-chain
        /// clock), or -1 if <paramref name="ut"/> falls in a gap / outside all segments. A non-last
        /// segment owns [StartUT, EndUT); the last segment owns [StartUT, EndUT] (inclusive end), so a
        /// boundary UT shared by two adjacent segments belongs to the later one.
        /// </summary>
        internal int LocateSegmentIndex(double ut)
        {
            var segs = Segments;
            int n = segs.Count;
            if (n == 0) return -1;

            // rightmost segment with StartUT <= ut
            int lo = 0, hi = n - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (segs[mid].StartUT <= ut) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found < 0) return -1; // ut is before the first segment

            var s = segs[found];
            bool isLast = found == n - 1;
            bool contains = isLast ? (ut <= s.EndUT) : (ut < s.EndUT);
            return contains ? found : -1; // -1 = a gap after segs[found]
        }

        internal bool TryGetSegment(double ut, out RenderSegment segment, out int index)
        {
            index = LocateSegmentIndex(ut);
            if (index >= 0) { segment = Segments[index]; return true; }
            segment = default(RenderSegment);
            return false;
        }

        /// <summary>
        /// Classify a UT (already mapped into the assembled-chain clock by the sampler) into the
        /// three-valued <see cref="Coverage"/>. Outside the window → OutsideWindow; inside the window
        /// but in no segment → InInteriorGap; otherwise InSegment.
        /// </summary>
        internal Coverage ClassifyCoverage(double ut, out RenderSegment segment, out int index)
        {
            segment = default(RenderSegment);
            index = -1;
            if (ut < WindowStartUT || ut > WindowEndUT)
                return Coverage.OutsideWindow;
            index = LocateSegmentIndex(ut);
            if (index >= 0)
            {
                segment = Segments[index];
                return Coverage.InSegment;
            }
            return Coverage.InInteriorGap;
        }
    }
}
