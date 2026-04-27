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
        private const string Tag = "LedgerOrchestrator";

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

        /// <summary>
        /// Set to <c>true</c> by <see cref="MigrateOldSaveEvents"/> when — and only when —
        /// it actually synthesized at least one action from <see cref="GameStateStore.Events"/>.
        /// Reset to <c>false</c> at the top of every <see cref="OnKspLoad"/>.
        ///
        /// <para>Used by <see cref="HasAnyLedgerCoverage"/> to gate the null-RecordingId-in-window
        /// coverage rule. Round-2 external review (PR #347) found that
        /// <see cref="TryRecoverBrokenLedgerOnLoad"/> synthesizes null-tagged
        /// <see cref="GameActionType.ContractAccept"/>/<see cref="GameActionType.ContractComplete"/>/<see cref="GameActionType.ContractFail"/>/<see cref="GameActionType.ContractCancel"/>/<see cref="GameActionType.FundsSpending"/>
        /// actions from the global <see cref="GameStateStore"/> — and on subsequent loads,
        /// those previously-persisted null-tagged actions sit in the ledger at UTs that may
        /// fall inside a legacy tree's window. A multi-day mission can easily overlap with
        /// unrelated KSC activity (player accepts a contract, buys a part, cancels a
        /// contract). The coverage probe saw those actions and incorrectly concluded
        /// "covered" → skipped migration → legacy residual silently dropped.</para>
        ///
        /// <para>The flag distinguishes "null-tagged because the first-load
        /// MigrateOldSaveEvents just synthesized these" from "null-tagged because normal
        /// KSC activity". Only the first case should register as coverage (to prevent
        /// the first-load double-credit that round-3 of Phase A was designed to avoid).</para>
        /// </summary>
        private static bool migrateOldSaveEventsRanThisLoad;

        /// <summary>
        /// Test-only setter for <see cref="migrateOldSaveEventsRanThisLoad"/>. Production
        /// code sets this flag via <see cref="MigrateOldSaveEvents"/> and resets it at the
        /// top of <see cref="OnKspLoad"/>; tests bypass that flow by calling this helper.
        /// </summary>
        internal static void SetMigrateOldSaveEventsRanThisLoadForTesting(bool value)
        {
            migrateOldSaveEventsRanThisLoad = value;
        }

        /// <summary>Test-only getter paired with the setter above.</summary>
        internal static bool GetMigrateOldSaveEventsRanThisLoadForTesting()
        {
            return migrateOldSaveEventsRanThisLoad;
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

        // Shared with GameActionDisplay.IsUnclaimedRolloutAction (#452) so the
        // label-side predicate and the adoption-side producer/scan cannot drift.
        internal const string RolloutDedupPrefix = "rollout:";

        private struct RolloutAdoptionContext
        {
            public uint VesselPersistentId;
            public string VesselName;
            public string LaunchSiteName;
            public bool IsLegacyBareKey;
        }

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

        private static bool LogReconcileWarnOnce(string warnKey, string message)
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
            migrateOldSaveEventsRanThisLoad = false;
            consumedRecoveryEventKeys.Clear();
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

        /// <summary>
        /// Migration for old saves: converts existing GameStateStore events from committed
        /// recordings into GameActions and adds them to the ledger. Called once when an old
        /// save loads with committed recordings but no ledger file.
        /// </summary>
        private static void MigrateOldSaveEvents(HashSet<string> validRecordingIds)
        {
            var events = GameStateStore.Events;
            if (events == null || events.Count == 0)
            {
                ParsekLog.Info(Tag, "MigrateOldSaveEvents: no events to migrate");
                return;
            }

            // Convert events using the same converter path as normal commits.
            // Use null recordingId since we can't reliably map old events to specific recordings.
            double minUT = 0;
            double maxUT = double.MaxValue;
            var actions = GameStateEventConverter.ConvertEvents(events, null, minUT, maxUT);

            if (actions.Count > 0)
            {
                Ledger.AddActions(actions);
                // Signal to HasAnyLedgerCoverage that null-tagged in-window actions
                // synthesized on this load should count as coverage (to prevent the
                // first-load double-credit that Phase A round 3 was designed to avoid).
                // Only set on actual synthesis — an empty MigrateOldSaveEvents must NOT
                // make pre-existing null-tagged actions (from prior loads' recovery)
                // falsely count as coverage.
                migrateOldSaveEventsRanThisLoad = true;
                ParsekLog.Info(Tag,
                    $"MigrateOldSaveEvents: migrated {actions.Count} actions from {events.Count} events " +
                    $"(committed recordings: {validRecordingIds.Count})");
            }
            else
            {
                ParsekLog.Info(Tag,
                    $"MigrateOldSaveEvents: {events.Count} events produced 0 convertible actions");
            }
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

        /// <summary>
        /// Tolerance (absolute value) below which a persisted legacy-tree delta is
        /// treated as zero and NOT synthesized. Matches field precision of the
        /// persisted `deltaFunds` / `deltaScience` / `deltaReputation` values with
        /// some slack for floating-point drift across save/load.
        /// </summary>
        private const double LegacyMigrationFundsTolerance = 1.0;
        private const double LegacyMigrationScienceTolerance = 0.1;
        private const double LegacyMigrationReputationTolerance = 0.1;
        private const string LegacyFormatGateTag = "LegacyFormatGate";

        /// <summary>
        /// Phase A of the ledger / lump-sum resource reconciliation fix
        /// (<c>docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md</c>). Walks the
        /// committed trees and, for each tree whose <see cref="RecordingTree.Load"/>
        /// hydrated a transient legacy residual with any persisted
        /// <c>deltaFunds</c>/<c>deltaScience</c>/<c>deltaReputation</c> outside
        /// tolerance, decides — per the reviewer's zero-coverage scope — either:
        /// <list type="bullet">
        ///   <item><description>
        ///   <b>Zero ledger coverage</b> (no ledger action tagged to any of the tree's
        ///   recordings falls inside the tree's UT window): inject the FULL persisted
        ///   delta as <see cref="FundsEarningSource.LegacyMigration"/> earnings tagged
        ///   with the tree's <see cref="RecordingTree.RootRecordingId"/>. This is the
        ///   stress-test repro path (prior-epoch events were skipped by
        ///   <see cref="TryRecoverBrokenLedgerOnLoad"/>'s epoch gate, so the ledger
        ///   has nothing for those trees).
        ///   </description></item>
        ///   <item><description>
        ///   <b>Any ledger coverage</b> (partial — at least one tagged action in window):
        ///   do NOT inject. Comparing the pre-walk raw field sums to post-walk,
        ///   post-transform, post-curve-and-cap persisted tree deltas is structurally
        ///   wrong (ScienceModule's subject cap, ReputationModule's non-linear curve,
        ///   StrategiesModule's reward transform, and Effective=false duplicate
        ///   suppression all run between the two sides). Log WARN and rely on
        ///   the existing ledger coverage as best-effort; Phase F's format-version
        ///   gate catches anything that slips through to deletion.
        ///   </description></item>
        /// </list>
        ///
        /// <para>Degraded tree (<see cref="RecordingTree.ComputeEndUT"/> returns 0 —
        /// trajectory data missing): mark recordings fully applied, log INFO, no
        /// injection.</para>
        ///
        /// <para>Empty <c>RootRecordingId</c>: mark recordings fully applied, log
        /// WARN. No synthetic — no correct recording id to tag with. Data loss for
        /// the rare empty-root case is preferable to silent residual loss without a
        /// warning.</para>
        ///
        /// <para>Regardless of branch, the tree's recordings are marked fully applied
        /// via <see cref="RecordingStore.MarkTreeAsApplied"/> so budget reservation does
        /// not re-hold already-live resources. Idempotent: after the transient residual
        /// is consumed, repeat runs on the same in-memory tree are no-ops. Reloading the
        /// same legacy save is guarded by existing LegacyMigration synthetic detection.</para>
        /// </summary>
        internal static void MigrateLegacyTreeResources()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null || trees.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    "MigrateLegacyTreeResources: no committed trees, nothing to migrate");
                return;
            }

            int treesConsidered = 0;
            int treesWithoutLegacyResidual = 0;
            int treesAlreadyApplied = 0;
            int treesAlreadyMigrated = 0;
            int treesSkippedNoDelta = 0;
            int treesSkippedEmptyRoot = 0;
            int treesSkippedDegraded = 0;
            int treesSkippedPartialCoverage = 0;
            int treesMigrated = 0;
            int treesWarnedByGate = 0;
            int fundsInjected = 0;
            int scienceInjected = 0;
            int repInjected = 0;

            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree == null) continue;

                var residual = tree.ConsumeLegacyResidual();
                if (residual == null)
                {
                    treesWithoutLegacyResidual++;
                    continue;
                }

                treesConsidered++;

                bool fundsNonZero = Math.Abs(residual.DeltaFunds) > LegacyMigrationFundsTolerance;
                bool scienceNonZero = Math.Abs(residual.DeltaScience) > LegacyMigrationScienceTolerance;
                bool repNonZero = Math.Abs((double)residual.DeltaReputation) > LegacyMigrationReputationTolerance;
                bool anyNonZeroResidual = fundsNonZero || scienceNonZero || repNonZero;

                if (residual.ResourcesApplied)
                {
                    treesAlreadyApplied++;
                    RecordingStore.MarkTreeAsApplied(tree);
                    continue;
                }

                if (!anyNonZeroResidual)
                {
                    treesSkippedNoDelta++;
                    ParsekLog.Verbose(Tag,
                        $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                        $"all deltas within tolerance (funds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"science={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"rep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)}), " +
                        "marking recordings fully applied without synthetic injection");
                    RecordingStore.MarkTreeAsApplied(tree);
                    continue;
                }

                double endUT = tree.ComputeEndUT();
                bool hasFundsSynthetic;
                bool hasScienceSynthetic;
                bool hasRepSynthetic;
                FindLegacyMigrationSyntheticMatches(
                    tree,
                    residual,
                    endUT,
                    out hasFundsSynthetic,
                    out hasScienceSynthetic,
                    out hasRepSynthetic);

                if ((!fundsNonZero || hasFundsSynthetic)
                    && (!scienceNonZero || hasScienceSynthetic)
                    && (!repNonZero || hasRepSynthetic))
                {
                    treesAlreadyMigrated++;
                    ParsekLog.Verbose(Tag,
                        $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                        "already has a LegacyMigration synthetic in the ledger; skipping duplicate injection");
                    RecordingStore.MarkTreeAsApplied(tree);
                    continue;
                }

                bool warnByFormatGate = false;
                if (endUT == 0)
                {
                    treesSkippedDegraded++;
                    warnByFormatGate = tree.TreeFormatVersion < RecordingTree.CurrentTreeFormatVersion;
                    ParsekLog.Info(Tag,
                        $"MigrateLegacyTreeResources: skipping degraded tree id='{tree.Id}' " +
                        $"name='{tree.TreeName}' — ComputeEndUT()==0 (no trajectory data), " +
                        "marking recordings fully applied without synthetic injection " +
                        $"(stale deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)})");
                    RecordingStore.MarkTreeAsApplied(tree);
                }
                else if (string.IsNullOrEmpty(tree.RootRecordingId))
                {
                    treesSkippedEmptyRoot++;
                    warnByFormatGate = tree.TreeFormatVersion < RecordingTree.CurrentTreeFormatVersion;
                    ParsekLog.Warn(Tag,
                        $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                        $"has empty RootRecordingId — cannot tag synthetic ledger actions; " +
                        "marking recordings fully applied without synthetic injection " +
                        $"(persisted residuals LOST: deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                        $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)})");
                    RecordingStore.MarkTreeAsApplied(tree);
                }
                else
                {
                    double startUT = ComputeTreeStartUT(tree);
                    if (HasAnyLedgerCoverage(tree, residual, startUT, endUT))
                    {
                        treesSkippedPartialCoverage++;
                        warnByFormatGate = tree.TreeFormatVersion < RecordingTree.CurrentTreeFormatVersion;
                        ParsekLog.Warn(Tag,
                            $"MigrateLegacyTreeResources: tree id='{tree.Id}' name='{tree.TreeName}' " +
                            $"has partial ledger coverage in UT window [{startUT.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"{endUT.ToString("R", CultureInfo.InvariantCulture)}]; skipping synthetic injection " +
                            $"(persisted deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                            $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)})");
                        RecordingStore.MarkTreeAsApplied(tree);
                    }
                    else
                    {
                        bool injectedFunds = false;
                        bool injectedScience = false;
                        bool injectedRep = false;

                        if (fundsNonZero
                            && !hasFundsSynthetic
                            && TryInjectLegacyFundsEarning(tree, residual, endUT))
                        {
                            fundsInjected++;
                            injectedFunds = true;
                        }

                        if (scienceNonZero && !hasScienceSynthetic)
                        {
                            if (residual.DeltaScience > 0)
                                InjectLegacyScienceEarning(tree, residual.DeltaScience, endUT);
                            else
                                InjectLegacyScienceSpending(tree, residual.DeltaScience, endUT);
                            scienceInjected++;
                            injectedScience = true;
                        }

                        if (repNonZero && !hasRepSynthetic)
                        {
                            if (residual.DeltaReputation > 0)
                                InjectLegacyReputationEarning(tree, (double)residual.DeltaReputation, endUT);
                            else
                                InjectLegacyReputationPenalty(tree, (double)residual.DeltaReputation, endUT);
                            repInjected++;
                            injectedRep = true;
                        }

                        ParsekLog.Info(Tag,
                            $"MigrateLegacyTreeResources: migrated tree id='{tree.Id}' name='{tree.TreeName}' " +
                            $"rootRec='{tree.RootRecordingId}' " +
                            $"startUT={startUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"endUT={endUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            "coverage=zero " +
                            $"deltaFunds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"(injected={injectedFunds}), " +
                            $"deltaScience={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"(injected={injectedScience}), " +
                            $"deltaRep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)} " +
                            $"(injected={injectedRep})");

                        RecordingStore.MarkTreeAsApplied(tree);
                        treesMigrated++;
                    }
                }

                if (warnByFormatGate)
                {
                    WarnLegacyFormatGate(tree, residual);
                    treesWarnedByGate++;
                }
            }

            ParsekLog.Info(Tag,
                $"MigrateLegacyTreeResources complete: considered={treesConsidered}, " +
                $"withoutResidual={treesWithoutLegacyResidual}, alreadyApplied={treesAlreadyApplied}, " +
                $"alreadyMigrated={treesAlreadyMigrated}, degraded={treesSkippedDegraded}, " +
                $"noDelta={treesSkippedNoDelta}, emptyRoot={treesSkippedEmptyRoot}, " +
                $"partialCoverage={treesSkippedPartialCoverage}, " +
                $"migrated={treesMigrated}, gateWarned={treesWarnedByGate}, " +
                $"fundsInjected={fundsInjected}, scienceInjected={scienceInjected}, " +
                $"repInjected={repInjected}");
        }

        /// <summary>
        /// Funds-emission helper: injects a <see cref="FundsEarningSource.LegacyMigration"/>
        /// FundsEarning with the raw (signed) persisted legacy residual.
        /// Returns true if a non-zero funds synthetic was emitted.
        ///
        /// <para>Sign note: <see cref="FundsModule.ProcessFundsEarning"/> adds the raw
        /// FundsAwarded to both <c>runningBalance</c> and <c>totalEarnings</c> with no clamp,
        /// so a negative FundsEarning correctly reduces both. The reviewer flagged an earlier
        /// concern about "poisoning totalEarnings" as wrong — a tree that net-lost funds
        /// really did reduce the player's income capacity, so a negative totalEarnings
        /// contribution is the right shape. Earning-type actions are pruned by RecordingId
        /// in <see cref="Ledger.Reconcile"/>, so the synthetic is cleaned up with the tree.</para>
        /// </summary>
        private static bool TryInjectLegacyFundsEarning(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            if (Math.Abs(residual.DeltaFunds) <= LegacyMigrationFundsTolerance)
                return false;

            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.FundsEarning,
                RecordingId = tree.RootRecordingId,
                FundsAwarded = (float)residual.DeltaFunds,
                FundsSource = FundsEarningSource.LegacyMigration
            };
            Ledger.AddAction(action);
            return true;
        }

        /// <summary>
        /// Science-emission helper: injects a <see cref="GameActionType.ScienceEarning"/>
        /// with <c>SubjectId="LegacyMigration:{treeId}"</c> (no source enum exists).
        /// Only called for positive residuals — negative science goes through
        /// <see cref="InjectLegacyScienceSpending"/> which emits a ScienceSpending instead.
        /// A generous <see cref="GameAction.SubjectMaxValue"/> is set so the per-subject
        /// hard cap never clips the migrated science on the walk.
        ///
        /// <para>Note on ContractComplete / strategy transforms (per reviewer NIT):
        /// the ContractComplete reward field used by partial-coverage residual math would
        /// need to be <c>TransformedScienceReward</c> once Phase E1.5 lands strategy
        /// capture — <c>ScienceReward</c> and <c>TransformedScienceReward</c> will diverge
        /// then. The zero-coverage scope sidesteps that problem entirely: we do not read
        /// ContractComplete fields at all, we only probe for presence.</para>
        /// </summary>
        private static void InjectLegacyScienceEarning(RecordingTree tree, double scienceDelta, double endUT)
        {
            float residualF = (float)scienceDelta;
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ScienceEarning,
                RecordingId = tree.RootRecordingId,
                SubjectId = "LegacyMigration:" + tree.Id,
                ScienceAwarded = residualF,
                SubjectMaxValue = residualF + 10f
            };
            Ledger.AddAction(action);
        }

        /// <summary>
        /// Science-spending helper for negative residuals: injects a
        /// <see cref="GameActionType.ScienceSpending"/> with <c>Cost=-scienceDelta</c>
        /// (magnitude, since <see cref="ScienceModule.ProcessSpending"/> subtracts
        /// <c>Cost</c> from the running balance). <see cref="Ledger.Reconcile"/> prunes
        /// spendings by RecordingId since #441, so this synthetic is purged with the tree
        /// on deletion — same contract as the positive-residual earning.
        /// </summary>
        private static void InjectLegacyScienceSpending(RecordingTree tree, double scienceDelta, double endUT)
        {
            float costF = (float)(-scienceDelta);
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ScienceSpending,
                RecordingId = tree.RootRecordingId,
                NodeId = "LegacyMigration:" + tree.Id,
                Cost = costF
            };
            Ledger.AddAction(action);
        }

        /// <summary>
        /// Reputation-emission helper: injects a <see cref="GameActionType.ReputationEarning"/>
        /// with <see cref="ReputationSource.Other"/>. Only called for positive residuals —
        /// negative rep goes through <see cref="InjectLegacyReputationPenalty"/> which emits
        /// a ReputationPenalty instead.
        /// </summary>
        private static void InjectLegacyReputationEarning(RecordingTree tree, double repDelta, double endUT)
        {
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ReputationEarning,
                RecordingId = tree.RootRecordingId,
                NominalRep = (float)repDelta,
                RepSource = ReputationSource.Other
            };
            Ledger.AddAction(action);
        }

        /// <summary>
        /// Reputation-penalty helper for negative residuals: injects a
        /// <see cref="GameActionType.ReputationPenalty"/> with <c>NominalPenalty=-repDelta</c>
        /// (magnitude; <see cref="ReputationModule"/> stores penalty as a positive magnitude
        /// and negates it internally before applying the curve).
        /// <see cref="ReputationPenaltySource.Other"/> matches the earning helper's
        /// <see cref="ReputationSource.Other"/>. <see cref="Ledger.Reconcile"/> prunes
        /// spendings by RecordingId since #441.
        /// </summary>
        private static void InjectLegacyReputationPenalty(RecordingTree tree, double repDelta, double endUT)
        {
            var action = new GameAction
            {
                UT = endUT,
                Type = GameActionType.ReputationPenalty,
                RecordingId = tree.RootRecordingId,
                NominalPenalty = (float)(-repDelta),
                RepPenaltySource = ReputationPenaltySource.Other
            };
            Ledger.AddAction(action);
        }

        private static void FindLegacyMigrationSyntheticMatches(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT,
            out bool hasFundsSynthetic,
            out bool hasScienceSynthetic,
            out bool hasRepSynthetic)
        {
            bool fundsNonZero = Math.Abs(residual.DeltaFunds) > LegacyMigrationFundsTolerance;
            bool scienceNonZero = Math.Abs(residual.DeltaScience) > LegacyMigrationScienceTolerance;
            bool repNonZero = Math.Abs((double)residual.DeltaReputation) > LegacyMigrationReputationTolerance;
            hasFundsSynthetic = !fundsNonZero;
            hasScienceSynthetic = !scienceNonZero;
            hasRepSynthetic = !repNonZero;
            if (hasFundsSynthetic && hasScienceSynthetic && hasRepSynthetic)
                return;

            string scienceKey = "LegacyMigration:" + tree.Id;
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null)
                    continue;

                if (!hasFundsSynthetic
                    && IsMatchingLegacyFundsMigration(action, tree, residual, endUT))
                {
                    hasFundsSynthetic = true;
                }

                if (!hasScienceSynthetic)
                {
                    if (IsMatchingLegacyScienceMigration(action, tree, residual, endUT))
                    {
                        hasScienceSynthetic = true;
                    }
                }

                if (!hasRepSynthetic
                    && IsMatchingLegacyReputationMigration(action, tree, residual, endUT))
                {
                    hasRepSynthetic = true;
                }

                if (hasFundsSynthetic && hasScienceSynthetic && hasRepSynthetic)
                    return;
            }
        }

        private static void WarnLegacyFormatGate(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual)
        {
            string treeId = !string.IsNullOrEmpty(tree.Id)
                ? tree.Id
                : (tree.TreeName ?? "(unknown)");

            ParsekLog.Warn(LegacyFormatGateTag,
                $"Tree '{treeId}' has pre-Phase-F legacy resource fields " +
                $"(funds={residual.DeltaFunds.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"sci={residual.DeltaScience.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"rep={residual.DeltaReputation.ToString("R", CultureInfo.InvariantCulture)}) " +
                "that Phase A migration could not recover. Legacy resource values will " +
                "not be applied; ledger state may differ by this residual. Open the save " +
                "in a 0.8.1 build first if accurate migration matters.");
        }

        /// <summary>
        /// Returns the earliest non-zero StartUT across the tree's recordings, or 0 if
        /// no recording has points. Needed as a conservative lower bound when matching
        /// ledger actions back to a tree's UT window; <see cref="RecordingTree"/> does
        /// not carry a stored StartUT field.
        /// </summary>
        private static double ComputeTreeStartUT(RecordingTree tree)
        {
            double startUT = double.MaxValue;
            bool any = false;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null) continue;
                double recStart = rec.StartUT;
                if (recStart <= 0) continue;
                if (recStart < startUT)
                {
                    startUT = recStart;
                    any = true;
                }
            }
            return any ? startUT : 0.0;
        }

        /// <summary>
        /// Pure classifier: returns true when the given action type actually moves the
        /// funds/science/reputation pools (so its presence inside a tree's UT window counts
        /// as ledger coverage for that tree). Non-resource action types — roster changes
        /// (<see cref="GameActionType.KerbalAssignment"/>, <see cref="GameActionType.KerbalRescue"/>,
        /// <see cref="GameActionType.KerbalStandIn"/>), <see cref="GameActionType.FacilityDestruction"/>
        /// (stateful only, no cost), <see cref="GameActionType.StrategyDeactivate"/> (stateful only,
        /// no cost), and the three <c>*Initial</c> seed types — return false.
        ///
        /// <para>This classification exists specifically so the
        /// <see cref="HasAnyLedgerCoverage"/> probe is not tripped by
        /// <see cref="MigrateKerbalAssignments"/>'s backfilled <c>KerbalAssignment</c> rows,
        /// which run earlier in <see cref="OnKspLoad"/> and would otherwise flag every
        /// crewed legacy tree as partially covered, permanently dropping the residual.</para>
        ///
        /// <para>When adding a new <see cref="GameActionType"/>, this switch must be updated
        /// and the <c>IsResourceImpactingAction_Theory</c> test in
        /// <c>LegacyTreeMigrationTests.cs</c> will fail until an entry is added — which is
        /// the intended forcing function for a code review.</para>
        /// </summary>
        internal static bool IsResourceImpactingAction(GameActionType t)
        {
            switch (t)
            {
                // Earnings/spendings that directly move a pool.
                case GameActionType.FundsEarning:
                case GameActionType.FundsSpending:
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceSpending:
                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    return true;

                // Composite rewards/penalties consumed by first-tier + second-tier modules.
                case GameActionType.MilestoneAchievement:  // MilestoneFunds/Sci/RepAwarded
                case GameActionType.ContractAccept:        // AdvanceFunds
                case GameActionType.ContractComplete:      // FundsReward/ScienceReward/RepReward
                case GameActionType.ContractFail:          // FundsPenalty/RepPenalty
                case GameActionType.ContractCancel:        // FundsPenalty/RepPenalty
                    return true;

                // Infrastructure/strategy costs consumed by FundsModule / StrategiesModule.
                case GameActionType.FacilityUpgrade:       // FacilityCost
                case GameActionType.FacilityRepair:        // FacilityCost
                case GameActionType.KerbalHire:            // HireCost
                case GameActionType.StrategyActivate:      // SetupCost
                    return true;

                // Non-resource action types: roster changes, stateful facility/strategy flips,
                // and the immutable seed rows. These are intentionally ignored by the
                // coverage probe — see class summary for the false-positive this guards.
                case GameActionType.KerbalAssignment:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                case GameActionType.FacilityDestruction:
                case GameActionType.StrategyDeactivate:
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return false;

                default:
                    // Conservative: an unrecognized new action type is treated as non-
                    // resource so it cannot poison the zero-coverage probe. The
                    // IsResourceImpactingAction_Theory test will flag any new enum value
                    // that reaches this branch.
                    return false;
            }
        }

        /// <summary>
        /// Zero-coverage probe: returns true if <see cref="Ledger.Actions"/> contains at
        /// least one <see cref="IsResourceImpactingAction"/>-passing action whose UT falls
        /// inside <paramref name="startUT"/>..<paramref name="endUT"/> AND whose
        /// <see cref="GameAction.RecordingId"/> is either a key in
        /// <see cref="RecordingTree.Recordings"/> OR null (null-tagged actions may be
        /// <see cref="MigrateOldSaveEvents"/> output — see the flag gate below).
        ///
        /// <para>Two round-2 P1s this shape fixes:</para>
        /// <list type="number">
        ///   <item><description><see cref="MigrateKerbalAssignments"/>'s
        ///   <see cref="GameActionType.KerbalAssignment"/> rows (tagged with recording id,
        ///   added before this migration runs) are excluded by the type filter — without
        ///   this, every crewed legacy tree would be falsely flagged as partially covered
        ///   and its residual silently dropped.</description></item>
        ///   <item><description><see cref="MigrateOldSaveEvents"/>'s null-tagged reward
        ///   synthetics (ContractComplete / MilestoneAchievement / ContractAccept from
        ///   GameStateStore on first load of a pre-ledger save) register as coverage ONLY
        ///   when <see cref="migrateOldSaveEventsRanThisLoad"/> is true — otherwise an
        ///   uncrewed legacy tree on first load would get its full residual injected on
        ///   top of the already-migrated rewards, double-crediting.</description></item>
        /// </list>
        ///
        /// <para>Round-2 P1 (PR #347 external review) — null-tag gate:
        /// <see cref="TryRecoverBrokenLedgerOnLoad"/> runs on every load and persists
        /// null-tagged <see cref="GameActionType.ContractAccept"/>/<see cref="GameActionType.ContractComplete"/>/<see cref="GameActionType.ContractFail"/>/<see cref="GameActionType.ContractCancel"/>/<see cref="GameActionType.FundsSpending"/>
        /// actions into the ledger file. On a subsequent load of a long-running mission
        /// save, those previously-persisted null-tagged KSC actions can land at UTs
        /// inside the legacy tree's window (multi-day flight spanning normal KSC
        /// activity: contract accept, part purchase, contract cancel). Without the
        /// flag gate, the probe sees them and falsely concludes the tree is covered —
        /// residual silently lost. With the gate, null-tagged in-window actions only
        /// count when <see cref="MigrateOldSaveEvents"/> actually synthesized actions
        /// on THIS load (the case where the first-load double-credit protection is
        /// needed); in every other case they are ignored, and the tree gets its
        /// residual injected as intended.</para>
        ///
        /// <para>Replaces the v1 residual-math approach: persisted tree deltas were captured
        /// from LIVE KSP state post-commit (post-cap, post-curve, post-strategy-transform,
        /// post-Effective-suppression), so subtracting pre-walk raw field sums was
        /// structurally wrong for any tree with partial coverage.</para>
        ///
        /// <para>Pure; does not mutate state. Early-returns on first match.</para>
        /// </summary>
        private static bool HasAnyLedgerCoverage(
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double startUT,
            double endUT)
        {
            var recordings = tree.Recordings;
            if (recordings == null || recordings.Count == 0)
                return false;

            var ledgerActions = Ledger.Actions;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (action == null) continue;
                if (!IsResourceImpactingAction(action.Type)) continue;
                if (action.UT < startUT || action.UT > endUT) continue;

                string recId = action.RecordingId;
                bool nullTagged = string.IsNullOrEmpty(recId);
                bool treeTagged = !nullTagged && recordings.ContainsKey(recId);

                if (treeTagged)
                {
                    if (IsMatchingLegacyFundsMigration(action, tree, residual, endUT)
                        || IsMatchingLegacyScienceMigration(action, tree, residual, endUT)
                        || IsMatchingLegacyReputationMigration(action, tree, residual, endUT))
                    {
                        continue;
                    }

                    // Tree-tagged in-window action always counts.
                    return true;
                }
                if (nullTagged && migrateOldSaveEventsRanThisLoad)
                {
                    // Null-tagged action counts ONLY when MigrateOldSaveEvents just ran
                    // this load — see the flag's XML doc for why TryRecoverBrokenLedgerOnLoad
                    // output (persisted from prior loads) must NOT trigger this branch.
                    return true;
                }
                // Otherwise skip — non-tree-tagged (orphaned) actions and null-tagged
                // KSC activity during normal operation do not count as tree coverage.
            }
            return false;
        }

        private static bool IsMatchingLegacyFundsMigration(
            GameAction action,
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            return action != null
                && Math.Abs(residual.DeltaFunds) > LegacyMigrationFundsTolerance
                && action.Type == GameActionType.FundsEarning
                && action.FundsSource == FundsEarningSource.LegacyMigration
                && action.RecordingId == tree.RootRecordingId
                && Math.Abs(action.UT - endUT) <= 0.001
                && Math.Abs(action.FundsAwarded - (float)residual.DeltaFunds)
                    <= LegacyMigrationFundsTolerance;
        }

        private static bool IsMatchingLegacyScienceMigration(
            GameAction action,
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            if (action == null
                || Math.Abs(residual.DeltaScience) <= LegacyMigrationScienceTolerance
                || action.RecordingId != tree.RootRecordingId
                || Math.Abs(action.UT - endUT) > 0.001)
            {
                return false;
            }

            string scienceKey = "LegacyMigration:" + tree.Id;
            if (residual.DeltaScience > 0)
            {
                return action.Type == GameActionType.ScienceEarning
                    && action.SubjectId == scienceKey
                    && Math.Abs(action.ScienceAwarded - (float)residual.DeltaScience)
                        <= LegacyMigrationScienceTolerance;
            }

            return action.Type == GameActionType.ScienceSpending
                && action.NodeId == scienceKey
                && Math.Abs(action.Cost - (float)(-residual.DeltaScience))
                    <= LegacyMigrationScienceTolerance;
        }

        private static bool IsMatchingLegacyReputationMigration(
            GameAction action,
            RecordingTree tree,
            RecordingTree.LegacyResourceResidual residual,
            double endUT)
        {
            if (action == null
                || Math.Abs((double)residual.DeltaReputation) <= LegacyMigrationReputationTolerance
                || action.RecordingId != tree.RootRecordingId
                || Math.Abs(action.UT - endUT) > 0.001)
            {
                return false;
            }

            if (residual.DeltaReputation > 0)
            {
                return action.Type == GameActionType.ReputationEarning
                    && action.RepSource == ReputationSource.Other
                    && Math.Abs(action.NominalRep - (float)residual.DeltaReputation)
                        <= LegacyMigrationReputationTolerance;
            }

            return action.Type == GameActionType.ReputationPenalty
                && action.RepPenaltySource == ReputationPenaltySource.Other
                && Math.Abs(action.NominalPenalty - (float)(-residual.DeltaReputation))
                    <= LegacyMigrationReputationTolerance;
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

        /// <summary>
        /// Rewrites persisted legacy part-purchase ledger rows whose stored funds debit
        /// still reflects rollout <c>part.cost</c> instead of the historical R&amp;D
        /// charge proved by saved game-state events. Existing version-1 ledger files
        /// keep the same shape; the compatibility path mutates the in-memory actions
        /// after load and before the first recalculation walk. Ambiguous rows are
        /// preserved as-is rather than guessed from current runtime semantics; the
        /// only no-funds fallback is the known stock-bypass save shape that rewrites
        /// the matched <c>PartPurchased</c> event to zero-cost first.
        /// </summary>
        internal static int RepairLegacyPartPurchaseActionsOnLoad(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions)
        {
            if (ledgerActions == null || ledgerActions.Count == 0)
                return 0;

            int repaired = 0;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (!IsPartPurchaseFundsSpendingAction(action))
                    continue;

                float canonicalCost;
                if (!TryResolveCanonicalPartPurchaseChargeForAction(action, events, out canonicalCost))
                    continue;

                if (Math.Abs(action.FundsSpent - canonicalCost) <= 0.01f)
                    continue;

                action.FundsSpent = canonicalCost;
                repaired++;
            }

            return repaired;
        }

        private static bool IsPartPurchaseFundsSpendingAction(GameAction action)
        {
            return action != null &&
                   action.Type == GameActionType.FundsSpending &&
                   action.FundsSpendingSource == FundsSpendingSource.Other;
        }

        private static bool TryResolveCanonicalPartPurchaseChargeForAction(
            GameAction action,
            IReadOnlyList<GameStateEvent> events,
            out float canonicalCost)
        {
            canonicalCost = 0f;
            if (action == null)
                return false;

            GameStateEvent matchedEvent;
            if (TryFindMatchingPartPurchasedEvent(events, action, out matchedEvent))
            {
                GameStateEvent rewrittenEvent;
                if (GameStateStore.TryRewriteLegacyPartPurchaseEvent(
                    matchedEvent, events, out rewrittenEvent))
                {
                    matchedEvent = rewrittenEvent;
                }

                if (GameStateStore.TryGetStoredPartPurchaseCost(
                    matchedEvent.detail, out canonicalCost))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindMatchingPartPurchasedEvent(
            IReadOnlyList<GameStateEvent> events,
            GameAction action,
            out GameStateEvent matchedEvent)
        {
            matchedEvent = default(GameStateEvent);
            if (events == null || action == null)
                return false;

            string partName = action.DedupKey;
            int fallbackCount = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.PartPurchased)
                    continue;
                if (Math.Abs(evt.ut - action.UT) > KscReconcileEpsilonSeconds)
                    continue;

                if (!string.IsNullOrEmpty(partName))
                {
                    if (string.Equals(evt.key, partName, StringComparison.Ordinal))
                    {
                        matchedEvent = evt;
                        return true;
                    }

                    continue;
                }

                matchedEvent = evt;
                fallbackCount++;
                if (fallbackCount > 1)
                    return false;
            }

            return fallbackCount == 1;
        }

        /// <summary>
        /// One-shot save recovery migration for #401 (c1 bricked funds) and #396
        /// (sci1 bricked science). Walks the GameStateStore events and committed
        /// science subjects, synthesizes a matching GameAction for any entry that
        /// does NOT already have one in the ledger, and adds them.
        ///
        /// Must be called AFTER committed recordings (and any cold-start pending
        /// tree) have been restored, because the migration only acts on events
        /// whose recording tags are still visible in the current timeline.
        ///
        /// Idempotent: the LedgerHasMatchingAction guard makes repeat loads a no-op.
        /// </summary>
        internal static int TryRecoverBrokenLedgerOnLoad()
        {
            Initialize();
            GameStateStore.RepairLegacyPartPurchaseEventsForCurrentSemantics();

            int recoveredFundsParts = 0;
            int recoveredContracts = 0;
            int recoveredScience = 0;
            int skippedHidden = 0;

            // 1) Funds + contract events
            var events = GameStateStore.Events;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (!GameStateStore.IsEventVisibleToCurrentTimeline(evt))
                {
                    skippedHidden++;
                    continue;
                }
                if (!IsRecoverableEventType(evt.eventType)) continue;
                if (LedgerHasMatchingAction(evt)) continue;

                var action = GameStateEventConverter.ConvertEvent(evt, null);
                if (action == null) continue;

                Ledger.AddAction(action);
                if (evt.eventType == GameStateEventType.PartPurchased)
                    recoveredFundsParts++;
                else
                    recoveredContracts++;

                ParsekLog.Verbose(Tag,
                    $"TryRecoverBrokenLedgerOnLoad: synthesized {action.Type} action for " +
                    $"event {evt.eventType} key='{evt.key}' ut={evt.ut:F1}");
            }

            // 2) Committed science subjects
            // Use the persisted store as the recovery source of truth. Broken pre-#397
            // saves are missing ScienceEarning rows in the ledger; rebuilding the store
            // from the broken ledger here would erase the very totals we need to heal.
            // When some earnings already survive, synthesize only the missing delta so
            // cumulative subject totals do not double-count.
            var subjectIds = GameStateStore.GetCommittedScienceSubjectIds();
            if (subjectIds != null)
            {
                foreach (var subjectId in subjectIds)
                {
                    if (string.IsNullOrEmpty(subjectId)) continue;
                    if (!GameStateStore.TryGetCommittedSubjectScience(subjectId, out float sci)) continue;
                    if (sci <= 0f) continue;

                    float existingScience = GetLedgerScienceEarningTotal(subjectId);
                    float missingScience = sci - existingScience;
                    if (missingScience <= 0.01f) continue;

                    var action = new GameAction
                    {
                        // Science earnings need a UT for ordering; use the earliest
                        // pseudo-UT so the synthesized action slots in at the beginning
                        // of the currently-visible timeline.
                        UT = 0.0,
                        Type = GameActionType.ScienceEarning,
                        RecordingId = null,
                        SubjectId = subjectId,
                        ScienceAwarded = missingScience,
                        SubjectMaxValue = sci + 10f  // generous cap so walk doesn't clip
                    };
                    Ledger.AddAction(action);
                    recoveredScience++;

                    ParsekLog.Verbose(Tag,
                        $"TryRecoverBrokenLedgerOnLoad: synthesized ScienceEarning for " +
                        $"subject='{subjectId}' missingSci={missingScience:F1} " +
                        $"(stored={sci:F1}, existing={existingScience:F1})");
                }
            }

            int totalRecovered = recoveredFundsParts + recoveredContracts + recoveredScience;
            if (totalRecovered > 0)
            {
                ParsekLog.Warn(Tag,
                    $"TryRecoverBrokenLedgerOnLoad: synthesized {recoveredFundsParts} funds/part, " +
                    $"{recoveredContracts} contract, {recoveredScience} science actions from store " +
                    $"(skippedHidden={skippedHidden}).");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"TryRecoverBrokenLedgerOnLoad: no recovery needed " +
                    $"(skippedHidden={skippedHidden})");
            }

            return totalRecovered;
        }

        // Scope limit (deliberate): only contract state changes and part purchases
        // are migrated because those are the event types that #405/#404 actually
        // stripped from the ledger on c1/sci1 saves, so those are the ones a broken
        // save will be missing. MilestoneAchieved is excluded because historical
        // milestones were emitted with funds=0/rep=0 hardcoded — synthesizing them
        // would only add zero-reward actions. CrewHired, TechResearched, and
        // FacilityUpgraded are excluded because their real-time OnKscSpending writes
        // were never broken; c1/sci1 already have those actions. A future broken
        // save that surfaces missing hires/tech/facility actions would need this
        // list extended (see todo-and-known-bugs.md).
        /// <summary>Pure: event types the migration can synthesize actions for.</summary>
        internal static bool IsRecoverableEventType(GameStateEventType t)
        {
            switch (t)
            {
                case GameStateEventType.ContractAccepted:
                case GameStateEventType.ContractCompleted:
                case GameStateEventType.ContractFailed:
                case GameStateEventType.ContractCancelled:
                case GameStateEventType.PartPurchased:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure: checks whether the ledger already has an action matching the given
        /// event (same type + UT within epsilon + key). Used by the save recovery
        /// migration to avoid duplicating an action that already exists.
        /// </summary>
        internal static bool LedgerHasMatchingAction(GameStateEvent evt)
        {
            GameActionType? expectedType = MapEventTypeToActionType(evt.eventType);
            if (expectedType == null) return true;   // unmappable => don't recover

            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a.Type != expectedType.Value) continue;
                if (Math.Abs(a.UT - evt.ut) > 0.1) continue;

                // Match on the type-specific key field.
                switch (a.Type)
                {
                    case GameActionType.ContractAccept:
                    case GameActionType.ContractComplete:
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        if (a.ContractId == evt.key) return true;
                        break;
                    case GameActionType.FundsSpending:
                        // Part purchase: evt.key is the part name, stored on DedupKey.
                        if (a.DedupKey == evt.key) return true;
                        break;
                    default:
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Pure: checks whether the ledger already has a ScienceEarning action for
        /// the given subject with at least the given science value credited. Uses
        /// &gt;= comparison so multi-experiment top-ups don't synthesize duplicates.
        /// </summary>
        internal static bool LedgerHasMatchingScienceEarning(string subjectId, float minScience)
        {
            if (string.IsNullOrEmpty(subjectId)) return true;
            return GetLedgerScienceEarningTotal(subjectId) >= minScience - 0.01f;
        }

        /// <summary>
        /// Pure: returns the total ScienceAwarded already present in the ledger for a
        /// given subject. Used by save recovery to synthesize only the missing delta
        /// when a broken save kept some ScienceEarning rows but lost later top-ups.
        /// </summary>
        internal static float GetLedgerScienceEarningTotal(string subjectId)
        {
            if (string.IsNullOrEmpty(subjectId)) return 0f;

            float totalForSubject = 0f;
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a.Type != GameActionType.ScienceEarning) continue;
                if (a.SubjectId != subjectId) continue;
                totalForSubject += a.ScienceAwarded;
            }

            return totalForSubject;
        }

        /// <summary>Pure: maps GameStateEventType to the corresponding GameActionType.</summary>
        internal static GameActionType? MapEventTypeToActionType(GameStateEventType t)
        {
            switch (t)
            {
                case GameStateEventType.ContractAccepted: return GameActionType.ContractAccept;
                case GameStateEventType.ContractCompleted: return GameActionType.ContractComplete;
                case GameStateEventType.ContractFailed: return GameActionType.ContractFail;
                case GameStateEventType.ContractCancelled: return GameActionType.ContractCancel;
                case GameStateEventType.PartPurchased: return GameActionType.FundsSpending;
                case GameStateEventType.TechResearched: return GameActionType.ScienceSpending;
                case GameStateEventType.CrewHired: return GameActionType.KerbalHire;
                case GameStateEventType.MilestoneAchieved: return GameActionType.MilestoneAchievement;
                case GameStateEventType.FacilityUpgraded: return GameActionType.FacilityUpgrade;
                default: return null;
            }
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
            OnVesselRolloutSpending(ut, cost, ResolveCurrentRolloutAdoptionContext());
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
            OnVesselRolloutSpending(
                ut,
                cost,
                CreateRolloutAdoptionContext(vesselPersistentId, vesselName, launchSiteName));
        }

        private static void OnVesselRolloutSpending(double ut, double cost, RolloutAdoptionContext context)
        {
            Initialize();

            if (cost <= 0)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRolloutSpending: non-positive cost={cost:F1} at UT={ut:F1}, skipping");
                return;
            }

            // Sequence assignment mirrors OnKscSpending — see that method for the
            // canonical pattern. Same-UT KSC writes need a deterministic order in the
            // recalculation sort; the shared kscSequenceCounter provides it.
            kscSequenceCounter++;
            var action = new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                RecordingId = null,
                FundsSpent = (float)cost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                DedupKey = BuildRolloutDedupKey(ut, context),
                Sequence = kscSequenceCounter
            };

            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"VesselRollout spending recorded: cost={cost:F0}, UT={ut:F1}, dedupKey={action.DedupKey}, " +
                $"context={FormatRolloutAdoptionContext(context)}");

            // Reuse the KSC reconciliation path. Classifier returns ExpectedReasonKey=
            // "VesselRollout" for FundsSpending(VesselBuild), so the matching FundsChanged
            // event emitted by OnFundsChanged is paired and any drift logs WARN.
            ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, ut);

            RecalculateAndPatch();
        }

        /// <summary>
        /// UT epsilon used by <see cref="OnVesselRecoveryFunds"/> when locating the paired
        /// <see cref="GameStateEventType.FundsChanged"/>(<c>VesselRecovery</c>) event in
        /// <see cref="GameStateStore.Events"/>. KSP fires the funds change immediately
        /// before <c>onVesselRecovered</c>, so the two share the same UT to within a frame.
        /// Tightened to 0.1 s (matching the dedup epsilon at line ~375 in
        /// <see cref="DeduplicateAgainstLedger"/>) to avoid latching unrelated back-to-back
        /// recoveries — see consumed-index guard below for the second layer of protection.
        /// </summary>
        internal const double VesselRecoveryEventEpsilonSeconds = 0.1;
        private const double LegacyRecoveryActionAmountTolerance = 0.01;

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
        /// Dedup fingerprints of FundsChanged(VesselRecovery) events already consumed by
        /// an <see cref="OnVesselRecoveryFunds"/> call. Uses immutable event payload
        /// instead of mutable store indices so later list pruning/reindexing cannot
        /// retarget a consumed marker onto a different event.
        /// Cleared by <see cref="OnKspLoad"/> and <see cref="ResetForTesting"/>.
        /// Written exclusively from <see cref="TryAddVesselRecoveryFundsAction"/>.
        /// </summary>
        private static readonly HashSet<string> consumedRecoveryEventKeys = new HashSet<string>();

        private struct PendingRecoveryFundsRequest
        {
            public double Ut;
            public string VesselName;
            public bool FromTrackingStation;
        }

        private static readonly List<PendingRecoveryFundsRequest> pendingRecoveryFunds =
            new List<PendingRecoveryFundsRequest>();

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
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2:R}|{3:R}|{4:R}|{5}",
                e.eventType,
                e.key ?? "",
                e.ut,
                e.valueBefore,
                e.valueAfter,
                e.recordingId ?? "");
        }

        /// <summary>
        /// Locates the latest FundsChanged(VesselRecovery) event within the recovery
        /// epsilon window. When <paramref name="skipConsumed"/> is true, events whose
        /// dedup fingerprint is already in <see cref="consumedRecoveryEventKeys"/> are
        /// ignored.
        /// </summary>
        private static bool TryFindRecoveryFundsEvent(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            bool skipConsumed,
            out GameStateEvent matched,
            out string dedupKey)
        {
            matched = default;
            dedupKey = null;
            if (events == null)
                return false;

            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.eventType != GameStateEventType.FundsChanged) continue;
                if (e.key != VesselRecoveryReasonKey) continue;
                if (Math.Abs(e.ut - ut) > VesselRecoveryEventEpsilonSeconds) continue;

                string candidateKey = BuildRecoveryEventDedupKey(e);
                if (skipConsumed && consumedRecoveryEventKeys.Contains(candidateKey)) continue;

                matched = e;
                dedupKey = candidateKey;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true when the ledger already contains a recovery earning sourced from
        /// the same FundsChanged(VesselRecovery) event.
        /// </summary>
        private static bool HasRecoveryActionForDedupKey(string dedupKey)
        {
            if (string.IsNullOrEmpty(dedupKey))
                return false;

            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Type != GameActionType.FundsEarning) continue;
                if (action.FundsSource != FundsEarningSource.Recovery) continue;
                if (!string.Equals(action.DedupKey, dedupKey, StringComparison.Ordinal)) continue;
                return true;
            }

            return false;
        }

        private static void AddPendingRecoveryFundsRequest(
            double ut, string vesselName, bool fromTrackingStation)
        {
            // Do not fuzzy-dedup pending callbacks. Bulk recovery can deliver
            // multiple same-named callbacks in the same UT epsilon before any paired
            // FundsChanged(VesselRecovery) event has reached the recorder; the event
            // dedup fingerprint is applied when each request is actually paired.
            pendingRecoveryFunds.Add(new PendingRecoveryFundsRequest
            {
                Ut = ut,
                VesselName = vesselName,
                FromTrackingStation = fromTrackingStation
            });

            // Safety net: if the list grows past the staleness threshold without a
            // matching FundsChanged(VesselRecovery) event draining it, something
            // upstream stopped firing paired events. Lifecycle flushes
            // (OnKspLoad / rewind end / scene switch) normally catch this, but this
            // log keeps a footprint in KSP.log when a leak happens mid-session.
            if (pendingRecoveryFunds.Count > PendingRecoveryFundsStaleThreshold)
            {
                ParsekLog.Warn(Tag,
                    $"OnVesselRecoveryFunds: pending queue exceeded threshold " +
                    $"(count={pendingRecoveryFunds.Count} > {PendingRecoveryFundsStaleThreshold}) " +
                    $"— paired FundsChanged(VesselRecovery) events may be missing. " +
                    $"Latest deferred request vessel='{vesselName}' ut={ut.ToString("F1", CultureInfo.InvariantCulture)}");
            }
        }

        internal static int PendingRecoveryFundsCountForTesting => pendingRecoveryFunds.Count;

        internal static void OnRecoveryFundsEventRecorded(GameStateEvent evt)
        {
            if (evt.eventType != GameStateEventType.FundsChanged)
                return;
            if (evt.key != VesselRecoveryReasonKey)
                return;

            // Initialize() is idempotent: it short-circuits on the `initialized` flag so
            // repeated calls cost one branch. See LedgerOrchestrator.Initialize().
            Initialize();
            if (pendingRecoveryFunds.Count == 0)
                return;

            // Two-tier pairing:
            //
            //   Tier 1 — Vessel-name preferred. If the event carries a vessel name
            //            (propagated through GameStateEvent.detail on future recovery
            //            paths; currently set only by synthetic test events), prefer
            //            pending requests whose VesselName matches. This protects
            //            against mis-pairing when two same-UT recoveries of different
            //            vessels arrive and the funds events interleave out of order.
            //
            //   Tier 2 — Nearest UT. Legacy behavior: pick the pending request whose
            //            UT is closest to the event UT inside the epsilon window.
            //
            // When multiple candidates tie after the name-match filter and share the
            // same UT distance within the epsilon, we still pick the first match but
            // log a WARN listing every tied candidate so the ambiguity is visible in
            // KSP.log. This preserves the #444 callback-before-event contract without
            // introducing silent mis-pairing.
            string eventVesselName = evt.detail ?? "";

            int bestIndex = FindBestPairingIndex(evt.ut, eventVesselName);
            if (bestIndex < 0)
                return;

            var request = pendingRecoveryFunds[bestIndex];
            if (TryAddVesselRecoveryFundsAction(
                    request.Ut,
                    request.VesselName,
                    request.FromTrackingStation))
            {
                pendingRecoveryFunds.RemoveAt(bestIndex);
            }
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
            if (pendingRecoveryFunds.Count == 0)
                return -1;

            bool haveName = !string.IsNullOrEmpty(eventVesselName);

            // Tier 1: vessel-name preferred pass.
            int nameMatchBestIndex = -1;
            double nameMatchBestDistance = double.MaxValue;
            int nameMatchTies = 0;
            if (haveName)
            {
                for (int i = 0; i < pendingRecoveryFunds.Count; i++)
                {
                    if (!string.Equals(pendingRecoveryFunds[i].VesselName,
                            eventVesselName, StringComparison.Ordinal))
                        continue;

                    double distance = Math.Abs(pendingRecoveryFunds[i].Ut - eventUt);
                    if (distance > VesselRecoveryEventEpsilonSeconds) continue;

                    if (distance < nameMatchBestDistance)
                    {
                        nameMatchBestIndex = i;
                        nameMatchBestDistance = distance;
                        nameMatchTies = 1;
                    }
                    else if (distance == nameMatchBestDistance)
                    {
                        nameMatchTies++;
                    }
                }
            }

            if (nameMatchBestIndex >= 0)
            {
                if (nameMatchTies > 1)
                {
                    WarnPairingCandidateTie(eventUt, eventVesselName, byNameMatch: true,
                        bestDistance: nameMatchBestDistance);
                }
                return nameMatchBestIndex;
            }

            // Tier 2: nearest UT fallback.
            int fallbackBestIndex = -1;
            double fallbackBestDistance = double.MaxValue;
            int fallbackTies = 0;
            for (int i = 0; i < pendingRecoveryFunds.Count; i++)
            {
                double distance = Math.Abs(pendingRecoveryFunds[i].Ut - eventUt);
                if (distance > VesselRecoveryEventEpsilonSeconds) continue;

                if (distance < fallbackBestDistance)
                {
                    fallbackBestIndex = i;
                    fallbackBestDistance = distance;
                    fallbackTies = 1;
                }
                else if (distance == fallbackBestDistance)
                {
                    fallbackTies++;
                }
            }

            if (fallbackBestIndex >= 0 && fallbackTies > 1)
            {
                WarnPairingCandidateTie(eventUt, eventVesselName, byNameMatch: false,
                    bestDistance: fallbackBestDistance);
            }

            return fallbackBestIndex;
        }

        private static void WarnPairingCandidateTie(
            double eventUt, string eventVesselName, bool byNameMatch, double bestDistance)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pendingRecoveryFunds.Count; i++)
            {
                double distance = Math.Abs(pendingRecoveryFunds[i].Ut - eventUt);
                if (distance > VesselRecoveryEventEpsilonSeconds) continue;
                if (byNameMatch &&
                    !string.Equals(pendingRecoveryFunds[i].VesselName,
                        eventVesselName, StringComparison.Ordinal))
                    continue;
                if (distance != bestDistance) continue;

                if (sb.Length > 0) sb.Append(", ");
                sb.Append("vessel='").Append(pendingRecoveryFunds[i].VesselName ?? "")
                  .Append("' ut=").Append(pendingRecoveryFunds[i].Ut.ToString("F1", CultureInfo.InvariantCulture));
            }

            string tier = byNameMatch ? "name-match" : "nearest-UT";
            ParsekLog.Warn(Tag,
                $"OnRecoveryFundsEventRecorded: multiple pending requests tied at " +
                $"{tier} distance={bestDistance.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"for event ut={eventUt.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"vesselName='{eventVesselName ?? ""}'. Candidates: [{sb}]. " +
                $"Picking first match in list order.");
        }

        /// <summary>
        /// Drains unclaimed <see cref="pendingRecoveryFunds"/> entries at a lifecycle
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
            if (pendingRecoveryFunds.Count == 0)
                return;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pendingRecoveryFunds.Count; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("vessel='").Append(pendingRecoveryFunds[i].VesselName ?? "")
                  .Append("' ut=").Append(pendingRecoveryFunds[i].Ut.ToString("F1", CultureInfo.InvariantCulture));
            }

            ParsekLog.Warn(Tag,
                $"FlushStalePendingRecoveryFunds ({reason ?? ""}): " +
                $"evicting {pendingRecoveryFunds.Count} unclaimed recovery request(s) " +
                $"that never received a paired FundsChanged(VesselRecovery) event. " +
                $"Entries: [{sb}]");

            pendingRecoveryFunds.Clear();
        }

        /// <summary>
        /// Compatibility repair for saves written before recovery FundsEarning actions
        /// persisted <see cref="GameAction.DedupKey"/>. Rebuilds the missing key from
        /// the matching loaded FundsChanged(VesselRecovery) event.
        /// </summary>
        private static void RepairMissingRecoveryDedupKeys()
        {
            var actions = Ledger.Actions;
            var events = GameStateStore.Events;
            if (actions == null || actions.Count == 0 || events == null || events.Count == 0)
                return;

            var reservedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Type != GameActionType.FundsEarning) continue;
                if (action.FundsSource != FundsEarningSource.Recovery) continue;
                if (string.IsNullOrEmpty(action.DedupKey)) continue;
                reservedKeys.Add(action.DedupKey);
            }

            int scanned = 0;
            int repaired = 0;
            int unmatched = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Type != GameActionType.FundsEarning) continue;
                if (action.FundsSource != FundsEarningSource.Recovery) continue;
                if (!string.IsNullOrEmpty(action.DedupKey)) continue;

                scanned++;
                if (TryFindLegacyRecoveryDedupKey(action, events, reservedKeys, out string repairedKey))
                {
                    action.DedupKey = repairedKey;
                    reservedKeys.Add(repairedKey);
                    repaired++;
                }
                else
                {
                    unmatched++;
                }
            }

            if (scanned > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"RepairMissingRecoveryDedupKeys: scanned={scanned}, repaired={repaired}, unmatched={unmatched}");
            }
        }

        /// <summary>
        /// Matches a legacy recovery action lacking DedupKey back to its saved
        /// FundsChanged(VesselRecovery) event using the action UT and credited amount.
        /// </summary>
        private static bool TryFindLegacyRecoveryDedupKey(
            GameAction action,
            IReadOnlyList<GameStateEvent> events,
            HashSet<string> reservedKeys,
            out string dedupKey)
        {
            dedupKey = null;
            if (action == null || events == null)
                return false;

            double bestUtDistance = double.MaxValue;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.eventType != GameStateEventType.FundsChanged) continue;
                if (e.key != VesselRecoveryReasonKey) continue;

                double utDistance = Math.Abs(e.ut - action.UT);
                if (utDistance > VesselRecoveryEventEpsilonSeconds) continue;

                double delta = e.valueAfter - e.valueBefore;
                // Recovery payouts are stored on GameAction as float and serialized with
                // "R", so large amounts can round-trip back to double with more than a
                // cent of widening error. Keep the existing cent floor (matching funds
                // patch/no-op behavior) but widen it by the delta's own float-storage loss.
                double amountTolerance = Math.Max(
                    LegacyRecoveryActionAmountTolerance,
                    Math.Abs(delta - (double)(float)delta));
                if (Math.Abs(delta - action.FundsAwarded) > amountTolerance) continue;

                string candidateKey = BuildRecoveryEventDedupKey(e);
                if (reservedKeys.Contains(candidateKey)) continue;
                if (utDistance >= bestUtDistance) continue;

                dedupKey = candidateKey;
                bestUtDistance = utDistance;
                if (utDistance == 0)
                    break;
            }

            return dedupKey != null;
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
        /// <see cref="consumedRecoveryEventKeys"/> so a bulk-recovery burst (two
        /// <c>onVesselRecovered</c> callbacks within the epsilon window) routes each call
        /// to its own funds delta instead of double-latching the most recent event.</para>
        ///
        /// <para>The missing-pair <see cref="VesselType.Debris"/> case is handled before
        /// the deferred queue: once immediate pairing fails, debris callbacks return
        /// without entering <see cref="pendingRecoveryFunds"/>. Other vessel types,
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

            if (string.IsNullOrEmpty(vesselName))
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: empty vesselName at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} — skipping");
                return;
            }

            if (TryAddVesselRecoveryFundsAction(ut, vesselName, fromTrackingStation))
                return;

            if (vesselType == VesselType.Debris)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: vessel '{vesselName}' (VesselType.Debris) at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} — skipping deferred recovery-funds pairing");
                return;
            }

            AddPendingRecoveryFundsRequest(ut, vesselName, fromTrackingStation);
            ParsekLog.Verbose(Tag,
                $"OnVesselRecoveryFunds: deferred pairing for vessel '{vesselName}' " +
                $"at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} until FundsChanged(VesselRecovery) is recorded");
        }

        private static bool TryAddVesselRecoveryFundsAction(double ut, string vesselName, bool fromTrackingStation)
        {
            // Locate the paired FundsChanged(VesselRecovery) event. KSP fires the funds change
            // before or shortly after onVesselRecovered depending on the stock recovery path.
            // Search by reason key (TransactionReasons.VesselRecovery.ToString() == "VesselRecovery"
            // — see GameStateRecorder.OnFundsChanged where the key is written) within a small
            // UT window, and skip dedup fingerprints already consumed by an earlier recovery
            // call to protect against bulk-recovery double-latching.
            if (!TryFindRecoveryFundsEvent(
                    GameStateStore.Events,
                    ut,
                    skipConsumed: true,
                    out GameStateEvent matched,
                    out string dedupKey))
            {
                return false;
            }

            double delta = matched.valueAfter - matched.valueBefore;
            if (delta <= 0)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: paired event for '{vesselName}' at ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"has delta={delta.ToString("F1", CultureInfo.InvariantCulture)} — skipping (zero or negative recovery value)");
                consumedRecoveryEventKeys.Add(dedupKey);
                return true;
            }

            if (HasRecoveryActionForDedupKey(dedupKey))
            {
                consumedRecoveryEventKeys.Add(dedupKey);
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: paired event for '{vesselName}' at ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"already exists in ledger (dedupKey='{dedupKey}') — skipping duplicate add");
                return true;
            }

            // Pick the recording whose [StartUT, EndUT] brackets the recovery UT (the best
            // semantic match for "the flight this recovery belongs to"); fall back to the
            // most recent same-named flight that already ended at or before `ut`; final
            // fallback is the global latest by EndUT, which preserves the original behavior
            // for recoveries whose recording metadata has drifted (e.g., manual EndUT edit).
            string recordingId = PickRecoveryRecordingId(vesselName, matched.ut);

            // Mark consumed BEFORE adding the action so a re-entrant RecalculateAndPatch
            // (or any future test that calls back into this method) cannot reuse the same
            // paired funds event.
            consumedRecoveryEventKeys.Add(dedupKey);

            // Allocate a sequence number from the same counter used by OnKscSpending so
            // that recovery actions interleave deterministically with other KSC events
            // captured at the same UT.
            kscSequenceCounter++;

            var action = new GameAction
            {
                UT = matched.ut,
                Type = GameActionType.FundsEarning,
                RecordingId = recordingId,
                FundsAwarded = (float)delta,
                FundsSource = FundsEarningSource.Recovery,
                DedupKey = dedupKey,
                Sequence = kscSequenceCounter
            };

            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"VesselRecovery funds patched: vessel='{vesselName}' " +
                $"amount={delta.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"recordingId={recordingId ?? "(none)"} fromTrackingStation={fromTrackingStation}");

            RecalculateAndPatch();
            return true;
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
            if (string.IsNullOrEmpty(recordingId)) return null;
            if (rec == null)
            {
                ParsekLog.Verbose(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' not found, cannot match rollout context");
                return null;
            }

            RolloutAdoptionContext recordingContext = CreateRolloutAdoptionContext(rec);
            if (!CanMatchRolloutAdoptionContext(recordingContext))
            {
                ParsekLog.Verbose(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' missing rollout context " +
                    $"({FormatRolloutAdoptionContext(recordingContext)}), skipping adoption");
                return null;
            }

            var actions = Ledger.Actions;
            // LIFO scan: walk newest-first so the rollout immediately preceding this
            // launch wins over any older unclaimed entries (cancelled rollouts) sitting
            // in the same adoption window.
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var a = actions[i];
                if (a == null) continue;
                if (a.Type != GameActionType.FundsSpending) continue;
                if (a.FundsSpendingSource != FundsSpendingSource.VesselBuild) continue;
                if (!string.IsNullOrEmpty(a.RecordingId)) continue; // already adopted
                if (string.IsNullOrEmpty(a.DedupKey)) continue;
                if (!a.DedupKey.StartsWith(RolloutDedupPrefix, StringComparison.Ordinal)) continue;
                // 0.5 s slack absorbs UT epsilon between OnFundsChanged ut and FlightRecorder startUT.
                if (a.UT > startUT + 0.5) continue;
                if (startUT - a.UT > RolloutAdoptionWindowSeconds) continue;
                if (!RolloutAdoptionContextsMatch(ParseRolloutAdoptionContext(a.DedupKey), recordingContext))
                    continue;

                string oldDedup = a.DedupKey;
                a.RecordingId = recordingId;
                a.DedupKey = null;
                ParsekLog.Info(Tag,
                    $"TryAdoptRolloutAction: recording '{recordingId}' adopted rollout action " +
                    $"(UT={a.UT:F1}, cost={a.FundsSpent:F0}, oldDedupKey={oldDedup}, " +
                    $"startUT={startUT:F1}, lag={startUT - a.UT:F1}s, " +
                    $"context={FormatRolloutAdoptionContext(recordingContext)})");
                return a;
            }

            ParsekLog.Verbose(Tag,
                $"TryAdoptRolloutAction: no unclaimed rollout action within {RolloutAdoptionWindowSeconds:F0}s " +
                $"before startUT={startUT:F1} for recording '{recordingId}' " +
                $"with context {FormatRolloutAdoptionContext(recordingContext)}");
            return null;
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
            if (rec == null || string.IsNullOrEmpty(rec.StartSituation))
                return false;

            return rec.StartSituation.Equals("Prelaunch", StringComparison.OrdinalIgnoreCase)
                || rec.StartSituation.Equals("PRELAUNCH", StringComparison.OrdinalIgnoreCase);
        }

        private static RolloutAdoptionContext ResolveCurrentRolloutAdoptionContext()
        {
            try
            {
                Vessel activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null)
                {
                    string launchSiteName = FlightRecorder.ResolveLaunchSiteName(activeVessel, false);
                    if (string.IsNullOrEmpty(launchSiteName))
                        launchSiteName = TryResolveLaunchSiteNameFromFlightDriver();

                    return CreateRolloutAdoptionContext(
                        activeVessel.persistentId,
                        Recording.ResolveLocalizedName(activeVessel.vesselName),
                        launchSiteName);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveCurrentRolloutAdoptionContext: ActiveVessel lookup failed: {ex.Message}");
            }

            return CreateRolloutAdoptionContext(
                0u,
                null,
                TryResolveLaunchSiteNameFromFlightDriver());
        }

        private static string TryResolveLaunchSiteNameFromFlightDriver()
        {
            try
            {
                return FlightRecorder.HumanizeLaunchSiteName(FlightDriver.LaunchSiteName);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag, $"TryResolveLaunchSiteNameFromFlightDriver failed: {ex.Message}");
                return null;
            }
        }

        private static RolloutAdoptionContext CreateRolloutAdoptionContext(Recording rec)
        {
            if (rec == null)
                return default(RolloutAdoptionContext);

            return CreateRolloutAdoptionContext(
                rec.VesselPersistentId,
                rec.VesselName,
                rec.LaunchSiteName);
        }

        private static RolloutAdoptionContext CreateRolloutAdoptionContext(
            uint vesselPersistentId,
            string vesselName,
            string launchSiteName)
        {
            return new RolloutAdoptionContext
            {
                VesselPersistentId = vesselPersistentId,
                VesselName = NormalizeRolloutContextText(vesselName),
                LaunchSiteName = NormalizeRolloutContextText(launchSiteName)
            };
        }

        private static string NormalizeRolloutContextText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim();
        }

        private static bool CanMatchRolloutAdoptionContext(RolloutAdoptionContext context)
        {
            if (context.VesselPersistentId != 0)
                return true;

            return !string.IsNullOrEmpty(context.VesselName)
                && !string.IsNullOrEmpty(context.LaunchSiteName);
        }

        private static bool RolloutAdoptionContextsMatch(
            RolloutAdoptionContext actionContext,
            RolloutAdoptionContext recordingContext)
        {
            // Compatibility: the immediately previous #445 follow-up persisted
            // rollout adoption markers as bare "rollout:<UT>" keys with no
            // vessel/site fields. Those saves still need to reattach the saved
            // charge after upgrade/load, so preserve the legacy window-only
            // adoption behavior for that exact key shape.
            if (actionContext.IsLegacyBareKey)
                return true;

            if (actionContext.VesselPersistentId != 0 && recordingContext.VesselPersistentId != 0)
                return actionContext.VesselPersistentId == recordingContext.VesselPersistentId;

            return !string.IsNullOrEmpty(actionContext.VesselName)
                && !string.IsNullOrEmpty(recordingContext.VesselName)
                && !string.IsNullOrEmpty(actionContext.LaunchSiteName)
                && !string.IsNullOrEmpty(recordingContext.LaunchSiteName)
                && actionContext.VesselName.Equals(recordingContext.VesselName, StringComparison.OrdinalIgnoreCase)
                && actionContext.LaunchSiteName.Equals(recordingContext.LaunchSiteName, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildRolloutDedupKey(double ut, RolloutAdoptionContext context)
        {
            return RolloutDedupPrefix
                + ut.ToString("R", CultureInfo.InvariantCulture)
                + "|pid=" + context.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                + "|site=" + Uri.EscapeDataString(context.LaunchSiteName ?? string.Empty)
                + "|vessel=" + Uri.EscapeDataString(context.VesselName ?? string.Empty);
        }

        private static RolloutAdoptionContext ParseRolloutAdoptionContext(string dedupKey)
        {
            if (string.IsNullOrEmpty(dedupKey)
                || !dedupKey.StartsWith(RolloutDedupPrefix, StringComparison.Ordinal))
                return default(RolloutAdoptionContext);

            var context = default(RolloutAdoptionContext);
            string[] parts = dedupKey.Split('|');
            if (parts.Length == 1)
            {
                context.IsLegacyBareKey = true;
                return context;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.StartsWith("pid=", StringComparison.Ordinal))
                {
                    uint.TryParse(
                        part.Substring(4),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out context.VesselPersistentId);
                }
                else if (part.StartsWith("site=", StringComparison.Ordinal))
                {
                    context.LaunchSiteName = NormalizeRolloutContextText(
                        Uri.UnescapeDataString(part.Substring(5)));
                }
                else if (part.StartsWith("vessel=", StringComparison.Ordinal))
                {
                    context.VesselName = NormalizeRolloutContextText(
                        Uri.UnescapeDataString(part.Substring(7)));
                }
            }

            return context;
        }

        private static string FormatRolloutAdoptionContext(RolloutAdoptionContext context)
        {
            return $"pid={context.VesselPersistentId}, vessel='{context.VesselName ?? "(null)"}', " +
                $"site='{context.LaunchSiteName ?? "(null)"}'";
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
        // After RecalculationEngine.Recalculate populates TransformedFundsReward,
        // TransformedScienceReward, EffectiveRep, and EffectiveScience on each
        // action, ReconcilePostWalk iterates the same (cutoff-filtered) action
        // list and compares those derived values against live KSP deltas in
        // GameStateStore. WARN on divergence, VERBOSE on match. Log-only: does
        // not mutate the ledger, the KSP state, or any module.
        //
        // Scope: the eight action types ClassifyAction routes to the Transformed
        // bucket (ContractComplete, ContractFail, ContractCancel,
        // MilestoneAchievement, ReputationEarning, ReputationPenalty, KSC-path
        // FundsEarning and ScienceEarning). Other action types are reconciled
        // per-action by ReconcileKscAction and return Reconcile=false here.
        //
        // See docs/dev/done/plans/fix-440-post-walk-reconciliation.md.
        // ================================================================

        /// <summary>
        /// UT window (seconds) used by <see cref="ReconcilePostWalk"/> to pair a
        /// transformed-type action with its resource-changed event. Matches the same
        /// coalesce invariant documented on
        /// <see cref="KscActionReconciler.KscReconcileEpsilonSeconds"/>, kept
        /// independent so a future tune cannot inadvertently couple the two paths.
        /// </summary>
        internal const double PostWalkReconcileEpsilonSeconds = 0.1;

        // Membership in a coalesced post-walk window is stricter than "exactly zero",
        // but intentionally independent from the compare tolerance so multiple tiny
        // contributors can still aggregate into a visible mismatch.
        private const double PostWalkAggregateContributionEpsilon = 1e-6;

        /// <summary>
        /// One resource leg of a <see cref="PostWalkExpectation"/>. Populated
        /// only when the leg applies for the action type; otherwise the leg is
        /// default-zeroed (Applies=false).
        /// </summary>
        internal struct PostWalkLeg
        {
            public bool Applies;
            public double Expected;
            public string ReasonKey;
            public GameStateEventType EventType;
        }

        /// <summary>
        /// Classification output for one action in the post-walk reconcile pass.
        /// Up to three legs (funds/rep/sci) may apply independently. Returns
        /// Reconcile=false for action types outside #440 scope.
        /// </summary>
        internal struct PostWalkExpectation
        {
            public bool Reconcile;
            public PostWalkLeg Funds;
            public PostWalkLeg Rep;
            public PostWalkLeg Sci;
        }

        /// <summary>
        /// Pure classifier: maps a Transformed-bucket <see cref="GameAction"/>
        /// to its post-walk expected delta(s) and the TransactionReasons key
        /// the emitter stamps on the paired GameStateEvent. Action types
        /// outside #440 scope (everything ClassifyAction returns as
        /// Untransformed or NoResourceImpact) return Reconcile=false.
        /// <para>
        /// Reviewer-corrected event keys (see plan sections 3.1-3.6):
        /// <list type="bullet">
        /// <item><c>ContractComplete</c> -> <c>ContractReward</c> (all three legs)</item>
        /// <item><c>ContractFail</c> / <c>ContractCancel</c> -> <c>ContractPenalty</c></item>
        /// <item><c>MilestoneAchievement</c> -> <c>Progression</c> (all three legs, via
        /// the generic resource-event path; <c>MilestoneAchieved.detail</c> is NOT parsed)</item>
        /// <item><c>ReputationEarning</c> maps by <see cref="ReputationSource"/>;
        /// <c>Other</c> returns Reconcile=false (synthetic, no paired event).</item>
        /// <item><c>ReputationPenalty</c> maps by <see cref="ReputationPenaltySource"/>;
        /// <c>Other</c> returns Reconcile=false.</item>
        /// </list>
        /// </para>
        /// </summary>
        internal static PostWalkExpectation ClassifyPostWalk(GameAction action)
        {
            var exp = new PostWalkExpectation();
            if (action == null) return exp;

            switch (action.Type)
            {
                case GameActionType.ContractComplete:
                    // All three legs when Effective; duplicate completions (Effective=false)
                    // are filtered by MilestonesModule/ContractsModule and the modules
                    // skip crediting, so the observed delta is 0 for those. Post-walk
                    // mirrors the module gate by skipping entirely.
                    if (!action.Effective) return exp;
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.TransformedFundsReward,
                        ReasonKey = "ContractReward",
                        EventType = GameStateEventType.FundsChanged
                    };
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = "ContractReward",
                        EventType = GameStateEventType.ReputationChanged
                    };
                    exp.Sci = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.TransformedScienceReward,
                        ReasonKey = "ContractReward",
                        EventType = GameStateEventType.ScienceChanged
                    };
                    return exp;

                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    // Penalties fire unconditionally (no Effective gate in the modules).
                    // Funds leg: FundsModule deducts FundsPenalty directly (no transform
                    // today). Rep leg: EffectiveRep from the curve (negative).
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = -(double)action.FundsPenalty,
                        ReasonKey = "ContractPenalty",
                        EventType = GameStateEventType.FundsChanged
                    };
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = "ContractPenalty",
                        EventType = GameStateEventType.ReputationChanged
                    };
                    return exp;

                case GameActionType.MilestoneAchievement:
                    // All three legs gated on Effective (duplicates skip).
                    if (!action.Effective) return exp;
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.MilestoneFundsAwarded,
                        ReasonKey = "Progression",
                        EventType = GameStateEventType.FundsChanged
                    };
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = "Progression",
                        EventType = GameStateEventType.ReputationChanged
                    };
                    exp.Sci = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.MilestoneScienceAwarded,
                        ReasonKey = "Progression",
                        EventType = GameStateEventType.ScienceChanged
                    };
                    return exp;

                case GameActionType.ReputationEarning:
                {
                    // Map source -> key. Synthetic sources (Other, and LegacyMigration if
                    // re-introduced on the rep enum later) return Reconcile=false.
                    string key;
                    switch (action.RepSource)
                    {
                        case ReputationSource.ContractComplete:
                            key = "ContractReward"; break;
                        case ReputationSource.Milestone:
                            key = "Progression"; break;
                        case ReputationSource.Other:
                        default:
                            return exp; // synthetic, no paired event
                    }
                    exp.Reconcile = true;
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = key,
                        EventType = GameStateEventType.ReputationChanged
                    };
                    return exp;
                }

                case GameActionType.ReputationPenalty:
                {
                    string key;
                    switch (action.RepPenaltySource)
                    {
                        case ReputationPenaltySource.ContractFail:
                            key = "ContractPenalty"; break;
                        case ReputationPenaltySource.ContractDecline:
                            key = "ContractDecline"; break;
                        case ReputationPenaltySource.KerbalDeath:
                            key = "CrewKilled"; break;
                        case ReputationPenaltySource.Strategy:
                        case ReputationPenaltySource.Other:
                        default:
                            return exp; // synthetic / no stock emitter today
                    }
                    exp.Reconcile = true;
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = key,
                        EventType = GameStateEventType.ReputationChanged
                    };
                    return exp;
                }

                case GameActionType.FundsEarning:
                {
                    // KSC-path direct earnings (source != Recovery / ContractComplete /
                    // Milestone). Recovery/ContractComplete/Milestone arrive as their own
                    // action types and are handled above. This branch is a safety net
                    // for direct "Other"-source KSC payouts (today: none from stock; mod
                    // strategy payouts could land here once #439 Phase C captures them).
                    if (!action.Effective) return exp;
                    if (action.FundsSource == FundsEarningSource.LegacyMigration) return exp;
                    if (action.FundsSource == FundsEarningSource.Recovery) return exp;
                    if (action.FundsSource == FundsEarningSource.ContractComplete) return exp;
                    if (action.FundsSource == FundsEarningSource.ContractAdvance) return exp;
                    if (action.FundsSource == FundsEarningSource.Milestone) return exp;
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.FundsAwarded,
                        ReasonKey = "Other",
                        EventType = GameStateEventType.FundsChanged
                    };
                    return exp;
                }

                case GameActionType.ScienceEarning:
                {
                    // Post-cap EffectiveScience (ScienceModule sets this on walk).
                    if (!action.Effective) return exp;
                    exp.Reconcile = true;
                    exp.Sci = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveScience,
                        ReasonKey = GetScienceChangedReasonKey(action),
                        EventType = GameStateEventType.ScienceChanged
                    };
                    return exp;
                }

                default:
                    return exp; // Reconcile stays false
            }
        }

        /// <summary>
        /// Post-walk reconciliation. Runs once per <see cref="RecalculateAndPatch"/>
        /// after <see cref="RecalculationEngine.Recalculate"/> returns and before
        /// <see cref="KspStatePatcher.PatchAll"/>. Iterates actions in stored
        /// order; for each Transformed-bucket action whose UT survives the
        /// cutoff, compares the post-walk derived delta against the live KSP
        /// delta (<c>valueAfter - valueBefore</c>) summed across matching
        /// <see cref="GameStateStore"/> events inside the observed-side match window:
        /// normally <see cref="PostWalkReconcileEpsilonSeconds"/> around <c>action.UT</c>,
        /// but for end-anchored <see cref="GameActionType.ScienceEarning"/> actions the
        /// owning recording span is used so earlier in-flight science transmissions still pair.
        /// WARN on divergence per leg; VERBOSE rate-limited on match. Emits a
        /// single INFO summary after the iteration. Log-only.
        /// <para>
        /// Parameterized for testability — production calls pass
        /// <see cref="GameStateStore.Events"/> and <see cref="Ledger.Actions"/>.
        /// </para>
        /// </summary>
        internal static void ReconcilePostWalk(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            if (actions == null || actions.Count == 0) return;

            GetResourceTrackingAvailability(
                out bool fundsTracked,
                out bool scienceTracked,
                out bool repTracked);
            if (!fundsTracked && !scienceTracked && !repTracked)
            {
                LogReconcileSkippedOnce("post-walk", "Post-walk reconcile",
                    fundsTracked, scienceTracked, repTracked);
                return;
            }

            const double fundsTol = 1.0;
            const double repTol = 0.1;
            const double sciTol = 0.1;
            double livePruneThreshold = MilestoneStore.GetLatestCommittedEndUT();

            int walked = 0;
            int compared = 0;
            int matched = 0;
            int mismatchFunds = 0;
            int mismatchRep = 0;
            int mismatchSci = 0;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;

                // Respect the same filter as RecalculationEngine.Recalculate: seed
                // types always pass; non-seed actions pass when UT <= cutoff.
                if (utCutoff.HasValue &&
                    !RecalculationEngine.IsSeedType(action.Type) &&
                    action.UT > utCutoff.Value)
                {
                    continue;
                }

                var exp = ClassifyPostWalk(action);
                if (!exp.Reconcile) continue;
                if (!HasTrackedPostWalkLeg(exp, fundsTracked, scienceTracked, repTracked))
                    continue;
                if (IsOutsidePostWalkLiveCoverage(
                        action,
                        exp,
                        livePruneThreshold,
                        events,
                        actions,
                        utCutoff))
                {
                    continue;
                }
                walked++;

                bool anyCompared = false;
                bool anyMismatch = false;
                if (fundsTracked && exp.Funds.Applies)
                {
                    var result = CompareLeg(
                        action, "funds", exp.Funds, fundsTol, events, actions, utCutoff, livePruneThreshold);
                    if (result != PostWalkCompareResult.Skipped)
                    {
                        anyCompared = true;
                        if (result == PostWalkCompareResult.Mismatch)
                        {
                            mismatchFunds++;
                            anyMismatch = true;
                        }
                    }
                }
                if (repTracked && exp.Rep.Applies)
                {
                    var result = CompareLeg(
                        action, "rep", exp.Rep, repTol, events, actions, utCutoff, livePruneThreshold);
                    if (result != PostWalkCompareResult.Skipped)
                    {
                        anyCompared = true;
                        if (result == PostWalkCompareResult.Mismatch)
                        {
                            mismatchRep++;
                            anyMismatch = true;
                        }
                    }
                }
                if (scienceTracked && exp.Sci.Applies)
                {
                    var result = CompareLeg(
                        action, "sci", exp.Sci, sciTol, events, actions, utCutoff, livePruneThreshold);
                    if (result != PostWalkCompareResult.Skipped)
                    {
                        anyCompared = true;
                        if (result == PostWalkCompareResult.Mismatch)
                        {
                            mismatchSci++;
                            anyMismatch = true;
                        }
                    }
                }

                if (anyCompared)
                {
                    compared++;
                    if (!anyMismatch) matched++;
                }
            }

            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture)
                : "null";
            ParsekLog.Info(Tag,
                $"Post-walk reconcile: actions={walked}, compared={compared}, matches={matched}, " +
                $"mismatches(funds/rep/sci)={mismatchFunds}/{mismatchRep}/{mismatchSci}, " +
                $"cutoffUT={cutoffLabel}");
        }

        /// <summary>
        /// Returns true when the action's UT falls outside the live <see cref="GameStateStore"/>
        /// coverage available to post-walk reconciliation. The live store prunes resource
        /// events at or below the latest committed milestone EndUT, and after a rewind/load the
        /// current epoch may have no coverage for older-epoch action history even though the
        /// ledger still retains those actions.
        /// </summary>
        private static bool IsOutsidePostWalkLiveCoverage(
            GameAction action,
            PostWalkExpectation expectation,
            double livePruneThreshold,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            if (action == null)
                return false;

            if (action.UT <= livePruneThreshold)
            {
                LogPostWalkLiveCoverageSkip(
                    action,
                    "ut is at/below live prune threshold=" +
                    livePruneThreshold.ToString("F1", CultureInfo.InvariantCulture));
                return true;
            }

            GameStateEventType anchorType;
            string anchorKey;
            if (!TryGetPostWalkSourceAnchor(action, out anchorType, out anchorKey))
                return false;

            if (HasLivePostWalkSourceAnchor(action, anchorType, anchorKey, events))
                return false;

            if (!HasLivePostWalkObservedEvent(action, expectation, events, livePruneThreshold))
            {
                LogPostWalkLiveCoverageSkip(
                    action,
                    "no live source anchor or observed reward leg remains in the current timeline");
                return true;
            }

            if (HasAmbiguousLiveCoverageOverlap(
                    action,
                    expectation,
                    actions,
                    utCutoff,
                    events,
                    livePruneThreshold))
            {
                LogPostWalkLiveCoverageSkip(
                    action,
                    "same-UT live overlap is ambiguous without a live source anchor");
                return true;
            }

            return false;
        }

        private static void LogPostWalkLiveCoverageSkip(
            GameAction action,
            string reason)
        {
            string actionType = action != null ? action.Type.ToString() : "(null)";
            string actionId = action != null ? ActionIdForPostWalk(action) : "(null)";
            string utLabel = action != null
                ? action.UT.ToString("F1", CultureInfo.InvariantCulture)
                : "null";
            string rateKey = string.Format(
                CultureInfo.InvariantCulture,
                "post-walk-live-coverage-skip:{0}:{1}:{2}",
                actionType,
                actionId,
                action != null ? action.UT.ToString("R", CultureInfo.InvariantCulture) : "null");

            ParsekLog.VerboseRateLimited(
                Tag,
                rateKey,
                $"Post-walk live-coverage skip: {actionType} id={actionId} ut={utLabel} -- {reason}");
        }

        private static bool TryGetPostWalkSourceAnchor(
            GameAction action,
            out GameStateEventType anchorType,
            out string anchorKey)
        {
            anchorType = default(GameStateEventType);
            anchorKey = null;

            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.ContractComplete:
                    anchorType = GameStateEventType.ContractCompleted;
                    anchorKey = action.ContractId;
                    return !string.IsNullOrEmpty(anchorKey);

                case GameActionType.ContractFail:
                    anchorType = GameStateEventType.ContractFailed;
                    anchorKey = action.ContractId;
                    return !string.IsNullOrEmpty(anchorKey);

                case GameActionType.ContractCancel:
                    anchorType = GameStateEventType.ContractCancelled;
                    anchorKey = action.ContractId;
                    return !string.IsNullOrEmpty(anchorKey);

                case GameActionType.MilestoneAchievement:
                    anchorType = GameStateEventType.MilestoneAchieved;
                    anchorKey = action.MilestoneId;
                    return !string.IsNullOrEmpty(anchorKey);

                default:
                    return false;
            }
        }

        private static bool HasLivePostWalkSourceAnchor(
            GameAction action,
            GameStateEventType anchorType,
            string anchorKey,
            IReadOnlyList<GameStateEvent> events)
        {
            if (action == null || events == null || events.Count == 0)
                return false;

            string expectedKey = anchorKey ?? "";

            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (!GameStateStore.IsEventVisibleToCurrentTimeline(e)) continue;
                if (e.eventType != anchorType) continue;
                if (Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds) continue;
                if (!PostWalkEventMatchesAction(e, action)) continue;
                if (!string.Equals(e.key ?? "", expectedKey, StringComparison.Ordinal))
                    continue;
                return true;
            }

            return false;
        }

        private static bool HasLivePostWalkObservedEvent(
            GameAction action,
            PostWalkExpectation expectation,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            if (action == null || events == null || events.Count == 0)
                return false;

            return HasLivePostWalkObservedEventForLeg(
                       action, expectation.Funds, events, livePruneThreshold) ||
                   HasLivePostWalkObservedEventForLeg(
                       action, expectation.Rep, events, livePruneThreshold) ||
                   HasLivePostWalkObservedEventForLeg(
                       action, expectation.Sci, events, livePruneThreshold);
        }

        private static bool HasLivePostWalkObservedEventForLeg(
            GameAction action,
            PostWalkLeg leg,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            if (action == null || !leg.Applies || events == null || events.Count == 0)
                return false;

            string expectedKey = leg.ReasonKey ?? "";
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (!IsLivePostWalkObservedEvent(e, livePruneThreshold)) continue;
                if (e.eventType != leg.EventType) continue;
                if (Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds) continue;
                if (!PostWalkEventMatchesAction(e, action)) continue;
                if (!string.Equals(e.key ?? "", expectedKey, StringComparison.Ordinal))
                    continue;
                return true;
            }

            return false;
        }

        private static bool IsLivePostWalkObservedEvent(
            GameStateEvent e,
            double livePruneThreshold)
        {
            if (e.ut <= livePruneThreshold)
                return false;

            return GameStateStore.IsEventVisibleToCurrentTimeline(e);
        }

        private static bool HasAmbiguousLiveCoverageOverlap(
            GameAction anchorAction,
            PostWalkExpectation anchorExpectation,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            if (anchorAction == null || actions == null || actions.Count == 0)
                return false;

            var anchorLegs = new[] { anchorExpectation.Funds, anchorExpectation.Rep, anchorExpectation.Sci };

            for (int i = 0; i < actions.Count; i++)
            {
                var other = actions[i];
                if (other == null || object.ReferenceEquals(other, anchorAction))
                    continue;
                if (!PostWalkActionsShareScope(anchorAction, other))
                    continue;
                if (Math.Abs(other.UT - anchorAction.UT) > PostWalkReconcileEpsilonSeconds)
                    continue;
                if (utCutoff.HasValue &&
                    !RecalculationEngine.IsSeedType(other.Type) &&
                    other.UT > utCutoff.Value)
                {
                    continue;
                }

                var otherExp = ClassifyPostWalk(other);
                if (!otherExp.Reconcile)
                    continue;
                if (other.UT <= livePruneThreshold)
                    continue;

                GameStateEventType otherAnchorType;
                string otherAnchorKey;
                bool hasLiveSourceAnchor =
                    TryGetPostWalkSourceAnchor(other, out otherAnchorType, out otherAnchorKey) &&
                    HasLivePostWalkSourceAnchor(other, otherAnchorType, otherAnchorKey, events);

                var otherLegs = new[] { otherExp.Funds, otherExp.Rep, otherExp.Sci };
                for (int anchorIndex = 0; anchorIndex < anchorLegs.Length; anchorIndex++)
                {
                    var anchorLeg = anchorLegs[anchorIndex];
                    if (!anchorLeg.Applies)
                        continue;

                    for (int otherIndex = 0; otherIndex < otherLegs.Length; otherIndex++)
                    {
                        var otherLeg = otherLegs[otherIndex];
                        if (!otherLeg.Applies)
                            continue;
                        if (anchorLeg.EventType != otherLeg.EventType)
                            continue;
                        if (!string.Equals(
                                anchorLeg.ReasonKey ?? "",
                                otherLeg.ReasonKey ?? "",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (hasLiveSourceAnchor ||
                            HasLivePostWalkObservedEventForLeg(
                                other, otherLeg, events, livePruneThreshold))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Compares one resource leg for a transformed action. Returns
        /// <see cref="PostWalkCompareResult.Match"/> on match,
        /// <see cref="PostWalkCompareResult.Mismatch"/> on mismatch or missing event,
        /// and <see cref="PostWalkCompareResult.Skipped"/> when another action in the
        /// same coalesced window already owns the comparison/logging for this leg.
        /// Observed delta =
        /// <c>sum(valueAfter - valueBefore)</c> across events with matching
        /// type + key within <see cref="PostWalkReconcileEpsilonSeconds"/> of
        /// <c>action.UT</c>.
        /// </summary>
        private enum PostWalkCompareResult
        {
            Match,
            Mismatch,
            Skipped
        }

        private struct PostWalkWindowAggregate
        {
            public double Expected;
            public int ContributorCount;
            public bool IsPrimary;
            public string ContributorLabel;
            public double ObservedWindowStartUt;
            public double ObservedWindowEndUt;
            public double DisplayWindowStartUt;
            public double DisplayWindowEndUt;
        }

        private static PostWalkCompareResult CompareLeg(
            GameAction action,
            string legTag,
            PostWalkLeg leg,
            double tolerance,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            double livePruneThreshold)
        {
            var aggregate = AggregatePostWalkWindow(
                action, leg, tolerance, actions, utCutoff, events, livePruneThreshold);
            if (!aggregate.IsPrimary)
                return PostWalkCompareResult.Skipped;

            double observedWindowStartUt = aggregate.ObservedWindowStartUt;
            double observedWindowEndUt = aggregate.ObservedWindowEndUt;
            string observedWindowLabel = FormatPostWalkObservedWindowLabel(
                action,
                leg,
                aggregate.DisplayWindowStartUt,
                aggregate.DisplayWindowEndUt);

            double observed = 0.0;
            int observedCount = 0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (!IsLivePostWalkObservedEvent(e, livePruneThreshold)) continue;
                    if (e.eventType != leg.EventType) continue;
                    if (e.ut < observedWindowStartUt || e.ut > observedWindowEndUt) continue;
                    if (!PostWalkEventMatchesAction(e, action)) continue;
                    string eventKey = e.key ?? "";
                    if (!string.Equals(eventKey, leg.ReasonKey, StringComparison.Ordinal))
                        continue;
                    observed += (e.valueAfter - e.valueBefore);
                    observedCount++;
                }
            }

            // Zero-expected + zero-observed is silent match (no event fired, none
            // expected). Zero-expected + non-zero observed is a mismatch: the walk
            // produced nothing but KSP credited a delta.
            if (Math.Abs(aggregate.Expected) <= tolerance && observedCount == 0)
                return PostWalkCompareResult.Match;

            string expectedLabel = aggregate.Expected.ToString("F1", CultureInfo.InvariantCulture);
            string observedLabel = observed.ToString("F1", CultureInfo.InvariantCulture);
            string warnKeyPrefix = string.Format(
                CultureInfo.InvariantCulture,
                "postwalk:{0}:{1}:{2}:{3:F3}:{4:F3}:{5}",
                legTag,
                action.Type,
                action.RecordingId ?? "",
                observedWindowStartUt,
                observedWindowEndUt,
                leg.ReasonKey ?? "");

            if (observedCount == 0)
            {
                string message =
                    $"Earnings reconciliation (post-walk, {legTag}): {action.Type} " +
                    $"{aggregate.ContributorLabel} expected={expectedLabel} but no matching {leg.EventType} event " +
                    $"keyed '{leg.ReasonKey}' {observedWindowLabel} -- missing earning channel or stale event?";
                string warnKey = warnKeyPrefix + ":missing:" + expectedLabel;
                if (LogReconcileWarnOnce(warnKey, message))
                {
                    LogSciencePostWalkReconcileDumpOnce(
                        warnKey,
                        action,
                        leg,
                        events,
                        observedWindowStartUt,
                        observedWindowEndUt,
                        livePruneThreshold);
                }
                return PostWalkCompareResult.Mismatch;
            }

            if (Math.Abs(aggregate.Expected - observed) > tolerance)
            {
                string message =
                    $"Earnings reconciliation (post-walk, {legTag}): {action.Type} " +
                    $"{aggregate.ContributorLabel} expected={expectedLabel}, observed={observedLabel} across " +
                    $"{observedCount} event(s) keyed '{leg.ReasonKey}' {observedWindowLabel} " +
                    $"-- post-walk delta mismatch";
                string warnKey = warnKeyPrefix + ":mismatch:" + expectedLabel + ":" + observedLabel;
                if (LogReconcileWarnOnce(warnKey, message))
                {
                    LogSciencePostWalkReconcileDumpOnce(
                        warnKey,
                        action,
                        leg,
                        events,
                        observedWindowStartUt,
                        observedWindowEndUt,
                        livePruneThreshold);
                }
                return PostWalkCompareResult.Mismatch;
            }

            ParsekLog.VerboseRateLimited(Tag,
                $"post-walk-match:{action.Type}:{legTag}:{action.RecordingId ?? ""}:{leg.ReasonKey ?? ""}:{action.UT.ToString("R", CultureInfo.InvariantCulture)}",
                $"Post-walk match: {action.Type} {legTag} {aggregate.ContributorLabel} " +
                $"expected={expectedLabel}, observed={observedLabel}, keyed '{leg.ReasonKey}' " +
                $"{observedWindowLabel}");
            return PostWalkCompareResult.Match;
        }

        private static void GetPostWalkObservedWindow(
            GameAction action,
            PostWalkLeg leg,
            out double startUt,
            out double endUt,
            out string label)
        {
            double epsilon = PostWalkReconcileEpsilonSeconds;
            startUt = action.UT - epsilon;
            endUt = action.UT + epsilon;
            GetPostWalkObservedDisplayWindow(action, leg, out double displayStartUt, out double displayEndUt, out label);

            if (action.Type != GameActionType.ScienceEarning ||
                leg.EventType != GameStateEventType.ScienceChanged ||
                string.IsNullOrEmpty(action.SubjectId) ||
                action.SubjectId.StartsWith("LegacyMigration:", StringComparison.Ordinal))
            {
                return;
            }

            if (!TryGetScienceReconcileWindow(
                    action,
                    out double scienceStartUt,
                    out double scienceEndUt,
                    out bool collapsedPersistedSpan))
                return;

            double startPad = collapsedPersistedSpan ? 0.0 : GetScienceReconcileBoundaryPadding(scienceStartUt);
            double endPad = collapsedPersistedSpan ? 0.0 : GetScienceReconcileBoundaryPadding(scienceEndUt);
            startUt = scienceStartUt - epsilon - startPad;
            endUt = scienceEndUt + epsilon + endPad;
            label = FormatPostWalkObservedWindowLabel(action, leg, displayStartUt, displayEndUt);
        }

        private static void GetPostWalkObservedDisplayWindow(
            GameAction action,
            PostWalkLeg leg,
            out double startUt,
            out double endUt,
            out string label)
        {
            double epsilon = PostWalkReconcileEpsilonSeconds;
            startUt = action.UT - epsilon;
            endUt = action.UT + epsilon;
            label = FormatPostWalkObservedWindowLabel(action, leg, startUt, endUt);

            if (action.Type != GameActionType.ScienceEarning ||
                leg.EventType != GameStateEventType.ScienceChanged ||
                string.IsNullOrEmpty(action.SubjectId) ||
                action.SubjectId.StartsWith("LegacyMigration:", StringComparison.Ordinal))
            {
                return;
            }

            if (!TryGetScienceReconcileWindow(
                    action,
                    out double scienceStartUt,
                    out double scienceEndUt,
                    out bool ignoredCollapsedPersistedSpan))
                return;

            startUt = scienceStartUt;
            endUt = scienceEndUt;
            label = FormatPostWalkObservedWindowLabel(action, leg, startUt, endUt);
        }

        private static bool TryGetScienceReconcileWindow(
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

            if (!TryGetPersistedScienceActionWindow(
                    action,
                    out startUt,
                    out endUt,
                    out collapsedPersistedSpan))
            {
                var rec = FindRecordingById(action.RecordingId);
                if (rec == null)
                    return false;

                startUt = rec.StartUT;
                endUt = rec.EndUT;
                if (endUt <= startUt)
                    return false;

                ParsekLog.VerboseRateLimited(Tag,
                    $"post-walk-science-window-fallback:{action.RecordingId}:{ActionIdForPostWalk(action)}",
                    $"Post-walk science window: {ActionIdForPostWalk(action)} missing persisted span; " +
                    $"falling back to recording {action.RecordingId ?? "(null)"} " +
                    $"[{FormatFixed1(startUt)},{FormatFixed1(endUt)}]");
            }
            else if (collapsedPersistedSpan)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    $"post-walk-science-window-collapsed:{action.RecordingId}:{ActionIdForPostWalk(action)}",
                    $"Post-walk science window: {ActionIdForPostWalk(action)} collapsed persisted span " +
                    $"recording={action.RecordingId ?? "(null)"} " +
                    $"start={action.StartUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"end={action.EndUT.ToString("R", CultureInfo.InvariantCulture)} -> " +
                    $"reconstructed [{FormatFixed3(startUt)},{FormatFixed3(endUt)}]");
            }

            // Only widen the observed-side window for the current end-anchored shape.
            // ScienceEarning spans are persisted through float fields, so at large UTs
            // the stored EndUT may drift from the double-backed action.UT by more than
            // the nominal 0.1 s epsilon. Allow the float quantization loss here while
            // still keeping the gate tight enough to reject truly non-end-anchored rows.
            return Math.Abs(action.UT - endUt) <= GetScienceReconcileAnchorTolerance(action.UT);
        }

        private static double GetScienceReconcileAnchorTolerance(double actionUt)
        {
            double floatRoundTripLoss = Math.Abs((double)(float)actionUt - actionUt);
            return PostWalkReconcileEpsilonSeconds + floatRoundTripLoss;
        }

        private static bool TryGetPersistedScienceActionWindow(
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

        private static double GetScienceReconcileBoundaryPadding(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            float single = (float)value;
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(single), 0);
            float nextUp = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            if (float.IsNaN(nextUp) || float.IsInfinity(nextUp))
                return 0.0;

            // One full ULP safely covers any double->float rounding loss at this magnitude.
            return Math.Abs((double)nextUp - single);
        }

        /// <summary>
        /// Aggregates the expected post-walk delta across all actions that classify to
        /// the same (EventType, ReasonKey) pair within the coalesce window. Mirrors the
        /// observed-side event coalescing so same-UT reward bursts do not falsely warn,
        /// and designates one "primary" action to own the comparison/logging for that
        /// coalesced window.
        /// </summary>
        private static PostWalkWindowAggregate AggregatePostWalkWindow(
            GameAction anchorAction,
            PostWalkLeg anchorLeg,
            double tolerance,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            double summedExpected = 0.0;
            int expectedCount = 0;
            GameAction primaryAction = null;
            var contributorIds = new List<string>();
            double observedWindowStartUt = double.PositiveInfinity;
            double observedWindowEndUt = double.NegativeInfinity;
            bool hasObservedWindow = false;
            double displayWindowStartUt = double.PositiveInfinity;
            double displayWindowEndUt = double.NegativeInfinity;
            bool hasDisplayWindow = false;

            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var other = actions[i];
                    if (other == null) continue;
                    if (!PostWalkActionsShareScope(anchorAction, other)) continue;
                    if (Math.Abs(other.UT - anchorAction.UT) > PostWalkReconcileEpsilonSeconds)
                        continue;
                    if (utCutoff.HasValue &&
                        !RecalculationEngine.IsSeedType(other.Type) &&
                        other.UT > utCutoff.Value)
                    {
                        continue;
                    }

                    var otherExp = ClassifyPostWalk(other);
                    if (!otherExp.Reconcile) continue;
                    if (IsOutsidePostWalkLiveCoverage(
                            other,
                            otherExp,
                            livePruneThreshold,
                            events,
                            actions,
                            utCutoff))
                    {
                        continue;
                    }

                    bool matched = false;
                    matched |= AccumulateMatchingPostWalkLeg(
                        otherExp.Funds, anchorLeg,
                        ref summedExpected, ref expectedCount);
                    matched |= AccumulateMatchingPostWalkLeg(
                        otherExp.Rep, anchorLeg,
                        ref summedExpected, ref expectedCount);
                    matched |= AccumulateMatchingPostWalkLeg(
                        otherExp.Sci, anchorLeg,
                        ref summedExpected, ref expectedCount);

                    if (!matched) continue;

                    if (primaryAction == null ||
                        (!ActionHasRecordingScope(primaryAction) && ActionHasRecordingScope(other)))
                        primaryAction = other;

                    GetPostWalkObservedWindow(
                        other,
                        anchorLeg,
                        out double contributorWindowStartUt,
                        out double contributorWindowEndUt,
                        out string ignoredContributorWindowLabel);
                    GetPostWalkObservedDisplayWindow(
                        other,
                        anchorLeg,
                        out double contributorDisplayWindowStartUt,
                        out double contributorDisplayWindowEndUt,
                        out string ignoredContributorDisplayWindowLabel);
                    if (!hasObservedWindow || contributorWindowStartUt < observedWindowStartUt)
                        observedWindowStartUt = contributorWindowStartUt;
                    if (!hasObservedWindow || contributorWindowEndUt > observedWindowEndUt)
                        observedWindowEndUt = contributorWindowEndUt;
                    hasObservedWindow = true;
                    if (!hasDisplayWindow || contributorDisplayWindowStartUt < displayWindowStartUt)
                        displayWindowStartUt = contributorDisplayWindowStartUt;
                    if (!hasDisplayWindow || contributorDisplayWindowEndUt > displayWindowEndUt)
                        displayWindowEndUt = contributorDisplayWindowEndUt;
                    hasDisplayWindow = true;
                    contributorIds.Add(ActionIdForPostWalk(other));
                }
            }

            if (expectedCount == 0)
            {
                GetPostWalkObservedWindow(
                    anchorAction,
                    anchorLeg,
                    out observedWindowStartUt,
                    out observedWindowEndUt,
                    out string ignoredAnchorWindowLabel);
                GetPostWalkObservedDisplayWindow(
                    anchorAction,
                    anchorLeg,
                    out displayWindowStartUt,
                    out displayWindowEndUt,
                    out string ignoredAnchorDisplayWindowLabel);
                return new PostWalkWindowAggregate
                {
                    Expected = anchorLeg.Expected,
                    ContributorCount = 1,
                    IsPrimary = true,
                    ContributorLabel = $"id={ActionIdForPostWalk(anchorAction)}",
                    ObservedWindowStartUt = observedWindowStartUt,
                    ObservedWindowEndUt = observedWindowEndUt,
                    DisplayWindowStartUt = displayWindowStartUt,
                    DisplayWindowEndUt = displayWindowEndUt
                };
            }

            return new PostWalkWindowAggregate
            {
                Expected = summedExpected,
                ContributorCount = expectedCount,
                IsPrimary = object.ReferenceEquals(primaryAction, anchorAction),
                ContributorLabel = FormatPostWalkContributorLabel(contributorIds, expectedCount),
                ObservedWindowStartUt = observedWindowStartUt,
                ObservedWindowEndUt = observedWindowEndUt,
                DisplayWindowStartUt = displayWindowStartUt,
                DisplayWindowEndUt = displayWindowEndUt
            };
        }

        // Mirrored in GameStateEventConverter.EventMatchesRecordingScope; keep the two in sync.
        private static bool EventMatchesRecordingScope(GameStateEvent evt, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return true;

            string eventRecordingId = evt.recordingId ?? "";
            return string.Equals(eventRecordingId, recordingId, StringComparison.Ordinal);
        }

        private static bool PostWalkEventMatchesAction(GameStateEvent evt, GameAction action)
        {
            if (evt.eventType == GameStateEventType.ScienceChanged &&
                action != null &&
                action.Type == GameActionType.ScienceEarning)
            {
                double actionStartUt = action.StartUT;
                double actionEndUt = action.EndUT;
                if (TryGetScienceReconcileWindow(
                        action,
                        out double reconstructedStartUt,
                        out double reconstructedEndUt,
                        out bool ignoredCollapsedPersistedSpan))
                {
                    actionStartUt = reconstructedStartUt;
                    actionEndUt = reconstructedEndUt;
                }

                return DoesScienceEventMatchActionScope(
                    evt,
                    action,
                    actionStartUt,
                    actionEndUt,
                    out bool _);
            }

            string eventRecordingId = evt.recordingId ?? "";
            string actionRecordingId = action?.RecordingId ?? "";
            if (string.IsNullOrEmpty(actionRecordingId))
                return true;
            return string.Equals(eventRecordingId, actionRecordingId, StringComparison.Ordinal);
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

        private static bool DoesScienceEventMatchActionScope(
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

        private static string GetScienceChangedReasonKey(GameAction action)
        {
            if (action != null && action.Method == ScienceMethod.Recovered)
                return VesselRecoveryReasonKey;

            return "ScienceTransmission";
        }

        private static string FormatPostWalkObservedWindowLabel(
            GameAction action,
            PostWalkLeg leg,
            double startUt,
            double endUt)
        {
            if (action != null &&
                action.Type == GameActionType.ScienceEarning &&
                leg.EventType == GameStateEventType.ScienceChanged &&
                !string.IsNullOrEmpty(action.SubjectId) &&
                !action.SubjectId.StartsWith("LegacyMigration:", StringComparison.Ordinal))
            {
                return $"within science window [{FormatFixed1(startUt)},{FormatFixed1(endUt)}] for action ut={FormatFixed1(action.UT)}";
            }

            return $"within {FormatFixed1(PostWalkReconcileEpsilonSeconds)}s of ut={FormatFixed1(action.UT)}";
        }

        private static void LogSciencePostWalkReconcileDumpOnce(
            string dumpKey,
            GameAction action,
            PostWalkLeg leg,
            IReadOnlyList<GameStateEvent> events,
            double observedWindowStartUt,
            double observedWindowEndUt,
            double livePruneThreshold)
        {
            if (events == null || events.Count == 0)
                return;
            if (leg.EventType != GameStateEventType.ScienceChanged)
                return;
            if (!emittedScienceReconcileDumpKeys.Add("postwalk-dump:" + (dumpKey ?? "")))
                return;

            double dumpStartUt = observedWindowStartUt - 5.0;
            double dumpEndUt = observedWindowEndUt + 5.0;
            var lines = new List<string>();
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.ScienceChanged)
                    continue;
                if (evt.ut < dumpStartUt || evt.ut > dumpEndUt)
                    continue;

                bool scopeMatch = PostWalkEventMatchesAction(evt, action);
                bool liveMatch = IsLivePostWalkObservedEvent(evt, livePruneThreshold);
                lines.Add(FormatScienceEventForReconcileDump(evt, scopeMatch && liveMatch, scopeMatch && string.IsNullOrEmpty(evt.recordingId ?? "")));
            }

            string detail = lines.Count == 0
                ? "(no ScienceChanged events in dump window)"
                : string.Join(" | ", lines.ToArray());
            ParsekLog.Error(Tag,
                $"Science reconcile dump (post-walk): action={ActionIdForPostWalk(action)} " +
                $"reason='{leg.ReasonKey}' window=[{FormatFixed1(observedWindowStartUt)},{FormatFixed1(observedWindowEndUt)}] " +
                $"events={detail}");
        }

        private static string FormatScienceEventForReconcileDump(
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

        private static bool PostWalkActionsShareScope(GameAction anchorAction, GameAction other)
        {
            string anchorRecordingId = anchorAction?.RecordingId ?? "";
            string otherRecordingId = other?.RecordingId ?? "";
            if (string.IsNullOrEmpty(anchorRecordingId))
                return true;
            return string.Equals(anchorRecordingId, otherRecordingId, StringComparison.Ordinal);
        }

        private static bool ActionHasRecordingScope(GameAction action)
        {
            return !string.IsNullOrEmpty(action?.RecordingId);
        }

        private static bool AccumulateMatchingPostWalkLeg(
            PostWalkLeg candidate,
            PostWalkLeg anchorLeg,
            ref double summedExpected,
            ref int expectedCount)
        {
            if (!candidate.Applies) return false;
            if (candidate.EventType != anchorLeg.EventType) return false;

            string candidateKey = candidate.ReasonKey ?? "";
            string anchorKey = anchorLeg.ReasonKey ?? "";
            if (!string.Equals(candidateKey, anchorKey, StringComparison.Ordinal))
                return false;

            if (Math.Abs(candidate.Expected) <= PostWalkAggregateContributionEpsilon)
                return false;

            summedExpected += candidate.Expected;
            expectedCount++;
            return true;
        }

        private static string FormatPostWalkContributorLabel(
            List<string> contributorIds,
            int contributorCount)
        {
            if (contributorIds == null || contributorIds.Count == 0)
                return "id=(none)";

            if (contributorCount <= 1 || contributorIds.Count == 1)
                return $"id={contributorIds[0]}";

            return $"ids=[{string.Join(", ", contributorIds.ToArray())}] across {contributorCount} action(s)";
        }

        private static bool HasTrackedPostWalkLeg(
            PostWalkExpectation exp,
            bool fundsTracked,
            bool scienceTracked,
            bool repTracked)
        {
            return (fundsTracked && exp.Funds.Applies)
                || (scienceTracked && exp.Sci.Applies)
                || (repTracked && exp.Rep.Applies);
        }

        private static void GetResourceTrackingAvailability(
            out bool fundsTracked,
            out bool scienceTracked,
            out bool repTracked)
        {
            fundsTracked = fundsTrackedOverrideForTesting ?? Funding.Instance != null;
            scienceTracked = scienceTrackedOverrideForTesting
                ?? ResearchAndDevelopment.Instance != null;
            repTracked = repTrackedOverrideForTesting ?? global::Reputation.Instance != null;
        }

        private static void LogReconcileSkippedOnce(
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

        /// <summary>Pure: best-effort identifier for post-walk log lines.</summary>
        private static string ActionIdForPostWalk(GameAction action)
        {
            if (action == null) return "null";
            if (!string.IsNullOrEmpty(action.ContractId)) return action.ContractId;
            if (!string.IsNullOrEmpty(action.MilestoneId)) return action.MilestoneId;
            if (!string.IsNullOrEmpty(action.SubjectId)) return action.SubjectId;
            if (!string.IsNullOrEmpty(action.RecordingId)) return action.RecordingId;
            return "(none)";
        }

        private static string FormatFixed1(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        private static string FormatFixed3(double value)
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
            migrateOldSaveEventsRanThisLoad = false;
            kscSequenceCounter = 0;
            emittedReconcileWarnKeys.Clear();
            emittedScienceReconcileDumpKeys.Clear();
            consumedRecoveryEventKeys.Clear();
            pendingRecoveryFunds.Clear();
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
