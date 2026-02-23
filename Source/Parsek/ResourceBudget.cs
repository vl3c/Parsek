using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class ResourceBudget
    {
        internal struct BudgetSummary
        {
            public double reservedFunds;
            public double reservedScience;
            public double reservedReputation;
        }

        internal static double CommittedFundsCost(RecordingStore.Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            if (rec.PreLaunchFunds == 0 && rec.Points[rec.Points.Count - 1].funds == 0)
                return 0;

            double totalImpact = rec.PreLaunchFunds - rec.Points[rec.Points.Count - 1].funds;

            int lastIdx = rec.LastAppliedResourceIndex;
            if (lastIdx >= rec.Points.Count - 1)
                return 0;

            if (lastIdx < 0)
                return totalImpact;

            double alreadyApplied = rec.PreLaunchFunds - rec.Points[lastIdx].funds;
            return totalImpact - alreadyApplied;
        }

        internal static double CommittedScienceCost(RecordingStore.Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            if (rec.PreLaunchScience == 0 && rec.Points[rec.Points.Count - 1].science == 0)
                return 0;

            double totalImpact = rec.PreLaunchScience - rec.Points[rec.Points.Count - 1].science;

            int lastIdx = rec.LastAppliedResourceIndex;
            if (lastIdx >= rec.Points.Count - 1)
                return 0;

            if (lastIdx < 0)
                return totalImpact;

            double alreadyApplied = rec.PreLaunchScience - rec.Points[lastIdx].science;
            return totalImpact - alreadyApplied;
        }

        internal static double CommittedReputationCost(RecordingStore.Recording rec)
        {
            if (rec == null || rec.Points.Count == 0) return 0;
            if (rec.PreLaunchReputation == 0 && rec.Points[rec.Points.Count - 1].reputation == 0)
                return 0;

            double totalImpact = rec.PreLaunchReputation - rec.Points[rec.Points.Count - 1].reputation;

            int lastIdx = rec.LastAppliedResourceIndex;
            if (lastIdx >= rec.Points.Count - 1)
                return 0;

            if (lastIdx < 0)
                return totalImpact;

            double alreadyApplied = rec.PreLaunchReputation - rec.Points[lastIdx].reputation;
            return totalImpact - alreadyApplied;
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
                        cost += ParseCostFromDetail(e.detail);
                        break;
                    case GameStateEventType.FacilityUpgraded:
                        cost += ComputeFacilityUpgradeCost(e.valueBefore, e.valueAfter);
                        break;
                }
            }
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
                    cost += ParseCostFromDetail(e.detail);
            }
            return cost;
        }

        internal static BudgetSummary ComputeTotal(
            IList<RecordingStore.Recording> recordings,
            IReadOnlyList<Milestone> milestones)
        {
            var result = new BudgetSummary();

            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    result.reservedFunds += CommittedFundsCost(recordings[i]);
                    result.reservedScience += CommittedScienceCost(recordings[i]);
                    result.reservedReputation += CommittedReputationCost(recordings[i]);
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

            return result;
        }

        internal static double ParseCostFromDetail(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return 0;

            string[] parts = detail.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.StartsWith("cost=", StringComparison.Ordinal))
                {
                    double cost;
                    if (double.TryParse(part.Substring(5), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out cost))
                        return cost;
                }
            }
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
