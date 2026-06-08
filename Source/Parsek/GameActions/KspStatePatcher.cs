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

        // Max per-identity entries appended to a default-level (Info) apply line.
        // Keeps the Info aggregate diagnosable without unbounded log growth; the full
        // list is emitted alongside at Verbose.
        private const int IdentitySampleCap = 10;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private static void VerboseStablePatchState(string identity, string stateKey, string message)
        {
            ParsekLog.VerboseOnChange(Tag, identity, stateKey, message);
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
            bool authoritativeReduction = false)
        {
            using (SuppressionGuard.ResourcesAndReplay())
            {
                // Rewind read-back divergence guard (audit rec #1): when armed at the
                // rewind apply boundary, flag (and optionally abort) a recalc that would
                // write the economy below the real career floor. Default warn-only — the
                // runner returns false unless abort is opt-in/forced, so the normal path
                // logs and proceeds with NO behavior change. Inert (returns false fast)
                // when not armed.
                if (RunRewindReadbackGuard(science, funds, reputation, authoritativeReduction))
                {
                    ParsekLog.Warn(Tag,
                        "PatchAll: ABORTING rewind ledger patch: divergence flagged and abort enabled; " +
                        "economy/tech/contracts/milestones NOT patched, crew roster already applied; " +
                        "loaded quicksave values stand");
                    return;
                }

                PatchScience(science, suppressSuspiciousDrawdownWarnings, authoritativeReduction);
                PatchTechTree(targetTechIds, techUtCutoff, techBaselineUt);
                PatchFunds(funds, suppressSuspiciousDrawdownWarnings, authoritativeReduction);
                PatchReputation(reputation, authoritativeReduction);
                PatchFacilities(facilities);
                PatchMilestones(milestones, authoritativeRepeatableRecordState);
                PatchContracts(contracts);

                VerboseStablePatchState("patch-all-complete", "complete", "PatchAll complete");
            }
        }

        /// <summary>
        /// Patches KSP's science pool to match the module's available science.
        /// Uses AddScience with a computed delta to reach the target value.
        /// No-op if ResearchAndDevelopment.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchScience(
            ScienceModule science,
            bool suppressSuspiciousDrawdownWarnings = false,
            bool authoritativeReduction = false)
        {
            if (science == null)
            {
                ParsekLog.Warn(Tag, "PatchScience: null ScienceModule — skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                VerboseStablePatchState("patch-skip|science|rnd", "rnd-null",
                    "PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) — skipping");
                return;
            }

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
            if (!science.HasSeed)
            {
                VerboseStablePatchState("patch-skip|science|seed", "missing-seed",
                    "PatchScience: module has no ScienceInitial seed — skipping to preserve KSP values");
                return;
            }

            float currentScience = ResearchAndDevelopment.Instance.Science;
            double targetScience = AdjustSciencePatchTargetForPendingRecentTechResearch(
                science.GetAvailableScience(),
                currentScience);
            targetScience = AdjustSciencePatchTargetForPendingRecentScienceEarning(
                targetScience,
                currentScience);

            // "Keep what you earned" guard (plan §3.3 / §4.2): clamp keyed on the
            // NON-RESERVED running balance, not the reservation-aware target. A
            // reservation lowers only GetAvailableScience() while leaving
            // GetRunningScience() intact, so it never trips this; a missing-earning leak
            // lowers the running balance itself, so it does.
            //
            // DEVIATION from the literal plan seam (achieving its INTENT, plan §3.3 /
            // §2.6.3): the running-balance discriminator is the pending-adjusted running
            // balance, not the raw GetRunningScience(). The two pending adjusters above
            // (AdjustSciencePatchTargetForPendingRecent{ScienceEarning,TechResearch})
            // exist precisely for the brief window where a stock KSC science credit /
            // tech-unlock debit has hit the live singleton but the matching ledger action
            // is still catching up. During that window the raw running balance is
            // transiently below/above live for a KNOWN in-flight reason (not a missing
            // earning channel), so folding the same pending credit/debit into the
            // discriminator keeps the guard from a false clamp+toast on a transient
            // ledger-catch-up. The plan's invariant ("running below live ONLY when an
            // earning channel is missing") holds against this adjusted value.
            double runningScience = ComputePendingAdjustedRunningScience(science);
            double effTargetScience = ApplyDrawdownGuard(
                targetScience, runningScience, currentScience, 0.001,
                authoritativeReduction, "science", out bool scienceClamped,
                out ClampDirection scienceDirection);
            if (scienceClamped)
            {
                bool scienceDown = scienceDirection == ClampDirection.Down;
                ref bool scienceLatch = ref (scienceDown
                    ? ref scienceUpliftClampToastShownThisSession
                    : ref scienceClampToastShownThisSession);
                EmitDrawdownGuardClamp(
                    "Science", runningScience, currentScience,
                    wouldBeTarget: targetScience, clampedTo: effTargetScience,
                    toastText: scienceDown ? "Held your science at the spent value" : "Kept your earned science",
                    sessionToastLatch: ref scienceLatch,
                    perSubjectScienceNote: true,
                    direction: scienceDirection);
                targetScience = effTargetScience;
            }

            float delta = (float)(targetScience - (double)currentScience);

            if (Math.Abs(delta) < 0.001f)
            {
                VerboseStablePatchState("patch-noop|science", targetScience.ToString("F1", IC),
                    $"PatchScience: balance unchanged (current={currentScience.ToString("F1", IC)}, " +
                    $"target={targetScience.ToString("F1", IC)})");
            }
            else
            {
                // #402 parity: WARN when science drawdown >10% of a non-trivial pool.
                if (!suppressSuspiciousDrawdownWarnings && IsSuspiciousDrawdown(delta, currentScience))
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

                // LedgerTrace Tier-C: reconcile the computed target against the live
                // read-back we just took (afterScience). A drift beyond the science
                // tolerance is a ledger-vs-truth anomaly (the write did not land where
                // the recalc intended). Reuses afterScience; no second read.
                if (LedgerTrace.IsEnabled)
                {
                    double tol = LedgerTrace.ResourceTolerance("science");
                    if (LedgerTrace.IsResourceDrift(targetScience, afterScience, tol))
                        LedgerTrace.EmitAnomaly("science", "pool", "ledger-vs-truth",
                            $"target={LedgerTrace.FormatDouble(targetScience, "F3")} " +
                            $"actual={LedgerTrace.FormatDouble(afterScience, "F3")} " +
                            $"delta={LedgerTrace.FormatDouble(targetScience - afterScience, "F3")} " +
                            $"tol={LedgerTrace.FormatDouble(tol, "R")}");
                }
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
            return BuildTargetTechIdsForPatch(
                baselines,
                actions,
                utCutoff,
                baselineTechExclusions: null);
        }

        /// <summary>
        /// Builds the authoritative researched-tech set for a recalculation patch,
        /// optionally excluding tech ids from the selected baseline before replaying
        /// surviving ledger-backed unlocks. The exclusion hook is used after merge
        /// tombstones: a commit-time baseline may already include a now-retired old
        /// branch unlock, but a surviving ScienceSpending row for the same node must
        /// still be able to add it back.
        /// </summary>
        internal static HashSet<string> BuildTargetTechIdsForPatch(
            IReadOnlyList<GameStateBaseline> baselines,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            IReadOnlyCollection<string> baselineTechExclusions)
        {
            var selectedBaseline = SelectTechBaselineForPatch(baselines, utCutoff);
            if (selectedBaseline == null ||
                selectedBaseline.researchedTechIds == null ||
                selectedBaseline.researchedTechIds.Count == 0)
            {
                return null;
            }

            var target = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> excludedBaselineTech = null;
            if (baselineTechExclusions != null && baselineTechExclusions.Count > 0)
                excludedBaselineTech = new HashSet<string>(baselineTechExclusions, StringComparer.Ordinal);

            for (int i = 0; i < selectedBaseline.researchedTechIds.Count; i++)
            {
                string techId = selectedBaseline.researchedTechIds[i];
                if (!string.IsNullOrEmpty(techId) &&
                    (excludedBaselineTech == null || !excludedBaselineTech.Contains(techId)))
                {
                    target.Add(techId);
                }
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
                VerboseStablePatchState("patch-skip|tech-tree|target", "target-null",
                    "PatchTechTree: no target tech set supplied — skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                VerboseStablePatchState("patch-skip|tech-tree|rnd", "rnd-null",
                    "PatchTechTree: ResearchAndDevelopment.Instance is null — skipping");
                return;
            }

            if (AssetBase.RnDTechTree == null || AssetBase.RnDTechTree.GetTreeTechs() == null)
            {
                VerboseStablePatchState("patch-skip|tech-tree|tree", "tree-unavailable",
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
            // Bypass-rehydrate stats accumulated across the tech-tree loop and emitted
            // as ONE summary after it (the tree has ~80+ nodes; a per-node line here was
            // a per-item-in-loop log over a large collection on every recalc).
            int bypassRehydratedNodes = 0, bypassTotalPartsAdded = 0, bypassFallbackNodes = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> missingTargetIds = null;
            var madeUnavailableIds = new List<string>();
            var madeAvailableIds = new List<string>();

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
                    {
                        madeAvailable++;
                        madeAvailableIds.Add(techId);
                    }
                    else
                        alreadyAvailable++;

                    proto = EnsureAvailableProtoTechNode(
                        tech, proto, out int bypassPartsAdded, out bool bypassFallbackUsed);
                    if (bypassPartsAdded >= 0)
                    {
                        bypassRehydratedNodes++;
                        bypassTotalPartsAdded += bypassPartsAdded;
                        if (bypassFallbackUsed) bypassFallbackNodes++;
                    }
                    ResearchAndDevelopment.Instance.SetTechState(techId, proto);
                    tech.state = RDTech.State.Available;
                }
                else
                {
                    if (currentState == RDTech.State.Available || tech.state == RDTech.State.Available)
                    {
                        madeUnavailable++;
                        madeUnavailableIds.Add(techId);
                    }
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
            // madeUnavailable identities lead (the dangerous direction: a wrongly
            // re-locked researched node), followed by madeAvailable identities.
            string madeUnavailableSample =
                ComposeBoundedIdentitySample(madeUnavailableIds, IdentitySampleCap);
            string madeAvailableSample =
                ComposeBoundedIdentitySample(madeAvailableIds, IdentitySampleCap);
            ParsekLog.Info(Tag,
                $"PatchTechTree: available={target.Count.ToString(IC)}, " +
                $"madeAvailable={madeAvailable.ToString(IC)}, " +
                $"madeUnavailable={madeUnavailable.ToString(IC)}, " +
                $"alreadyAvailable={alreadyAvailable.ToString(IC)}, " +
                $"alreadyUnavailable={alreadyUnavailable.ToString(IC)}, " +
                $"missingTargets={missingTargets.ToString(IC)}, " +
                $"utCutoff={cutoffLabel}, baselineUt={baselineLabel}" +
                (madeUnavailableSample.Length > 0
                    ? $", madeUnavailableIds=[{madeUnavailableSample}]"
                    : string.Empty) +
                (madeAvailableSample.Length > 0
                    ? $", madeAvailableIds=[{madeAvailableSample}]"
                    : string.Empty));

            if (madeUnavailableIds.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchTechTree: all {madeUnavailableIds.Count.ToString(IC)} made-unavailable tech id(s): " +
                    string.Join(", ", madeUnavailableIds));

            if (madeAvailableIds.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchTechTree: all {madeAvailableIds.Count.ToString(IC)} made-available tech id(s): " +
                    string.Join(", ", madeAvailableIds));

            // LedgerTrace Tier-B (per-node change lines, reusing the #1098 changed-sets)
            // + Tier-C (read-back presence reconcile). The read-back re-queries
            // ResearchAndDevelopment.GetTechnologyState per changed node, so it is
            // guarded by IsEnabled — the extra KSP calls only run when tracing.
            if (LedgerTrace.IsEnabled)
            {
                foreach (string techId in madeUnavailableIds)
                {
                    LedgerTrace.EmitOnChange("tech-node", techId, "->unavailable");
                    bool actualAvailable =
                        ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available;
                    // Intended: NOT in the target set -> should be unavailable.
                    if (LedgerTrace.IsTechNodePresenceMismatch(false, actualAvailable))
                        LedgerTrace.EmitAnomaly("tech-node", techId, "ledger-vs-truth",
                            "intendedAvailable=false actualAvailable=" + LedgerTrace.Bool(actualAvailable));
                }
                foreach (string techId in madeAvailableIds)
                {
                    LedgerTrace.EmitOnChange("tech-node", techId, "->available");
                    bool actualAvailable =
                        ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available;
                    // Intended: in the target set -> should be available.
                    if (LedgerTrace.IsTechNodePresenceMismatch(true, actualAvailable))
                        LedgerTrace.EmitAnomaly("tech-node", techId, "ledger-vs-truth",
                            "intendedAvailable=true actualAvailable=" + LedgerTrace.Bool(actualAvailable));
                }
            }

            if (bypassRehydratedNodes > 0)
                ParsekLog.Verbose(Tag,
                    $"EnsureAvailableProtoTechNode: bypass rehydrated {bypassRehydratedNodes.ToString(IC)} tech node(s), " +
                    $"{bypassTotalPartsAdded.ToString(IC)} part(s) added, {bypassFallbackNodes.ToString(IC)} via fallback");

            if (missingTargets > 0 && missingTargetIds != null)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchTechTree: first {missingTargetIds.Count.ToString(IC)} missing tech ids (of {missingTargets.ToString(IC)}): " +
                    string.Join(", ", missingTargetIds));
            }
        }

        // Reports bypass-rehydrate stats to the caller for a batched summary instead of
        // logging per node: <paramref name="bypassPartsAdded"/> is the net parts added by
        // the rehydrate (-1 when bypass is inactive, so the caller skips this node), and
        // <paramref name="bypassFallbackUsed"/> flags the AddPurchasedParts fallback path.
        private static ProtoTechNode EnsureAvailableProtoTechNode(
            ProtoTechNode tech, ProtoTechNode existing,
            out int bypassPartsAdded, out bool bypassFallbackUsed)
        {
            bypassPartsAdded = -1;
            bypassFallbackUsed = false;
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
                {
                    AddPurchasedParts(proto, tech.partsPurchased);
                    bypassFallbackUsed = true;
                }
                bypassPartsAdded = proto.partsPurchased.Count - beforeCount;
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
        /// Keeps a recent stock science credit (VesselRecovery / ScienceTransmission)
        /// authoritative while the matching <c>ScienceEarning</c> ledger action is still
        /// catching up. Without this, a recalc triggered by a co-incident event (e.g. the
        /// first science milestone) firing between KSP crediting the science and the ledger
        /// ingesting it would transiently claw the pool back down to the stale pre-credit
        /// total. Mirror of <see cref="AdjustSciencePatchTargetForPendingRecentTechResearch"/>
        /// for the opposite (ledger-behind-KSP) direction.
        /// </summary>
        internal static double AdjustSciencePatchTargetForPendingRecentScienceEarning(
            double targetScience,
            float currentScience)
        {
            if (targetScience >= (double)currentScience)
                return targetScience;

            double pendingCredit = LedgerOrchestrator.GetPendingRecentKscScienceCredit();
            if (pendingCredit <= 0.0)
                return targetScience;

            double adjustedTarget = targetScience + pendingCredit;
            if (adjustedTarget > (double)currentScience)
                adjustedTarget = (double)currentScience;

            if (adjustedTarget > targetScience)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchScience: holding forward {pendingCredit.ToString("F1", IC)} pending science earning " +
                    $"(current={currentScience.ToString("F1", IC)}, " +
                    $"ledgerTarget={targetScience.ToString("F1", IC)}, " +
                    $"adjustedTarget={adjustedTarget.ToString("F1", IC)})");
            }

            return adjustedTarget;
        }

        /// <summary>
        /// The pending-adjusted realized running science balance — the exact value
        /// <see cref="PatchScience"/> uses as its drawdown-guard discriminator (the raw
        /// <c>GetRunningScience()</c> folded with the two in-flight KSC pending
        /// adjusters). Extracted so the rewind read-back guard reads a byte-identical
        /// value: comparing the recalc target against anything but the same realized
        /// running quantity would diverge by a transient ledger-catch-up credit / debit.
        /// </summary>
        internal static double ComputePendingAdjustedRunningScience(ScienceModule science)
        {
            return science.GetRunningScience()
                + LedgerOrchestrator.GetPendingRecentKscScienceCredit()
                - LedgerOrchestrator.GetPendingRecentKscTechResearchScienceDebit();
        }

        /// <summary>
        /// Patches KSP's fund balance to match the module's available funds.
        /// Uses AddFunds with a computed delta to reach the target value.
        /// No-op if Funding.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchFunds(
            FundsModule funds,
            bool suppressSuspiciousDrawdownWarnings = false,
            bool authoritativeReduction = false)
        {
            if (funds == null)
            {
                ParsekLog.Warn(Tag, "PatchFunds: null FundsModule — skipping");
                return;
            }

            if (Funding.Instance == null)
            {
                VerboseStablePatchState("patch-skip|funds|funding", "funding-null",
                    "PatchFunds: Funding.Instance is null (sandbox mode) — skipping");
                return;
            }

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
            if (!funds.HasSeed)
            {
                VerboseStablePatchState("patch-skip|funds|seed", "missing-seed",
                    "PatchFunds: module has no FundsInitial seed — skipping to preserve KSP values");
                return;
            }

            double currentFunds = Funding.Instance.Funds;
            double targetFunds = funds.GetAvailableFunds();

            // "Keep what you earned" guard (plan §3.3 / §4.2): clamp keyed on the
            // NON-RESERVED running balance, not the reservation-aware available target.
            // A reservation lowers only GetAvailableFunds() while leaving
            // GetRunningBalance() intact, so it never trips this; a missing-earning leak
            // lowers the running balance itself, so it does.
            double runningFunds = funds.GetRunningBalance();
            double effTargetFunds = ApplyDrawdownGuard(
                targetFunds, runningFunds, currentFunds, 0.01,
                authoritativeReduction, "funds", out bool fundsClamped,
                out ClampDirection fundsDirection);
            if (fundsClamped)
            {
                bool fundsDown = fundsDirection == ClampDirection.Down;
                ref bool fundsLatch = ref (fundsDown
                    ? ref fundsUpliftClampToastShownThisSession
                    : ref fundsClampToastShownThisSession);
                EmitDrawdownGuardClamp(
                    "Funds", runningFunds, currentFunds,
                    wouldBeTarget: targetFunds, clampedTo: effTargetFunds,
                    toastText: fundsDown ? "Held your funds at the spent value" : "Kept your earned funds",
                    sessionToastLatch: ref fundsLatch,
                    perSubjectScienceNote: false,
                    direction: fundsDirection);
                targetFunds = effTargetFunds;
            }

            double delta = targetFunds - currentFunds;

            if (Math.Abs(delta) < 0.01)
            {
                VerboseStablePatchState("patch-noop|funds", targetFunds.ToString("F1", IC),
                    $"PatchFunds: no change needed (current={currentFunds.ToString("F1", IC)}, " +
                    $"target={targetFunds.ToString("F1", IC)})");
                return;
            }

            // #402: defensive WARN when a single recalculation drops more than 10% of
            // the live funds pool. Legitimate walks can subtract large amounts after a
            // revert, so this is log-only (never aborts the patch) — but a >10% drop
            // alongside a small pool (>1000F) is the shape of missing-earnings bugs.
            if (!suppressSuspiciousDrawdownWarnings && IsSuspiciousDrawdown(delta, currentFunds))
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

            // LedgerTrace Tier-C: reconcile the computed target against the live
            // read-back we just took (afterFunds). Reuses afterFunds; no second read.
            if (LedgerTrace.IsEnabled)
            {
                double tol = LedgerTrace.ResourceTolerance("funds");
                if (LedgerTrace.IsResourceDrift(targetFunds, afterFunds, tol))
                    LedgerTrace.EmitAnomaly("funds", "pool", "ledger-vs-truth",
                        $"target={LedgerTrace.FormatDouble(targetFunds, "F3")} " +
                        $"actual={LedgerTrace.FormatDouble(afterFunds, "F3")} " +
                        $"delta={LedgerTrace.FormatDouble(targetFunds - afterFunds, "F3")} " +
                        $"tol={LedgerTrace.FormatDouble(tol, "R")}");
            }
        }

        /// <summary>
        /// Patches KSP's reputation to match the module's running reputation.
        /// Uses SetReputation (NOT AddReputation) to avoid double curve application —
        /// the ReputationModule already applied the curve during the walk.
        /// No-op if Reputation.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchReputation(
            ReputationModule reputation,
            bool authoritativeReduction = false)
        {
            if (reputation == null)
            {
                ParsekLog.Warn(Tag, "PatchReputation: null ReputationModule — skipping");
                return;
            }

            if (Reputation.Instance == null)
            {
                VerboseStablePatchState("patch-skip|reputation|singleton", "reputation-null",
                    "PatchReputation: Reputation.Instance is null (sandbox mode) — skipping");
                return;
            }

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
            if (!reputation.HasSeed)
            {
                VerboseStablePatchState("patch-skip|reputation|seed", "missing-seed",
                    "PatchReputation: module has no ReputationInitial seed — skipping to preserve KSP values");
                return;
            }

            float currentRep = Reputation.Instance.reputation;
            float targetRep = reputation.GetRunningRep();

            // "Keep what you earned" guard (plan §3.4 / §6): reputation has NO reservation
            // system, so its running balance IS the patch target. The discriminator is the
            // plain target-vs-live check, and "downward" is by magnitude (< live - eps) so
            // negative rep is handled. Express the clamp against the SetReputation
            // semantics: "do not set below live".
            double effTargetRep = ApplyDrawdownGuard(
                targetRep, targetRep, currentRep, 0.01,
                authoritativeReduction, "reputation", out bool repClamped,
                out ClampDirection repDirection);
            if (repClamped)
            {
                bool repDown = repDirection == ClampDirection.Down;
                ref bool repLatch = ref (repDown
                    ? ref reputationUpliftClampToastShownThisSession
                    : ref reputationClampToastShownThisSession);
                EmitDrawdownGuardClamp(
                    "Reputation", targetRep, currentRep,
                    wouldBeTarget: targetRep, clampedTo: effTargetRep,
                    toastText: repDown ? "Held your reputation at the current value" : "Kept your earned reputation",
                    sessionToastLatch: ref repLatch,
                    perSubjectScienceNote: false,
                    direction: repDirection);
                targetRep = (float)effTargetRep;
            }

            if (Math.Abs(targetRep - currentRep) < 0.01f)
            {
                VerboseStablePatchState("patch-noop|reputation", targetRep.ToString("F2", IC),
                    $"PatchReputation: no change needed (current={currentRep.ToString("F2", IC)}, " +
                    $"target={targetRep.ToString("F2", IC)})");
                return;
            }

            Reputation.Instance.SetReputation(targetRep, TransactionReasons.None);

            float afterRep = Reputation.Instance.reputation;
            ParsekLog.Info(Tag,
                $"PatchReputation: {currentRep.ToString("F2", IC)} -> {afterRep.ToString("F2", IC)} " +
                $"(target={targetRep.ToString("F2", IC)})");

            // LedgerTrace Tier-C: reconcile the computed target against the live
            // read-back we just took (afterRep). Reuses afterRep; no second read.
            if (LedgerTrace.IsEnabled)
            {
                double tol = LedgerTrace.ResourceTolerance("reputation");
                if (LedgerTrace.IsResourceDrift(targetRep, afterRep, tol))
                    LedgerTrace.EmitAnomaly("reputation", "pool", "ledger-vs-truth",
                        $"target={LedgerTrace.FormatDouble(targetRep, "F3")} " +
                        $"actual={LedgerTrace.FormatDouble(afterRep, "F3")} " +
                        $"delta={LedgerTrace.FormatDouble(targetRep - afterRep, "F3")} " +
                        $"tol={LedgerTrace.FormatDouble(tol, "R")}");
            }
        }

        /// <summary>
        /// Wrapper preserved for existing KspStatePatcher callers.
        /// Facility patch behavior lives in <see cref="FacilityStatePatcher.PatchFacilities"/>.
        /// </summary>
        internal static void PatchFacilities(FacilitiesModule facilities)
        {
            FacilityStatePatcher.PatchFacilities(facilities);
        }

        /// <summary>
        /// Wrapper preserved for existing KspStatePatcher callers.
        /// Destruction patch behavior lives in <see cref="FacilityStatePatcher.PatchDestructionState"/>.
        /// </summary>
        internal static void PatchDestructionState(
            System.Collections.Generic.IReadOnlyDictionary<string, FacilitiesModule.FacilityState> allFacilities)
        {
            FacilityStatePatcher.PatchDestructionState(allFacilities);
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
                VerboseStablePatchState("patch-skip|subject-science|rnd", "rnd-null",
                    "PatchPerSubjectScience: ResearchAndDevelopment.Instance is null — skipping");
                return;
            }

            var subjects = science.GetAllSubjects();
            var subjectIds = BuildSubjectIdsForPatch(
                subjects,
                GameStateStore.GetCommittedScienceSubjectIds());
            int patchedSubjects = 0;
            int skippedSubjects = 0;
            int notFoundSubjects = 0;
            int clearedSubjects = 0;
            var changedSubjects = new List<string>();

            foreach (var subjectId in subjectIds)
            {
                var kspSubject = ResearchAndDevelopment.GetSubjectByID(subjectId);
                if (kspSubject == null)
                {
                    notFoundSubjects++;
                    continue;
                }

                ScienceModule.SubjectState state;
                bool hasCurrentState = subjects.TryGetValue(subjectId, out state);
                float targetScience = hasCurrentState ? (float)state.CreditedTotal : 0f;
                if (Math.Abs(kspSubject.science - targetScience) > 0.001f)
                {
                    // Read the live value BEFORE the write so the log captures old->new.
                    float oldScience = kspSubject.science;
                    kspSubject.science = targetScience;
                    if (kspSubject.scienceCap > 0f)
                        kspSubject.scientificValue = 1f - (targetScience / kspSubject.scienceCap);
                    else
                        kspSubject.scientificValue = 0f;
                    if (kspSubject.scientificValue < 0f) kspSubject.scientificValue = 0f;
                    patchedSubjects++;
                    if (!hasCurrentState)
                        clearedSubjects++;
                    changedSubjects.Add(
                        $"{subjectId}:{oldScience.ToString("R", IC)}->{targetScience.ToString("R", IC)}");

                    // LedgerTrace Tier-B (per-subject change line) + Tier-C (read-back
                    // reconcile of the UNCLAMPED gap-1 per-subject write).
                    if (LedgerTrace.IsEnabled)
                    {
                        LedgerTrace.EmitOnChange("subject-science", subjectId,
                            $"{oldScience.ToString("R", IC)}->{targetScience.ToString("R", IC)}");

                        double subTol = LedgerTrace.ResourceTolerance("subject-science");
                        if (LedgerTrace.IsSubjectScienceDrift(targetScience, kspSubject.science, subTol))
                            LedgerTrace.EmitAnomaly("subject-science", subjectId, "ledger-vs-truth",
                                $"target={LedgerTrace.FormatDouble(targetScience, "F3")} " +
                                $"actual={LedgerTrace.FormatDouble(kspSubject.science, "F3")} " +
                                $"delta={LedgerTrace.FormatDouble(targetScience - kspSubject.science, "F3")} " +
                                $"tol={LedgerTrace.FormatDouble(subTol, "R")}");
                    }
                }
                else
                {
                    skippedSubjects++;
                }
            }

            string changedSubjectsSample =
                ComposeBoundedIdentitySample(changedSubjects, IdentitySampleCap);
            ParsekLog.Info(Tag,
                $"PatchPerSubjectScience: patched={patchedSubjects.ToString(IC)}, " +
                $"cleared={clearedSubjects.ToString(IC)}, " +
                $"skipped={skippedSubjects.ToString(IC)}, " +
                $"notFound={notFoundSubjects.ToString(IC)}, " +
                $"totalSubjects={subjectIds.Count}" +
                (changedSubjectsSample.Length > 0
                    ? $", changedSubjects=[{changedSubjectsSample}]"
                    : string.Empty));

            if (changedSubjects.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchPerSubjectScience: all {changedSubjects.Count.ToString(IC)} changed subject(s): " +
                    string.Join(", ", changedSubjects));
        }

        internal static HashSet<string> BuildSubjectIdsForPatch(
            IReadOnlyDictionary<string, ScienceModule.SubjectState> currentSubjects,
            IEnumerable<string> previouslyCommittedSubjectIds)
        {
            var subjectIds = new HashSet<string>(StringComparer.Ordinal);
            if (currentSubjects != null)
            {
                foreach (var kvp in currentSubjects)
                {
                    if (!string.IsNullOrEmpty(kvp.Key))
                        subjectIds.Add(kvp.Key);
                }
            }
            if (previouslyCommittedSubjectIds != null)
            {
                foreach (var subjectId in previouslyCommittedSubjectIds)
                {
                    if (!string.IsNullOrEmpty(subjectId))
                        subjectIds.Add(subjectId);
                }
            }
            return subjectIds;
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
                VerboseStablePatchState("patch-skip|milestones|progress-tracking", "progress-null",
                    "PatchMilestones: ProgressTracking.Instance is null — skipping");
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

            // LedgerTrace Tier-B coverage for milestones (gap 6) is intentionally
            // DEFERRED from v1: the Prompt-4 Tier-B enumeration is "subject / node /
            // facility / contract id" and omits milestones, and there is no #1098
            // changed-set for the per-node reached/complete flips below. Add a
            // milestone changed-set (and a presence read-back) when Tier-B is extended
            // to milestones; until then milestone changes are not traced.
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
            var terminalIds = contracts.GetTerminalContractIds();
            var terminalIdSet = new HashSet<Guid>();
            foreach (var idStr in terminalIds)
            {
                if (Guid.TryParse(idStr, out var gid))
                    terminalIdSet.Add(gid);
            }
            var terminalOutcomeById = BuildTerminalContractOutcomeMap(contracts);

            // If no active contracts in ledger, just log the current KSP state
            int currentCount = ContractSystem.Instance.Contracts != null
                ? ContractSystem.Instance.Contracts.Count
                : 0;
            int finishedCount = ContractSystem.Instance.ContractsFinished != null
                ? ContractSystem.Instance.ContractsFinished.Count
                : 0;

            ParsekLog.Verbose(Tag,
                $"PatchContracts: ledger has {activeIds.Count.ToString(IC)} active contracts, " +
                $"{terminalIds.Count.ToString(IC)} terminal contracts, " +
                $"KSP has {currentCount.ToString(IC)} current contracts, " +
                $"{finishedCount.ToString(IC)} finished contracts");

            // #404/#756: Filtered remove. Active current contracts whose id is NOT
            // in the ledger's active set are stale and can be removed. Offered and
            // Declined entries still stay untouched. Terminal stock rows in either
            // current contracts or ContractsFinished are removable only for explicit
            // tombstoned Parsek contract action ids when their terminal ELS row no
            // longer survives.
            // Filtering is delegated to PartitionContractsForPatch for testability.
            var currentContracts = ContractSystem.Instance.Contracts;
            var finishedContracts = ContractSystem.Instance.ContractsFinished;
            var currentEntries = BuildContractFilterEntries(currentContracts);
            var finishedEntries = BuildContractFilterEntries(finishedContracts);
            var tombstonedContractGuids =
                LedgerOrchestrator.BuildTombstonedContractGuidsForPatch();

            PartitionContractsForPatch(currentEntries, finishedEntries,
                activeIdSet, terminalIdSet, terminalOutcomeById, tombstonedContractGuids,
                out var toRemoveCurrent, out var toRemoveFinished,
                out var survivingActiveIds,
                out var survivingTerminalIds);
            var toRemoveCurrentKeys = BuildCurrentContractRemovalKeys(
                currentEntries,
                activeIdSet,
                terminalIdSet,
                terminalOutcomeById,
                tombstonedContractGuids);
            var toRemoveFinishedKeys = BuildFinishedContractRemovalKeys(
                finishedEntries,
                terminalIdSet,
                terminalOutcomeById,
                tombstonedContractGuids);

            int removedStale = 0;
            int removedFinishedTombstoned = 0;
            int unregisterFailures = 0;
            removedStale = RemoveContractsByKey(
                currentContracts,
                toRemoveCurrentKeys,
                unregisterBeforeRemove: true,
                ref unregisterFailures);
            removedFinishedTombstoned = RemoveContractsByKey(
                finishedContracts,
                toRemoveFinishedKeys,
                unregisterBeforeRemove: false,
                ref unregisterFailures);

            ParsekLog.Verbose(Tag,
                $"PatchContracts: removed {removedStale.ToString(IC)} stale Active/terminal contract(s), " +
                $"{removedFinishedTombstoned.ToString(IC)} tombstoned finished terminal contract(s), " +
                $"{survivingActiveIds.Count.ToString(IC)} Active contract(s) preserved in place, " +
                $"{survivingTerminalIds.Count.ToString(IC)} terminal contract(s) preserved in place " +
                $"(unregisterFailures={unregisterFailures.ToString(IC)})");

            // These are the TARGETED removal keys (pure set): current keys first, then
            // finished keys. The count can exceed the actually-removed count if a targeted
            // live contract was already absent from the KSP list.
            var removedIds = DescribeRemovedContractIdentities(
                toRemoveCurrentKeys, toRemoveFinishedKeys);

            // 2. Rebuild ONLY contracts that are in the ledger but not already present.
            int restored = 0;
            int skippedExisting = 0;
            int noSnapshot = 0;
            int noType = 0;
            int typeNotFound = 0;
            int loadFailed = 0;
            int registered = 0;
            var restoredIds = new List<string>();

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
                        restoredIds.Add(contractId);
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
            int kspFinishedAfter = finishedContracts != null ? finishedContracts.Count : 0;
            string removedSample =
                ComposeBoundedIdentitySample(removedIds, IdentitySampleCap);
            string restoredSample =
                ComposeBoundedIdentitySample(restoredIds, IdentitySampleCap);
            ParsekLog.Info(Tag,
                $"PatchContracts: removedStale={removedStale.ToString(IC)}, " +
                $"removedFinishedTombstoned={removedFinishedTombstoned.ToString(IC)}, " +
                $"restored={restored.ToString(IC)}, " +
                $"registered={registered.ToString(IC)}, " +
                $"skippedExisting={skippedExisting.ToString(IC)}, " +
                $"noSnapshot={noSnapshot.ToString(IC)}, " +
                $"noType={noType.ToString(IC)}, " +
                $"typeNotFound={typeNotFound.ToString(IC)}, " +
                $"loadFailed={loadFailed.ToString(IC)}, " +
                $"ledgerActive={activeIds.Count.ToString(IC)}, " +
                $"ledgerTerminal={terminalIds.Count.ToString(IC)}, " +
                $"kspTotal={kspTotalAfter.ToString(IC)}, " +
                $"kspFinished={kspFinishedAfter.ToString(IC)} " +
                $"(Offered and unrelated finished history preserved; tombstoned terminal filtered)" +
                (removedSample.Length > 0
                    ? $", removedIds=[{removedSample}]"
                    : string.Empty) +
                (restoredSample.Length > 0
                    ? $", restoredIds=[{restoredSample}]"
                    : string.Empty));

            if (removedIds.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchContracts: all {removedIds.Count.ToString(IC)} targeted removed contract id(s): " +
                    string.Join(", ", removedIds));

            if (restoredIds.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchContracts: all {restoredIds.Count.ToString(IC)} restored contract id(s): " +
                    string.Join(", ", restoredIds));

            // LedgerTrace Tier-B (per-contract change lines, reusing the #1098
            // removed/restored sets) + Tier-C (presence read-back reconcile). The
            // read-back builds a set of live-present contract guids from the current
            // contracts list, so it is guarded by IsEnabled — the extra work only runs
            // when tracing. Intended: a restored id must be present, a removed id absent.
            if (LedgerTrace.IsEnabled)
            {
                // Coverage boundary (intentional, not a bug): livePresentGuids is built
                // from the ACTIVE contract bucket (currentContracts) only. removedIds can
                // include finished-bucket removals, whose absence from this active set
                // would read as "absent" — which matches the intended-removed expectation,
                // so this never produces a false positive. Reconciling the finished bucket
                // is out of v1 scope; this read-back reconciles the active bucket only.
                var livePresentGuids = new HashSet<string>(StringComparer.Ordinal);
                if (currentContracts != null)
                {
                    foreach (var c in currentContracts)
                    {
                        if (c != null)
                            livePresentGuids.Add(c.ContractGuid.ToString());
                    }
                }

                foreach (string contractId in restoredIds)
                {
                    LedgerTrace.EmitOnChange("contract", contractId, "restored");
                    bool present = livePresentGuids.Contains(contractId);
                    if (LedgerTrace.IsContractPresenceMismatch(true, present))
                        LedgerTrace.EmitAnomaly("contract", contractId, "ledger-vs-truth",
                            "intendedPresent=true actualPresent=" + LedgerTrace.Bool(present));
                }
                foreach (string contractId in removedIds)
                {
                    LedgerTrace.EmitOnChange("contract", contractId, "removed");
                    bool present = livePresentGuids.Contains(contractId);
                    if (LedgerTrace.IsContractPresenceMismatch(false, present))
                        LedgerTrace.EmitAnomaly("contract", contractId, "ledger-vs-truth",
                            "intendedPresent=false actualPresent=" + LedgerTrace.Bool(present));
                }
            }
        }

        // ================================================================
        // Testable pure helpers for #404 filtered contract partitioning
        // ================================================================

        /// <summary>
        /// Represents the current-state classification of a KSP contract for PatchContracts
        /// filtering. Active contracts are matched against ledger Active ids; terminal
        /// contracts are matched against ledger terminal ids; offered/declined/unknown
        /// non-terminal state is preserved untouched.
        /// </summary>
        internal struct ContractFilterEntry
        {
            public Guid Id;
            public bool IsActive;
            public bool IsTerminal;
            public Contract.State State;
        }

        internal struct ContractRemovalKey
        {
            public Guid Id;
            public Contract.State State;
        }

        /// <summary>
        /// Pure helper: partitions the current KSP contract list into {stale Active
        /// contracts plus explicitly tombstoned terminal contracts to remove} and
        /// {surviving ledger-backed contracts to keep in place}.
        /// Non-terminal non-Active entries are implicitly preserved by being absent from the
        /// remove list. Extracted from <see cref="PatchContracts"/> so the filtering logic can
        /// be unit-tested without real KSP Contract instances.
        ///
        /// The rules:
        /// <list type="number">
        ///   <item>An Active contract whose ID is NOT in the ledger's active set is "stale" and goes to <paramref name="toRemove"/>.</item>
        ///   <item>An Active contract whose ID IS in the ledger's active set is "surviving" and goes to <paramref name="surviving"/>.</item>
        ///   <item>A terminal contract whose ID IS in the ledger's terminal set is "surviving".</item>
        ///   <item>A terminal contract whose ID is NOT in the ledger's terminal set is removed only when the caller supplies it in the removable tombstoned set.</item>
        ///   <item>A non-terminal non-Active contract is left out of both lists — callers MUST NOT touch it.</item>
        /// </list>
        /// </summary>
        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> surviving)
        {
            PartitionContractsForPatch(
                currentEntries,
                activeIdSet,
                new HashSet<Guid>(),
                null,
                null,
                out toRemove,
                out surviving,
                out _);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, activeIdSet, terminalIdSet,
                null, null,
                out toRemove, out survivingActive, out survivingTerminal);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            HashSet<Guid> removableTerminalIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, activeIdSet, terminalIdSet,
                null, removableTerminalIdSet,
                out toRemove, out survivingActive, out survivingTerminal);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableTerminalIdSet,
            out List<Guid> toRemove,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            toRemove = new List<Guid>();
            survivingActive = new HashSet<Guid>();
            survivingTerminal = new HashSet<Guid>();
            if (currentEntries == null) return;

            for (int i = 0; i < currentEntries.Count; i++)
            {
                var entry = currentEntries[i];
                if (entry.IsActive)
                {
                    if (activeIdSet != null && activeIdSet.Contains(entry.Id))
                        survivingActive.Add(entry.Id);
                    else
                        toRemove.Add(entry.Id);
                    continue;
                }

                if (entry.IsTerminal)
                {
                    if (IsSurvivingTerminalEntry(
                            entry, terminalIdSet, terminalOutcomeById))
                    {
                        survivingTerminal.Add(entry.Id);
                    }
                    else if (removableTerminalIdSet != null &&
                             removableTerminalIdSet.Contains(entry.Id))
                    {
                        toRemove.Add(entry.Id);
                    }
                    continue;
                }

                // Offered/declined/unknown non-terminal stock state is preserved.
            }
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            HashSet<Guid> removableFinishedTerminalIdSet,
            out List<Guid> toRemoveCurrent,
            out List<Guid> toRemoveFinished,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, finishedEntries,
                activeIdSet, terminalIdSet, null, removableFinishedTerminalIdSet,
                out toRemoveCurrent, out toRemoveFinished,
                out survivingActive, out survivingTerminal);
        }

        internal static void PartitionContractsForPatch(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableFinishedTerminalIdSet,
            out List<Guid> toRemoveCurrent,
            out List<Guid> toRemoveFinished,
            out HashSet<Guid> survivingActive,
            out HashSet<Guid> survivingTerminal)
        {
            PartitionContractsForPatch(currentEntries, activeIdSet, terminalIdSet,
                terminalOutcomeById, removableFinishedTerminalIdSet,
                out toRemoveCurrent, out survivingActive, out survivingTerminal);

            PartitionFinishedContractsForPatch(finishedEntries, terminalIdSet,
                terminalOutcomeById, removableFinishedTerminalIdSet,
                out toRemoveFinished, out var survivingFinishedTerminal);

            foreach (var id in survivingFinishedTerminal)
                survivingTerminal.Add(id);
        }

        internal static void PartitionFinishedContractsForPatch(
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableFinishedTerminalIdSet,
            out List<Guid> toRemoveFinished,
            out HashSet<Guid> survivingTerminal)
        {
            toRemoveFinished = new List<Guid>();
            survivingTerminal = new HashSet<Guid>();
            if (finishedEntries == null) return;

            for (int i = 0; i < finishedEntries.Count; i++)
            {
                var entry = finishedEntries[i];
                if (!entry.IsTerminal)
                    continue;

                if (IsSurvivingTerminalEntry(
                        entry, terminalIdSet, terminalOutcomeById))
                {
                    survivingTerminal.Add(entry.Id);
                    continue;
                }

                if (removableFinishedTerminalIdSet != null &&
                    removableFinishedTerminalIdSet.Contains(entry.Id))
                {
                    toRemoveFinished.Add(entry.Id);
                }
            }
        }

        internal static HashSet<ContractRemovalKey> BuildCurrentContractRemovalKeys(
            IReadOnlyList<ContractFilterEntry> currentEntries,
            HashSet<Guid> activeIdSet,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableTerminalIdSet)
        {
            var keys = new HashSet<ContractRemovalKey>();
            if (currentEntries == null)
                return keys;

            for (int i = 0; i < currentEntries.Count; i++)
            {
                var entry = currentEntries[i];
                if (entry.IsActive)
                {
                    if (activeIdSet == null || !activeIdSet.Contains(entry.Id))
                        keys.Add(ToRemovalKey(entry));
                    continue;
                }

                if (entry.IsTerminal &&
                    !IsSurvivingTerminalEntry(entry, terminalIdSet, terminalOutcomeById) &&
                    removableTerminalIdSet != null &&
                    removableTerminalIdSet.Contains(entry.Id))
                {
                    keys.Add(ToRemovalKey(entry));
                }
            }

            return keys;
        }

        internal static HashSet<ContractRemovalKey> BuildFinishedContractRemovalKeys(
            IReadOnlyList<ContractFilterEntry> finishedEntries,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById,
            HashSet<Guid> removableFinishedTerminalIdSet)
        {
            var keys = new HashSet<ContractRemovalKey>();
            if (finishedEntries == null)
                return keys;

            for (int i = 0; i < finishedEntries.Count; i++)
            {
                var entry = finishedEntries[i];
                if (entry.IsTerminal &&
                    !IsSurvivingTerminalEntry(entry, terminalIdSet, terminalOutcomeById) &&
                    removableFinishedTerminalIdSet != null &&
                    removableFinishedTerminalIdSet.Contains(entry.Id))
                {
                    keys.Add(ToRemovalKey(entry));
                }
            }

            return keys;
        }

        private static ContractRemovalKey ToRemovalKey(ContractFilterEntry entry)
        {
            return new ContractRemovalKey
            {
                Id = entry.Id,
                State = entry.State
            };
        }

        /// <summary>
        /// Pure helper: composes a bounded, comma-joined sample of changed-identity
        /// descriptions for a default-level (Info) apply line. Returns string.Empty for a
        /// null/empty list so the caller appends NOTHING (steady-state Info lines stay
        /// byte-identical). Under the cap, the full list joins as-is; over the cap, the
        /// first <paramref name="cap"/> entries join followed by a " (+N more)" overflow
        /// marker. The full list is emitted separately at Verbose by each caller.
        /// </summary>
        internal static string ComposeBoundedIdentitySample(
            IReadOnlyList<string> changedDescriptions, int cap)
        {
            if (changedDescriptions == null || changedDescriptions.Count == 0)
                return string.Empty;

            int count = changedDescriptions.Count;
            if (count <= cap)
                return string.Join(", ", changedDescriptions);

            var head = new List<string>(cap);
            for (int i = 0; i < cap; i++)
                head.Add(changedDescriptions[i]);

            return string.Join(", ", head)
                + " (+" + (count - cap).ToString(IC) + " more)";
        }

        /// <summary>
        /// Pure helper: derives the contract identities targeted for removal by the
        /// per-state removal-key sets — current-set keys first, then finished-set keys.
        /// Headless-testable because both inputs come from pure key builders. The result
        /// reflects the TARGETED removal keys, which can exceed the count actually removed
        /// when a targeted contract was already absent from the live KSP list.
        /// </summary>
        internal static List<string> DescribeRemovedContractIdentities(
            HashSet<ContractRemovalKey> currentKeys,
            HashSet<ContractRemovalKey> finishedKeys)
        {
            var ids = new List<string>();
            if (currentKeys != null)
            {
                foreach (var key in currentKeys)
                    ids.Add(key.Id.ToString());
            }

            if (finishedKeys != null)
            {
                foreach (var key in finishedKeys)
                    ids.Add(key.Id.ToString());
            }

            return ids;
        }

        private static bool IsSurvivingTerminalEntry(
            ContractFilterEntry entry,
            HashSet<Guid> terminalIdSet,
            IReadOnlyDictionary<Guid, ContractTerminalOutcome> terminalOutcomeById)
        {
            if (terminalIdSet == null || !terminalIdSet.Contains(entry.Id))
                return false;

            if (terminalOutcomeById == null)
                return true;

            ContractTerminalOutcome terminalOutcome;
            return terminalOutcomeById.TryGetValue(entry.Id, out terminalOutcome)
                && IsTerminalContractStateCompatible(entry.State, terminalOutcome);
        }

        private static bool IsTerminalContractStateCompatible(
            Contract.State state,
            ContractTerminalOutcome terminalOutcome)
        {
            switch (terminalOutcome)
            {
                case ContractTerminalOutcome.Completed:
                    return state == Contract.State.Completed;
                case ContractTerminalOutcome.Failed:
                    return state == Contract.State.Failed;
                case ContractTerminalOutcome.DeadlineExpired:
                    return state == Contract.State.DeadlineExpired;
                case ContractTerminalOutcome.Cancelled:
                    return state == Contract.State.Cancelled;
                default:
                    return false;
            }
        }

        private static List<ContractFilterEntry> BuildContractFilterEntries(
            IReadOnlyList<Contract> contracts)
        {
            var entries = new List<ContractFilterEntry>();
            if (contracts == null)
                return entries;

            for (int i = 0; i < contracts.Count; i++)
            {
                var c = contracts[i];
                if (c == null) continue;
                entries.Add(new ContractFilterEntry
                {
                    Id = c.ContractGuid,
                    IsActive = c.ContractState == Contract.State.Active,
                    IsTerminal = IsTerminalContractState(c.ContractState),
                    State = c.ContractState
                });
            }

            return entries;
        }

        private static Dictionary<Guid, ContractTerminalOutcome> BuildTerminalContractOutcomeMap(
            ContractsModule contracts)
        {
            var result = new Dictionary<Guid, ContractTerminalOutcome>();
            if (contracts == null)
                return result;

            var source = contracts.GetTerminalContractOutcomes();
            if (source == null)
                return result;

            foreach (var kvp in source)
            {
                Guid contractGuid;
                if (Guid.TryParse(kvp.Key, out contractGuid))
                    result[contractGuid] = kvp.Value;
            }

            return result;
        }

        private static int RemoveContractsByKey(
            List<Contract> contracts,
            HashSet<ContractRemovalKey> toRemove,
            bool unregisterBeforeRemove,
            ref int unregisterFailures)
        {
            if (contracts == null || toRemove == null || toRemove.Count == 0)
                return 0;

            int removed = 0;
            for (int i = contracts.Count - 1; i >= 0; i--)
            {
                var c = contracts[i];
                if (c == null) continue;
                var key = new ContractRemovalKey
                {
                    Id = c.ContractGuid,
                    State = c.ContractState
                };
                if (!toRemove.Contains(key)) continue;

                if (unregisterBeforeRemove)
                {
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
                }

                contracts.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        private static bool IsTerminalContractState(Contract.State state)
        {
            switch (state)
            {
                case Contract.State.Completed:
                case Contract.State.Failed:
                case Contract.State.Cancelled:
                case Contract.State.DeadlineExpired:
                    return true;
                default:
                    return false;
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
        /// Clears the per-resource clamp-toast session latches. Called at a genuine
        /// new-session boundary (a fresh save load) so the first guarded clamp in a new
        /// session toasts again. NOT called on a plain scene change.
        /// </summary>
        internal static void ResetDrawdownGuardSessionLatches()
        {
            fundsClampToastShownThisSession = false;
            scienceClampToastShownThisSession = false;
            reputationClampToastShownThisSession = false;
            fundsUpliftClampToastShownThisSession = false;
            scienceUpliftClampToastShownThisSession = false;
            reputationUpliftClampToastShownThisSession = false;
        }

        /// <summary>
        /// Direction a guarded clamp moved the patch target (plan §2.2 step 2).
        /// <list type="bullet">
        /// <item><see cref="None"/>: no clamp fired.</item>
        /// <item><see cref="Up"/>: running BELOW live (a missing earning channel) -> target
        /// floored UP to live ("keep what you earned").</item>
        /// <item><see cref="Down"/>: running ABOVE live (a spend the ledger does not model,
        /// e.g. a facility upgrade — Bug 2) -> target capped DOWN to live ("hold at the
        /// spent value"), so a non-authoritative recalc cannot refund the spend.</item>
        /// </list>
        /// </summary>
        internal enum ClampDirection { None, Up, Down }

        /// <summary>
        /// Pure predicate (Blocker 2, plan §3.3): a drawdown is guardable iff the
        /// NON-RESERVED running balance is more than <paramref name="epsilon"/> below the
        /// current live value. Keyed on the running balance, NOT the reservation-aware
        /// patch target: a legitimate reservation lowers only the spendable target while
        /// leaving the running balance intact (>= live), so it does not trip the guard; a
        /// missing-earning-channel leak lowers the running balance itself, so it does.
        /// </summary>
        internal static bool IsGuardableDrawdown(
            double runningBalance, double currentLive, double epsilon)
        {
            return runningBalance < currentLive - epsilon;
        }

        /// <summary>
        /// Pure predicate (Bug 2, plan §2.1): an UPLIFT is guardable iff the running
        /// balance is more than <paramref name="epsilon"/> ABOVE the current live value.
        /// Exact mirror of <see cref="IsGuardableDrawdown"/> on the SAME running-balance
        /// discriminator, NOT the target: a legitimate reservation lowers only the
        /// spendable target while leaving the running balance at/above live, so it never
        /// trips this (running is within eps of live for a pure load); a real spend the
        /// ledger does not model (a facility upgrade, the Bug-2 signature) leaves the
        /// running balance ABOVE live, so it does. When this fires with no time-travel
        /// authority the target is capped DOWN to live so the patch cannot refund the spend.
        /// </summary>
        internal static bool IsGuardableUplift(
            double runningBalance, double currentLive, double epsilon)
        {
            return runningBalance > currentLive + epsilon;
        }

        /// <summary>
        /// Pure clamp decision (1-out overload preserved for the existing call sites /
        /// DrawdownGuardTests). Delegates to the 2-out <see cref="ApplyDrawdownGuard(double,
        /// double, double, double, bool, string, out bool, out ClampDirection)"/> overload
        /// and discards the direction.
        /// </summary>
        internal static double ApplyDrawdownGuard(
            double patchTarget, double runningBalance, double currentLive, double epsilon,
            bool authoritativeReduction, string resource, out bool clamped)
        {
            return ApplyDrawdownGuard(
                patchTarget, runningBalance, currentLive, epsilon,
                authoritativeReduction, resource, out clamped, out _);
        }

        /// <summary>
        /// Pure symmetric clamp decision (plan §2.2 step 2). When no time-travel context
        /// authorizes the change the guard keys on the RUNNING balance vs live in BOTH
        /// directions:
        /// <list type="bullet">
        /// <item>running BELOW live past epsilon -> floor the target UP to live (returns
        /// <c>max(patchTarget, currentLive)</c>), <paramref name="direction"/>=<see
        /// cref="ClampDirection.Up"/>. UP semantics are UNCHANGED from the original guard,
        /// including flagging <paramref name="clamped"/> even when <c>patchTarget &gt;
        /// live</c> (so the existing TargetAboveLive assertion still holds).</item>
        /// <item>running ABOVE live past epsilon -> cap the target DOWN to live (returns
        /// <c>min(patchTarget, currentLive)</c>), <paramref name="direction"/>=<see
        /// cref="ClampDirection.Down"/>. The DOWN branch flags <paramref name="clamped"/>
        /// ONLY when the clamp actually LOWERED the value (<c>min &lt; patchTarget</c>),
        /// so a target already at/below live (a partial reservation on top of a leak) does
        /// not emit a misleading WARN/toast (plan §2.4 last bullet).</item>
        /// <item>otherwise (running within epsilon of live in either direction, OR an
        /// authoritative signal is set) -> return <paramref name="patchTarget"/> unchanged,
        /// <paramref name="direction"/>=<see cref="ClampDirection.None"/>. Covers the
        /// legitimate reservation case (target below live while running is at live) and
        /// every authoritative time-travel restore (which moves funds freely either way).</item>
        /// </list>
        /// </summary>
        internal static double ApplyDrawdownGuard(
            double patchTarget, double runningBalance, double currentLive, double epsilon,
            bool authoritativeReduction, string resource, out bool clamped,
            out ClampDirection direction)
        {
            if (!authoritativeReduction)
            {
                if (IsGuardableDrawdown(runningBalance, currentLive, epsilon))
                {
                    clamped = true;
                    direction = ClampDirection.Up;
                    return patchTarget > currentLive ? patchTarget : currentLive;
                }

                if (IsGuardableUplift(runningBalance, currentLive, epsilon))
                {
                    // Cap DOWN to live. Flag clamped only when the value actually moved:
                    // a target already at/below live is not refunding the spend, so a
                    // no-op clamp must not emit a misleading WARN/toast (plan §2.4).
                    double capped = patchTarget < currentLive ? patchTarget : currentLive;
                    clamped = capped < patchTarget;
                    direction = clamped ? ClampDirection.Down : ClampDirection.None;
                    return capped;
                }
            }

            clamped = false;
            direction = ClampDirection.None;
            return patchTarget;
        }

        /// <summary>
        /// Emits the loud observability for a guarded clamp (plan §4.2 steps 2-3 / §2.2
        /// step 3): a WARN with the full numbers ALWAYS (every guarded recalc, documenting
        /// the ongoing leak), and one short session-latched native ScreenMessage naming the
        /// protected resource. The WARN distinguishes the two directions:
        /// <list type="bullet">
        /// <item><see cref="ClampDirection.Up"/> -> "GUARDED DRAWDOWN clamped" (running below
        /// live: a missing earning channel; target floored up to live).</item>
        /// <item><see cref="ClampDirection.Down"/> -> "GUARDED UPLIFT clamped" (running above
        /// live: a spend the ledger does not model, e.g. a facility upgrade; target capped
        /// down to live so the patch cannot refund it — Bug 2).</item>
        /// </list>
        /// <paramref name="perSubjectScienceNote"/> appends the documented per-subject
        /// divergence limitation to the WARN for the science pool (MINOR 5). Internal static
        /// so xUnit can drive the WARN + toast path directly (the live patch methods
        /// early-return on null KSP singletons).
        /// </summary>
        internal static void EmitDrawdownGuardClamp(
            string resource, double runningBalance, double currentLive,
            double wouldBeTarget, double clampedTo, string toastText,
            ref bool sessionToastLatch, bool perSubjectScienceNote,
            ClampDirection direction = ClampDirection.Up)
        {
            string label = direction == ClampDirection.Down
                ? "GUARDED UPLIFT clamped"
                : "GUARDED DRAWDOWN clamped";
            string tail = direction == ClampDirection.Down
                ? "(no time-travel context) - spent value held; ledger may be missing a spending channel"
                : "(no time-travel context) - earned value preserved; ledger may be missing an earning channel";
            string warn =
                $"Patch{resource}: {label} resource={resource} " +
                $"running={runningBalance.ToString("R", IC)} live={currentLive.ToString("R", IC)} " +
                $"wouldBeTarget={wouldBeTarget.ToString("R", IC)} clampedTo={clampedTo.ToString("R", IC)} " +
                tail;
            if (perSubjectScienceNote)
            {
                warn += " NOTE: per-subject credited science is patched UNCLAMPED, so the " +
                    "Science Archive may transiently disagree with the clamped pool until the leak is fixed";
            }
            ParsekLog.Warn(Tag, warn);

            if (!sessionToastLatch)
            {
                sessionToastLatch = true;
                ParsekLog.ScreenMessage(toastText, 2.5f);
            }
        }

        // ================================================================
        // Rewind read-back divergence guard (audit rec #1,
        // docs/dev/ledger-state-reconstruction-audit.md §8). Catches the case where a
        // Rewind-to-Separation recalc would write an economy BELOW the real career
        // economy — silent career corruption. Two-witness floor, downward-only,
        // realized-vs-realized. Default warn-only; abort is opt-in via
        // RewindReadbackGuard.AbortRewindPatchOnDivergence.
        // ================================================================

        /// <summary>
        /// Verdict from <see cref="ResolveRewindDivergence"/>.
        /// <list type="bullet">
        /// <item><see cref="NotEvaluated"/>: no finite witness for the resource — cannot
        /// build a floor, so no judgement.</item>
        /// <item><see cref="WithinExpectedRange"/>: the recalc target is at or above the
        /// floor (within tolerance) — the normal "career restores / sticks" direction.</item>
        /// <item><see cref="FlaggedDivergence"/>: the recalc target dropped below the floor
        /// past tolerance, OR the target itself is NaN / Infinity.</item>
        /// </list>
        /// </summary>
        internal enum RewindReadbackVerdict { NotEvaluated, WithinExpectedRange, FlaggedDivergence }

        // Absolute downward tolerances for the read-back floor comparison. Set a touch
        // looser than the per-resource patch no-op thresholds (funds 0.01, science 0.001,
        // rep 0.01) so float-rounding / curve-application noise between the realized
        // running value and the witnessed live value never trips a false divergence,
        // while still flagging any economically meaningful drop. Funds/science floors are
        // large pools so 1.0 / 0.1 are still tight; rep is small-magnitude so 0.1 stays
        // sensitive.
        internal const double RewindReadbackFundsTolerance = 1.0;
        internal const double RewindReadbackScienceTolerance = 0.1;
        internal const double RewindReadbackReputationTolerance = 0.1;

        /// <summary>
        /// Pure read-back divergence predicate. Builds a per-resource floor from the two
        /// economy witnesses (<paramref name="eBefore"/> = pre-rewind live career,
        /// <paramref name="eRp"/> = loaded quicksave / OLD at-RP economy) using only the
        /// FINITE witnesses, then judges the recalc's realized running target against it:
        /// <list type="bullet">
        /// <item>A NaN / Infinity target is itself corruption → <see cref="RewindReadbackVerdict.FlaggedDivergence"/>
        /// (floor / delta returned as NaN).</item>
        /// <item>No finite witness → cannot judge → <see cref="RewindReadbackVerdict.NotEvaluated"/>.</item>
        /// <item><paramref name="recalcRunningTarget"/> &lt; floor − tolerance → downward
        /// clobber → <see cref="RewindReadbackVerdict.FlaggedDivergence"/>; otherwise
        /// <see cref="RewindReadbackVerdict.WithinExpectedRange"/> (upward / restore never flags).</item>
        /// </list>
        /// The min-floor structurally encodes legit drawdown: post-RP spending lowers
        /// <paramref name="eBefore"/>, lowering the floor, and the older quicksave
        /// <paramref name="eRp"/> absorbs uncommitted-in-flight effects — so the guard does
        /// NOT gate on <c>authoritativeReduction</c>. Pure: no KSP singletons, fully
        /// unit-testable. <paramref name="resource"/> is logging-only (keeps it pure).
        /// </summary>
        internal static RewindReadbackVerdict ResolveRewindDivergence(
            string resource,
            double? eBefore, double? eRp,
            double recalcRunningTarget,
            double tolerance,
            out double floor, out double delta)
        {
            // A corrupt target value is divergence by definition — no floor needed.
            if (double.IsNaN(recalcRunningTarget) || double.IsInfinity(recalcRunningTarget))
            {
                floor = double.NaN;
                delta = double.NaN;
                return RewindReadbackVerdict.FlaggedDivergence;
            }

            bool beforeFinite = eBefore.HasValue && IsFinite(eBefore.Value);
            bool rpFinite = eRp.HasValue && IsFinite(eRp.Value);

            if (beforeFinite && rpFinite)
                floor = Math.Min(eBefore.Value, eRp.Value);
            else if (beforeFinite)
                floor = eBefore.Value;
            else if (rpFinite)
                floor = eRp.Value;
            else
            {
                floor = double.NaN;
                delta = double.NaN;
                return RewindReadbackVerdict.NotEvaluated;
            }

            delta = recalcRunningTarget - floor;

            // Downward-only: at or above (floor - tolerance) is the normal restore / stick
            // direction. Strictly below it is the silent-clobber signature.
            if (recalcRunningTarget >= floor - tolerance)
                return RewindReadbackVerdict.WithinExpectedRange;

            return RewindReadbackVerdict.FlaggedDivergence;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// Runs the rewind read-back guard over the three economy modules at the rewind
        /// apply boundary. For each resource it computes the SAME realized running value the
        /// matching <c>Patch*</c> writes (funds <c>GetRunningBalance()</c>, science the
        /// pending-adjusted <see cref="ComputePendingAdjustedRunningScience"/>, rep
        /// <c>GetRunningRep()</c>), resolves the verdict against the two witnesses, and logs
        /// every branch. Returns <c>true</c> iff (any resource flagged divergence) AND
        /// (abort is enabled or forced) — i.e. PatchAll should abort. Default warn-only path
        /// always returns <c>false</c>. Early-returns <c>false</c> when the guard is not armed,
        /// so it is inert on ordinary (non-rewind) recalc patches.
        /// </summary>
        internal static bool RunRewindReadbackGuard(
            ScienceModule science, FundsModule funds, ReputationModule reputation,
            bool authoritativeReduction)
        {
            if (!RewindReadbackGuard.Armed)
                return false;

            bool anyFlagged = false;

            anyFlagged |= EvaluateReadbackResource(
                "funds",
                RewindReadbackGuard.Before.Funds,
                RewindReadbackGuard.Loaded.Funds,
                funds != null ? (double?)funds.GetRunningBalance() : null,
                RewindReadbackFundsTolerance,
                authoritativeReduction);

            anyFlagged |= EvaluateReadbackResource(
                "science",
                RewindReadbackGuard.Before.Science,
                RewindReadbackGuard.Loaded.Science,
                science != null ? (double?)ComputePendingAdjustedRunningScience(science) : null,
                RewindReadbackScienceTolerance,
                authoritativeReduction);

            anyFlagged |= EvaluateReadbackResource(
                "reputation",
                RewindReadbackGuard.Before.Reputation.HasValue
                    ? (double?)RewindReadbackGuard.Before.Reputation.Value : null,
                RewindReadbackGuard.Loaded.Reputation.HasValue
                    ? (double?)RewindReadbackGuard.Loaded.Reputation.Value : null,
                reputation != null ? (double?)reputation.GetRunningRep() : null,
                RewindReadbackReputationTolerance,
                authoritativeReduction);

            return anyFlagged &&
                (RewindReadbackGuard.AbortRewindPatchOnDivergence ||
                 RewindReadbackGuard.ForceAbortForTesting);
        }

        // Evaluates one resource and logs its branch. Returns true iff flagged divergence.
        private static bool EvaluateReadbackResource(
            string resource, double? eBefore, double? eRp, double? recalcRunningTarget,
            double tolerance, bool authoritativeReduction)
        {
            // No module / no target to read — record the skip and bail.
            if (!recalcRunningTarget.HasValue)
            {
                ParsekLog.Verbose(RewindReadbackTag,
                    $"skipped resource={resource} no recalc target (module null)");
                return false;
            }

            RewindReadbackVerdict verdict = ResolveRewindDivergence(
                resource, eBefore, eRp, recalcRunningTarget.Value, tolerance,
                out double floor, out double delta);

            string target = recalcRunningTarget.Value.ToString("R", IC);

            switch (verdict)
            {
                case RewindReadbackVerdict.NotEvaluated:
                    ParsekLog.Verbose(RewindReadbackTag,
                        $"skipped resource={resource} no finite witness " +
                        $"(eBefore={FmtNullable(eBefore)} eRp={FmtNullable(eRp)} target={target})");
                    return false;

                case RewindReadbackVerdict.WithinExpectedRange:
                    ParsekLog.Verbose(RewindReadbackTag,
                        $"within-expected-range resource={resource} " +
                        $"eBefore={FmtNullable(eBefore)} eRp={FmtNullable(eRp)} " +
                        $"floor={floor.ToString("R", IC)} target={target} " +
                        $"delta={delta.ToString("R", IC)} tolerance={tolerance.ToString("R", IC)}");
                    return false;

                case RewindReadbackVerdict.FlaggedDivergence:
                    ParsekLog.Warn(RewindReadbackTag,
                        $"FLAGGED DIVERGENCE resource={resource} " +
                        $"eBefore={FmtNullable(eBefore)} eRp={FmtNullable(eRp)} " +
                        $"floor={floor.ToString("R", IC)} target={target} " +
                        $"delta={delta.ToString("R", IC)} tolerance={tolerance.ToString("R", IC)} " +
                        $"authoritativeReduction={authoritativeReduction}: recalc would write the " +
                        "economy BELOW the real career floor (possible silent career corruption)");
                    return true;

                default:
                    return false;
            }
        }

        private const string RewindReadbackTag = "RewindReadback";

        private static string FmtNullable(double? v)
        {
            return v.HasValue ? v.Value.ToString("R", IC) : "none";
        }

        internal static void ResetForTesting()
        {
            SuppressUnityCallsForTesting = false;
            protoTechNodesReflectionWarnEmitted = false;
            ResetDrawdownGuardSessionLatches();
            FacilityStatePatcher.ResetForTesting();
        }
    }
}
