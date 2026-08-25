using System.Globalization;
using System.Text;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure presentation helpers for the Supply-Route per-run funds cost
    /// (<see cref="RouteRunCostCalculator.RouteRunCost"/>). Every method is
    /// Unity-free and locale-stable
    /// (<see cref="CultureInfo.InvariantCulture"/>), so the cost line / block /
    /// tooltip render identically on comma-locale systems and are directly
    /// unit-testable without IMGUI. The <c>*UI</c> file only draws; all string
    /// shaping lives here.
    ///
    /// The per-cycle charge now matches the displayed net (logistics-recovery-credit):
    /// the gross launch cost is fronted at dispatch and the recovered amount is
    /// credited back one cycle later, so the headline net is what the run truly
    /// costs in steady state. The detail tooltip explains that timing.
    ///
    /// Funds are written as a plain comma-grouped integer plus the word "funds"
    /// (no currency glyph, no em dash, plain ASCII, gotcha G6).
    /// </summary>
    internal static class LogisticsCostPresentation
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // The no-recovery caption is pinned as a constant so the detail line and
        // the tests read the exact same wording (the plan's default phrasing,
        // question 3).
        internal const string NoRecoveryCaption = "transport not recovered in the recording";

        /// <summary>
        /// Format a funds amount as a comma-grouped integer plus the unit word,
        /// e.g. <c>"5,200 funds"</c>. Rounds to whole funds (the dispatch charge
        /// is integer-scaled in practice and a fractional credit is noise here).
        /// </summary>
        internal static string FormatFunds(double amount)
        {
            return amount.ToString("N0", IC) + " funds";
        }

        /// <summary>
        /// The detail-panel "Cost/run:" line. With recovery it reads
        /// <c>"Cost/run: 5,200 funds  (launch 12,500 - recovered 7,300)"</c>;
        /// with no recovery it reads
        /// <c>"Cost/run: 12,500 funds  (transport not recovered in the recording)"</c>.
        /// The recovery breakdown uses a plain hyphen (not an em dash, gotcha
        /// G6). Caller draws this only when
        /// <c>cost.Applicable &amp;&amp; cost.CostKnown</c>; this method does not
        /// re-check that gate (it formats whatever it is handed).
        /// </summary>
        internal static string FormatDetailLine(RouteRunCostCalculator.RouteRunCost cost)
        {
            var sb = new StringBuilder();
            sb.Append("Cost/run: ").Append(FormatFunds(cost.NetCost)).Append("  (");
            if (cost.RecoveryEventCount > 0 || cost.RecoveredCredits > 0.0)
            {
                sb.Append("launch ").Append(cost.LaunchCost.ToString("N0", IC))
                  .Append(" - recovered ").Append(cost.RecoveredCredits.ToString("N0", IC));
            }
            else
            {
                sb.Append(NoRecoveryCaption);
            }
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>
        /// The detail-line hover tooltip: explains net = launch - recovered, that
        /// recovered is the distance-scaled payout, and that it is credited one
        /// cycle later. On a no-recovery run it also appends the (inline)
        /// recover-to-reduce hint. Constant length by contract - no funds amounts.
        /// </summary>
        internal static string FormatDetailTooltip(RouteRunCostCalculator.RouteRunCost cost)
        {
            // CONSTANT LENGTH: the Logistics help strip is ONE wrapped line tall
            // (TooltipEchoBox.SingleLine), so this tooltip embeds no funds amounts -
            // the exact figures stay in the visible FormatDetailLine cell text and the
            // candidate suffix. TooltipEchoBudgetTests pins this under the 218-char
            // single-line budget at the window's 1556px first-open width.
            var sb = new StringBuilder();
            sb.Append("Net cost per run = launch - recovered credits ");
            sb.Append("(distance-scaled recovery payout), credited one cycle later.");
            if (cost.RecoveryEventCount <= 0 && cost.RecoveredCredits <= 0.0)
                sb.Append(" Recovering the transport lowers future runs.");
            return sb.ToString();
        }

        /// <summary>
        /// The route-creation-summary cost block (the replacement for the old
        /// "Dispatch cost: TBD" line):
        /// <code>
        /// Cost per run: 5,200 funds
        ///   Launch: 12,500 funds
        ///   Recovered: 7,300 funds
        /// </code>
        /// Each line ends with a newline so the block drops straight into the
        /// dialog body. Caller draws this only for Career + KSC origin with a
        /// known launch cost (<c>cost.Applicable &amp;&amp; cost.CostKnown</c>).
        /// </summary>
        internal static string FormatCreationSummaryBlock(RouteRunCostCalculator.RouteRunCost cost)
        {
            var sb = new StringBuilder();
            sb.Append("Cost per run: ").Append(FormatFunds(cost.NetCost)).Append('\n');
            sb.Append("  Launch: ").Append(FormatFunds(cost.LaunchCost)).Append('\n');
            sb.Append("  Recovered: ").Append(FormatFunds(cost.RecoveredCredits)).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// The compact candidate-row "Would deliver" cost suffix, e.g.
        /// <c>"  (cost/run 5,200 funds)"</c>. Caller appends this to the cell
        /// text only for Career + KSC origin with a known launch cost.
        /// </summary>
        internal static string FormatCandidateSuffix(RouteRunCostCalculator.RouteRunCost cost)
        {
            return "  (cost/run " + FormatFunds(cost.NetCost) + ")";
        }
    }
}
