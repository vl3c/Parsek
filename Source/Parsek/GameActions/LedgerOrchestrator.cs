using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    /// <summary>
    /// Central coordination point for the ledger-based game actions system.
    /// Creates module instances, registers them with the recalculation engine,
    /// and orchestrates the convert-ledger-recalculate-patch pipeline.
    ///
    /// Called from ParsekScenario lifecycle hooks (OnSave, OnLoad, revert, rewind)
    /// and from recording commit paths.
    ///
    /// Replaces the old ResourceApplicator + ActionReplay system.
    /// </summary>
    internal static class LedgerOrchestrator
    {
        internal const string Tag = "LedgerOrchestrator";

        // Module instances (created once, reused)
        private static ScienceModule scienceModule;
        private static MilestonesModule milestonesModule;
        private static ContractsModule contractsModule;
        private static FundsModule fundsModule;
        private static ReputationModule reputationModule;
        private static FacilitiesModule facilitiesModule;
        private static StrategiesModule strategiesModule;
        private static KerbalsModule kerbalsModule;
        private static bool initialized;
        private static bool fundsSeedDone;
        private static bool scienceSeedDone;
        private static bool repSeedDone;
        [ThreadStatic]
        private static bool? fundsTrackedOverrideForTesting;
        [ThreadStatic]
        private static bool? scienceTrackedOverrideForTesting;
        [ThreadStatic]
        private static bool? repTrackedOverrideForTesting;
        // Effectively "once per play session" for the rate-limited skip diagnostics.
        private static readonly double OneShotReconcileSkipLogIntervalSeconds =
            TimeSpan.FromDays(365).TotalSeconds;
        private static readonly HashSet<string> emittedReconcileWarnKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> emittedScienceReconcileDumpKeys =
            new HashSet<string>(StringComparer.Ordinal);

        internal static void ResetMigrateOldSaveEventsForLoad()
        {
            LedgerLoadMigration.ResetMigrateOldSaveEventsForLoad();
        }

        internal static void ResetMigrateOldSaveEventsForTesting()
        {
            LedgerLoadMigration.ResetMigrateOldSaveEventsForTesting();
        }

        internal static void SetMigrateOldSaveEventsRanThisLoadForTesting(bool value)
        {
            LedgerLoadMigration.SetMigrateOldSaveEventsRanThisLoadForTesting(value);
        }

        internal static bool GetMigrateOldSaveEventsRanThisLoadForTesting()
        {
            return LedgerLoadMigration.GetMigrateOldSaveEventsRanThisLoadForTesting();
        }
        /// <summary>
        /// Test-only override for resource tracker availability checks used by the
        /// earnings reconciliation diagnostics. Null falls back to the live KSP
        /// singleton availability.
        /// </summary>
        internal static void SetResourceTrackingAvailabilityForTesting(
            bool? fundsTracked,
            bool? scienceTracked,
            bool? reputationTracked)
        {
            fundsTrackedOverrideForTesting = fundsTracked;
            scienceTrackedOverrideForTesting = scienceTracked;
            repTrackedOverrideForTesting = reputationTracked;
        }

        /// <summary>
        /// Monotonically increasing sequence counter for KSC events added individually
        /// via <see cref="OnKscSpending"/>. Ensures stable ordering when multiple KSC
        /// events occur at the same UT (e.g., rapid tech unlocks or facility upgrades).
        /// Reset on <see cref="ResetForTesting"/>.
        /// </summary>
        private static int kscSequenceCounter;

        private static int AllocateKscSequence()
        {
            kscSequenceCounter++;
            return kscSequenceCounter;
        }

        // Shared with GameActionDisplay.IsUnclaimedRolloutAction (#452) so the
        // label-side predicate and the adoption-side producer/scan cannot drift.
        internal const string RolloutDedupPrefix = "rollout:";

        /// <summary>
        /// Fired after RecalculateAndPatch completes — signals that timeline data
        /// (recordings, ledger actions) may have changed. Subscribed by ParsekUI
        /// to invalidate the TimelineWindowUI cache.
        /// </summary>
        internal static Action OnTimelineDataChanged;

        /// <summary>
        /// Initialize modules and register with engine. Call once on game start.
        /// Idempotent — safe to call multiple times.
        /// </summary>
        internal static void Initialize()
        {
            if (initialized) return;

            scienceModule = new ScienceModule();
            milestonesModule = new MilestonesModule();
            contractsModule = new ContractsModule();
            fundsModule = new FundsModule();
            reputationModule = new ReputationModule();
            facilitiesModule = new FacilitiesModule();
            strategiesModule = new StrategiesModule();

            RecalculationEngine.ClearModules();

            kerbalsModule = new KerbalsModule();

            // Register in tier order per design doc 1.8
            RecalculationEngine.RegisterModule(milestonesModule, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contractsModule, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(scienceModule, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(kerbalsModule, RecalculationEngine.ModuleTier.FirstTier);

            // Strategy transform between tiers
            RecalculationEngine.RegisterModule(strategiesModule, RecalculationEngine.ModuleTier.Strategy);

            // Second tier
            RecalculationEngine.RegisterModule(fundsModule, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputationModule, RecalculationEngine.ModuleTier.SecondTier);

            // Facilities parallel
            RecalculationEngine.RegisterModule(facilitiesModule, RecalculationEngine.ModuleTier.Facilities);

            initialized = true;
            ParsekLog.Info(Tag, "Initialized: 8 modules registered");

            // #444 review item 8: pin the VesselRecovery reason key against KSP's enum.ToString()
            // so a future rename in TransactionReasons surfaces immediately instead of silently
            // breaking the OnVesselRecoveryFunds pairing path. Wrapped in try/catch because
            // unit tests run without the KSP assembly enum loaded.
            try
            {
                string actualKey = TransactionReasons.VesselRecovery.ToString();
                if (actualKey != VesselRecoveryReasonKey)
                {
                    ParsekLog.Warn(Tag,
                        $"VesselRecoveryReasonKey drift: constant='{VesselRecoveryReasonKey}' " +
                        $"vs TransactionReasons.VesselRecovery.ToString()='{actualKey}' — " +
                        $"OnVesselRecoveryFunds pairing will not match KSP events until the constant is updated");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag, $"VesselRecoveryReasonKey pin skipped (likely unit test without KSP enum): {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// Called when a recording is committed. Converts events to GameActions,
        /// adds to ledger, recalculates, and patches KSP state.
        /// </summary>
        /// <param name="recordingId">The recording that was just committed.</param>
        /// <param name="startUT">Start UT of the recording.</param>
        /// <param name="endUT">End UT of the recording.</param>
        /// <summary>
        /// Test-only fault injector. When non-null, <see cref="OnRecordingCommitted"/>
        /// invokes this action at its entry point with the current recording being
        /// committed, before any real logic runs. Used by PendingScienceSubjectsClearTests
        /// to verify the failure-path pending-science retention invariant in
        /// NotifyLedgerTreeCommitted — allowing the test to force a throw without
        /// corrupting real state.
        /// Reset to null in test Dispose.
        /// </summary>
        internal static Action<string> OnRecordingCommittedFaultInjector;

        /// <summary>
        /// Test-only fault injector fired after a recording's science actions have been
        /// persisted to both the ledger and committed-science store, but before
        /// <see cref="OnRecordingCommitted"/> returns to the caller. Used to verify that
        /// post-persist failures do not leave pending science behind and cannot leave
        /// ledger/store ownership split apart. Reset to null in test Dispose /
        /// <see cref="ResetForTesting"/>.
        /// </summary>
        internal static Action<string> OnRecordingCommittedPostSciencePersistFaultInjector;

        /// <summary>
        /// Sentinel value passed by <see cref="NotifyLedgerTreeCommitted"/> to recordings
        /// in a multi-recording tree that should NOT absorb any pending science subjects.
        /// Distinct from <c>null</c>, which means "read from the static list as usual"
        /// (the single-recording commit paths).
        /// </summary>
        private static readonly IReadOnlyList<PendingScienceSubject> EmptyPendingScience =
            new PendingScienceSubject[0];

        private struct RecordingScienceWindow
        {
            public string RecordingId;
            public double StartUT;
            public double EndUT;
        }

        internal static void OnRecordingCommitted(
            string recordingId, double startUT, double endUT,
            IReadOnlyList<PendingScienceSubject> pendingScienceOverride,
            ref bool scienceActionsAddedToLedger)
        {
            // Test-only fault injection (see OnRecordingCommittedFaultInjector doc).
            OnRecordingCommittedFaultInjector?.Invoke(recordingId);

            Initialize();

            // 1. Convert GameStateStore events to GameActions
            var events = GameStateStore.Events;
            var actions = GameStateEventConverter.ConvertEvents(events, recordingId, startUT, endUT);

            // 2. Convert pending science subjects.
            // When called from NotifyLedgerTreeCommitted, an override list is passed —
            // non-null means "use this exact routed subset". When the override is null
            // (the single-recording commit paths in ChainSegmentManager.CommitSegmentCore
            // and ParsekFlight.FallbackCommitSplitRecorder), fall back to the static list.
            IReadOnlyList<PendingScienceSubject> pendingSource =
                pendingScienceOverride ?? GameStateRecorder.PendingScienceSubjects;
            var scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                pendingSource, recordingId, startUT, endUT);
            actions.AddRange(scienceActions);

            // 3. Inject vessel build cost and recovery funds from recording data
            var costActions = CreateVesselCostActions(recordingId, startUT, endUT);
            actions.AddRange(costActions);

            // 3b. Populate crew end states before action creation so KerbalEndStateField is correct
            var recForEndStates = FindRecordingById(recordingId);
            if (NeedsCrewEndStatePopulation(recForEndStates))
                KerbalsModule.PopulateCrewEndStates(recForEndStates);

            // 3c. Generate KerbalAssignment actions from vessel crew data
            var kerbalActions = CreateKerbalAssignmentActions(recordingId, startUT, endUT);
            actions.AddRange(kerbalActions);

            // 4. Deduplicate: remove actions already in the ledger from KSC real-time writes.
            // KSC events (tech, facility, hire, milestone) written via OnKscSpending may overlap
            // with the recording's time range. Compare by type + UT + key to avoid double-adding.
            int beforeDedup = actions.Count;
            actions = DeduplicateAgainstLedger(actions);
            int dedupRemoved = beforeDedup - actions.Count;

            // 5. Add to ledger
            Ledger.AddActions(actions);
            if (scienceActions.Count > 0)
            {
                // Mirror only after all pre-ledger work succeeded and the resulting
                // ScienceEarning actions are safely in the ledger. This keeps the
                // committed-subject cache aligned with the real persistence point.
                GameStateStore.CommitScienceActions(scienceActions);
                scienceActionsAddedToLedger = true;
            }

            // Test-only post-persist fault injection. Fires after both ledger add and
            // committed-science mirroring succeeded, but before the caller can mark the
            // overall commit as complete.
            OnRecordingCommittedPostSciencePersistFaultInjector?.Invoke(recordingId);

            ParsekLog.Info(Tag,
                $"Committed recording '{recordingId}': {actions.Count} actions added to ledger " +
                $"(events={events.Count}, science={scienceActions.Count}, cost={costActions.Count}, " +
                $"kerbals={kerbalActions.Count}, dedup={dedupRemoved}, startUT={startUT:F1}, endUT={endUT:F1})");

            // 6. Recalculate + patch. Must run BEFORE ReconcileEarningsWindow so the
            // walk populates derived fields (Transformed* / EffectiveRep / EffectiveScience)
            // that the reconciliation switch now reads. See #440B.
            RecalculateAndPatch();

            // 5b. #394 / #440B: commit-time reconciliation -- compare the sum of dropped
            // FundsChanged/ReputationChanged/ScienceChanged events (which the converter
            // intentionally drops, see GameStateEventConverter:121-130) against the
            // sum of effective emitted Funds/Rep/Science actions in the same UT window.
            // Log WARN on mismatch. Log-only: the drop block itself must stay, because
            // re-emitting those events would double-count against recovery + contract +
            // milestone channels (review section 5.1 regression guard).
            // #440B: now runs AFTER RecalculateAndPatch so it can read post-walk derived
            // fields; mirrors ReconcilePostWalk semantics and closes the double-WARN risk.
            ReconcileEarningsWindow(events, actions, startUT, endUT, recordingId);
        }

        /// <summary>
        /// Pure reconciliation: sums dropped FundsChanged/ReputationChanged/ScienceChanged
        /// deltas against effective emitted FundsEarning/FundsSpending/ReputationEarning/
        /// ReputationPenalty/ScienceEarning/ScienceSpending actions in the same UT window
        /// and logs WARN on mismatch beyond tolerance. When <paramref name="recordingId"/>
        /// is supplied, only events tagged to that recording contribute to the store-side
        /// delta. Internal static for testability.
        /// <para>
        /// Note: vesselCost and science actions are already merged into <paramref name="newActions"/>
        /// by <see cref="OnRecordingCommitted"/> before this runs, so they are summed implicitly
        /// through the single <c>newActions</c> walk and do not need separate parameters.
        /// </para>
        /// <para>
        /// #440B: this method must run AFTER <see cref="RecalculateAndPatch"/> populates
        /// derived fields on the seeded actions. The switch reads post-walk values --
        /// TransformedFundsReward / TransformedScienceReward / EffectiveRep / EffectiveScience
        /// -- matching the rewind-path <see cref="ReconcilePostWalk"/> hook. Calling this
        /// before the walk reads zero for every derived field and warns spuriously on
        /// every contract. The Effective-gated cases (ContractComplete, MilestoneAchievement,
        /// ScienceEarning, FundsEarning) mirror the module-side skip on duplicates.
        /// </para>
        /// </summary>
        internal static void ReconcileEarningsWindow(
            IReadOnlyList<GameStateEvent> events,
            List<GameAction> newActions,
            double startUT, double endUT,
            string recordingId = null)
        {
            GetResourceTrackingAvailability(
                out bool fundsTracked,
                out bool scienceTracked,
                out bool repTracked);
            if (!fundsTracked && !scienceTracked && !repTracked)
            {
                LogReconcileSkippedOnce("earnings-window", "Earnings reconcile",
                    fundsTracked, scienceTracked, repTracked);
                return;
            }

            var storeDeltas = ComputeEarningsWindowStoreDeltas(
                events,
                startUT,
                endUT,
                recordingId);
            LogEarningsWindowScopeSkipped(storeDeltas, startUT, endUT, recordingId);

            var emittedDeltas = ComputeEarningsWindowEmittedDeltas(newActions);

            if (scienceTracked)
            {
                storeDeltas.DroppedSciDelta = SumCommitWindowScienceDelta(
                    events,
                    emittedDeltas.EffectiveScienceActions,
                    startUT,
                    endUT,
                    recordingId,
                    storeDeltas.WindowScopedSciDelta);
            }

            LogEarningsWindowActionSummary(emittedDeltas, startUT, endUT);
            WarnOnEarningsWindowMismatches(
                storeDeltas,
                emittedDeltas,
                events,
                startUT,
                endUT,
                recordingId,
                fundsTracked,
                scienceTracked,
                repTracked);
        }

        private struct EarningsWindowStoreDeltas
        {
            public double DroppedFundsDelta;
            public double DroppedRepDelta;
            public double DroppedSciDelta;
            public double WindowScopedSciDelta;
            public int ScopeSkipped;
        }

        private struct EarningsWindowEmittedDeltas
        {
            public double EmittedFundsDelta;
            public double EmittedRepDelta;
            public double EmittedSciDelta;
            // Assigned once by ComputeEarningsWindowEmittedDeltas and treated read-only after return.
            public List<GameAction> EffectiveScienceActions;
            public int ContractAcceptCount;
            public int FacilityUpgradeCount;
            public int FacilityRepairCount;
        }

        private static EarningsWindowStoreDeltas ComputeEarningsWindowStoreDeltas(
            IReadOnlyList<GameStateEvent> events,
            double startUT,
            double endUT,
            string recordingId)
        {
            var deltas = new EarningsWindowStoreDeltas();
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e.ut < startUT || e.ut > endUT) continue;
                    if (!EventMatchesRecordingScope(e, recordingId))
                    {
                        deltas.ScopeSkipped++;
                        continue;
                    }
                    switch (e.eventType)
                    {
                        case GameStateEventType.FundsChanged:
                            deltas.DroppedFundsDelta += (e.valueAfter - e.valueBefore);
                            break;
                        case GameStateEventType.ReputationChanged:
                            deltas.DroppedRepDelta += (e.valueAfter - e.valueBefore);
                            break;
                        case GameStateEventType.ScienceChanged:
                            deltas.WindowScopedSciDelta += (e.valueAfter - e.valueBefore);
                            break;
                    }
                }
            }

            return deltas;
        }

        private static void LogEarningsWindowScopeSkipped(
            EarningsWindowStoreDeltas storeDeltas,
            double startUT,
            double endUT,
            string recordingId)
        {
            if (storeDeltas.ScopeSkipped > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReconcileEarningsWindow: skipped {storeDeltas.ScopeSkipped} event(s) tagged to other recordings " +
                    $"(scope='{recordingId}', window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}])");
            }
        }

        private static EarningsWindowEmittedDeltas ComputeEarningsWindowEmittedDeltas(
            List<GameAction> newActions)
        {
            var deltas = new EarningsWindowEmittedDeltas
            {
                EffectiveScienceActions = new List<GameAction>()
            };

            // newActions already contains vesselCostActions + scienceActions (merged by
            // OnRecordingCommitted before this runs), so a single walk suffices.
            if (newActions != null)
            {
                for (int i = 0; i < newActions.Count; i++)
                {
                    var a = newActions[i];
                    switch (a.Type)
                    {
                        case GameActionType.FundsEarning:
                            // #440B: preemptive parity with FundsModule, which skips
                            // crediting when !Effective. No TransformedFundsAwarded
                            // exists today; read raw FundsAwarded.
                            if (!a.Effective) break;
                            deltas.EmittedFundsDelta += a.FundsAwarded;
                            break;
                        case GameActionType.FundsSpending:
                            deltas.EmittedFundsDelta -= a.FundsSpent;
                            break;
                        case GameActionType.ReputationEarning:
                            // #440B: EffectiveRep is curve-applied by ReputationModule
                            // (ApplyReputationCurve). Mirrors ReconcilePostWalk.
                            deltas.EmittedRepDelta += a.EffectiveRep;
                            break;
                        case GameActionType.ReputationPenalty:
                            // #440B: EffectiveRep is signed negative for penalties
                            // (ReputationModule.ProcessContractPenaltyRep). No unary minus.
                            deltas.EmittedRepDelta += a.EffectiveRep;
                            break;
                        case GameActionType.ContractComplete:
                            // #440B: read post-walk Transformed* / EffectiveRep. Gate on
                            // Effective to skip duplicate completions (module skips credit).
                            if (!a.Effective) break;
                            deltas.EmittedFundsDelta += a.TransformedFundsReward;
                            deltas.EmittedRepDelta += a.EffectiveRep;
                            deltas.EmittedSciDelta += a.TransformedScienceReward;
                            break;
                        case GameActionType.ContractFail:
                        case GameActionType.ContractCancel:
                            // #440B: funds penalty has no transform today; keep raw.
                            // Rep penalty: EffectiveRep is already signed negative.
                            deltas.EmittedFundsDelta -= a.FundsPenalty;
                            deltas.EmittedRepDelta += a.EffectiveRep;
                            break;
                        case GameActionType.ContractAccept:
                            // #438 gap #1: without this case the advance payment's
                            // FundsChanged(ContractAdvance) delta had no emitted-side
                            // counterpart and every accepted contract whose UT fell
                            // inside a commit window produced a spurious funds WARN.
                            deltas.EmittedFundsDelta += a.AdvanceFunds;
                            deltas.ContractAcceptCount++;
                            break;
                        case GameActionType.FacilityUpgrade:
                            // #438 gap #6: symmetric to the KSC-side ReconcileKscAction
                            // path -- a facility spend inside a recording's commit window
                            // must subtract from the emitted delta to match the store's
                            // negative FundsChanged.
                            deltas.EmittedFundsDelta -= a.FacilityCost;
                            deltas.FacilityUpgradeCount++;
                            break;
                        case GameActionType.FacilityRepair:
                            deltas.EmittedFundsDelta -= a.FacilityCost;
                            deltas.FacilityRepairCount++;
                            break;
                        case GameActionType.MilestoneAchievement:
                            // #440B: rep leg reads EffectiveRep (curve-applied). Funds/Sci
                            // legs keep raw MilestoneFundsAwarded / MilestoneScienceAwarded
                            // (no transform today). Gate on Effective for duplicate milestones.
                            if (!a.Effective) break;
                            deltas.EmittedFundsDelta += a.MilestoneFundsAwarded;
                            deltas.EmittedRepDelta += a.EffectiveRep;
                            deltas.EmittedSciDelta += a.MilestoneScienceAwarded;
                            break;
                        case GameActionType.ScienceEarning:
                            // #440B: EffectiveScience is post-subject-cap (ScienceModule).
                            // At cap, EffectiveScience=0 while ScienceAwarded != 0 --
                            // this silences a false-positive WARN on capped subjects.
                            if (!a.Effective) break;
                            deltas.EmittedSciDelta += a.EffectiveScience;
                            deltas.EffectiveScienceActions.Add(a);
                            break;
                        case GameActionType.ScienceSpending:
                            deltas.EmittedSciDelta -= a.Cost;
                            break;
                    }
                }
            }

            return deltas;
        }

        private static void LogEarningsWindowActionSummary(
            EarningsWindowEmittedDeltas emittedDeltas,
            double startUT,
            double endUT)
        {
            // #438: single Verbose summary covering the three newly-handled action
            // types so their contribution to emittedFundsDelta is visible in KSP.log
            // without emitting one log line per loop iteration.
            if (emittedDeltas.ContractAcceptCount > 0
                || emittedDeltas.FacilityUpgradeCount > 0
                || emittedDeltas.FacilityRepairCount > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReconcileEarningsWindow: summed {emittedDeltas.ContractAcceptCount} ContractAccept, " +
                    $"{emittedDeltas.FacilityUpgradeCount} FacilityUpgrade, {emittedDeltas.FacilityRepairCount} FacilityRepair " +
                    $"into emittedFundsDelta window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]");
            }
        }

        private static void WarnOnEarningsWindowMismatches(
            EarningsWindowStoreDeltas storeDeltas,
            EarningsWindowEmittedDeltas emittedDeltas,
            IReadOnlyList<GameStateEvent> events,
            double startUT,
            double endUT,
            string recordingId,
            bool fundsTracked,
            bool scienceTracked,
            bool repTracked)
        {
            const double fundsTol = 1.0;   // 1 funds tolerance for rounding
            const double repTol = 0.1f;
            const double sciTol = 0.1f;

            if (fundsTracked && Math.Abs(storeDeltas.DroppedFundsDelta - emittedDeltas.EmittedFundsDelta) > fundsTol)
            {
                ParsekLog.Warn(Tag,
                    $"Earnings reconciliation (funds): store delta={FormatFixed1(storeDeltas.DroppedFundsDelta)} vs " +
                    $"ledger emitted delta={FormatFixed1(emittedDeltas.EmittedFundsDelta)} — missing earning channel? " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]");
            }
            if (repTracked && Math.Abs(storeDeltas.DroppedRepDelta - emittedDeltas.EmittedRepDelta) > repTol)
            {
                ParsekLog.Warn(Tag,
                    $"Earnings reconciliation (rep): store delta={FormatFixed1(storeDeltas.DroppedRepDelta)} vs " +
                    $"ledger emitted delta={FormatFixed1(emittedDeltas.EmittedRepDelta)} — missing earning channel? " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]");
            }
            if (scienceTracked && Math.Abs(storeDeltas.DroppedSciDelta - emittedDeltas.EmittedSciDelta) > sciTol)
            {
                string message =
                    $"Earnings reconciliation (sci): store delta={FormatFixed1(storeDeltas.DroppedSciDelta)} vs " +
                    $"ledger emitted delta={FormatFixed1(emittedDeltas.EmittedSciDelta)} — missing earning channel? " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]";
                string warnKey = string.Format(
                    CultureInfo.InvariantCulture,
                    "commit:sci:{0}:{1:F3}:{2:F3}:{3:F3}:{4:F3}",
                    recordingId ?? "",
                    startUT,
                    endUT,
                    storeDeltas.DroppedSciDelta,
                    emittedDeltas.EmittedSciDelta);
                if (LogReconcileWarnOnce(warnKey, message))
                {
                    LogScienceCommitReconcileDumpOnce(
                        warnKey,
                        events,
                        emittedDeltas.EffectiveScienceActions,
                        startUT,
                        endUT,
                        recordingId);
                }
            }
        }

        private static double SumCommitWindowScienceDelta(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> effectiveScienceActions,
            double startUT,
            double endUT,
            string recordingId,
            double windowScopedSciDelta)
        {
            if (events == null || events.Count == 0)
                return 0.0;
            if (effectiveScienceActions == null || effectiveScienceActions.Count == 0)
                return windowScopedSciDelta;

            double matchedSciDelta = windowScopedSciDelta;
            int extendedCount = 0;
            int widenedUntaggedCount = 0;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.ScienceChanged)
                    continue;
                if (evt.ut >= startUT)
                    continue;
                if (evt.ut > endUT)
                    continue;
                if (!string.IsNullOrEmpty(evt.recordingId ?? ""))
                    continue;
                if (!DoesScienceEventMatchAnyCommitAction(
                        evt,
                        effectiveScienceActions,
                        startUT,
                        endUT,
                        recordingId,
                        out bool matchedViaUntaggedWindow))
                {
                    continue;
                }

                matchedSciDelta += (evt.valueAfter - evt.valueBefore);
                extendedCount++;
                if (matchedViaUntaggedWindow)
                    widenedUntaggedCount++;
            }

            if (extendedCount > 0 && widenedUntaggedCount > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReconcileEarningsWindow: extended science delta with {extendedCount} " +
                    $"including {widenedUntaggedCount} untagged pre-recording event(s) " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}] scope='{recordingId ?? "(none)"}'");
            }

            return matchedSciDelta;
        }

        private static bool DoesScienceEventMatchAnyCommitAction(
            GameStateEvent evt,
            IReadOnlyList<GameAction> effectiveScienceActions,
            double startUT,
            double endUT,
            string recordingId,
            out bool matchedViaUntaggedWindow)
        {
            matchedViaUntaggedWindow = false;

            if (effectiveScienceActions == null || effectiveScienceActions.Count == 0)
                return evt.ut >= startUT && evt.ut <= endUT && EventMatchesRecordingScope(evt, recordingId);

            for (int i = 0; i < effectiveScienceActions.Count; i++)
            {
                var action = effectiveScienceActions[i];
                if (action == null)
                    continue;
                if (!TryGetPersistedScienceActionWindow(
                        action,
                        out double actionStartUt,
                        out double actionEndUt,
                        out bool ignoredCollapsedPersistedSpan))
                {
                    actionStartUt = action.StartUT;
                    actionEndUt = action.EndUT;
                }
                if (evt.ut < actionStartUt || evt.ut > actionEndUt)
                    continue;
                if (!string.Equals(
                        evt.key ?? "",
                        GetScienceChangedReasonKey(action),
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!DoesScienceEventMatchActionScope(
                        evt,
                        action,
                        actionStartUt,
                        actionEndUt,
                        out bool viaUntaggedWindow))
                    continue;

                matchedViaUntaggedWindow = viaUntaggedWindow;
                return true;
            }

            return false;
        }

        internal static bool LogReconcileWarnOnce(string warnKey, string message)
        {
            if (string.IsNullOrEmpty(warnKey) || emittedReconcileWarnKeys.Add(warnKey))
            {
                ParsekLog.Warn(Tag, message);
                return true;
            }

            return false;
        }

        private static void LogScienceCommitReconcileDumpOnce(
            string dumpKey,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> effectiveScienceActions,
            double startUT,
            double endUT,
            string recordingId)
        {
            if (events == null || events.Count == 0)
                return;
            if (!emittedScienceReconcileDumpKeys.Add("commit-dump:" + (dumpKey ?? "")))
                return;

            double dumpStartUt = startUT - 5.0;
            double dumpEndUt = endUT + 5.0;
            var lines = new List<string>();
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.ScienceChanged)
                    continue;
                if (evt.ut < dumpStartUt || evt.ut > dumpEndUt)
                    continue;

                bool matched = DoesScienceEventMatchAnyCommitAction(
                    evt,
                    effectiveScienceActions,
                    startUT,
                    endUT,
                    recordingId,
                    out bool viaUntaggedWindow);

                lines.Add(FormatScienceEventForReconcileDump(evt, matched, viaUntaggedWindow));
            }

            string detail = lines.Count == 0
                ? "(no ScienceChanged events in dump window)"
                : string.Join(" | ", lines.ToArray());
            ParsekLog.Error(Tag,
                $"Science reconcile dump (commit): window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}] " +
                $"scope='{recordingId ?? "(none)"}' events={detail}");
        }

        /// <summary>
        /// Removes actions from the candidate list that already exist in the ledger.
        /// Matches on Type + UT (within epsilon) + key field (SubjectId, NodeId, FacilityId,
        /// MilestoneId, or ContractId depending on type). This prevents double-adding KSC
        /// events that were written to the ledger in real-time via OnKscSpending but also
        /// fall within a recording's time range.
        /// </summary>
        internal static List<GameAction> DeduplicateAgainstLedger(List<GameAction> candidates)
        {
            var existing = Ledger.Actions;
            if (existing.Count == 0) return candidates;

            var result = new List<GameAction>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                bool isDuplicate = false;

                for (int j = 0; j < existing.Count; j++)
                {
                    var e = existing[j];
                    if (e.Type != c.Type) continue;
                    if (System.Math.Abs(e.UT - c.UT) > 0.1) continue;

                    // Match on the type-specific key field
                    if (GetActionKey(e) == GetActionKey(c))
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                    result.Add(c);
            }

            if (result.Count < candidates.Count)
            {
                ParsekLog.Verbose(Tag,
                    $"DeduplicateAgainstLedger: removed {candidates.Count - result.Count} duplicates " +
                    $"from {candidates.Count} candidates");
            }

            return result;
        }

        /// <summary>Returns the type-specific key field for deduplication matching.</summary>
        internal static string GetActionKey(GameAction a)
        {
            switch (a.Type)
            {
                case GameActionType.ScienceEarning: return a.SubjectId ?? "";
                case GameActionType.ScienceSpending: return a.NodeId ?? "";
                case GameActionType.FundsEarning: return a.RecordingId ?? "";
                // FundsSpending: RecordingId alone collides when multiple KSC part
                // purchases share a null/empty RecordingId. DedupKey is the part name
                // (populated by ConvertPartPurchased) so each part disambiguates.
                // VesselBuild spending has RecordingId set and DedupKey null, so the
                // composite key stays unique there.
                case GameActionType.FundsSpending: return (a.RecordingId ?? "") + ":" + (a.DedupKey ?? "");
                case GameActionType.MilestoneAchievement: return a.MilestoneId ?? "";
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityDestruction:
                case GameActionType.FacilityRepair: return a.FacilityId ?? "";
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel: return a.ContractId ?? "";
                case GameActionType.KerbalHire: return a.KerbalName ?? "";
                default: return "";
            }
        }


        /// <summary>
        /// Creates FundsSpending (VesselBuild) and FundsEarning (Recovery) actions
        /// from the recording's resource data. Looks up the recording by ID from
        /// CommittedRecordings (it has already been committed before this is called).
        /// </summary>
        /// <param name="recordingId">Recording ID to look up.</param>
        /// <param name="startUT">Start UT of the recording.</param>
        /// <param name="endUT">End UT of the recording.</param>
        /// <returns>List of 0–2 actions (build cost and/or recovery).</returns>
        internal static List<GameAction> CreateVesselCostActions(string recordingId, double startUT, double endUT)
        {
            var result = new List<GameAction>();

            var rec = FindRecordingById(recordingId);
            if (rec == null)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: recording '{recordingId}' not found in CommittedRecordings — skipping");
                return result;
            }

            // #432: ghost-only recordings (Gloops) have zero career footprint on the ledger.
            if (rec.IsGhostOnly)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: recording '{recordingId}' is ghost-only — skipping");
                return result;
            }

            if (rec.Points.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: recording '{recordingId}' has no points — skipping");
                return result;
            }

            // Vessel build cost = PreLaunchFunds - first point's funds
            // (KSP deducts vessel cost before the first frame is recorded)
            double firstFunds = rec.Points[0].funds;
            double buildCost = rec.PreLaunchFunds - firstFunds;

            AddVesselBuildCostActions(result, recordingId, startUT, rec, firstFunds, buildCost);
            AddVesselRecoveryCostActions(result, recordingId, endUT, rec);

            return result;
        }

        private static void AddVesselBuildCostActions(
            List<GameAction> result,
            string recordingId,
            double startUT,
            Recording rec,
            double firstFunds,
            double buildCost)
        {
            // #445: KSP fires TransactionReasons.VesselRollout BEFORE PreLaunchFunds is
            // captured (rollout deducts at editor->launchpad transition; CapturePreLaunchResources
            // runs later in the FLIGHT scene). So in the normal pad-start record flow the
            // rollout cost has already been written to the ledger by OnVesselRolloutSpending
            // and buildCost above is ~0. Adopt that rollout action onto this recording so the
            // ledger associates the cost with the trip instead of leaving it null-tagged.
            //
            // Claim gate: only recordings that STARTED from a launch-ready prelaunch state
            // are allowed to adopt. Without this, a user who presses Record after liftoff
            // (or later in ascent) would incorrectly steal the launch cost onto a mid-flight
            // recording, and deleting that recording would wrongly remove the real vessel
            // build cost from the ledger.
            var adopted = CanRecordingAdoptRolloutAction(rec)
                ? TryAdoptRolloutAction(recordingId, startUT, rec)
                : null;

            if (buildCost > 0)
            {
                if (adopted != null)
                {
                    // Rare case: rollout cost was written and there's still a positive
                    // delta between PreLaunchFunds and the first frame (e.g. another
                    // KSP-side deduction landed during launch). Emit the residual on top
                    // of the adopted rollout action so neither cost goes missing.
                    ParsekLog.Verbose(Tag,
                        $"CreateVesselCostActions: residual buildCost={buildCost:F0} on top of " +
                        $"adopted rollout (rolloutCost={adopted.FundsSpent:F0}) for '{recordingId}'");
                }

                result.Add(new GameAction
                {
                    UT = startUT,
                    Type = GameActionType.FundsSpending,
                    RecordingId = recordingId,
                    FundsSpent = (float)buildCost,
                    FundsSpendingSource = FundsSpendingSource.VesselBuild
                });

                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: vessel build cost={buildCost:F0} " +
                    $"(preLaunch={rec.PreLaunchFunds:F0}, first={firstFunds:F0}) for '{recordingId}'");
            }
            else if (adopted == null)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: zero build cost and no rollout adoption " +
                    $"(preLaunch={rec.PreLaunchFunds:F0}, first={firstFunds:F0}) for '{recordingId}'");
            }
        }

        private static void AddVesselRecoveryCostActions(
            List<GameAction> result,
            string recordingId,
            double endUT,
            Recording rec)
        {
            // Recovery funds: prefer the paired FundsChanged(VesselRecovery) event near
            // the recording end UT. This covers recoveries that happen after the last
            // recorded point but before commit outside FLIGHT (e.g. post-flight KSC
            // recovery). Fall back to the legacy "last point minus penultimate point"
            // heuristic only when no paired funds event is present.
            if (rec.TerminalStateValue == TerminalState.Recovered)
            {
                if (TryFindRecoveryFundsEvent(
                        GameStateStore.Events,
                        endUT,
                        skipConsumed: false,
                        out GameStateEvent pairedRecoveryEvent,
                        out string pairedDedupKey))
                {
                    double pairedRecoveryAmount =
                        pairedRecoveryEvent.valueAfter - pairedRecoveryEvent.valueBefore;
                    if (pairedRecoveryAmount > 0)
                    {
                        result.Add(new GameAction
                        {
                            UT = pairedRecoveryEvent.ut,
                            Type = GameActionType.FundsEarning,
                            RecordingId = recordingId,
                            FundsAwarded = (float)pairedRecoveryAmount,
                            FundsSource = FundsEarningSource.Recovery,
                            DedupKey = pairedDedupKey
                        });

                        ParsekLog.Verbose(Tag,
                            $"CreateVesselCostActions: paired recovery funds={pairedRecoveryAmount:F0} " +
                            $"(before={pairedRecoveryEvent.valueBefore:F0}, after={pairedRecoveryEvent.valueAfter:F0}) " +
                            $"for '{recordingId}'");
                    }
                    else
                    {
                        ParsekLog.Verbose(Tag,
                            $"CreateVesselCostActions: paired recovery event for '{recordingId}' " +
                            $"at ut={pairedRecoveryEvent.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                            $"has non-positive delta {pairedRecoveryAmount.ToString("F1", CultureInfo.InvariantCulture)} — skipping");
                    }
                }
                else if (rec.Points.Count >= 2)
                {
                    double lastFunds = rec.Points[rec.Points.Count - 1].funds;
                    double penultimateFunds = rec.Points[rec.Points.Count - 2].funds;
                    double recoveryAmount = lastFunds - penultimateFunds;

                    if (recoveryAmount > 0)
                    {
                        result.Add(new GameAction
                        {
                            UT = endUT,
                            Type = GameActionType.FundsEarning,
                            RecordingId = recordingId,
                            FundsAwarded = (float)recoveryAmount,
                            FundsSource = FundsEarningSource.Recovery
                        });

                        ParsekLog.Verbose(Tag,
                            $"CreateVesselCostActions: recovery funds={recoveryAmount:F0} " +
                            $"(last={lastFunds:F0}, penultimate={penultimateFunds:F0}) for '{recordingId}'");
                    }
                }
            }
        }

        /// <summary>
        /// Creates KerbalAssignment actions from the recording's vessel crew data.
        /// Each crew member in the vessel snapshot becomes a KerbalAssignment action
        /// recording their role, mission start/end UT, and end state.
        /// This populates the ledger for future full KerbalsModule integration.
        /// </summary>
        internal static List<GameAction> CreateKerbalAssignmentActions(
            string recordingId, double startUT, double endUT)
        {
            var result = new List<GameAction>();

            var rec = FindRecordingById(recordingId);
            if (rec == null) return result;

            // #432: ghost-only recordings (Gloops) have zero career footprint on the ledger.
            // Closes the MigrateKerbalAssignments leak where a crewed Gloops recording would
            // reserve its kerbals for the loop duration. Note that end-state population still
            // occurs via PopulateUnpopulatedCrewEndStates (the safety-net pass inside
            // RecalculateAndPatch at :1108) for Gloops recordings that need CrewEndStates for
            // the Kerbals-window per-recording-fates view; only the ledger-action emission is
            // suppressed here.
            if (rec.IsGhostOnly)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateKerbalAssignmentActions: recording '{recordingId}' is ghost-only — skipping");
                return result;
            }

            if (NeedsCrewEndStatePopulation(rec))
                KerbalsModule.PopulateCrewEndStates(rec);

            // Extract crew from vessel snapshot (same source KerbalsModule.ProcessAction uses)
            var crew = ExtractCrewFromRecording(rec);
            if (crew == null || crew.Count == 0) return result;

            for (int i = 0; i < crew.Count; i++)
            {
                var member = crew[i];
                result.Add(new GameAction
                {
                    UT = startUT,
                    Type = GameActionType.KerbalAssignment,
                    RecordingId = recordingId,
                    KerbalName = member.Name,
                    KerbalRole = member.Role,
                    StartUT = (float)startUT,
                    EndUT = (float)endUT,
                    KerbalEndStateField = member.EndState,
                    Sequence = i + 1
                });
            }

            if (result.Count > 0)
            {
                ParsekLog.Info(Tag,
                    $"CreateKerbalAssignmentActions: {result.Count} crew members from '{recordingId}'");
            }

            return result;
        }

        /// <summary>
        /// Holds extracted crew member info for KerbalAssignment action creation.
        /// </summary>
        internal struct CrewInfo
        {
            public string Name;
            public string Role;
            public KerbalEndState EndState;
        }

        private static bool ShouldTrackKerbalRole(string role)
        {
            return !string.Equals(role, "Tourist", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts crew members from a recording's vessel snapshot data,
        /// mirroring the crew extraction logic in KerbalsModule.ProcessAction.
        /// Uses GhostVisualSnapshot (recording-start state) as the primary source
        /// for crew names (same as PopulateCrewEndStates), falls back to VesselSnapshot.
        /// Stand-in names are reverse-mapped back to original slot owners so
        /// KerbalAssignment actions use the same identity as CrewEndStates.
        /// Roles come from KerbalsModule.FindTraitForKerbal (KSP roster lookup).
        /// End states come from rec.CrewEndStates if populated.
        /// </summary>
        internal static List<CrewInfo> ExtractCrewFromRecording(Recording rec)
        {
            var result = new List<CrewInfo>();
            if (rec == null) return result;

            // Prefer GhostVisualSnapshot (recording-start crew), fall back to VesselSnapshot
            var snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
            var names = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            // EVA fallback: snapshot crew extraction returns empty for EVA vessels
            if (names.Count == 0 && !string.IsNullOrEmpty(rec.EvaCrewName))
                names.Add(rec.EvaCrewName);
            if (names.Count == 0) return result;

            // Match PopulateCrewEndStates: later recordings may already contain stand-in
            // names after a prior commit swapped live vessel crew.
            KerbalsModule.ReverseMapCrewNames(
                names,
                CrewReservationManager.CrewReplacements,
                null);

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                string role = KerbalsModule.FindTraitForKerbal(name);
                if (!ShouldTrackKerbalRole(role))
                    continue;

                KerbalEndState endState = KerbalEndState.Unknown;
                if (rec.CrewEndStates != null)
                    rec.CrewEndStates.TryGetValue(name, out endState);

                result.Add(new CrewInfo
                {
                    Name = name,
                    Role = role,
                    EndState = endState
                });
            }

            return result;
        }

        private static bool NeedsCrewEndStatePopulation(Recording rec)
        {
            return rec != null
                && !rec.CrewEndStatesResolved
                && rec.CrewEndStates == null
                && (rec.VesselSnapshot != null
                    || !string.IsNullOrEmpty(rec.EvaCrewName)
                    || KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(rec));
        }

        /// <summary>
        /// #432: removes every action in <see cref="Ledger.Actions"/> whose <c>RecordingId</c>
        /// belongs to a ghost-only (Gloops) recording. Mutates the ledger in place so raw-ledger
        /// consumers (Timeline, career-state views) see a clean list — filtering only the walk
        /// copy would leave stale rows visible elsewhere. Action-creation guards
        /// (<see cref="CreateKerbalAssignmentActions"/>, <see cref="CreateVesselCostActions"/>)
        /// already prevent new ghost-only rows from being produced; this catches pre-fix saves
        /// and any future regression that deposits a ghost-only-tagged action via some other
        /// path. Empty-<c>RecordingId</c> actions (InitialFunds / InitialScience / InitialReputation
        /// seeds, KSC-spending-forwarded rows, and <see cref="MigrateOldSaveEvents"/> output) are
        /// preserved — <see cref="Ledger.RemoveActionsForRecording"/> keys strictly on non-empty ids.
        /// Returns the number of actions removed.
        /// </summary>
        internal static int PurgeGhostOnlyActionsFromLedger()
        {
            var recs = RecordingStore.CommittedRecordings;
            if (recs == null || recs.Count == 0) return 0;

            var ghostOnlyIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (r != null && r.IsGhostOnly && !string.IsNullOrEmpty(r.RecordingId))
                    ghostOnlyIds.Add(r.RecordingId);
            }

            if (ghostOnlyIds.Count == 0) return 0;

            int removedTotal = 0;
            foreach (var id in ghostOnlyIds)
                removedTotal += Ledger.RemoveActionsForRecording(id);

            if (removedTotal > 0)
                ParsekLog.Info(Tag,
                    $"PurgeGhostOnlyActionsFromLedger: removed {removedTotal} action(s) tagged with ghost-only recordings");

            return removedTotal;
        }

        /// <summary>
        /// Finds a committed recording by its ID. Returns null if not found.
        /// </summary>
        internal static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;

            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return null;

            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].RecordingId == recordingId)
                    return recordings[i];
            }

            return null;
        }

        /// <summary>
        /// Full recalculation from ledger to engine to KSP state patch.
        /// Called on: commit, rewind, warp exit, KSP load.
        /// </summary>
        /// <param name="utCutoff">
        /// Optional UT cutoff forwarded to <see cref="RecalculationEngine.Recalculate"/>.
        /// When non-null, actions with <c>UT &gt; utCutoff</c> are excluded from the walk
        /// (seed actions always survive). Callers that are not rewind-driven pass no
        /// argument and get <c>null</c> — the default walk-everything behavior. The
        /// rewind paths (<c>HandleRewindOnLoad</c> + <c>ApplyRewindResourceAdjustment</c>)
        /// pass the adjusted rewind UT explicitly; <c>RecalculateAndPatch</c> never
        /// consults <see cref="RewindContext"/> itself, so callers must supply the
        /// cutoff at the call site before <see cref="RewindContext.EndRewind"/> clears
        /// the global.
        /// </param>
        internal static void RecalculateAndPatch(double? utCutoff = null)
        {
            RecalculateAndPatchCore(
                utCutoff,
                bypassPatchDeferral: utCutoff.HasValue,
                authoritativeRepeatableRecordState: utCutoff.HasValue);
        }

        /// <summary>
        /// Post-rewind FLIGHT-load follow-up recalculation that filters the walk to
        /// the currently loaded UT without opting into rewind-only patch side
        /// effects. Unlike the normal explicit-cutoff path, this preserves
        /// pending/live-tree patch deferral and same-branch repeatable-record
        /// preservation.
        /// </summary>
        internal static void RecalculateAndPatchForPostRewindFlightLoad(double utCutoff)
        {
            RecalculateAndPatchCore(
                utCutoff,
                bypassPatchDeferral: false,
                authoritativeRepeatableRecordState: false);
        }

        private const double InitialResourceBaselineMaxUtSeconds = 1.0;

        private static void SeedInitialResourceBalances()
        {
            GameStateBaseline initialBaseline;
            bool hasInitialBaseline = TryGetInitialResourceBaseline(out initialBaseline);

            if (!fundsSeedDone)
                fundsSeedDone = EnsureInitialFundsSeed(hasInitialBaseline, initialBaseline);
            if (!scienceSeedDone)
                scienceSeedDone = EnsureInitialScienceSeed(hasInitialBaseline, initialBaseline);
            if (!repSeedDone)
                repSeedDone = EnsureInitialReputationSeed(hasInitialBaseline, initialBaseline);
        }

        private static bool EnsureInitialFundsSeed(bool hasInitialBaseline, GameStateBaseline initialBaseline)
        {
            GameAction existingSeed;
            if (TryGetFundsSeed(out existingSeed) && existingSeed.InitialFunds != 0f)
                return true;

            if (hasInitialBaseline)
            {
                if (initialBaseline.funds != 0.0)
                {
                    Ledger.SeedInitialFunds(initialBaseline.funds);
                    return true;
                }

                ParsekLog.Verbose(Tag,
                    "SeedInitialFunds: initial baseline has zero funds; waiting for a non-zero Funding seed");
            }

            if (Funding.Instance != null && Funding.Instance.Funds != 0.0)
            {
                Ledger.SeedInitialFunds(Funding.Instance.Funds);
                return true;
            }

            return false;
        }

        private static bool TryGetFundsSeed(out GameAction seed)
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action != null && action.Type == GameActionType.FundsInitial)
                {
                    seed = action;
                    return true;
                }
            }

            seed = null;
            return false;
        }

        private static bool EnsureInitialScienceSeed(bool hasInitialBaseline, GameStateBaseline initialBaseline)
        {
            if (LedgerHasSeed(GameActionType.ScienceInitial))
                return true;

            if (hasInitialBaseline)
            {
                Ledger.SeedInitialScience((float)initialBaseline.science);
                return true;
            }

            if (LedgerHasScienceTimelineActions())
            {
                Ledger.SeedInitialScience(0f);
                ParsekLog.Warn(Tag,
                    "SeedInitialScience: refusing to treat current science as initial " +
                    "because science timeline actions already exist and no baseline was available");
                return true;
            }

            if (ResearchAndDevelopment.Instance == null
                || ResearchAndDevelopment.Instance.Science == 0f)
                return false;

            Ledger.SeedInitialScience(ResearchAndDevelopment.Instance.Science);
            return true;
        }

        private static bool EnsureInitialReputationSeed(bool hasInitialBaseline, GameStateBaseline initialBaseline)
        {
            if (LedgerHasSeed(GameActionType.ReputationInitial))
                return true;

            if (hasInitialBaseline)
            {
                Ledger.SeedInitialReputation(initialBaseline.reputation);
                return true;
            }

            if (LedgerHasReputationTimelineActions())
            {
                Ledger.SeedInitialReputation(0f);
                ParsekLog.Warn(Tag,
                    "SeedInitialReputation: refusing to treat current reputation as initial " +
                    "because reputation timeline actions already exist and no baseline was available");
                return true;
            }

            if (global::Reputation.Instance == null
                || Math.Abs(global::Reputation.Instance.reputation) <= 0.01f)
                return false;

            Ledger.SeedInitialReputation(global::Reputation.Instance.reputation);
            return true;
        }

        private static bool TryGetInitialResourceBaseline(out GameStateBaseline initialBaseline)
        {
            initialBaseline = null;

            var baselines = GameStateStore.Baselines;
            if (baselines == null || baselines.Count == 0)
                return false;

            for (int i = 0; i < baselines.Count; i++)
            {
                var candidate = baselines[i];
                if (candidate == null)
                    continue;

                if (!IsInitialResourceBaseline(candidate))
                    continue;

                if (initialBaseline == null || candidate.ut < initialBaseline.ut)
                    initialBaseline = candidate;
            }

            return initialBaseline != null;
        }

        private static bool IsInitialResourceBaseline(GameStateBaseline candidate)
        {
            if (candidate == null)
                return false;

            return candidate.ut >= -InitialResourceBaselineMaxUtSeconds
                && candidate.ut <= InitialResourceBaselineMaxUtSeconds;
        }

        private static bool LedgerHasSeed(GameActionType seedType)
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action != null && action.Type == seedType)
                    return true;
            }

            return false;
        }

        private static bool LedgerHasScienceTimelineActions()
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (ActionTouchesScienceBudget(action))
                    return true;
            }

            return false;
        }

        private static bool LedgerHasReputationTimelineActions()
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (ActionTouchesReputationBudget(action))
                    return true;
            }

            return false;
        }

        private static bool ActionTouchesScienceBudget(GameAction action)
        {
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceSpending:
                    return true;
                case GameActionType.ContractComplete:
                    return action.ScienceReward != 0f || action.TransformedScienceReward != 0f;
                case GameActionType.MilestoneAchievement:
                    return action.MilestoneScienceAwarded != 0f;
                case GameActionType.StrategyActivate:
                    return action.SetupScienceCost != 0f;
                default:
                    return false;
            }
        }

        private static bool ActionTouchesReputationBudget(GameAction action)
        {
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    return true;
                case GameActionType.ContractAccept:
                    return action.RepPenalty != 0f;
                case GameActionType.ContractComplete:
                    return action.RepReward != 0f || action.TransformedRepReward != 0f;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return action.RepPenalty != 0f;
                case GameActionType.MilestoneAchievement:
                    return action.MilestoneRepAwarded != 0f;
                case GameActionType.StrategyActivate:
                    return action.SetupReputationCost != 0f;
                default:
                    return false;
            }
        }

        private static void RecalculateAndPatchCore(
            double? utCutoff,
            bool bypassPatchDeferral,
            bool authoritativeRepeatableRecordState)
        {
            Initialize();

            // Seed initial balances for career mode (per-resource, idempotent).
            // Baselines can represent legitimate zero science/rep values; once such
            // a seed exists it must not be upgraded later from future live state.
            SeedInitialResourceBalances();

            // Update contract and strategy slot limits based on facility levels
            // from the previous recalculation walk. On the very first call, defaults
            // (2 contracts / 1 strategy) apply — correct for unupgraded buildings.
            // Subsequent calls use facility state derived from the prior walk,
            // so slot limits are always one walk behind facility upgrades. This is
            // acceptable because RecalculateAndPatch runs on every trigger (commit,
            // rewind, warp exit, load), converging within two calls at most.
            UpdateSlotLimitsFromFacilities();

            // End-state population safety net: catch recordings with unpopulated end states
            PopulateUnpopulatedCrewEndStates();

            // #432: purge any ghost-only-tagged actions from the ledger before the walk.
            // Mutates Ledger.Actions directly so raw-ledger consumers (Timeline window,
            // career-state views) never see stale rows — filtering the walk copy alone
            // would leave Ledger.Actions dirty. Idempotent — no-op when no ghost-only
            // recordings exist or no actions are tagged with them. CreateKerbalAssignment-
            // Actions / CreateVesselCostActions already skip ghost-only recordings; this
            // catches pre-fix saves and any future regression that deposits a ghost-only-
            // tagged action via some other path.
            PurgeGhostOnlyActionsFromLedger();

            var actions = BuildRecalculationActions();
            LogRecalculationInputSummary(actions, utCutoff);

            RecalculationEngine.Recalculate(actions, utCutoff);

            // #440 Phase E2: post-walk reconciliation for strategy-transformed
            // and curve-applied reward types. Runs once per walk, log-only,
            // after derived fields (Transformed*Reward/EffectiveRep/
            // EffectiveScience) are populated and before the later KSP patch/defer
            // branch decides whether this walk mutates live game state.
            ReconcilePostWalk(GameStateStore.Events, actions, utCutoff);

            // #466: a live or pending tree means KSP's mutable state may already include
            // uncommitted in-flight effects that the committed-only ledger cannot see yet.
            // Walking the committed ledger is still useful, but writing that partial state
            // back into KSP is destructive. Rewind-style cutoff walks remain authoritative.
            string patchDeferralReason = bypassPatchDeferral
                ? null
                : GetKspPatchDeferralReason();
            if (!string.IsNullOrEmpty(patchDeferralReason))
            {
                ParsekLog.Verbose(Tag,
                    $"RecalculateAndPatch: deferred KSP state patch ({patchDeferralReason})");
            }
            else
            {
                ApplyRecalculatedStateToKsp(
                    actions,
                    utCutoff,
                    authoritativeRepeatableRecordState);
            }

            // #391: rebuild committedScienceSubjects from the walk's authoritative
            // per-subject credits. This prunes stale entries left behind when a
            // recording was deleted (the old dictionary was only ever appended to,
            // never pruned). Without this, TryRecoverBrokenLedgerOnLoad would
            // synthesize ghost ScienceEarning actions for the stale subjects.
            RebuildCommittedScienceFromWalk();

            string completeStateKey = string.Format(
                CultureInfo.InvariantCulture,
                "actions={0}|cutoff={1}|deferred={2}|repeatable={3}",
                actions.Count,
                utCutoff.HasValue ? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture) : "null",
                string.IsNullOrEmpty(patchDeferralReason) ? "none" : patchDeferralReason,
                authoritativeRepeatableRecordState ? "authoritative" : "preserve-live");
            ParsekLog.VerboseOnChange(Tag,
                "recalculate-and-patch-complete",
                completeStateKey,
                $"RecalculateAndPatch complete: {actions.Count} actions walked");

            OnTimelineDataChanged?.Invoke();
        }

        private static List<GameAction> BuildRecalculationActions()
        {
            // Phase 9 of Rewind-to-Staging (design §3.2): route the walk input
            // through the Effective Ledger Set so any tombstoned action is
            // excluded. Tombstones are the only filter — the recording-level
            // filter (v0.4) was explicitly dropped in v0.5's narrow-supersede
            // design, and ComputeELS implements exactly that rule. Pre-Phase-9
            // behavior (Ledger.Actions raw) matches the Phase 9 output on a
            // save with no tombstones (trivially), so this is a drop-in for
            // existing behavior plus the new tombstone filter.
            //
            // NOTE: Ghost-only actions are purged from Ledger.Actions above,
            // so ComputeELS (which derives from Ledger.Actions) correctly
            // reflects the post-purge state.
            return new List<GameAction>(EffectiveState.ComputeELS());
        }

        private static void LogRecalculationInputSummary(
            List<GameAction> actions,
            double? utCutoff)
        {
            // Count how many actions survive the cutoff filter for the log summary.
            // Matches the filter in RecalculationEngine.Recalculate (seeds always pass).
            int actionsAfterCutoff = actions.Count;
            if (utCutoff.HasValue)
            {
                double cutoff = utCutoff.Value;
                actionsAfterCutoff = 0;
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null) continue;
                    if (RecalculationEngine.IsSeedType(a.Type) || a.UT <= cutoff)
                        actionsAfterCutoff++;
                }
            }

            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture)
                : "null";
            string stateKey = string.Format(
                CultureInfo.InvariantCulture,
                "total={0}|after={1}|cutoff={2}",
                actions.Count,
                actionsAfterCutoff,
                cutoffLabel);
            string message =
                $"RecalculateAndPatch: actionsTotal={actions.Count}, " +
                $"actionsAfterCutoff={actionsAfterCutoff}, cutoffUT={cutoffLabel}";
            if (utCutoff.HasValue)
            {
                ParsekLog.Verbose(Tag, message);
            }
            else
            {
                ParsekLog.VerboseOnChange(Tag,
                    "recalculate-and-patch-input",
                    stateKey,
                    message);
            }
        }

        private static void ApplyRecalculatedStateToKsp(
            List<GameAction> actions,
            double? utCutoff,
            bool authoritativeRepeatableRecordState)
        {
            // KSP state mutations (PostWalk already called by engine). Repeatable
            // Records* nodes only rebuild strictly from ledger-backed thresholds on
            // rewind-style cutoff walks; normal same-branch recalculations preserve the
            // live in-tier best value because that finer-grained partial progress is not
            // persisted in the ledger.
            kerbalsModule.ApplyToRoster(HighLogic.CurrentGame?.CrewRoster);
            // #559: tech-tree patching is rewind-only. Live unlocks that happened after
            // the latest captured baseline are not replayed by any ledger action, so a
            // non-rewind (utCutoff == null) patch would clobber them. Only build and pass
            // the target tech set when a cutoff is supplied; the null branch no-ops
            // through PatchTechTree's existing null-target guard.
            HashSet<string> targetTechIds = null;
            double? techBaselineUt = null;
            if (utCutoff.HasValue)
            {
                targetTechIds = KspStatePatcher.BuildTargetTechIdsForPatch(
                    GameStateStore.Baselines,
                    actions,
                    utCutoff);
                techBaselineUt = KspStatePatcher.GetSelectedTechBaselineUt(
                    GameStateStore.Baselines,
                    utCutoff);
                ParsekLog.Verbose(Tag,
                    "RecalculateAndPatch: rewind-path tech-tree patch enabled " +
                    $"(utCutoff={utCutoff.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"baselineUt={(techBaselineUt.HasValue ? techBaselineUt.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "null")}, " +
                    $"targetCount={(targetTechIds == null ? "null" : targetTechIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))})");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    "RecalculateAndPatch: no cutoff supplied — skipping tech-tree patch to preserve live unlocks");
            }

            KspStatePatcher.PatchAll(scienceModule, fundsModule, reputationModule,
                milestonesModule, facilitiesModule, contractsModule,
                targetTechIds,
                authoritativeRepeatableRecordState: authoritativeRepeatableRecordState,
                techUtCutoff: utCutoff,
                techBaselineUt: techBaselineUt);
        }

        internal static bool HasActionsAfterUT(double ut)
        {
            for (int i = 0; i < Ledger.Actions.Count; i++)
            {
                var action = Ledger.Actions[i];
                if (action != null && action.UT > ut)
                    return true;
            }

            return false;
        }

        private static string GetKspPatchDeferralReason()
        {
            bool hasActiveUncommittedTree = GameStateRecorder.HasActiveUncommittedTree();
            bool hasLiveRecorder = GameStateRecorder.HasLiveRecorder();
            bool hasPendingTree = RecordingStore.HasPendingTree;
            if (!hasActiveUncommittedTree && !hasPendingTree)
                return null;

            string reason = null;
            if (hasLiveRecorder)
                reason = "live recorder active";
            else if (hasActiveUncommittedTree)
                reason = "active uncommitted flight tree";

            if (hasPendingTree)
            {
                string treeName = RecordingStore.PendingTree?.TreeName;
                string pendingReason = string.IsNullOrEmpty(treeName)
                    ? $"pending tree state={RecordingStore.PendingTreeStateValue}"
                    : $"pending tree '{treeName}' state={RecordingStore.PendingTreeStateValue}";
                reason = string.IsNullOrEmpty(reason)
                    ? pendingReason
                    : reason + "; " + pendingReason;
            }

            return reason;
        }

        /// <summary>
        /// Rebuilds the committed science subjects dictionary via
        /// <see cref="GameStateStore.RebuildCommittedScienceSubjects"/>
        /// from the <see cref="ScienceModule"/> walk state. After a recalculation
        /// walk, the module has authoritative per-subject credited totals — these
        /// are the source of truth because they derive purely from surviving ledger
        /// actions. Replaces the stale append-only dictionary.
        /// </summary>
        private static void RebuildCommittedScienceFromWalk()
        {
            if (scienceModule == null) return;

            var walkSubjects = scienceModule.GetAllSubjects();
            var pairs = new List<KeyValuePair<string, float>>(walkSubjects.Count);
            foreach (var kvp in walkSubjects)
            {
                if (kvp.Value.CreditedTotal > 0.0)
                    pairs.Add(new KeyValuePair<string, float>(kvp.Key, (float)kvp.Value.CreditedTotal));
            }

            GameStateStore.RebuildCommittedScienceSubjects(pairs);
        }

        /// <summary>
        /// Called on KSP load. Reconcile ledger against current save state,
        /// then recalculate and patch.
        /// </summary>
        /// <param name="validRecordingIds">Set of recording IDs present in the loaded save.</param>
        /// <param name="maxUT">Current UT — spending actions after this are pruned.</param>
        internal static void OnKspLoad(HashSet<string> validRecordingIds, double maxUT)
        {
            Initialize();

            // Reset the per-load MigrateOldSaveEvents flag — only this load's
            // MigrateOldSaveEvents synthetics should gate the null-tag coverage rule.
            // Previous loads' TryRecoverBrokenLedgerOnLoad synthetics (persisted in the
            // ledger file) must NOT falsely register as "MigrateOldSaveEvents output".
            ResetMigrateOldSaveEventsForLoad();
            LedgerRecoveryFundsPairing.ClearConsumedRecoveryEventKeys();
            // Log-before-clear so any stale entries from the previous session show up
            // in KSP.log before we drop them. Matches the FlushStalePendingRecoveryFunds
            // contract used at scene-switch / rewind-end boundaries.
            FlushStalePendingRecoveryFunds("KSP load");

            // Reconcile first — prunes orphaned actions from the existing ledger.
            // On empty ledger (old save), this is a no-op.
            Ledger.Reconcile(validRecordingIds, maxUT);

            // Compatibility repair for early #444 saves written before recovery
            // FundsEarning.DedupKey was serialized. Rebuild the missing key from the
            // loaded FundsChanged(VesselRecovery) event so replayed recoveries cannot
            // double-credit after the per-session consumed set is cleared on load.
            RepairMissingRecoveryDedupKeys();

            // Migration: if ledger is still empty after reconcile but committed recordings exist,
            // this is an old save being loaded for the first time with the ledger system.
            // Convert existing GameStateStore events into GameActions.
            // Done AFTER reconcile so migrated actions are not pruned (they have null recordingId
            // which would be pruned for earning types if reconcile ran after).
            if (Ledger.Actions.Count == 0 && validRecordingIds != null && validRecordingIds.Count > 0)
            {
                MigrateOldSaveEvents(validRecordingIds);
            }

            // Migration: ensure all committed recordings have KerbalAssignment actions.
            // Old saves predating the KerbalAssignment feature have recordings but no
            // kerbal actions in the ledger.
            MigrateKerbalAssignments();

            // Phase A migration: pre-existing committed trees whose save data still
            // carries legacy resource residual fields get those residuals injected as
            // synthetic ledger actions tagged with the tree's RootRecordingId. This
            // runs AFTER Reconcile and BEFORE RecalculateAndPatch so the synthesized
            // actions enter the same walk that patches KSP state.
            MigrateLegacyTreeResources();

            // #401/#396 one-shot save recovery now runs here, after committed recordings
            // (and any cold-start pending active tree) have been loaded. That gives the
            // post-epoch visibility filter an authoritative recording-id scope and keeps
            // old hidden branch events out of the synthesized ledger rows for the right
            // reason: their recording ids are no longer part of the current timeline.
            try
            {
                TryRecoverBrokenLedgerOnLoad();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"TryRecoverBrokenLedgerOnLoad failed during OnKspLoad: {ex.GetType().Name}: {ex.Message}");
            }

            RecalculateAndPatch();
            KerbalLoadRepairDiagnostics.EmitAndReset();
        }

        private static void MigrateOldSaveEvents(HashSet<string> validRecordingIds)
        {
            LedgerLoadMigration.MigrateOldSaveEvents(validRecordingIds);
        }
        /// <summary>
        /// Migration: ensures all committed recordings have KerbalAssignment actions
        /// in the ledger. Old saves predating the feature have recordings but no kerbal
        /// actions. Generates them from vessel snapshot crew data.
        /// </summary>
        private static void MigrateKerbalAssignments()
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null || recordings.Count == 0) return;

            var existingByRecording = new Dictionary<string, List<GameAction>>();
            var ledgerActions = Ledger.Actions;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (action.Type != GameActionType.KerbalAssignment
                    || string.IsNullOrEmpty(action.RecordingId))
                    continue;

                List<GameAction> existing;
                if (!existingByRecording.TryGetValue(action.RecordingId, out existing))
                {
                    existing = new List<GameAction>();
                    existingByRecording[action.RecordingId] = existing;
                }

                existing.Add(action);
            }

            int repairedRecordings = 0;
            int oldRows = 0;
            int newRows = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];

                var kerbalActions = CreateKerbalAssignmentActions(
                    rec.RecordingId, rec.StartUT, rec.EndUT);
                List<GameAction> existing;
                existingByRecording.TryGetValue(rec.RecordingId, out existing);
                if (KerbalAssignmentActionsMatch(existing, kerbalActions))
                    continue;

                var repairStats = ClassifyKerbalAssignmentRepair(existing, kerbalActions);

                Ledger.ReplaceActionsForRecording(
                    GameActionType.KerbalAssignment, rec.RecordingId, kerbalActions);
                repairedRecordings++;
                oldRows += existing != null ? existing.Count : 0;
                newRows += kerbalActions.Count;
                KerbalLoadRepairDiagnostics.RecordMigrationRepair(
                    1,
                    existing != null ? existing.Count : 0,
                    kerbalActions.Count);
                KerbalLoadRepairDiagnostics.RecordTouristRowsSkipped(
                    rec.RecordingId, repairStats.TouristRowsSkipped);

                for (int s = 0; s < repairStats.RemappedRows.Count; s++)
                {
                    var remap = repairStats.RemappedRows[s];
                    KerbalLoadRepairDiagnostics.RecordRemappedRow(
                        rec.RecordingId, remap.FromName, remap.ToName);
                }

                for (int s = 0; s < repairStats.EndStateRewrites.Count; s++)
                {
                    var rewrite = repairStats.EndStateRewrites[s];
                    KerbalLoadRepairDiagnostics.RecordEndStateRewrite(
                        rec.RecordingId, rewrite.KerbalName, rewrite.FromState, rewrite.ToState);
                }
            }

            if (repairedRecordings > 0)
                ParsekLog.Info(Tag,
                    $"MigrateKerbalAssignments: repaired {repairedRecordings} recording(s) " +
                    $"(oldRows={oldRows}, newRows={newRows})");
        }

        // ================================================================
        // Phase A: legacy tree-resource residual migration (zero-coverage scope)
        // ================================================================

        internal static void MigrateLegacyTreeResources()
        {
            LedgerLoadMigration.MigrateLegacyTreeResources();
        }

        internal static bool IsResourceImpactingAction(GameActionType t)
        {
            return LedgerLoadMigration.IsResourceImpactingAction(t);
        }
        private struct KerbalAssignmentRepairRename
        {
            public string FromName;
            public string ToName;
        }

        private struct KerbalAssignmentEndStateRewrite
        {
            public string KerbalName;
            public KerbalEndState FromState;
            public KerbalEndState ToState;
        }

        private sealed class KerbalAssignmentRepairStats
        {
            public readonly List<KerbalAssignmentRepairRename> RemappedRows
                = new List<KerbalAssignmentRepairRename>();
            public readonly List<KerbalAssignmentEndStateRewrite> EndStateRewrites
                = new List<KerbalAssignmentEndStateRewrite>();
            public int TouristRowsSkipped;
        }

        private static KerbalAssignmentRepairStats ClassifyKerbalAssignmentRepair(
            List<GameAction> existing,
            List<GameAction> desired)
        {
            var stats = new KerbalAssignmentRepairStats();
            if (existing == null || existing.Count == 0)
                return stats;

            var filteredExisting = new List<GameAction>();
            for (int i = 0; i < existing.Count; i++)
            {
                var existingAction = existing[i];
                if (existingAction == null)
                    continue;

                if (string.Equals(existingAction.KerbalRole, "Tourist",
                    StringComparison.OrdinalIgnoreCase))
                {
                    stats.TouristRowsSkipped++;
                    continue;
                }

                filteredExisting.Add(existingAction);
            }

            if (desired == null || desired.Count == 0 || filteredExisting.Count == 0)
                return stats;

            var matchedExisting = new bool[filteredExisting.Count];
            var matchedDesired = new bool[desired.Count];

            // Reorder-only repairs should not log false remap samples. Match exact-name rows
            // first, then classify the remaining unmatched rows as true remaps/rewrites.
            for (int i = 0; i < desired.Count; i++)
            {
                var desiredAction = desired[i];
                int existingIndex = FindRepairRowMatch(
                    filteredExisting, matchedExisting, desiredAction, requireSameName: true);
                if (existingIndex < 0)
                    continue;

                matchedExisting[existingIndex] = true;
                matchedDesired[i] = true;

                var existingAction = filteredExisting[existingIndex];
                if (existingAction.KerbalEndStateField != desiredAction.KerbalEndStateField)
                {
                    stats.EndStateRewrites.Add(new KerbalAssignmentEndStateRewrite
                    {
                        KerbalName = desiredAction.KerbalName,
                        FromState = existingAction.KerbalEndStateField,
                        ToState = desiredAction.KerbalEndStateField
                    });
                }
            }

            for (int i = 0; i < desired.Count; i++)
            {
                if (matchedDesired[i])
                    continue;

                var desiredAction = desired[i];
                int existingIndex = FindRepairRowMatch(
                    filteredExisting, matchedExisting, desiredAction, requireSameName: false);
                if (existingIndex < 0)
                    continue;

                matchedExisting[existingIndex] = true;
                matchedDesired[i] = true;

                var existingAction = filteredExisting[existingIndex];
                if (!string.Equals(existingAction.KerbalName, desiredAction.KerbalName,
                    StringComparison.Ordinal))
                {
                    stats.RemappedRows.Add(new KerbalAssignmentRepairRename
                    {
                        FromName = existingAction.KerbalName,
                        ToName = desiredAction.KerbalName
                    });
                }

                if (existingAction.KerbalEndStateField != desiredAction.KerbalEndStateField)
                {
                    stats.EndStateRewrites.Add(new KerbalAssignmentEndStateRewrite
                    {
                        KerbalName = desiredAction.KerbalName,
                        FromState = existingAction.KerbalEndStateField,
                        ToState = desiredAction.KerbalEndStateField
                    });
                }
            }

            return stats;
        }

        private static int FindRepairRowMatch(
            List<GameAction> existing,
            bool[] matchedExisting,
            GameAction desiredAction,
            bool requireSameName)
        {
            if (existing == null || matchedExisting == null || desiredAction == null)
                return -1;

            for (int i = 0; i < existing.Count; i++)
            {
                if (matchedExisting[i])
                    continue;

                var existingAction = existing[i];
                if (existingAction == null)
                    continue;
                if (!string.Equals(existingAction.RecordingId, desiredAction.RecordingId,
                    StringComparison.Ordinal))
                    continue;
                if (!string.Equals(existingAction.KerbalRole, desiredAction.KerbalRole,
                    StringComparison.Ordinal))
                    continue;
                if (Math.Abs(existingAction.UT - desiredAction.UT) > 0.1)
                    continue;
                if (Math.Abs(existingAction.StartUT - desiredAction.StartUT) > 0.1f)
                    continue;
                if (Math.Abs(existingAction.EndUT - desiredAction.EndUT) > 0.1f)
                    continue;
                if (requireSameName
                    && !string.Equals(existingAction.KerbalName, desiredAction.KerbalName,
                        StringComparison.Ordinal))
                    continue;

                return i;
            }

            return -1;
        }

        private static bool KerbalAssignmentActionsMatch(
            List<GameAction> existing, List<GameAction> desired)
        {
            int existingCount = existing != null ? existing.Count : 0;
            int desiredCount = desired != null ? desired.Count : 0;
            if (existingCount != desiredCount)
                return false;

            for (int i = 0; i < existingCount; i++)
            {
                var a = existing[i];
                var b = desired[i];
                if (a == null || b == null)
                    return false;
                if (a.Type != b.Type)
                    return false;
                if (Math.Abs(a.UT - b.UT) > 0.1)
                    return false;
                if (!string.Equals(a.RecordingId, b.RecordingId, StringComparison.Ordinal))
                    return false;
                if (!string.Equals(a.KerbalName, b.KerbalName, StringComparison.Ordinal))
                    return false;
                if (!string.Equals(a.KerbalRole, b.KerbalRole, StringComparison.Ordinal))
                    return false;
                if (Math.Abs(a.StartUT - b.StartUT) > 0.1f)
                    return false;
                if (Math.Abs(a.EndUT - b.EndUT) > 0.1f)
                    return false;
                if (a.KerbalEndStateField != b.KerbalEndStateField)
                    return false;
                if (a.Sequence != b.Sequence)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Populates CrewEndStates on committed recordings that don't have them yet.
        /// Safety net for recordings whose end states weren't populated at commit time.
        /// </summary>
        private static void PopulateUnpopulatedCrewEndStates()
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return;

            int populated = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (NeedsCrewEndStatePopulation(rec))
                {
                    KerbalsModule.PopulateCrewEndStates(rec);
                    if (rec.CrewEndStates != null) populated++;
                }
            }
            if (populated > 0)
                ParsekLog.Verbose(Tag,
                    $"PopulateUnpopulatedCrewEndStates: populated {populated} crew end states");
        }

        /// <summary>
        /// Called on save. Persist ledger to file.
        /// </summary>
        internal static void OnSave()
        {
            string path = Ledger.GetLedgerPath();
            if (!string.IsNullOrEmpty(path))
            {
                Ledger.SaveToFile(path);
                ParsekLog.Verbose(Tag, $"OnSave: ledger saved to '{path}'");
            }
            else
            {
                ParsekLog.Warn(Tag, "OnSave: could not resolve ledger path — skipping save");
            }
        }

        /// <summary>
        /// Called on load. Load ledger from file.
        /// </summary>
        internal static void OnLoad()
        {
            Initialize();

            // Reset per-resource seeding flags so initial balances are re-captured
            // for this save. Without this, switching from a sandbox save to a career
            // save would skip seeding entirely.
            fundsSeedDone = false;
            scienceSeedDone = false;
            repSeedDone = false;

            string path = Ledger.GetLedgerPath();
            if (!string.IsNullOrEmpty(path))
            {
                Ledger.LoadFromFile(path);
                int repairedLegacyPartPurchaseEvents =
                    GameStateStore.RepairLegacyPartPurchaseEventsForCurrentSemantics();
                int repairedLegacyPartPurchaseActions =
                    RepairLegacyPartPurchaseActionsOnLoad(GameStateStore.Events, Ledger.Actions);
                if (repairedLegacyPartPurchaseEvents > 0 || repairedLegacyPartPurchaseActions > 0)
                {
                    ParsekLog.Info(Tag,
                        $"OnLoad: repaired legacy R&D part-purchase rows " +
                        $"(events={repairedLegacyPartPurchaseEvents}, actions={repairedLegacyPartPurchaseActions})");
                }
                ParsekLog.Verbose(Tag, $"OnLoad: ledger loaded from '{path}'");
            }
            else
            {
                ParsekLog.Warn(Tag, "OnLoad: could not resolve ledger path — starting with empty ledger");
            }
        }

        internal static int RepairLegacyPartPurchaseActionsOnLoad(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions)
        {
            return LedgerLoadMigration.RepairLegacyPartPurchaseActionsOnLoad(events, ledgerActions);
        }
        internal static int TryRecoverBrokenLedgerOnLoad()
        {
            return LedgerLoadMigration.TryRecoverBrokenLedgerOnLoad();
        }

        internal static bool IsRecoverableEventType(GameStateEventType t)
        {
            return LedgerLoadMigration.IsRecoverableEventType(t);
        }

        internal static bool LedgerHasMatchingAction(GameStateEvent evt)
        {
            return LedgerLoadMigration.LedgerHasMatchingAction(evt);
        }

        internal static bool LedgerHasMatchingScienceEarning(string subjectId, float minScience)
        {
            return LedgerLoadMigration.LedgerHasMatchingScienceEarning(subjectId, minScience);
        }

        internal static float GetLedgerScienceEarningTotal(string subjectId)
        {
            return LedgerLoadMigration.GetLedgerScienceEarningTotal(subjectId);
        }

        internal static GameActionType? MapEventTypeToActionType(GameStateEventType t)
        {
            return LedgerLoadMigration.MapEventTypeToActionType(t);
        }
        /// <summary>
        /// Called when a KSC spending action occurs outside of a flight recording session.
        /// Converts the event directly to a GameAction and adds to the ledger, then recalculates.
        /// Used for tech unlocks, facility upgrades, and kerbal hires at KSC.
        /// </summary>
        /// <param name="evt">The GameStateEvent captured by GameStateRecorder.</param>
        internal static void OnKscSpending(GameStateEvent evt)
        {
            Initialize();

            var action = GameStateEventConverter.ConvertEvent(evt, null);
            if (action == null)
            {
                ParsekLog.Verbose(Tag,
                    $"OnKscSpending: event type {evt.eventType} produced no action, skipping");
                return;
            }

            // Assign a sequence number so multiple KSC events at the same UT have
            // deterministic ordering in the recalculation sort. (See also
            // OnVesselRolloutSpending, which follows this same pattern.)
            kscSequenceCounter++;
            action.Sequence = kscSequenceCounter;

            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"KSC spending recorded: type={action.Type}, UT={evt.ut:F1}, " +
                $"key={evt.key ?? "(none)"}");

            // Phase B (plan: fix-ledger-lump-sum-reconciliation.md): KSC-side ledger writes
            // were bypassing the commit-time reconciliation hook used by recording commits.
            // Key-match this action against the nearest FundsChanged/ScienceChanged/
            // ReputationChanged event in GameStateStore (paired by KSP TransactionReasons,
            // scoped to untransformed action types only) and log WARN on missing event or
            // delta mismatch. Surfaces missing earning channels (strategy, world first, etc.)
            // as they happen without triggering false positives on curve-/strategy-
            // transformed rewards.
            ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, evt.ut);

            RecalculateAndPatch();
        }

        /// <summary>
        /// #445: Called from <see cref="GameStateRecorder.OnFundsChanged"/> when KSP fires a
        /// <c>TransactionReasons.VesselRollout</c> deduction (player launches a vessel from
        /// VAB/SPH onto the launchpad/runway). Synthesizes a
        /// <see cref="GameActionType.FundsSpending"/> with
        /// <see cref="FundsSpendingSource.VesselBuild"/> at the moment of the deduction so
        /// the ledger tracks the cost even when the player cancels the rollout without ever
        /// starting a recording. The <see cref="GameAction.DedupKey"/> is set to a
        /// <c>"rollout:"</c>-prefixed context key so a subsequent recording from the same
        /// vessel/site can claim the action via <see cref="TryAdoptRolloutAction"/>
        /// instead of double-charging.
        /// </summary>
        /// <param name="ut">Universal time of the rollout transaction.</param>
        /// <param name="cost">Positive funds amount KSP deducted (rollout cost).</param>
        internal static void OnVesselRolloutSpending(double ut, double cost)
        {
            var context = LedgerRolloutAdoption.ResolveCurrentRolloutAdoptionContext();

            Initialize();

            LedgerRolloutAdoption.RecordVesselRolloutSpending(
                ut,
                cost,
                context,
                AllocateKscSequence,
                (action, actionUt) => ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, actionUt),
                () => RecalculateAndPatch());
        }

        /// <summary>
        /// Test seam for rollout adoption context. Production calls the two-argument
        /// overload, which resolves the current live vessel/site from KSP state.
        /// </summary>
        internal static void OnVesselRolloutSpending(
            double ut,
            double cost,
            uint vesselPersistentId,
            string vesselName,
            string launchSiteName)
        {
            var context = LedgerRolloutAdoption.CreateRolloutAdoptionContext(
                vesselPersistentId,
                vesselName,
                launchSiteName);

            Initialize();

            LedgerRolloutAdoption.RecordVesselRolloutSpending(
                ut,
                cost,
                context,
                AllocateKscSequence,
                (action, actionUt) => ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, actionUt),
                () => RecalculateAndPatch());
        }

        /// <summary>
        /// UT epsilon used by <see cref="OnVesselRecoveryFunds"/> when locating the paired
        /// <see cref="GameStateEventType.FundsChanged"/>(<c>VesselRecovery</c>) event in
        /// <see cref="GameStateStore.Events"/>. KSP fires the funds change immediately
        /// before <c>onVesselRecovered</c>, so the two share the same UT to within a frame.
        /// Tightened to 0.1 s (matching the dedup epsilon at line ~375 in
        /// <see cref="DeduplicateAgainstLedger"/>) to avoid latching unrelated back-to-back
        /// recoveries — see consumed-fingerprint guard below for the second layer of protection.
        /// </summary>
        internal const double VesselRecoveryEventEpsilonSeconds = 0.1;

        /// <summary>
        /// Reason-key string written by <see cref="GameStateRecorder"/>'s OnFundsChanged
        /// handler when <c>TransactionReasons.VesselRecovery</c> is the cause. Stored as
        /// a string literal so unit tests can synthesize the matching event without a live
        /// KSP runtime. Pinned at <see cref="Initialize"/> time against
        /// <c>TransactionReasons.VesselRecovery.ToString()</c> — drift logs WARN.
        /// </summary>
        internal const string VesselRecoveryReasonKey = "VesselRecovery";
        internal const string TechResearchScienceReasonKey = "RnDTechResearch";

        /// <summary>
        /// Hard cap on how many unmatched recovery requests can accumulate before the
        /// list itself becomes a leak signal. Staleness eviction also runs on lifecycle
        /// boundaries (<see cref="OnKspLoad"/>, rewind end, scene switch), but this
        /// threshold is the safety net when a boundary is missed. Chosen to absorb the
        /// largest realistic bulk-recovery burst while still flagging runaway
        /// growth from callbacks that never receive paired funds events.
        /// </summary>
        internal const int PendingRecoveryFundsStaleThreshold = 5;

        /// <summary>
        /// Stable fingerprint for a FundsChanged(VesselRecovery) event. Internal for
        /// direct testability.
        /// </summary>
        internal static string BuildRecoveryEventDedupKey(GameStateEvent e)
        {
            return LedgerRecoveryFundsPairing.BuildRecoveryEventDedupKey(e);
        }

        /// <summary>
        /// Locates the latest FundsChanged(VesselRecovery) event within the recovery
        /// epsilon window. When <paramref name="skipConsumed"/> is true, events whose
        /// dedup fingerprint is already consumed are ignored by
        /// <see cref="LedgerRecoveryFundsPairing"/>.
        /// </summary>
        private static bool TryFindRecoveryFundsEvent(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            bool skipConsumed,
            out GameStateEvent matched,
            out string dedupKey)
        {
            return LedgerRecoveryFundsPairing.TryFindRecoveryFundsEvent(
                events,
                ut,
                skipConsumed,
                out matched,
                out dedupKey);
        }

        internal static int PendingRecoveryFundsCountForTesting =>
            LedgerRecoveryFundsPairing.PendingRecoveryFundsCountForTesting;

        internal static void OnRecoveryFundsEventRecorded(GameStateEvent evt)
        {
            if (evt.eventType != GameStateEventType.FundsChanged)
                return;
            if (evt.key != VesselRecoveryReasonKey)
                return;

            // Initialize() is idempotent: it short-circuits on the `initialized` flag so
            // repeated calls cost one branch. See LedgerOrchestrator.Initialize().
            Initialize();

            LedgerRecoveryFundsPairing.OnRecoveryFundsEventRecorded(
                evt,
                TryAddVesselRecoveryFundsAction);
        }

        /// <summary>
        /// Picks the best pending recovery-funds request for a FundsChanged(VesselRecovery)
        /// event using the two-tier policy documented on <see cref="OnRecoveryFundsEventRecorded"/>.
        /// Internal static for direct testability.
        /// </summary>
        /// <remarks>
        /// <para>Ties after vessel-name matching at identical UT distance fall back to the
        /// first match (list order) but emit a WARN so the ambiguity is visible.</para>
        /// </remarks>
        internal static int FindBestPairingIndex(double eventUt, string eventVesselName)
        {
            return LedgerRecoveryFundsPairing.FindBestPairingIndex(eventUt, eventVesselName);
        }

        /// <summary>
        /// Drains unclaimed pending recovery-funds entries at a lifecycle
        /// boundary (scene switch, rewind end, load). Any entry still waiting for a
        /// paired FundsChanged(VesselRecovery) event past the boundary cannot pair
        /// afterward — the funds event would have arrived by now if stock was going to
        /// fire it. The WARN lists every unclaimed entry so a missing-payout bug stays
        /// visible instead of silently leaking into the next session's pending list.
        /// </summary>
        /// <param name="reason">Human-readable reason for the flush (e.g., "scene switch",
        /// "rewind end", "KSP load"). Appears in the WARN log line for diagnostics.</param>
        internal static void FlushStalePendingRecoveryFunds(string reason)
        {
            LedgerRecoveryFundsPairing.FlushStalePendingRecoveryFunds(reason);
        }

        /// <summary>
        /// Compatibility repair for saves written before recovery FundsEarning actions
        /// persisted <see cref="GameAction.DedupKey"/>. Rebuilds the missing key from
        /// the matching loaded FundsChanged(VesselRecovery) event.
        /// </summary>
        private static void RepairMissingRecoveryDedupKeys()
        {
            LedgerRecoveryFundsPairing.RepairMissingRecoveryDedupKeys(
                Ledger.Actions,
                GameStateStore.Events);
        }

        /// <summary>
        /// #444: Called from <see cref="ParsekScenario"/>'s <c>onVesselRecovered</c> handler
        /// when a vessel is recovered from outside the Flight scene (tracking station or the
        /// post-flight summary at KSC). KSP emits <c>FundsChanged(VesselRecovery)</c> at the
        /// recovery UT, but that event lies between recording windows for any committed
        /// recording — <see cref="CreateVesselCostActions"/> only emits <see cref="FundsEarningSource.Recovery"/>
        /// when the recording's <see cref="TerminalState"/> is <see cref="TerminalState.Recovered"/>
        /// AND the funds delta falls inside its <see cref="Recording.Points"/> window. Recovery
        /// performed after the recording has already ended misses both gates and the funds were
        /// silently dropped from the ledger.
        ///
        /// <para>This routes the recovery payout into the ledger as a real-time
        /// <see cref="GameActionType.FundsEarning"/> action with <see cref="FundsEarningSource.Recovery"/>.
        /// The amount comes from the matching <see cref="GameStateEventType.FundsChanged"/> event
        /// in <see cref="GameStateStore"/>. If stock fires <c>onVesselRecovered</c> before
        /// the funds event, the request is deferred and paired when
        /// <see cref="OnRecoveryFundsEventRecorded"/> sees the event. The recording tag is the committed recording whose
        /// <c>[StartUT, EndUT]</c> brackets <paramref name="ut"/> (preferred), then the most
        /// recent same-named flight that ended at or before <paramref name="ut"/>, then the
        /// global latest by EndUT. Falls back to <c>null</c> when no real recording matches.</para>
        ///
        /// <para>Each <see cref="GameStateStore.Events"/> entry can pair to at most one
        /// recovery call: consumed recovery-event fingerprints are tracked in
        /// the recovery pairing helper so a bulk-recovery burst (two
        /// <c>onVesselRecovered</c> callbacks within the epsilon window) routes each call
        /// to its own funds delta instead of double-latching the most recent event.</para>
        ///
        /// <para>The missing-pair <see cref="VesselType.Debris"/> case is handled before
        /// the deferred queue: once immediate pairing fails, debris callbacks return
        /// without entering the pending recovery-funds queue. Other vessel types,
        /// including <see cref="VesselType.SpaceObject"/>, keep the deferred-pair path.</para>
        ///
        /// <para>In-flight recovery is handled by the existing terminal-state path:
        /// <see cref="ParsekScenario.UpdateRecordingsForTerminalEvent"/> sets
        /// <see cref="TerminalState.Recovered"/> on the live recording, and the subsequent
        /// commit invokes <see cref="CreateVesselCostActions"/> which emits the same action.
        /// <see cref="ParsekScenario"/> only calls this helper when no pending-tree
        /// recording still owns the vessel, avoiding overlap with the commit-time path.</para>
        ///
        /// <para><b>Known false-positive in <see cref="ReconcileEarningsWindow"/>:</b> a future
        /// recording-commit whose <c>[startUT, endUT]</c> overlaps <paramref name="ut"/> will
        /// see this <c>FundsChanged(VesselRecovery)</c> event in-window but the matching
        /// <c>FundsEarning(Recovery)</c> action lives in the pre-existing ledger (added here),
        /// not in <c>newActions</c>. The reconciliation walk only sums <c>newActions</c>, so
        /// it will WARN about a missing earning channel of magnitude
        /// <c>delta</c>. Acceptable: the WARN is informational, the ledger is correct, and
        /// folding pre-existing in-window recovery actions into the comparison would also
        /// require accounting for in-window non-recovery deletions — out of scope for #444.</para>
        /// </summary>
        /// <param name="ut">UT at which <c>onVesselRecovered</c> fired.</param>
        /// <param name="vesselName">Recovered vessel name (used to tag the action with the matching recording).</param>
        /// <param name="fromTrackingStation">Pass-through diagnostic flag for the log line.</param>
        /// <param name="vesselType">Recovered vessel type; debris without an immediate
        /// pair is not deferred because debris-only recoveries can lack a ledger-worthy
        /// paired FundsChanged(VesselRecovery) event.</param>
        internal static void OnVesselRecoveryFunds(
            double ut,
            string vesselName,
            bool fromTrackingStation,
            VesselType vesselType = VesselType.Unknown)
        {
            Initialize();

            LedgerRecoveryFundsPairing.OnVesselRecoveryFunds(
                ut,
                vesselName,
                fromTrackingStation,
                vesselType,
                TryAddVesselRecoveryFundsAction);
        }

        private static bool TryAddVesselRecoveryFundsAction(double ut, string vesselName, bool fromTrackingStation)
        {
            return LedgerRecoveryFundsPairing.TryAddVesselRecoveryFundsAction(
                ut,
                vesselName,
                fromTrackingStation,
                PickRecoveryRecordingId,
                AllocateKscSequence,
                Ledger.Actions,
                () => RecalculateAndPatch());
        }

        /// <summary>
        /// #445: Searches the ledger for an unclaimed rollout-tagged
        /// <see cref="FundsSpendingSource.VesselBuild"/> action within
        /// <see cref="RolloutAdoptionWindowSeconds"/> game seconds before
        /// <paramref name="startUT"/>. If found, transfers ownership to
        /// <paramref name="recordingId"/> by setting
        /// <see cref="GameAction.RecordingId"/> and clearing
        /// <see cref="GameAction.DedupKey"/>. Returns the adopted action or <c>null</c>.
        /// <para>
        /// Adoption avoids double-charging when a recording starts from a vessel that has
        /// already incurred the rollout deduction at the launchpad. When multiple
        /// unclaimed rollouts sit within the window, the search runs LIFO — but only
        /// among actions whose stored rollout context matches the recording's current
        /// vessel/site (prefer pid, else vessel name + launch site). This keeps a
        /// stranded cancelled rollout from vessel A from being stolen by an unrelated
        /// prelaunch recording B that happens to start nearby in the same 30-minute window.
        /// </para>
        /// </summary>
        internal static GameAction TryAdoptRolloutAction(string recordingId, double startUT)
        {
            return TryAdoptRolloutAction(recordingId, startUT, FindRecordingById(recordingId));
        }

        internal static GameAction TryAdoptRolloutAction(string recordingId, double startUT, Recording rec)
        {
            return LedgerRolloutAdoption.TryAdoptRolloutAction(recordingId, startUT, rec, Ledger.Actions);
        }

        /// <summary>
        /// Pure eligibility gate for rollout adoption. Only recordings that started from a
        /// launch-ready PRELAUNCH state are allowed to claim a prior VesselRollout charge.
        /// This deliberately excludes recordings started after liftoff (Flying/Sub-orbital/
        /// Orbiting) so a late Record click cannot pull the vessel build cost into a
        /// mid-flight recording and later refund it on delete/discard.
        /// </summary>
        internal static bool CanRecordingAdoptRolloutAction(Recording rec)
        {
            return LedgerRolloutAdoption.CanRecordingAdoptRolloutAction(rec);
        }

        /// <summary>
        /// #445: maximum game-seconds gap between a <c>TransactionReasons.VesselRollout</c>
        /// deduction and the start of the recording that should claim it. Measured in UT
        /// (not wall-clock). Kept deliberately broad because players can sit on the
        /// launchpad/runway for several real minutes before pressing Record; the
        /// <see cref="CanRecordingAdoptRolloutAction"/> gate keeps this from spilling
        /// into post-liftoff recordings. The boundary is inclusive: a recording whose
        /// <c>startUT - rollout.UT</c> equals exactly this value still adopts (strict
        /// <c>&gt;</c> comparison); value + epsilon does not.
        /// </summary>
        internal const double RolloutAdoptionWindowSeconds = 1800.0;

        /// <summary>
        /// #444 review item 2: pick the committed recording id that best matches a
        /// vessel-recovery payout at <paramref name="ut"/>. Selection priority:
        /// <list type="number">
        ///   <item>Recording whose <c>[StartUT, EndUT]</c> brackets <paramref name="ut"/>
        ///   (the flight that actually covers the recovery moment). If multiple bracket,
        ///   pick the one with the largest EndUT.</item>
        ///   <item>Most recent flight that ended at or before <paramref name="ut"/>
        ///   (largest EndUT with EndUT &lt;= ut).</item>
        ///   <item>Global latest by EndUT (preserved fallback for recoveries whose metadata
        ///   has drifted, e.g., manual EndUT trim).</item>
        /// </list>
        /// Skips ghost-only recordings (zero career footprint per #432). Returns null when
        /// no non-ghost-only recording matches <paramref name="vesselName"/>.
        /// Internal static for testability.
        /// </summary>
        internal static string PickRecoveryRecordingId(string vesselName, double ut)
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return null;

            Recording bracketing = null;       // tier 1
            Recording mostRecentEnded = null;  // tier 2
            Recording globalLatest = null;     // tier 3
            int candidateCount = 0;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null) continue;
                if (rec.IsGhostOnly) continue;
                if (rec.VesselName != vesselName) continue;
                candidateCount++;

                double startUT = rec.StartUT;
                double endUT = rec.EndUT;

                // Tier 1: brackets ut
                if (startUT <= ut && ut <= endUT)
                {
                    if (bracketing == null || endUT > bracketing.EndUT)
                        bracketing = rec;
                }

                // Tier 2: ended at or before ut
                if (endUT <= ut)
                {
                    if (mostRecentEnded == null || endUT > mostRecentEnded.EndUT)
                        mostRecentEnded = rec;
                }

                // Tier 3: global latest
                if (globalLatest == null || endUT > globalLatest.EndUT)
                    globalLatest = rec;
            }

            Recording pick = bracketing ?? mostRecentEnded ?? globalLatest;
            if (pick == null) return null;

            string tier = bracketing != null ? "bracketing"
                        : mostRecentEnded != null ? "most-recent-ended"
                        : "global-latest";
            ParsekLog.Verbose(Tag,
                $"PickRecoveryRecordingId: vessel='{vesselName}' ut={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"candidates={candidateCount} tier={tier} pick={pick.RecordingId}");

            return pick.RecordingId;
        }

        /// <summary>
        /// Pure classifier: maps a KSC-path <see cref="GameAction"/> to its reconciliation
        /// class + expected paired resource event. Used by <see cref="ReconcileKscAction"/>.
        /// <para>
        /// Event keys are <c>TransactionReasons.ToString()</c> as written by
        /// <see cref="GameStateRecorder"/>'s <c>OnFundsChanged</c> / <c>OnScienceChanged</c> /
        /// <c>OnReputationChanged</c> handlers (see their <c>key = reason.ToString()</c>
        /// lines).
        /// </para>
        /// </summary>
        internal static KscActionExpectation ClassifyAction(GameAction action)
        {
            return KscActionExpectationClassifier.ClassifyAction(action);
        }

        /// <summary>Compatibility facade for <see cref="KscActionReconciler.ReconcileKscAction"/>.</summary>
        internal static void ReconcileKscAction(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            GameAction action,
            double ut)
        {
            KscActionReconciler.ReconcileKscAction(events, ledgerActions, action, ut);
        }

        /// <summary>Compatibility facade for <see cref="KscActionReconciler.ResourceChannelTag"/>.</summary>
        internal static string ResourceChannelTag(GameStateEventType t)
        {
            return KscActionReconciler.ResourceChannelTag(t);
        }

        /// <summary>
        /// Compatibility facade for <see cref="KscActionReconciler.KscReconcileEpsilonSeconds"/>;
        /// see the extracted reconciler constant for the coalescing rationale.
        /// </summary>
        internal const double KscReconcileEpsilonSeconds = KscActionReconciler.KscReconcileEpsilonSeconds;

        // ================================================================
        // #440 Phase E2 -- Post-walk reconciliation (transformed reward types)
        //
        // Implementation lives in PostWalkActionReconciler. Keep these facades so
        // existing tests/call sites continue to use the LedgerOrchestrator surface.
        // ================================================================

        /// <summary>
        /// UT window (seconds) used by <see cref="ReconcilePostWalk"/> to pair a
        /// transformed-type action with its resource-changed event. Const facade:
        /// downstream assemblies must be rebuilt if this value changes because C#
        /// const references are inlined.
        /// </summary>
        internal const double PostWalkReconcileEpsilonSeconds =
            PostWalkActionReconciler.PostWalkReconcileEpsilonSeconds;

        internal static PostWalkActionReconciler.PostWalkExpectation ClassifyPostWalk(GameAction action)
        {
            return PostWalkActionReconciler.ClassifyPostWalk(action);
        }

        internal static void ReconcilePostWalk(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            PostWalkActionReconciler.ReconcilePostWalk(events, actions, utCutoff);
        }

        internal static bool TryGetPersistedScienceActionWindow(
            GameAction action,
            out double startUt,
            out double endUt,
            out bool collapsedPersistedSpan)
        {
            startUt = 0.0;
            endUt = 0.0;
            collapsedPersistedSpan = false;

            if (action == null)
                return false;
            if (float.IsNaN(action.StartUT) || float.IsNaN(action.EndUT))
                return false;

            if (action.EndUT > action.StartUT)
            {
                startUt = action.StartUT;
                endUt = action.EndUT;
                return true;
            }

            if (action.EndUT == action.StartUT && action.EndUT > 0f)
            {
                collapsedPersistedSpan = true;
                startUt = GetCollapsedScienceWindowStart(action.StartUT, action.UT);
                endUt = action.UT;
                if (endUt < startUt)
                    endUt = startUt;
                return true;
            }

            return false;
        }

        private static double GetCollapsedScienceWindowStart(float collapsedStartUt, double actionUt)
        {
            double halfWidth = GetScienceReconcileCollapsedHalfWidth(collapsedStartUt);
            double startUt = collapsedStartUt - halfWidth;
            if (double.IsNaN(startUt) || double.IsInfinity(startUt))
                startUt = collapsedStartUt;
            if (actionUt < startUt)
                startUt = actionUt;
            return startUt;
        }

        private static double GetScienceReconcileCollapsedHalfWidth(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0.0;

            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            float previous = BitConverter.ToSingle(BitConverter.GetBytes(bits - 1), 0);
            float next = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            double lower = float.IsNaN(previous) || float.IsInfinity(previous)
                ? 0.0
                : Math.Abs((double)value - previous);
            double upper = float.IsNaN(next) || float.IsInfinity(next)
                ? 0.0
                : Math.Abs(next - (double)value);

            double width = 0.0;
            if (lower > 0.0 && upper > 0.0)
                width = Math.Min(lower, upper);
            else
                width = Math.Max(lower, upper);

            return width * 0.5;
        }

        // Mirrored in GameStateEventConverter.EventMatchesRecordingScope; keep the two in sync.
        private static bool EventMatchesRecordingScope(GameStateEvent evt, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return true;

            string eventRecordingId = evt.recordingId ?? "";
            return string.Equals(eventRecordingId, recordingId, StringComparison.Ordinal);
        }
        private static bool DoesScienceEventMatchActionScope(
            GameStateEvent evt,
            GameAction action,
            out bool matchedViaUntaggedWindow)
        {
            return DoesScienceEventMatchActionScope(
                evt,
                action,
                action != null ? action.StartUT : 0.0,
                action != null ? action.EndUT : 0.0,
                out matchedViaUntaggedWindow);
        }

        internal static bool DoesScienceEventMatchActionScope(
            GameStateEvent evt,
            GameAction action,
            double actionStartUt,
            double actionEndUt,
            out bool matchedViaUntaggedWindow)
        {
            matchedViaUntaggedWindow = false;

            string actionRecordingId = action?.RecordingId ?? "";
            if (string.IsNullOrEmpty(actionRecordingId))
                return true;

            string eventRecordingId = evt.recordingId ?? "";
            if (string.Equals(eventRecordingId, actionRecordingId, StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(eventRecordingId))
                return false;

            if (evt.ut < actionStartUt || evt.ut > actionEndUt)
                return false;

            matchedViaUntaggedWindow = true;
            return true;
        }

        internal static string GetScienceChangedReasonKey(GameAction action)
        {
            if (action != null && action.Method == ScienceMethod.Recovered)
                return VesselRecoveryReasonKey;

            return "ScienceTransmission";
        }

        internal static string FormatScienceEventForReconcileDump(
            GameStateEvent evt,
            bool matchedScope,
            bool matchedViaUntaggedWindow)
        {
            string tag = evt.recordingId ?? "";
            string matchLabel = matchedScope ? "match" : "skip";
            string untaggedLabel = matchedViaUntaggedWindow ? ", untagged-window" : "";
            return string.Format(
                CultureInfo.InvariantCulture,
                "ut={0:F1} key='{1}' delta={2:F1} tag='{3}' [{4}{5}]",
                evt.ut,
                evt.key ?? "",
                evt.valueAfter - evt.valueBefore,
                tag,
                matchLabel,
                untaggedLabel);
        }
        internal static void GetResourceTrackingAvailability(
            out bool fundsTracked,
            out bool scienceTracked,
            out bool repTracked)
        {
            fundsTracked = fundsTrackedOverrideForTesting ?? Funding.Instance != null;
            scienceTracked = scienceTrackedOverrideForTesting
                ?? ResearchAndDevelopment.Instance != null;
            repTracked = repTrackedOverrideForTesting ?? global::Reputation.Instance != null;
        }

        internal static void LogReconcileSkippedOnce(
            string scopeKey,
            string scopeLabel,
            bool fundsTracked,
            bool scienceTracked,
            bool repTracked)
        {
            ParsekLog.VerboseRateLimited(
                Tag,
                $"reconcile-skip:{scopeKey}:{fundsTracked}:{scienceTracked}:{repTracked}",
                $"{scopeLabel} skipped: sandbox / tracker unavailable " +
                $"(funds={fundsTracked.ToString().ToLowerInvariant()} " +
                $"sci={scienceTracked.ToString().ToLowerInvariant()} " +
                $"rep={repTracked.ToString().ToLowerInvariant()})",
                OneShotReconcileSkipLogIntervalSeconds);
        }

        internal static bool TryRegisterPostWalkDumpKey(string dumpKey)
        {
            return emittedScienceReconcileDumpKeys.Add("postwalk-dump:" + (dumpKey ?? ""));
        }

        internal static string FormatFixed1(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        internal static string FormatFixed3(double value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Test-only seam for the "now UT" used by <see cref="CanAffordScienceSpending"/>
        /// and <see cref="CanAffordFundsSpending"/>. In production this stays null and the
        /// helpers call <see cref="Planetarium.GetUniversalTime"/> directly. Unit tests can
        /// install a delegate to supply a deterministic UT because Planetarium throws NRE
        /// in the Unity-static-free xUnit harness (see comments in
        /// <c>FastForwardTests.CanFastForward_NoSaveFile_PassesPreRuntimeGuards</c>). Cleared
        /// by <see cref="ResetForTesting"/>.
        /// </summary>
        internal static System.Func<double> NowUtProviderForTesting;

        /// <summary>
        /// Returns the current universal time for affordability-helper UT cutoffs. Production
        /// path reads <see cref="Planetarium.GetUniversalTime"/>; the test seam
        /// <see cref="NowUtProviderForTesting"/> overrides when set.
        /// </summary>
        private static double GetNowUT()
        {
            var provider = NowUtProviderForTesting;
            if (provider != null)
                return provider();
            return Planetarium.GetUniversalTime();
        }

        /// <summary>
        /// Pure helper for the live KSC tech-unlock race: when KSP has already applied a
        /// recent <c>ScienceChanged(RnDTechResearch)</c> debit but the matching
        /// <c>TechResearched -> ScienceSpending</c> action has not landed in the ledger yet,
        /// this returns the unmatched science amount that would otherwise be refunded by
        /// <see cref="KspStatePatcher.PatchScience"/>.
        /// </summary>
        internal static double ComputePendingRecentKscTechResearchScienceDebit(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            double nowUt)
        {
            double observedDebit = 0.0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    if (evt.eventType != GameStateEventType.ScienceChanged)
                        continue;
                    if (!string.Equals(evt.key, TechResearchScienceReasonKey, StringComparison.Ordinal))
                        continue;
                    if (evt.valueAfter >= evt.valueBefore)
                        continue;
                    if (Math.Abs(evt.ut - nowUt) > KscReconcileEpsilonSeconds)
                        continue;

                    observedDebit += evt.valueBefore - evt.valueAfter;
                }
            }

            if (observedDebit <= 0.1)
                return 0.0;

            double committedDebit = 0.0;
            if (ledgerActions != null)
            {
                for (int i = 0; i < ledgerActions.Count; i++)
                {
                    var action = ledgerActions[i];
                    if (action == null)
                        continue;
                    if (action.Type != GameActionType.ScienceSpending)
                        continue;
                    if (!string.IsNullOrEmpty(action.RecordingId))
                        continue;
                    if (Math.Abs(action.UT - nowUt) > KscReconcileEpsilonSeconds)
                        continue;

                    committedDebit += action.Cost;
                }
            }

            double pendingDebit = observedDebit - committedDebit;
            return pendingDebit > 0.1 ? pendingDebit : 0.0;
        }

        /// <summary>
        /// Live wrapper over <see cref="ComputePendingRecentKscTechResearchScienceDebit"/>.
        /// Uses the current GameStateStore / ledger state and the affordability "now UT"
        /// seam so both production and tests evaluate the same recent-action window.
        /// </summary>
        internal static double GetPendingRecentKscTechResearchScienceDebit()
        {
            return ComputePendingRecentKscTechResearchScienceDebit(
                GameStateStore.Events,
                Ledger.Actions,
                GetNowUT());
        }

        /// <summary>
        /// Checks whether a science spending of the given cost is affordable under the
        /// current ledger reservation. Returns true if available science >= cost.
        /// Used by TechResearchPatch to block unfunded tech unlocks.
        /// </summary>
        /// <remarks>
        /// The recalculation walk is scoped to <c>Planetarium.GetUniversalTime()</c> as
        /// its UT cutoff so post-rewind future actions on the persisted ledger don't leak
        /// into affordability ("what's the state right now?" → right now = Planetarium's
        /// current UT, not the ledger's last action).
        /// </remarks>
        internal static bool CanAffordScienceSpending(float cost)
        {
            Initialize();
            if (scienceModule == null) return true;

            // Run a recalculation to get current state (may already be current).
            // cutoff = Planetarium.GetUniversalTime() so post-rewind future actions don't
            // leak into affordability.
            // Phase 9 of Rewind-to-Staging (design §3.2): route through ELS so
            // any tombstoned action (kerbal deaths + bundled rep penalties) is
            // excluded from the affordability probe's walk.
            var actions = new System.Collections.Generic.List<GameAction>(EffectiveState.ComputeELS());
            double nowUT = GetNowUT();
            RecalculationEngine.Recalculate(actions, nowUT);

            double available = scienceModule.GetAvailableScience();
            bool affordable = available >= (double)cost;

            ParsekLog.Verbose(Tag,
                $"CanAffordScienceSpending: cost={cost:F1}, available={available:F1}, " +
                $"affordable={affordable}, cutoffUT={nowUT:R}");

            return affordable;
        }

        /// <summary>
        /// Checks whether a funds spending of the given cost is affordable under the
        /// current ledger reservation. Returns true if available funds >= cost.
        /// Not yet wired to FacilityUpgradePatch — scaffolding for future use.
        /// </summary>
        /// <remarks>
        /// Same UT-cutoff rule as <see cref="CanAffordScienceSpending"/>: the walk is
        /// scoped to <c>Planetarium.GetUniversalTime()</c> so post-rewind future actions
        /// never fabricate or withhold present-day affordability.
        /// </remarks>
        internal static bool CanAffordFundsSpending(float cost)
        {
            Initialize();
            if (fundsModule == null) return true;

            // cutoff = Planetarium.GetUniversalTime() so post-rewind future actions don't
            // leak into affordability.
            // Phase 9 of Rewind-to-Staging (design §3.2): route through ELS so
            // any tombstoned action (kerbal deaths + bundled rep penalties) is
            // excluded from the affordability probe's walk.
            var actions = new System.Collections.Generic.List<GameAction>(EffectiveState.ComputeELS());
            double nowUT = GetNowUT();
            RecalculationEngine.Recalculate(actions, nowUT);

            double available = fundsModule.GetAvailableFunds();
            bool affordable = available >= (double)cost;

            ParsekLog.Verbose(Tag,
                $"CanAffordFundsSpending: cost={cost:F1}, available={available:F1}, " +
                $"affordable={affordable}, cutoffUT={nowUT:R}");

            return affordable;
        }

        /// <summary>Reset all state for testing.</summary>
        internal static void ResetForTesting()
        {
            initialized = false;
            fundsSeedDone = false;
            scienceSeedDone = false;
            repSeedDone = false;
            fundsTrackedOverrideForTesting = null;
            scienceTrackedOverrideForTesting = null;
            repTrackedOverrideForTesting = null;
            ResetMigrateOldSaveEventsForTesting();
            kscSequenceCounter = 0;
            emittedReconcileWarnKeys.Clear();
            emittedScienceReconcileDumpKeys.Clear();
            LedgerRecoveryFundsPairing.ResetForTesting();
            scienceModule = null;
            milestonesModule = null;
            contractsModule = null;
            fundsModule = null;
            reputationModule = null;
            facilitiesModule = null;
            strategiesModule = null;
            kerbalsModule = null;
            RecalculationEngine.ClearModules();
            Ledger.ResetForTesting();
            OnTimelineDataChanged = null;
            OnRecordingCommittedFaultInjector = null;
            OnRecordingCommittedPostSciencePersistFaultInjector = null;
            NowUtProviderForTesting = null;
            ParsekLog.Verbose(Tag, "ResetForTesting: all state cleared");
        }

        // ================================================================
        // Dynamic slot limits
        // ================================================================

        /// <summary>
        /// Updates ContractsModule and StrategiesModule max slot counts based on
        /// current facility levels tracked by FacilitiesModule. Called before each
        /// recalculation walk so slot limits reflect the most recent facility state.
        /// </summary>
        private static void UpdateSlotLimitsFromFacilities()
        {
            if (facilitiesModule == null || contractsModule == null || strategiesModule == null)
                return;

            int missionControlLevel = facilitiesModule.GetFacilityLevel("MissionControl");
            int contractSlots = GetContractSlots(missionControlLevel);
            contractsModule.SetMaxSlots(contractSlots);

            int adminLevel = facilitiesModule.GetFacilityLevel("Administration");
            int strategySlots = GetStrategySlots(adminLevel);
            strategiesModule.SetMaxSlots(strategySlots);

            ParsekLog.Verbose(Tag,
                $"UpdateSlotLimits: MissionControl level={missionControlLevel} -> {contractSlots} contract slots, " +
                $"Administration level={adminLevel} -> {strategySlots} strategy slots");
        }

        /// <summary>
        /// Maps Mission Control facility level to active contract slot count.
        /// KSP stock values: Level 1 = 2, Level 2 = 7, Level 3 = unlimited (999).
        /// </summary>
        internal static int GetContractSlots(int level)
        {
            switch (level)
            {
                case 1: return 2;    // Level 1 (unupgraded)
                case 2: return 7;    // Level 2
                default: return 999; // Level 3+ (effectively unlimited)
            }
        }

        /// <summary>
        /// Maps Administration facility level to active strategy slot count.
        /// KSP stock values: Level 1 = 1, Level 2 = 3, Level 3 = 5.
        /// </summary>
        internal static int GetStrategySlots(int level)
        {
            switch (level)
            {
                case 1: return 1;   // Level 1 (unupgraded)
                case 2: return 3;   // Level 2
                default: return 5;  // Level 3+
            }
        }

        // ================================================================
        // Module accessors (for KspStatePatcher and tests)
        // ================================================================

        internal static ScienceModule Science => scienceModule;
        internal static FundsModule Funds => fundsModule;
        internal static ReputationModule Reputation => reputationModule;
        internal static MilestonesModule Milestones => milestonesModule;
        internal static FacilitiesModule Facilities => facilitiesModule;
        internal static ContractsModule Contracts => contractsModule;
        internal static StrategiesModule Strategies => strategiesModule;
        internal static KerbalsModule Kerbals => kerbalsModule;
        internal static void SetKerbalsForTesting(KerbalsModule module) { kerbalsModule = module; }
        internal static bool IsInitialized => initialized;

        /// <summary>
        /// Notifies the ledger orchestrator about each recording in a committed tree.
        /// Calls OnRecordingCommitted for each recording in the tree.
        /// Chain continuations (ChainIndex > 0) have their startUT extended backward
        /// to the predecessor segment's EndUT, closing inter-segment gaps created by
        /// the optimizer's TrackSection splits so that events in those gaps are not lost.
        /// </summary>
        internal static void NotifyLedgerTreeCommitted(RecordingTree tree)
        {
            if (tree == null) return;

            // Build predecessor EndUT lookup for chain gap closure.
            // Key: "chainId:chainIndex", Value: EndUT of that segment.
            var chainEndUTs = BuildChainEndUtMap(tree);

            // #397 / #528 follow-up: snapshot PendingScienceSubjects ONCE, then route each
            // subject to the recording it actually belongs to. Tagged subjects keep their
            // tagged recording, while untagged subjects are attributed by captureUT window.
            // This preserves valid mixed-recording science without reopening the old
            // duplication bug where every recording re-read and re-emitted the full batch.
            var pendingSnapshot = GameStateRecorder.PendingScienceSubjects.Count == 0
                ? EmptyPendingScience
                : (IReadOnlyList<PendingScienceSubject>)
                    new List<PendingScienceSubject>(GameStateRecorder.PendingScienceSubjects);
            int pendingBefore = pendingSnapshot.Count;
            var pendingByRecording = BuildTreePendingScienceRouting(tree, chainEndUTs, pendingSnapshot);
            int routedPending = 0;
            foreach (var routed in pendingByRecording.Values)
                routedPending += routed.Count;
            int removedPending = 0;

            int gapsClosed = 0;
            bool commitSucceeded = false;
            try
            {
                foreach (var rec in tree.Recordings.Values)
                {
                    double startUT = rec.StartUT;
                    double endUT = rec.EndUT;

                    // Close chain segment gap: extend startUT backward to predecessor's EndUT
                    double adjusted = AdjustStartUtForChainGap(rec, startUT, chainEndUTs);
                    if (adjusted < startUT) gapsClosed++;
                    startUT = adjusted;

                    IReadOnlyList<PendingScienceSubject> perRecordingPending;
                    if (!pendingByRecording.TryGetValue(rec.RecordingId, out perRecordingPending))
                        perRecordingPending = EmptyPendingScience;

                    bool scienceAddedToLedger = false;
                    try
                    {
                        OnRecordingCommitted(
                            rec.RecordingId,
                            startUT,
                            endUT,
                            perRecordingPending,
                            ref scienceAddedToLedger);
                        removedPending += RemovePendingSubjectsFromStatic(perRecordingPending);
                    }
                    catch
                    {
                        if (scienceAddedToLedger)
                            removedPending += RemovePendingSubjectsFromStatic(perRecordingPending);
                        throw;
                    }
                }

                commitSucceeded = true;
            }
            finally
            {
                // #397 / #528 follow-up: successful routed subsets are removed from the
                // global pending list as they become safe to discard. Unrelated retained
                // science from earlier failures stays pending across later successful
                // commits, and failing subsets stay visible so the caller can decide how
                // to recover; current production callers surface the exception instead of
                // retrying the whole commit batch.
                int remainingPending = GameStateRecorder.PendingScienceSubjects.Count;
                if (commitSucceeded)
                {
                    if (remainingPending == 0 && (pendingBefore > 0 || removedPending > 0))
                    {
                        ParsekLog.Verbose(Tag,
                            $"NotifyLedgerTreeCommitted: cleared PendingScienceSubjects " +
                            $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending})");
                    }
                    else if (removedPending > 0 || remainingPending > 0)
                    {
                        ParsekLog.Verbose(Tag,
                            $"NotifyLedgerTreeCommitted: preserved unrelated PendingScienceSubjects after success " +
                            $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                            $"remaining={remainingPending})");
                    }
                }
                else
                {
                    if (removedPending == 0)
                    {
                        ParsekLog.Verbose(Tag,
                            $"NotifyLedgerTreeCommitted: retained PendingScienceSubjects after failure " +
                            $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                            $"remaining={remainingPending})");
                    }
                    else if (remainingPending == 0)
                    {
                        ParsekLog.Verbose(Tag,
                            $"NotifyLedgerTreeCommitted: already removed PendingScienceSubjects before failure " +
                            $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                            $"remaining={remainingPending})");
                    }
                    else
                    {
                        ParsekLog.Verbose(Tag,
                            $"NotifyLedgerTreeCommitted: partially retained PendingScienceSubjects after failure " +
                            $"(snapshot={pendingBefore}, routed={routedPending}, removed={removedPending}, " +
                            $"remaining={remainingPending})");
                    }
                }
            }

            // #390: prune events consumed by milestone creation + ledger conversion
            GameStateStore.PruneProcessedEvents();

            ParsekLog.Verbose(Tag,
                $"NotifyLedgerTreeCommitted: tree='{tree.TreeName}' " +
                $"recordings={tree.Recordings.Count}, chainSegments={chainEndUTs.Count}, " +
                $"gapsClosed={gapsClosed}");
        }

        private static Dictionary<string, IReadOnlyList<PendingScienceSubject>> BuildTreePendingScienceRouting(
            RecordingTree tree,
            Dictionary<string, double> chainEndUTs,
            IReadOnlyList<PendingScienceSubject> pendingSnapshot)
        {
            var routed = new Dictionary<string, IReadOnlyList<PendingScienceSubject>>(StringComparer.Ordinal);
            var mutableBuckets = new Dictionary<string, List<PendingScienceSubject>>(StringComparer.Ordinal);
            var windows = BuildRecordingScienceWindows(tree, chainEndUTs);

            for (int i = 0; i < windows.Count; i++)
            {
                string recordingId = windows[i].RecordingId;
                if (string.IsNullOrEmpty(recordingId) || mutableBuckets.ContainsKey(recordingId))
                    continue;

                mutableBuckets[recordingId] = new List<PendingScienceSubject>();
            }

            if (pendingSnapshot != null && pendingSnapshot.Count > 0 && windows.Count > 0)
            {
                for (int i = 0; i < pendingSnapshot.Count; i++)
                {
                    string ownerRecordingId = ResolveTreePendingScienceOwnerRecordingId(
                        pendingSnapshot[i],
                        windows,
                        tree != null ? tree.ActiveRecordingId : null);
                    if (string.IsNullOrEmpty(ownerRecordingId))
                        continue;

                    List<PendingScienceSubject> bucket;
                    if (mutableBuckets.TryGetValue(ownerRecordingId, out bucket))
                        bucket.Add(pendingSnapshot[i]);
                }
            }

            foreach (var kvp in mutableBuckets)
            {
                routed[kvp.Key] = kvp.Value.Count == 0
                    ? EmptyPendingScience
                    : (IReadOnlyList<PendingScienceSubject>)kvp.Value;
            }

            return routed;
        }

        private static List<RecordingScienceWindow> BuildRecordingScienceWindows(
            RecordingTree tree,
            Dictionary<string, double> chainEndUTs)
        {
            var windows = new List<RecordingScienceWindow>();
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return windows;

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;

                windows.Add(new RecordingScienceWindow
                {
                    RecordingId = rec.RecordingId,
                    StartUT = AdjustStartUtForChainGap(rec, rec.StartUT, chainEndUTs),
                    EndUT = rec.EndUT
                });
            }

            return windows;
        }

        private static string ResolveTreePendingScienceOwnerRecordingId(
            PendingScienceSubject subject,
            IReadOnlyList<RecordingScienceWindow> windows,
            string activeRecordingId)
        {
            string subjectRecordingId = subject.recordingId ?? "";
            if (!string.IsNullOrEmpty(subjectRecordingId))
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    if (string.Equals(windows[i].RecordingId, subjectRecordingId, StringComparison.Ordinal))
                        return windows[i].RecordingId;
                }

                return null;
            }

            return PickScienceWindowRecordingId(subject.captureUT, windows, activeRecordingId);
        }

        private static string PickScienceWindowRecordingId(
            double captureUT,
            IReadOnlyList<RecordingScienceWindow> windows,
            string activeRecordingId)
        {
            if (double.IsNaN(captureUT) || double.IsInfinity(captureUT))
                return null;
            if (captureUT < 0.0)
                return null;

            bool found = false;
            RecordingScienceWindow best = default(RecordingScienceWindow);
            for (int i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                if (captureUT < window.StartUT || captureUT > window.EndUT)
                    continue;

                if (!found)
                {
                    best = window;
                    found = true;
                    continue;
                }

                if (window.StartUT > best.StartUT)
                {
                    best = window;
                    continue;
                }

                if (window.StartUT < best.StartUT)
                    continue;

                if (window.EndUT < best.EndUT)
                {
                    best = window;
                    continue;
                }

                if (window.EndUT > best.EndUT)
                    continue;

                bool windowIsActive = string.Equals(window.RecordingId, activeRecordingId, StringComparison.Ordinal);
                bool bestIsActive = string.Equals(best.RecordingId, activeRecordingId, StringComparison.Ordinal);
                if (windowIsActive && !bestIsActive)
                {
                    best = window;
                    continue;
                }

                if (!windowIsActive && bestIsActive)
                    continue;

                if (string.CompareOrdinal(window.RecordingId ?? "", best.RecordingId ?? "") < 0)
                    best = window;
            }

            return found ? best.RecordingId : null;
        }

        private static int RemovePendingSubjects(
            List<PendingScienceSubject> remainingPending,
            IReadOnlyList<PendingScienceSubject> processedPending)
        {
            if (remainingPending == null || remainingPending.Count == 0)
                return 0;
            if (processedPending == null || processedPending.Count == 0)
                return 0;

            int removed = 0;
            for (int i = 0; i < processedPending.Count; i++)
            {
                if (remainingPending.Remove(processedPending[i]))
                    removed++;
            }

            return removed;
        }

        internal static IReadOnlyList<PendingScienceSubject> BuildPendingScienceSubsetForRecording(
            IReadOnlyList<PendingScienceSubject> pendingSnapshot,
            string recordingId,
            double startUT,
            double endUT)
        {
            if (pendingSnapshot == null || pendingSnapshot.Count == 0)
                return EmptyPendingScience;

            var routed = new List<PendingScienceSubject>();
            string ownerRecordingId = recordingId ?? "";
            for (int i = 0; i < pendingSnapshot.Count; i++)
            {
                var subject = pendingSnapshot[i];
                string subjectRecordingId = subject.recordingId ?? "";
                if (!string.IsNullOrEmpty(subjectRecordingId))
                {
                    if (string.Equals(subjectRecordingId, ownerRecordingId, StringComparison.Ordinal))
                        routed.Add(subject);
                    continue;
                }

                if (DoesUntaggedScienceCaptureMatchWindow(subject.captureUT, startUT, endUT))
                    routed.Add(subject);
            }

            return routed.Count == 0
                ? EmptyPendingScience
                : (IReadOnlyList<PendingScienceSubject>)routed;
        }

        /// <summary>
        /// Returns the commit-window start UT for a direct single-recording commit.
        /// Chain continuations can have optimizer-introduced gaps between segments; when
        /// the immediate predecessor is already committed, extend the window backward to
        /// that predecessor's EndUT so the standalone path matches tree-commit routing.
        /// </summary>
        internal static double ResolveStandaloneCommitWindowStartUt(Recording rec, double startUT)
        {
            if (rec == null ||
                !rec.IsChainRecording ||
                rec.ChainIndex <= 0 ||
                string.IsNullOrEmpty(rec.ParentRecordingId))
            {
                return startUT;
            }

            var predecessor = FindRecordingById(rec.ParentRecordingId);
            if (predecessor == null ||
                !string.Equals(predecessor.ChainId ?? "", rec.ChainId ?? "", StringComparison.Ordinal) ||
                predecessor.ChainIndex != rec.ChainIndex - 1)
            {
                return startUT;
            }

            var chainEndUTs = new Dictionary<string, double>
            {
                { $"{rec.ChainId}:{predecessor.ChainIndex}", predecessor.EndUT }
            };
            return AdjustStartUtForChainGap(rec, startUT, chainEndUTs);
        }

        internal static int RemovePendingSubjectsFromStatic(
            IReadOnlyList<PendingScienceSubject> processedPending)
        {
            return RemovePendingSubjects(GameStateRecorder.PendingScienceSubjects, processedPending);
        }

        /// <summary>
        /// Finalizes a standalone recording commit's scoped pending-science subset.
        /// Successful commits (or failures after science already reached the ledger)
        /// discard only the subset routed to that recording. Pre-ledger failures keep
        /// the subset pending so the caller can decide how to recover without losing the
        /// science data. Current production callers surface the exception instead of
        /// retrying the whole commit batch.
        /// </summary>
        internal static void FinalizeScopedPendingScienceCommit(
            string logTag,
            string commitContext,
            int pendingBefore,
            IReadOnlyList<PendingScienceSubject> pendingForCommit,
            bool commitSucceeded,
            bool scienceAddedToLedger)
        {
            pendingForCommit = pendingForCommit ?? EmptyPendingScience;

            int removed = 0;
            if (commitSucceeded || scienceAddedToLedger)
                removed = RemovePendingSubjectsFromStatic(pendingForCommit);

            int currentPending = GameStateRecorder.PendingScienceSubjects.Count;
            if (commitSucceeded || scienceAddedToLedger)
            {
                if (currentPending == 0 && (pendingBefore > 0 || removed > 0))
                {
                    ParsekLog.Verbose(logTag,
                        $"{commitContext}: cleared PendingScienceSubjects " +
                        $"(before={pendingBefore}, removed={removed})");
                }
                else if (removed > 0 || currentPending > 0)
                {
                    ParsekLog.Verbose(logTag,
                        $"{commitContext}: preserved unrelated PendingScienceSubjects after success " +
                        $"(before={pendingBefore}, removed={removed}, remaining={currentPending})");
                }
            }
            else if (pendingBefore > 0 || currentPending > 0)
            {
                ParsekLog.Verbose(logTag,
                    $"{commitContext}: retained PendingScienceSubjects after failure " +
                    $"(before={pendingBefore}, remaining={currentPending})");
            }
        }

        private static bool DoesUntaggedScienceCaptureMatchWindow(
            double captureUT,
            double startUT,
            double endUT)
        {
            if (double.IsNaN(captureUT) || double.IsInfinity(captureUT))
                return false;
            if (captureUT < 0.0)
                return false;
            if (captureUT < startUT)
                return false;
            if (captureUT > endUT)
                return false;

            return true;
        }

        /// <summary>
        /// Builds a lookup of chain segment EndUTs from a recording tree.
        /// Returns a dictionary keyed by "chainId:chainIndex" for O(1) predecessor lookup.
        /// </summary>
        internal static Dictionary<string, double> BuildChainEndUtMap(RecordingTree tree)
        {
            var map = new Dictionary<string, double>();
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.IsChainRecording && rec.ChainIndex >= 0)
                {
                    map[$"{rec.ChainId}:{rec.ChainIndex}"] = rec.EndUT;
                }
            }
            return map;
        }

        /// <summary>
        /// If the recording is a chain continuation (ChainIndex > 0) and the predecessor
        /// segment's EndUT is earlier than this recording's startUT, returns the predecessor's
        /// EndUT to close the inter-segment gap. Otherwise returns startUT unchanged.
        /// </summary>
        internal static double AdjustStartUtForChainGap(
            Recording rec, double startUT, Dictionary<string, double> chainEndUTs)
        {
            if (rec == null || !rec.IsChainRecording || rec.ChainIndex <= 0)
                return startUT;

            string predKey = $"{rec.ChainId}:{rec.ChainIndex - 1}";
            if (chainEndUTs.TryGetValue(predKey, out double predEndUT) && predEndUT < startUT)
            {
                ParsekLog.Verbose(Tag,
                    $"Chain gap closure: extended startUT from " +
                    $"{startUT.ToString("F1", CultureInfo.InvariantCulture)} to " +
                    $"{predEndUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"for recording '{rec.RecordingId}' " +
                    $"(chain={rec.ChainId}, idx={rec.ChainIndex})");
                return predEndUT;
            }

            return startUT;
        }

        // ================================================================
        // Warp facility visual helpers
        // ================================================================

        /// <summary>
        /// Returns true if the ledger contains any facility-type actions
        /// (FacilityUpgrade, FacilityDestruction, FacilityRepair) with UT
        /// in the half-open range (fromUT, toUT].
        /// Used by ParsekFlight to decide whether facility visuals need
        /// patching when warp starts or ends.
        /// </summary>
        internal static bool HasFacilityActionsInRange(double fromUT, double toUT)
        {
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a.UT > fromUT && a.UT <= toUT &&
                    (a.Type == GameActionType.FacilityUpgrade ||
                     a.Type == GameActionType.FacilityDestruction ||
                     a.Type == GameActionType.FacilityRepair))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
