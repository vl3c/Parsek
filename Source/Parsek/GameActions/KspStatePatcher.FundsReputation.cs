using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Funds and reputation pool patches.
    internal static partial class KspStatePatcher
    {
        /// <summary>
        /// Pure decision core for <see cref="PatchFunds"/> (apply-boundary test seam,
        /// audit rec #5). Takes the live funds pool as a PARAMETER instead of reading
        /// <c>Funding.Instance</c>, so the compute-delta -> drawdown-clamp -> no-op /
        /// suspicious-drawdown composition is unit-testable headlessly. The live wrapper
        /// performs the side effects (clamp WARN+toast, AddFunds, logs) from the returned
        /// flags. No KSP singletons touched.
        /// </summary>
        internal readonly struct FundsPatchDecision
        {
            /// <summary>True when |delta| >= the 0.01 no-op threshold (a write is needed).</summary>
            internal readonly bool ShouldWrite;
            /// <summary>The delta passed to AddFunds (effectiveTarget - currentFunds).</summary>
            internal readonly double Delta;
            /// <summary>The post-clamp target (the value the pool reaches).</summary>
            internal readonly double EffectiveTarget;
            /// <summary>True when the symmetric drawdown guard clamped the target (up to or down to live).</summary>
            internal readonly bool Clamped;
            /// <summary>Which direction the guard clamped (Up = kept earnings, Down = held the spent value, None = no clamp).</summary>
            internal readonly ClampDirection Direction;
            /// <summary>True when a needed write is a >10% drawdown of a non-trivial pool.</summary>
            internal readonly bool SuspiciousDrawdown;

            internal FundsPatchDecision(
                bool shouldWrite, double delta, double effectiveTarget,
                bool clamped, ClampDirection direction, bool suspiciousDrawdown)
            {
                ShouldWrite = shouldWrite;
                Delta = delta;
                EffectiveTarget = effectiveTarget;
                Clamped = clamped;
                Direction = direction;
                SuspiciousDrawdown = suspiciousDrawdown;
            }
        }

        /// <summary>
        /// Resolves the funds patch outcome from the live pool + the reservation-aware
        /// available target + the non-reserved running balance. Mirrors
        /// <see cref="PatchFunds"/>'s arithmetic exactly: drawdown-guard the target (keyed on
        /// the running balance), derive the delta against the post-clamp target, decide no-op
        /// vs write on the 0.01 threshold, and flag a suspicious >10% drawdown only when a
        /// write is actually needed. Pure: no singletons.
        /// </summary>
        internal static FundsPatchDecision ResolveFundsPatch(
            double currentFunds, double targetFunds, double runningFunds,
            bool authoritativeReduction)
        {
            double effTarget = ApplyDrawdownGuard(
                targetFunds, runningFunds, currentFunds, 0.01,
                authoritativeReduction, "funds", out bool clamped, out ClampDirection direction);
            double delta = effTarget - currentFunds;
            bool shouldWrite = Math.Abs(delta) >= 0.01;
            bool suspicious = shouldWrite && IsSuspiciousDrawdown(delta, currentFunds);
            return new FundsPatchDecision(shouldWrite, delta, effTarget, clamped, direction, suspicious);
        }

        /// <summary>
        /// Patches KSP's fund balance to match the module's available funds.
        /// Uses AddFunds with a computed delta to reach the target value.
        /// No-op if Funding.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchFunds(
            FundsModule funds,
            bool suppressSuspiciousDrawdownWarnings = false,
            bool authoritativeReduction = false)
        {
            if (funds == null)
            {
                ParsekLog.Warn(Tag, "PatchFunds: null FundsModule — skipping");
                return;
            }

            if (Funding.Instance == null)
            {
                var noPoolCrossing = ActiveClockCrossing;
                if (noPoolCrossing != null)
                    noPoolCrossing.NoFundsPool = true;
                VerboseStablePatchState("patch-skip|funds|funding", "funding-null",
                    "PatchFunds: Funding.Instance is null (sandbox mode) — skipping");
                return;
            }

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
            if (!funds.HasSeed)
            {
                VerboseStablePatchState("patch-skip|funds|seed", "missing-seed",
                    "PatchFunds: module has no FundsInitial seed — skipping to preserve KSP values");
                return;
            }

            // See PatchScience: a pool whose module has not loaded is not measured.
            if (!CurrencyScenarioReadiness.IsScenarioModuleLoaded(Funding.Instance))
            {
                VerboseStablePatchState("patch-skip|funds|not-loaded", "not-loaded",
                    "PatchFunds: Funding has not finished loading - skipping " +
                    "(the stock pool reference measures only a loaded pool)");
                return;
            }

            double currentFunds = Funding.Instance.Funds;
            bool allowNegativeCurrency = ReadAllowNegativeCurrency();
            double runningFunds = funds.GetRunningBalance();

            // The drawdown guard reads the stock pool reference (committed-value rule plan
            // section 2.4): anything stock applied that the ledger holds no row for is filed
            // as a StockPoolAdjustment row and added to this write BEFORE the 0 floor (later
            // walks carry the row), so a reservation that releases comes back and an
            // untracked earning or spend stays as stock left it.
            StockPoolMeasurement measurement = MeasureStockPool(
                StockPool.Funds, "Funds", funds, currentFunds, 0.0,
                authoritativeReduction, 0.01,
                funds.GetProjectionMinBalance(), out List<StockPoolRow> referenceRows);
            double targetWithoutLeak = StockPoolReference.ResolveCurrencyWriteTarget(
                funds.GetProjectionMinBalance(), runningFunds, currentFunds, allowNegativeCurrency, 0.0);
            double targetFunds = StockPoolReference.ResolveCurrencyWriteTarget(
                funds.GetProjectionMinBalance(), runningFunds, currentFunds, allowNegativeCurrency,
                measurement.Adjustment);
            if (measurement.GapAdopted)
                targetFunds = currentFunds;

            FundsPatchDecision decision = ResolveFundsPatch(
                currentFunds, targetFunds, runningFunds, authoritativeReduction: true);
            var crossing = ActiveClockCrossing;
            if (crossing != null)
                crossing.FundsApplied = true;
            EmitStockPoolLeak("Funds", measurement, runningFunds, targetWithoutLeak,
                decision.EffectiveTarget, "Kept your earned funds",
                "Held your funds at the spent value", perSubjectScienceNote: false);
            EmitTakeBackShortfall("Funds", measurement, runningFunds);

            if (!decision.ShouldWrite)
            {
                CommitStockPool(StockPool.Funds, referenceRows, measurement, currentFunds, 0.0);
                VerboseStablePatchState("patch-noop|funds", decision.EffectiveTarget.ToString("F1", IC),
                    $"PatchFunds: no change needed (current={currentFunds.ToString("F1", IC)}, " +
                    $"target={decision.EffectiveTarget.ToString("F1", IC)})");
                return;
            }

            // #402: defensive WARN when a single recalculation drops more than 10% of
            // the live funds pool. Legitimate walks can subtract large amounts after a
            // revert, so this is log-only (never aborts the patch) — but a >10% drop
            // alongside a small pool (>1000F) is the shape of missing-earnings bugs.
            if (!suppressSuspiciousDrawdownWarnings && decision.SuspiciousDrawdown)
            {
                ParsekLog.Warn(Tag,
                    $"PatchFunds: suspicious drawdown delta={decision.Delta.ToString("F1", IC)} " +
                    $"from current={currentFunds.ToString("F1", IC)} " +
                    $"(>10% of pool, target={decision.EffectiveTarget.ToString("F1", IC)}) — " +
                    $"earning channel may be missing. HasSeed={funds.HasSeed}");
            }

            Funding.Instance.AddFunds(decision.Delta, TransactionReasons.None);

            double afterFunds = Funding.Instance.Funds;
            CommitStockPool(StockPool.Funds, referenceRows, measurement, afterFunds, 0.0);
            ParsekLog.Info(Tag,
                $"PatchFunds: {currentFunds.ToString("F1", IC)} -> {afterFunds.ToString("F1", IC)} " +
                $"(delta={decision.Delta.ToString("F1", IC)}, target={decision.EffectiveTarget.ToString("F1", IC)})");

            // LedgerTrace Tier-C: reconcile the computed target against the live
            // read-back we just took (afterFunds). Reuses afterFunds; no second read.
            if (LedgerTrace.IsEnabled)
            {
                EmitPoolReadbackAnomaly("funds", decision.EffectiveTarget, afterFunds);
            }
        }

        /// <summary>
        /// Pure decision core for <see cref="PatchReputation"/> (apply-boundary test seam,
        /// audit rec #5). Takes the live reputation as a PARAMETER instead of reading
        /// <c>Reputation.Instance</c>. Reputation uses SetReputation (absolute, no delta) and
        /// has NO reservation system, so its running balance IS the target; the only decision
        /// is the symmetric drawdown-guard clamp + the 0.01 no-op gate. The live wrapper
        /// performs the side effects (clamp WARN+toast, SetReputation, logs) from the returned
        /// flags. Pure: no KSP singletons touched.
        /// </summary>
        internal readonly struct ReputationPatchDecision
        {
            /// <summary>True when |effectiveTarget - currentRep| >= the 0.01f no-op threshold.</summary>
            internal readonly bool ShouldWrite;
            /// <summary>True when the symmetric drawdown guard clamped the target (up to or down to live).</summary>
            internal readonly bool Clamped;
            /// <summary>Which direction the guard clamped (Up = kept earnings, Down = held the current value, None = no clamp).</summary>
            internal readonly ClampDirection Direction;
            /// <summary>
            /// The raw post-clamp target as the <see cref="ApplyDrawdownGuard"/> double, used
            /// ONLY for the clamp WARN's <c>clampedTo</c> so its formatted output is identical
            /// to the pre-refactor wrapper (a float round-trip would change the "R" string).
            /// </summary>
            internal readonly double EffectiveTargetRaw;
            /// <summary>The post-clamp target as the float value written by SetReputation and logged.</summary>
            internal readonly float EffectiveTarget;

            internal ReputationPatchDecision(
                bool shouldWrite, bool clamped, ClampDirection direction,
                double effectiveTargetRaw, float effectiveTarget)
            {
                ShouldWrite = shouldWrite;
                Clamped = clamped;
                Direction = direction;
                EffectiveTargetRaw = effectiveTargetRaw;
                EffectiveTarget = effectiveTarget;
            }
        }

        /// <summary>
        /// Resolves the reputation patch outcome from the live reputation + the module's
        /// running reputation. Mirrors <see cref="PatchReputation"/> exactly: drawdown-guard
        /// the target (running == target, so "downward" is by magnitude, negative rep handled),
        /// cast the clamped target back to float, and decide no-op vs write on the 0.01f
        /// threshold. Pure: no singletons.
        /// </summary>
        internal static ReputationPatchDecision ResolveReputationPatch(
            float currentRep, float targetRep, bool authoritativeReduction)
        {
            double effTargetRaw = ApplyDrawdownGuard(
                targetRep, targetRep, currentRep, 0.01,
                authoritativeReduction, "reputation", out bool clamped, out ClampDirection direction);
            float effTarget = (float)effTargetRaw;
            bool shouldWrite = Math.Abs(effTarget - currentRep) >= 0.01f;
            return new ReputationPatchDecision(shouldWrite, clamped, direction, effTargetRaw, effTarget);
        }

        /// <summary>
        /// Patches KSP's reputation to match the module's running reputation.
        /// Uses SetReputation (NOT AddReputation) to avoid double curve application —
        /// the ReputationModule already applied the curve during the walk.
        /// No-op if Reputation.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchReputation(
            ReputationModule reputation,
            bool authoritativeReduction = false)
        {
            if (reputation == null)
            {
                ParsekLog.Warn(Tag, "PatchReputation: null ReputationModule — skipping");
                return;
            }

            if (Reputation.Instance == null)
            {
                VerboseStablePatchState("patch-skip|reputation|singleton", "reputation-null",
                    "PatchReputation: Reputation.Instance is null (sandbox mode) — skipping");
                return;
            }

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
            if (!reputation.HasSeed)
            {
                VerboseStablePatchState("patch-skip|reputation|seed", "missing-seed",
                    "PatchReputation: module has no ReputationInitial seed — skipping to preserve KSP values");
                return;
            }

            // See PatchScience: a pool whose module has not loaded is not measured.
            if (!CurrencyScenarioReadiness.IsScenarioModuleLoaded(Reputation.Instance))
            {
                VerboseStablePatchState("patch-skip|reputation|not-loaded", "not-loaded",
                    "PatchReputation: Reputation has not finished loading - skipping " +
                    "(the stock pool reference measures only a loaded pool)");
                return;
            }

            float currentRep = Reputation.Instance.reputation;
            float runningRep = reputation.GetRunningRep();

            // Reputation has no reservation: the target is the running value plus this
            // patch's adjustment. The reference steps each row by its effect in the walk,
            // whose curve runs at the pool's own value (adjustment rows included).
            StockPoolMeasurement measurement = MeasureStockPool(
                StockPool.Reputation, "Reputation", reputation, currentRep, 0.0,
                authoritativeReduction, 0.01,
                runningRep, out List<StockPoolRow> referenceRows);
            float targetRep = measurement.GapAdopted
                ? currentRep
                : (float)StockPoolReference.ResolveReputationWriteTarget(runningRep, measurement.Adjustment);
            ReputationPatchDecision decision = ResolveReputationPatch(
                currentRep, targetRep, authoritativeReduction: true);
            EmitStockPoolLeak("Reputation", measurement, runningRep, runningRep,
                decision.EffectiveTargetRaw, "Kept your earned reputation",
                "Held your reputation at the current value", perSubjectScienceNote: false);

            if (!decision.ShouldWrite)
            {
                CommitStockPool(StockPool.Reputation, referenceRows, measurement, currentRep, 0.0);
                VerboseStablePatchState("patch-noop|reputation", decision.EffectiveTarget.ToString("F2", IC),
                    $"PatchReputation: no change needed (current={currentRep.ToString("F2", IC)}, " +
                    $"target={decision.EffectiveTarget.ToString("F2", IC)})");
                return;
            }

            Reputation.Instance.SetReputation(decision.EffectiveTarget, TransactionReasons.None);

            float afterRep = Reputation.Instance.reputation;
            CommitStockPool(StockPool.Reputation, referenceRows, measurement, afterRep, 0.0);
            ParsekLog.Info(Tag,
                $"PatchReputation: {currentRep.ToString("F2", IC)} -> {afterRep.ToString("F2", IC)} " +
                $"(target={decision.EffectiveTarget.ToString("F2", IC)})");

            // LedgerTrace Tier-C: reconcile the computed target against the live
            // read-back we just took (afterRep). Reuses afterRep; no second read.
            if (LedgerTrace.IsEnabled)
            {
                EmitPoolReadbackAnomaly("reputation", decision.EffectiveTarget, afterRep);
            }
        }
    }
}
