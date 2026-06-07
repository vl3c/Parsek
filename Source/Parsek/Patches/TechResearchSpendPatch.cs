using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on RDTech.ResearchTech, the player-facing tech-purchase entry
    /// point. Stock RDTech.ResearchTech deducts science (host.AddScience(-scienceCost,
    /// RnDTechResearch)) and ONLY THEN calls UnlockTech, so the old UnlockTech-only block
    /// fired AFTER the deduction and silently consumed science with nothing delivered
    /// (BUG-G, Bug 2). Gating here — BEFORE any deduction — makes a Parsek block
    /// non-destructive by construction: returning false skips the original method
    /// entirely, so no science is deducted and the node stays locked. The stock caller
    /// (KSP.UI.Screens.RDController.ActionButtonClick) discards ResearchTech's result and
    /// calls techTree.RefreshUI(), which re-reads the unchanged node state, so the UI
    /// correctly shows the node as still un-researched.
    /// </summary>
    [HarmonyPatch(typeof(RDTech), nameof(RDTech.ResearchTech))]
    internal static class TechResearchSpendPatch
    {
        static bool Prefix(RDTech __instance, ref RDTech.OperationResult __result)
        {
            // includeAffordability:true — this is the authoritative pre-deduction gate.
            if (TechResearchPatch.TryBlockTechResearch(__instance, includeAffordability: true))
            {
                // Skip the original (no deduction, no unlock). Report Failure so any
                // result-aware caller does not treat the node as researched.
                __result = RDTech.OperationResult.Failure;
                return false;
            }

            return true;
        }
    }
}
