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
    internal static partial class LedgerOrchestrator
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
        // The set every production walk and probe passes to RecalculationEngine: the eight
        // modules above, registered by Initialize in tier order. Replaced, never shared, on
        // a reset.
        private static RecalculationModuleSet moduleSet = new RecalculationModuleSet();
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
        /// (recordings, ledger actions, milestone replay cursors, or event
        /// visibility) may have changed. Subscribed by ParsekUI to invalidate the
        /// TimelineWindowUI cache and by StockUiOverlayController to refresh open
        /// stock-screen badges.
        ///
        /// Future code that mutates MilestoneStore.LastReplayedEventIndex or
        /// GameStateStore event visibility must end with RecalculateAndPatch()
        /// or invoke this signal directly, otherwise open stock overlays can stay
        /// stale until their building is reopened.
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

            ReplaceModuleSet();

            kerbalsModule = new KerbalsModule();

            // Register in tier order per design doc 1.8
            moduleSet.Register(milestonesModule, RecalculationModuleSet.ModuleTier.FirstTier);
            moduleSet.Register(contractsModule, RecalculationModuleSet.ModuleTier.FirstTier);
            moduleSet.Register(scienceModule, RecalculationModuleSet.ModuleTier.FirstTier);
            moduleSet.Register(kerbalsModule, RecalculationModuleSet.ModuleTier.FirstTier);

            // Strategy transform between tiers
            moduleSet.Register(strategiesModule, RecalculationModuleSet.ModuleTier.Strategy);

            // Second tier
            moduleSet.Register(fundsModule, RecalculationModuleSet.ModuleTier.SecondTier);
            moduleSet.Register(reputationModule, RecalculationModuleSet.ModuleTier.SecondTier);

            // Facilities parallel
            moduleSet.Register(facilitiesModule, RecalculationModuleSet.ModuleTier.Facilities);

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
        /// Ensures the kerbal module exists before queuing merge-tombstone roster
        /// cleanup work. Does not reinitialize a partially initialized orchestrator.
        /// </summary>
        internal static bool TryEnsureKerbalsModuleForTombstoneRosterCleanup()
        {
            if (kerbalsModule != null)
                return true;

            if (initialized)
            {
                ParsekLog.Warn(Tag,
                    "Tombstoned roster cleanup queue skipped: LedgerOrchestrator is initialized but KerbalsModule is missing");
                return false;
            }

            ParsekLog.Warn(Tag,
                "Tombstoned roster cleanup requested before LedgerOrchestrator.Initialize; initializing modules before queueing cleanup");
            Initialize();

            if (kerbalsModule != null)
                return true;

            ParsekLog.Warn(Tag,
                "Tombstoned roster cleanup queue skipped: KerbalsModule unavailable after initialization");
            return false;
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

        /// <summary>
        /// Reputation tolerance of the earnings-window comparison, and of the
        /// kerbal-death pairing that decides what that comparison may drop. The two
        /// share one constant on purpose: a pair is excluded only when it would have
        /// compared equal anyway, so the exclusion can never hide a rep discrepancy the
        /// walk would otherwise have WARNed about. Kept at the historical <c>0.1f</c>
        /// literal so the comparison is bit-for-bit what it was.
        /// </summary>
        internal const double EarningsWindowRepTolerance = 0.1f;

        /// <summary>Reason of the walk <see cref="NotifyLedgerTreeCommitted"/> runs after filing a tree.</summary>
        internal const string TreeCommitRecalcReason = "tree-commit";

        /// <summary>Reason of the walk <see cref="NotifyLedgerTreeCommitted"/> runs over the rows a
        /// failed tree commit had already filed.</summary>
        internal const string TreeCommitFailedRecalcReason = "tree-commit-failed";

        /// <summary>Reason of the walk <see cref="OnRecordingCommitted"/> runs after filing one recording.</summary>
        internal const string RecordingCommitRecalcReason = "recording-commit";

        /// <summary>
        /// The pending release that last triggered <see cref="RecalculateIfKerbalReservationReleaseDue"/>,
        /// so a walk that for any reason does not move the release cannot re-trigger it
        /// every frame. NaN = none yet.
        /// </summary>
        private static double lastTriggeredReservationReleaseUT = double.NaN;

        /// <summary><see cref="KerbalsModule.PostWalkCount"/> right after the recalculation
        /// the last trigger ran.</summary>
        private static int postWalkCountAfterReservationReleaseTrigger = -1;

        // Committed-future rows at the last applied patch (ActionIds of rows past that
        // patch's cutoff). A clock-advance recalc that reaches some of them may pass the
        // drawdown guard; see ComputeClockCrossing.
        private static readonly HashSet<string> committedFutureActionIdsAtLastPatch =
            new HashSet<string>(StringComparer.Ordinal);
        private static bool committedFutureSnapshotValid;
        private static bool clockAdvanceRecalcInProgress;

        private const double InitialResourceBaselineMaxUtSeconds = 1.0;

        /// <summary>Test seam replacing <see cref="ReadProgressSeedProbe"/>. Cleared by ResetForTesting.</summary>
        internal static Func<ProgressSeedProbe> ProgressSeedProbeForTesting;

        /// <summary>Test seam replacing <see cref="ReadCurrencyPoolProbe"/>. Cleared by ResetForTesting.</summary>
        internal static Func<CurrencyPoolProbe> CurrencyPoolProbeForTesting;

        /// <summary>Test seam replacing the uncommitted-recording read a live-pool
        /// reputation seed records. Cleared by ResetForTesting.</summary>
        internal static Func<List<string>> UncommittedRecordingIdsForTesting;

        /// <summary>Test seam replacing the loaded save's <c>flightState.universalTime</c>
        /// read. Cleared by ResetForTesting.</summary>
        internal static Func<double> LoadedFlightStateUTForTesting;

        /// <summary>Test seam replacing the live game-mode read the baseline presence
        /// decision consults. Cleared by ResetForTesting.</summary>
        internal static Func<CurrencySingletons> ExpectedCurrencySingletonsForTesting;

        /// <summary>Why <see cref="PlanReachedCommittedDismissals"/> planned nothing.</summary>
        internal const string DismissalSkipRewindPending = "rewind-ut-adjustment-pending";
        internal const string DismissalSkipClockNotReady = "clock-not-ready";

        internal const string HeldLedgerPatchDeferralReason = "ledger held from a newer Parsek version";

        /// <summary>
        /// Test-only one-frame defer host. Non-null replaces
        /// <c>WarpToTimeConsumer.RunNextFrame</c> in <see cref="OnKscSpending"/> so a test
        /// can prove the recalc really is postponed rather than asserting about the
        /// predicate alone - the real host is a MonoBehaviour singleton and cannot exist
        /// headless, which would otherwise leave the deferral's CALL SITE unpinned.
        /// Cleared by <c>ResetForTesting</c>.
        /// </summary>
        internal static Action<Action> DeferOneFrameForTesting;

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
        /// Returns the current universal time for affordability-helper UT cutoffs: the
        /// ledger's now (<see cref="LedgerHeldWindow.ResolveLedgerNowUT"/>) over
        /// <see cref="GetRawClockUT"/>.
        /// </summary>
        private static double GetNowUT()
        {
            return LedgerHeldWindow.ResolveLedgerNowUT(GetRawClockUT());
        }

        /// <summary>
        /// The raw game clock, for readers that pair it with game-state events captured at it
        /// (the pending-recent-action helpers), never the ledger's held now. Production path
        /// reads <see cref="Planetarium.GetUniversalTime"/>; the test seam
        /// <see cref="NowUtProviderForTesting"/> overrides when set.
        /// </summary>
        private static double GetRawClockUT()
        {
            var provider = NowUtProviderForTesting;
            if (provider != null)
                return provider();
            return Planetarium.GetUniversalTime();
        }

        /// <summary>
        /// Production reset for a cold load (<c>ParsekScenario.ResetSaveScopedSessionState</c>):
        /// the session statics that describe the loaded save (initialization and its modules,
        /// seed flags, the KSC sequence counter, the committed-future snapshot, the strategy
        /// debit baseline, the reservation-release trigger, the pending recovery funds and
        /// consumed keys). Leaves the test seams, the timeline subscription and the log
        /// dedup sets alone. The caller re-initializes.
        /// </summary>
        internal static void ResetForSaveLoad()
        {
            initialized = false;
            scienceModule = null;
            milestonesModule = null;
            contractsModule = null;
            fundsModule = null;
            reputationModule = null;
            facilitiesModule = null;
            strategiesModule = null;
            kerbalsModule = null;
            ReplaceModuleSet();
            fundsSeedDone = false;
            scienceSeedDone = false;
            repSeedDone = false;
            kscSequenceCounter = 0;
            LedgerRecoveryFundsPairing.ResetForSaveLoad();
            CommittedFutureIndexCache.Invalidate("save-scoped-reset");
            lastTriggeredReservationReleaseUT = double.NaN;
            postWalkCountAfterReservationReleaseTrigger = -1;
            committedFutureActionIdsAtLastPatch.Clear();
            committedFutureSnapshotValid = false;
        }

        /// <summary>Reset all state for testing.</summary>
        internal static void ResetForTesting()
        {
            initialized = false;
            CurrencyPoolProbeForTesting = null;
            UncommittedRecordingIdsForTesting = null;
            LoadedFlightStateUTForTesting = null;
            ExpectedCurrencySingletonsForTesting = null;
            ProgressSeedProbeForTesting = null;
            fundsSeedDone = false;
            scienceSeedDone = false;
            repSeedDone = false;
            fundsTrackedOverrideForTesting = null;
            scienceTrackedOverrideForTesting = null;
            repTrackedOverrideForTesting = null;
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
            ReplaceModuleSet();
            Ledger.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            OnTimelineDataChanged = null;
            OnRecordingCommittedFaultInjector = null;
            OnRecordingCommittedPostSciencePersistFaultInjector = null;
            NowUtProviderForTesting = null;
            CommittedFutureIndexCache.ResetForTesting();
            DeferOneFrameForTesting = null;
            KerbalsModule.LiveClockUTProviderForTesting = null;
            KerbalsModule.LoadedSaveUTProviderForTesting = null;
            lastTriggeredReservationReleaseUT = double.NaN;
            postWalkCountAfterReservationReleaseTrigger = -1;
            committedFutureActionIdsAtLastPatch.Clear();
            committedFutureSnapshotValid = false;
            clockAdvanceRecalcInProgress = false;
            ParsekLog.Verbose(Tag, "ResetForTesting: all state cleared");
        }

        // ================================================================
        // Dynamic slot limits
        // ================================================================

        /// <summary>
        /// Updates ContractsModule and StrategiesModule max slot counts based on
        /// current facility levels tracked by FacilitiesModule. Called around each
        /// recalculation walk so final slot limits reflect the just-walked facility state.
        /// </summary>
        private static void UpdateSlotLimitsFromFacilities()
        {
            if (facilitiesModule == null || contractsModule == null || strategiesModule == null)
                return;

            int missionControlLevel = GetUpgradeableFacilityLevel("MissionControl");
            int contractSlots = GetContractSlots(missionControlLevel);
            contractsModule.SetMaxSlots(contractSlots);

            int adminLevel = GetUpgradeableFacilityLevel("Administration");
            int strategySlots = GetStrategySlots(adminLevel);
            strategiesModule.SetMaxSlots(strategySlots);

            ParsekLog.Verbose(Tag,
                $"UpdateSlotLimits: MissionControl level={missionControlLevel} -> {contractSlots} contract slots, " +
                $"Administration level={adminLevel} -> {strategySlots} strategy slots");
        }

        private static int GetUpgradeableFacilityLevel(string displayFacilityId)
        {
            if (facilitiesModule == null)
                return 1;

            FacilitiesModule.FacilityState state;
            string kspFacilityId = KspFacilityIds.ToUpgradeableFacilityId(displayFacilityId);
            if (facilitiesModule.TryGetFacilityState(kspFacilityId, out state))
                return state.Level;

            if (facilitiesModule.TryGetFacilityState(displayFacilityId, out state))
                return state.Level;

            return 1;
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

        /// <summary>
        /// The module set production walks: <see cref="Initialize"/> registers the eight
        /// modules in tier order; empty before it runs and after a reset. The read-only probes
        /// and clone walks outside this class (click reservation, Re-Fly seal credit, merge
        /// dominance) pass it too.
        /// </summary>
        internal static RecalculationModuleSet Modules => moduleSet;

        /// <summary>
        /// Drops the module set for a fresh empty one (a reset never mutates the set a caller
        /// may still hold) and logs what it removed.
        /// </summary>
        private static void ReplaceModuleSet()
        {
            int removed = RecalculationModuleSet.CountOf(moduleSet);
            moduleSet = new RecalculationModuleSet();
            RecalculationModuleSet.LogCleared(removed);
        }

        internal static bool IsInitialized => initialized;

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
