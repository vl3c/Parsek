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
    ///
    /// #442: certain ProgressNode subclasses (RecordsSpeed, RecordsAltitude,
    /// RecordsDistance, RecordsDepth) call AwardProgress directly without first going
    /// through Complete()/OnProgressComplete, so no pending event ever lands in
    /// PendingMilestoneEventByNode. When the postfix sees no pending event for the
    /// node, it emits a standalone fully-populated MilestoneAchieved event instead of
    /// silently no-oping. This also mechanically protects against future KSP additions
    /// that bypass the OnProgressComplete pipeline.
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
                // Route to the testable helper with a live UT from Planetarium. Production
                // is the only call site that touches Unity statics; tests call RoutePostfix
                // directly with a literal UT.
                RoutePostfix(__instance, funds, science, reputation,
                    __instance != null ? Planetarium.GetUniversalTime() : 0.0);
            }
            catch (Exception ex)
            {
                // Defensive: even the UT lookup must not throw into KSP's reward pipeline.
                ParsekLog.Warn(Tag,
                    $"Postfix threw while capturing milestone rewards: {ex.Message}");
            }
        }

        /// <summary>
        /// #442: branch-routing helper, extracted from <see cref="Postfix"/> so unit tests
        /// can exercise the enrich vs emit-standalone decision without needing live
        /// <see cref="Planetarium"/> statics. Production passes
        /// <see cref="Planetarium.GetUniversalTime"/> from the postfix; tests pass a literal.
        /// Internal static for testability.
        /// </summary>
        internal static void RoutePostfix(
            ProgressNode node,
            float funds, float science, float reputation, double ut)
        {
            try
            {
                if (GameStateRecorder.IsReplayingActions)
                {
                    ParsekLog.Verbose(Tag,
                        "Suppressed milestone enrichment during action replay");
                    return;
                }
                if (node == null) return;

                // #442: branch on whether OnProgressComplete already emitted a pending
                // event for this node. The two cases are mutually exclusive — never enrich
                // and emit-standalone for the same AwardProgress call. The standalone
                // helper logs its own Info line with node ID + reward values, so no extra
                // patch-side log is needed on the else branch.
                if (GameStateRecorder.PendingMilestoneEventByNode.ContainsKey(node))
                {
                    GameStateRecorder.EnrichPendingMilestoneRewards(
                        node, (double)funds, reputation, (double)science);
                }
                else
                {
                    GameStateRecorder.EmitStandaloneProgressReward(
                        node, (double)funds, reputation, (double)science, ut);
                }
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
