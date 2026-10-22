using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Parsek
{
    /// <summary>One of the three stock pools the ledger patches.</summary>
    internal enum StockPool
    {
        Funds,
        Science,
        Reputation,
    }

    /// <summary>
    /// One ledger row as the stock pool reference reads it: its id, its UT, whether stock
    /// itself applied it (<see cref="LedgerRowWriter.StockApplied"/>), and the amount it moves
    /// the pool by. Funds and science: the walk's delta for the row. Reputation: the amount
    /// stock applied, nominal (curved at the live value) unless <see cref="PostCurve"/>.
    /// </summary>
    internal struct StockPoolRow
    {
        internal string ActionId;
        internal double UT;
        internal bool StockApplied;
        internal double Amount;
        internal bool PostCurve;
    }

    /// <summary>
    /// What the reference has already accounted for one row: <see cref="Amount"/>, the
    /// row's amount at the last patch, and <see cref="Stepped"/>, the part of it the
    /// reference stepped into its expected pool as something stock applied. A row counted
    /// any other way (a future row, a Parsek-authored row, a re-baseline) carries
    /// <see cref="Stepped"/> 0: its effect is in the pool through the patch's write.
    /// </summary>
    internal struct CountedRow
    {
        internal double Amount;
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
        /// <summary>Everything stock applied that the ledger holds no row for, kept on every write.</summary>
        internal double CumLeak;
        /// <summary>Science only: the three pending adjusters at <see cref="LastWritten"/>.</summary>
        internal double PendingAdjust;
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
            CumLeak = 0.0;
            PendingAdjust = 0.0;
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
        internal double CumLeakBefore;
        internal double CumLeakAfter;
        internal int SteppedRows;
        internal double SteppedTotal;
        internal int RemovedRows;
        internal int ReversedRows;
        internal double ReversedTotal;
        internal int RowsWithoutId;
        internal double PendingStep;
        internal readonly Dictionary<string, double> NewStepped =
            new Dictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The drawdown guard's incremental stock reference (committed-value rule plan section
    /// 2.4, phase 2). Per pool it answers one question at every patch: did the live pool move
    /// by anything other than what stock applied for rows the ledger holds? Starting from
    /// the value Parsek last wrote, it steps every row stock applied since (a row at or
    /// before now, written by a <see cref="LedgerRowWriter.StockApplied"/> writer, whose
    /// counted amount differs from its amount) the way stock applies it: funds and science
    /// clamped at 0 unless <c>AllowNegativeCurrency</c>, reputation through stock's curve at
    /// the reference's own value unless the row is stored post-curve. Science also takes the
    /// change in its pending adjusters. A row the reference stepped that has left the ledger
    /// is stepped back. Whatever is left between live and that expectation is a leak, kept
    /// for good in <see cref="StockPoolReferenceState.CumLeak"/> and added to every later
    /// write, so an earning or spend the ledger does not model stays as stock left it, and a
    /// reservation that releases comes back (nothing in the expectation fights it).
    ///
    /// <para>An authoritative patch (a time-travel signal, a Re-Fly merge or discard, a
    /// clock-cursor crossing that found the pool where the last patch left it) measures no
    /// leak: it writes the target plus the kept leak and re-baselines. A pool with no saved
    /// reference starts from the live pool with every row counted.</para>
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
        internal const string CumLeakKey = "cumLeak";
        internal const string PendingAdjustKey = "pendingAdjust";
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

        /// <summary>The kept leak of a pool (0 before its first patch).</summary>
        internal static double CumLeakOf(StockPool pool)
        {
            return For(pool).CumLeak;
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

        /// <summary>
        /// The row of <paramref name="action"/> for <paramref name="pool"/>, or false when the
        /// action's type never moves that pool (the reference does not track it at all). An
        /// ineffective row of a pool type is a row with amount 0. Seeds are not rows: the
        /// seed is the walk's starting value, read off the live pool.
        /// </summary>
        internal static bool TryBuildRow(StockPool pool, GameAction action, out StockPoolRow row)
        {
            row = default(StockPoolRow);
            if (action == null || RecalculationEngine.IsSeedType(action.Type))
                return false;

            double amount;
            bool postCurve = false;
            switch (pool)
            {
                case StockPool.Funds:
                    if (!FundsModule.AffectsFunds(action.Type))
                        return false;
                    if (!FundsModule.TryGetRowDelta(action, out amount))
                        amount = 0.0;
                    break;
                case StockPool.Science:
                    if (!ScienceModule.AffectsScience(action.Type))
                        return false;
                    if (!ScienceModule.TryGetRowDelta(action, out amount))
                        amount = 0.0;
                    break;
                case StockPool.Reputation:
                    if (!ReputationModule.TryGetStockReferenceAmount(action, out amount, out postCurve))
                        return false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pool), pool, "unknown StockPool");
            }

            if (double.IsNaN(amount) || double.IsInfinity(amount))
                amount = 0.0;

            row = new StockPoolRow
            {
                ActionId = action.ActionId,
                UT = action.UT,
                StockApplied = !action.ParsekAuthored,
                Amount = amount,
                PostCurve = postCurve,
            };
            return true;
        }

        /// <summary>
        /// Every row of <paramref name="pool"/> in <paramref name="actions"/>, in the walk's
        /// own order (UT, earnings before spendings, then sequence), so stock's 0 clamp and
        /// reputation curve are stepped in the order stock met them.
        /// </summary>
        internal static List<StockPoolRow> BuildRows(StockPool pool, IEnumerable<GameAction> actions)
        {
            var rows = new List<StockPoolRow>();
            if (actions == null)
                return rows;
            var ordered = actions
                .Where(a => a != null)
                .OrderBy(a => a.UT)
                .ThenBy(a => RecalculationEngine.IsEarningType(a.Type) ? 0 : 1)
                .ThenBy(a => a.Sequence);
            foreach (var action in ordered)
            {
                StockPoolRow row;
                if (TryBuildRow(pool, action, out row))
                    rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------------
        // Stock's own step
        // ------------------------------------------------------------------

        /// <summary>
        /// Applies <paramref name="amount"/> to <paramref name="value"/> the way stock applies
        /// it to its pool: funds and science through <c>Funding.AddFunds</c> /
        /// <c>ResearchAndDevelopment.AddScience</c>, clamped at 0 unless
        /// <paramref name="allowNegativeCurrency"/>; reputation through
        /// <c>Reputation.AddReputation</c>'s curve at <paramref name="value"/>, or as stored
        /// when <paramref name="postCurve"/>.
        /// </summary>
        internal static double StockStep(
            StockPool pool, double value, double amount, bool postCurve, bool allowNegativeCurrency)
        {
            if (pool == StockPool.Reputation)
            {
                if (postCurve)
                    return value + amount;
                var curved = ReputationModule.ApplyReputationCurve((float)amount, (float)value);
                return value + curved.actualDelta;
            }

            double next = value + amount;
            if (!allowNegativeCurrency && next < 0.0)
                next = 0.0;
            return next;
        }

        // ------------------------------------------------------------------
        // Measure and commit
        // ------------------------------------------------------------------

        /// <summary>
        /// Measures one patch of <paramref name="state"/>'s pool. Does not change the state:
        /// the caller writes the pool from <see cref="StockPoolMeasurement.CumLeakAfter"/> and
        /// then calls <see cref="Commit"/> with the value the pool holds afterwards.
        /// </summary>
        /// <param name="rows">Every row of the pool in the walk's order (<see cref="BuildRows"/>).</param>
        /// <param name="nowUT">The patch's now: rows after it have not been applied by stock yet.</param>
        /// <param name="live">The live pool before the write.</param>
        /// <param name="pendingAdjustNow">Science: the pending adjusters now; 0 for the others.</param>
        internal static StockPoolMeasurement Measure(
            StockPoolReferenceState state, IList<StockPoolRow> rows, double nowUT, double live,
            double pendingAdjustNow, bool allowNegativeCurrency, bool authoritative, double epsilon)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var m = new StockPoolMeasurement
            {
                Pool = state.Pool,
                Live = live,
                Authoritative = authoritative,
                LastWritten = state.LastWritten,
                CumLeakBefore = state.CumLeak,
                CumLeakAfter = state.CumLeak,
                Expected = live,
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
                // taken as already in it.
                m.FreshBaseline = true;
                m.LastWritten = live;
                return m;
            }

            // Rows the reference stepped that have left the ledger: step them back, in a
            // stable order, so their part of the kept leak goes with them.
            double expected = state.LastWritten;
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
                CountedRow counted = state.Counted[removedIds[i]];
                if (Math.Abs(counted.Stepped) <= AmountTolerance)
                    continue;
                double before = expected;
                expected = StockStep(state.Pool, expected, -counted.Stepped,
                    postCurve: false, allowNegativeCurrency: allowNegativeCurrency);
                m.ReversedRows++;
                m.ReversedTotal += expected - before;
            }

            if (authoritative)
            {
                // The write is the target plus the kept leak; a reversed row takes its share
                // of that leak back with it.
                m.CumLeakAfter = state.CumLeak - m.ReversedTotal;
                m.Expected = expected + 0.0;
                return m;
            }

            if (rows != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < rows.Count; i++)
                {
                    StockPoolRow row = rows[i];
                    if (string.IsNullOrEmpty(row.ActionId) || !seen.Add(row.ActionId))
                        continue;
                    if (!row.StockApplied || row.UT > nowUT)
                        continue;
                    CountedRow counted;
                    state.Counted.TryGetValue(row.ActionId, out counted);
                    double diff = row.Amount - counted.Amount;
                    if (Math.Abs(diff) <= AmountTolerance)
                        continue;
                    double before = expected;
                    expected = StockStep(state.Pool, expected, diff, row.PostCurve, allowNegativeCurrency);
                    m.SteppedRows++;
                    m.SteppedTotal += expected - before;
                    m.NewStepped[row.ActionId] = counted.Stepped + diff;
                }
            }

            double pendingStep = pendingAdjustNow - state.PendingAdjust;
            if (Math.Abs(pendingStep) > AmountTolerance)
            {
                double before = expected;
                expected = StockStep(state.Pool, expected, pendingStep, postCurve: true,
                    allowNegativeCurrency: allowNegativeCurrency);
                m.PendingStep = expected - before;
            }

            m.Expected = expected;
            double leak = live - expected;
            if (Math.Abs(leak) > epsilon)
            {
                m.Leak = leak;
                m.LeakKept = true;
                m.CumLeakAfter = state.CumLeak + leak;
            }
            return m;
        }

        /// <summary>
        /// Records a finished patch: <paramref name="poolAfter"/> (the pool read back after
        /// the write, or the live pool of a no-op) becomes the last written value, the kept
        /// leak becomes the measurement's, and every row is counted at its amount. A row
        /// keeps what the reference stepped for it (plus this patch's steps); a row new to the
        /// reference that was not stepped counts with nothing stepped. Rows that left the
        /// ledger are dropped.
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
                    next[row.ActionId] = new CountedRow { Amount = row.Amount, Stepped = stepped };
                }
            }

            state.Counted.Clear();
            foreach (var kv in next)
                state.Counted[kv.Key] = kv.Value;
            state.LastWritten = poolAfter;
            state.CumLeak = m.CumLeakAfter;
            state.PendingAdjust = state.Pool == StockPool.Science ? pendingAdjustNow : 0.0;
            state.Initialized = true;
        }

        // ------------------------------------------------------------------
        // Write targets
        // ------------------------------------------------------------------

        /// <summary>
        /// The funds or science value a patch writes: the kept leak added to the target
        /// BEFORE stock's 0 floor (adding it after the floor would create the difference on
        /// every purchase that spends a kept leak). With <c>AllowNegativeCurrency</c> on, the
        /// existing-deficit rule of
        /// <see cref="KspStatePatcher.ResolveNegativeCurrencyPatchTarget(double, double, double, double, bool)"/>
        /// applies to the leak-shifted values. With no leak this is exactly today's target.
        /// </summary>
        internal static double ResolveCurrencyWriteTarget(
            double unflooredTarget, double runningBalanceNow, double currentLive,
            bool allowNegativeCurrency, double cumLeak)
        {
            double shifted = unflooredTarget + cumLeak;
            double floored = shifted > 0.0 ? shifted : 0.0;
            if (!allowNegativeCurrency)
                return floored;
            return KspStatePatcher.ResolveNegativeCurrencyPatchTarget(
                floored, shifted, runningBalanceNow + cumLeak, currentLive, true);
        }

        /// <summary>The reputation a patch writes: the running value plus the kept leak.</summary>
        internal static double ResolveReputationWriteTarget(double runningRep, double cumLeak)
        {
            return runningRep + cumLeak;
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
        /// Only counted rows with a nonzero amount or step are written: a missing row reads
        /// as counted 0.
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
                child.AddValue(CumLeakKey, state.CumLeak.ToString("R", IC));
                if (state.Pool == StockPool.Science)
                    child.AddValue(PendingAdjustKey, state.PendingAdjust.ToString("R", IC));
                var ids = new List<string>(state.Counted.Keys);
                ids.Sort(StringComparer.Ordinal);
                for (int i = 0; i < ids.Count; i++)
                {
                    CountedRow c = state.Counted[ids[i]];
                    if (Math.Abs(c.Amount) <= AmountTolerance && Math.Abs(c.Stepped) <= AmountTolerance)
                        continue;
                    child.AddValue(RowKey,
                        ids[i] + " " + c.Amount.ToString("R", IC) + " " + c.Stepped.ToString("R", IC));
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
                double lastWritten, cumLeak;
                if (!TryParse(child.GetValue(LastWrittenKey), out lastWritten)
                    || !TryParse(child.GetValue(CumLeakKey), out cumLeak))
                {
                    ParsekLog.Warn(Tag,
                        $"Load: {state.Pool} reference unreadable (lastWritten='{child.GetValue(LastWrittenKey) ?? "<none>"}' " +
                        $"cumLeak='{child.GetValue(CumLeakKey) ?? "<none>"}'): starting afresh at the first patch");
                    continue;
                }
                state.LastWritten = lastWritten;
                state.CumLeak = cumLeak;
                double pending;
                if (state.Pool == StockPool.Science && TryParse(child.GetValue(PendingAdjustKey), out pending))
                    state.PendingAdjust = pending;
                string[] values = child.GetValues(RowKey);
                for (int i = 0; i < values.Length; i++)
                {
                    string[] parts = (values[i] ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    double amount, stepped;
                    if (parts.Length != 3 || !TryParse(parts[1], out amount) || !TryParse(parts[2], out stepped))
                    {
                        rowsBad++;
                        continue;
                    }
                    state.Counted[parts[0]] = new CountedRow { Amount = amount, Stepped = stepped };
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

        // ------------------------------------------------------------------
        // Log text
        // ------------------------------------------------------------------

        internal static string Describe(StockPoolReferenceState state)
        {
            if (state == null || !state.Initialized)
                return "unset";
            return "lastWritten=" + state.LastWritten.ToString("R", IC)
                + "/cumLeak=" + state.CumLeak.ToString("R", IC)
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
                + " cumLeak=" + m.CumLeakBefore.ToString("R", IC) + "->" + m.CumLeakAfter.ToString("R", IC)
                + (m.RowsWithoutId > 0 ? " rowsWithoutId=" + m.RowsWithoutId.ToString(IC) : "");
        }
    }
}
