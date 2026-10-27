using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// The row that credited an identity first in walk order (design 3.15 step 5): its UT and
    /// capture order, or the pre-ledger progress seed (achieved before the ledger began, so in
    /// every state a rewind can load). A later row of the same identity is a repeat; stock
    /// paid that repeat only when its own state had forgotten this witness.
    /// </summary>
    internal struct IdentityWitness
    {
        internal double UT;
        internal long CaptureSequence;
        internal bool PreLedger;
        internal string Label;

        internal static IdentityWitness Of(GameAction row, string label)
        {
            return new IdentityWitness
            {
                UT = row != null ? row.UT : 0.0,
                CaptureSequence = row != null ? row.CaptureSequence : 0,
                PreLedger = false,
                Label = label,
            };
        }

        internal static IdentityWitness At(double ut, long captureSequence, string label)
        {
            return new IdentityWitness { UT = ut, CaptureSequence = captureSequence, Label = label };
        }

        internal static IdentityWitness PreLedgerSeed(string label)
        {
            return new IdentityWitness { UT = double.NegativeInfinity, PreLedger = true, Label = label };
        }

        internal string Describe()
        {
            var ic = CultureInfo.InvariantCulture;
            if (PreLedger)
                return (Label ?? "credit") + " before the ledger began";
            return (Label ?? "credit") + " at ut=" + UT.ToString("R", ic)
                + " cseq=" + CaptureSequence.ToString(ic);
        }
    }

    /// <summary>
    /// One time travel that kept the ledger and replaced stock's state (design 3.15 step 5):
    /// a rewind, a Re-Fly start or discard, a stock revert. Every row captured before it
    /// (capture order at most <see cref="CaptureHighWater"/>) with a UT after
    /// <see cref="UT"/> is a row the loaded stock state does not hold.
    /// </summary>
    internal struct TimelineBreak
    {
        internal long CaptureHighWater;
        internal double UT;
        internal string Kind;
    }

    /// <summary>
    /// Where stock's own pool stood when it paid a repeat (design 3.15 step 5), so the
    /// take-back is what stock moved the pool by: its 0 clamp and its reputation curve
    /// applied at that value, not at the walk's.
    ///
    /// <para>The start is what the pool reference (<see cref="StockPoolReferenceState"/>)
    /// last wrote or read back (<see cref="StockPoolReferenceState.LastWritten"/>): stock's
    /// pool at its last patch, whatever a rewind made stock forget before it and whatever an
    /// undeferred write put back. Since then stock applied only the rows it paid live: the
    /// rows the reference had not counted at that patch, stock-applied (not Parsek-authored),
    /// and captured before the repeat (stock pays in capture order). Each adds what stock
    /// paid for it (its walk effect plus its own take-back part). A row the reference counted
    /// adds nothing: one it walked is in <see cref="StockPoolReferenceState.LastWritten"/>,
    /// and a committed future row it saw unwalked was never paid by stock (the write applies
    /// it). A row captured before the repeat that walks after it (a later UT) is not seen.</para>
    ///
    /// <para>With no reference yet (an older build's save before its first patch) the start
    /// is the walk's own value plus the take-backs so far; that patch adopts the live pool
    /// as its baseline and steps no take-back anyway.</para>
    /// </summary>
    internal sealed class StockShadowStart
    {
        private readonly StockPool pool;
        private bool known;
        private double lastWritten;
        private readonly HashSet<string> counted = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<long, double>> applied = new List<KeyValuePair<long, double>>();

        internal StockShadowStart(StockPool pool)
        {
            this.pool = pool;
        }

        /// <summary>True when the start comes from a pool reference.</summary>
        internal bool Known => known;

        /// <summary>The rows noted as paid by stock since the reference's last patch.</summary>
        internal int AppliedCount => applied.Count;

        /// <summary>Snapshots <paramref name="reference"/> (null or uninitialized: no start).</summary>
        internal void Reset(StockPoolReferenceState reference)
        {
            applied.Clear();
            counted.Clear();
            known = reference != null && reference.Initialized;
            lastWritten = known ? reference.LastWritten : 0.0;
            if (known)
                foreach (string id in reference.Counted.Keys)
                    counted.Add(id);
        }

        /// <summary>
        /// Notes a walked row: when the reference has not counted it and stock applied it,
        /// <paramref name="stockPaid"/> (its walk effect plus its take-back part) is in stock's
        /// pool for every repeat captured after it.
        /// </summary>
        internal void Note(GameAction row, double stockPaid)
        {
            if (!known || row == null || row.ParsekAuthored || string.IsNullOrEmpty(row.ActionId)
                || stockPaid == 0.0 || double.IsNaN(stockPaid) || double.IsInfinity(stockPaid)
                || !StockPoolReference.IsPoolRow(pool, row) || counted.Contains(row.ActionId))
                return;
            applied.Add(new KeyValuePair<long, double>(row.CaptureSequence, stockPaid));
        }

        /// <summary>
        /// Stock's pool just before it paid the repeat captured at
        /// <paramref name="repeatCaptureSequence"/>; <paramref name="walkFallback"/> when there
        /// is no reference.
        /// </summary>
        internal double Before(long repeatCaptureSequence, double walkFallback)
        {
            if (!known)
                return walkFallback;
            double value = lastWritten;
            for (int i = 0; i < applied.Count; i++)
            {
                if (RecordThresholdBook.CapturedBefore(applied[i].Key, repeatCaptureSequence)
                    && applied[i].Key != repeatCaptureSequence)
                    value += applied[i].Value;
            }
            return value;
        }
    }

    /// <summary>
    /// Stock is stopped first and corrected second (design 3.15 step 5, owner ruling
    /// 2026-10-26, prevention plus take-back). This class holds the take-back's rules and
    /// its two save-level facts; the walk modules compute the amounts and
    /// <see cref="StockPoolReference"/> applies them.
    ///
    /// <para>Game rule: when stock pays live for an identity the timeline already credited
    /// (a world-first, a contract or parameter reward, a record threshold, science past a
    /// subject's cap) because a rewind made stock forget the earlier credit, the next update
    /// takes that payment back. Never for a floor or a strategy share, never as a
    /// <see cref="GameActionType.StockPoolAdjustment"/> row.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Era.</b> Only rows first captured by a build that ships the take-back:
    /// <see cref="EligibleFromCaptureSequence"/> is the first capture number this build
    /// issued for the save (set lazily at that issue, saved with the ledger, additive key).
    /// A row with a lower or no number is never taken back.</item>
    /// <item><b>Forgotten witness.</b> A repeat is taken back only for the part its
    /// crediting row(s) stand for that a <see cref="TimelineBreak"/> separates from it: the
    /// witness captured before the break, the repeat after, and the witness's UT after the
    /// break's loaded UT. On a career never rewound there is no break, so nothing is ever
    /// taken back and the pools end exactly as before (where stock pays a repeat by its own
    /// rule - a mod parameter that pays again, a deadline the walk and stock disagree on -
    /// the reference keeps it as before). A science row is stamped when its recording commits,
    /// so for science "captured after the break" means committed after it.</item>
    /// <item><b>Records.</b> A world-record threshold needs no break: step 4's second first
    /// payment of an interval is the rewind evidence itself
    /// (<see cref="MilestonesModule.ResolveRecordTakeBack"/>).</item>
    /// </list>
    /// </summary>
    internal static class StockShadowTakeBack
    {
        internal const string Tag = "TakeBack";
        internal const string EligibleFromKey = "takeBackFromCseq";
        internal const string TimelineBreakKey = "timelineBreak";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private static long eligibleFrom;
        private static readonly List<TimelineBreak> breaks = new List<TimelineBreak>();

        /// <summary>The first capture number this build issued for the save; 0 while none.</summary>
        internal static long EligibleFromCaptureSequence => eligibleFrom;

        internal static IReadOnlyList<TimelineBreak> Breaks => breaks;

        internal static void ResetForTesting()
        {
            eligibleFrom = 0;
            breaks.Clear();
        }

        /// <summary>xUnit seam: sets the era start (0 clears it).</summary>
        internal static void SetEligibleFromForTesting(long value)
        {
            eligibleFrom = value < 0 ? 0 : value;
        }

        /// <summary>xUnit seam: appends a break.</summary>
        internal static void AddBreakForTesting(long captureHighWater, double ut, string kind = "test")
        {
            breaks.Add(new TimelineBreak { CaptureHighWater = captureHighWater, UT = ut, Kind = kind });
        }

        // ------------------------------------------------------------------
        // Era
        // ------------------------------------------------------------------

        /// <summary>
        /// Called by <see cref="CaptureSequence"/> for every number it issues: the first one
        /// issued for a save with no era starts it. Every row this build captures carries a
        /// number at or above it; everything an older build captured carries less or none.
        /// </summary>
        internal static void NoteCaptureIssued(long value)
        {
            if (eligibleFrom > 0 || value <= 0)
                return;
            eligibleFrom = value;
            ParsekLog.Info(Tag,
                "Take-back era starts at capture " + value.ToString(IC)
                + ": rows captured from here on can have a duplicate stock payment taken back; "
                + "older rows never are (design 3.15 step 5)");
        }

        /// <summary>Pure: a row captured at <paramref name="captureSequence"/> is in the era
        /// that starts at <paramref name="eraStart"/> (0: no era yet).</summary>
        internal static bool IsInEra(long captureSequence, long eraStart)
        {
            return eraStart > 0 && captureSequence >= eraStart;
        }

        /// <summary>A stock-applied row first captured by this build.</summary>
        internal static bool IsEligibleRow(GameAction row)
        {
            return row != null && !row.ParsekAuthored && IsInEra(row.CaptureSequence, eligibleFrom);
        }

        // ------------------------------------------------------------------
        // Identity reasons
        // ------------------------------------------------------------------

        /// <summary>
        /// Pure: a not-counted reason the registry classes as an identity rule (one outcome
        /// ever, first wins, once per threshold, first wins while the contract is open). The
        /// purchase state rule (<see cref="GameActionNotCountedReason.FacilityAlreadyIntact"/>,
        /// <see cref="GameActionNotCountedReason.PurchaseAlreadyOwned"/>, step 6) is not taken
        /// back here: stock never sells an item it owns, and a purchase the walk stops charging
        /// is a committed row stock never charged live.
        /// </summary>
        internal static bool IsIdentityReason(GameActionNotCountedReason reason)
        {
            switch (reason)
            {
                case GameActionNotCountedReason.ContractAlreadyCompleted:
                case GameActionNotCountedReason.ContractDeadlinePassed:
                case GameActionNotCountedReason.ContractAlreadyResolved:
                case GameActionNotCountedReason.ContractOutcomeAfterEnd:
                case GameActionNotCountedReason.MilestoneAlreadyAchieved:
                case GameActionNotCountedReason.ContractParameterAfterCompletion:
                case GameActionNotCountedReason.RecordThresholdsAlreadyCredited:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // Timeline breaks
        // ------------------------------------------------------------------

        /// <summary>Pure: a load that keeps the in-memory ledger and replaces stock's state
        /// with an earlier one. A cold load restores the save's own ledger with its breaks.</summary>
        internal static bool IsTimeTravelLoad(LoadKind kind)
        {
            switch (kind)
            {
                case LoadKind.PlainRewind:
                case LoadKind.ReFlyStart:
                case LoadKind.DiscardReFly:
                case LoadKind.StockRevert:
                case LoadKind.QuickloadFlight:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Records the break of a time-travel load (<see cref="IsTimeTravelLoad"/>) at the
        /// loaded game's UT, with the capture high water at the load. Returns true when a
        /// break was added (an identical last break is not added twice).
        /// </summary>
        internal static bool NoteLoad(LoadKind kind, double loadedUT, long captureHighWater)
        {
            if (!IsTimeTravelLoad(kind))
                return false;
            if (double.IsNaN(loadedUT) || double.IsInfinity(loadedUT))
            {
                ParsekLog.Warn(Tag,
                    $"Timeline break not recorded for load kind={kind}: the loaded UT is unknown " +
                    "(a duplicate stock payment after this load is kept as before)");
                return false;
            }
            if (breaks.Count > 0)
            {
                TimelineBreak last = breaks[breaks.Count - 1];
                if (last.CaptureHighWater == captureHighWater && last.UT == loadedUT)
                {
                    ParsekLog.Verbose(Tag,
                        $"Timeline break kind={kind} ut={loadedUT.ToString("R", IC)} " +
                        $"highWater={captureHighWater.ToString(IC)} equals the last one - not added");
                    return false;
                }
            }
            breaks.Add(new TimelineBreak { CaptureHighWater = captureHighWater, UT = loadedUT, Kind = kind.ToString() });
            ParsekLog.Info(Tag,
                $"Timeline break recorded: kind={kind} loadedUT={loadedUT.ToString("R", IC)} " +
                $"captureHighWater={captureHighWater.ToString(IC)} breaks={breaks.Count.ToString(IC)} " +
                "(rows captured before it with a later UT are not in the loaded stock state)");
            return true;
        }

        /// <summary>Pure: the witness was captured before a break that loaded a state from
        /// before its UT, and the repeat was captured after that break.</summary>
        internal static bool IsWitnessForgotten(
            IdentityWitness witness, long repeatCaptureSequence, IList<TimelineBreak> timelineBreaks)
        {
            if (witness.PreLedger || timelineBreaks == null || repeatCaptureSequence <= 0)
                return false;
            long witnessSeq = witness.CaptureSequence > 0 ? witness.CaptureSequence : 0;
            for (int i = 0; i < timelineBreaks.Count; i++)
            {
                TimelineBreak b = timelineBreaks[i];
                if (b.UT < witness.UT
                    && b.CaptureHighWater >= witnessSeq
                    && b.CaptureHighWater < repeatCaptureSequence)
                    return true;
            }
            return false;
        }

        /// <summary><see cref="IsWitnessForgotten(IdentityWitness, long, IList{TimelineBreak})"/>
        /// over this save's breaks.</summary>
        internal static bool IsWitnessForgotten(IdentityWitness witness, long repeatCaptureSequence)
        {
            return IsWitnessForgotten(witness, repeatCaptureSequence, breaks);
        }

        /// <summary>
        /// The walk's decision for a whole-row repeat (one witness): true, with the witness
        /// text stamped, when the row is eligible, its reason is an identity reason and its
        /// witness is forgotten. The caller sets the amounts.
        /// </summary>
        internal static bool ShouldTakeBackWholeRow(GameAction row, IdentityWitness witness)
        {
            if (row == null || !IsIdentityReason(row.NotCountedReason) || !IsEligibleRow(row))
                return false;
            if (!IsWitnessForgotten(witness, row.CaptureSequence))
                return false;
            row.TakeBackWitness = witness.Describe();
            return true;
        }

        // ------------------------------------------------------------------
        // The stock shadow
        // ------------------------------------------------------------------

        /// <summary>
        /// Pure: what stock moved a funds or science pool by for a row the walk moved by
        /// <paramref name="walkEffect"/> and whose take-back part is
        /// <paramref name="takeBack"/> (captured units). The shadow pool stands at
        /// <paramref name="shadowBefore"/>; a part that would take it below 0 stops there
        /// (stock's 0 clamp, off under <c>AllowNegativeCurrency</c>).
        /// </summary>
        internal static double ShadowCurrencyEffect(
            double shadowBefore, double walkEffect, double takeBack, bool allowNegativeCurrency)
        {
            double after = shadowBefore + walkEffect + takeBack;
            if (!allowNegativeCurrency && takeBack < 0.0 && after < 0.0)
            {
                double floor = Math.Min(shadowBefore + walkEffect, 0.0);
                if (after < floor) after = floor;
            }
            return after - shadowBefore;
        }

        /// <summary>
        /// Pure: the part of <paramref name="takenBack"/> a write could not collect because
        /// the walk's running value after it is below 0 (a duplicate stock payment the player
        /// spent before the update; possible only with auto-merge off).
        /// </summary>
        internal static double ResolveShortfall(double takenBack, double runningAfter)
        {
            if (!(takenBack > 0.0) || !(runningAfter < 0.0))
                return 0.0;
            return Math.Min(takenBack, -runningAfter);
        }

        // ------------------------------------------------------------------
        // Persistence (in the LEDGER node, additive keys)
        // ------------------------------------------------------------------

        internal static void SaveInto(ConfigNode root)
        {
            if (root == null)
                return;
            if (eligibleFrom > 0)
                root.AddValue(EligibleFromKey, eligibleFrom.ToString(IC));
            for (int i = 0; i < breaks.Count; i++)
            {
                TimelineBreak b = breaks[i];
                root.AddValue(TimelineBreakKey,
                    b.CaptureHighWater.ToString(IC) + " " + b.UT.ToString("R", IC) + " " + (b.Kind ?? "unknown"));
            }
        }

        /// <summary>Replaces both facts with the node's (absent: no era, no break).</summary>
        internal static void LoadFrom(ConfigNode root, string sourceLabel)
        {
            eligibleFrom = 0;
            breaks.Clear();
            if (root == null)
                return;
            long era;
            string eraText = root.GetValue(EligibleFromKey);
            if (eraText != null && long.TryParse(eraText, NumberStyles.Integer, IC, out era) && era > 0)
                eligibleFrom = era;
            int bad = 0;
            string[] values = root.GetValues(TimelineBreakKey);
            for (int i = 0; i < values.Length; i++)
            {
                string[] parts = (values[i] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                long h;
                double ut;
                if (parts.Length < 2
                    || !long.TryParse(parts[0], NumberStyles.Integer, IC, out h)
                    || !double.TryParse(parts[1], NumberStyles.Float, IC, out ut)
                    || double.IsNaN(ut) || double.IsInfinity(ut))
                {
                    bad++;
                    continue;
                }
                breaks.Add(new TimelineBreak
                {
                    CaptureHighWater = h,
                    UT = ut,
                    Kind = parts.Length > 2 ? parts[2] : "unknown",
                });
            }
            string line = $"Loaded take-back facts from '{sourceLabel ?? "unknown"}': " +
                $"era={(eligibleFrom > 0 ? eligibleFrom.ToString(IC) : "none")} " +
                $"breaks={breaks.Count.ToString(IC)} badBreaks={bad.ToString(IC)}";
            if (bad > 0)
                ParsekLog.Warn(Tag, line);
            else
                ParsekLog.Verbose(Tag, line);
        }

        // ------------------------------------------------------------------
        // Log text
        // ------------------------------------------------------------------

        /// <summary>Pure: the identity a row stands for, e.g. <c>MilestoneAchievement 'Mun/Landing'</c>.</summary>
        internal static string DescribeIdentity(GameAction row)
        {
            if (row == null)
                return "(unknown row)";
            switch (row.Type)
            {
                case GameActionType.MilestoneAchievement:
                    return "milestone '" + (row.MilestoneId ?? "") + "'";
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return row.Type + " contract '" + (row.ContractId ?? "") + "'";
                case GameActionType.ContractParameterComplete:
                    return "parameter '" + (row.ContractParameterId ?? "") + "' of contract '"
                        + (row.ContractId ?? "") + "'";
                case GameActionType.ScienceEarning:
                    return "science subject '" + (row.SubjectId ?? "") + "'";
                default:
                    return row.Type.ToString();
            }
        }

        /// <summary>Pure: the one Warn a take-back logs, naming the identity.</summary>
        internal static string FormatTakeBack(StockPool pool, double amount, GameAction row, bool byAuthoritativeWrite)
        {
            var sb = new StringBuilder();
            sb.Append("Took back ").Append(amount.ToString("R", IC)).Append(' ').Append(pool)
              .Append(" stock paid again for ").Append(DescribeIdentity(row))
              .Append(" (row ").Append(row?.ActionId ?? "(none)")
              .Append(" ut=").Append(row != null ? row.UT.ToString("R", IC) : "?")
              .Append(" cseq=").Append(row != null ? row.CaptureSequence.ToString(IC) : "?")
              .Append(" reason=").Append(row != null && row.NotCountedReason != GameActionNotCountedReason.None
                  ? row.NotCountedReason.ToString() : "partial-credit")
              .Append("; already credited by ").Append(row?.TakeBackWitness ?? "an earlier row")
              .Append("; ").Append(row != null ? LedgerEventIdentityRegistry.Describe(row) : "")
              .Append(byAuthoritativeWrite ? "; applied by the authoritative write" : "")
              .Append(")");
            return sb.ToString();
        }
    }
}
