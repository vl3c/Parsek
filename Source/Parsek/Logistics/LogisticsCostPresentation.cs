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
    /// Cost is display-only (decision D1): the figures shown never change what
    /// the orchestrator actually charges per dispatch. The detail tooltip
    /// surfaces that caveat so the player knows the headline net is what the run
    /// truly costs once the transport is recovered, while the per-cycle charge
    /// stays the gross launch cost.
    ///
    /// Funds are written as a plain comma-grouped integer plus the word "funds"
    /// (no currency glyph, no em dash, plain ASCII, gotcha G6).
    /// </summary>
    internal static class LogisticsCostPresentation
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // The no-recovery caption and the recover-to-reduce hint are pinned as
        // constants so the detail line, the candidate suffix, and the tests all
        // read the exact same wording (the plan's default phrasing, question 3).
        internal const string NoRecoveryCaption = "transport not recovered in the recording";
        internal const string RecoverToReduceHint =
            "Recovering the transport at the end of the recorded flight lowers the route cost.";

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
        /// The detail-line hover tooltip: explains net = launch - recovered,
        /// that recovered is the actual distance-scaled payout, and the D1
        /// caveat (the per-cycle charge is currently the gross launch cost). On a
        /// no-recovery run it also appends the recover-to-reduce hint.
        /// </summary>
        internal static string FormatDetailTooltip(RouteRunCostCalculator.RouteRunCost cost)
        {
            var sb = new StringBuilder();
            sb.Append("Net cost per run = launch cost - recovered credits. ");
            sb.Append("Recovered is the actual distance-scaled payout KSP paid back ");
            sb.Append("when the transport (and any jettisoned parts) were recovered in the recording. ");
            if (cost.RecoveryEventCount <= 0 && cost.RecoveredCredits <= 0.0)
                sb.Append(RecoverToReduceHint).Append(' ');
            sb.Append("Note: the per-cycle charge is currently the gross launch cost (")
              .Append(FormatFunds(cost.LaunchCost))
              .Append("); the net shown is what the run truly costs once the transport is recovered.");
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
