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
            if (ParsekGameModeGate.CheckInert("TechResearchPatch.Prefix")) return true; // S9 game-mode gate
            // includeAffordability:false - UnlockTech is post-deduction; only the
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

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("TechResearchPatch",
                    $"Bypassing block for '{techId}' - action replay in progress");
                return false;
            }

            if (TryBlockCommittedTech(techId, tech.title))
                return true;

            if (includeAffordability)
            {
                // Ledger reservation: can the player afford this tech? Reconciled against
                // the drawdown-guard-preserved live pool (BUG-G), so a missing-earning leak
                // no longer falsely blocks an affordable purchase.
                float sciCostCheck = tech.scienceCost;
                if (sciCostCheck > 0 && !LedgerOrchestrator.CanAffordScienceSpending(sciCostCheck))
                {
                    ParsekLog.Info("TechResearchPatch",
                        $"Blocking tech research: '{techId}' ({tech.title ?? techId}) - " +
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
                $"Allowing tech research: '{techId}' ({tech.title ?? techId}) - no committed future row " +
                $"(nowUT={CommittedFutureIndexCache.CurrentUT().ToString("F0", System.Globalization.CultureInfo.InvariantCulture)})");
            return false;
        }

        /// <summary>
        /// The committed-future half of the tech-research block: refuses a node a committed
        /// future researches (the log line + the blocked dialog). The R&amp;D screen marks
        /// and disables exactly this set (<c>StockUiRnDDecoration</c> reads the same
        /// <see cref="StockUiReservationPredicates.IsTechResearchBlocked"/>). The caller
        /// handles the replay bypass.
        /// </summary>
        internal static bool TryBlockCommittedTech(string techId, string title)
        {
            if (string.IsNullOrEmpty(techId)) return false;
            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            if (!StockUiReservationPredicates.IsTechResearchBlocked(index, techId, nowUT))
                return false;

            var entry = index.FirstFuture(CommittedFutureKind.TechResearch, techId, nowUT);
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            string sciCost = "";
            if (entry != null && entry.Amount > 0f)
                sciCost = entry.Amount.ToString("F1", ic) + " science reserved for this action";

            ParsekLog.Info("TechResearchPatch",
                $"Blocking tech research: '{techId}' ({title ?? techId}) - committed future row " +
                $"ut={(entry != null ? entry.UT.ToString("F0", ic) : "?")} nowUT={nowUT.ToString("F0", ic)} " +
                $"recording={entry?.RecordingId ?? "(ksc)"}" +
                (!string.IsNullOrEmpty(sciCost) ? $", {sciCost}" : ""));

            var text = StockUiReservationPredicates.ExplainTech(
                index, techId, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(
                "Cannot research \"" + (title ?? techId) + "\"",
                text.Body,
                sciCost);
            return true;
        }
    }
}
