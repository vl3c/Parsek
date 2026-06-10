using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Pure departure-scan selection helpers for the deterministic re-aim E2E harness
    // (docs/dev/plans/reaim-resolver-reliability.md, M-MIS-1). A feasibility scan samples one
    // synodic period of candidate departures at equal steps and records which of them synthesize
    // a window-0 transfer. Because the scan covers EXACTLY one synodic period the feasibility is
    // CYCLIC: the feasible band can wrap from the last index back to index 0. These helpers pick
    // the two deterministic departures the harness drives: the band CENTER (the robust mid-band
    // departure for the strict every-window assertion) and the band EDGE (the first success in
    // scan order - the fragile departure the old live-UT-seeded test picked by accident, which
    // produced the intermittent "window k must resolve" failures).
    internal static class ReaimFeasibilityScan
    {
        /// <summary>Index of the first true entry in scan order, or -1 when none (or null/empty
        /// scan). Pure.</summary>
        internal static int FirstSuccessIndex(IReadOnlyList<bool> scan)
        {
            if (scan == null)
                return -1;
            for (int i = 0; i < scan.Count; i++)
            {
                if (scan[i])
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Center index of the longest contiguous run of true entries, or -1 when none (or
        /// null/empty scan). With <paramref name="cyclic"/> a run may WRAP from the last index
        /// back to index 0 (the scan covers exactly one synodic period, so index 0 is the
        /// neighbor of index n-1). Tie between equal-length runs: the run whose start index
        /// comes first in linear scan order wins (a wrapped run starts near the END, so it wins
        /// only when strictly longer). Even-length run: the lower-middle index. All-true scan:
        /// the run is taken to start at index 0. Pure.
        /// </summary>
        internal static int CenterOfLongestRunIndex(IReadOnlyList<bool> scan, bool cyclic)
        {
            if (scan == null || scan.Count == 0)
                return -1;
            int n = scan.Count;

            int bestStart = -1, bestLen = 0;
            int headRunLen = 0; // length of the run starting at index 0 (wrap-merge candidate)
            int i = 0;
            while (i < n)
            {
                if (!scan[i])
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < n && scan[i])
                    i++;
                int len = i - start;
                if (start == 0)
                    headRunLen = len;
                if (len > bestLen)
                {
                    bestLen = len;
                    bestStart = start;
                }
            }
            if (bestLen == 0)
                return -1;

            // Cyclic wrap: when the scan both starts and ends inside a run (and is not all-true,
            // which is a single run already counted), the tail run ending at n-1 and the head run
            // starting at 0 are ONE wrapped run.
            if (cyclic && headRunLen > 0 && headRunLen < n && scan[n - 1])
            {
                int tailStart = n - 1;
                while (tailStart > 0 && scan[tailStart - 1])
                    tailStart--;
                int wrappedLen = (n - tailStart) + headRunLen;
                if (wrappedLen > bestLen)
                {
                    bestLen = wrappedLen;
                    bestStart = tailStart;
                }
            }

            return (bestStart + (bestLen - 1) / 2) % n;
        }
    }
}
