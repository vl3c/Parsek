using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// One stock reward threshold a world-record row crossed (design 7.8, section 3.15 step 4):
    /// its position in the node's band and what stock paid for it.
    ///
    /// <para><see cref="Index"/> is stock's own 1-based interval number, the
    /// <c>rewardInterval</c> field of <c>KSPAchievements.RecordsAltitude</c> / <c>Depth</c> /
    /// <c>Speed</c> / <c>Distance</c> at the moment stock pays it (decompiled KSP 1.12.5
    /// <c>iterateVessels</c>: <c>AwardProgressInterval(..., rewardInterval, RecordSplit, ...)</c>
    /// pays, and only then <c>rewardThreshold = FindNextRecord(rewardThreshold, ..., ref
    /// rewardInterval)</c> advances it). Interval i is threshold i of
    /// <c>ProgressUtilities.FindNextRecord</c>, i in 1..<c>ContractDefs.Progression.RecordSplit</c>,
    /// the same order <see cref="KspStatePatcher.TryComputeRepeatableRecordState(ProgressNode, int, out KspStatePatcher.RepeatableRecordState)"/>
    /// counts paid thresholds in, so a band count of k means intervals 1..k.</para>
    ///
    /// <para><see cref="Repay"/> marks a re-payment: the live node instance that paid it had
    /// already paid that interval. Stock does that when its award loop breaks without
    /// advancing (the last interval, or a threshold rounded to the maximum or above) while the
    /// record stays at or past it, on every frame until the node completes. A fresh node
    /// instance never re-pays (its <c>OnLoad</c> sets <c>rewardThreshold =
    /// FindNextRecord(record)</c>, strictly above the record), so the flag is exact on the
    /// timeline that paid it. A re-payment is a real stock payment of that timeline, not a new
    /// crossing.</para>
    /// </summary>
    internal struct RecordThresholdCredit
    {
        internal int Index;
        internal float Funds;
        internal float Rep;
        internal float Science;
        internal bool Repay;

        internal RecordThresholdCredit(int index, float funds, float rep, float science, bool repay = false)
        {
            Index = index;
            Funds = funds;
            Rep = rep;
            Science = science;
            Repay = repay;
        }
    }

    /// <summary>
    /// The text form of a world-record row's threshold list, shared by the ledger key
    /// (<see cref="GameAction.MilestoneRecordBandKey"/>) and the event detail key
    /// (<see cref="GameStateEventConverter.MilestoneRecordBandDetailKey"/>):
    /// <c>index:funds:rep:sci</c> per threshold, joined by commas, floats round-trip
    /// InvariantCulture; a re-payment (<see cref="RecordThresholdCredit.Repay"/>) writes its
    /// index with an <c>r</c> suffix (<c>5r:5000:0:0</c>). <see cref="IncompleteToken"/> marks a scope one of whose awards had no
    /// readable interval, so the list is not trusted and the row falls back to the
    /// cumulative count (<see cref="RecordThresholdBook"/>).
    /// </summary>
    internal static class RecordThresholdBand
    {
        internal const string IncompleteToken = "-";
        internal const char RepaySuffix = 'r';

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The parse result: no key, an incomplete marker, or a list.</summary>
        internal enum ParseState
        {
            Absent = 0,
            Incomplete,
            Listed
        }

        /// <summary>Pure: formats a list; null or empty formats as null (write nothing).</summary>
        internal static string Format(IList<RecordThresholdCredit> band)
        {
            if (band == null || band.Count == 0)
                return null;
            var sb = new StringBuilder();
            for (int i = 0; i < band.Count; i++)
            {
                if (i > 0) sb.Append(',');
                RecordThresholdCredit c = band[i];
                sb.Append(c.Index.ToString(IC));
                if (c.Repay) sb.Append(RepaySuffix);
                sb.Append(':')
                  .Append(c.Funds.ToString("R", IC)).Append(':')
                  .Append(c.Rep.ToString("R", IC)).Append(':')
                  .Append(c.Science.ToString("R", IC));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Pure: parses <paramref name="raw"/>. Null reads <see cref="ParseState.Absent"/>,
        /// the incomplete token (or an empty value) <see cref="ParseState.Incomplete"/>, a
        /// well-formed list <see cref="ParseState.Listed"/>. A malformed entry (index below 1,
        /// a non-number) reads Incomplete with <paramref name="malformed"/> set, so the row
        /// keeps today's cumulative count rather than a guessed band.
        /// </summary>
        internal static ParseState Parse(string raw, out List<RecordThresholdCredit> band, out bool malformed)
        {
            band = null;
            malformed = false;
            if (raw == null)
                return ParseState.Absent;
            string trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed == IncompleteToken)
                return ParseState.Incomplete;

            var list = new List<RecordThresholdCredit>();
            string[] entries = trimmed.Split(',');
            for (int i = 0; i < entries.Length; i++)
            {
                string[] parts = entries[i].Split(':');
                string indexText = parts[0];
                bool repay = indexText.Length > 1 && indexText[indexText.Length - 1] == RepaySuffix;
                if (repay)
                    indexText = indexText.Substring(0, indexText.Length - 1);
                if (parts.Length != 4
                    || !int.TryParse(indexText, NumberStyles.None, IC, out int index) || index < 1
                    || !float.TryParse(parts[1], NumberStyles.Float, IC, out float funds)
                    || !float.TryParse(parts[2], NumberStyles.Float, IC, out float rep)
                    || !float.TryParse(parts[3], NumberStyles.Float, IC, out float sci))
                {
                    malformed = true;
                    return ParseState.Incomplete;
                }
                list.Add(new RecordThresholdCredit(index, funds, rep, sci, repay));
            }
            band = list;
            return ParseState.Listed;
        }

        /// <summary>
        /// Pure: <paramref name="band"/> with one more award folded in. An interval already in
        /// the list gets the award added to its entry, keeping that entry's kind (a re-payment
        /// in the scope that first paid the interval rides that first payment), otherwise a
        /// new entry is appended with <paramref name="repay"/> as its kind (a scope whose first
        /// award of an interval is a re-payment of another scope's crossing). Returns a new
        /// list; the input is not changed.
        /// </summary>
        internal static List<RecordThresholdCredit> Fold(
            IList<RecordThresholdCredit> band, int index, float funds, float rep, float science,
            bool repay = false)
        {
            var result = band != null
                ? new List<RecordThresholdCredit>(band)
                : new List<RecordThresholdCredit>();
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].Index != index) continue;
                RecordThresholdCredit c = result[i];
                c.Funds += funds;
                c.Rep += rep;
                c.Science += science;
                result[i] = c;
                return result;
            }
            result.Add(new RecordThresholdCredit(index, funds, rep, science, repay));
            return result;
        }

        /// <summary>Pure: a copy of <paramref name="band"/> (null stays null).</summary>
        internal static List<RecordThresholdCredit> Copy(IList<RecordThresholdCredit> band)
        {
            return band == null ? null : new List<RecordThresholdCredit>(band);
        }

        /// <summary>Pure: "3,4" for logs; "-" for none.</summary>
        internal static string DescribeIndices(IList<int> indices)
        {
            if (indices == null || indices.Count == 0)
                return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < indices.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(indices[i].ToString(IC));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The walk's book of credited world-record thresholds (design 7.8, section 3.15 step 4):
    /// each threshold of a record node pays once per career, earliest crossing first, and a
    /// career with one timeline walks exactly what stock paid. Rows are fed in walk order;
    /// the book says which of each row's thresholds count and what the row earns. Pure: no
    /// KSP state. Shared by <see cref="MilestonesModule"/> and the live band mirror
    /// (<see cref="CommittedMilestoneReach"/>), so both count alike.
    ///
    /// <para>Where a row's thresholds come from:</para>
    /// <list type="bullet">
    /// <item>A row that names them (<see cref="GameAction.MilestoneRecordBand"/>, captured
    /// from stock's interval) uses them, with the reward stock paid for each and whether it
    /// was a first payment or a re-payment.</item>
    /// <item>A row written before the field (or whose list is incomplete) is DERIVED: it takes
    /// consecutive positions after the highest position among the seed (1..seed) and the same
    /// node's rows that are both earlier in walk order and captured before it
    /// (<see cref="CapturedBefore"/>). Those positions are a guess.</item>
    /// </list>
    ///
    /// <para>When a threshold counts. An overlap zeroes a threshold only on evidence that the
    /// band was lowered between the two payments, which on one timeline never happens (stock's
    /// band only advances, and a fresh node instance resumes above its saved record):</para>
    /// <list type="bullet">
    /// <item>The credited holder of the position was captured AFTER this row although it walks
    /// before it: a later timeline re-crossed it earlier (the rewind re-crossing).</item>
    /// <item>Both are named first payments of one interval: stock paid that interval as new
    /// twice, so its band was lowered in between (a rewind whose mirror did not raise it).</item>
    /// <item>A named first payment at or below the pre-ledger seed (stock already held it).</item>
    /// <item>A re-payment counts while the latest first payment of its interval captured before
    /// it counts: it rides the crossing of its own timeline.</item>
    /// </list>
    /// <para>A derived guess therefore never blocks a named row captured after it, and a
    /// re-payment captured after its crossing is never zeroed for it.</para>
    ///
    /// <para>What a row earns: every threshold counted, its full captured reward (the stored
    /// totals). None, nothing (<see cref="GameActionNotCountedReason.RecordThresholdsAlreadyCredited"/>).
    /// Some: a named row earns the reward stock paid for each counted entry. A derived row
    /// earns its totals less, for each zeroed position, the reward the named row holding that
    /// position was paid (the same threshold priced by stock on the other timeline), or an
    /// even share of its totals where a derived row holds it; clamped to 0..totals.</para>
    ///
    /// <para>The node's count (what the band patch rebuilds stock's band from) is the seed plus
    /// the counted first payments, a derived row counting each of its positions. On one
    /// timeline that is at least the band stock holds (an older build folded re-payments into
    /// its count), so the patch never lowers a band stock paid for.</para>
    /// </summary>
    internal sealed class RecordThresholdBook
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private sealed class Entry
        {
            internal int Position;
            internal long CaptureSequence;
            internal bool Named;
            internal bool Repay;
            internal bool Counted;
            internal float Funds;
            internal float Rep;
            internal float Science;
        }

        private sealed class NodeState
        {
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<ProcessedRow> Rows = new List<ProcessedRow>();
            internal int Seed;
            internal int CountedFirstPayments;
        }

        private struct ProcessedRow
        {
            internal long CaptureSequence;
            internal int Top;
        }

        /// <summary>Why a position of a row was not counted (logs and tests).</summary>
        internal enum BlockReason
        {
            None = 0,
            CapturedLaterWalkedEarlier,
            SecondFirstPayment,
            PreLedgerSeed,
            RepayOfZeroedCrossing
        }

        /// <summary>
        /// A named first payment the book zeroed as a second first payment of its interval
        /// (<see cref="BlockReason.SecondFirstPayment"/>): stock paid that interval as new
        /// after an earlier capture had, so its band was lowered in between (the rewind
        /// evidence). What the row's entry holds and which capture paid it first (design 3.15
        /// step 5's take-back reads it).
        /// </summary>
        internal struct SecondFirstPayment
        {
            internal int Position;
            internal float Funds;
            internal float Rep;
            internal float Science;
            internal long HolderCaptureSequence;
        }

        /// <summary>What the book decided for one row.</summary>
        internal struct RowCredit
        {
            /// <summary>Every position the row stands for, ascending, distinct.</summary>
            internal List<int> Positions;
            /// <summary>The positions this row counts.</summary>
            internal List<int> NewPositions;
            /// <summary>The positions that are re-payments (named rows only).</summary>
            internal List<int> RepayPositions;
            /// <summary>Why each zeroed position was zeroed, parallel to the zeroed positions
            /// in <see cref="Positions"/> order.</summary>
            internal List<BlockReason> BlockReasons;
            /// <summary>The entries zeroed as a second first payment, ascending.</summary>
            internal List<SecondFirstPayment> SecondFirstPayments;
            /// <summary>True when the row named its thresholds; false when they were derived.</summary>
            internal bool Named;
            /// <summary>The first derived position minus one (derived rows only).</summary>
            internal int Base;
            internal float Funds;
            internal float Rep;
            internal float Science;

            internal int Count => Positions != null ? Positions.Count : 0;
            internal int NewCount => NewPositions != null ? NewPositions.Count : 0;
            internal bool AllNew => NewCount == Count;
            internal bool NoneNew => Count > 0 && NewCount == 0;
        }

        private readonly Dictionary<string, NodeState> nodes =
            new Dictionary<string, NodeState>(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, int> seedCounts;

        /// <summary>Clears the book and installs the pre-ledger seed's paid counts
        /// (positions 1..count of each node start credited).</summary>
        internal void Reset(IReadOnlyDictionary<string, int> seeds)
        {
            nodes.Clear();
            seedCounts = seeds;
        }

        private int SeedFor(string id)
        {
            if (seedCounts != null && seedCounts.TryGetValue(id, out int s) && s > 0)
                return s;
            return 0;
        }

        private NodeState GetNode(string id)
        {
            if (nodes.TryGetValue(id, out NodeState state))
                return state;
            state = new NodeState { Seed = SeedFor(id) };
            nodes[id] = state;
            return state;
        }

        /// <summary>
        /// The node's count so far in this walk: the seed plus the counted first payments (a
        /// derived row's positions each count once). The count the record band patch rebuilds
        /// stock's band from.
        /// </summary>
        internal int GetCreditedCount(string milestoneId)
        {
            if (string.IsNullOrEmpty(milestoneId))
                return 0;
            if (nodes.TryGetValue(milestoneId, out NodeState state))
                return state.Seed + state.CountedFirstPayments;
            return SeedFor(milestoneId);
        }

        /// <summary>
        /// Pure over the book: whether <paramref name="earlier"/> (earlier in walk order) was
        /// captured before <paramref name="later"/>. A capture number of 0 or below is an
        /// older build's row, captured before every numbered row.
        /// </summary>
        internal static bool CapturedBefore(long earlier, long later)
        {
            bool earlierNumbered = earlier > 0;
            bool laterNumbered = later > 0;
            if (earlierNumbered && laterNumbered)
                return earlier < later;
            if (!earlierNumbered && laterNumbered)
                return true;
            if (earlierNumbered && !laterNumbered)
                return false;
            return true;
        }

        /// <summary>
        /// Pure: true when <paramref name="holder"/>, which walks before the row, was captured
        /// after it - the rewind evidence. One capture (equal numbers) is never a rewind.
        /// </summary>
        internal static bool CapturedAfterAlthoughWalkedBefore(long holder, long row)
        {
            if (holder > 0 && holder == row)
                return false;
            return !CapturedBefore(holder, row);
        }

        /// <summary>
        /// Decides one record row (fed in walk order) and books its counted thresholds.
        /// The row is not changed.
        /// </summary>
        internal RowCredit Credit(GameAction row)
        {
            var credit = new RowCredit
            {
                Positions = new List<int>(),
                NewPositions = new List<int>(),
                RepayPositions = new List<int>(),
                BlockReasons = new List<BlockReason>(),
                SecondFirstPayments = new List<SecondFirstPayment>()
            };
            if (row == null)
                return credit;
            NodeState node = GetNode(row.MilestoneId ?? "");
            long rowCseq = row.CaptureSequence;

            // The row's entries: named from its list, or derived consecutive positions.
            var candidates = new List<Entry>();
            List<RecordThresholdCredit> band = row.MilestoneRecordBand;
            if (band != null && band.Count > 0)
            {
                credit.Named = true;
                var byIndex = new Dictionary<int, Entry>();
                for (int i = 0; i < band.Count; i++)
                {
                    RecordThresholdCredit c = band[i];
                    if (c.Index < 1) continue;
                    if (byIndex.TryGetValue(c.Index, out Entry prior))
                    {
                        prior.Funds += c.Funds;
                        prior.Rep += c.Rep;
                        prior.Science += c.Science;
                        prior.Repay = prior.Repay && c.Repay;
                        continue;
                    }
                    var e = new Entry
                    {
                        Position = c.Index, CaptureSequence = rowCseq, Named = true, Repay = c.Repay,
                        Funds = c.Funds, Rep = c.Rep, Science = c.Science
                    };
                    byIndex[c.Index] = e;
                    candidates.Add(e);
                }
                candidates.Sort((a, b) => a.Position.CompareTo(b.Position));
            }
            else
            {
                int count = MilestonesModule.GetRepresentedRecordThresholds(row);
                int top = node.Seed;
                for (int i = 0; i < node.Rows.Count; i++)
                {
                    ProcessedRow p = node.Rows[i];
                    if (p.Top > top && CapturedBefore(p.CaptureSequence, rowCseq))
                        top = p.Top;
                }
                credit.Base = top;
                float shareFunds = count > 0 ? row.MilestoneFundsAwarded / count : 0f;
                float shareRep = count > 0 ? row.MilestoneRepAwarded / count : 0f;
                float shareSci = count > 0 ? row.MilestoneScienceAwarded / count : 0f;
                for (int i = 1; i <= count; i++)
                {
                    candidates.Add(new Entry
                    {
                        Position = top + i, CaptureSequence = rowCseq, Named = false,
                        Funds = shareFunds, Rep = shareRep, Science = shareSci
                    });
                }
            }

            // Decide each entry against what earlier rows booked, then book them all.
            float zeroedFunds = 0f, zeroedRep = 0f, zeroedSci = 0f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entry e = candidates[i];
                credit.Positions.Add(e.Position);
                if (e.Repay)
                    credit.RepayPositions.Add(e.Position);
                Entry blocker;
                BlockReason reason = Decide(node, e, out blocker);
                e.Counted = reason == BlockReason.None;
                if (e.Counted)
                {
                    credit.NewPositions.Add(e.Position);
                    continue;
                }
                credit.BlockReasons.Add(reason);
                if (reason == BlockReason.SecondFirstPayment)
                {
                    credit.SecondFirstPayments.Add(new SecondFirstPayment
                    {
                        Position = e.Position, Funds = e.Funds, Rep = e.Rep, Science = e.Science,
                        HolderCaptureSequence = blocker != null ? blocker.CaptureSequence : 0,
                    });
                }
                if (blocker != null && blocker.Named && !blocker.Repay)
                {
                    zeroedFunds += blocker.Funds;
                    zeroedRep += blocker.Rep;
                    zeroedSci += blocker.Science;
                }
                else
                {
                    zeroedFunds += e.Funds;
                    zeroedRep += e.Rep;
                    zeroedSci += e.Science;
                }
            }

            int rowTop = credit.Named ? 0 : credit.Base;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entry e = candidates[i];
                node.Entries.Add(e);
                if (e.Counted && !e.Repay)
                    node.CountedFirstPayments++;
                if (e.Position > rowTop)
                    rowTop = e.Position;
            }
            node.Rows.Add(new ProcessedRow { CaptureSequence = rowCseq, Top = rowTop });

            if (credit.AllNew)
            {
                credit.Funds = row.MilestoneFundsAwarded;
                credit.Rep = row.MilestoneRepAwarded;
                credit.Science = row.MilestoneScienceAwarded;
            }
            else if (credit.NewCount > 0)
            {
                if (credit.Named)
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        Entry e = candidates[i];
                        if (!e.Counted) continue;
                        credit.Funds += e.Funds;
                        credit.Rep += e.Rep;
                        credit.Science += e.Science;
                    }
                }
                else
                {
                    credit.Funds = Remainder(row.MilestoneFundsAwarded, zeroedFunds);
                    credit.Rep = Remainder(row.MilestoneRepAwarded, zeroedRep);
                    credit.Science = Remainder(row.MilestoneScienceAwarded, zeroedSci);
                }
            }
            return credit;
        }

        private static float Remainder(float total, float zeroed)
        {
            float left = total - zeroed;
            if (total >= 0f)
            {
                if (left < 0f) return 0f;
                if (left > total) return total;
            }
            return left;
        }

        private static BlockReason Decide(NodeState node, Entry e, out Entry blocker)
        {
            blocker = null;
            if (e.Repay)
            {
                // The crossing this re-payment rides: the latest first payment of the
                // interval captured before it (walked already: one timeline's UT and capture
                // order agree, so its crossing walks first).
                Entry crossing = null;
                for (int i = 0; i < node.Entries.Count; i++)
                {
                    Entry h = node.Entries[i];
                    if (h.Position != e.Position || !h.Named || h.Repay) continue;
                    if (!CapturedBefore(h.CaptureSequence, e.CaptureSequence)) continue;
                    if (crossing == null || CapturedBefore(crossing.CaptureSequence, h.CaptureSequence))
                        crossing = h;
                }
                if (crossing != null && !crossing.Counted)
                {
                    blocker = crossing;
                    return BlockReason.RepayOfZeroedCrossing;
                }
                return BlockReason.None;
            }

            if (e.Named && e.Position <= node.Seed)
                return BlockReason.PreLedgerSeed;

            for (int i = 0; i < node.Entries.Count; i++)
            {
                Entry h = node.Entries[i];
                if (!h.Counted || h.Position != e.Position) continue;
                if (CapturedAfterAlthoughWalkedBefore(h.CaptureSequence, e.CaptureSequence))
                {
                    blocker = h;
                    return BlockReason.CapturedLaterWalkedEarlier;
                }
                if (e.Named && h.Named && !h.Repay)
                {
                    blocker = h;
                    return BlockReason.SecondFirstPayment;
                }
            }
            return BlockReason.None;
        }

        /// <summary>One-line summary of a row's decision for logs.</summary>
        internal static string Describe(RowCredit credit)
        {
            var reasons = new StringBuilder();
            if (credit.BlockReasons != null)
            {
                for (int i = 0; i < credit.BlockReasons.Count; i++)
                {
                    if (i > 0) reasons.Append(',');
                    reasons.Append(credit.BlockReasons[i].ToString());
                }
            }
            return string.Format(IC, "thresholds=[{0}] new=[{1}] source={2}{3}",
                DescribePositions(credit.Positions, credit.RepayPositions),
                RecordThresholdBand.DescribeIndices(credit.NewPositions),
                credit.Named ? "named" : "derived(base=" + credit.Base.ToString(IC) + ")",
                reasons.Length > 0 ? " zeroed=[" + reasons + "]" : "");
        }

        private static string DescribePositions(IList<int> positions, IList<int> repays)
        {
            if (positions == null || positions.Count == 0)
                return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < positions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(positions[i].ToString(IC));
                if (repays != null && repays.Contains(positions[i]))
                    sb.Append(RecordThresholdBand.RepaySuffix);
            }
            return sb.ToString();
        }
    }
}
