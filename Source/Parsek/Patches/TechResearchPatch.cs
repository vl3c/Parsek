using System;
using System.Runtime.CompilerServices;
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

            string title = DisplayTitle(tech.title, techId, StockTreeTitle);
            if (TryBlockCommittedTech(techId, title))
                return true;

            if (includeAffordability)
            {
                // Ledger reservation: can the player afford this tech? Reconciled against
                // the drawdown-guard-preserved live pool (BUG-G), so a missing-earning leak
                // no longer falsely blocks an affordable purchase. Only a TIMELINE shortage is
                // refused here (the live pool covers the cost, the free science does not); a
                // plain shortage is stock's own refusal. The R&D side panel greys Research
                // over this same predicate (IsScienceShort), so the refusal has a mark.
                float sciCostCheck = tech.scienceCost;
                double free;
                if (IsScienceShort(sciCostCheck, out free))
                {
                    ParsekLog.Info("TechResearchPatch",
                        $"Blocking tech research: '{techId}' ({title}) - " +
                        $"insufficient science (cost={sciCostCheck:F1})");

                    CommittedActionDialog.ShowBlocked(
                        "Cannot research \"" + title + "\"",
                        ReservationExplanation.ScienceShortage().Body,
                        ReservationExplanation.ScienceShortageDetail(sciCostCheck, free));

                    return true;
                }
            }

            ParsekLog.Verbose("TechResearchPatch",
                $"Allowing tech research: '{techId}' ({title}) - no committed future row " +
                $"(nowUT={CommittedFutureIndexCache.CurrentUT().ToString("F0", System.Globalization.CultureInfo.InvariantCulture)})");
            return false;
        }

        /// <summary>
        /// The timeline science-shortage predicate: the live pool covers the cost but the
        /// effective free science
        /// (<see cref="LedgerOrchestrator.CanAffordScienceSpending(float, out double)"/>, a
        /// read-only probe) does not, because research later on the timeline holds the rest
        /// (<see cref="IsTimelineScienceShortage"/>). A plain shortage (the live pool itself
        /// is below the cost) is stock's own refusal and is left to stock: no grey, no
        /// tooltip, no click block. The pre-deduction click gate and the R&amp;D side panel's
        /// greyed Research button both read this (the pairing rule). <paramref name="free"/>
        /// is the effective free science it compared against (+inf when not probed).
        /// </summary>
        internal static bool IsScienceShort(float cost, out double free)
        {
            free = double.PositiveInfinity;
            if (cost <= 0f) return false;
            double live = ReadLiveScience();
            // A plain shortage, or no live pool: stock's own check decides, so no probe.
            if (double.IsNaN(live) || live < cost) return false;
            LedgerOrchestrator.CanAffordScienceSpending(cost, out free);
            return IsTimelineScienceShortage(cost, free, live);
        }

        /// <summary>
        /// Pure core of <see cref="IsScienceShort"/>: a positive cost the live pool covers
        /// and the effective free science does not. NaN live (no R&amp;D singleton) is never
        /// a timeline shortage.
        /// </summary>
        internal static bool IsTimelineScienceShortage(double cost, double effectiveFree, double liveScience)
        {
            if (!(cost > 0.0) || double.IsNaN(liveScience)) return false;
            return liveScience >= cost && effectiveFree < cost;
        }

        /// <summary>Test seam for the live science pool; null in every player build.</summary>
        internal static Func<double> LiveScienceForTesting;

        private static double ReadLiveScience()
        {
            var seam = LiveScienceForTesting;
            if (seam != null) return seam();
            try { return ReadLiveScienceCore(); }
            catch (Exception) { return double.NaN; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadLiveScienceCore()
        {
            var rnd = ResearchAndDevelopment.Instance;
            return rnd != null ? rnd.Science : double.NaN;
        }

        /// <summary>
        /// A tech's player-facing title: stock's own <c>RDTech.title</c> (localized when it is
        /// a key), else the tech tree's title for the id (<paramref name="treeTitle"/>,
        /// production <see cref="StockTreeTitle"/>), else the id itself, so an empty title
        /// never prints the internal id while a title exists to resolve.
        /// </summary>
        internal static string DisplayTitle(string title, string techId, Func<string, string> treeTitle)
        {
            string resolved = StockUiText.ResolveStockKey(title, "tech title");
            if (!string.IsNullOrEmpty(resolved)) return resolved;
            if (treeTitle != null && !string.IsNullOrEmpty(techId))
            {
                string fromTree = null;
                try { fromTree = treeTitle(techId); }
                catch (Exception ex)
                {
                    ParsekLog.VerboseRateLimited("TechResearchPatch", "tech-tree-title-failed",
                        "tech tree title lookup failed for '" + techId + "' (" + ex.GetType().Name + ")");
                }
                fromTree = StockUiText.ResolveStockKey(fromTree, "tech tree title");
                if (!string.IsNullOrEmpty(fromTree)) return fromTree;
            }
            return techId;
        }

        /// <summary>The production tech-tree title lookup
        /// (<c>ResearchAndDevelopment.GetTechnologyTitle</c>).</summary>
        internal static readonly Func<string, string> StockTreeTitle = LiveTreeTitleCore;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string LiveTreeTitleCore(string techId)
        {
            return ResearchAndDevelopment.GetTechnologyTitle(techId);
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

            title = DisplayTitle(title, techId, StockTreeTitle);
            ParsekLog.Info("TechResearchPatch",
                $"Blocking tech research: '{techId}' ({title}) - committed future row " +
                $"ut={(entry != null ? entry.UT.ToString("F0", ic) : "?")} nowUT={nowUT.ToString("F0", ic)} " +
                $"recording={entry?.RecordingId ?? "(ksc)"}" +
                (!string.IsNullOrEmpty(sciCost) ? $", {sciCost}" : ""));

            var text = StockUiReservationPredicates.ExplainTech(
                index, techId, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(
                "Cannot research \"" + title + "\"",
                text.Body,
                sciCost);
            return true;
        }
    }
}
