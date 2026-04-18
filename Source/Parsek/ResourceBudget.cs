using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal struct BudgetSummary
    {
        public double reservedFunds;
        public double reservedScience;
        public double reservedReputation;
    }

    // Phase F: ResourceDelta struct removed alongside ComputeStandaloneDelta —
    // the standalone resource applier (ApplyResourceDeltas) is gone, the ledger
    // drives funds/science/reputation now.

    internal static class ResourceBudget
    {
        private static BudgetSummary cachedBudget;
        private static bool budgetDirty = true;

        internal static void Invalidate() { budgetDirty = true; }

        internal static double CommittedFundsCost(Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            if (rec.PreLaunchFunds == 0 && rec.Points[rec.Points.Count - 1].funds == 0)
                return 0;

            double totalImpact = rec.PreLaunchFunds - rec.Points[rec.Points.Count - 1].funds;

            int lastIdx = rec.LastAppliedResourceIndex;
            if (lastIdx >= rec.Points.Count - 1)
            {
                ParsekLog.Verbose("ResourceBudget",
                    $"CommittedFundsCost: '{rec.VesselName}' fully applied (lastIdx={lastIdx}) — 0");
                return 0;
            }

            if (lastIdx < 0)
            {
                ParsekLog.Verbose("ResourceBudget",
                    $"CommittedFundsCost: '{rec.VesselName}' not yet applied — totalImpact={totalImpact:F0} (pre={rec.PreLaunchFunds:F0}, end={rec.Points[rec.Points.Count - 1].funds:F0})");
                return totalImpact;
            }

            double alreadyApplied = rec.PreLaunchFunds - rec.Points[lastIdx].funds;
            double remaining = totalImpact - alreadyApplied;
            ParsekLog.Verbose("ResourceBudget",
                $"CommittedFundsCost: '{rec.VesselName}' partial — total={totalImpact:F0}, applied={alreadyApplied:F0}, remaining={remaining:F0}");
            return remaining;
        }

        internal static double CommittedScienceCost(Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            if (rec.PreLaunchScience == 0 && rec.Points[rec.Points.Count - 1].science == 0)
                return 0;

            double totalImpact = rec.PreLaunchScience - rec.Points[rec.Points.Count - 1].science;

            int lastIdx = rec.LastAppliedResourceIndex;
            if (lastIdx >= rec.Points.Count - 1)
            {
                ParsekLog.Verbose("ResourceBudget",
                    $"CommittedScienceCost: '{rec.VesselName}' fully applied (lastIdx={lastIdx}) — 0");
                return 0;
            }

            if (lastIdx < 0)
            {
                ParsekLog.Verbose("ResourceBudget",
                    $"CommittedScienceCost: '{rec.VesselName}' not yet applied — totalImpact={totalImpact:F1}");
                return totalImpact;
            }

            double alreadyApplied = rec.PreLaunchScience - rec.Points[lastIdx].science;
            double remaining = totalImpact - alreadyApplied;
            ParsekLog.Verbose("ResourceBudget",
                $"CommittedScienceCost: '{rec.VesselName}' partial — total={totalImpact:F1}, applied={alreadyApplied:F1}, remaining={remaining:F1}");
            return remaining;
        }

        internal static double CommittedReputationCost(Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            if (rec.PreLaunchReputation == 0 && rec.Points[rec.Points.Count - 1].reputation == 0)
                return 0;

            double totalImpact = rec.PreLaunchReputation - rec.Points[rec.Points.Count - 1].reputation;

            int lastIdx = rec.LastAppliedResourceIndex;
            if (lastIdx >= rec.Points.Count - 1)
            {
                ParsekLog.Verbose("ResourceBudget",
                    $"CommittedReputationCost: '{rec.VesselName}' fully applied (lastIdx={lastIdx}) — 0");
                return 0;
            }

            if (lastIdx < 0)
            {
                ParsekLog.Verbose("ResourceBudget",
                    $"CommittedReputationCost: '{rec.VesselName}' not yet applied — totalImpact={totalImpact:F1}");
                return totalImpact;
            }

            double alreadyApplied = rec.PreLaunchReputation - rec.Points[lastIdx].reputation;
            double remaining = totalImpact - alreadyApplied;
            ParsekLog.Verbose("ResourceBudget",
                $"CommittedReputationCost: '{rec.VesselName}' partial — total={totalImpact:F1}, applied={alreadyApplied:F1}, remaining={remaining:F1}");
            return remaining;
        }

        internal static double MilestoneCommittedFunds(Milestone m)
        {
            if (m == null || m.Events.Count == 0) return 0;

            double cost = 0;
            for (int i = m.LastReplayedEventIndex + 1; i < m.Events.Count; i++)
            {
                var e = m.Events[i];
                switch (e.eventType)
                {
                    case GameStateEventType.PartPurchased:
                        double partCost = ParseCostFromDetail(e.detail);
                        cost += partCost;
                        if (partCost > 0)
                            ParsekLog.Verbose("ResourceBudget",
                                $"MilestoneCommittedFunds: PartPurchased '{e.key}' cost={partCost:F0}");
                        break;
                    case GameStateEventType.FacilityUpgraded:
                        double facCost = ComputeFacilityUpgradeCost(e.valueBefore, e.valueAfter);
                        cost += facCost;
                        break;
                }
            }

            if (cost > 0)
                ParsekLog.Verbose("ResourceBudget",
                    $"MilestoneCommittedFunds: milestone {(m.MilestoneId != null && m.MilestoneId.Length >= 8 ? m.MilestoneId.Substring(0, 8) : m.MilestoneId ?? "?")} total={cost:F0}");

            return cost;
        }

        internal static double MilestoneCommittedScience(Milestone m)
        {
            if (m == null || m.Events.Count == 0) return 0;

            double cost = 0;
            for (int i = m.LastReplayedEventIndex + 1; i < m.Events.Count; i++)
            {
                var e = m.Events[i];
                if (e.eventType == GameStateEventType.TechResearched)
                {
                    double techCost = ParseCostFromDetail(e.detail);
                    cost += techCost;
                    if (techCost > 0)
                        ParsekLog.Verbose("ResourceBudget",
                            $"MilestoneCommittedScience: TechResearched '{e.key}' cost={techCost:F1}");
                }
            }

            if (cost > 0)
                ParsekLog.Verbose("ResourceBudget",
                    $"MilestoneCommittedScience: milestone {(m.MilestoneId != null && m.MilestoneId.Length >= 8 ? m.MilestoneId.Substring(0, 8) : m.MilestoneId ?? "?")} total={cost:F1}");

            return cost;
        }

        // Phase F: TreeCommittedFundsCost / Science / Reputation removed.
        // Trees no longer track lump-sum deltas; per-recording CommittedFundsCost
        // (etc.) is summed directly over tree.Recordings.Values in ComputeTotal.

        internal static BudgetSummary ComputeTotal(
            IList<Recording> recordings,
            IReadOnlyList<Milestone> milestones,
            IReadOnlyList<RecordingTree> trees = null)
        {
            if (!budgetDirty)
                return cachedBudget;

            budgetDirty = false;
            var result = new BudgetSummary();

            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    // Phase F round 2: skip tree-child recordings in the flat-list
                    // loop. RecordingStore.FinalizeTreeCommit adds every tree child
                    // into committedRecordings AND exposes it via CommittedTrees[i]
                    // .Recordings, so the per-tree loop below already counts it.
                    // Without this skip, mixed-store callers (the production
                    // `ComputeTotal(CommittedRecordings, Milestones, CommittedTrees)`
                    // shape) double-count every tree child. The old code used
                    // `!ManagesOwnResources` for the same skip; ManagesOwnResources
                    // was deleted as part of Phase F, so we branch on TreeId
                    // directly (set on every tree child — confirmed via
                    // ChainSegmentManager.AssignTreeId and
                    // RecordingStore.AddRecordingWithTreeForTesting).
                    if (recordings[i] != null && recordings[i].TreeId != null)
                        continue;
                    result.reservedFunds += CommittedFundsCost(recordings[i]);
                    result.reservedScience += CommittedScienceCost(recordings[i]);
                    result.reservedReputation += CommittedReputationCost(recordings[i]);
                }
            }

            if (trees != null)
            {
                // Phase F: iterate per-recording inside each tree instead of summing
                // a tree-level delta. Each recording's CommittedFundsCost (etc.)
                // captures its share of the tree's resource impact.
                for (int i = 0; i < trees.Count; i++)
                {
                    var tree = trees[i];
                    if (tree == null) continue;
                    foreach (var rec in tree.Recordings.Values)
                    {
                        result.reservedFunds += CommittedFundsCost(rec);
                        result.reservedScience += CommittedScienceCost(rec);
                        result.reservedReputation += CommittedReputationCost(rec);
                    }
                }
            }

            if (milestones != null)
            {
                for (int i = 0; i < milestones.Count; i++)
                {
                    if (!milestones[i].Committed) continue;
                    result.reservedFunds += MilestoneCommittedFunds(milestones[i]);
                    result.reservedScience += MilestoneCommittedScience(milestones[i]);
                }
            }

            cachedBudget = result;
            return result;
        }

        // Phase F: ComputeStandaloneDelta + ResourceDelta struct removed
        // alongside ApplyResourceDeltas in ParsekFlight (no remaining callers).

        // --- Full cost helpers (ignore application state) ---

        internal static double FullCommittedFundsCost(Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            return rec.PreLaunchFunds - rec.Points[rec.Points.Count - 1].funds;
        }

        internal static double FullCommittedScienceCost(Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            return rec.PreLaunchScience - rec.Points[rec.Points.Count - 1].science;
        }

        internal static double FullCommittedReputationCost(Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            return rec.PreLaunchReputation - rec.Points[rec.Points.Count - 1].reputation;
        }

        // Phase F: FullTreeCommittedFundsCost / Science / Reputation removed.
        // Tree-level lump-sum delta is gone; ComputeTotalFullCost iterates per
        // recording inside each tree using FullCommittedFundsCost (etc.) directly.

        internal static double FullMilestoneCommittedFunds(Milestone m)
        {
            if (m == null || m.Events.Count == 0) return 0;

            double cost = 0;
            for (int i = 0; i < m.Events.Count; i++)
            {
                var e = m.Events[i];
                switch (e.eventType)
                {
                    case GameStateEventType.PartPurchased:
                        cost += ParseCostFromDetail(e.detail);
                        break;
                    case GameStateEventType.FacilityUpgraded:
                        cost += ComputeFacilityUpgradeCost(e.valueBefore, e.valueAfter);
                        break;
                }
            }
            return cost;
        }

        internal static double FullMilestoneCommittedScience(Milestone m)
        {
            if (m == null || m.Events.Count == 0) return 0;

            double cost = 0;
            for (int i = 0; i < m.Events.Count; i++)
            {
                var e = m.Events[i];
                if (e.eventType == GameStateEventType.TechResearched)
                    cost += ParseCostFromDetail(e.detail);
            }
            return cost;
        }

        internal static double FullMilestoneCommittedReputation(Milestone m)
        {
            // Currently no milestone event types affect reputation,
            // but included for API symmetry with ComputeTotalFullCost.
            return 0;
        }

        internal static BudgetSummary ComputeTotalFullCost(
            IList<Recording> recordings,
            IReadOnlyList<Milestone> milestones,
            IReadOnlyList<RecordingTree> trees = null)
        {
            var result = new BudgetSummary();

            ParsekLog.Verbose("ResourceBudget",
                $"ComputeTotalFullCost: {recordings?.Count ?? 0} recordings, {milestones?.Count ?? 0} milestones, {trees?.Count ?? 0} trees");

            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    // Phase F round 2: skip tree-child recordings in the flat-list
                    // loop to avoid double-counting when the caller passes both
                    // CommittedRecordings (which includes tree children per
                    // FinalizeTreeCommit) and CommittedTrees. Mirrors the skip in
                    // ComputeTotal above.
                    if (recordings[i] != null && recordings[i].TreeId != null)
                        continue;
                    result.reservedFunds += FullCommittedFundsCost(recordings[i]);
                    result.reservedScience += FullCommittedScienceCost(recordings[i]);
                    result.reservedReputation += FullCommittedReputationCost(recordings[i]);
                }
            }

            if (trees != null)
            {
                // Phase F: iterate per-recording inside each tree.
                for (int i = 0; i < trees.Count; i++)
                {
                    var tree = trees[i];
                    if (tree == null) continue;
                    foreach (var rec in tree.Recordings.Values)
                    {
                        result.reservedFunds += FullCommittedFundsCost(rec);
                        result.reservedScience += FullCommittedScienceCost(rec);
                        result.reservedReputation += FullCommittedReputationCost(rec);
                    }
                }
            }

            if (milestones != null)
            {
                for (int i = 0; i < milestones.Count; i++)
                {
                    if (!milestones[i].Committed) continue;
                    result.reservedFunds += FullMilestoneCommittedFunds(milestones[i]);
                    result.reservedScience += FullMilestoneCommittedScience(milestones[i]);
                    result.reservedReputation += (double)FullMilestoneCommittedReputation(milestones[i]);
                }
            }

            ParsekLog.Verbose("ResourceBudget",
                $"ComputeTotalFullCost result: funds={result.reservedFunds:F0}, " +
                $"science={result.reservedScience:F1}, reputation={result.reservedReputation:F1}");

            return result;
        }

        internal static double ParseCostFromDetail(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return 0;

            // #451: prefer `entryCost=` (post-#451 PartPurchased detail) and fall
            // back to the legacy `cost=` token. Mirrors the dual-token strategy in
            // GameStateEventConverter.ConvertPartPurchased so MilestoneCommittedFunds
            // (the only consumer of this helper for PartPurchased events) stays in
            // lockstep if a future producer drops the legacy `cost=` token. Other
            // event types that emit only `cost=` (CrewHired, TechResearch, etc.)
            // continue to land on the cost= fallback unchanged.
            string[] parts = detail.Split(';');
            double entryCost = 0;
            bool entryCostFound = false;
            double legacyCost = 0;
            bool legacyCostFound = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (!entryCostFound && part.StartsWith("entryCost=", StringComparison.Ordinal))
                {
                    double parsed;
                    if (double.TryParse(part.Substring(10), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        entryCost = parsed;
                        entryCostFound = true;
                    }
                    else
                        ParsekLog.Warn("ResourceBudget", $"ParseCostFromDetail: failed to parse '{part}' from detail='{detail}'");
                }
                else if (!legacyCostFound && part.StartsWith("cost=", StringComparison.Ordinal))
                {
                    double parsed;
                    if (double.TryParse(part.Substring(5), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed))
                    {
                        legacyCost = parsed;
                        legacyCostFound = true;
                    }
                    else
                        ParsekLog.Warn("ResourceBudget", $"ParseCostFromDetail: failed to parse '{part}' from detail='{detail}'");
                }
            }
            if (entryCostFound) return entryCost;
            if (legacyCostFound) return legacyCost;
            return 0;
        }

        internal static double ComputeFacilityUpgradeCost(double levelBefore, double levelAfter)
        {
            // Facility upgrade costs scale with level difference.
            // KSP facility costs are game-specific; we estimate based on level delta.
            // This is a rough placeholder — exact costs depend on facility type and game settings.
            // For now, we use the valueBefore/valueAfter as resource delta indicators.
            // If the event's valueBefore/valueAfter represent the actual funds before/after,
            // the cost is simply the difference. Otherwise we return 0.
            if (levelAfter > levelBefore && levelBefore >= 0)
                return 0; // Facility costs are captured by FundsChanged events which are excluded from milestones
            return 0;
        }
    }
}
