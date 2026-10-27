using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// What the walk decided for one continuous-producer slice row
    /// (<see cref="ProducerSliceBook.Decide"/>): how much of its time slice an earlier credited
    /// slice of the same producer and subject already holds, how much of that stock forgot
    /// after a rewind, and the part left to credit.
    /// </summary>
    internal struct ProducerSliceDecision
    {
        /// <summary>The row's slice length in seconds (0 for a row that is not a slice).</summary>
        internal double Length;

        /// <summary>Seconds of the slice an earlier credited slice already holds.</summary>
        internal double Covered;

        /// <summary>Seconds of <see cref="Covered"/> held by slices stock forgot after a
        /// timeline break (the producer paid them again).</summary>
        internal double Forgotten;

        /// <summary>The first holder of a covered part, for the log and the witness.</summary>
        internal GameAction Holder;

        /// <summary>The parts of the slice no earlier credited slice holds.</summary>
        internal List<KeyValuePair<double, double>> Uncovered;

        /// <summary>The share of the row's science the walk credits, in [0, 1].</summary>
        internal double CreditedFraction
        {
            get
            {
                if (!(Length > 0.0))
                    return 1.0;
                double f = 1.0 - Covered / Length;
                return f < 0.0 ? 0.0 : (f > 1.0 ? 1.0 : f);
            }
        }

        /// <summary>The share of the row's science stock paid for forgotten slices, in [0, 1].</summary>
        internal double ForgottenFraction
        {
            get
            {
                if (!(Length > 0.0))
                    return 0.0;
                double f = Forgotten / Length;
                return f < 0.0 ? 0.0 : (f > 1.0 ? 1.0 : f);
            }
        }
    }

    /// <summary>
    /// The continuous-producer rule (design 3.15 step 7, owner ruling 2026-10-26): a producer
    /// that pays science for a span of time (a Breaking Ground deployed experiment, Kerbalism's
    /// background science) is keyed on producer + subject + time slice, and a slice the
    /// committed timeline already holds earns nothing. Walk-scoped: fed in walk order, it
    /// records the parts of each slice the walk credited.
    ///
    /// <para>An earlier credited slice counts against a later row only when a rewind separated
    /// them: its row was captured after this one (both capture numbers known), or stock forgot
    /// it across a timeline break (<see cref="StockShadowTakeBack.IsWitnessForgotten(IdentityWitness, long, IList{TimelineBreak})"/>).
    /// An overlap on one timeline is the producer's own rule and is credited as captured, so a
    /// career never rewound walks exactly as before.</para>
    ///
    /// <para>Mod-only: no capture channel writes these rows yet. The rule is built against the
    /// row shape (<see cref="GameAction.ProducerId"/>, <see cref="GameAction.SliceStartUT"/>,
    /// <see cref="GameAction.SliceEndUT"/> on a <see cref="GameActionType.ScienceEarning"/>).</para>
    /// </summary>
    internal sealed class ProducerSliceBook
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private struct Held
        {
            internal double Start;
            internal double End;
            internal GameAction Holder;
        }

        private readonly Dictionary<string, List<Held>> held = new Dictionary<string, List<Held>>(StringComparer.Ordinal);

        internal void Reset()
        {
            held.Clear();
        }

        /// <summary>Pure: the row is a continuous producer's slice: a science earning naming
        /// its producer and a slice with a positive, finite length.</summary>
        internal static bool IsProducerSlice(GameAction row)
        {
            return row != null && row.Type == GameActionType.ScienceEarning
                && !string.IsNullOrEmpty(row.ProducerId)
                && !double.IsNaN(row.SliceStartUT) && !double.IsInfinity(row.SliceStartUT)
                && !double.IsNaN(row.SliceEndUT) && !double.IsInfinity(row.SliceEndUT)
                && row.SliceEndUT > row.SliceStartUT;
        }

        /// <summary>Pure: the book key, producer + subject.</summary>
        internal static string KeyOf(GameAction row)
        {
            return (row.ProducerId ?? "") + "|" + (row.SubjectId ?? "");
        }

        /// <summary>
        /// The decision for <paramref name="row"/> against the slices credited so far, without
        /// recording it. A row that is not a slice gets a zero-length decision (credit in full).
        /// </summary>
        internal ProducerSliceDecision Decide(GameAction row, IList<TimelineBreak> breaks)
        {
            var d = new ProducerSliceDecision();
            if (!IsProducerSlice(row))
                return d;
            double start = row.SliceStartUT;
            double end = row.SliceEndUT;
            d.Length = end - start;
            d.Uncovered = new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(start, end) };

            List<Held> slices;
            if (!held.TryGetValue(KeyOf(row), out slices) || slices.Count == 0)
                return d;

            var counting = new List<KeyValuePair<double, double>>();
            var forgotten = new List<KeyValuePair<double, double>>();
            for (int i = 0; i < slices.Count; i++)
            {
                Held h = slices[i];
                double a = Math.Max(h.Start, start);
                double b = Math.Min(h.End, end);
                if (!(b > a))
                    continue;
                bool capturedAfter = PurchaseStateRule.IsCapturedAfter(h.Holder.CaptureSequence, row.CaptureSequence);
                bool isForgotten = !capturedAfter && StockShadowTakeBack.IsWitnessForgotten(
                    IdentityWitness.Of(h.Holder, Label(row)), row.CaptureSequence, breaks);
                if (!capturedAfter && !isForgotten)
                    continue;
                counting.Add(new KeyValuePair<double, double>(a, b));
                if (isForgotten)
                    forgotten.Add(new KeyValuePair<double, double>(a, b));
                if (d.Holder == null)
                    d.Holder = h.Holder;
            }
            if (counting.Count == 0)
                return d;
            List<KeyValuePair<double, double>> coveredUnion = Union(counting);
            d.Covered = Length(coveredUnion);
            d.Forgotten = Length(Union(forgotten));
            d.Uncovered = Subtract(start, end, coveredUnion);
            return d;
        }

        /// <summary>Records the parts of the row's slice the walk credited.</summary>
        internal void Hold(GameAction row, ProducerSliceDecision decision)
        {
            if (!IsProducerSlice(row) || decision.Uncovered == null)
                return;
            string key = KeyOf(row);
            List<Held> slices;
            if (!held.TryGetValue(key, out slices))
            {
                slices = new List<Held>();
                held[key] = slices;
            }
            for (int i = 0; i < decision.Uncovered.Count; i++)
            {
                KeyValuePair<double, double> part = decision.Uncovered[i];
                if (part.Value > part.Key)
                    slices.Add(new Held { Start = part.Key, End = part.Value, Holder = row });
            }
        }

        /// <summary>The identity label of a slice row, for logs and the take-back witness.</summary>
        internal static string Label(GameAction row)
        {
            if (row == null)
                return "producer slice";
            return "producer '" + (row.ProducerId ?? "") + "' subject '" + (row.SubjectId ?? "") + "'";
        }

        /// <summary>Pure: one line naming a slice, e.g. <c>[100, 200)</c>.</summary>
        internal static string FormatSlice(GameAction row)
        {
            if (row == null)
                return "";
            return "[" + row.SliceStartUT.ToString("R", IC) + ", " + row.SliceEndUT.ToString("R", IC) + ")";
        }

        /// <summary>Pure: the union of half-open intervals, sorted and merged.</summary>
        internal static List<KeyValuePair<double, double>> Union(List<KeyValuePair<double, double>> parts)
        {
            var result = new List<KeyValuePair<double, double>>();
            if (parts == null || parts.Count == 0)
                return result;
            var sorted = new List<KeyValuePair<double, double>>(parts);
            sorted.Sort((x, y) => x.Key.CompareTo(y.Key));
            double curStart = sorted[0].Key;
            double curEnd = sorted[0].Value;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Key <= curEnd)
                {
                    if (sorted[i].Value > curEnd)
                        curEnd = sorted[i].Value;
                    continue;
                }
                result.Add(new KeyValuePair<double, double>(curStart, curEnd));
                curStart = sorted[i].Key;
                curEnd = sorted[i].Value;
            }
            result.Add(new KeyValuePair<double, double>(curStart, curEnd));
            return result;
        }

        /// <summary>Pure: total length of disjoint intervals.</summary>
        internal static double Length(List<KeyValuePair<double, double>> disjoint)
        {
            double total = 0.0;
            if (disjoint == null)
                return total;
            for (int i = 0; i < disjoint.Count; i++)
                if (disjoint[i].Value > disjoint[i].Key)
                    total += disjoint[i].Value - disjoint[i].Key;
            return total;
        }

        /// <summary>Pure: [start, end) minus sorted disjoint intervals.</summary>
        internal static List<KeyValuePair<double, double>> Subtract(
            double start, double end, List<KeyValuePair<double, double>> sortedDisjoint)
        {
            var result = new List<KeyValuePair<double, double>>();
            double cursor = start;
            if (sortedDisjoint != null)
            {
                for (int i = 0; i < sortedDisjoint.Count; i++)
                {
                    double a = Math.Max(sortedDisjoint[i].Key, start);
                    double b = Math.Min(sortedDisjoint[i].Value, end);
                    if (!(b > a))
                        continue;
                    if (a > cursor)
                        result.Add(new KeyValuePair<double, double>(cursor, a));
                    if (b > cursor)
                        cursor = b;
                }
            }
            if (end > cursor)
                result.Add(new KeyValuePair<double, double>(cursor, end));
            return result;
        }
    }
}
