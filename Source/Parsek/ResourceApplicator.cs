using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure static methods for applying resource deltas (funds, science, reputation)
    /// from recordings and trees to the game state. Extracted from ParsekScenario.
    /// </summary>
    internal static class ResourceApplicator
    {
        /// <summary>
        /// Ticks standalone (non-tree, non-loop) recording resource deltas up to the given UT.
        /// </summary>
        internal static void TickStandalone(IList<Recording> recordings, double currentUT)
        {
            bool anyApplied = false;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];

                if (rec.TreeId != null) continue;
                if (rec.LoopPlayback) continue;
                if (rec.Points.Count < 2) continue;

                var delta = ResourceBudget.ComputeStandaloneDelta(
                    rec.Points, rec.LastAppliedResourceIndex, currentUT);
                if (!delta.hasChange) continue;

                int startIdx = Math.Max(rec.LastAppliedResourceIndex, 0);
                double fundsDelta = delta.funds;
                float scienceDelta = delta.science;
                float repDelta = delta.reputation;

                GameStateRecorder.SuppressResourceEvents = true;
                try
                {
                    if (fundsDelta != 0 && Funding.Instance != null)
                    {
                        if (fundsDelta < 0 && Funding.Instance.Funds + fundsDelta < 0)
                            fundsDelta = -Funding.Instance.Funds;
                        Funding.Instance.AddFunds(fundsDelta, TransactionReasons.None);
                    }

                    if (scienceDelta != 0 && ResearchAndDevelopment.Instance != null)
                    {
                        if (scienceDelta < 0 && ResearchAndDevelopment.Instance.Science + scienceDelta < 0)
                            scienceDelta = -ResearchAndDevelopment.Instance.Science;
                        ResearchAndDevelopment.Instance.AddScience(scienceDelta, TransactionReasons.None);
                    }

                    if (repDelta != 0 && Reputation.Instance != null)
                    {
                        if (repDelta < 0 && Reputation.CurrentRep + repDelta < 0)
                            repDelta = -Reputation.CurrentRep;
                        Reputation.Instance.AddReputation(repDelta, TransactionReasons.None);
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressResourceEvents = false;
                }

                rec.LastAppliedResourceIndex = delta.targetIndex;
                anyApplied = true;

                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Scenario",
                    $"Resource tick: \"{rec.VesselName}\" idx {startIdx}\u2192{delta.targetIndex}" +
                    $" funds={fundsDelta.ToString("+0.0;-0.0", ic)} sci={scienceDelta.ToString("+0.0;-0.0", ic)} rep={repDelta.ToString("+0.0;-0.0", ic)}");

                if (delta.targetIndex == rec.Points.Count - 1)
                {
                    ParsekLog.Info("Scenario",
                        $"Resource tick complete for \"{rec.VesselName}\"");
                }
            }

            if (anyApplied)
                ResourceBudget.Invalidate();
        }

        /// <summary>
        /// Ticks tree recording resource deltas (lump-sum application after tree end UT).
        /// </summary>
        internal static void TickTrees(IReadOnlyList<RecordingTree> trees, double currentUT)
        {
            bool anyApplied = false;

            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree.ResourcesApplied)
                {
                    ParsekLog.VerboseRateLimited("ResourceApplicator", "tree-skip-applied",
                        $"TickTrees: skipping tree '{tree.TreeName}' — already applied");
                    continue;
                }

                double treeEndUT = 0;
                foreach (var rec in tree.Recordings.Values)
                {
                    double recEnd = rec.EndUT;
                    if (recEnd > treeEndUT) treeEndUT = recEnd;
                }

                if (currentUT <= treeEndUT)
                {
                    ParsekLog.VerboseRateLimited("ResourceApplicator", "tree-wait-ut",
                        $"TickTrees: waiting for tree '{tree.TreeName}' — currentUT={currentUT:F1} treeEndUT={treeEndUT:F1}");
                    continue;
                }

                GameStateRecorder.SuppressResourceEvents = true;
                try
                {
                    if (tree.DeltaFunds != 0 && Funding.Instance != null)
                    {
                        double delta = tree.DeltaFunds;
                        if (delta < 0 && Funding.Instance.Funds + delta < 0)
                            delta = -Funding.Instance.Funds;
                        Funding.Instance.AddFunds(delta, TransactionReasons.None);
                        var ic = CultureInfo.InvariantCulture;
                        ParsekLog.Info("Scenario",
                            $"Tree resource tick: funds {delta.ToString("+0.0;-0.0", ic)} (tree '{tree.TreeName}')");
                    }

                    if (tree.DeltaScience != 0 && ResearchAndDevelopment.Instance != null)
                    {
                        double delta = tree.DeltaScience;
                        if (delta < 0 && ResearchAndDevelopment.Instance.Science + delta < 0)
                            delta = -ResearchAndDevelopment.Instance.Science;
                        ResearchAndDevelopment.Instance.AddScience((float)delta, TransactionReasons.None);
                        var ic = CultureInfo.InvariantCulture;
                        ParsekLog.Info("Scenario",
                            $"Tree resource tick: science {delta.ToString("+0.0;-0.0", ic)} (tree '{tree.TreeName}')");
                    }

                    if (tree.DeltaReputation != 0 && Reputation.Instance != null)
                    {
                        float delta = tree.DeltaReputation;
                        if (delta < 0 && Reputation.CurrentRep + delta < 0)
                            delta = -Reputation.CurrentRep;
                        Reputation.Instance.AddReputation(delta, TransactionReasons.None);
                        var ic = CultureInfo.InvariantCulture;
                        ParsekLog.Info("Scenario",
                            $"Tree resource tick: reputation {delta.ToString("+0.0;-0.0", ic)} (tree '{tree.TreeName}')");
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressResourceEvents = false;
                }

                tree.ResourcesApplied = true;
                anyApplied = true;
                ParsekLog.Info("Scenario",
                    $"Tree resource lump sum applied for '{tree.TreeName}'");
            }

            if (anyApplied)
                ResourceBudget.Invalidate();
        }

        /// <summary>
        /// Deducts the committed budget from the game state and marks all recordings/trees
        /// as fully applied. Called after resource singletons are available on revert.
        /// </summary>
        internal static void DeductBudget(BudgetSummary budget, IList<Recording> recordings, IReadOnlyList<RecordingTree> trees)
        {
            GameStateRecorder.SuppressResourceEvents = true;
            try
            {
                if (budget.reservedFunds > 0 && Funding.Instance != null)
                {
                    double fundsBefore = Funding.Instance.Funds;
                    Funding.Instance.AddFunds(-budget.reservedFunds, TransactionReasons.None);
                    ParsekLog.Info("Scenario",
                        $"Budget deduction: funds {fundsBefore:F0} → {Funding.Instance.Funds:F0} (reserved={budget.reservedFunds:F0})");
                }

                if (budget.reservedScience > 0 && ResearchAndDevelopment.Instance != null)
                {
                    double scienceBefore = ResearchAndDevelopment.Instance.Science;
                    ResearchAndDevelopment.Instance.AddScience(
                        -(float)budget.reservedScience, TransactionReasons.None);
                    ParsekLog.Info("Scenario",
                        $"Budget deduction: science {scienceBefore:F1} → {ResearchAndDevelopment.Instance.Science:F1} (reserved={budget.reservedScience:F1})");
                }

                if (budget.reservedReputation > 0 && Reputation.Instance != null)
                {
                    float repBefore = Reputation.Instance.reputation;
                    Reputation.Instance.AddReputation(
                        -(float)budget.reservedReputation, TransactionReasons.None);
                    ParsekLog.Info("Scenario",
                        $"Budget deduction: reputation {repBefore:F1} → {Reputation.Instance.reputation:F1} (reserved={budget.reservedReputation:F1})");
                }
            }
            finally
            {
                GameStateRecorder.SuppressResourceEvents = false;
            }

            // Mark all recordings as fully applied so that:
            // 1. ResourceBudget.ComputeTotal returns 0 reserved (deduction already covers it)
            // 2. Ghost replay doesn't re-apply resource deltas (avoiding double-subtraction)
            // 3. The UI correctly shows current funds as available (no second subtraction)
            int recMarked = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].Points.Count > 0 && recordings[i].LastAppliedResourceIndex < recordings[i].Points.Count - 1)
                {
                    int oldIdx = recordings[i].LastAppliedResourceIndex;
                    recordings[i].LastAppliedResourceIndex = recordings[i].Points.Count - 1;
                    recMarked++;
                    ParsekLog.Verbose("Scenario",
                        $"  recording #{i} '{recordings[i].VesselName}': lastAppliedResourceIndex {oldIdx} → {recordings[i].LastAppliedResourceIndex}");
                }
            }

            // Mark all committed trees as ResourcesApplied (deduction already covers their costs)
            int treeMarked = 0;
            for (int i = 0; i < trees.Count; i++)
            {
                if (!trees[i].ResourcesApplied)
                {
                    trees[i].ResourcesApplied = true;
                    treeMarked++;
                }
            }
            ParsekLog.Verbose("Scenario", $"  Marked {treeMarked} tree(s) as ResourcesApplied");

            ParsekLog.Info("Scenario",
                $"Budget deduction applied for epoch {MilestoneStore.CurrentEpoch} — " +
                $"{recMarked} recording(s) and {treeMarked} tree(s) marked as fully applied");
        }

        /// <summary>
        /// Resets game resources to the given baseline values (career mode only).
        /// Computes correction deltas from current state and applies them.
        /// </summary>
        internal static void CorrectToBaseline(double baselineFunds, double baselineScience, float baselineRep)
        {
            var ic = CultureInfo.InvariantCulture;

            bool hasResources = Funding.Instance != null
                && ResearchAndDevelopment.Instance != null
                && Reputation.Instance != null;

            if (hasResources)
            {
                // Career mode: reset resources to baseline
                double preFunds = Funding.Instance.Funds;
                float preScience = ResearchAndDevelopment.Instance.Science;
                float preRep = Reputation.Instance.reputation;
                ParsekLog.Info("Rewind",
                    $"Pre-adjustment state: funds={preFunds.ToString("F1", ic)}, " +
                    $"science={preScience.ToString("F1", ic)}, " +
                    $"rep={preRep.ToString("F1", ic)}, " +
                    $"baseline: funds={baselineFunds.ToString("F1", ic)}, " +
                    $"science={baselineScience.ToString("F1", ic)}, " +
                    $"rep={baselineRep.ToString("F1", ic)}");

                double fundsCorrection = baselineFunds - preFunds;
                double scienceCorrection = baselineScience - preScience;
                double repCorrection = (double)baselineRep - (double)preRep;

                ParsekLog.Info("Rewind",
                    $"Resource reset to baseline: funds={baselineFunds.ToString("F1", ic)}, " +
                    $"science={baselineScience.ToString("F1", ic)}, " +
                    $"rep={baselineRep.ToString("F1", ic)}, " +
                    $"correction: {fundsCorrection.ToString("F1", ic)}, " +
                    $"{scienceCorrection.ToString("F1", ic)}, " +
                    $"{repCorrection.ToString("F1", ic)}");

                GameStateRecorder.SuppressResourceEvents = true;
                try
                {
                    if (fundsCorrection != 0)
                        Funding.Instance.AddFunds(fundsCorrection, TransactionReasons.None);
                    if (scienceCorrection != 0)
                        ResearchAndDevelopment.Instance.AddScience((float)scienceCorrection, TransactionReasons.None);
                    if (repCorrection != 0)
                        Reputation.Instance.AddReputation((float)repCorrection, TransactionReasons.None);
                }
                finally
                {
                    GameStateRecorder.SuppressResourceEvents = false;
                }

                ParsekLog.Info("Rewind",
                    $"Post-adjustment state: funds={Funding.Instance.Funds.ToString("F1", ic)}, " +
                    $"science={ResearchAndDevelopment.Instance.Science.ToString("F1", ic)}, " +
                    $"rep={Reputation.Instance.reputation.ToString("F1", ic)}");
            }
            else
            {
                ParsekLog.Info("Rewind",
                    "Sandbox/science mode: no resource singletons — skipping resource adjustment");
            }
        }
    }
}
