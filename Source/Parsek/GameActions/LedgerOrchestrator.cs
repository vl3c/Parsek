using System;
using System.Collections.Generic;
using System.Globalization;

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

        /// <summary>
        /// Monotonically increasing sequence counter for KSC events added individually
        /// via <see cref="OnKscSpending"/>. Ensures stable ordering when multiple KSC
        /// events occur at the same UT (e.g., rapid tech unlocks or facility upgrades).
        /// Reset on <see cref="ResetForTesting"/>.
        /// </summary>
        private static int kscSequenceCounter;

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
        }

        /// <summary>
        /// Called when a recording is committed. Converts events to GameActions,
        /// adds to ledger, recalculates, and patches KSP state.
        /// </summary>
        /// <param name="recordingId">The recording that was just committed.</param>
        /// <param name="startUT">Start UT of the recording.</param>
        /// <param name="endUT">End UT of the recording.</param>
        internal static void OnRecordingCommitted(string recordingId, double startUT, double endUT)
        {
            Initialize();

            // 1. Convert GameStateStore events to GameActions
            var events = GameStateStore.Events;
            var actions = GameStateEventConverter.ConvertEvents(events, recordingId, startUT, endUT);

            // 2. Convert pending science subjects
            var scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                GameStateRecorder.PendingScienceSubjects, recordingId, endUT);
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

            ParsekLog.Info(Tag,
                $"Committed recording '{recordingId}': {actions.Count} actions added to ledger " +
                $"(events={events.Count}, science={scienceActions.Count}, cost={costActions.Count}, " +
                $"kerbals={kerbalActions.Count}, dedup={dedupRemoved}, startUT={startUT:F1}, endUT={endUT:F1})");

            // 5b. #394: commit-time reconciliation — compare the sum of dropped
            // FundsChanged/ReputationChanged/ScienceChanged events (which the converter
            // intentionally drops, see GameStateEventConverter:121-130) against the
            // sum of effective emitted Funds/Rep/Science actions in the same UT window.
            // Log WARN on mismatch. Log-only: the drop block itself must stay, because
            // re-emitting those events would double-count against recovery + contract +
            // milestone channels (review §5.1 regression guard).
            ReconcileEarningsWindow(events, actions, costActions, scienceActions, startUT, endUT);

            // 6. Recalculate + patch
            RecalculateAndPatch();
        }

        /// <summary>
        /// Pure reconciliation: sums dropped FundsChanged/ReputationChanged/ScienceChanged
        /// deltas against effective emitted FundsEarning/FundsSpending/ReputationEarning/
        /// ReputationPenalty/ScienceEarning/ScienceSpending actions in the same UT window
        /// and logs WARN on mismatch beyond tolerance. Internal static for testability.
        /// </summary>
        internal static void ReconcileEarningsWindow(
            IReadOnlyList<GameStateEvent> events,
            List<GameAction> newActions,
            List<GameAction> vesselCostActions,
            List<GameAction> scienceActions,
            double startUT, double endUT)
        {
            double droppedFundsDelta = 0;
            double droppedRepDelta = 0;
            double droppedSciDelta = 0;

            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e.ut < startUT || e.ut > endUT) continue;
                    switch (e.eventType)
                    {
                        case GameStateEventType.FundsChanged:
                            droppedFundsDelta += (e.valueAfter - e.valueBefore);
                            break;
                        case GameStateEventType.ReputationChanged:
                            droppedRepDelta += (e.valueAfter - e.valueBefore);
                            break;
                        case GameStateEventType.ScienceChanged:
                            droppedSciDelta += (e.valueAfter - e.valueBefore);
                            break;
                    }
                }
            }

            double emittedFundsDelta = 0;
            double emittedRepDelta = 0;
            double emittedSciDelta = 0;

            // newActions already contains vesselCostActions + scienceActions, but the
            // caller supplies them separately for clarity. We only enumerate newActions
            // so the sum is well-defined even if caller dedups differently.
            if (newActions != null)
            {
                for (int i = 0; i < newActions.Count; i++)
                {
                    var a = newActions[i];
                    switch (a.Type)
                    {
                        case GameActionType.FundsEarning:
                            emittedFundsDelta += a.FundsAwarded;
                            break;
                        case GameActionType.FundsSpending:
                            emittedFundsDelta -= a.FundsSpent;
                            break;
                        case GameActionType.ReputationEarning:
                            emittedRepDelta += a.NominalRep;
                            break;
                        case GameActionType.ReputationPenalty:
                            emittedRepDelta -= a.NominalPenalty;
                            break;
                        case GameActionType.ContractComplete:
                            emittedFundsDelta += a.FundsReward;
                            emittedRepDelta += a.RepReward;
                            emittedSciDelta += a.ScienceReward;
                            break;
                        case GameActionType.ContractFail:
                        case GameActionType.ContractCancel:
                            emittedFundsDelta -= a.FundsPenalty;
                            emittedRepDelta -= a.RepPenalty;
                            break;
                        case GameActionType.MilestoneAchievement:
                            emittedFundsDelta += a.MilestoneFundsAwarded;
                            emittedRepDelta += a.MilestoneRepAwarded;
                            break;
                        case GameActionType.ScienceEarning:
                            emittedSciDelta += a.ScienceAwarded;
                            break;
                        case GameActionType.ScienceSpending:
                            emittedSciDelta -= a.Cost;
                            break;
                    }
                }
            }

            const double fundsTol = 1.0;   // 1 funds tolerance for rounding
            const double repTol = 0.1f;
            const double sciTol = 0.1f;

            if (Math.Abs(droppedFundsDelta - emittedFundsDelta) > fundsTol)
            {
                ParsekLog.Warn(Tag,
                    $"Earnings reconciliation (funds): store delta={droppedFundsDelta:F1} vs " +
                    $"ledger emitted delta={emittedFundsDelta:F1} — missing earning channel? " +
                    $"window=[{startUT:F1},{endUT:F1}]");
            }
            if (Math.Abs(droppedRepDelta - emittedRepDelta) > repTol)
            {
                ParsekLog.Warn(Tag,
                    $"Earnings reconciliation (rep): store delta={droppedRepDelta:F1} vs " +
                    $"ledger emitted delta={emittedRepDelta:F1} — missing earning channel? " +
                    $"window=[{startUT:F1},{endUT:F1}]");
            }
            if (Math.Abs(droppedSciDelta - emittedSciDelta) > sciTol)
            {
                ParsekLog.Warn(Tag,
                    $"Earnings reconciliation (sci): store delta={droppedSciDelta:F1} vs " +
                    $"ledger emitted delta={emittedSciDelta:F1} — missing earning channel? " +
                    $"window=[{startUT:F1},{endUT:F1}]");
            }
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

            if (buildCost > 0)
            {
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

            // Recovery funds = last point's funds - second-to-last point's funds
            // Only if the recording ended with vessel recovery.
            if (rec.TerminalStateValue == TerminalState.Recovered && rec.Points.Count >= 2)
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

            return result;
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
        internal static void RecalculateAndPatch()
        {
            Initialize();

            // Seed initial balances for career mode (per-resource, idempotent).
            // Each resource is tracked independently — a partial seed (e.g., only
            // reputation ready) must not lock out future funds/science seeding.
            // Skip zero values: KSP singletons exist but report 0 before their
            // OnLoad populates save data. If value is legitimately 0, HasSeed stays
            // false and patching is skipped, preserving KSP's own 0.
            if (!fundsSeedDone && Funding.Instance != null
                && Funding.Instance.Funds != 0.0)
            {
                Ledger.SeedInitialFunds(Funding.Instance.Funds);
                fundsSeedDone = true;
            }
            if (!scienceSeedDone && ResearchAndDevelopment.Instance != null
                && ResearchAndDevelopment.Instance.Science != 0f)
            {
                Ledger.SeedInitialScience(ResearchAndDevelopment.Instance.Science);
                scienceSeedDone = true;
            }
            if (!repSeedDone && global::Reputation.Instance != null
                && Math.Abs(global::Reputation.Instance.reputation) > 0.01f)
            {
                Ledger.SeedInitialReputation(global::Reputation.Instance.reputation);
                repSeedDone = true;
            }

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

            var actions = new List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actions);

            // KSP state mutations (PostWalk already called by engine)
            kerbalsModule.ApplyToRoster(HighLogic.CurrentGame?.CrewRoster);
            KspStatePatcher.PatchAll(scienceModule, fundsModule, reputationModule,
                milestonesModule, facilitiesModule, contractsModule);

            ParsekLog.Info(Tag,
                $"RecalculateAndPatch complete: {actions.Count} actions walked");

            OnTimelineDataChanged?.Invoke();
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

            // Reconcile first — prunes orphaned actions from the existing ledger.
            // On empty ledger (old save), this is a no-op.
            Ledger.Reconcile(validRecordingIds, maxUT);

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
                ParsekLog.Verbose(Tag, $"OnLoad: ledger loaded from '{path}'");
            }
            else
            {
                ParsekLog.Warn(Tag, "OnLoad: could not resolve ledger path — starting with empty ledger");
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
            // deterministic ordering in the recalculation sort.
            kscSequenceCounter++;
            action.Sequence = kscSequenceCounter;

            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"KSC spending recorded: type={action.Type}, UT={evt.ut:F1}, " +
                $"key={evt.key ?? "(none)"}");

            RecalculateAndPatch();
        }

        /// <summary>
        /// Checks whether a science spending of the given cost is affordable under the
        /// current ledger reservation. Returns true if available science >= cost.
        /// Used by TechResearchPatch to block unfunded tech unlocks.
        /// </summary>
        internal static bool CanAffordScienceSpending(float cost)
        {
            Initialize();
            if (scienceModule == null) return true;

            // Run a recalculation to get current state (may already be current)
            var actions = new System.Collections.Generic.List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actions);

            double available = scienceModule.GetAvailableScience();
            bool affordable = available >= (double)cost;

            ParsekLog.Verbose(Tag,
                $"CanAffordScienceSpending: cost={cost:F1}, available={available:F1}, affordable={affordable}");

            return affordable;
        }

        /// <summary>
        /// Checks whether a funds spending of the given cost is affordable under the
        /// current ledger reservation. Returns true if available funds >= cost.
        /// Not yet wired to FacilityUpgradePatch — scaffolding for future use.
        /// </summary>
        internal static bool CanAffordFundsSpending(float cost)
        {
            Initialize();
            if (fundsModule == null) return true;

            var actions = new System.Collections.Generic.List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actions);

            double available = fundsModule.GetAvailableFunds();
            bool affordable = available >= (double)cost;

            ParsekLog.Verbose(Tag,
                $"CanAffordFundsSpending: cost={cost:F1}, available={available:F1}, affordable={affordable}");

            return affordable;
        }

        /// <summary>Reset all state for testing.</summary>
        internal static void ResetForTesting()
        {
            initialized = false;
            fundsSeedDone = false;
            scienceSeedDone = false;
            repSeedDone = false;
            kscSequenceCounter = 0;
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

            int gapsClosed = 0;
            int pendingBefore = GameStateRecorder.PendingScienceSubjects.Count;
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

                    OnRecordingCommitted(rec.RecordingId, startUT, endUT);
                }
            }
            finally
            {
                // #397: PendingScienceSubjects MUST be cleared after the orchestrator has
                // read them for every recording in the tree. Previously, RecordingStore
                // cleared the list BEFORE NotifyLedgerTreeCommitted ran, so
                // ConvertScienceSubjects always saw an empty list and no ScienceEarning
                // actions ever landed. The try/finally ensures the clear fires even if
                // OnRecordingCommitted throws.
                int cleared = GameStateRecorder.PendingScienceSubjects.Count;
                GameStateRecorder.PendingScienceSubjects.Clear();
                if (pendingBefore > 0 || cleared > 0)
                    ParsekLog.Verbose(Tag,
                        $"NotifyLedgerTreeCommitted: cleared PendingScienceSubjects " +
                        $"(before={pendingBefore}, atClear={cleared})");
            }

            ParsekLog.Verbose(Tag,
                $"NotifyLedgerTreeCommitted: tree='{tree.TreeName}' " +
                $"recordings={tree.Recordings.Count}, chainSegments={chainEndUTs.Count}, " +
                $"gapsClosed={gapsClosed}");
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
