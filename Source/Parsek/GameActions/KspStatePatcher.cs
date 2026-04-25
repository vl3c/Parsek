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
        private const double RecordStateEpsilon = 0.000001;
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
            ContractsModule contracts = null,
            IReadOnlyCollection<string> targetTechIds = null,
            bool authoritativeRepeatableRecordState = false,
            double? techUtCutoff = null,
            double? techBaselineUt = null)
        {
            using (SuppressionGuard.ResourcesAndReplay())
            {
                PatchScience(science);
                PatchTechTree(targetTechIds, techUtCutoff, techBaselineUt);
                PatchFunds(funds);
                PatchReputation(reputation);
                PatchFacilities(facilities);
                PatchMilestones(milestones, authoritativeRepeatableRecordState);
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
                    "PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) — skipping");
                return;
            }

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
            if (!science.HasSeed)
            {
                ParsekLog.Verbose(Tag,
                    "PatchScience: module has no ScienceInitial seed — skipping to preserve KSP values");
                return;
            }

            float currentScience = ResearchAndDevelopment.Instance.Science;
            double targetScience = AdjustSciencePatchTargetForPendingRecentTechResearch(
                science.GetAvailableScience(),
                currentScience);
            float delta = (float)(targetScience - (double)currentScience);

            if (Math.Abs(delta) < 0.001f)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchScience: balance unchanged (current={currentScience.ToString("F1", IC)}, " +
                    $"target={targetScience.ToString("F1", IC)})");
            }
            else
            {
                // #402 parity: WARN when science drawdown >10% of a non-trivial pool.
                if (IsSuspiciousDrawdown(delta, currentScience))
                {
                    ParsekLog.Warn(Tag,
                        $"PatchScience: suspicious drawdown delta={delta.ToString("F1", IC)} " +
                        $"from current={currentScience.ToString("F1", IC)} " +
                        $"(>10% of pool, target={targetScience.ToString("F1", IC)}) — " +
                        $"earning channel may be missing. HasSeed={science.HasSeed}");
                }
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
        /// Builds the authoritative researched-tech set for a recalculation patch.
        /// Baselines provide the researched nodes already present at the selected point
        /// in time; affordable ScienceSpending actions add tech nodes unlocked by the
        /// ledger walk up to the same cutoff.
        /// </summary>
        internal static HashSet<string> BuildTargetTechIdsForPatch(
            IReadOnlyList<GameStateBaseline> baselines,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            var selectedBaseline = SelectTechBaselineForPatch(baselines, utCutoff);
            if (selectedBaseline == null ||
                selectedBaseline.researchedTechIds == null ||
                selectedBaseline.researchedTechIds.Count == 0)
            {
                return null;
            }

            var target = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < selectedBaseline.researchedTechIds.Count; i++)
            {
                string techId = selectedBaseline.researchedTechIds[i];
                if (!string.IsNullOrEmpty(techId))
                    target.Add(techId);
            }

            if (actions == null)
                return target;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null || action.Type != GameActionType.ScienceSpending)
                    continue;
                if (string.IsNullOrEmpty(action.NodeId))
                    continue;
                if (utCutoff.HasValue && action.UT > utCutoff.Value)
                    continue;
                if (!action.Affordable)
                    continue;

                target.Add(action.NodeId);
            }

            return target;
        }

        /// <summary>
        /// Returns the UT of the baseline selected by <see cref="SelectTechBaselineForPatch"/>
        /// for the given cutoff, or <c>null</c> when no baseline is eligible. Exposes the
        /// selection UT so callers (e.g., LedgerOrchestrator) can forward it to logs without
        /// re-walking the baseline list.
        /// </summary>
        internal static double? GetSelectedTechBaselineUt(
            IReadOnlyList<GameStateBaseline> baselines,
            double? utCutoff)
        {
            var selected = SelectTechBaselineForPatch(baselines, utCutoff);
            return selected == null ? (double?)null : selected.ut;
        }

        private static GameStateBaseline SelectTechBaselineForPatch(
            IReadOnlyList<GameStateBaseline> baselines,
            double? utCutoff)
        {
            if (baselines == null || baselines.Count == 0)
                return null;

            GameStateBaseline selected = null;
            if (utCutoff.HasValue)
            {
                double cutoff = utCutoff.Value;
                for (int i = 0; i < baselines.Count; i++)
                {
                    var baseline = baselines[i];
                    if (baseline == null)
                        continue;
                    if (baseline.ut <= cutoff &&
                        (selected == null || baseline.ut > selected.ut))
                    {
                        selected = baseline;
                    }
                }

                return selected;
            }

            for (int i = 0; i < baselines.Count; i++)
            {
                var baseline = baselines[i];
                if (baseline == null)
                    continue;
                if (selected == null || baseline.ut > selected.ut)
                    selected = baseline;
            }

            return selected;
        }

        /// <summary>
        /// Patches KSP's R&amp;D tech state to the recalculated timeline state.
        /// This updates both the authoritative proto-tech dictionary and the static
        /// tech-tree proto nodes, then asks KSP to refresh the tech-tree UI.
        /// </summary>
        internal static void PatchTechTree(
            IReadOnlyCollection<string> targetTechIds,
            double? utCutoff = null,
            double? baselineUt = null)
        {
            if (targetTechIds == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchTechTree: no target tech set supplied — skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchTechTree: ResearchAndDevelopment.Instance is null — skipping");
                return;
            }

            if (AssetBase.RnDTechTree == null || AssetBase.RnDTechTree.GetTreeTechs() == null)
            {
                ParsekLog.Verbose(Tag,
                    "PatchTechTree: RnDTechTree unavailable — skipping");
                return;
            }

            var target = targetTechIds as HashSet<string>
                ?? new HashSet<string>(targetTechIds, StringComparer.Ordinal);

            var protoNodes = GetProtoTechNodesDictionary();
            if (protoNodes == null)
            {
                ParsekLog.Warn(Tag,
                    "PatchTechTree: protoTechNodes dictionary unavailable — static tech-tree nodes will still be patched");
            }

            int madeAvailable = 0;
            int madeUnavailable = 0;
            int alreadyAvailable = 0;
            int alreadyUnavailable = 0;
            int missingTargets = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> missingTargetIds = null;

            foreach (var tech in AssetBase.RnDTechTree.GetTreeTechs())
            {
                if (tech == null || string.IsNullOrEmpty(tech.techID))
                    continue;

                string techId = tech.techID;
                seen.Add(techId);
                bool shouldBeAvailable = target.Contains(techId);
                RDTech.State currentState = ResearchAndDevelopment.GetTechnologyState(techId);

                if (shouldBeAvailable)
                {
                    ProtoTechNode proto = ResearchAndDevelopment.Instance.GetTechState(techId);
                    if (proto == null || proto.state != RDTech.State.Available)
                        madeAvailable++;
                    else
                        alreadyAvailable++;

                    proto = EnsureAvailableProtoTechNode(tech, proto);
                    ResearchAndDevelopment.Instance.SetTechState(techId, proto);
                    tech.state = RDTech.State.Available;
                }
                else
                {
                    if (currentState == RDTech.State.Available || tech.state == RDTech.State.Available)
                        madeUnavailable++;
                    else
                        alreadyUnavailable++;

                    if (protoNodes != null)
                        protoNodes.Remove(techId);
                    tech.state = RDTech.State.Unavailable;
                }
            }

            foreach (string techId in target)
            {
                if (seen.Contains(techId))
                    continue;
                missingTargets++;
                if (missingTargetIds == null)
                    missingTargetIds = new List<string>();
                if (missingTargetIds.Count < 10)
                    missingTargetIds.Add(techId);
            }

            RefreshTechTreeUi();

            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", IC)
                : "null";
            string baselineLabel = baselineUt.HasValue
                ? baselineUt.Value.ToString("R", IC)
                : "null";
            ParsekLog.Info(Tag,
                $"PatchTechTree: available={target.Count.ToString(IC)}, " +
                $"madeAvailable={madeAvailable.ToString(IC)}, " +
                $"madeUnavailable={madeUnavailable.ToString(IC)}, " +
                $"alreadyAvailable={alreadyAvailable.ToString(IC)}, " +
                $"alreadyUnavailable={alreadyUnavailable.ToString(IC)}, " +
                $"missingTargets={missingTargets.ToString(IC)}, " +
                $"utCutoff={cutoffLabel}, baselineUt={baselineLabel}");

            if (missingTargets > 0 && missingTargetIds != null)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchTechTree: first {missingTargetIds.Count.ToString(IC)} missing tech ids (of {missingTargets.ToString(IC)}): " +
                    string.Join(", ", missingTargetIds));
            }
        }

        private static ProtoTechNode EnsureAvailableProtoTechNode(ProtoTechNode tech, ProtoTechNode existing)
        {
            var proto = existing ?? new ProtoTechNode();
            proto.techID = tech.techID;
            proto.state = RDTech.State.Available;
            proto.scienceCost = tech.scienceCost;
            if (proto.partsPurchased == null)
                proto.partsPurchased = new List<AvailablePart>();

            // #559 review follow-up: rehydrate bypass-entry-purchase parts unconditionally
            // when bypass is on. The previous `partsPurchased.Count == 0` guard missed the
            // stale/partial case where a proto node kept a subset of purchased parts from a
            // prior unlock — those carried-over entries would block AddPurchasedPartsForTech
            // and leave the tech tree silently missing the rest. AddPurchasedPartsForTech
            // already dedups by reference + part.name, so re-running it is safe.
            if (GameStateRecorder.IsBypassEntryPurchaseAfterResearch())
            {
                int beforeCount = proto.partsPurchased.Count;
                int added = AddPurchasedPartsForTech(proto, tech.techID, PartLoader.LoadedPartsList);
                if (added == 0 && beforeCount == 0 && tech.partsPurchased != null)
                    AddPurchasedParts(proto, tech.partsPurchased);
                ParsekLog.Verbose(Tag,
                    $"EnsureAvailableProtoTechNode: bypass rehydrate techId={tech.techID} " +
                    $"before={beforeCount.ToString(IC)} added={added.ToString(IC)} " +
                    $"after={proto.partsPurchased.Count.ToString(IC)}");
            }

            return proto;
        }

        internal static int AddPurchasedPartsForTech(
            ProtoTechNode proto, string techId, IEnumerable<AvailablePart> loadedParts)
        {
            if (proto == null || string.IsNullOrEmpty(techId) || loadedParts == null)
                return 0;

            if (proto.partsPurchased == null)
                proto.partsPurchased = new List<AvailablePart>();

            int added = 0;
            foreach (var part in loadedParts)
            {
                if (part == null)
                    continue;
                if (!string.Equals(part.TechRequired, techId, StringComparison.Ordinal))
                    continue;
                if (ContainsPurchasedPart(proto.partsPurchased, part))
                    continue;

                proto.partsPurchased.Add(part);
                added++;
            }

            return added;
        }

        private static void AddPurchasedParts(ProtoTechNode proto, IEnumerable<AvailablePart> parts)
        {
            foreach (var part in parts)
            {
                if (part == null || ContainsPurchasedPart(proto.partsPurchased, part))
                    continue;
                proto.partsPurchased.Add(part);
            }
        }

        private static bool ContainsPurchasedPart(List<AvailablePart> parts, AvailablePart candidate)
        {
            if (parts == null || candidate == null)
                return false;

            for (int i = 0; i < parts.Count; i++)
            {
                var existing = parts[i];
                if (existing == null)
                    continue;
                if (object.ReferenceEquals(existing, candidate))
                    return true;
                if (!string.IsNullOrEmpty(candidate.name)
                    && string.Equals(existing.name, candidate.name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        // #559 review follow-up: one-shot reflection-failure diagnostics. When KSP renames
        // or changes the visibility of `protoTechNodes` / its backing field, rewind patches
        // silently fall back to static-tree-only coverage. The Warn log fires exactly once
        // per session (guarded by a static bool; reset in ResetForTesting) to flag the
        // regression without spamming per-call.
        private static bool protoTechNodesReflectionWarnEmitted;

        private static Dictionary<string, ProtoTechNode> GetProtoTechNodesDictionary()
        {
            if (ResearchAndDevelopment.Instance == null)
                return null;

            var field = typeof(ResearchAndDevelopment).GetField(
                "protoTechNodes",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                EmitProtoTechNodesReflectionWarnOnce(
                    "field typeof(ResearchAndDevelopment).GetField(\"protoTechNodes\") returned null");
                return null;
            }

            var value = field.GetValue(ResearchAndDevelopment.Instance)
                as Dictionary<string, ProtoTechNode>;
            if (value == null)
            {
                EmitProtoTechNodesReflectionWarnOnce(
                    "field.GetValue returned null or wrong type for \"protoTechNodes\"");
            }
            return value;
        }

        // Internal so xUnit can drive the one-shot warn path without booting R&D singletons.
        internal static void EmitProtoTechNodesReflectionWarnOnce(string detail)
        {
            if (protoTechNodesReflectionWarnEmitted)
                return;
            protoTechNodesReflectionWarnEmitted = true;
            ParsekLog.Warn(Tag,
                "PatchTechTree: reflection lookup for ResearchAndDevelopment.protoTechNodes failed " +
                $"(one-shot per session) — {detail}. Static tech-tree nodes will still be patched, " +
                "but future nodes cannot be removed from the proto dictionary until this is resolved.");
        }

        private static void RefreshTechTreeUi()
        {
            try
            {
                ResearchAndDevelopment.RefreshTechTreeUI();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PatchTechTree: RefreshTechTreeUI threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Keeps recent stock tech-unlock debits authoritative while the matching
        /// <c>TechResearched</c> ledger action is still catching up.
        /// </summary>
        internal static double AdjustSciencePatchTargetForPendingRecentTechResearch(
            double targetScience,
            float currentScience)
        {
            if (targetScience <= (double)currentScience)
                return targetScience;

            double pendingDebit = LedgerOrchestrator.GetPendingRecentKscTechResearchScienceDebit();
            if (pendingDebit <= 0.0)
                return targetScience;

            double adjustedTarget = targetScience - pendingDebit;
            if (adjustedTarget < (double)currentScience)
                adjustedTarget = (double)currentScience;

            if (adjustedTarget < targetScience)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchScience: holding back {pendingDebit.ToString("F1", IC)} pending tech-unlock science " +
                    $"(current={currentScience.ToString("F1", IC)}, " +
                    $"ledgerTarget={targetScience.ToString("F1", IC)}, " +
                    $"adjustedTarget={adjustedTarget.ToString("F1", IC)})");
            }

            return adjustedTarget;
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

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
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

            // #402: defensive WARN when a single recalculation drops more than 10% of
            // the live funds pool. Legitimate walks can subtract large amounts after a
            // revert, so this is log-only (never aborts the patch) — but a >10% drop
            // alongside a small pool (>1000F) is the shape of missing-earnings bugs.
            if (IsSuspiciousDrawdown(delta, currentFunds))
            {
                ParsekLog.Warn(Tag,
                    $"PatchFunds: suspicious drawdown delta={delta.ToString("F1", IC)} " +
                    $"from current={currentFunds.ToString("F1", IC)} " +
                    $"(>10% of pool, target={targetFunds.ToString("F1", IC)}) — " +
                    $"earning channel may be missing. HasSeed={funds.HasSeed}");
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

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
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

            // Bug #596: gate INFO on changed-state-or-not-found-diagnostic only.
            // skippedCount increments when a facility is already at its target
            // level (no-op pass), so a steady-state non-empty facility list
            // would still emit the INFO summary on every recalc if `skipped`
            // counted toward the gate. Use `patched + notFound > 0` so INFO
            // fires only when real game state changed (`patched`) or when a
            // facility lookup miss is worth surfacing (`notFound`). Skipped-
            // only summaries (steady state, nothing to do) and the all-zero
            // empty-totals case both route through `VerboseRateLimited` so the
            // diagnostic stays available when investigating without flooding
            // INFO.
            int materialWork = patchedCount + notFoundCount;
            if (materialWork > 0)
            {
                ParsekLog.Info(Tag,
                    $"PatchFacilities: levels patched={patchedCount.ToString(IC)}, " +
                    $"skipped={skippedCount.ToString(IC)}, " +
                    $"notFound={notFoundCount.ToString(IC)}, " +
                    $"total={allFacilities.Count}");
            }
            else if (skippedCount > 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "patch-facilities-skipped-only",
                    $"PatchFacilities: skipped-only steady state " +
                    $"(patched=0, skipped={skippedCount.ToString(IC)}, " +
                    $"notFound=0, total={allFacilities.Count})");
            }
            else
            {
                ParsekLog.VerboseRateLimited(Tag, "patch-facilities-empty",
                    $"PatchFacilities: nothing to patch (total={allFacilities.Count})");
            }

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
        /// normal once-ever nodes and rebuilding full stock interval state for repeatable
        /// Records* nodes.
        ///
        /// Body-specific nodes (under CelestialBodySubtree) are matched using path-qualified
        /// IDs ("Mun/Landing"), while top-level nodes use bare IDs ("FirstLaunch").
        /// Old recordings with bare body-specific IDs ("Landing" instead of "Mun/Landing")
        /// will not match and are logged for diagnostics.
        ///
        /// Repeatable Records* nodes have two patch modes: normal same-branch recalculations
        /// preserve a live in-tier best value when it still fits the currently paid reward
        /// band, while authoritative rewind-style patching rebuilds strictly from ledger-
        /// backed hit counts so discarded future progress cannot leak through.
        ///
        /// Note: After clearing a node, KSP's ProgressTracking.Update() may immediately
        /// re-trigger it if conditions are still met (e.g., a vessel is in orbit). This is
        /// correct behavior — milestones whose conditions are still satisfied SHOULD remain
        /// achieved. The IsReplayingActions flag suppresses GameStateRecorder from capturing
        /// re-triggered milestones as new events.
        /// </summary>
        internal static void PatchMilestones(MilestonesModule milestones,
            bool authoritativeRepeatableRecordState = false)
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
                "", authoritativeRepeatableRecordState,
                ref credited, ref unreached, ref skipped);

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
        /// Repeatable Records* nodes are handled specially: their effective hit count drives
        /// reached/complete plus the stock numeric record interval state.
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
            bool authoritativeRepeatableRecordState,
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

                int effectiveCount = milestones.GetEffectiveMilestoneCount(qualifiedId);
                if (PatchRepeatableRecordNode(
                    node, effectiveCount, qualifiedId, reachedField, completeField,
                    authoritativeRepeatableRecordState, out bool repeatableChanged))
                {
                    if (repeatableChanged)
                    {
                        if (effectiveCount > 0)
                            credited++;
                        else
                            unreached++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
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
                            // Bug #594: this diagnostic fires every recalc walk for the
                            // same (nodeId, qualifiedId) pair — once a fallback match
                            // exists for an old recording, every walk re-emits the
                            // same line. Rate-limit per pair so the diagnostic still
                            // surfaces when a new fallback path appears, without
                            // flooding the log on steady-state walks.
                            string fallbackKey = string.Format(IC,
                                "patch-milestones-fallback-{0}-{1}",
                                nodeId, qualifiedId);
                            ParsekLog.VerboseRateLimited(Tag, fallbackKey,
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
                }

                // Recurse into subtree children
                if (node.Subtree != null && node.Subtree.Count > 0)
                {
                    PatchProgressNodeTree(node.Subtree, milestones,
                        reachedField, completeField, mannedProp, unmannedProp,
                        qualifiedId, authoritativeRepeatableRecordState,
                        ref credited, ref unreached, ref skipped);
                }
            }
        }

        internal struct RepeatableRecordState
        {
            public bool Reached;
            public bool Complete;
            public double Record;
            public double RewardThreshold;
            public int RewardInterval;
        }

        /// <summary>
        /// Computes the stock save-state shape for KSP's repeatable record nodes from the
        /// number of effective ledger hits that survived recalculation. Used by
        /// authoritative rewind-style patching where only ledger-backed paid thresholds
        /// may survive.
        /// </summary>
        internal static bool TryComputeRepeatableRecordState(
            ProgressNode node, int effectiveCount, out RepeatableRecordState state)
        {
            return TryComputeRepeatableRecordState(
                node, effectiveCount, currentRecord: double.NaN, out state);
        }

        /// <summary>
        /// Computes the stock save-state shape for KSP's repeatable record nodes from the
        /// number of effective ledger hits that survived recalculation, while preserving the
        /// live best-value progress when it still fits inside that reward band, including
        /// the initial unpaid band before the first ledger-backed hit exists.
        /// </summary>
        internal static bool TryComputeRepeatableRecordState(
            ProgressNode node, int effectiveCount, double currentRecord,
            out RepeatableRecordState state)
        {
            state = default;
            if (!TryGetRepeatableRecordDefinition(node, out double maxRecord, out double roundValue))
                return false;

            double boundedCurrentRecord = NormalizeRepeatableRecordValue(currentRecord, maxRecord);
            if (effectiveCount <= 0)
            {
                int initialInterval = 1;
                double initialThreshold = FinePrint.Utilities.ProgressUtilities.FindNextRecord(
                    0.0, maxRecord, roundValue, ref initialInterval);

                if (boundedCurrentRecord > RecordStateEpsilon &&
                    !double.IsInfinity(initialThreshold) &&
                    !double.IsNaN(initialThreshold) &&
                    initialThreshold != double.MaxValue &&
                    boundedCurrentRecord < initialThreshold - RecordStateEpsilon)
                {
                    state.Reached = true;
                    state.Complete = false;
                    state.Record = boundedCurrentRecord;
                    state.RewardThreshold = initialThreshold;
                    state.RewardInterval = initialInterval;
                    return true;
                }

                state.Reached = false;
                state.Complete = false;
                state.Record = 0.0;
                state.RewardThreshold = 0.0;
                state.RewardInterval = 1;
                return true;
            }

            double paidRecord = 0.0;
            int clampedEffectiveCount = Math.Max(0, effectiveCount);
            for (int i = 0; i < clampedEffectiveCount; i++)
            {
                int interval = 1;
                double nextRecord = FinePrint.Utilities.ProgressUtilities.FindNextRecord(
                    paidRecord, maxRecord, roundValue, ref interval);
                if (double.IsInfinity(nextRecord) || double.IsNaN(nextRecord) ||
                    nextRecord == double.MaxValue)
                {
                    state.Reached = true;
                    state.Complete = true;
                    state.Record = maxRecord;
                    state.RewardThreshold = 0.0;
                    state.RewardInterval = 1;
                    return true;
                }

                paidRecord = nextRecord;
            }

            int nextInterval = 1;
            double nextThreshold = FinePrint.Utilities.ProgressUtilities.FindNextRecord(
                paidRecord, maxRecord, roundValue, ref nextInterval);
            bool isComplete = nextThreshold == double.MaxValue ||
                paidRecord >= maxRecord - RecordStateEpsilon;

            double record = paidRecord;
            if (isComplete)
            {
                record = maxRecord;
            }
            else if (boundedCurrentRecord > paidRecord + RecordStateEpsilon &&
                boundedCurrentRecord < nextThreshold - RecordStateEpsilon)
            {
                // Preserve the live best value as long as it still fits within the reward
                // band implied by the effective hit count. If it spills into a later band,
                // the extra rewards did not survive recalculation, so we fall back to the
                // last paid threshold instead of inventing an in-between value.
                record = boundedCurrentRecord;
            }

            state.Reached = record > RecordStateEpsilon;
            state.Complete = isComplete;
            state.Record = record;
            state.RewardThreshold = isComplete ? 0.0 : nextThreshold;
            state.RewardInterval = isComplete ? 1 : nextInterval;
            return true;
        }

        /// <summary>
        /// Rebuilds live state for KSP's repeatable record nodes
        /// (RecordsAltitude/Depth/Speed/Distance) from the effective count that survived the
        /// ledger walk. Returns true when the node was recognized and patched.
        /// </summary>
        internal static bool PatchRepeatableRecordNode(
            ProgressNode node, int effectiveCount, string qualifiedId,
            bool authoritativeRepeatableRecordState = false)
        {
            FieldInfo reachedField = typeof(ProgressNode).GetField("reached",
                BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo completeField = typeof(ProgressNode).GetField("complete",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (reachedField == null || completeField == null)
            {
                ParsekLog.Warn(Tag,
                    "PatchMilestones: could not find reached/complete fields for repeatable record patching");
                return false;
            }

            return PatchRepeatableRecordNode(node, effectiveCount, qualifiedId,
                reachedField, completeField, authoritativeRepeatableRecordState, out _);
        }

        /// <summary>
        /// Returns true when the node is one of KSP's four repeatable world-record subclasses
        /// (<c>KSPAchievements.RecordsAltitude</c>, <c>KSPAchievements.RecordsDepth</c>,
        /// <c>KSPAchievements.RecordsSpeed</c>, <c>KSPAchievements.RecordsDistance</c>).
        /// Uses string-based full-name comparison so production matching stays scoped to the
        /// stock repeatable types without a compile-time reference to <c>KSPAchievements</c>.
        /// When <see cref="SuppressUnityCallsForTesting"/> is enabled, a simple-name fallback
        /// accepts xUnit stand-ins with matching type names.
        ///
        /// One-shot nodes (<c>Orbit</c>, <c>Landing</c>, <c>Flyby</c>, <c>Flight</c>, etc.) return
        /// false here — they are patched through the caller's one-shot achieve/un-achieve branch,
        /// not through the repeatable-record pipeline.
        /// </summary>
        internal static bool IsRepeatableRecordType(ProgressNode node)
        {
            if (node == null) return false;
            string fullName = node.GetType().FullName;
            if (fullName == "KSPAchievements.RecordsAltitude"
                || fullName == "KSPAchievements.RecordsDepth"
                || fullName == "KSPAchievements.RecordsSpeed"
                || fullName == "KSPAchievements.RecordsDistance")
            {
                return true;
            }

            if (!SuppressUnityCallsForTesting)
                return false;

            string name = node.GetType().Name;
            return name == "RecordsAltitude"
                || name == "RecordsDepth"
                || name == "RecordsSpeed"
                || name == "RecordsDistance";
        }

        /// <summary>
        /// Rebuilds live state for KSP's repeatable record nodes
        /// (RecordsAltitude/Depth/Speed/Distance) from the effective count that survived the
        /// ledger walk. Returns true when the node was recognized; <paramref name="changed"/>
        /// indicates whether any live state actually changed.
        ///
        /// Nodes whose type is not one of the four stock repeatable-record subclasses return
        /// false with no log, leaving them to the caller's one-shot patch path. This keeps
        /// the 392+ per-body sub-achievements (<c>Bop/Orbit</c>, <c>Dres/Flight</c>, …) out of
        /// the record-field reflection branch and its missing-field WARN. See bug #455.
        /// </summary>
        internal static bool PatchRepeatableRecordNode(
            ProgressNode node, int effectiveCount, string qualifiedId,
            FieldInfo reachedField, FieldInfo completeField,
            bool authoritativeRepeatableRecordState, out bool changed)
        {
            changed = false;

            // Pre-filter by type so one-shot nodes (Orbit/Landing/Flyby/Flight/…) fall through
            // to the caller's one-shot branch. Returning false here (vs. true) is load-bearing:
            // `true` would short-circuit PatchProgressNodeTree and leave those nodes unpatched.
            if (!IsRepeatableRecordType(node))
                return false;

            Type nodeType = node.GetType();
            FieldInfo recordField = nodeType.GetField("record",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo rewardThresholdField = nodeType.GetField("rewardThreshold",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo rewardIntervalField = nodeType.GetField("rewardInterval",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo recordHolderField = nodeType.GetField("recordHolder",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (recordField == null || rewardThresholdField == null || rewardIntervalField == null)
            {
                // A node whose type IS one of the four record subclasses but is missing the
                // stock record fields — that's a genuine KSP-API structural change and should
                // page someone. Keep the WARN.
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{qualifiedId}' is missing stock record fields " +
                    $"(record={recordField != null}, rewardThreshold={rewardThresholdField != null}, " +
                    $"rewardInterval={rewardIntervalField != null}) — skipping");
                return true;
            }

            RepeatableRecordState targetState;
            if (authoritativeRepeatableRecordState)
            {
                if (!TryComputeRepeatableRecordState(node, effectiveCount, out targetState))
                    return false;
            }
            else
            {
                double liveRecord = NormalizeRepeatableRecordValue(recordField.GetValue(node), double.MaxValue);
                if (!TryComputeRepeatableRecordState(
                    node, effectiveCount, liveRecord, out targetState))
                {
                    return false;
                }
            }

            bool currentReached = node.IsReached;
            if (currentReached != targetState.Reached)
            {
                reachedField.SetValue(node, targetState.Reached);
                changed = true;
            }

            bool currentComplete = node.IsComplete;
            if (currentComplete != targetState.Complete)
            {
                completeField.SetValue(node, targetState.Complete);
                changed = true;
            }

            double currentRecordValue = (double)recordField.GetValue(node);
            if (Math.Abs(currentRecordValue - targetState.Record) > RecordStateEpsilon)
            {
                recordField.SetValue(node, targetState.Record);
                changed = true;
            }

            double currentRewardThreshold = (double)rewardThresholdField.GetValue(node);
            if (Math.Abs(currentRewardThreshold - targetState.RewardThreshold) > RecordStateEpsilon)
            {
                rewardThresholdField.SetValue(node, targetState.RewardThreshold);
                changed = true;
            }

            int currentRewardInterval = (int)rewardIntervalField.GetValue(node);
            if (currentRewardInterval != targetState.RewardInterval)
            {
                rewardIntervalField.SetValue(node, targetState.RewardInterval);
                changed = true;
            }

            if (recordHolderField != null && recordHolderField.GetValue(node) != null)
            {
                recordHolderField.SetValue(node, null);
                changed = true;
            }

            Action<Vessel> targetIterator = targetState.Complete
                ? null
                : CreateRepeatableRecordIterator(node);
            if (!targetState.Complete && targetIterator == null)
            {
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{qualifiedId}' has no iterateVessels method — future record tracking may stall");
                targetIterator = node.OnIterateVessels;
            }
            if (!object.Equals(node.OnIterateVessels, targetIterator))
            {
                node.OnIterateVessels = targetIterator;
                changed = true;
            }

            if (changed)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchMilestones: synced repeatable record '{qualifiedId}' " +
                    $"hits={effectiveCount.ToString(IC)} reached={targetState.Reached.ToString(IC)} " +
                    $"complete={targetState.Complete.ToString(IC)} " +
                    $"record={targetState.Record.ToString("F1", IC)} " +
                    $"nextThreshold={targetState.RewardThreshold.ToString("F1", IC)} " +
                    $"nextInterval={targetState.RewardInterval.ToString(IC)}");
            }

            return true;
        }

        private static double NormalizeRepeatableRecordValue(object value, double maxRecord)
        {
            if (value == null)
                return 0.0;

            double numericValue;
            if (value is double doubleValue)
                numericValue = doubleValue;
            else if (value is float floatValue)
                numericValue = floatValue;
            else if (value is int intValue)
                numericValue = intValue;
            else if (value is long longValue)
                numericValue = longValue;
            else
                return 0.0;

            if (double.IsNaN(numericValue) || double.IsInfinity(numericValue))
                return 0.0;
            if (numericValue <= 0.0)
                return 0.0;
            return numericValue > maxRecord ? maxRecord : numericValue;
        }

        private static bool TryGetRepeatableRecordDefinition(
            ProgressNode node, out double maxRecord, out double roundValue)
        {
            maxRecord = 0.0;
            roundValue = 0.0;
            if (node == null) return false;

            string maxPropertyName;
            double fallbackMaxRecord;
            switch (node.GetType().FullName)
            {
                case "KSPAchievements.RecordsAltitude":
                    maxPropertyName = "maxAltitude";
                    fallbackMaxRecord = 70000.0;
                    roundValue = 500.0;
                    break;
                case "KSPAchievements.RecordsDepth":
                    maxPropertyName = "maxDepth";
                    fallbackMaxRecord = 750.0;
                    roundValue = 10.0;
                    break;
                case "KSPAchievements.RecordsSpeed":
                    maxPropertyName = "maxSpeed";
                    fallbackMaxRecord = 2500.0;
                    roundValue = 5.0;
                    break;
                case "KSPAchievements.RecordsDistance":
                    maxPropertyName = "maxDistance";
                    fallbackMaxRecord = 100000.0;
                    roundValue = 1000.0;
                    break;
                default:
                    return false;
            }

            if (SuppressUnityCallsForTesting)
            {
                maxRecord = fallbackMaxRecord;
                return true;
            }

            PropertyInfo maxProperty = node.GetType().GetProperty(maxPropertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (maxProperty == null)
            {
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{node.Id ?? "<null>"}' missing '{maxPropertyName}' property");
                return false;
            }

            object maxValue;
            try
            {
                maxValue = maxProperty.GetValue(node, null);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchMilestones: repeatable node '{node.Id ?? "<null>"}' max lookup via '{maxPropertyName}' threw '{ex.Message}' — using fallback {fallbackMaxRecord.ToString("F1", IC)}");
                maxRecord = fallbackMaxRecord;
                return true;
            }

            if (!(maxValue is double))
            {
                ParsekLog.Warn(Tag,
                    $"PatchMilestones: repeatable node '{node.Id ?? "<null>"}' returned non-double max record");
                return false;
            }

            maxRecord = (double)maxValue;
            return true;
        }

        private static Action<Vessel> CreateRepeatableRecordIterator(ProgressNode node)
        {
            if (node == null) return null;

            MethodInfo iterateMethod = node.GetType().GetMethod("iterateVessels",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (iterateMethod == null)
                return null;

            return Delegate.CreateDelegate(typeof(Action<Vessel>), node, iterateMethod,
                throwOnBindFailure: false) as Action<Vessel>;
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

        /// <summary>
        /// Pure threshold check for #402: returns true when a negative delta removes
        /// more than 10% of a non-trivial pool (&gt;1000). Below the pool-floor threshold
        /// the warning is noisy and useless, so we stay silent. Positive deltas and
        /// small percentage drops are always allowed.
        /// </summary>
        internal static bool IsSuspiciousDrawdown(double delta, double currentPool)
        {
            if (delta >= 0) return false;
            if (currentPool <= 1000.0) return false;
            return Math.Abs(delta) > 0.10 * currentPool;
        }

        internal static void ResetForTesting()
        {
            SuppressUnityCallsForTesting = false;
            protoTechNodesReflectionWarnEmitted = false;
        }
    }
}
