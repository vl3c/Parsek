using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>One of the three stock pools the ledger patches.</summary>
    public enum StockPool
    {
        Funds,
        Science,
        Reputation,
    }

    /// <summary>
    /// One ledger row as the stock pool reference reads it: its id, its UT, whether stock
    /// itself applied it (<see cref="LedgerRowWriter.StockApplied"/>), and what the visible
    /// walk's dispatch of it moved the pool's running value by (<see cref="Effect"/>: stock's
    /// 0 clamp and the reputation curve included, as the walk applied them; NaN when the walk
    /// did not dispatch it, a row past a cutoff walk's now).
    /// </summary>
    internal struct StockPoolRow
    {
        internal string ActionId;
        internal double UT;
        internal bool StockApplied;
        internal double Effect;
        /// <summary>
        /// What stock paid for the row beyond <see cref="Effect"/> for an identity the ledger
        /// already credited (design 3.15 step 5, <see cref="IStockPoolModule.GetTakeBackDeltas"/>);
        /// 0 for every other row. Read only the first time the reference steps the row.
        /// </summary>
        internal double TakeBack;
    }

    /// <summary>
    /// What the reference has already accounted for one row: <see cref="Effect"/>, the row's
    /// walk effect at the last patch (NaN: seen but not walked, a committed future row), and
    /// <see cref="Stepped"/>, the part of it the reference stepped into its expected pool as
    /// something stock applied. A row counted any other way (a future row, a Parsek-authored
    /// row, a re-baseline) carries <see cref="Stepped"/> 0: its effect is in the pool through
    /// the patch's write. An adjustment row is counted at creation with both equal to its
    /// amount: the live pool already holds it.
    /// </summary>
    internal struct CountedRow
    {
        internal double Effect;
        internal double Stepped;
    }

    /// <summary>The saved and in-memory reference for one pool.</summary>
    internal sealed class StockPoolReferenceState
    {
        internal readonly StockPool Pool;
        /// <summary>False until the first patch of a save that carried no reference
        /// (an older build), or after a reset.</summary>
        internal bool Initialized;
        /// <summary>The pool value Parsek left at its last patch (a no-op patch re-reads live).</summary>
        internal double LastWritten;
        /// <summary>Science only: the three pending adjusters at <see cref="LastWritten"/>.</summary>
        internal double PendingAdjust;
        /// <summary>The now of the last patch (NaN when unknown). A row at or before it was
        /// already in stock's pool at <see cref="LastWritten"/>; only a later row can have
        /// met stock's 0 clamp since (<see cref="StockPoolMeasurement.ClampForgiven"/>).</summary>
        internal double LastPatchUT = double.NaN;
        internal readonly Dictionary<string, CountedRow> Counted =
            new Dictionary<string, CountedRow>(StringComparer.Ordinal);

        internal StockPoolReferenceState(StockPool pool)
        {
            Pool = pool;
        }

        internal void Clear()
        {
            Initialized = false;
            LastWritten = 0.0;
            PendingAdjust = 0.0;
            LastPatchUT = double.NaN;
            Counted.Clear();
        }
    }

    /// <summary>The outcome of <see cref="StockPoolReference.Measure"/> for one patch.</summary>
    internal sealed class StockPoolMeasurement
    {
        internal StockPool Pool;
        internal bool FreshBaseline;
        internal bool Authoritative;
        internal double LastWritten;
        internal double Expected;
        internal double Live;
        /// <summary>live - expected when beyond epsilon on a measured patch, else 0.</summary>
        internal double Leak;
        internal bool LeakKept;
        internal int SteppedRows;
        internal double SteppedTotal;
        internal int RemovedRows;
        internal int ReversedRows;
        internal double ReversedTotal;
        internal int RowsWithoutId;
        internal double PendingStep;
        /// <summary>
        /// A measured fresh baseline (a save with no saved reference): the gap between the
        /// live pool and the leak-free target is filed as one adjustment row, so the first
        /// patch leaves the pool as the older build left it (<see cref="AdoptedGap"/>).
        /// </summary>
        internal bool GapAdopted;
        internal double AdoptedGap;
        /// <summary>
        /// An authoritative patch that dropped rows the reference had stepped: their stepped
        /// part stays in the pool (stock applied it), filed as one adjustment row.
        /// </summary>
        internal double CarriedRemoved;
        /// <summary>
        /// Counted rows that left the effective ledger while staying in the ledger itself
        /// (tombstoned, superseded, session-suppressed): dropped from the reference with no
        /// step back and no carry, so the walk's removal of their effect reaches the pool.
        /// </summary>
        internal int DroppedOutsideEls;
        /// <summary>The patch's now (recorded as the state's last patch UT at commit).</summary>
        internal double NowUT = double.NaN;
        /// <summary>
        /// The part of a positive leak that is stock's 0 clamp forgiving a loss the walk did
        /// not clamp (a reservation holds the pool below the walk's running value): with
        /// <c>AllowNegativeCurrency</c> off, what stock's clamp takes off the rows stepped this
        /// patch that stock applied since the last patch (UT after it), replayed in order from
        /// the last written pool, capped at the leak. Not filed; the target stays below 0 and
        /// later gains repay it (the debt of design 3.13 rule 4).
        /// </summary>
        internal double ClampForgiven;
        /// <summary>
        /// The amount this patch files as a <see cref="GameActionType.StockPoolAdjustment"/>
        /// row (the leak, the adopted gap or the carried removed rows; 0 when none) and adds
        /// to its write once. Later walks carry the row.
        /// </summary>
        internal double Adjustment;
        /// <summary>The adjustment row the patch filed, counted at creation by <see cref="StockPoolReference.Commit"/>.</summary>
        internal GameAction AdjustmentRow;
        internal readonly Dictionary<string, double> NewStepped =
            new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>
        /// Rows whose duplicate stock payment this patch takes back (design 3.15 step 5): id
        /// and amount. A measured patch steps such a row by what stock paid, so the payment is
        /// not a leak, and the write (the walk's target) takes it back; an authoritative patch
        /// writes the target anyway and lists them for the log. Never an adjustment row.
        /// </summary>
        internal readonly List<KeyValuePair<string, double>> TakenBack = new List<KeyValuePair<string, double>>();
        internal double TakenBackTotal;
        internal bool TakenBackByAuthoritativeWrite;
    }

    /// <summary>
    /// The drawdown guard's incremental stock reference (committed-value rule plan section
    /// 2.4, phase 2). Per pool it answers one question at every patch: did the live pool move
    /// by anything other than what stock applied for rows the ledger holds? Starting from
    /// the value Parsek last wrote, it steps every row stock applied since (a row at or
    /// before now, written by a <see cref="LedgerRowWriter.StockApplied"/> writer) by the
    /// change in its effect in the visible walk (the walk's running value after the row
    /// minus before it, stock's 0 clamp and the reputation curve included, as the walk
    /// applied them, minus what was counted). Science also takes the change in its pending
    /// adjusters. A row the reference stepped that has left the ledger is stepped back.
    /// Whatever is left between live and that expectation is a leak, filed as a
    /// <see cref="GameActionType.StockPoolAdjustment"/> ledger row (counted at creation, so it
    /// never steps) and added to this patch's write once; every later walk carries it, so
    /// the walk's running value is the pool's own value and its 0 clamp and curve run where
    /// stock's do. An earning or spend the ledger does not model stays as stock left it, and
    /// a reservation that releases comes back (nothing in the expectation fights it).
    ///
    /// <para>An authoritative patch (a time-travel signal, a Re-Fly merge or discard, a
    /// clock-cursor crossing that found the pool where the last patch left it) measures no
    /// leak: it writes the target (plus the stepped part of any row it dropped) and
    /// re-baselines. A pool with no saved reference starts from the live pool with every
    /// row counted, and a measured first patch files the gap between live and its leak-free
    /// target as one adjustment row, so the pool stays where the older build left it.</para>
    ///
    /// <para>Pure core plus the three static states the patcher reads and the scenario
    /// saves. No KSP singletons.</para>
    /// </summary>
    internal static class StockPoolReference
    {
        internal const string Tag = "StockPoolReference";
        internal const string NodeName = "STOCK_POOL_REFERENCE";
        internal const string FundsNodeName = "FUNDS";
        internal const string ScienceNodeName = "SCIENCE";
        internal const string ReputationNodeName = "REPUTATION";
        internal const string LastWrittenKey = "lastWritten";
        internal const string PendingAdjustKey = "pendingAdjust";
        internal const string LastPatchUTKey = "lastPatchUT";
        internal const string RowKey = "row";

        private const double AmountTolerance = 1e-9;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal static readonly StockPoolReferenceState Funds = new StockPoolReferenceState(StockPool.Funds);
        internal static readonly StockPoolReferenceState Science = new StockPoolReferenceState(StockPool.Science);
        internal static readonly StockPoolReferenceState Reputation = new StockPoolReferenceState(StockPool.Reputation);

        internal static StockPoolReferenceState For(StockPool pool)
        {
            switch (pool)
            {
                case StockPool.Funds: return Funds;
                case StockPool.Science: return Science;
                case StockPool.Reputation: return Reputation;
            }
            throw new ArgumentOutOfRangeException(nameof(pool), pool, "unknown StockPool");
        }

        internal static void ResetForTesting()
        {
            Funds.Clear();
            Science.Clear();
            Reputation.Clear();
        }

        // ------------------------------------------------------------------
        // Rows
        // ------------------------------------------------------------------

        /// <summary>True when <paramref name="action"/> is a row of <paramref name="pool"/>.
        /// Seeds are not rows: the seed is the walk's starting value, read off the live pool.</summary>
        internal static bool IsPoolRow(StockPool pool, GameAction action)
        {
            if (action == null || RecalculationEngine.IsSeedType(action.Type))
                return false;
            if (action.Type == GameActionType.StockPoolAdjustment)
                return action.AdjustmentPool == pool;
            switch (pool)
            {
                case StockPool.Funds: return FundsModule.AffectsFunds(action.Type);
                case StockPool.Science: return ScienceModule.AffectsScience(action.Type);
                case StockPool.Reputation: return ReputationModule.AffectsReputation(action.Type);
            }
            throw new ArgumentOutOfRangeException(nameof(pool), pool, "unknown StockPool");
        }

        /// <summary>
        /// The row of <paramref name="action"/> for <paramref name="pool"/>, or false when the
        /// action's type never moves that pool. <paramref name="effects"/> is the visible walk's
        /// per-row effect (<see cref="IStockPoolModule.GetRowEffects"/>); a row it does not hold
        /// was not dispatched (NaN).
        /// </summary>
        internal static bool TryBuildRow(
            StockPool pool, GameAction action, IReadOnlyDictionary<string, double> effects,
            out StockPoolRow row, IReadOnlyDictionary<string, double> takeBackDeltas = null)
        {
            row = default(StockPoolRow);
            if (!IsPoolRow(pool, action))
                return false;

            double effect = double.NaN;
            double found;
            if (effects != null && !string.IsNullOrEmpty(action.ActionId)
                && effects.TryGetValue(action.ActionId, out found)
                && !double.IsNaN(found) && !double.IsInfinity(found))
                effect = found;

            double takeBack = 0.0;
            double tb;
            if (takeBackDeltas != null && !double.IsNaN(effect) && !string.IsNullOrEmpty(action.ActionId)
                && takeBackDeltas.TryGetValue(action.ActionId, out tb)
                && !double.IsNaN(tb) && !double.IsInfinity(tb))
                takeBack = tb;

            row = new StockPoolRow
            {
                ActionId = action.ActionId,
                UT = action.UT,
                StockApplied = !action.ParsekAuthored,
                Effect = effect,
                TakeBack = takeBack,
            };
            return true;
        }

        /// <summary>Every row of <paramref name="pool"/> in <paramref name="actions"/>.</summary>
        internal static List<StockPoolRow> BuildRows(
            StockPool pool, IEnumerable<GameAction> actions, IReadOnlyDictionary<string, double> effects,
            IReadOnlyDictionary<string, double> takeBackDeltas = null)
        {
            var rows = new List<StockPoolRow>();
            if (actions == null)
                return rows;
            foreach (var action in actions)
            {
                StockPoolRow row;
                if (TryBuildRow(pool, action, effects, out row, takeBackDeltas))
                    rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// The <see cref="GameActionType.StockPoolAdjustment"/> row for <paramref name="amount"/>
        /// of <paramref name="pool"/> at <paramref name="ut"/>: untagged, with a fresh id.
        /// </summary>
        internal static GameAction BuildAdjustmentRow(StockPool pool, double amount, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.StockPoolAdjustment,
                UT = ut,
                AdjustmentPool = pool,
                AdjustmentAmount = amount,
            };
        }

        /// <summary>The sum of the adjustment rows of <paramref name="pool"/> in <paramref name="actions"/>.</summary>
        internal static double SumAdjustments(StockPool pool, IEnumerable<GameAction> actions)
        {
            double sum = 0.0;
            if (actions == null)
                return sum;
            foreach (var a in actions)
            {
                if (a != null && a.Type == GameActionType.StockPoolAdjustment && a.AdjustmentPool == pool)
                    sum += a.AdjustmentAmount;
            }
            return sum;
        }

        // ------------------------------------------------------------------
        // Measure and commit
        // ------------------------------------------------------------------

        /// <summary>True when the reference counts a row <paramref name="rows"/> does not hold.</summary>
        internal static bool HasCountedRowsMissingFrom(StockPoolReferenceState state, IList<StockPoolRow> rows)
        {
            if (state == null || !state.Initialized || state.Counted.Count == 0)
                return false;
            var present = new HashSet<string>(StringComparer.Ordinal);
            if (rows != null)
                for (int i = 0; i < rows.Count; i++)
                    if (!string.IsNullOrEmpty(rows[i].ActionId))
                        present.Add(rows[i].ActionId);
            foreach (string id in state.Counted.Keys)
                if (!present.Contains(id))
                    return true;
            return false;
        }

        /// <summary>
        /// Measures one patch of <paramref name="state"/>'s pool. Does not change the state:
        /// the caller files <see cref="StockPoolMeasurement.Adjustment"/> as an adjustment row
        /// (when nonzero), writes the pool, and then calls <see cref="Commit"/> with the value
        /// the pool holds afterwards.
        /// </summary>
        /// <param name="rows">Every row of the pool (<see cref="BuildRows"/>).</param>
        /// <param name="nowUT">The patch's now: rows after it have not been applied by stock yet.</param>
        /// <param name="live">The live pool before the write.</param>
        /// <param name="pendingAdjustNow">Science: the pending adjusters now; 0 for the others.</param>
        /// <param name="freshBaselineTarget">The value the patch would write with no adjustment,
        /// before the 0 floor (funds and science: the projected minimum, science plus its
        /// pending adjusters; reputation: the running value). Read only when the state has no
        /// reference: a measured patch adopts <c>live - freshBaselineTarget</c> as an
        /// adjustment. NaN adopts nothing.</param>
        /// <param name="idsInLedger">Every action id the ledger itself still holds (effective
        /// or not). A counted row missing from <paramref name="rows"/> but named here left the
        /// effective ledger only (a tombstone, a supersede, a session suppression): it is
        /// dropped without a step back or a carry. Only a row physically gone from the ledger
        /// (a duplicate-rollout repair, a dedupe, the revert strategy prune) is stepped back.
        /// Null treats every missing row as physically gone.</param>
        /// <param name="clampAtZero">Funds and science with <c>AllowNegativeCurrency</c> off:
        /// stock's pool never goes below 0, so what its clamp took off the rows it applied
        /// since the last patch (replayed from the last written pool) is forgiveness, and that
        /// part of a positive leak is not filed.</param>
        internal static StockPoolMeasurement Measure(
            StockPoolReferenceState state, IList<StockPoolRow> rows, double nowUT, double live,
            double pendingAdjustNow, bool authoritative, double epsilon,
            double freshBaselineTarget = double.NaN, ICollection<string> idsInLedger = null,
            bool clampAtZero = false)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var m = new StockPoolMeasurement
            {
                Pool = state.Pool,
                Live = live,
                Authoritative = authoritative,
                LastWritten = state.LastWritten,
                Expected = live,
                NowUT = nowUT,
            };

            var present = new HashSet<string>(StringComparer.Ordinal);
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (string.IsNullOrEmpty(rows[i].ActionId))
                        m.RowsWithoutId++;
                    else
                        present.Add(rows[i].ActionId);
                }
            }

            if (!state.Initialized)
            {
                // No saved reference: the live pool is the starting point and every row is
                // taken as already in it. A measured patch keeps the pool where the older
                // build left it: the gap to the leak-free target becomes one adjustment row
                // (what the old guard kept by clamping on every patch). An authoritative one
                // writes the target, as it always has.
                m.FreshBaseline = true;
                m.LastWritten = live;
                if (!authoritative && !double.IsNaN(freshBaselineTarget)
                    && !double.IsInfinity(freshBaselineTarget))
                {
                    m.GapAdopted = true;
                    m.AdoptedGap = live - freshBaselineTarget;
                    if (Math.Abs(m.AdoptedGap) > epsilon)
                        m.Adjustment = m.AdoptedGap;
                }
                return m;
            }

            // Rows the reference stepped that have left the ledger: step them back, so the
            // part stock applied for them reads as unexplained (it stays in the pool).
            double expected = state.LastWritten;
            double removedStepped = 0.0;
            var removedIds = new List<string>();
            foreach (var kv in state.Counted)
            {
                if (!present.Contains(kv.Key))
                    removedIds.Add(kv.Key);
            }
            removedIds.Sort(StringComparer.Ordinal);
            for (int i = 0; i < removedIds.Count; i++)
            {
                m.RemovedRows++;
                if (idsInLedger != null && idsInLedger.Contains(removedIds[i]))
                {
                    m.DroppedOutsideEls++;
                    continue;
                }
                CountedRow counted = state.Counted[removedIds[i]];
                if (Math.Abs(counted.Stepped) <= AmountTolerance)
                    continue;
                expected -= counted.Stepped;
                removedStepped += counted.Stepped;
                m.ReversedRows++;
                m.ReversedTotal -= counted.Stepped;
            }

            if (authoritative)
            {
                // The write is the target; a dropped row's stepped part stays in the pool
                // (stock applied it), filed as an adjustment. A duplicate stock payment is
                // taken back by that write; it is listed for the log.
                CollectAuthoritativeTakeBacks(state, rows, nowUT, epsilon, m);
                m.Expected = expected;
                if (Math.Abs(removedStepped) > epsilon)
                {
                    m.CarriedRemoved = removedStepped;
                    m.Adjustment = removedStepped;
                }
                return m;
            }

            // Stock's own pool along the rows it applied since the last patch, clamped at 0 the
            // way stock clamps: what the clamp takes off is forgiveness, not a leak.
            double stockSim = expected;
            double clampForgiven = 0.0;
            if (rows != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < rows.Count; i++)
                {
                    StockPoolRow row = rows[i];
                    if (string.IsNullOrEmpty(row.ActionId) || !seen.Add(row.ActionId))
                        continue;
                    if (!row.StockApplied || row.UT > nowUT || double.IsNaN(row.Effect))
                        continue;
                    CountedRow counted;
                    bool wasCounted = state.Counted.TryGetValue(row.ActionId, out counted);
                    // A row counted while the walk did not dispatch it (a committed future row)
                    // crossing the clock: stock did not apply it, the write does.
                    if (wasCounted && double.IsNaN(counted.Effect))
                        continue;
                    double step = wasCounted ? row.Effect - counted.Effect : row.Effect;
                    // Take-back (design 3.15 step 5): the first time a row is stepped, stock
                    // applied what it paid (the walk effect plus the duplicate part); the write,
                    // the walk's target, takes the duplicate part back. Only the walk effect
                    // stays stepped, since only that stays in the pool.
                    double takeBack = !wasCounted && Math.Abs(row.TakeBack) > epsilon ? row.TakeBack : 0.0;
                    if (takeBack != 0.0)
                    {
                        m.TakenBack.Add(new KeyValuePair<string, double>(row.ActionId, takeBack));
                        m.TakenBackTotal += takeBack;
                    }
                    if (Math.Abs(step) <= AmountTolerance && takeBack == 0.0)
                        continue;
                    expected += step + takeBack;
                    if (clampAtZero && (double.IsNaN(state.LastPatchUT) || row.UT > state.LastPatchUT))
                    {
                        stockSim += step + takeBack;
                        if (stockSim < 0.0)
                        {
                            clampForgiven -= stockSim;
                            stockSim = 0.0;
                        }
                    }
                    m.SteppedRows++;
                    m.SteppedTotal += step;
                    m.NewStepped[row.ActionId] = (wasCounted ? counted.Stepped : 0.0) + step;
                }
            }

            double pendingStep = pendingAdjustNow - state.PendingAdjust;
            if (Math.Abs(pendingStep) > AmountTolerance)
            {
                expected += pendingStep;
                m.PendingStep = pendingStep;
            }

            m.Expected = expected;
            double leak = live - expected;
            if (clampForgiven > AmountTolerance && leak > 0.0)
            {
                // Stock stopped the pool at 0 where a row it applied since the last patch took
                // it below: that much of the gap is forgiveness, and filing it would pay
                // committed costs with money nobody earned. Only the rest is a leak. A row from
                // before the last patch (a transient leak's row landing late) was already in
                // stock's pool and never counts here.
                double forgiven = Math.Min(leak, clampForgiven);
                m.ClampForgiven = forgiven;
                leak -= forgiven;
            }
            if (Math.Abs(leak) > epsilon)
            {
                m.Leak = leak;
                m.LeakKept = true;
                m.Adjustment = leak;
            }
            return m;
        }

        /// <summary>
        /// Records a finished patch: <paramref name="poolAfter"/> (the pool read back after
        /// the write, or the live pool of a no-op) becomes the last written value and every
        /// row is counted at its walk effect. A row keeps what the reference stepped for it
        /// (plus this patch's steps); a row new to the reference that was not stepped counts
        /// with nothing stepped. The patch's adjustment row (when it filed one) is counted at
        /// its amount, stepped in full, so the next walk's dispatch of it steps nothing. Rows
        /// that left the ledger are dropped.
        /// </summary>
        internal static void Commit(
            StockPoolReferenceState state, IList<StockPoolRow> rows, StockPoolMeasurement m,
            double poolAfter, double pendingAdjustNow)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (m == null) throw new ArgumentNullException(nameof(m));

            var next = new Dictionary<string, CountedRow>(StringComparer.Ordinal);
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    StockPoolRow row = rows[i];
                    if (string.IsNullOrEmpty(row.ActionId) || next.ContainsKey(row.ActionId))
                        continue;
                    double stepped;
                    if (!m.NewStepped.TryGetValue(row.ActionId, out stepped))
                    {
                        CountedRow previous;
                        stepped = !m.FreshBaseline && state.Counted.TryGetValue(row.ActionId, out previous)
                            ? previous.Stepped
                            : 0.0;
                    }
                    next[row.ActionId] = new CountedRow { Effect = row.Effect, Stepped = stepped };
                }
            }

            GameAction adjustment = m.AdjustmentRow;
            if (adjustment != null && !string.IsNullOrEmpty(adjustment.ActionId))
            {
                next[adjustment.ActionId] = new CountedRow
                {
                    Effect = adjustment.AdjustmentAmount,
                    Stepped = adjustment.AdjustmentAmount,
                };
            }

            state.Counted.Clear();
            foreach (var kv in next)
                state.Counted[kv.Key] = kv.Value;
            state.LastWritten = poolAfter;
            state.PendingAdjust = state.Pool == StockPool.Science ? pendingAdjustNow : 0.0;
            state.LastPatchUT = m.NowUT;
            state.Initialized = true;
        }

        /// <summary>
        /// The rows an authoritative patch takes back by its write (design 3.15 step 5): the
        /// same first-step rule as a measured patch, recorded for the log only.
        /// </summary>
        private static void CollectAuthoritativeTakeBacks(
            StockPoolReferenceState state, IList<StockPoolRow> rows, double nowUT, double epsilon,
            StockPoolMeasurement m)
        {
            if (rows == null)
                return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                StockPoolRow row = rows[i];
                if (string.IsNullOrEmpty(row.ActionId) || !seen.Add(row.ActionId))
                    continue;
                if (!row.StockApplied || row.UT > nowUT || double.IsNaN(row.Effect)
                    || state.Counted.ContainsKey(row.ActionId) || !(Math.Abs(row.TakeBack) > epsilon))
                    continue;
                m.TakenBack.Add(new KeyValuePair<string, double>(row.ActionId, row.TakeBack));
                m.TakenBackTotal += row.TakeBack;
                m.TakenBackByAuthoritativeWrite = true;
            }
        }

        // ------------------------------------------------------------------
        // Write targets
        // ------------------------------------------------------------------

        /// <summary>
        /// The funds or science value a patch writes: this patch's adjustment added to the
        /// target BEFORE stock's 0 floor (adding it after the floor would create the
        /// difference on every purchase that spends it). With <c>AllowNegativeCurrency</c>
        /// on, the existing-deficit rule of
        /// <see cref="KspStatePatcher.ResolveNegativeCurrencyPatchTarget(double, double, double, double, bool)"/>
        /// applies to the shifted values. With no adjustment this is exactly today's target.
        /// The next walk carries the adjustment row, so its target holds it on its own.
        /// </summary>
        internal static double ResolveCurrencyWriteTarget(
            double unflooredTarget, double runningBalanceNow, double currentLive,
            bool allowNegativeCurrency, double adjustment)
        {
            double shifted = unflooredTarget + adjustment;
            double floored = shifted > 0.0 ? shifted : 0.0;
            if (!allowNegativeCurrency)
                return floored;
            return KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                floored, shifted, runningBalanceNow + adjustment, currentLive, true);
        }

        /// <summary>The reputation a patch writes: the running value plus this patch's adjustment.</summary>
        internal static double ResolveReputationWriteTarget(double runningRep, double adjustment)
        {
            return runningRep + adjustment;
        }

        // ------------------------------------------------------------------
        // Persistence (the PARSEK scenario node)
        // ------------------------------------------------------------------

        private static string NodeNameFor(StockPool pool)
        {
            switch (pool)
            {
                case StockPool.Funds: return FundsNodeName;
                case StockPool.Science: return ScienceNodeName;
                case StockPool.Reputation: return ReputationNodeName;
            }
            throw new ArgumentOutOfRangeException(nameof(pool), pool, "unknown StockPool");
        }

        /// <summary>
        /// Writes the three references under <see cref="NodeName"/> (replacing any older
        /// copy). A pool that was never patched writes no child, so its load starts afresh.
        /// A counted row whose effect and step are both 0 is not written (a missing row reads
        /// as new, which steps it by its effect, 0); a row seen but not walked is written with
        /// its NaN effect, so it still crosses the clock unstepped after a load.
        /// </summary>
        internal static void SaveTo(ConfigNode parent)
        {
            if (parent == null)
                return;
            parent.RemoveNodes(NodeName);
            var root = new ConfigNode(NodeName);
            int pools = 0;
            int savedRows = 0;
            foreach (var state in new[] { Funds, Science, Reputation })
            {
                if (!state.Initialized)
                    continue;
                var child = root.AddNode(NodeNameFor(state.Pool));
                child.AddValue(LastWrittenKey, state.LastWritten.ToString("R", IC));
                if (state.Pool == StockPool.Science)
                    child.AddValue(PendingAdjustKey, state.PendingAdjust.ToString("R", IC));
                if (!double.IsNaN(state.LastPatchUT) && !double.IsInfinity(state.LastPatchUT))
                    child.AddValue(LastPatchUTKey, state.LastPatchUT.ToString("R", IC));
                var ids = new List<string>(state.Counted.Keys);
                ids.Sort(StringComparer.Ordinal);
                for (int i = 0; i < ids.Count; i++)
                {
                    CountedRow c = state.Counted[ids[i]];
                    if (!double.IsNaN(c.Effect) && Math.Abs(c.Effect) <= AmountTolerance
                        && Math.Abs(c.Stepped) <= AmountTolerance)
                        continue;
                    child.AddValue(RowKey,
                        ids[i] + " " + c.Effect.ToString("R", IC) + " " + c.Stepped.ToString("R", IC));
                    savedRows++;
                }
                pools++;
            }
            parent.AddNode(root);
            ParsekLog.Verbose(Tag,
                $"Saved: pools={pools.ToString(IC)} rows={savedRows.ToString(IC)} " +
                $"funds={Describe(Funds)} science={Describe(Science)} reputation={Describe(Reputation)}");
        }

        /// <summary>
        /// Replaces the three references with the ones <paramref name="parent"/> carries. A
        /// pool the node does not carry (a save from an older build, or a pool never patched)
        /// starts afresh at its first patch: the live pool with every row counted.
        /// </summary>
        internal static void LoadFrom(ConfigNode parent, string loadKindLabel)
        {
            ConfigNode root = parent?.GetNode(NodeName);
            int pools = 0;
            int rowsRead = 0;
            int rowsBad = 0;
            foreach (var state in new[] { Funds, Science, Reputation })
            {
                state.Clear();
                ConfigNode child = root?.GetNode(NodeNameFor(state.Pool));
                if (child == null)
                    continue;
                double lastWritten;
                if (!TryParse(child.GetValue(LastWrittenKey), out lastWritten))
                {
                    ParsekLog.Warn(Tag,
                        $"Load: {state.Pool} reference unreadable (lastWritten='{child.GetValue(LastWrittenKey) ?? "<none>"}'): " +
                        "starting afresh at the first patch");
                    continue;
                }
                state.LastWritten = lastWritten;
                double pending;
                if (state.Pool == StockPool.Science && TryParse(child.GetValue(PendingAdjustKey), out pending))
                    state.PendingAdjust = pending;
                double lastPatchUT;
                state.LastPatchUT = TryParse(child.GetValue(LastPatchUTKey), out lastPatchUT)
                    ? lastPatchUT : double.NaN;
                string[] values = child.GetValues(RowKey);
                for (int i = 0; i < values.Length; i++)
                {
                    string[] parts = (values[i] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    double effect, stepped;
                    if (parts.Length != 3 || !TryParseEffect(parts[1], out effect) || !TryParse(parts[2], out stepped))
                    {
                        rowsBad++;
                        continue;
                    }
                    state.Counted[parts[0]] = new CountedRow { Effect = effect, Stepped = stepped };
                    rowsRead++;
                }
                state.Initialized = true;
                pools++;
            }

            string line =
                $"Loaded ({loadKindLabel ?? "unknown"}): pools={pools.ToString(IC)} rows={rowsRead.ToString(IC)} " +
                $"badRows={rowsBad.ToString(IC)} funds={Describe(Funds)} science={Describe(Science)} " +
                $"reputation={Describe(Reputation)}" +
                (root == null ? " (no saved reference: each pool starts from its live value at its first patch)" : "");
            if (rowsBad > 0)
                ParsekLog.Warn(Tag, line);
            else
                ParsekLog.Info(Tag, line);
        }

        private static bool TryParse(string s, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(s))
                return false;
            if (!double.TryParse(s, NumberStyles.Float, IC, out value))
                return false;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>A counted effect: a finite number, or NaN (seen but not walked).</summary>
        private static bool TryParseEffect(string s, out double value)
        {
            value = double.NaN;
            if (string.Equals(s, "NaN", StringComparison.Ordinal))
                return true;
            return TryParse(s, out value);
        }

        // ------------------------------------------------------------------
        // Log text
        // ------------------------------------------------------------------

        internal static string Describe(StockPoolReferenceState state)
        {
            if (state == null || !state.Initialized)
                return "unset";
            return "lastWritten=" + state.LastWritten.ToString("R", IC)
                + "/rows=" + state.Counted.Count.ToString(IC);
        }

        /// <summary>The one-line summary of a measurement, for the patch log.</summary>
        internal static string FormatMeasurement(StockPoolMeasurement m)
        {
            if (m == null)
                return "measurement=<none>";
            string mode = m.FreshBaseline ? "fresh-baseline" : m.Authoritative ? "authoritative" : "measured";
            return "pool=" + m.Pool
                + " mode=" + mode
                + " lastWritten=" + m.LastWritten.ToString("R", IC)
                + " expected=" + m.Expected.ToString("R", IC)
                + " live=" + m.Live.ToString("R", IC)
                + " stepped=" + m.SteppedRows.ToString(IC) + "(" + m.SteppedTotal.ToString("R", IC) + ")"
                + " removed=" + m.RemovedRows.ToString(IC)
                + " reversed=" + m.ReversedRows.ToString(IC) + "(" + m.ReversedTotal.ToString("R", IC) + ")"
                + " pendingStep=" + m.PendingStep.ToString("R", IC)
                + " leak=" + m.Leak.ToString("R", IC)
                + " adjustment=" + m.Adjustment.ToString("R", IC)
                + (m.GapAdopted ? " adoptedGap=" + m.AdoptedGap.ToString("R", IC) : "")
                + (m.CarriedRemoved != 0.0 ? " carriedRemoved=" + m.CarriedRemoved.ToString("R", IC) : "")
                + (m.DroppedOutsideEls > 0 ? " droppedOutsideEls=" + m.DroppedOutsideEls.ToString(IC) : "")
                + (m.ClampForgiven != 0.0 ? " clampForgiven=" + m.ClampForgiven.ToString("R", IC) : "")
                + (m.TakenBack.Count > 0
                    ? " takenBack=" + m.TakenBack.Count.ToString(IC) + "(" + m.TakenBackTotal.ToString("R", IC) + ")"
                    : "")
                + (m.RowsWithoutId > 0 ? " rowsWithoutId=" + m.RowsWithoutId.ToString(IC) : "");
        }
    }
}
