using System;
using System.Globalization;

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
    internal static class KspStatePatcher
    {
        private const string Tag = "KspStatePatcher";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Patches all KSP singletons from the given module states.
        /// Wraps the entire operation in suppression flags.
        /// </summary>
        internal static void PatchAll(ScienceModule science, FundsModule funds,
            ReputationModule reputation, MilestonesModule milestones, FacilitiesModule facilities)
        {
            GameStateRecorder.SuppressResourceEvents = true;
            GameStateRecorder.IsReplayingActions = true;
            try
            {
                PatchScience(science);
                PatchFunds(funds);
                PatchReputation(reputation);
                PatchFacilities(facilities);

                ParsekLog.Info(Tag, "PatchAll complete");
            }
            finally
            {
                GameStateRecorder.SuppressResourceEvents = false;
                GameStateRecorder.IsReplayingActions = false;
            }
        }

        /// <summary>
        /// Patches KSP's science pool to match the module's available science.
        /// Uses AddScience with a computed delta to reach the target value.
        /// No-op if ResearchAndDevelopment.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchScience(ScienceModule science)
        {
            if (science == null)
            {
                ParsekLog.Warn(Tag, "PatchScience: null ScienceModule — skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchScience: ResearchAndDevelopment.Instance is null (sandbox/science mode) — skipping");
                return;
            }

            float currentScience = ResearchAndDevelopment.Instance.Science;
            double targetScience = science.GetAvailableScience();
            float delta = (float)(targetScience - (double)currentScience);

            if (Math.Abs(delta) < 0.001f)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchScience: balance unchanged (current={currentScience.ToString("F1", IC)}, " +
                    $"target={targetScience.ToString("F1", IC)})");
            }
            else
            {
                ResearchAndDevelopment.Instance.AddScience(delta, TransactionReasons.None);

                float afterScience = ResearchAndDevelopment.Instance.Science;
                ParsekLog.Info(Tag,
                    $"PatchScience: {currentScience.ToString("F1", IC)} -> {afterScience.ToString("F1", IC)} " +
                    $"(delta={delta.ToString("F1", IC)}, target={targetScience.ToString("F1", IC)})");
            }

            // Always patch per-subject credited totals — individual subjects may have changed
            // even when the total balance is unchanged (e.g., one experiment reverted, another added)
            PatchPerSubjectScience(science);
        }

        /// <summary>
        /// Patches KSP's fund balance to match the module's available funds.
        /// Uses AddFunds with a computed delta to reach the target value.
        /// No-op if Funding.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchFunds(FundsModule funds)
        {
            if (funds == null)
            {
                ParsekLog.Warn(Tag, "PatchFunds: null FundsModule — skipping");
                return;
            }

            if (Funding.Instance == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchFunds: Funding.Instance is null (sandbox mode) — skipping");
                return;
            }

            double currentFunds = Funding.Instance.Funds;
            double targetFunds = funds.GetAvailableFunds();
            double delta = targetFunds - currentFunds;

            if (Math.Abs(delta) < 0.01)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchFunds: no change needed (current={currentFunds.ToString("F1", IC)}, " +
                    $"target={targetFunds.ToString("F1", IC)})");
                return;
            }

            Funding.Instance.AddFunds(delta, TransactionReasons.None);

            double afterFunds = Funding.Instance.Funds;
            ParsekLog.Info(Tag,
                $"PatchFunds: {currentFunds.ToString("F1", IC)} -> {afterFunds.ToString("F1", IC)} " +
                $"(delta={delta.ToString("F1", IC)}, target={targetFunds.ToString("F1", IC)})");
        }

        /// <summary>
        /// Patches KSP's reputation to match the module's running reputation.
        /// Uses SetReputation (NOT AddReputation) to avoid double curve application —
        /// the ReputationModule already applied the curve during the walk.
        /// No-op if Reputation.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchReputation(ReputationModule reputation)
        {
            if (reputation == null)
            {
                ParsekLog.Warn(Tag, "PatchReputation: null ReputationModule — skipping");
                return;
            }

            if (Reputation.Instance == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchReputation: Reputation.Instance is null (sandbox mode) — skipping");
                return;
            }

            float currentRep = Reputation.Instance.reputation;
            float targetRep = reputation.GetRunningRep();

            if (Math.Abs(targetRep - currentRep) < 0.01f)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchReputation: no change needed (current={currentRep.ToString("F2", IC)}, " +
                    $"target={targetRep.ToString("F2", IC)})");
                return;
            }

            Reputation.Instance.SetReputation(targetRep, TransactionReasons.None);

            float afterRep = Reputation.Instance.reputation;
            ParsekLog.Info(Tag,
                $"PatchReputation: {currentRep.ToString("F2", IC)} -> {afterRep.ToString("F2", IC)} " +
                $"(target={targetRep.ToString("F2", IC)})");
        }

        /// <summary>
        /// Patches KSP's facility levels to match the module's derived state.
        /// Reads from ScenarioUpgradeableFacilities.protoUpgradeables and sets
        /// levels via UpgradeableFacility.SetLevel, following the same pattern
        /// as the old ActionReplay.ReplayFacilityUpgrade (now removed).
        /// No-op if protoUpgradeables is null.
        /// </summary>
        internal static void PatchFacilities(FacilitiesModule facilities)
        {
            if (facilities == null)
            {
                ParsekLog.Warn(Tag, "PatchFacilities: null FacilitiesModule — skipping");
                return;
            }

            if (ScenarioUpgradeableFacilities.protoUpgradeables == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchFacilities: protoUpgradeables is null — skipping");
                return;
            }

            var allFacilities = facilities.GetAllFacilities();
            int patchedCount = 0;
            int skippedCount = 0;
            int notFoundCount = 0;

            foreach (var kvp in allFacilities)
            {
                string facilityId = kvp.Key;
                var state = kvp.Value;

                ScenarioUpgradeableFacilities.ProtoUpgradeable proto;
                if (!ScenarioUpgradeableFacilities.protoUpgradeables.TryGetValue(
                        facilityId, out proto)
                    || proto.facilityRefs == null || proto.facilityRefs.Count == 0)
                {
                    notFoundCount++;
                    continue;
                }

                var facility = proto.facilityRefs[0];
                if (facility == null)
                {
                    notFoundCount++;
                    continue;
                }

                int currentLevel = facility.FacilityLevel;
                int targetLevel = state.Level;

                if (currentLevel == targetLevel)
                {
                    skippedCount++;
                    continue;
                }

                facility.SetLevel(targetLevel);
                patchedCount++;

                ParsekLog.Verbose(Tag,
                    $"PatchFacilities: '{facilityId}' level {currentLevel.ToString(IC)} -> " +
                    $"{targetLevel.ToString(IC)} (destroyed={state.Destroyed.ToString(IC)})");
            }

            ParsekLog.Info(Tag,
                $"PatchFacilities: levels patched={patchedCount.ToString(IC)}, " +
                $"skipped={skippedCount.ToString(IC)}, " +
                $"notFound={notFoundCount.ToString(IC)}, " +
                $"total={allFacilities.Count}");

            // Patch destruction state via DestructibleBuilding components.
            // Collect all destructibles once (expensive FindObjectsOfType call),
            // then match against facility states that have destruction data.
            PatchDestructionState(allFacilities);
        }

        /// <summary>
        /// Patches DestructibleBuilding components to match the module's destroyed/intact state.
        /// Collects all DestructibleBuilding objects once, then iterates facilities to find matches.
        /// Uses Contains matching because FacilityId in destruction actions stores the building ID
        /// (e.g. "%.%.%.%.%.%.%.%") which matches DestructibleBuilding.id strings.
        /// No-op if no DestructibleBuilding objects are found (e.g. not in KSC scene).
        /// </summary>
        internal static void PatchDestructionState(
            System.Collections.Generic.IReadOnlyDictionary<string, FacilitiesModule.FacilityState> allFacilities)
        {
            var destructibles = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            if (destructibles == null || destructibles.Length == 0)
            {
                ParsekLog.Verbose(Tag,
                    "PatchDestructionState: no DestructibleBuilding objects found (not in KSC scene?) — skipping");
                return;
            }

            // Build a lookup from building id to DestructibleBuilding for efficient matching
            var buildingById = new System.Collections.Generic.Dictionary<string, DestructibleBuilding>(
                destructibles.Length);
            foreach (var db in destructibles)
            {
                if (db != null && !string.IsNullOrEmpty(db.id))
                    buildingById[db.id] = db;
            }

            int demolishedCount = 0;
            int repairedCount = 0;
            int matchedCount = 0;
            int noMatchCount = 0;

            foreach (var kvp in allFacilities)
            {
                string facilityId = kvp.Key;
                var state = kvp.Value;

                DestructibleBuilding db;
                if (buildingById.TryGetValue(facilityId, out db))
                {
                    matchedCount++;

                    if (state.Destroyed && !db.IsDestroyed)
                    {
                        db.Demolish();
                        demolishedCount++;
                        ParsekLog.Verbose(Tag,
                            $"PatchDestructionState: demolished '{db.id}'");
                    }
                    else if (!state.Destroyed && db.IsDestroyed)
                    {
                        db.Repair();
                        repairedCount++;
                        ParsekLog.Verbose(Tag,
                            $"PatchDestructionState: repaired '{db.id}'");
                    }
                }
                else
                {
                    noMatchCount++;
                }
            }

            ParsekLog.Info(Tag,
                $"PatchDestructionState: demolished={demolishedCount.ToString(IC)}, " +
                $"repaired={repairedCount.ToString(IC)}, " +
                $"matched={matchedCount.ToString(IC)}, " +
                $"noMatch={noMatchCount.ToString(IC)}, " +
                $"buildings={buildingById.Count}, " +
                $"facilities={allFacilities.Count}");
        }

        /// <summary>
        /// Patches per-subject credited totals in KSP's R&amp;D system to match the module's
        /// derived state. After rewind, the Science Archive must show correct per-subject progress.
        /// Updates both the credited science and the scientific value (diminishing returns factor).
        /// No-op if ResearchAndDevelopment.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchPerSubjectScience(ScienceModule science)
        {
            if (science == null)
            {
                ParsekLog.Warn(Tag, "PatchPerSubjectScience: null ScienceModule — skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchPerSubjectScience: ResearchAndDevelopment.Instance is null — skipping");
                return;
            }

            var subjects = science.GetAllSubjects();
            int patchedSubjects = 0;
            int skippedSubjects = 0;
            int notFoundSubjects = 0;

            foreach (var kvp in subjects)
            {
                var kspSubject = ResearchAndDevelopment.GetSubjectByID(kvp.Key);
                if (kspSubject == null)
                {
                    notFoundSubjects++;
                    continue;
                }

                float targetScience = (float)kvp.Value.CreditedTotal;
                if (Math.Abs(kspSubject.science - targetScience) > 0.001f)
                {
                    kspSubject.science = targetScience;
                    kspSubject.scientificValue = 1f - (targetScience / kspSubject.scienceCap);
                    if (kspSubject.scientificValue < 0f) kspSubject.scientificValue = 0f;
                    patchedSubjects++;
                }
                else
                {
                    skippedSubjects++;
                }
            }

            ParsekLog.Info(Tag,
                $"PatchPerSubjectScience: patched={patchedSubjects.ToString(IC)}, " +
                $"skipped={skippedSubjects.ToString(IC)}, " +
                $"notFound={notFoundSubjects.ToString(IC)}, " +
                $"totalSubjects={subjects.Count}");
        }
    }
}
