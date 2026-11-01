using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    /// <summary>
    /// Writes derived module state from the recalculation engine to KSP singletons.
    /// Called after a full recalculation walk to sync the game's resource values,
    /// facility levels, and milestone state with the ledger's computed state.
    ///
    /// All mutations are wrapped in SuppressResourceEvents + IsReplayingActions
    /// to prevent the GameStateRecorder from re-capturing these patches as new events,
    /// and to bypass blocking Harmony patches.
    /// </summary>
    internal static partial class KspStatePatcher
    {
        private const string Tag = "KspStatePatcher";
        private const double RecordStateEpsilon = 0.000001;

        // Max per-identity entries appended to a default-level (Info) apply line.
        // Keeps the Info aggregate diagnosable without unbounded log growth; the full
        // list is emitted alongside at Verbose.
        private const int IdentitySampleCap = 10;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private static void VerboseStablePatchState(string identity, string stateKey, string message)
        {
            ParsekLog.VerboseOnChange(Tag, identity, stateKey, message);
        }

        // LedgerTrace Tier-C: reconcile the computed target against the live
        // pool read-back. A drift beyond the resource tolerance is a
        // ledger-vs-truth anomaly (the write did not land where the recalc
        // intended). Callers must still gate this on LedgerTrace.IsEnabled.
        private static void EmitPoolReadbackAnomaly(string resourceKey, double target, double actual)
        {
            double tol = LedgerTrace.ResourceTolerance(resourceKey);
            if (LedgerTrace.IsResourceDrift(target, actual, tol))
                LedgerTrace.EmitAnomaly(resourceKey, "pool", "ledger-vs-truth",
                    $"target={LedgerTrace.FormatDouble(target, "F3")} " +
                    $"actual={LedgerTrace.FormatDouble(actual, "F3")} " +
                    $"delta={LedgerTrace.FormatDouble(target - actual, "F3")} " +
                    $"tol={LedgerTrace.FormatDouble(tol, "R")}");
        }

        /// <summary>
        /// When true, skips Unity-only API calls (FindObjectsOfType etc.) that crash outside the engine.
        /// Set by test fixtures; reset via <see cref="ResetForTesting"/>.
        /// </summary>
        internal static bool SuppressUnityCallsForTesting;

        /// <summary>
        /// Patches all KSP singletons from the given module states.
        /// Wraps the entire operation in suppression flags.
        /// </summary>
        internal static void PatchAll(ScienceModule science, FundsModule funds,
            ReputationModule reputation, MilestonesModule milestones, FacilitiesModule facilities,
            ContractsModule contracts = null,
            IReadOnlyCollection<string> targetTechIds = null,
            bool authoritativeRepeatableRecordState = false,
            double? techUtCutoff = null,
            double? techBaselineUt = null,
            bool suppressSuspiciousDrawdownWarnings = false,
            bool authoritativeReduction = false,
            IDictionary<string, UnaffordableUnlockDrop> unaffordableTechDrops = null,
            StrategiesModule strategies = null,
            IReadOnlyList<GameAction> partPurchaseActions = null,
            double? walkUtCutoff = null,
            IReadOnlyCollection<string> committedTechUnlockIds = null,
            double? committedTechUnlockLiveUt = null,
            double? deadlineClockUT = null)
        {
            using (SuppressionGuard.ResourcesAndReplay())
            {
                // Rewind read-back divergence guard (audit rec #1): when armed at the
                // rewind apply boundary, flag a recalc that would write the economy below
                // the real career floor. Warn-and-proceed in production - the runner returns
                // false unless the abort test seam is forced, so the normal path logs and
                // proceeds with NO behavior change. Inert (returns false fast) when not
                // armed.
                if (RunRewindReadbackGuard(science, funds, reputation, authoritativeReduction))
                {
                    ParsekLog.Warn(Tag,
                        "PatchAll: ABORTING rewind ledger patch: divergence flagged and abort forced; " +
                        "economy/tech/contracts/milestones NOT patched, crew roster already applied; " +
                        "loaded quicksave values stand");
                    return;
                }

                // The drawdown guard's stock reference reads the walk's own rows and its own
                // now: rows after the walk's cutoff have not been applied by stock yet.
                var previousRows = stockReferenceActions;
                double previousNow = stockReferenceNowUT;
                stockReferenceActions = partPurchaseActions;
                stockReferenceNowUT = walkUtCutoff ?? double.NaN;
                try
                {
                    PatchPools(science, funds, reputation, suppressSuspiciousDrawdownWarnings,
                        authoritativeReduction, targetTechIds, techUtCutoff, techBaselineUt,
                        unaffordableTechDrops, partPurchaseActions, walkUtCutoff,
                        committedTechUnlockIds, committedTechUnlockLiveUt);
                }
                finally
                {
                    stockReferenceActions = previousRows;
                    stockReferenceNowUT = previousNow;
                }
                PatchFacilities(facilities);
                PatchMilestones(milestones, authoritativeRepeatableRecordState);
                // One clock rule for both committed-row filters: NaN for a cutoff walk (its
                // state is already the state at the cutoff) and while OnLoad runs (the live
                // clock is still the pre-load one there).
                double committedRowClockUT = ResolveContractAcceptClockUT(walkUtCutoff, deadlineClockUT,
                    ParsekScenario.IsOnLoadInProgress, ParsekScenario.GetCurrentTimelineUTForLedgerRecalc);
                // Parameter completion writes stock state that persists and has no undo, so
                // it needs a real clock: the walk's own cutoff, else the committed-row clock.
                double parameterClockUT = ResolveContractParameterClockUT(walkUtCutoff, committedRowClockUT);
                PatchContracts(contracts, committedRowClockUT, parameterClockUT);
                // After funds (the walk already charged the setup cost; this path charges
                // nothing) and facilities (the Administration level). Null skips it, which
                // is what callers that do not own a strategies module pass.
                if (strategies != null)
                    PatchStrategies(strategies, committedRowClockUT);

                VerboseStablePatchState("patch-all-complete", "complete", "PatchAll complete");
            }
        }

        private static void PatchPools(ScienceModule science, FundsModule funds,
            ReputationModule reputation, bool suppressSuspiciousDrawdownWarnings,
            bool authoritativeReduction, IReadOnlyCollection<string> targetTechIds,
            double? techUtCutoff, double? techBaselineUt,
            IDictionary<string, UnaffordableUnlockDrop> unaffordableTechDrops,
            IReadOnlyList<GameAction> partPurchaseActions, double? walkUtCutoff,
            IReadOnlyCollection<string> committedTechUnlockIds, double? committedTechUnlockLiveUt)
        {
            PatchScience(science, suppressSuspiciousDrawdownWarnings, authoritativeReduction);
            PatchTechTree(targetTechIds, techUtCutoff, techBaselineUt, unaffordableTechDrops);
            // Add-only; the caller supplies a set only when it supplied no tech cutoff,
            // so at most one of the two tech passes does anything.
            PatchCommittedTechUnlocks(committedTechUnlockIds, committedTechUnlockLiveUt);
            // Add-only, capped like the part purchases; the same action list. Free.
            PatchResourceScans(partPurchaseActions, walkUtCutoff, techUtCutoff);
            // Funds before the purchase unlocks: a purchase the clock just reached is marked
            // bought only by a pass whose funds patch took its cost.
            PatchFunds(funds, suppressSuspiciousDrawdownWarnings, authoritativeReduction);
            if (ShouldHoldPurchaseStatePatches(ActiveClockCrossing))
            {
                ParsekLog.Warn(Tag,
                    "Clock-crossing purchase unlocks held: the funds patch did not apply the reached rows " +
                    $"(skipped); crossedRows={ActiveClockCrossing.CrossedRows.ToString(IC)} - parts and upgrades " +
                    "are left unbought, and the next recalc with funds applied takes the cost and unlocks them");
            }
            else
            {
                PatchPurchasedParts(partPurchaseActions, walkUtCutoff, techUtCutoff);
                // Add-only, capped like the part purchases; the same action list.
                PatchUnlockedUpgrades(partPurchaseActions, walkUtCutoff, techUtCutoff);
            }
            PatchReputation(reputation, authoritativeReduction);
        }

        /// <summary>
        /// Pure: hold this pass's part-purchase and part-upgrade unlocks? Only on a clock
        /// crossing whose funds patch skipped while there is a funds pool to charge: the
        /// reached rows' cost is then not in the live pool, so unlocking would hand over a
        /// part for nothing. The cost is not lost: a crossed row stays counted as unapplied
        /// in the stock pool reference until a funds patch writes it, and the add-only
        /// purchase passes unlock it on that same later pass. No pool (sandbox, science
        /// mode) has nothing to charge, and an ordinary recalc reaches no unapplied row.
        /// </summary>
        internal static bool ShouldHoldPurchaseStatePatches(ClockCrossing crossing)
        {
            return crossing != null && !crossing.FundsApplied && !crossing.NoFundsPool;
        }

        // #559 review follow-up: one-shot reflection-failure diagnostics. When KSP renames
        // or changes the visibility of `protoTechNodes` / its backing field, rewind patches
        // silently fall back to static-tree-only coverage. The Warn log fires exactly once
        // per session (guarded by a static bool; reset in ResetForTesting) to flag the
        // regression without spamming per-call.
        private static bool protoTechNodesReflectionWarnEmitted;

        /// <summary>Test seam for the live clock the part-purchase cutoff falls back to.</summary>
        internal static Func<double> PartPurchaseLiveUtProviderForTesting;

        /// <summary>
        /// Test-only seam for <c>AdvancedParams.AllowNegativeCurrency</c>. Production reads
        /// the live game parameter. Cleared by <see cref="ResetForTesting"/>.
        /// </summary>
        internal static Func<bool> AllowNegativeCurrencyProviderForTesting;

        // ---------------- resource scans (RESOURCE-SCAN-UNLOCKS-NOT-LEDGERED) ----------------

        /// <summary>A stub for tests: the live clock the scan patch reads. Null reads the real one.</summary>
        internal static Func<double> ResourceScanLiveUtProviderForTesting;

        /// <summary>Test seam for the live clock the upgrade cutoff falls back to.</summary>
        internal static Func<double> UpgradeUnlockLiveUtProviderForTesting;

        /// <summary>
        /// Wrapper preserved for existing KspStatePatcher callers.
        /// Facility patch behavior lives in <see cref="FacilityStatePatcher.PatchFacilities"/>.
        /// </summary>
        internal static void PatchFacilities(FacilitiesModule facilities)
        {
            FacilityStatePatcher.PatchFacilities(facilities);
        }

        /// <summary>
        /// Wrapper for the strategy state patch; behavior lives in
        /// <see cref="StrategyStatePatcher.PatchStrategies"/>.
        /// </summary>
        internal static void PatchStrategies(StrategiesModule strategies, double committedRowClockUT = double.NaN)
        {
            StrategyStatePatcher.PatchStrategies(strategies, committedRowClockUT);
        }

        // Mirrors protoTechNodesReflectionWarnEmitted: one-shot per session, reset in
        // ResetForTesting. When the reflection handle breaks, the create path degrades to
        // exactly the pre-fix behavior (count as notFound and skip), never to a crash.
        private static bool scienceSubjectsReflectionWarnEmitted;

        internal static readonly DeferredContractPatch DeferredContractPatchState = new DeferredContractPatch();

        // A cached lambda, never a static method group: EventVoid.Add rejects a delegate
        // with a null Target.
        private static EventVoid.OnEvent stockContractsLoadedHandler;

        private static FieldInfo contractParameterStateField;
        private static bool contractParameterStateReflectionWarnEmitted;

        /// <summary>
        /// Test-only frame host for the contract re-evaluation; non-null replaces
        /// <c>WarpToTimeConsumer.RunNextFrame</c>. Cleared by <see cref="ResetForTesting"/>.
        /// </summary>
        internal static Action<Action> ContractReEvaluationDeferForTesting;

        private static FieldInfo contractStateField;
        private static FieldInfo contractDateFinishedField;
        private static bool contractStateReflectionWarnEmitted;

        // ================================================================
        // "Keep what you earned" drawdown guard (recalc-patch-drawdown-guard plan)
        // ================================================================

        // Per-resource SESSION-SCOPED one-shot latches for the on-screen "Kept your
        // earned X" toast. NOT reset on scene change: an ongoing leak toasts ONCE per
        // session and stays quiet across every subsequent scene-change recalc, while the
        // WARN log still fires on every guarded recalc for diagnostics. Cleared only in
        // ResetForTesting (xUnit) and at a genuine new-session boundary via
        // ResetDrawdownGuardSessionLatches (called from ParsekScenario OnLoad of a save).
        private static bool fundsClampToastShownThisSession;
        private static bool scienceClampToastShownThisSession;
        private static bool reputationClampToastShownThisSession;

        // Dedicated UPLIFT (DOWN-clamp) session latches (plan §2.2 step 3). The symmetric
        // no-authority guard can clamp DOWN to live when the running balance LEADS live
        // (a real spend the ledger does not model, e.g. a facility upgrade — Bug 2). That
        // direction toasts once per session INDEPENDENTLY of the drawdown (UP-clamp) latch,
        // so a session that experiences both an under-model leak and an over-model leak
        // shows both toasts. Same lifecycle as the drawdown latches.
        private static bool fundsUpliftClampToastShownThisSession;
        private static bool scienceUpliftClampToastShownThisSession;
        private static bool reputationUpliftClampToastShownThisSession;

        /// <summary>
        /// Set by the orchestrator around one clock-advance patch that reached committed-future
        /// rows (try/finally), null otherwise.
        /// </summary>
        internal static ClockCrossing ActiveClockCrossing;

        // ================================================================
        // Stock pool reference wiring (committed-value rule plan section 2.4)
        // ================================================================

        // The rows and the now the reference measures against, set by PatchAll around its
        // pool patches: the walk's own rows (the effective ledger) and its cutoff. A direct
        // Patch* call (an in-game test) reads the effective ledger and the live clock.
        private static IReadOnlyList<GameAction> stockReferenceActions;
        private static double stockReferenceNowUT = double.NaN;

        /// <summary>
        /// xUnit / in-game seam: when set, a patch's adjustment row goes here instead of the
        /// live ledger (an in-game test that drives the Patch* methods on the player's own
        /// career must not leave rows in its ledger). Reset by <see cref="ResetForTesting"/>.
        /// </summary>
        internal static Action<GameAction> StockPoolAdjustmentSinkForTesting;

        /// <summary>
        /// Production reset for a cold load (<c>ParsekScenario.ResetSaveScopedSessionState</c>):
        /// the per-session clamp toast latches. The stock pool reference is not reset here:
        /// the load installs the save's own (<see cref="StockPoolReference.LoadFrom"/>).
        /// </summary>
        internal static void ResetForSaveLoad()
        {
            ResetDrawdownGuardSessionLatches();
            // A contract patch deferred for the previous save must not run against this one.
            ResetDeferredContractPatch();
            FacilityStatePatcher.ResetForSaveLoad();
        }

        internal static void ResetForTesting()
        {
            SuppressUnityCallsForTesting = false;
            ActiveClockCrossing = null;
            stockReferenceActions = null;
            stockReferenceNowUT = double.NaN;
            StockPoolAdjustmentSinkForTesting = null;
            StockPoolReference.ResetForTesting();
            CommittedMilestoneReach.ResetForTesting();
            PartPurchaseLiveUtProviderForTesting = null;
            ResourceScanLiveUtProviderForTesting = null;
            UpgradeUnlockLiveUtProviderForTesting = null;
            AllowNegativeCurrencyProviderForTesting = null;
            protoTechNodesReflectionWarnEmitted = false;
            scienceSubjectsReflectionWarnEmitted = false;
            contractParameterStateReflectionWarnEmitted = false;
            contractStateReflectionWarnEmitted = false;
            ContractReEvaluationDeferForTesting = null;
            ResetDrawdownGuardSessionLatches();
            FacilityStatePatcher.ResetForTesting();
        }
    }
}
