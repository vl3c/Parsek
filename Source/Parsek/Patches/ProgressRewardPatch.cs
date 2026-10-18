using System;
using System.Globalization;
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
    /// PendingMilestoneEventById. When the postfix sees no pending event for the
    /// node, it emits a standalone fully-populated MilestoneAchieved event instead of
    /// silently no-oping. This also mechanically protects against future KSP additions
    /// that bypass the OnProgressComplete pipeline.
    ///
    /// #443: the routing branch keys off the qualified milestone id (e.g. "Kerbin/Landing")
    /// rather than the ProgressNode reference, matching the re-keyed pending map. The
    /// id is computed via <c>GameStateRecorder.QualifyMilestoneId</c> for both branches.
    ///
    /// Outside CAREER stock pays nothing: the decompiled <c>AwardProgress</c> returns at its
    /// first line when <c>HighLogic.CurrentGame.Mode != Game.Modes.CAREER</c>, but a Harmony
    /// postfix still runs with the nominal reward arguments. The milestone is therefore
    /// recorded with zero rewards in SANDBOX and SCIENCE_SANDBOX, so the ledger never books
    /// (and the science patch never writes into R&amp;D) a reward stock did not pay.
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
            if (ParsekGameModeGate.CheckInert("ProgressRewardPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                // Route to the testable helper with a live UT from Planetarium. Production
                // is the only call site that touches Unity statics; tests call RoutePostfix
                // directly with a literal UT.
                RoutePostfix(__instance, funds, science, reputation,
                    __instance != null ? Planetarium.GetUniversalTime() : 0.0,
                    StockPaysProgressRewards(ReadCurrentGameMode()));
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
            float funds, float science, float reputation, double ut,
            bool stockPaysRewards = true)
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
                // #443: lookup keyed by qualified milestone id (matches the re-keyed map);
                // QualifyMilestoneId returns the same string the OnProgressComplete writer
                // computed, so the branch decision is robust to ProgressNode instance
                // aliasing inside KSP's reward pipeline.
                string milestoneId = GameStateRecorder.QualifyMilestoneId(node);
                if (!stockPaysRewards && (funds != 0f || science != 0f || reputation != 0f))
                {
                    ParsekLog.Verbose(Tag,
                        $"AwardProgress outside CAREER pays no reward in stock: recording " +
                        $"'{milestoneId ?? "<null>"}' with zero rewards (nominal funds=" +
                        funds.ToString("F0", CultureInfo.InvariantCulture) +
                        " sci=" + science.ToString("F1", CultureInfo.InvariantCulture) +
                        " rep=" + reputation.ToString("F1", CultureInfo.InvariantCulture) + ")");
                    funds = 0f;
                    science = 0f;
                    reputation = 0f;
                }
                if (!string.IsNullOrEmpty(milestoneId) &&
                    GameStateRecorder.PendingMilestoneEventById.ContainsKey(milestoneId))
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

        /// <summary>
        /// True when stock's <c>ProgressNode.AwardProgress</c> pays its rewards in
        /// <paramref name="mode"/>: CAREER only (decompiled, KSP 1.12.5). No current game
        /// (xUnit, main menu) keeps the historical answer, true.
        /// </summary>
        internal static bool StockPaysProgressRewards(Game.Modes? mode)
        {
            return !mode.HasValue || mode.Value == Game.Modes.CAREER;
        }

        private static Game.Modes? ReadCurrentGameMode()
        {
            var game = HighLogic.CurrentGame;
            return game != null ? game.Mode : (Game.Modes?)null;
        }
    }
}
