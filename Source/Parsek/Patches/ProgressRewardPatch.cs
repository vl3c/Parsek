using System;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony postfix on <see cref="ProgressNode.AwardProgress"/> (the protected method
    /// that the various ProgressNode subclasses call to apply funds/science/reputation
    /// rewards). The reward values arrive as method parameters, so we can capture them
    /// accurately without decoding the per-subclass reward table.
    ///
    /// KSP's reward pipeline:
    ///   subclass handler -> ProgressNode.Complete() -> OnProgressComplete.Fire()
    ///                    -> GameStateRecorder.OnProgressComplete (emits event w/ zeros)
    ///                    -> subclass calls AwardProgressStandard() -> AwardProgress(...)
    ///                    -> THIS POSTFIX runs, updates pending event detail in place.
    ///
    /// Fixes #400 (milestone rewards hardcoded to zero). The plan (§D) recommended
    /// <c>GameVariables.GetProgressFunds/Rep/Science</c>, but those methods do not exist
    /// in stock KSP's GameVariables (verified via decompile) — the review's fallback
    /// plan of patching AwardProgress directly is the only stable approach.
    /// </summary>
    [HarmonyPatch(typeof(ProgressNode), "AwardProgress",
        typeof(string), typeof(float), typeof(float), typeof(float), typeof(CelestialBody))]
    internal static class ProgressRewardPatch
    {
        private const string Tag = "ProgressRewardPatch";

        internal static void Postfix(
            ProgressNode __instance,
            float funds, float science, float reputation)
        {
            try
            {
                if (GameStateRecorder.IsReplayingActions)
                {
                    ParsekLog.Verbose(Tag,
                        "Suppressed milestone enrichment during action replay");
                    return;
                }
                if (__instance == null) return;

                GameStateRecorder.EnrichPendingMilestoneRewards(
                    __instance, (double)funds, reputation, (double)science);
            }
            catch (Exception ex)
            {
                // Defensive: never let a Harmony postfix throw into KSP's reward pipeline.
                ParsekLog.Warn(Tag,
                    $"Postfix threw while enriching milestone rewards: {ex.Message}");
            }
        }
    }
}
