using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on RDTech.UnlockTech. UnlockTech does NOT deduct science itself
    /// (the stock deduction lives in RDTech.ResearchTech, which is gated pre-deduction
    /// by <see cref="TechResearchSpendPatch"/>), so this prefix is only a non-destructive
    /// backstop for direct UnlockTech callers and blocks already-committed techs ONLY.
    /// The affordability gate is intentionally NOT applied here: it runs pre-deduction in
    /// ResearchTech (BUG-G fix), and applying it post-deduction would re-introduce the
    /// destructive "deduct then block" loss.
    /// </summary>
    [HarmonyPatch(typeof(RDTech), nameof(RDTech.UnlockTech))]
    internal static class TechResearchPatch
    {
        static bool Prefix(RDTech __instance)
        {
            // includeAffordability:false — UnlockTech is post-deduction; only the
            // deduction-independent committed-tech block is safe here.
            return !TryBlockTechResearch(__instance, includeAffordability: false);
        }

        /// <summary>
        /// Shared tech-research block decision used by both the pre-deduction
        /// <see cref="TechResearchSpendPatch"/> (ResearchTech, includeAffordability=true)
        /// and the post-deduction backstop here (UnlockTech, includeAffordability=false).
        /// Returns true when the research must be BLOCKED (and emits the log + blocked
        /// dialog as a side effect); false to allow.
        /// </summary>
        internal static bool TryBlockTechResearch(RDTech tech, bool includeAffordability)
        {
            if (tech == null) return false;
            string techId = tech.techID;
            if (string.IsNullOrEmpty(techId)) return false;

            if (!(ParsekSettings.Current?.blockCommittedActions ?? true))
            {
                ParsekLog.Verbose("TechResearchPatch",
                    "feature disabled by ParsekSettings");
                return false;
            }

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("TechResearchPatch",
                    $"Bypassing block for '{techId}' — action replay in progress");
                return false;
            }

            var committedTechs = MilestoneStore.GetCommittedTechIds();
            if (committedTechs.Contains(techId))
            {
                var ev = MilestoneStore.FindCommittedEvent(
                    GameStateEventType.TechResearched, techId);

                string sciCost = "";
                if (ev.HasValue)
                {
                    double cost = ResourceBudget.ParseCostFromDetail(ev.Value.detail);
                    if (cost > 0)
                        sciCost = cost.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                            + " science reserved for this action";
                }

                string utStr = ev.HasValue
                    ? " at UT " + ev.Value.ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                    : "";

                ParsekLog.Info("TechResearchPatch",
                    $"Blocking tech research: '{techId}' ({tech.title ?? techId}) — already committed{utStr}" +
                    (!string.IsNullOrEmpty(sciCost) ? $", {sciCost}" : ""));

                CommittedActionDialog.ShowBlocked(
                    "Cannot research \"" + (tech.title ?? techId) + "\"",
                    "This technology is already committed on your timeline" + utStr + ".",
                    sciCost);

                return true;
            }

            if (includeAffordability)
            {
                // Ledger reservation: can the player afford this tech? Reconciled against
                // the drawdown-guard-preserved live pool (BUG-G), so a missing-earning leak
                // no longer falsely blocks an affordable purchase.
                float sciCostCheck = tech.scienceCost;
                if (sciCostCheck > 0 && !LedgerOrchestrator.CanAffordScienceSpending(sciCostCheck))
                {
                    ParsekLog.Info("TechResearchPatch",
                        $"Blocking tech research: '{techId}' ({tech.title ?? techId}) — " +
                        $"insufficient science (cost={sciCostCheck:F1})");

                    CommittedActionDialog.ShowBlocked(
                        "Cannot research \"" + (tech.title ?? techId) + "\"",
                        "Insufficient science. Other committed tech unlocks have reserved " +
                        "your science budget.",
                        $"{sciCostCheck:F1} science required");

                    return true;
                }
            }

            ParsekLog.Verbose("TechResearchPatch",
                $"Allowing tech research: '{techId}' ({tech.title ?? techId}) — not in committed set ({committedTechs.Count} committed)");
            return false;
        }
    }
}
