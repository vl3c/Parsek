using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Shared pool policy for the funds / science / reputation patches: the negative-currency target, the drawdown guard and the stock pool reference.
    internal static partial class KspStatePatcher
    {
        /// <summary>
        /// Stock <c>AdvancedParams.AllowNegativeCurrency</c> (Moderate / Hard presets): when
        /// on, <c>Funding.AddFunds</c> / <c>SetFunds</c> and
        /// <c>ResearchAndDevelopment.AddScience</c> / <c>SetScience</c> skip their
        /// <c>Math.Max(0, pool)</c> clamp, so an involuntary debit (a contract failure
        /// penalty) can leave the pool negative. Returns the value locked at career start
        /// (<see cref="CareerSettingsLock"/>) once the career has one, else the live value.
        /// </summary>
        internal static bool ReadAllowNegativeCurrency()
        {
            return CareerSettingsLock.Resolve(ReadLiveAllowNegativeCurrency());
        }

        /// <summary>
        /// The live game parameter, ignoring the career lock (the lock's own enforcement reads
        /// it). Null-guarded: no live game reads false.
        /// </summary>
        internal static bool ReadLiveAllowNegativeCurrency()
        {
            var provider = AllowNegativeCurrencyProviderForTesting;
            if (provider != null)
                return provider();
            try
            {
                var game = HighLogic.CurrentGame;
                if (game == null || game.Parameters == null)
                    return false;
                var advanced = game.Parameters.CustomParams<GameParameters.AdvancedParams>();
                return advanced != null && advanced.AllowNegativeCurrency;
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "allow-negative-currency-read",
                    $"ReadAllowNegativeCurrency: read failed ({ex.GetType().Name}) - treating as off");
                return false;
            }
        }

        /// <summary>
        /// The pool target the patch writes. The module's available value is floored at 0
        /// (the committed future may not be spent past zero). With
        /// <c>AllowNegativeCurrency</c> on, stock itself leaves a pool below zero after an
        /// involuntary debit (a contract failure penalty); the floored target then sits above
        /// that live deficit, the uplift guard reads the gap as a missing spending channel on
        /// every recalc, and an authoritative recalc lifts the deficit to 0.
        ///
        /// <para>With the flag on the target may go below zero, but only by a deficit that
        /// ALREADY EXISTS now: the lower of the ledger's running balance at the current UT
        /// and the live pool, capped at 0, and never below the projected minimum. The
        /// projected minimum alone also covers every FUTURE committed row, so using it would
        /// write a reserved future deficit (a committed spend a superseded reward no longer
        /// covers) into the live pool before any debit happened. Flag off keeps the floored
        /// target (stock clamps at 0 anyway). Pure.</para>
        /// </summary>
        internal static double ResolveNegativeCurrencyPatchTarget(
            double flooredAvailable, double unflooredMinBalance,
            double runningBalanceNow, double currentLive, bool allowNegativeCurrency)
        {
            if (!allowNegativeCurrency) return flooredAvailable;
            if (double.IsNaN(unflooredMinBalance) || double.IsInfinity(unflooredMinBalance))
                return flooredAvailable;
            if (flooredAvailable > 0.0 || unflooredMinBalance >= 0.0) return flooredAvailable;

            double existingDeficit = 0.0;
            if (!double.IsNaN(runningBalanceNow) && runningBalanceNow < existingDeficit)
                existingDeficit = runningBalanceNow;
            if (!double.IsNaN(currentLive) && currentLive < existingDeficit)
                existingDeficit = currentLive;
            return unflooredMinBalance > existingDeficit ? unflooredMinBalance : existingDeficit;
        }

        /// <summary>The funds patch target <see cref="PatchFunds"/> writes (before the drawdown guard).</summary>
        internal static double ResolveFundsPatchTarget(
            FundsModule funds, double currentLive, bool allowNegativeCurrency)
        {
            return ResolveNegativeCurrencyPatchTarget(
                "funds", funds.GetAvailableFunds(), funds.GetProjectionMinBalance(),
                funds.GetRunningBalance(), currentLive, allowNegativeCurrency);
        }

        /// <summary>The science patch target <see cref="PatchScience"/> starts from (before its pending adjusters).</summary>
        internal static double ResolveSciencePatchBaseTarget(
            ScienceModule science, double currentLive, bool allowNegativeCurrency)
        {
            return ResolveNegativeCurrencyPatchTarget(
                "science", science.GetAvailableScience(), science.GetProjectionMinBalance(),
                science.GetRunningScience(), currentLive, allowNegativeCurrency);
        }

        private static double ResolveNegativeCurrencyPatchTarget(
            string resource, double flooredAvailable, double unflooredMinBalance,
            double runningBalanceNow, double currentLive, bool allowNegativeCurrency)
        {
            double target = ResolveNegativeCurrencyPatchTarget(
                flooredAvailable, unflooredMinBalance, runningBalanceNow, currentLive,
                allowNegativeCurrency);
            if (target != flooredAvailable)
            {
                ParsekLog.VerboseRateLimited(Tag, "negative-currency-target|" + resource,
                    $"Patch target for {resource}: AllowNegativeCurrency on, existing deficit " +
                    $"{target.ToString("F1", IC)} kept below zero " +
                    $"(running={runningBalanceNow.ToString("F1", IC)} live={currentLive.ToString("F1", IC)} " +
                    $"projectedMin={unflooredMinBalance.ToString("F1", IC)} " +
                    $"floored available={flooredAvailable.ToString("F1", IC)})");
            }
            return target;
        }

        /// <summary>
        /// Pure threshold check for #402: returns true when a negative delta removes
        /// more than 10% of a non-trivial pool (&gt;1000). Below the pool-floor threshold
        /// the warning is noisy and useless, so we stay silent. Positive deltas and
        /// small percentage drops are always allowed.
        /// </summary>
        internal static bool IsSuspiciousDrawdown(double delta, double currentPool)
        {
            if (delta >= 0) return false;
            if (currentPool <= 1000.0) return false;
            return Math.Abs(delta) > 0.10 * currentPool;
        }

        /// <summary>
        /// Clears the per-resource clamp-toast session latches. Called at a genuine
        /// new-session boundary (a fresh save load) so the first guarded clamp in a new
        /// session toasts again. NOT called on a plain scene change.
        /// </summary>
        internal static void ResetDrawdownGuardSessionLatches()
        {
            fundsClampToastShownThisSession = false;
            scienceClampToastShownThisSession = false;
            reputationClampToastShownThisSession = false;
            fundsUpliftClampToastShownThisSession = false;
            scienceUpliftClampToastShownThisSession = false;
            reputationUpliftClampToastShownThisSession = false;
        }

        /// <summary>
        /// The session toast latch a guarded clamp of <paramref name="resource"/> in
        /// <paramref name="direction"/> reads and sets: the uplift latch for a DOWN clamp,
        /// the drawdown latch otherwise. The three Patch* call sites select through here so
        /// xUnit can reach the same statics <see cref="ResetDrawdownGuardSessionLatches"/>
        /// clears.
        /// </summary>
        internal static ref bool DrawdownGuardSessionToastLatch(string resource, ClampDirection direction)
        {
            bool down = direction == ClampDirection.Down;
            switch (resource)
            {
                case "Funds":
                    if (down) return ref fundsUpliftClampToastShownThisSession;
                    return ref fundsClampToastShownThisSession;
                case "Science":
                    if (down) return ref scienceUpliftClampToastShownThisSession;
                    return ref scienceClampToastShownThisSession;
                case "Reputation":
                    if (down) return ref reputationUpliftClampToastShownThisSession;
                    return ref reputationClampToastShownThisSession;
                default:
                    throw new ArgumentException("unknown drawdown guard resource: " + resource, nameof(resource));
            }
        }

        /// <summary>
        /// Direction a guarded clamp moved the patch target (plan §2.2 step 2).
        /// <list type="bullet">
        /// <item><see cref="None"/>: no clamp fired.</item>
        /// <item><see cref="Up"/>: running BELOW live (a missing earning channel) -> target
        /// floored UP to live ("keep what you earned").</item>
        /// <item><see cref="Down"/>: running ABOVE live (a spend the ledger does not model,
        /// e.g. a facility upgrade — Bug 2) -> target capped DOWN to live ("hold at the
        /// spent value"), so a non-authoritative recalc cannot refund the spend.</item>
        /// </list>
        /// </summary>
        internal enum ClampDirection { None, Up, Down }

        /// <summary>
        /// Pure predicate (Blocker 2, plan §3.3): a drawdown is guardable iff the
        /// NON-RESERVED running balance is more than <paramref name="epsilon"/> below the
        /// current live value. Keyed on the running balance, NOT the reservation-aware
        /// patch target: a legitimate reservation lowers only the spendable target while
        /// leaving the running balance intact (>= live), so it does not trip the guard; a
        /// missing-earning-channel leak lowers the running balance itself, so it does.
        /// </summary>
        internal static bool IsGuardableDrawdown(
            double runningBalance, double currentLive, double epsilon)
        {
            return runningBalance < currentLive - epsilon;
        }

        /// <summary>
        /// Pure predicate (Bug 2, plan §2.1): an UPLIFT is guardable iff the running
        /// balance is more than <paramref name="epsilon"/> ABOVE the current live value.
        /// Exact mirror of <see cref="IsGuardableDrawdown"/> on the SAME running-balance
        /// discriminator, NOT the target: a legitimate reservation lowers only the
        /// spendable target while leaving the running balance at/above live, so it never
        /// trips this (running is within eps of live for a pure load); a real spend the
        /// ledger does not model (a facility upgrade, the Bug-2 signature) leaves the
        /// running balance ABOVE live, so it does. When this fires with no time-travel
        /// authority the target is capped DOWN to live so the patch cannot refund the spend.
        /// </summary>
        internal static bool IsGuardableUplift(
            double runningBalance, double currentLive, double epsilon)
        {
            return runningBalance > currentLive + epsilon;
        }

        /// <summary>
        /// One clock-advance patch that reached committed-future rows
        /// (<see cref="LedgerOrchestrator.RecalculateAndPatchForClockAdvance"/>).
        /// </summary>
        internal sealed class ClockCrossing
        {
            internal int CrossedRows;
            /// <summary>Kerbals named by the reached committed-future KerbalHire rows.</summary>
            internal readonly List<string> CrossedHireKerbals = new List<string>();
            /// <summary>Set by the funds patch when it reached its write decision, so the
            /// reached rows' funds effect is in the live pool. A skipped funds patch leaves it
            /// false.</summary>
            internal bool FundsApplied;
            /// <summary>Set by the funds patch when the game has no funds pool (sandbox,
            /// science mode): nothing is charged, so nothing waits for a charge.</summary>
            internal bool NoFundsPool;
        }

        /// <summary>
        /// Measures one pool patch against its stock pool reference: builds the pool's rows,
        /// resolves the clock-crossing authorization (the live pool still holds exactly what
        /// the reference last wrote) and runs <see cref="StockPoolReference.Measure"/>.
        /// Logs a fresh baseline at Info and every measurement once per change.
        /// </summary>
        private static StockPoolMeasurement MeasureStockPool(
            StockPool pool, string resource, IStockPoolModule module, double live, double pendingAdjustNow,
            bool authoritativeReduction, double epsilon,
            double freshBaselineTarget, out List<StockPoolRow> rows)
        {
            IReadOnlyList<GameAction> actions = stockReferenceActions ?? EffectiveState.ComputeELS();
            double nowUT = !double.IsNaN(stockReferenceNowUT)
                ? stockReferenceNowUT
                : CommittedFutureIndexCache.CurrentUT();
            rows = StockPoolReference.BuildRows(pool, actions, module?.GetRowEffects(),
                module?.GetTakeBackDeltas());

            StockPoolReferenceState state = StockPoolReference.For(pool);
            bool clockCrossingApplies = ResolveClockCrossingAuthorization(
                resource, live, state.Initialized ? state.LastWritten : double.NaN, false, epsilon);
            bool authoritative = authoritativeReduction || clockCrossingApplies;

            // A counted row missing from the effective rows is stepped back only when it is gone
            // from the ledger itself; one that is only outside the effective ledger
            // (tombstoned, superseded, session-suppressed) is dropped without a carry.
            ICollection<string> idsInLedger = StockPoolReference.HasCountedRowsMissingFrom(state, rows)
                ? LedgerOrchestrator.CollectLedgerActionIds()
                : null;
            // Stock's 0 clamp (funds, science; off under AllowNegativeCurrency).
            bool clampAtZero = pool != StockPool.Reputation && !ReadAllowNegativeCurrency();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, nowUT, live, pendingAdjustNow,
                authoritative, epsilon, freshBaselineTarget, idsInLedger, clampAtZero);
            if (m.Adjustment != 0.0)
                m.AdjustmentRow = FileStockPoolAdjustment(pool, resource, m, nowUT);
            ApplyCarryDedupe(resource, m);
            EmitTakeBacks(pool, m, actions);

            string summary = StockPoolReference.FormatMeasurement(m);
            if (StockPoolReference.IsTakeBackFoundUnpaid(m, epsilon))
                ParsekLog.Warn(StockShadowTakeBack.Tag,
                    $"Patch{resource}: take-back found no stock payment: took back " +
                    $"{m.TakenBackTotal.ToString("R", IC)} and the live pool is short by the same amount " +
                    $"(leak={m.Leak.ToString("R", IC)}), so the leak row files it a second time: {summary}");
            if (m.FreshBaseline)
            {
                string adoption = m.GapAdopted
                    ? "the gap to the target filed as an adjustment row, pool left as it is (gap="
                      + m.AdoptedGap.ToString("R", IC) + " target="
                      + freshBaselineTarget.ToString("R", IC) + ")"
                    : "authoritative, the target is written";
                ParsekLog.Info(StockPoolReference.Tag,
                    $"Patch{resource}: no saved reference - baseline from the live pool with every row " +
                    $"counted, {adoption} (rows={rows.Count.ToString(IC)} now={nowUT.ToString("R", IC)}): {summary}");
            }
            else if (m.RemovedRows > 0 || m.LeakKept || m.Adjustment != 0.0 || m.ClampForgiven != 0.0)
            {
                ParsekLog.Info(StockPoolReference.Tag,
                    $"Patch{resource}: {summary} now={nowUT.ToString("R", IC)} " +
                    $"authoritative={authoritativeReduction} clockCrossing={clockCrossingApplies}");
            }
            else
            {
                ParsekLog.VerboseOnChange(StockPoolReference.Tag, "measure|" + resource, summary,
                    $"Patch{resource}: {summary} now={nowUT.ToString("R", IC)} " +
                    $"authoritative={authoritativeReduction} clockCrossing={clockCrossingApplies}");
            }
            return m;
        }

        /// <summary>
        /// One Warn per row whose duplicate stock payment this patch takes back (design 3.15
        /// step 5), naming the identity and the row that already credited it. Nothing is filed:
        /// the write, the walk's target, is the take-back. Also one Warn per take-back the
        /// reference refused because it already took that row back once.
        /// </summary>
        internal static void EmitTakeBacks(StockPool pool, StockPoolMeasurement m, IReadOnlyList<GameAction> actions)
        {
            if (m == null || (m.TakenBack.Count == 0 && m.TakeBackRepeatsRefused.Count == 0))
                return;
            var byId = new Dictionary<string, GameAction>(StringComparer.Ordinal);
            if (actions != null)
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i] != null && !string.IsNullOrEmpty(actions[i].ActionId))
                        byId[actions[i].ActionId] = actions[i];
            for (int i = 0; i < m.TakenBack.Count; i++)
            {
                GameAction row;
                byId.TryGetValue(m.TakenBack[i].Key, out row);
                ParsekLog.Warn(StockShadowTakeBack.Tag,
                    StockShadowTakeBack.FormatTakeBack(pool, m.TakenBack[i].Value, row,
                        m.TakenBackByAuthoritativeWrite));
            }
            StockPoolReferenceState state = StockPoolReference.For(pool);
            for (int i = 0; i < m.TakeBackRepeatsRefused.Count; i++)
            {
                GameAction row;
                byId.TryGetValue(m.TakeBackRepeatsRefused[i].Key, out row);
                double already;
                state.TakenBack.TryGetValue(m.TakeBackRepeatsRefused[i].Key, out already);
                ParsekLog.Warn(StockShadowTakeBack.Tag,
                    StockShadowTakeBack.FormatTakeBackRefused(pool, m.TakeBackRepeatsRefused[i].Value, already, row));
            }
        }

        /// <summary>
        /// Pure + log: the part of this patch's take-back the write could not collect because
        /// the walk's running value is below 0 (the duplicate was spent before the update,
        /// possible only with auto-merge off). Funds: the walk keeps the running value below 0
        /// and the 0-floored write leaves the rest to be repaid by later gains first, the debt
        /// of design 3.13 part 4. Reputation: no 0 clamp in stock, so it is applied in full.
        /// Science: the same debt (<see cref="NoteScienceTakeBackShortfall"/>, design 3.15 step 6).
        /// </summary>
        internal static double EmitTakeBackShortfall(string resource, StockPoolMeasurement m, double runningAfter)
        {
            if (m == null || m.TakenBack.Count == 0)
                return 0.0;
            double shortfall = StockShadowTakeBack.ResolveShortfall(m.TakenBackTotal, runningAfter);
            if (shortfall <= 0.0)
                return 0.0;
            ParsekLog.Warn(StockShadowTakeBack.Tag,
                $"Patch{resource}: take-back shortfall {shortfall.ToString("R", IC)} of " +
                $"{m.TakenBackTotal.ToString("R", IC)}: the duplicate payment was spent before this update; " +
                $"running={runningAfter.ToString("R", IC)} stays below 0 and later gains repay it first " +
                "(a debt, design 3.13 part 4)");
            return shortfall;
        }

        /// <summary>
        /// The science side of a take-back shortfall (design 3.15 step 5, wired to step 6's
        /// science debt): a duplicate the player spent on a tech before the update leaves the
        /// walk's running science below 0, because the tech is charged into a science debt
        /// rather than left researched for free. The 0-floored write leaves that debt to be
        /// repaid by later gains first, exactly as funds (<see cref="EmitTakeBackShortfall"/>).
        /// </summary>
        internal static double NoteScienceTakeBackShortfall(StockPoolMeasurement m, double runningAfter)
        {
            return EmitTakeBackShortfall("Science", m, runningAfter);
        }

        /// <summary>
        /// Files a patch's adjustment as a <see cref="GameActionType.StockPoolAdjustment"/> row
        /// (untagged, at the patch's now, stock-applied: it is something stock applied to the
        /// pool that no other row holds). <see cref="StockPoolReference.Commit"/> counts it at
        /// creation, so the next walk's dispatch of it steps nothing.
        /// </summary>
        private static GameAction FileStockPoolAdjustment(
            StockPool pool, string resource, StockPoolMeasurement m, double nowUT)
        {
            string reason = m.GapAdopted ? "adopted-gap" : m.LeakKept ? "leak" : "carried-removed";
            GameAction row = StockPoolReference.BuildAdjustmentRow(pool, m.Adjustment, nowUT,
                m.CarriedRemoved != 0.0 ? m.CarriedSources : null);
            var sink = StockPoolAdjustmentSinkForTesting;
            if (sink != null)
                sink(row);
            else
                Ledger.AddAction(row, LedgerRowWriter.StockApplied);
            ParsekLog.Info(StockPoolReference.Tag,
                $"Patch{resource}: filed StockPoolAdjustment amount={m.Adjustment.ToString("R", IC)} " +
                $"reason={reason} ut={nowUT.ToString("R", IC)} id={row.ActionId}" +
                (row.AdjustmentCarriedFrom != null
                    ? " carriedFrom=[" + string.Join(",", row.AdjustmentCarriedFrom.ToArray()) + "]"
                    : "") +
                (sink != null ? " (test sink)" : ""));
            return row;
        }

        /// <summary>
        /// The ledger side of <see cref="StockPoolReference.ResolveRemovedCarries"/>: removes the
        /// older carry rows this patch's carry replaces (under the test sink nothing leaves the
        /// live ledger), and logs the removed rows not carried again and the older carry rows
        /// kept because they also name something this patch does not carry.
        /// </summary>
        private static void ApplyCarryDedupe(string resource, StockPoolMeasurement m)
        {
            if (m == null)
                return;
            for (int i = 0; i < m.CarryAlreadyHeld.Count; i++)
                ParsekLog.Info(StockPoolReference.Tag,
                    $"Patch{resource}: removed row {m.CarryAlreadyHeld[i].Key} not carried again: an adjustment row " +
                    $"the walk dispatched already carries its stepped part ({m.CarryAlreadyHeld[i].Value.ToString("R", IC)})");
            for (int i = 0; i < m.CarriesOverlapKept.Count; i++)
                ParsekLog.Warn(StockPoolReference.Tag,
                    $"Patch{resource}: older carry row {m.CarriesOverlapKept[i]} names a row carried again and was kept " +
                    "(it also names a row this patch does not carry, or the reference counts it): a full walk may " +
                    "count that row's carry twice");
            if (m.CarriesRetired.Count == 0)
                return;
            var retire = new HashSet<string>(m.CarriesRetired, StringComparer.Ordinal);
            if (StockPoolAdjustmentSinkForTesting != null)
            {
                ParsekLog.Info(StockPoolReference.Tag,
                    $"Patch{resource}: would retire {retire.Count.ToString(IC)} older carry row(s) " +
                    $"[{string.Join(",", m.CarriesRetired.ToArray())}] (test sink: ledger left as it is)");
                return;
            }
            List<GameAction> removed = Ledger.RemoveWhere(
                a => a != null && a.Type == GameActionType.StockPoolAdjustment
                    && !string.IsNullOrEmpty(a.ActionId) && retire.Contains(a.ActionId),
                "stock-pool-carry-dedupe");
            ParsekLog.Info(StockPoolReference.Tag,
                $"Patch{resource}: retired {removed.Count.ToString(IC)} older carry row(s) " +
                $"[{string.Join(",", m.CarriesRetired.ToArray())}] replaced by this patch's carry " +
                $"{(m.AdjustmentRow != null ? m.AdjustmentRow.ActionId : "(none)")}: each removed row is carried once");
        }

        /// <summary>
        /// Records a finished pool patch in its reference: the pool read back after the
        /// write (or the live pool of a no-op) becomes the last written value.
        /// </summary>
        private static void CommitStockPool(
            StockPool pool, List<StockPoolRow> rows, StockPoolMeasurement m, double poolAfter,
            double pendingAdjustNow)
        {
            StockPoolReference.Commit(StockPoolReference.For(pool), rows, m, poolAfter, pendingAdjustNow);
            ParsekLog.VerboseOnChange(StockPoolReference.Tag, "commit|" + pool,
                StockPoolReference.Describe(StockPoolReference.For(pool)),
                $"{pool}: reference now {StockPoolReference.Describe(StockPoolReference.For(pool))}");
        }

        /// <summary>
        /// The observability of a kept leak (a measured patch that found the live pool off
        /// what stock applied for the ledger's rows): the existing guarded-clamp WARN and its
        /// one-per-session toast, Up for live above the expectation (an earning the ledger
        /// does not model, kept) and Down for live below it (a spend it does not model, held).
        /// Nothing on a patch that measured no leak.
        /// </summary>
        private static void EmitStockPoolLeak(
            string resource, StockPoolMeasurement m, double runningBalance,
            double targetWithoutLeak, double writtenTarget, string toastUp, string toastDown,
            bool perSubjectScienceNote)
        {
            if (m == null || !m.LeakKept)
                return;
            ClampDirection direction = m.Leak > 0.0 ? ClampDirection.Up : ClampDirection.Down;
            ref bool latch = ref DrawdownGuardSessionToastLatch(resource, direction);
            EmitDrawdownGuardClamp(
                resource, runningBalance, m.Live,
                wouldBeTarget: targetWithoutLeak, clampedTo: writtenTarget,
                toastText: direction == ClampDirection.Down ? toastDown : toastUp,
                sessionToastLatch: ref latch,
                perSubjectScienceNote: perSubjectScienceNote,
                direction: direction);
        }

        /// <summary>
        /// Pure: may a clock-advance patch move a pool to the walk's target past the
        /// drawdown guard? Only when the live pool still holds exactly what the last patch
        /// left there, and that patch did not clamp. Then nothing outside the ledger has
        /// touched the pool since, and the whole change is the reached committed-future rows
        /// (their costs, and the reservations they release). Anything else - a pool moved
        /// by stock since, a standing clamp, an unknown last patch - keeps the guard.
        /// </summary>
        internal static bool IsLiveUnchangedSinceLastPatch(
            double currentLive, double lastPatchedLive, bool lastPatchClamped, double epsilon)
        {
            if (lastPatchClamped) return false;
            if (double.IsNaN(lastPatchedLive) || double.IsInfinity(lastPatchedLive)) return false;
            if (double.IsNaN(currentLive) || double.IsInfinity(currentLive)) return false;
            return Math.Abs(currentLive - lastPatchedLive) <= epsilon;
        }

        private static bool ResolveClockCrossingAuthorization(
            string resource, double currentLive, double lastPatchedLive, bool lastPatchClamped,
            double epsilon)
        {
            if (ActiveClockCrossing == null)
                return false;
            bool applies = IsLiveUnchangedSinceLastPatch(
                currentLive, lastPatchedLive, lastPatchClamped, epsilon);
            ParsekLog.Info(Tag,
                $"Patch{resource}: clock-crossing {(applies ? "applies the reached rows" : "keeps the guard")}: " +
                $"live={currentLive.ToString("R", IC)} lastPatched={lastPatchedLive.ToString("R", IC)} " +
                $"lastPatchClamped={lastPatchClamped}");
            return applies;
        }

        /// <summary>
        /// Pure clamp decision (1-out overload preserved for the existing call sites /
        /// DrawdownGuardTests). Delegates to the 2-out <see cref="ApplyDrawdownGuard(double,
        /// double, double, double, bool, string, out bool, out ClampDirection)"/> overload
        /// and discards the direction.
        /// </summary>
        internal static double ApplyDrawdownGuard(
            double patchTarget, double runningBalance, double currentLive, double epsilon,
            bool authoritativeReduction, string resource, out bool clamped)
        {
            return ApplyDrawdownGuard(
                patchTarget, runningBalance, currentLive, epsilon,
                authoritativeReduction, resource, out clamped, out _);
        }

        /// <summary>
        /// Pure symmetric clamp decision (plan §2.2 step 2). When no time-travel context
        /// authorizes the change the guard keys on the RUNNING balance vs live in BOTH
        /// directions:
        /// <list type="bullet">
        /// <item>running BELOW live past epsilon -> floor the target UP to live (returns
        /// <c>max(patchTarget, currentLive)</c>), <paramref name="direction"/>=<see
        /// cref="ClampDirection.Up"/>. UP semantics are UNCHANGED from the original guard,
        /// including flagging <paramref name="clamped"/> even when <c>patchTarget &gt;
        /// live</c> (so the existing TargetAboveLive assertion still holds).</item>
        /// <item>running ABOVE live past epsilon -> cap the target DOWN to live (returns
        /// <c>min(patchTarget, currentLive)</c>), <paramref name="direction"/>=<see
        /// cref="ClampDirection.Down"/>. The DOWN branch flags <paramref name="clamped"/>
        /// ONLY when the clamp actually LOWERED the value (<c>min &lt; patchTarget</c>),
        /// so a target already at/below live (a partial reservation on top of a leak) does
        /// not emit a misleading WARN/toast (plan §2.4 last bullet).</item>
        /// <item>otherwise (running within epsilon of live in either direction, OR an
        /// authoritative signal is set) -> return <paramref name="patchTarget"/> unchanged,
        /// <paramref name="direction"/>=<see cref="ClampDirection.None"/>. Covers the
        /// legitimate reservation case (target below live while running is at live) and
        /// every authoritative time-travel restore (which moves funds freely either way).</item>
        /// </list>
        /// </summary>
        internal static double ApplyDrawdownGuard(
            double patchTarget, double runningBalance, double currentLive, double epsilon,
            bool authoritativeReduction, string resource, out bool clamped,
            out ClampDirection direction)
        {
            if (!authoritativeReduction)
            {
                if (IsGuardableDrawdown(runningBalance, currentLive, epsilon))
                {
                    clamped = true;
                    direction = ClampDirection.Up;
                    return patchTarget > currentLive ? patchTarget : currentLive;
                }

                if (IsGuardableUplift(runningBalance, currentLive, epsilon))
                {
                    // Cap DOWN to live. Flag clamped only when the value actually moved:
                    // a target already at/below live is not refunding the spend, so a
                    // no-op clamp must not emit a misleading WARN/toast (plan §2.4).
                    double capped = patchTarget < currentLive ? patchTarget : currentLive;
                    clamped = capped < patchTarget;
                    direction = clamped ? ClampDirection.Down : ClampDirection.None;
                    return capped;
                }
            }

            clamped = false;
            direction = ClampDirection.None;
            return patchTarget;
        }

        /// <summary>
        /// Emits the loud observability for a guarded clamp (plan §4.2 steps 2-3 / §2.2
        /// step 3): a WARN with the full numbers ALWAYS (every guarded recalc, documenting
        /// the ongoing leak), and one short session-latched native ScreenMessage naming the
        /// protected resource. The WARN distinguishes the two directions:
        /// <list type="bullet">
        /// <item><see cref="ClampDirection.Up"/> -> "GUARDED DRAWDOWN clamped" (running below
        /// live: a missing earning channel; target floored up to live).</item>
        /// <item><see cref="ClampDirection.Down"/> -> "GUARDED UPLIFT clamped" (running above
        /// live: a spend the ledger does not model, e.g. a facility upgrade; target capped
        /// down to live so the patch cannot refund it — Bug 2).</item>
        /// </list>
        /// <paramref name="perSubjectScienceNote"/> appends the documented per-subject
        /// divergence limitation to the WARN for the science pool (MINOR 5). Internal static
        /// so xUnit can drive the WARN + toast path directly (the live patch methods
        /// early-return on null KSP singletons).
        /// </summary>
        internal static void EmitDrawdownGuardClamp(
            string resource, double runningBalance, double currentLive,
            double wouldBeTarget, double clampedTo, string toastText,
            ref bool sessionToastLatch, bool perSubjectScienceNote,
            ClampDirection direction = ClampDirection.Up)
        {
            string label = direction == ClampDirection.Down
                ? "GUARDED UPLIFT clamped"
                : "GUARDED DRAWDOWN clamped";
            string tail = direction == ClampDirection.Down
                ? "(no time-travel context) - spent value held; ledger may be missing a spending channel"
                : "(no time-travel context) - earned value preserved; ledger may be missing an earning channel";
            string warn =
                $"Patch{resource}: {label} resource={resource} " +
                $"running={runningBalance.ToString("R", IC)} live={currentLive.ToString("R", IC)} " +
                $"wouldBeTarget={wouldBeTarget.ToString("R", IC)} clampedTo={clampedTo.ToString("R", IC)} " +
                tail;
            if (perSubjectScienceNote)
            {
                warn += " NOTE: per-subject credited science is patched UNCLAMPED, so the " +
                    "Science Archive may transiently disagree with the clamped pool until the leak is fixed";
            }
            ParsekLog.Warn(Tag, warn);

            if (!sessionToastLatch)
            {
                sessionToastLatch = true;
                ParsekLog.ScreenMessage(toastText, 2.5f);
            }
        }
    }
}
