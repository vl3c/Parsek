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

            var committedTechs = MilestoneStore.GetCommittedTechIds();
            if (!committedTechs.Contains(techId))
                return true;

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

            CommittedActionDialog.ShowBlocked(
                "Cannot research \"" + (__instance.title ?? techId) + "\"",
                "This technology is already committed on your timeline" + utStr + ".",
                sciCost);

            return false;
        }
    }
}
