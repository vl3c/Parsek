using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure core of the <c>SeatGhostKerbal</c> in-game category
    /// (<see cref="GhostKerbalVisibilityInGameTests"/>): in a flight-scene replay, is every
    /// kerbal a live ghost carries as a kerbal part drawn exactly once? (test-gap list
    /// 2026-11-18, "Command seat replay": a kerbal in an external command seat must not show
    /// twice, in the seat on the vehicle ghost and as an EVA ghost beside it, nor not at all.)
    ///
    /// <para>The input is one frame's occurrences: for every live, positioned ghost, every
    /// <c>kerbalEVA*</c> PART of its ghost snapshot with the crew name the part carries and
    /// whether that part's <c>ghost_part_&lt;pid&gt;</c> node is drawn. A seated kerbal is a
    /// kerbal part of the vehicle (the seat board couples the EVA part into the vessel), an
    /// EVA kerbal the root part of its own vessel, so both shapes are the same occurrence.
    /// Per crew name the frame reads <see cref="FrameVerdict.Once"/>,
    /// <see cref="FrameVerdict.Twice"/> (drawn on two ghosts, or twice on one) or
    /// <see cref="FrameVerdict.Missing"/> (carried by a live ghost, drawn on none).</para>
    ///
    /// <para>A recording boundary can put two ghosts of one kerbal on screen for a frame or
    /// two (the EVA leg ending on the frame the seated leg starts), so a defect is a RUN: the
    /// same bad verdict for one kerbal on <see cref="MinBadRunFrames"/> consecutive sampled
    /// frames. Anything shorter is counted, never failed.</para>
    /// </summary>
    internal static class GhostKerbalVisibility
    {
        internal const string Category = "SeatGhostKerbal";

        /// <summary>Consecutive bad frames for one kerbal before the cell fails.</summary>
        internal const int MinBadRunFrames = 10;

        internal enum FrameVerdict
        {
            Once = 0,
            Twice = 1,
            Missing = 2,
        }

        internal enum CellOutcome
        {
            Pass = 0,
            Fail = 1,
            Skip = 2,
        }

        /// <summary>One kerbal part on one live ghost in one frame.</summary>
        internal struct Occurrence
        {
            internal string Crew;
            internal int GhostIndex;
            internal bool Drawn;
        }

        /// <summary>True for a stock kerbal part name (kerbalEVA, kerbalEVAfemale,
        /// kerbalEVAVintage, kerbalEVAfemaleFuture, ...), in either the cfg or runtime form.</summary>
        internal static bool IsKerbalPartName(string partName)
            => !string.IsNullOrEmpty(partName)
               && partName.StartsWith("kerbalEVA", StringComparison.Ordinal);

        /// <summary>Per crew name, how many drawn copies and how many carriers this frame.</summary>
        internal static SortedDictionary<string, FrameVerdict> ClassifyFrame(
            IList<Occurrence> occurrences, out SortedDictionary<string, int> drawnCounts)
        {
            drawnCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var verdicts = new SortedDictionary<string, FrameVerdict>(StringComparer.Ordinal);
            if (occurrences == null) return verdicts;
            for (int i = 0; i < occurrences.Count; i++)
            {
                string crew = occurrences[i].Crew;
                if (string.IsNullOrEmpty(crew)) continue;
                drawnCounts.TryGetValue(crew, out int n);
                drawnCounts[crew] = n + (occurrences[i].Drawn ? 1 : 0);
            }
            foreach (var kv in drawnCounts)
            {
                verdicts[kv.Key] = kv.Value == 1 ? FrameVerdict.Once
                    : kv.Value == 0 ? FrameVerdict.Missing
                    : FrameVerdict.Twice;
            }
            return verdicts;
        }

        /// <summary>
        /// Folds one frame into the per-kerbal run state: a bad verdict extends that kerbal's
        /// run of the same verdict (a different bad verdict restarts it), a good one ends it.
        /// Returns the longest run reached so far by any kerbal for each bad verdict through
        /// <paramref name="longestTwice"/> / <paramref name="longestMissing"/>.
        /// </summary>
        internal static void Accumulate(SortedDictionary<string, FrameVerdict> frame,
            Dictionary<string, KeyValuePair<FrameVerdict, int>> runs,
            ref int longestTwice, ref int longestMissing)
        {
            if (frame == null || runs == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in frame)
            {
                seen.Add(kv.Key);
                if (kv.Value == FrameVerdict.Once)
                {
                    runs.Remove(kv.Key);
                    continue;
                }
                int len = 1;
                if (runs.TryGetValue(kv.Key, out var prior) && prior.Key == kv.Value)
                    len = prior.Value + 1;
                runs[kv.Key] = new KeyValuePair<FrameVerdict, int>(kv.Value, len);
                if (kv.Value == FrameVerdict.Twice && len > longestTwice) longestTwice = len;
                if (kv.Value == FrameVerdict.Missing && len > longestMissing) longestMissing = len;
            }
            // A kerbal no live ghost carries this frame ends its run.
            var stale = new List<string>();
            foreach (string k in runs.Keys)
                if (!seen.Contains(k)) stale.Add(k);
            for (int i = 0; i < stale.Count; i++) runs.Remove(stale[i]);
        }

        /// <summary>Fail on a long enough bad run, pass once a kerbal was drawn exactly once,
        /// skip when no live ghost carried a kerbal part.</summary>
        internal static CellOutcome DecideOutcome(int onceFrames, int longestTwice, int longestMissing)
        {
            if (longestTwice >= MinBadRunFrames || longestMissing >= MinBadRunFrames)
                return CellOutcome.Fail;
            return onceFrames > 0 ? CellOutcome.Pass : CellOutcome.Skip;
        }

        internal static string FormatSummary(string scene, int frames, int framesWithKerbal, int onceFrames,
            int twiceFrames, int missingFrames, int longestTwice, int longestMissing, string kerbals)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "SeatGhostKerbal summary scene=" + (scene ?? "?")
                + " frames=" + frames.ToString(ic)
                + " framesWithKerbal=" + framesWithKerbal.ToString(ic)
                + " once=" + onceFrames.ToString(ic)
                + " twice=" + twiceFrames.ToString(ic)
                + " missing=" + missingFrames.ToString(ic)
                + " longestTwice=" + longestTwice.ToString(ic)
                + " longestMissing=" + longestMissing.ToString(ic)
                + " kerbals=" + (string.IsNullOrEmpty(kerbals) ? "none" : kerbals);
        }
    }
}
