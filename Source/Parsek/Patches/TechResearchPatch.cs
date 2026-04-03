using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on RDTech.UnlockTech to block researching tech nodes
    /// that are already committed in unreplayed milestones.
    /// </summary>
    [HarmonyPatch(typeof(RDTech), nameof(RDTech.UnlockTech))]
    internal static class TechResearchPatch
    {
        static bool Prefix(RDTech __instance)
        {
            if (__instance == null) return true;
            string techId = __instance.techID;
            if (string.IsNullOrEmpty(techId)) return true;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("TechResearchPatch",
                    $"Bypassing block for '{techId}' — action replay in progress");
                return true;
            }

            var committedTechs = MilestoneStore.GetCommittedTechIds();
            if (!committedTechs.Contains(techId))
            {
                // Check ledger reservation: can the player afford this tech?
                float sciCostCheck = __instance.scienceCost;
                if (sciCostCheck > 0 && !LedgerOrchestrator.CanAffordScienceSpending(sciCostCheck))
                {
                    ParsekLog.Info("TechResearchPatch",
                        $"Blocking tech research: '{techId}' ({__instance.title ?? techId}) — " +
                        $"insufficient science (cost={sciCostCheck:F1})");

                    CommittedActionDialog.ShowBlocked(
                        "Cannot research \"" + (__instance.title ?? techId) + "\"",
                        "Insufficient science. Other committed tech unlocks have reserved " +
                        "your science budget.",
                        $"{sciCostCheck:F1} science required");

                    return false;
                }

                ParsekLog.Verbose("TechResearchPatch",
                    $"Allowing tech research: '{techId}' ({__instance.title ?? techId}) — not in committed set ({committedTechs.Count} committed)");
                return true;
            }

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
                $"Blocking tech research: '{techId}' ({__instance.title ?? techId}) — already committed{utStr}" +
                (!string.IsNullOrEmpty(sciCost) ? $", {sciCost}" : ""));

            CommittedActionDialog.ShowBlocked(
                "Cannot research \"" + (__instance.title ?? techId) + "\"",
                "This technology is already committed on your timeline" + utStr + ".",
                sciCost);

            return false;
        }
    }
}
