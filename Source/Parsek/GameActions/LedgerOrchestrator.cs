using System.Collections.Generic;

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
        private static bool initialized;
        private static bool seedChecked;

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

            // Register in tier order per design doc 1.8
            RecalculationEngine.RegisterModule(milestonesModule, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contractsModule, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(scienceModule, RecalculationEngine.ModuleTier.FirstTier);

            // Strategy transform between tiers
            RecalculationEngine.RegisterModule(strategiesModule, RecalculationEngine.ModuleTier.Strategy);

            // Second tier
            RecalculationEngine.RegisterModule(fundsModule, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputationModule, RecalculationEngine.ModuleTier.SecondTier);

            // Facilities parallel
            RecalculationEngine.RegisterModule(facilitiesModule, RecalculationEngine.ModuleTier.Facilities);

            initialized = true;
            ParsekLog.Info(Tag, "Initialized: 7 modules registered");
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

            // 3b. Generate KerbalAssignment actions from vessel crew data
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

            // 6. Recalculate + patch
            RecalculateAndPatch();
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
        private static string GetActionKey(GameAction a)
        {
            switch (a.Type)
            {
                case GameActionType.ScienceEarning: return a.SubjectId ?? "";
                case GameActionType.ScienceSpending: return a.NodeId ?? "";
                case GameActionType.FundsEarning: return a.RecordingId ?? "";
                case GameActionType.FundsSpending: return a.RecordingId ?? "";
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

            // Extract crew from vessel snapshot (same source KerbalsModule.Recalculate uses)
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

        /// <summary>
        /// Extracts crew members from a recording's vessel snapshot data,
        /// mirroring the crew extraction logic in KerbalsModule.Recalculate.
        /// Uses GhostVisualSnapshot (recording-start state) as the primary source
        /// for crew names (same as PopulateCrewEndStates), falls back to VesselSnapshot.
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
            if (names.Count == 0) return result;

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                KerbalEndState endState = KerbalEndState.Unknown;
                if (rec.CrewEndStates != null)
                    rec.CrewEndStates.TryGetValue(name, out endState);

                result.Add(new CrewInfo
                {
                    Name = name,
                    Role = KerbalsModule.FindTraitForKerbal(name),
                    EndState = endState
                });
            }

            return result;
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

            // Seed initial balances for career mode (once per session, idempotent)
            if (!seedChecked)
            {
                if (Funding.Instance != null)
                    Ledger.SeedInitialFunds(Funding.Instance.Funds);
                if (ResearchAndDevelopment.Instance != null)
                    Ledger.SeedInitialScience(ResearchAndDevelopment.Instance.Science);
                if (global::Reputation.Instance != null)
                    Ledger.SeedInitialReputation(global::Reputation.Instance.reputation);
                seedChecked = true;
            }

            // Update contract and strategy slot limits based on facility levels
            // from the previous recalculation walk. On the very first call, defaults
            // (2 contracts / 1 strategy) apply — correct for unupgraded buildings.
            // Subsequent calls use facility state derived from the prior walk,
            // so slot limits are always one walk behind facility upgrades. This is
            // acceptable because RecalculateAndPatch runs on every trigger (commit,
            // rewind, warp exit, load), converging within two calls at most.
            UpdateSlotLimitsFromFacilities();

            var actions = new List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actions);

            // Bridge: run KerbalsModule's snapshot-based recalculation alongside the ledger walk.
            // The kerbals system still operates on recording snapshots, not ledger actions.
            // This ensures crew reservations are updated on every recalculate trigger
            // (commit, rewind, warp exit, KSP load).
            // Bridge pattern — see T42 for future IResourceModule conversion.
            KerbalsModule.RecalculateAndApply();

            KspStatePatcher.PatchAll(scienceModule, fundsModule, reputationModule,
                milestonesModule, facilitiesModule, contractsModule);

            ParsekLog.Info(Tag,
                $"RecalculateAndPatch complete: {actions.Count} actions walked");
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

            RecalculateAndPatch();
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
            seedChecked = false;
            scienceModule = null;
            milestonesModule = null;
            contractsModule = null;
            fundsModule = null;
            reputationModule = null;
            facilitiesModule = null;
            strategiesModule = null;
            RecalculationEngine.ClearModules();
            Ledger.ResetForTesting();
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
        internal static bool IsInitialized => initialized;

        /// <summary>
        /// Notifies the ledger orchestrator about each recording in a committed tree.
        /// Calls OnRecordingCommitted for each recording in the tree.
        /// </summary>
        internal static void NotifyLedgerTreeCommitted(RecordingTree tree)
        {
            if (tree == null) return;
            foreach (var rec in tree.Recordings.Values)
            {
                OnRecordingCommitted(rec.RecordingId, rec.StartUT, rec.EndUT);
            }
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
