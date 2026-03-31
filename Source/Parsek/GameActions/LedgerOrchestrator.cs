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
        private static bool fundsSeedChecked;

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

            // 3. Add to ledger
            Ledger.AddActions(actions);

            ParsekLog.Info(Tag,
                $"Committed recording '{recordingId}': {actions.Count} actions added to ledger " +
                $"(events={events.Count}, science={scienceActions.Count}, " +
                $"startUT={startUT:F1}, endUT={endUT:F1})");

            // 4. Recalculate + patch
            RecalculateAndPatch();
        }

        /// <summary>
        /// Full recalculation from ledger to engine to KSP state patch.
        /// Called on: commit, rewind, warp exit, KSP load.
        /// </summary>
        internal static void RecalculateAndPatch()
        {
            Initialize();

            // Seed initial funds for career mode (once, idempotent)
            if (!fundsSeedChecked && Funding.Instance != null)
            {
                Ledger.SeedInitialFunds(Funding.Instance.Funds);
                fundsSeedChecked = true;
            }

            var actions = new List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actions);

            KspStatePatcher.PatchAll(scienceModule, fundsModule, reputationModule,
                milestonesModule, facilitiesModule);

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
            Ledger.Reconcile(validRecordingIds, maxUT);
            RecalculateAndPatch();
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

        /// <summary>Reset all state for testing.</summary>
        internal static void ResetForTesting()
        {
            initialized = false;
            fundsSeedChecked = false;
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
    }
}
