using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
    internal static class KspStatePatcher
    {
        private const string Tag = "KspStatePatcher";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

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
            ContractsModule contracts = null)
        {
            using (SuppressionGuard.ResourcesAndReplay())
            {
                PatchScience(science);
                PatchFunds(funds);
                PatchReputation(reputation);
                PatchFacilities(facilities);
                PatchMilestones(milestones);
                PatchContracts(contracts);

                ParsekLog.Info(Tag, "PatchAll complete");
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

            if (!science.HasSeed)
            {
                ParsekLog.Verbose(Tag,
                    "PatchScience: module has no ScienceInitial seed — skipping to preserve KSP values");
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

            if (!funds.HasSeed)
            {
                ParsekLog.Verbose(Tag,
                    "PatchFunds: module has no FundsInitial seed — skipping to preserve KSP values");
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

            if (!reputation.HasSeed)
            {
                ParsekLog.Verbose(Tag,
                    "PatchReputation: module has no ReputationInitial seed — skipping to preserve KSP values");
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
        /// Uses exact ID matching between FacilityId and DestructibleBuilding.id.
        /// No-op if no DestructibleBuilding objects are found (e.g. not in KSC scene).
        /// </summary>
        internal static void PatchDestructionState(
            System.Collections.Generic.IReadOnlyDictionary<string, FacilitiesModule.FacilityState> allFacilities)
        {
            if (SuppressUnityCallsForTesting)
            {
                ParsekLog.Verbose(Tag,
                    "PatchDestructionState: SuppressUnityCallsForTesting — skipping");
                return;
            }

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
                    if (kspSubject.scienceCap > 0f)
                        kspSubject.scientificValue = 1f - (targetScience / kspSubject.scienceCap);
                    else
                        kspSubject.scientificValue = 0f;
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

        /// <summary>
        /// Patches KSP's ProgressNode achievement tree to match the module's credited milestones.
        /// Iterates the tree recursively, setting reached/complete flags via reflection for
        /// nodes that should be achieved, and clearing them for nodes that should not be.
        ///
        /// Body-specific nodes (under CelestialBodySubtree) are matched using path-qualified
        /// IDs ("Mun/Landing"), while top-level nodes use bare IDs ("FirstLaunch").
        /// Old recordings with bare body-specific IDs ("Landing" instead of "Mun/Landing")
        /// will not match and are logged for diagnostics.
        ///
        /// Note: After clearing a node, KSP's ProgressTracking.Update() may immediately
        /// re-trigger it if conditions are still met (e.g., a vessel is in orbit). This is
        /// correct behavior — milestones whose conditions are still satisfied SHOULD remain
        /// achieved. The IsReplayingActions flag suppresses GameStateRecorder from capturing
        /// re-triggered milestones as new events.
        /// </summary>
        internal static void PatchMilestones(MilestonesModule milestones)
        {
            if (milestones == null)
            {
                ParsekLog.Warn(Tag, "PatchMilestones: null module — skipping");
                return;
            }

            if (ProgressTracking.Instance == null)
            {
                ParsekLog.Verbose(Tag, "PatchMilestones: ProgressTracking.Instance is null — skipping");
                return;
            }

            var tree = ProgressTracking.Instance.achievementTree;
            if (tree == null)
            {
                ParsekLog.Warn(Tag, "PatchMilestones: achievementTree is null — skipping");
                return;
            }

            // Cache reflection FieldInfo/PropertyInfo once for the private/protected fields
            var reachedField = typeof(ProgressNode).GetField("reached",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var completeField = typeof(ProgressNode).GetField("complete",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var mannedProp = typeof(ProgressNode).GetProperty("IsCompleteManned",
                BindingFlags.Public | BindingFlags.Instance);
            var unmannedProp = typeof(ProgressNode).GetProperty("IsCompleteUnmanned",
                BindingFlags.Public | BindingFlags.Instance);

            if (reachedField == null || completeField == null)
            {
                ParsekLog.Warn(Tag,
                    "PatchMilestones: could not find reached/complete fields via reflection " +
                    $"(reached={reachedField != null}, complete={completeField != null}) — skipping");
                return;
            }

            if (mannedProp == null || unmannedProp == null)
            {
                ParsekLog.Warn(Tag,
                    "PatchMilestones: could not find IsCompleteManned/IsCompleteUnmanned properties " +
                    $"(manned={mannedProp != null}, unmanned={unmannedProp != null}) — will skip manned/unmanned flags");
            }

            int credited = 0, unreached = 0, skipped = 0;

            PatchProgressNodeTree(tree, milestones, reachedField, completeField,
                mannedProp, unmannedProp,
                "", ref credited, ref unreached, ref skipped);

            ParsekLog.Info(Tag,
                $"PatchMilestones: credited={credited.ToString(IC)}, " +
                $"unreached={unreached.ToString(IC)}, " +
                $"skipped={skipped.ToString(IC)}, " +
                $"moduleCredited={milestones.GetCreditedCount().ToString(IC)}");
        }

        /// <summary>
        /// Recursively patches a ProgressTree's nodes to match credited milestone state.
        /// For each node, computes a qualified ID (body nodes get "BodyName/NodeId"),
        /// checks whether it should be achieved, and sets/clears flags via reflection.
        ///
        /// Matching logic: tries qualified path first ("Mun/Landing"), then falls back to
        /// bare node.Id ("Landing") for backward compat with old recordings. The bare-Id
        /// fallback only fires for top-level nodes (empty pathPrefix) to avoid false matches
        /// on body subtree children.
        ///
        /// Internal static for testability — the reflection FieldInfos are passed in.
        /// </summary>
        internal static void PatchProgressNodeTree(
            ProgressTree tree, MilestonesModule milestones,
            FieldInfo reachedField, FieldInfo completeField,
            PropertyInfo mannedProp, PropertyInfo unmannedProp,
            string pathPrefix,
            ref int credited, ref int unreached, ref int skipped)
        {
            for (int i = 0; i < tree.Count; i++)
            {
                var node = tree[i];
                if (node == null) continue;

                string nodeId = node.Id ?? "";
                string qualifiedId = string.IsNullOrEmpty(pathPrefix)
                    ? nodeId
                    : pathPrefix + "/" + nodeId;

                // Check if this milestone should be achieved.
                // Try qualified path first, then bare Id for backward compat on top-level nodes.
                bool shouldBeAchieved = milestones.IsMilestoneCredited(qualifiedId);
                if (!shouldBeAchieved && string.IsNullOrEmpty(pathPrefix))
                {
                    // Top-level node — bare Id is already the qualified path, no fallback needed
                }
                else if (!shouldBeAchieved && !string.IsNullOrEmpty(pathPrefix))
                {
                    // Body subtree child — try bare Id for backward compat with old recordings
                    // that stored "Landing" instead of "Mun/Landing"
                    if (milestones.IsMilestoneCredited(nodeId))
                    {
                        shouldBeAchieved = true;
                        ParsekLog.Verbose(Tag,
                            $"PatchMilestones: bare-Id fallback match for '{nodeId}' " +
                            $"(qualified='{qualifiedId}' not found — old recording?)");
                    }
                }

                bool isCurrentlyAchieved = node.IsComplete;

                if (shouldBeAchieved && !isCurrentlyAchieved)
                {
                    // Set achieved via reflection (avoid Complete() which fires GameEvents)
                    reachedField.SetValue(node, true);
                    completeField.SetValue(node, true);
                    credited++;

                    ParsekLog.Verbose(Tag,
                        $"PatchMilestones: set achieved '{qualifiedId}'");
                }
                else if (!shouldBeAchieved && isCurrentlyAchieved)
                {
                    // Un-achieve via reflection (no public setters available)
                    reachedField.SetValue(node, false);
                    completeField.SetValue(node, false);
                    if (mannedProp != null) mannedProp.SetValue(node, false, null);
                    if (unmannedProp != null) unmannedProp.SetValue(node, false, null);
                    unreached++;

                    ParsekLog.Verbose(Tag,
                        $"PatchMilestones: cleared achieved '{qualifiedId}'");
                }
                else
                {
                    skipped++;
                }

                // Recurse into subtree children
                if (node.Subtree != null && node.Subtree.Count > 0)
                {
                    PatchProgressNodeTree(node.Subtree, milestones,
                        reachedField, completeField, mannedProp, unmannedProp,
                        qualifiedId, ref credited, ref unreached, ref skipped);
                }
            }
        }

        /// <summary>
        /// Patches KSP's contract system to match the ledger's derived state.
        /// Unregisters and clears all current contracts, then rebuilds from ConfigNode
        /// snapshots stored in GameStateStore for each contract that should be active
        /// according to the ContractsModule walk.
        ///
        /// Uses the Contract.Load path which sets state directly via enum parsing
        /// without calling SetState — no rewards, penalties, or GameEvents are fired.
        ///
        /// CRITICAL: Contract.Load mutates the input ConfigNode by removing the "type"
        /// value, so we always clone the snapshot before passing it to the load pipeline.
        /// </summary>
        internal static void PatchContracts(ContractsModule contracts)
        {
            if (ContractSystem.Instance == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchContracts: ContractSystem.Instance is null — skipping");
                return;
            }

            if (contracts == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchContracts: null ContractsModule — skipping");
                return;
            }

            // Get the set of contract IDs that should be active after the ledger walk
            var activeIds = contracts.GetActiveContractIds();
            var activeIdSet = new HashSet<Guid>();
            foreach (var idStr in activeIds)
            {
                if (Guid.TryParse(idStr, out var gid))
                    activeIdSet.Add(gid);
            }

            // If no active contracts in ledger, just log the current KSP state
            int currentCount = ContractSystem.Instance.Contracts != null
                ? ContractSystem.Instance.Contracts.Count
                : 0;

            ParsekLog.Verbose(Tag,
                $"PatchContracts: ledger has {activeIds.Count.ToString(IC)} active contracts, " +
                $"KSP has {currentCount.ToString(IC)} current contracts");

            // #404: Filtered remove — only touch Active contracts whose id is NOT in the
            // ledger's active set. Offered/Declined/Cancelled/Failed/Completed entries MUST
            // stay untouched; the old code unconditionally cleared currentContracts and
            // ContractsFinished, which wiped the Mission Control Offered bucket and any
            // game history. ContractsFinished is append-only game state — Parsek must NOT
            // mutate it. Filtering is delegated to PartitionContractsForPatch for testability.
            var currentContracts = ContractSystem.Instance.Contracts;
            var entries = new List<ContractFilterEntry>();
            if (currentContracts != null)
            {
                for (int i = 0; i < currentContracts.Count; i++)
                {
                    var c = currentContracts[i];
                    if (c == null) continue;
                    entries.Add(new ContractFilterEntry
                    {
                        Id = c.ContractGuid,
                        IsActive = c.ContractState == Contract.State.Active
                    });
                }
            }

            PartitionContractsForPatch(entries, activeIdSet,
                out var toRemove, out var survivingActiveIds);
            var toRemoveSet = new HashSet<Guid>(toRemove);

            int removedStale = 0;
            int unregisterFailures = 0;
            if (currentContracts != null)
            {
                for (int i = currentContracts.Count - 1; i >= 0; i--)
                {
                    var c = currentContracts[i];
                    if (c == null) continue;
                    if (!toRemoveSet.Contains(c.ContractGuid)) continue;

                    try
                    {
                        c.Unregister();
                    }
                    catch (Exception ex)
                    {
                        unregisterFailures++;
                        ParsekLog.Warn(Tag,
                            $"PatchContracts: failed to unregister stale contract '{c.ContractGuid}': {ex.Message}");
                    }
                    currentContracts.RemoveAt(i);
                    removedStale++;
                }
            }

            ParsekLog.Verbose(Tag,
                $"PatchContracts: removed {removedStale.ToString(IC)} stale Active contract(s), " +
                $"{survivingActiveIds.Count.ToString(IC)} Active contract(s) preserved in place " +
                $"(unregisterFailures={unregisterFailures.ToString(IC)})");

            // 2. Rebuild ONLY contracts that are in the ledger but not already present.
            int restored = 0;
            int skippedExisting = 0;
            int noSnapshot = 0;
            int noType = 0;
            int typeNotFound = 0;
            int loadFailed = 0;
            int registered = 0;

            foreach (string contractId in activeIds)
            {
                // Skip contracts that survived the filtered remove — no need to unregister
                // and recreate them, preserving parameter subscriptions that are already hot.
                if (Guid.TryParse(contractId, out var cg) && survivingActiveIds.Contains(cg))
                {
                    skippedExisting++;
                    continue;
                }

                ConfigNode snapshot = GameStateStore.GetContractSnapshot(contractId);
                if (snapshot == null)
                {
                    noSnapshot++;
                    ParsekLog.Verbose(Tag,
                        $"PatchContracts: no snapshot for contractId='{contractId}' — skipping");
                    continue;
                }

                // Clone the snapshot — Contract.Load mutates the input by removing "type"
                ConfigNode cloned = snapshot.CreateCopy();

                // Read and remove the type value (same pattern as ContractSystem.LoadContract)
                string typeName = cloned.GetValue("type");
                if (string.IsNullOrEmpty(typeName))
                {
                    noType++;
                    ParsekLog.Warn(Tag,
                        $"PatchContracts: snapshot for contractId='{contractId}' has no 'type' value — skipping");
                    continue;
                }

                cloned.RemoveValues("type");

                // Look up the contract type in KSP's type registry
                Type contractType = ContractSystem.GetContractType(typeName);
                if (contractType == null)
                {
                    typeNotFound++;
                    ParsekLog.Warn(Tag,
                        $"PatchContracts: type '{typeName}' not found in ContractSystem.ContractTypes " +
                        $"for contractId='{contractId}' — skipping (mod removed?)");
                    continue;
                }

                // Create instance and load state from ConfigNode
                try
                {
                    Contract contract = (Contract)Activator.CreateInstance(contractType);
                    contract = Contract.Load(contract, cloned);

                    if (contract == null)
                    {
                        loadFailed++;
                        ParsekLog.Warn(Tag,
                            $"PatchContracts: Contract.Load returned null for contractId='{contractId}' " +
                            $"type='{typeName}' — skipping");
                        continue;
                    }

                    // Only add Active-state contracts. GetActiveContractIds only yields actives,
                    // so any other state here is either a mis-snapshotted entry or a contract
                    // that mutated between snapshot and now — either way, we do NOT mutate the
                    // Finished bucket (see #404: append-only game history).
                    if (contract.ContractState == Contract.State.Active)
                    {
                        currentContracts.Add(contract);

                        // Re-register so parameters subscribe to GameEvents
                        try
                        {
                            contract.Register();
                            registered++;
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn(Tag,
                                $"PatchContracts: failed to register contract '{contractId}' " +
                                $"type='{typeName}': {ex.Message}");
                        }

                        restored++;
                    }
                    else
                    {
                        ParsekLog.Warn(Tag,
                            $"PatchContracts: snapshot for contractId='{contractId}' loaded in " +
                            $"state={contract.ContractState} (expected Active) — skipping, not mutating " +
                            $"Finished bucket");
                    }
                }
                catch (Exception ex)
                {
                    loadFailed++;
                    ParsekLog.Warn(Tag,
                        $"PatchContracts: failed to create/load contract contractId='{contractId}' " +
                        $"type='{typeName}': {ex.Message}");
                }
            }

            // 3. Fire contracts loaded event so UI refreshes
            try
            {
                GameEvents.Contract.onContractsLoaded.Fire();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PatchContracts: onContractsLoaded.Fire() threw: {ex.Message}");
            }

            int kspTotalAfter = currentContracts != null ? currentContracts.Count : 0;
            ParsekLog.Info(Tag,
                $"PatchContracts: removedStale={removedStale.ToString(IC)}, " +
                $"restored={restored.ToString(IC)}, " +
                $"registered={registered.ToString(IC)}, " +
                $"skippedExisting={skippedExisting.ToString(IC)}, " +
                $"noSnapshot={noSnapshot.ToString(IC)}, " +
                $"noType={noType.ToString(IC)}, " +
                $"typeNotFound={typeNotFound.ToString(IC)}, " +
                $"loadFailed={loadFailed.ToString(IC)}, " +
                $"ledgerActive={activeIds.Count.ToString(IC)}, " +
                $"kspTotal={kspTotalAfter.ToString(IC)} " +
                $"(Offered/Finished preserved)");
        }

        // ================================================================
        // Testable pure helpers for #404 filtered contract partitioning
        // ================================================================

        /// <summary>
        /// Represents the current-state classification of a KSP contract for PatchContracts
        /// filtering. Active contracts are the only ones Parsek considers mutating; every
        /// other state (Offered, Declined, Cancelled, Failed, Completed, DeadlineExpired)
        /// is preserved untouched.
        /// </summary>
        internal struct ContractFilterEntry
        {
            public Guid Id;
            public bool IsActive;
        }

        /// <summary>
        /// Pure helper: partitions the current KSP contract list into {stale Actives to
        /// remove} and {surviving Actives to keep in place}. Non-Active entries are
        /// implicitly preserved by being absent from the remove list. Extracted from
        /// <see cref="PatchContracts"/> so the filtering logic can be unit-tested without
        /// real KSP Contract instances.
        ///
        /// The rules:
        /// <list type="number">
        ///   <item>An Active contract whose ID is NOT in the ledger's active set is "stale" and goes to <paramref name="toRemove"/>.</item>
        ///   <item>An Active contract whose ID IS in the ledger's active set is "surviving" and goes to <paramref name="surviving"/>.</item>
        ///   <item>A non-Active contract is left out of both lists — callers MUST NOT touch it.</item>
        /// </list>
        /// </summary>
        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> surviving)
        {
            toRemove = new List<Guid>();
            surviving = new HashSet<Guid>();
            if (currentEntries == null) return;

            for (int i = 0; i < currentEntries.Count; i++)
            {
                var entry = currentEntries[i];
                if (!entry.IsActive) continue;        // preserve non-Active
                if (activeIdSet != null && activeIdSet.Contains(entry.Id))
                    surviving.Add(entry.Id);
                else
                    toRemove.Add(entry.Id);
            }
        }

        internal static void ResetForTesting()
        {
            SuppressUnityCallsForTesting = false;
        }
    }
}
