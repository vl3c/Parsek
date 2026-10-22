using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>One walk row as <see cref="ReservedFloorPlan"/> reads it for one pool.</summary>
    internal struct ReservedFloorRow
    {
        internal GameAction Action;
        /// <summary>The row's delta to the pool in the debt-free walk (0 when it moves none).</summary>
        internal double Delta;
        /// <summary>A purchase or strategy cost of this pool (with a real charge).</summary>
        internal bool IsCost;
        /// <summary>A loss nobody reserves for (crew-death and "other" reputation penalties):
        /// left out of what costs ahead need.</summary>
        internal bool FloorLimited;
        /// <summary>A row a module's pre-pass injected (an open contract's synthetic
        /// deadline fail): not a ledger row, left out of the need.</summary>
        internal bool Synthetic;
    }

    /// <summary>
    /// A module that holds a pool's floor and debt in the walk (committed-value rule plan
    /// section 2.3, design 3.13 rule 4): funds, science, reputation. The engine builds one
    /// <see cref="ReservedFloorPlan"/> per pool from a debt-free walk and installs it after
    /// the module's reset; it knows no module type.
    /// </summary>
    internal interface IReservedFloorModule
    {
        /// <summary>The pool this module walks.</summary>
        StockPool FloorPool { get; }

        /// <summary>Pure: the row's delta to the pool after a walk populated its derived
        /// fields, whether it is a cost row, and whether it is a floor-limited loss.</summary>
        void ClassifyFloorRow(GameAction action, out double delta, out bool isCost, out bool floorLimited);

        /// <summary>Installs the plan for the next walk (null: no floor, phase 2's stock clamp).</summary>
        void SetReservedFloorPlan(ReservedFloorPlan plan);

        /// <summary>The debt outstanding at the end of the last walk.</summary>
        double GetOutstandingDebt();
    }

    /// <summary>
    /// The reserved floor of one pool (committed-value rule plan section 2.3). Pure: built
    /// from the rows of a debt-free walk of the full timeline in walk order, it answers, for
    /// a row of that walk and a capture-order bound, what the committed costs ahead of it
    /// still need.
    ///
    /// <para>"A committed cost lies ahead of row l" (coordinator's definition, 2026-10-22):
    /// a cost row with UT after l's that was already on the timeline when stock applied l,
    /// judged by real-time capture order (<see cref="GameAction.CaptureSequence"/> below
    /// the bound, l's own for a loss). A row without a capture sequence is ahead of nothing
    /// and nothing is ahead of it. In a career that never rewinds every later purchase was
    /// captured after the loss, so no row has a floor and the walk is stock's.</para>
    ///
    /// <para><c>need_i = max(0, -min over k in (i, L] of sum_{j=i+1..k} d'(j))</c>, with L the
    /// last cost row ahead of i under the bound and d' the deltas without floor-limited
    /// losses and injected rows; the floor is need_i when a cost lies ahead, else none.</para>
    /// </summary>
    internal sealed class ReservedFloorPlan
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal readonly StockPool Pool;
        private readonly int count;
        private readonly double[] ut;
        // prefix[m] = sum of d' over rows 0..m-1 (m = 0..count).
        private readonly double[] prefix;
        private readonly Dictionary<GameAction, int> positionOf;
        // Cost rows with a capture sequence, sorted by sequence, and the largest position
        // among the first n of them (maxPositionUpTo[n-1]).
        private readonly long[] costSequences;
        private readonly int[] maxPositionUpTo;
        // Sparse table of range minima over prefix.
        private readonly double[][] minTable;

        internal int RowCount => count;
        internal int CostRowCount { get; private set; }

        private ReservedFloorPlan(StockPool pool, IList<ReservedFloorRow> rows)
        {
            Pool = pool;
            count = rows?.Count ?? 0;
            ut = new double[count];
            prefix = new double[count + 1];
            positionOf = new Dictionary<GameAction, int>(count, ReferenceComparer.Instance);
            var costs = new List<KeyValuePair<long, int>>();
            for (int i = 0; i < count; i++)
            {
                ReservedFloorRow row = rows[i];
                ut[i] = row.Action != null ? row.Action.UT : 0.0;
                if (row.Action != null && !positionOf.ContainsKey(row.Action))
                    positionOf[row.Action] = i;
                double dPrime = row.Synthetic || row.FloorLimited || double.IsNaN(row.Delta)
                    || double.IsInfinity(row.Delta) ? 0.0 : row.Delta;
                prefix[i + 1] = prefix[i] + dPrime;
                if (row.IsCost && !row.Synthetic && row.Action != null && row.Action.CaptureSequence > 0)
                    costs.Add(new KeyValuePair<long, int>(row.Action.CaptureSequence, i));
            }
            CostRowCount = costs.Count;
            costs.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
            costSequences = new long[costs.Count];
            maxPositionUpTo = new int[costs.Count];
            int maxPos = -1;
            for (int i = 0; i < costs.Count; i++)
            {
                costSequences[i] = costs[i].Key;
                if (costs[i].Value > maxPos) maxPos = costs[i].Value;
                maxPositionUpTo[i] = maxPos;
            }
            minTable = BuildMinTable(prefix);
        }

        /// <summary>Builds the plan for <paramref name="pool"/> from the debt-free walk's rows, in walk order.</summary>
        internal static ReservedFloorPlan Build(StockPool pool, IList<ReservedFloorRow> rows)
        {
            return new ReservedFloorPlan(pool, rows);
        }

        /// <summary>
        /// The floor after <paramref name="action"/> for a capture-order bound
        /// <paramref name="sequenceBound"/>: true with <paramref name="floor"/> = need when a
        /// cost row captured before the bound lies after the row's UT, false (no floor)
        /// otherwise, or when the row is not in the plan or the bound is 0.
        /// </summary>
        internal bool TryGetFloor(GameAction action, long sequenceBound, out double floor)
        {
            floor = 0.0;
            int i;
            if (action == null || sequenceBound <= 0 || !positionOf.TryGetValue(action, out i))
                return false;
            return TryGetFloorAt(i, sequenceBound, out floor);
        }

        /// <summary>Position form of <see cref="TryGetFloor"/> (tests).</summary>
        internal bool TryGetFloorAt(int position, long sequenceBound, out double floor)
        {
            floor = 0.0;
            if (position < 0 || position >= count || sequenceBound <= 0 || costSequences.Length == 0)
                return false;
            // How many cost rows were captured before the bound.
            int n = LowerBound(costSequences, sequenceBound);
            if (n == 0)
                return false;
            int last = maxPositionUpTo[n - 1];
            if (last <= position || !(ut[last] > ut[position]))
                return false;
            // min over k in (position, last] of prefix[k+1] - prefix[position+1].
            double minPrefix = RangeMin(position + 2, last + 1);
            double need = prefix[position + 1] - minPrefix;
            floor = need > 0.0 ? need : 0.0;
            return true;
        }

        /// <summary>
        /// Pure, cheap pre-check: true when some cost-type row was captured before a row with
        /// an earlier UT (the only shape that can give a row a floor). False for every career
        /// that never rewound, so the walk then skips the plan and stays phase 2's.
        /// </summary>
        internal static bool HasCostCapturedAheadOfALaterLoss(IList<GameAction> actions)
        {
            if (actions == null || actions.Count < 2)
                return false;
            var rows = new List<GameAction>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
                if (actions[i] != null && actions[i].CaptureSequence > 0)
                    rows.Add(actions[i]);
            rows.Sort((a, b) => b.UT.CompareTo(a.UT));
            long minCostSeqAfter = long.MaxValue;
            int g = 0;
            while (g < rows.Count)
            {
                int end = g;
                while (end < rows.Count && rows[end].UT == rows[g].UT) end++;
                for (int k = g; k < end; k++)
                {
                    if (!IsAnyPoolCostType(rows[k].Type) && minCostSeqAfter < rows[k].CaptureSequence)
                        return true;
                }
                for (int k = g; k < end; k++)
                {
                    if (IsAnyPoolCostType(rows[k].Type) && rows[k].CaptureSequence < minCostSeqAfter)
                        minCostSeqAfter = rows[k].CaptureSequence;
                }
                g = end;
            }
            return false;
        }

        /// <summary>A cost row of any pool (funds, science or reputation).</summary>
        internal static bool IsAnyPoolCostType(GameActionType type)
        {
            return FundsModule.IsFundsCostRow(type) || ScienceModule.IsScienceCostRow(type);
        }

        internal string Describe()
        {
            return "pool=" + Pool + " rows=" + count.ToString(IC) + " costRows=" + CostRowCount.ToString(IC);
        }

        private static int LowerBound(long[] sorted, long value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static double[][] BuildMinTable(double[] values)
        {
            int n = values.Length;
            int levels = 1;
            while ((1 << levels) <= n) levels++;
            var table = new double[levels][];
            table[0] = (double[])values.Clone();
            for (int k = 1; k < levels; k++)
            {
                int len = n - (1 << k) + 1;
                if (len <= 0) { table[k] = new double[0]; continue; }
                table[k] = new double[len];
                int half = 1 << (k - 1);
                for (int i = 0; i < len; i++)
                    table[k][i] = Math.Min(table[k - 1][i], table[k - 1][i + half]);
            }
            return table;
        }

        /// <summary>Minimum of prefix[lo..hi] inclusive (lo &lt;= hi).</summary>
        private double RangeMin(int lo, int hi)
        {
            int len = hi - lo + 1;
            int k = 0;
            while ((1 << (k + 1)) <= len) k++;
            return Math.Min(minTable[k][lo], minTable[k][hi - (1 << k) + 1]);
        }

        private sealed class ReferenceComparer : IEqualityComparer<GameAction>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(GameAction x, GameAction y) => ReferenceEquals(x, y);
            public int GetHashCode(GameAction obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    /// <summary>
    /// The settle step and the debt charge of rule 4, shared by the funds, science and
    /// reputation modules (committed-value rule plan section 2.3). Pure arithmetic over the
    /// running value; the module records a row's effect for the stock pool reference BEFORE
    /// either (they are Parsek-side target adjustments, never something stock applied).
    /// </summary>
    internal static class ReservedFloorSettle
    {
        /// <summary>
        /// The part of a non-cost row's delta <paramref name="delta"/> below the floor:
        /// <c>min(-d, floor - after)</c> when the row lowered the value and left it below the
        /// floor, else 0. Taken back from the row and added to the debt.
        /// </summary>
        internal static double SettleBelowFloor(double after, double delta, double floor)
        {
            if (!(delta < 0.0) || !(after < floor))
                return 0.0;
            return Math.Min(-delta, floor - after);
        }

        /// <summary>
        /// The part of <paramref name="debt"/> charged from the room above
        /// <c>max(floor, 0)</c> (above 0 when no floor applies).
        /// </summary>
        internal static double ChargeFromRoom(double running, double debt, bool hasFloor, double floor)
        {
            if (!(debt > 0.0))
                return 0.0;
            double baseline = hasFloor && floor > 0.0 ? floor : 0.0;
            double room = running - baseline;
            if (!(room > 0.0))
                return 0.0;
            return Math.Min(room, debt);
        }
    }

    /// <summary>
    /// The per-walk floor and debt state one module keeps (committed-value rule plan 2.3).
    /// Shared by the three pool modules so they apply the rule identically.
    /// </summary>
    internal sealed class ReservedFloorWalkState
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal ReservedFloorPlan Plan;
        internal double Debt;
        /// <summary>The latest capture sequence among the losses that made the outstanding
        /// debt: the debt is charged above what the costs captured before it still need.</summary>
        internal long DebtSequence;
        internal int SettledRows;
        internal double SettledTotal;
        internal int ChargedRows;
        internal double ChargedTotal;
        internal double MaxFloor;

        internal void Reset()
        {
            Plan = null;
            ClearCounters();
        }

        internal void ClearCounters()
        {
            Debt = 0.0;
            DebtSequence = 0;
            SettledRows = 0;
            SettledTotal = 0.0;
            ChargedRows = 0;
            ChargedTotal = 0.0;
            MaxFloor = 0.0;
        }

        /// <summary>The floor that holds a loss in <paramref name="action"/> (by its own capture
        /// order), or false when no committed cost lies ahead of it.</summary>
        internal bool TryGetLossFloor(GameAction action, out double floor)
        {
            floor = 0.0;
            if (Plan == null || action == null || action.CaptureSequence <= 0)
                return false;
            if (!Plan.TryGetFloor(action, action.CaptureSequence, out floor))
                return false;
            if (floor > MaxFloor) MaxFloor = floor;
            return true;
        }

        /// <summary>The settle step after a non-cost row: returns the amount taken back.</summary>
        internal double Settle(GameAction action, double after, double delta, double floor)
        {
            double back = ReservedFloorSettle.SettleBelowFloor(after, delta, floor);
            if (back <= 0.0)
                return 0.0;
            Debt += back;
            SettledRows++;
            SettledTotal += back;
            if (action.CaptureSequence > DebtSequence)
                DebtSequence = action.CaptureSequence;
            return back;
        }

        /// <summary>The debt charge after any row: returns the amount taken from the running value.</summary>
        internal double Charge(GameAction action, double running)
        {
            if (!(Debt > 0.0) || Plan == null)
                return 0.0;
            long bound = Math.Max(action != null ? action.CaptureSequence : 0, DebtSequence);
            double floor;
            bool hasFloor = Plan.TryGetFloor(action, bound, out floor);
            double charged = ReservedFloorSettle.ChargeFromRoom(running, Debt, hasFloor, floor);
            if (charged <= 0.0)
                return 0.0;
            Debt -= charged;
            ChargedRows++;
            ChargedTotal += charged;
            if (Debt <= 1e-9)
            {
                Debt = 0.0;
                DebtSequence = 0;
            }
            return charged;
        }

        internal string Summary()
        {
            return "settled=" + SettledRows.ToString(IC) + "(" + SettledTotal.ToString("R", IC) + ")"
                + " charged=" + ChargedRows.ToString(IC) + "(" + ChargedTotal.ToString("R", IC) + ")"
                + " debt=" + Debt.ToString("R", IC)
                + " maxFloor=" + MaxFloor.ToString("R", IC)
                + " plan=" + (Plan != null ? Plan.Describe() : "none");
        }
    }
}
