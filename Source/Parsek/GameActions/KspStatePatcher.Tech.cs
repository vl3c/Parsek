using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // R&D tech tree patch and the committed tech-unlock pass.
    internal static partial class KspStatePatcher
    {
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
        /// One tech node the ledger walk claims was researched but could not pay for: a
        /// <see cref="GameActionType.ScienceSpending"/> row inside the cutoff that a walk
        /// REFUSED, leaving the node out of the patch target set. Since design 3.15 step 6
        /// <c>ScienceModule.ProcessSpending</c> refuses no tech (it charges a science debt),
        /// so this is the backstop that keeps a refused row from un-researching a node. Carries the numbers the re-lock refusal WARN
        /// reports so <see cref="PatchTechTree"/> never has to re-walk the action list.
        /// </summary>
        internal readonly struct UnaffordableUnlockDrop
        {
            /// <summary>The tech node id the refused row would have unlocked.</summary>
            internal readonly string NodeId;
            /// <summary>The node's science cost the walk could not cover.</summary>
            internal readonly float Cost;
            /// <summary>The running science pool at the moment the walk refused the spend.</summary>
            internal readonly double RunningScience;
            /// <summary>The refused row's UT.</summary>
            internal readonly double Ut;

            internal UnaffordableUnlockDrop(string nodeId, float cost, double runningScience, double ut)
            {
                NodeId = nodeId;
                Cost = cost;
                RunningScience = runningScience;
                Ut = ut;
            }
        }

        /// <summary>
        /// Pure row predicate: is this action a ScienceSpending unlock that the walk
        /// PROCESSED AND REFUSED inside <paramref name="utCutoff"/>? True only for the
        /// bookkeeping-shortfall shape the re-lock guard exists for.
        ///
        /// <para>The <see cref="GameAction.UnaffordableRunningScience"/> conjunct is the
        /// load-bearing one and must not be relaxed to a bare <c>!Affordable</c>:
        /// <c>RecalculationEngine.ResetDerivedFields</c> seeds <see cref="GameAction.Affordable"/>
        /// to <c>false</c> on every action, so <c>!Affordable</c> also matches a row the
        /// science walk never dispatched — and treating that as a shortfall would suppress a
        /// LEGITIMATE re-lock. Pinned by
        /// <c>IsRefusedUnaffordableUnlockRow_UnprocessedRow_IsFalse</c> and, at the
        /// consumed-decision layer, by <c>Legitimate_RewoundPastResearch_StillRelocks</c>.</para>
        ///
        /// <para>The cutoff check is a DEFENSIVE DUPLICATE of the outer loop's identical
        /// filter in <see cref="BuildTargetTechIdsForPatch"/>, not the thing that protects a
        /// rewind: the loop already skips a past-cutoff row before ever calling this. Keep it
        /// so the predicate is sound when called directly, but do not credit it with the
        /// rewind protection. The duplicate itself is pinned by
        /// <c>IsRefusedUnaffordableUnlockRow_RowPastCutoff_IsFalse</c>; the outer layer that
        /// actually does the work is pinned by
        /// <c>BuildTarget_RowRewoundPastTheCutoff_IsNotADrop</c>.</para>
        /// </summary>
        internal static bool IsRefusedUnaffordableUnlockRow(GameAction action, double? utCutoff)
        {
            if (action == null || action.Type != GameActionType.ScienceSpending)
                return false;
            if (string.IsNullOrEmpty(action.NodeId))
                return false;
            if (utCutoff.HasValue && action.UT > utCutoff.Value)
                return false;
            if (action.Affordable)
                return false;
            return action.UnaffordableRunningScience.HasValue;
        }

        /// <summary>
        /// Pure node predicate consumed by <see cref="PatchTechTree"/>'s not-in-target branch:
        /// refuse a re-lock when (and only when) the classification is the dangerous direction
        /// AND the node's absence from the target set is explained by a refused-unaffordable
        /// unlock row. Every other re-lock — a genuine rewind past the research, a tombstoned
        /// row, a node that was never researched — is left completely untouched.
        /// Pure: no KSP singletons touched.
        /// </summary>
        internal static bool ShouldRefuseUnaffordableRelock(
            TechNodePatchAction classification,
            string techId,
            IDictionary<string, UnaffordableUnlockDrop> unaffordableDrops)
        {
            if (classification != TechNodePatchAction.MakeUnavailable)
                return false;
            if (string.IsNullOrEmpty(techId))
                return false;
            if (unaffordableDrops == null || unaffordableDrops.Count == 0)
                return false;
            return unaffordableDrops.ContainsKey(techId);
        }

        /// <summary>
        /// The three ways <see cref="PatchTechTree"/>'s not-in-target branch can end.
        /// <see cref="TechRelockOutcome.Relock"/> is the mutating one (proto node removed —
        /// purchased parts go with it — and the static state flipped to Unavailable);
        /// <see cref="TechRelockOutcome.RefusedUnaffordable"/> mutates NOTHING.
        /// </summary>
        internal enum TechRelockOutcome { Relock, AlreadyUnavailable, RefusedUnaffordable }

        /// <summary>
        /// Pure decision for <see cref="PatchTechTree"/>'s not-in-target branch, composing
        /// <see cref="ClassifyTechNodeForPatch"/> with the unaffordable-re-lock guard. This
        /// is the single production expression that decides whether a researched node loses
        /// its proto node; the live loop only dispatches the returned outcome, so a test that
        /// drives this function drives the real branch selector. Pure: no KSP singletons.
        ///
        /// <para><paramref name="currentlyAvailable"/> is the classification input (EITHER
        /// half of KSP's split state: the proto dictionary, or the static tree node).
        /// <paramref name="protoBackedAvailable"/> is the proto half ALONE, and the guard
        /// requires it. The two differ in exactly one shape — "static node says Available but
        /// no proto node exists" — where there is no researched state to preserve (no proto
        /// means no purchased parts) and the pre-existing normalization (drop the stale static
        /// Available) is the right outcome. Refusing there would preserve nothing and leave
        /// the tree inconsistent, so the guard stands down and normalization proceeds. Pinned
        /// by <c>Guard_StandsDownOnSplitStateWithNoProtoNode</c>.</para>
        /// </summary>
        internal static TechRelockOutcome ResolveTechRelockOutcome(
            bool currentlyAvailable,
            bool protoBackedAvailable,
            string techId,
            IDictionary<string, UnaffordableUnlockDrop> unaffordableDrops)
        {
            var classification = ClassifyTechNodeForPatch(false, currentlyAvailable);
            if (protoBackedAvailable &&
                ShouldRefuseUnaffordableRelock(classification, techId, unaffordableDrops))
                return TechRelockOutcome.RefusedUnaffordable;
            return classification == TechNodePatchAction.MakeUnavailable
                ? TechRelockOutcome.Relock
                : TechRelockOutcome.AlreadyUnavailable;
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
            return BuildTargetTechIdsForPatch(
                baselines,
                actions,
                utCutoff,
                baselineTechExclusions,
                out _);
        }

        /// <summary>
        /// As the four-argument overload, and additionally reports the nodes that were
        /// dropped from the target set SOLELY because their ScienceSpending row was refused
        /// as unaffordable (<paramref name="unaffordableDrops"/>, keyed by node id; null when
        /// there is no target set at all, empty when nothing was refused).
        ///
        /// <para>"Solely" is enforced two ways: a candidate is only collected when
        /// <see cref="IsRefusedUnaffordableUnlockRow"/> holds (so a row rewound past the
        /// cutoff, or one the walk never processed, is not a candidate), and any candidate
        /// that ends up in the target set anyway — via the baseline, or via a second,
        /// affordable row for the same node — is dropped from the map after the walk. What
        /// survives is exactly "the surviving ledger still claims this node was researched,
        /// and the only thing standing between it and the target set is a reconstructed pool
        /// that could not pay". <see cref="PatchTechTree"/> refuses to re-lock those.</para>
        ///
        /// <para><b>Caller invariant.</b> Soundness of the drop map rests on every caller
        /// satisfying <c>techPatchCutoff &lt;= (walk utCutoff ?? +infinity)</c>, which every
        /// caller in <c>LedgerOrchestrator</c> does today (the two are the same value, or the
        /// walk is uncut). A future wrapper violating it would evaluate rows in the gap
        /// against markers left by the PROJECTION walk rather than the live walk. The failure
        /// direction is over-protection — a spurious drop entry only ever refuses a re-lock,
        /// never causes one — but it would be a wrong reading, so keep the invariant.</para>
        /// </summary>
        internal static HashSet<string> BuildTargetTechIdsForPatch(
            IReadOnlyList<GameStateBaseline> baselines,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            IReadOnlyCollection<string> baselineTechExclusions,
            out Dictionary<string, UnaffordableUnlockDrop> unaffordableDrops)
        {
            unaffordableDrops = null;

            var selectedBaseline = SelectTechBaselineForPatch(baselines, utCutoff);
            if (selectedBaseline == null ||
                selectedBaseline.researchedTechIds == null ||
                selectedBaseline.researchedTechIds.Count == 0)
            {
                return null;
            }

            unaffordableDrops = new Dictionary<string, UnaffordableUnlockDrop>(StringComparer.Ordinal);
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
                {
                    // Refused-and-processed rows become re-lock-guard candidates; anything
                    // else (never dispatched, rewound past) is not a claim and is ignored.
                    if (IsRefusedUnaffordableUnlockRow(action, utCutoff))
                    {
                        UnaffordableUnlockDrop existing;
                        if (!unaffordableDrops.TryGetValue(action.NodeId, out existing) ||
                            action.UT >= existing.Ut)
                        {
                            // Latest refusal wins: it is the one whose numbers describe the
                            // state the patch is about to act on.
                            unaffordableDrops[action.NodeId] = new UnaffordableUnlockDrop(
                                action.NodeId,
                                action.Cost,
                                action.UnaffordableRunningScience.Value,
                                action.UT);
                        }
                    }

                    continue;
                }

                target.Add(action.NodeId);
            }

            // A node that reached the target set anyway (baseline seed, or a second
            // affordable row) was never dropped, so it is not a guard candidate.
            if (unaffordableDrops.Count > 0)
            {
                List<string> covered = null;
                foreach (var nodeId in unaffordableDrops.Keys)
                {
                    if (!target.Contains(nodeId))
                        continue;
                    if (covered == null)
                        covered = new List<string>();
                    covered.Add(nodeId);
                }

                if (covered != null)
                {
                    for (int i = 0; i < covered.Count; i++)
                        unaffordableDrops.Remove(covered[i]);
                }
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
        /// Pure per-node classification for <see cref="PatchTechTree"/> (apply-boundary test
        /// seam, audit gap 2 / rec #5). Given whether a node should be researched in the target
        /// set and whether it is currently available live, returns which of the four outcomes
        /// applies. <see cref="TechNodePatchAction.MakeUnavailable"/> is the dangerous direction
        /// (a researched node being re-locked); the live wrapper resolves
        /// <paramref name="currentlyAvailable"/> per branch from the proto / static tech state
        /// and dispatches the actual mutation. Pure: no KSP singletons touched.
        /// </summary>
        internal enum TechNodePatchAction { MakeAvailable, AlreadyAvailable, MakeUnavailable, AlreadyUnavailable }

        internal static TechNodePatchAction ClassifyTechNodeForPatch(
            bool shouldBeAvailable, bool currentlyAvailable)
        {
            if (shouldBeAvailable)
                return currentlyAvailable
                    ? TechNodePatchAction.AlreadyAvailable
                    : TechNodePatchAction.MakeAvailable;
            return currentlyAvailable
                ? TechNodePatchAction.MakeUnavailable
                : TechNodePatchAction.AlreadyUnavailable;
        }

        /// <summary>
        /// Patches KSP's R&amp;D tech state to the recalculated timeline state.
        /// This updates both the authoritative proto-tech dictionary and the static
        /// tech-tree proto nodes, then asks KSP to refresh the tech-tree UI.
        ///
        /// <para><paramref name="unaffordableDrops"/> (from
        /// <see cref="BuildTargetTechIdsForPatch"/>) arms the unaffordable-re-lock guard:
        /// a node in that map is one the surviving ledger still claims was researched, so
        /// this method REFUSES to re-lock it and leaves the live researched state — proto
        /// node, purchased parts and all — exactly as it found it. Same philosophy as the
        /// drawdown guard: a non-authoritative recalc does not get to take away live career
        /// state on evidence the walk itself logs as suspect.</para>
        /// </summary>
        internal static void PatchTechTree(
            IReadOnlyCollection<string> targetTechIds,
            double? utCutoff = null,
            double? baselineUt = null,
            IDictionary<string, UnaffordableUnlockDrop> unaffordableDrops = null)
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
            int relockRefused = 0;
            List<string> relockRefusedIds = null;
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
                    bool currentlyAvailable = proto != null && proto.state == RDTech.State.Available;
                    if (ClassifyTechNodeForPatch(true, currentlyAvailable) == TechNodePatchAction.MakeAvailable)
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
                    // currentState is read from the proto dictionary, so it IS the proto half
                    // of KSP's split availability state; tech.state is the static tree half.
                    bool protoBackedAvailable = currentState == RDTech.State.Available;
                    bool currentlyAvailable =
                        protoBackedAvailable || tech.state == RDTech.State.Available;
                    var outcome = ResolveTechRelockOutcome(
                        currentlyAvailable, protoBackedAvailable, techId, unaffordableDrops);

                    // Unaffordable-re-lock guard: the ledger still claims this node was
                    // researched and only a reconstructed-pool shortfall kept it out of the
                    // target set. Refuse the re-lock outright — no proto removal (which would
                    // take the node's purchased parts with it, unrecoverable in a default
                    // career), no state flip. WARN loudly; the condition is a bug signature.
                    if (outcome == TechRelockOutcome.RefusedUnaffordable)
                    {
                        relockRefused++;
                        if (relockRefusedIds == null)
                            relockRefusedIds = new List<string>();
                        relockRefusedIds.Add(techId);

                        if (relockRefusedIds.Count <= IdentitySampleCap)
                        {
                            var drop = unaffordableDrops[techId];
                            ParsekLog.Warn(Tag,
                                "PatchTechTree: refusing-unaffordable-relock: " +
                                $"nodeId={techId}, cost={drop.Cost.ToString("R", IC)}, " +
                                $"runningScience={drop.RunningScience.ToString("R", IC)}, " +
                                $"spendUT={drop.Ut.ToString("R", IC)}, " +
                                "reason=unaffordable-science-spending-row — live researched state " +
                                "preserved (ledger still claims this node was researched; the " +
                                "reconstructed science pool could not pay for it, which is a " +
                                "bookkeeping shortfall, not a retraction)");
                        }

                        continue;
                    }

                    if (outcome == TechRelockOutcome.Relock)
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
                $"relockRefused={relockRefused.ToString(IC)}, " +
                $"utCutoff={cutoffLabel}, baselineUt={baselineLabel}" +
                (madeUnavailableSample.Length > 0
                    ? $", madeUnavailableIds=[{madeUnavailableSample}]"
                    : string.Empty) +
                (madeAvailableSample.Length > 0
                    ? $", madeAvailableIds=[{madeAvailableSample}]"
                    : string.Empty));

            if (relockRefused > 0)
            {
                // Loud summary in addition to the per-node WARNs: the per-node lines are
                // capped at IdentitySampleCap, this one always carries the true count.
                ParsekLog.Warn(Tag,
                    $"PatchTechTree: relock-refusal-summary: relockRefused={relockRefused.ToString(IC)}, " +
                    $"relockRefusedIds=[{ComposeBoundedIdentitySample(relockRefusedIds, IdentitySampleCap)}], " +
                    $"utCutoff={cutoffLabel} — a reconstructed science pool could not pay for " +
                    "unlock rows the surviving ledger still carries; the affected nodes were " +
                    "LEFT RESEARCHED. Investigate the ledger's science earnings, not the tech tree");
            }

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

        internal static Dictionary<string, ProtoTechNode> GetProtoTechNodesDictionary()
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

        // ---------------- committed tech unlocks on a cutoff-less walk ----------------

        /// <summary>Why <see cref="PlanCommittedTechUnlocksForPatch"/> planned nothing.</summary>
        internal const string CommittedTechUnlockSkipTechCutoff = "tech-cutoff-supplied";
        internal const string CommittedTechUnlockSkipClockNotReady = "clock-not-ready";
        internal const string CommittedTechUnlockSkipRewindPending = "rewind-ut-adjustment-pending";

        /// <summary>
        /// The tech nodes the committed timeline has researched by <paramref name="cutoff"/>:
        /// every affordable <see cref="GameActionType.ScienceSpending"/> row with a node id at
        /// or before it (the same rule <see cref="BuildTargetTechIdsForPatch"/> applies). Since
        /// design 3.15 step 6 the walk marks every tech it charges affordable, one charged into
        /// a science debt included, so a committed tech is never left out for want of science.
        /// Sorted, so the apply order and its log are deterministic. Pure.
        /// </summary>
        internal static List<string> BuildCommittedTechUnlockIdsForPatch(
            IReadOnlyList<GameAction> actions, double cutoff)
        {
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null || a.Type != GameActionType.ScienceSpending) continue;
                    if (string.IsNullOrEmpty(a.NodeId)) continue;
                    if (!a.Affordable) continue;
                    if (a.UT > cutoff) continue;
                    ids.Add(a.NodeId);
                }
            }
            return new List<string>(ids);
        }

        /// <summary>
        /// The tech nodes a recalculation WITHOUT a tech cutoff unlocks in stock, add-only.
        ///
        /// <para>A cutoff-less walk skips the two-direction <see cref="PatchTechTree"/> (#559:
        /// its re-lock direction would clobber live unlocks the ledger does not carry). But the
        /// KSC ledger cursor takes exactly that walk when the Space Center clock passes the
        /// LAST committed row (<c>RecalculateAndPatchForLiveTimelineEvent</c> finds no row
        /// after now), and so does every other "no future rows" recalculation, so a committed
        /// unlock at that row would never reach stock while the walk still charges its
        /// science. This pass closes that: it only ever unlocks, so the #559 guarantee
        /// stands.</para>
        ///
        /// <para>Capped at the live clock, never later, so a future node is never unlocked and
        /// a node unlocks exactly when its research block (<c>row.UT &gt; now</c>, same clock)
        /// lifts. Null (skip, <paramref name="skipReason"/> says why) when a tech cutoff was
        /// supplied (<see cref="PatchTechTree"/> owns the tree then, in both directions), when
        /// the clock is not ready (a cold load reads UT 0), or while a rewind's UT adjustment
        /// is pending (the clock is about to move back, and reading it now would unlock a node
        /// the rewind just re-locked). Pure.</para>
        /// </summary>
        internal static List<string> PlanCommittedTechUnlocksForPatch(
            IReadOnlyList<GameAction> actions, double? techPatchCutoff, double liveUT,
            bool rewindUtAdjustmentPending, out string skipReason)
        {
            if (techPatchCutoff.HasValue)
            {
                skipReason = CommittedTechUnlockSkipTechCutoff;
                return null;
            }
            if (rewindUtAdjustmentPending)
            {
                skipReason = CommittedTechUnlockSkipRewindPending;
                return null;
            }
            if (double.IsNaN(liveUT) || double.IsInfinity(liveUT)
                || !LedgerOrchestrator.IsCurrentUtReadyForCutoff(liveUT))
            {
                skipReason = CommittedTechUnlockSkipClockNotReady;
                return null;
            }
            skipReason = null;
            return BuildCommittedTechUnlockIdsForPatch(actions, liveUT);
        }

        /// <summary>
        /// Applies <see cref="PlanCommittedTechUnlocksForPatch"/>'s set to stock R&amp;D: each
        /// listed node that is not already Available gets the same proto node
        /// <see cref="PatchTechTree"/>'s unlock branch writes (bypass-entry-purchase parts
        /// rehydrated by <see cref="EnsureAvailableProtoTechNode"/>). Never re-locks and never
        /// touches a node outside the set. Runs before <see cref="PatchPurchasedParts"/>, so a
        /// committed purchase on a node unlocked here lands in the same pass. Null is a no-op.
        /// </summary>
        internal static void PatchCommittedTechUnlocks(
            IReadOnlyCollection<string> unlockIds, double? liveUT)
        {
            if (unlockIds == null)
                return;

            if (unlockIds.Count == 0)
            {
                VerboseStablePatchState("patch-noop|tech-unlocks", "no-rows",
                    "PatchCommittedTechUnlocks: no committed tech unlock at or before the live clock");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                VerboseStablePatchState("patch-skip|tech-unlocks|rnd", "rnd-null",
                    "PatchCommittedTechUnlocks: ResearchAndDevelopment.Instance is null - skipping " +
                    "(the next recalc with R&D loaded applies it)");
                return;
            }

            if (AssetBase.RnDTechTree == null || AssetBase.RnDTechTree.GetTreeTechs() == null)
            {
                VerboseStablePatchState("patch-skip|tech-unlocks|tree", "tree-unavailable",
                    "PatchCommittedTechUnlocks: RnDTechTree unavailable - skipping");
                return;
            }

            var wanted = new HashSet<string>(unlockIds, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var madeAvailableIds = new List<string>();
            int alreadyAvailable = 0;
            foreach (var tech in AssetBase.RnDTechTree.GetTreeTechs())
            {
                if (tech == null || string.IsNullOrEmpty(tech.techID) || !wanted.Contains(tech.techID))
                    continue;

                string techId = tech.techID;
                seen.Add(techId);
                ProtoTechNode proto = ResearchAndDevelopment.Instance.GetTechState(techId);
                bool protoAvailable = proto != null && proto.state == RDTech.State.Available;
                if (protoAvailable && tech.state == RDTech.State.Available)
                {
                    alreadyAvailable++;
                    continue;
                }

                proto = EnsureAvailableProtoTechNode(tech, proto, out _, out _);
                ResearchAndDevelopment.Instance.SetTechState(techId, proto);
                tech.state = RDTech.State.Available;
                madeAvailableIds.Add(techId);
            }

            var missingIds = new List<string>();
            foreach (string techId in wanted)
            {
                if (!seen.Contains(techId))
                    missingIds.Add(techId);
            }

            if (madeAvailableIds.Count == 0)
            {
                VerboseStablePatchState("patch-noop|tech-unlocks",
                    wanted.Count.ToString(IC) + "|" + missingIds.Count.ToString(IC),
                    $"PatchCommittedTechUnlocks: all {alreadyAvailable.ToString(IC)} committed unlock(s) " +
                    $"already Available (missing={missingIds.Count.ToString(IC)})");
            }
            else
            {
                RefreshTechTreeUi();
                string clockLabel = liveUT.HasValue ? liveUT.Value.ToString("R", IC) : "null";
                ParsekLog.Info(Tag,
                    $"PatchCommittedTechUnlocks: madeAvailable={madeAvailableIds.Count.ToString(IC)}, " +
                    $"alreadyAvailable={alreadyAvailable.ToString(IC)}, " +
                    $"missing={missingIds.Count.ToString(IC)}, liveUT={clockLabel}, " +
                    $"madeAvailableIds=[{ComposeBoundedIdentitySample(madeAvailableIds, IdentitySampleCap)}]");

                if (LedgerTrace.IsEnabled)
                {
                    foreach (string techId in madeAvailableIds)
                    {
                        LedgerTrace.EmitOnChange("tech-node", techId, "->available");
                        bool actualAvailable =
                            ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available;
                        if (LedgerTrace.IsTechNodePresenceMismatch(true, actualAvailable))
                            LedgerTrace.EmitAnomaly("tech-node", techId, "ledger-vs-truth",
                                "intendedAvailable=true actualAvailable=" + LedgerTrace.Bool(actualAvailable));
                    }
                }
            }

            if (missingIds.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchCommittedTechUnlocks: {missingIds.Count.ToString(IC)} committed unlock id(s) " +
                    $"not in the tech tree: [{ComposeBoundedIdentitySample(missingIds, IdentitySampleCap)}]");
        }
    }
}
